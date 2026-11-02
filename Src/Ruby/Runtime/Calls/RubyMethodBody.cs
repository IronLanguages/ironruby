/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

#if FEATURE_CORE_DLR
using MSA = System.Linq.Expressions;
#else
using MSA = Microsoft.Scripting.Ast;
#endif

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Compiler.Generation;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

namespace IronRuby.Runtime.Calls {
    /// <summary>
    /// Represents a Ruby method body AST. Multiple RubyMethodInfos can share the same instance.
    /// </summary>
    public sealed class RubyMethodBody {
        private readonly MethodDeclaration/*!*/ _ast;
        private readonly MSA.SymbolDocumentInfo _document;
        private readonly RubyEncoding/*!*/ _encoding;
        // the Coverage measurement the body was defined under: it is compiled only when first called
        private readonly LineCoverage _coverage;

        private Delegate _delegate;
        private RubyModule _delegateModule;
        private ConditionalWeakTable<RubyModule, Delegate> _moduleVariants;

        internal RubyMethodBody(MethodDeclaration/*!*/ ast, MSA.SymbolDocumentInfo document, RubyEncoding/*!*/ encoding, LineCoverage coverage) {
            Assert.NotNull(ast, encoding);

            _ast = ast;
            _document = document;
            _encoding = encoding;
            _coverage = coverage;
        }

        // Set for a body compiled ahead of time (Util/aot): builds the delegate for a declaring
        // scope and module, in place of transforming the AST - which then carries only the
        // method's header (name, parameters, location), not its body.
        private readonly Func<RubyScope, RubyModule, Delegate> _precompiled;

        internal RubyMethodBody(MethodDeclaration/*!*/ header, MSA.SymbolDocumentInfo document, RubyEncoding/*!*/ encoding,
            Func<RubyScope, RubyModule, Delegate>/*!*/ precompiled)
            : this(header, document, encoding, (LineCoverage)null) {
            _precompiled = precompiled;
        }

        /// <summary>
        /// Set by Module#ruby2_keywords. It lives on the body rather than on the method info so
        /// that it is shared with every alias of the method, which is what MRI does - marking a
        /// method marks the aliases made of it, before or after.
        /// </summary>
        private bool _ruby2Keywords;

        public bool Ruby2Keywords {
            get { return _ruby2Keywords; }
            set { _ruby2Keywords = value; }
        }

        public MethodDeclaration Ast { get { return _ast; } }

        /// <summary>
        /// The Coverage measurement this `def' was compiled under, null if it is not measured.
        /// </summary>
        internal LineCoverage Coverage { get { return _coverage; } }

        // Set once the call of this method with a block it ignores has been warned about (or would
        // have been, had warnings been on): MRI warns once per method definition.
        internal bool UnusedBlockReported { get; set; }
        public MSA.SymbolDocumentInfo Document { get { return _document; } }
        public bool HasTarget { get { return _ast.Target != null; } }
        public string/*!*/ Name { get { return _ast.Name; } }

        // The lexical modules the first compilation was made for, when they run through the
        // singleton class of a plain object; null otherwise. See GetDelegate.
        private RubyModule[] _singletonLexicalModules;
        private List<KeyValuePair<RubyModule[], Delegate>> _lexicalVariants;

        internal Delegate GetDelegate(RubyScope/*!*/ declaringScope, RubyModule/*!*/ declaringModule) {
            Delegate primary = _delegate;
            if (primary == null) {
                lock (this) {
                    primary = _delegate;
                    if (primary == null) {
                        _singletonLexicalModules = GetObjectSingletonLexicalModules(declaringScope);
                        _delegateModule = declaringModule;
                        _delegate = primary = Compile(declaringScope, declaringModule);
                        WatchLightCompilation(primary);
                    }
                }
            }

            // The declaring module is baked in as well, and `super' looks up from it. A `def' run
            // for more than one module - `Class.new { def initialize; super; end }' made twice,
            // class_eval in a loop - gets a compilation per module.
            if (declaringModule != _delegateModule && _singletonLexicalModules == null) {
                lock (this) {
                    if (_moduleVariants == null) {
                        _moduleVariants = new ConditionalWeakTable<RubyModule, Delegate>();
                    }
                    Delegate result;
                    if (!_moduleVariants.TryGetValue(declaringModule, out result)) {
                        result = Compile(declaringScope, declaringModule);
                        _moduleVariants.Add(declaringModule, result);
                    }
                    return result;
                }
            }

            // The declaring scope is baked into the compiled body, which is shared by every
            // execution of the `def'. For `class << obj' run once per obj (in a loop, a method)
            // each run has its own singleton class, and constants inside the method have to be
            // looked up there, so such a body is compiled once per lexical module chain.
            if (_singletonLexicalModules != null) {
                var modules = GetLexicalModules(declaringScope);
                if (!SameModules(modules, _singletonLexicalModules)) {
                    lock (this) {
                        if (_lexicalVariants == null) {
                            _lexicalVariants = new List<KeyValuePair<RubyModule[], Delegate>>();
                        }
                        foreach (var variant in _lexicalVariants) {
                            if (SameModules(modules, variant.Key)) {
                                return variant.Value;
                            }
                        }
                        var result = Compile(declaringScope, declaringModule);
                        _lexicalVariants.Add(new KeyValuePair<RubyModule[], Delegate>(modules, result));
                        return result;
                    }
                }
            }

            return primary;
        }

        #region Compiled-body forwarding

        // The primary delegate starts out as the light compiler's interpreted LightLambda, which goes
        // on standing in front of the IL it compiles after a few dozen calls: every call through it is
        // one more delegate hop plus a cast of the compiled delegate. Precompiled dispatchers made
        // while the body was interpreted are recorded here and pointed at the compiled delegate when
        // it arrives; later binds get it from GetDelegate. Rules already built keep the LightLambda,
        // which forwards correctly - just not as cheaply.
        private Delegate _interpretedDelegate;
        private List<MethodDispatcher> _interpretedDispatchers;
        private Delegate _compiledFrom, _compiledTo;

        private void WatchLightCompilation(Delegate/*!*/ primary) {
            var light = primary.Target as Microsoft.Scripting.Interpreter.LightLambda;
            if (light != null) {
                _interpretedDelegate = primary;
                light.Compile += (sender, e) => OnLightCompiled(primary, e.Compiled);
            }
        }

        private void OnLightCompiled(Delegate/*!*/ interpreted, Delegate/*!*/ compiled) {
            List<MethodDispatcher> dispatchers;
            lock (this) {
                if (_delegate != interpreted || compiled.GetType() != interpreted.GetType()) {
                    return;
                }
                _delegate = compiled;
                _interpretedDelegate = null;
                _compiledFrom = interpreted;
                _compiledTo = compiled;
                dispatchers = _interpretedDispatchers;
                _interpretedDispatchers = null;
            }
            if (dispatchers != null) {
                foreach (var dispatcher in dispatchers) {
                    dispatcher.SetMethod(compiled);
                }
            }
        }

        /// <summary>
        /// Called for each dispatcher made over <paramref name="method"/>, a delegate GetDelegate returned.
        /// </summary>
        internal void RegisterDispatcher(MethodDispatcher/*!*/ dispatcher, Delegate/*!*/ method) {
            Delegate compiled;
            lock (this) {
                if (method == _interpretedDelegate) {
                    if (_interpretedDispatchers == null) {
                        _interpretedDispatchers = new List<MethodDispatcher>();
                    }
                    _interpretedDispatchers.Add(dispatcher);
                    return;
                }
                if (method != _compiledFrom) {
                    return;
                }
                compiled = _compiledTo;
            }
            // compiled between GetDelegate and here:
            dispatcher.SetMethod(compiled);
        }

        #endregion

        private Delegate _moduleFunctionDelegate;

        // The refinements that were active where the body was last defined for a module, which is
        // what the method sees - not what its declaring scope has activated since.  The declaring
        // scope is baked into the compiled body and shared by every method defined in that scope, so
        // it cannot carry this; the box the compiled body hands each RubyMethodScope does.  Keyed like
        // the compilations are, by declaring module.
        private ConditionalWeakTable<RubyModule, StrongBox<RefinementActivation>> _definitionRefinements;

        // A compilation made while nothing had been refined carries no box: its scopes see no
        // refinements, which is right for a `def' that ran before any refinement existed.
        private bool _compiledWithoutDefinitionRefinements;

        // A `def' of this body ran while nothing had been refined (so it recorded nothing): a
        // compilation for its module made after something has been must still see no refinements.
        private bool _definedWithoutRefinements;

        /// <summary>
        /// A `def' of this body ran before anything was refined: no refinement is active where it was written.
        /// </summary>
        internal void SetDefinedWithoutRefinements() {
            _definedWithoutRefinements = true;
        }

        /// <summary>
        /// Records the refinements active at a `def' of this body for <paramref name="declaringModule"/>.
        /// </summary>
        internal void SetDefinitionRefinements(RubyModule/*!*/ declaringModule, RefinementActivation/*!*/ refinements) {
            lock (this) {
                if (_compiledWithoutDefinitionRefinements && !refinements.IsEmpty) {
                    // The same `def' ran again after a refinement came into existence: drop the
                    // compilations that cannot see it (callers re-bind: the def replaces the method).
                    _compiledWithoutDefinitionRefinements = false;
                    _delegate = null;
                    _delegateModule = null;
                    _moduleVariants = null;
                    _singletonLexicalModules = null;
                    _lexicalVariants = null;
                    _moduleFunctionDelegate = null;
                    _interpretedDelegate = null;
                    _interpretedDispatchers = null;
                    _compiledFrom = _compiledTo = null;
                }

                StrongBox<RefinementActivation> record;
                if (_definitionRefinements != null && _definitionRefinements.TryGetValue(declaringModule, out record)) {
                    if (!ReferenceEquals(record.Value, refinements)) {
                        // The `def' ran again where other refinements are active, and the compiled body is shared: the
                        // calls of it still running answer the new record from now on, the way new calls do. A scope
                        // that memoized the old answer recomputes it, as it does after a `using', so that it agrees
                        // with the rules that read the record (RubyCallAction.Resolve).
                        declaringModule.Context.RefinementVersion++;
                    }
                } else {
                    // nothing has been compiled against a record that did not exist
                    record = GetDefinitionRefinementsNoLock(declaringModule);
                }
                record.Value = refinements;
            }
        }

        private StrongBox<RefinementActivation>/*!*/ GetDefinitionRefinementsNoLock(RubyModule/*!*/ declaringModule) {
            if (_definitionRefinements == null) {
                _definitionRefinements = new ConditionalWeakTable<RubyModule, StrongBox<RefinementActivation>>();
            }
            return _definitionRefinements.GetValue(declaringModule, (_) => new StrongBox<RefinementActivation>());
        }

        /// <summary>
        /// The body compiled for the singleton copy Module#module_function makes, which differs
        /// from the instance method only in the label its frames carry.
        /// </summary>
        internal Delegate/*!*/ GetModuleFunctionDelegate(RubyScope/*!*/ declaringScope, RubyModule/*!*/ declaringModule) {
            if (_moduleFunctionDelegate == null) {
                lock (this) {
                    if (_moduleFunctionDelegate == null) {
                        _moduleFunctionDelegate = Compile(declaringScope, declaringModule);
                    }
                }
            }
            return _moduleFunctionDelegate;
        }

        private Delegate/*!*/ Compile(RubyScope/*!*/ declaringScope, RubyModule/*!*/ declaringModule) {
            if (_precompiled != null) {
                return _precompiled(declaringScope, declaringModule);
            }

            // TODO: remove options
            AstGenerator gen = new AstGenerator(declaringScope.RubyContext, new RubyCompilerOptions(), _document, _encoding, false);
            gen.Coverage = _coverage;
            if (_coverage != null && (_coverage.State.Modes & CoverageModes.Methods) != 0) {
                gen.MethodCoverage = _coverage.FindMethod(this, declaringModule);
            }

            // Only a method defined where something is `using'd pays for the definition-time refinement
            // record (a scope field written on every call): no call in any other body can see a
            // refinement, so it is compiled as if nothing were refined.  Should the same `def' run
            // again where a refinement is active, SetDefinitionRefinements drops this compilation.
            StrongBox<RefinementActivation> definitionRefinements = null;
            if (declaringScope.RubyContext.HasRefinements) {
                lock (this) {
                    definitionRefinements = GetDefinitionRefinementsNoLock(declaringModule);
                    // With nothing recorded the scopes follow the declaring scope - what an imported refinement
                    // method's copy needs (RubyModule.ImportMethod) - but a `def' that ran before anything was
                    // refined saw nothing, whatever that scope `using's afterwards.
                    if (definitionRefinements.Value == null && _definedWithoutRefinements && !declaringModule.IsRefinement) {
                        definitionRefinements.Value = RefinementActivation.Empty;
                    }
                    if (definitionRefinements.Value != null && definitionRefinements.Value.IsEmpty) {
                        definitionRefinements = null;
                        _compiledWithoutDefinitionRefinements = true;
                    }
                }
            } else {
                _compiledWithoutDefinitionRefinements = true;
            }

            MSA.LambdaExpression lambda = _ast.TransformBody(gen, declaringScope, declaringModule, definitionRefinements);
            var result = RubyScriptCode.CompileLambda(lambda, declaringScope.RubyContext);

            // -X:JIT: hand the generic body to the method JIT, which may put a profiling
            // trampoline of the same delegate type in front of it. Off by default, and this is
            // the only place the rest of the runtime knows about it.
            var context = declaringScope.RubyContext;
            if (context.RubyOptions.Jit) {
                result = IronRuby.Runtime.Jit.JitStub.Wrap(result, _ast, context, declaringModule, definitionRefinements);
            }
            return result;
        }

        private static RubyModule/*!*/[]/*!*/ GetLexicalModules(RubyScope/*!*/ scope) {
            var result = new List<RubyModule>();
            for (RubyScope s = scope; s != null; s = s.Parent) {
                if (s.Module != null) {
                    result.Add(s.Module);
                }
            }
            return result.ToArray();
        }

        private static RubyModule[] GetObjectSingletonLexicalModules(RubyScope/*!*/ scope) {
            var modules = GetLexicalModules(scope);
            foreach (var module in modules) {
                var cls = module as RubyClass;
                if (cls != null && cls.IsSingletonClass && !(cls.SingletonClassOf is RubyModule)) {
                    return modules;
                }
            }
            return null;
        }

        private static bool SameModules(RubyModule/*!*/[]/*!*/ a, RubyModule/*!*/[]/*!*/ b) {
            if (a.Length != b.Length) {
                return false;
            }
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) {
                    return false;
                }
            }
            return true;
        }
    }
}

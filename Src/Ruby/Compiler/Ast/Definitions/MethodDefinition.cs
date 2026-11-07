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

using MSA = System.Linq.Expressions;

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstExpressions = ReadOnlyCollectionBuilder<MSA.Expression>;
    using AstParameters = ReadOnlyCollectionBuilder<MSA.ParameterExpression>;
    
    public partial class MethodDefinition : DefinitionExpression {
        // self, block
        internal const int HiddenParameterCount = 2;
            
        /// <summary>
        /// Non-null for singleton/class methods.
        /// </summary>
        private readonly Expression _target;

        private readonly string/*!*/ _name;
        private readonly Parameters/*!*/ _parameters;

        public Expression Target {
            get { return _target; }
        }

        public string/*!*/ Name { 
            get { return _name; }
        }

        public Parameters/*!*/ Parameters {
            get { return _parameters; }
        }

        /// <summary>
        /// MRI's use_block: the method declares a block parameter, yields, or calls super passing
        /// its block on. A block given to a method without it may be ignored, which MRI warns about.
        /// </summary>
        // The prism bridge works this out; a method built any other way is assumed to use its
        // block, so it is never warned about or given the block-less fast path.
        public bool UsesBlock { get; set; } = true;

        public MethodDefinition(LexicalScope/*!*/ definedScope, Expression target, string/*!*/ name, Parameters parameters, Body/*!*/ body, 
            SourceSpan location)
            : base(definedScope, body, location) {
            Assert.NotNull(name);

            // only for-loop block might use other than local variable for unsplat:
            Debug.Assert(parameters.Unsplat == null || parameters.Unsplat is LocalVariable);
                
            _target = target;
            _name = name;
            _parameters = parameters ?? Parameters.Empty;
        }

        /// <summary>
        /// A method whose body the front end builds when it is first asked for - normally when the method
        /// is first called. Building it adds the body's locals to <paramref name="definedScope"/>.
        /// </summary>
        public MethodDefinition(LexicalScope/*!*/ definedScope, Expression target, string/*!*/ name, Parameters parameters,
            Func<Body>/*!*/ bodyFactory, SourceSpan location)
            : base(definedScope, bodyFactory, location) {
            Assert.NotNull(name);
            Debug.Assert(parameters == null || parameters.Unsplat == null || parameters.Unsplat is LocalVariable);

            _target = target;
            _name = name;
            _parameters = parameters ?? Parameters.Empty;
        }

        private ScopeBuilder/*!*/ DefineLocals(out AstParameters/*!*/ parameters) {
            // Method signature:
            // <self>, &<block>, <leading-mandatory>, <trailing-mandatory>, <optional>, *<unsplat>
            
            parameters = new AstParameters(
                HiddenParameterCount +
                _parameters.Mandatory.Length +
                _parameters.Optional.Length +
                (_parameters.Unsplat != null ? 1 : 0)
            );

            int closureIndex = 0;
            int firstClosureParam = 1;
            parameters.Add(Ast.Parameter(typeof(object), "#self"));

            if (_parameters.Block != null) {
                parameters.Add(Ast.Parameter(typeof(Proc), _parameters.Block.Name));
                _parameters.Block.SetClosureIndex(closureIndex++);
            } else {
                parameters.Add(Ast.Parameter(typeof(Proc), "#block"));
                firstClosureParam++;
            }

            foreach (LeftValue lvalue in _parameters.Mandatory) {
                var param = lvalue as LocalVariable;
                if (param != null) {
                    parameters.Add(Ast.Parameter(typeof(object), param.Name));
                    param.SetClosureIndex(closureIndex++);
                } else {
                    // TODO:
                    throw new NotSupportedException("TODO: compound parameters");
                }
            }

            foreach (var lvalue in _parameters.Optional) {
                var param = (LocalVariable)lvalue.Left;
                parameters.Add(Ast.Parameter(typeof(object), param.Name));
                param.SetClosureIndex(closureIndex++);
            }

            if (_parameters.Unsplat != null) {
                var unsplatLocal = (LocalVariable)_parameters.Unsplat;
                parameters.Add(Ast.Parameter(typeof(object), unsplatLocal.Name));
                unsplatLocal.SetClosureIndex(closureIndex++);
            }

            // allocate closure slots for locals:
            int localCount = DefinedScope.AllocateClosureSlotsForLocals(closureIndex);

            return new ScopeBuilder(parameters, firstClosureParam, localCount, null, DefinedScope);
        }

        /// <summary>
        /// The label MRI 3.4 and later puts on a method frame: "M::C#foo" for an instance method,
        /// "M::C.foo" for a method on a module's singleton, and the bare name where there is no
        /// module to name - a singleton method of an ordinary object (including main), or a method
        /// defined in an anonymous module.
        ///
        /// The owner is only known once the method is defined, which is also when the body is
        /// compiled, so the label can be baked into the frame name along with everything else.
        /// </summary>
        internal static string/*!*/ QualifyFrameLabel(string/*!*/ name, RubyModule declaringModule) {
            if (declaringModule == null) {
                return name;
            }

            RubyClass cls = declaringModule as RubyClass;
            if (cls != null && cls.IsSingletonClass) {
                RubyModule attached = cls.SingletonClassOf as RubyModule;
                return (attached != null && attached.Name != null) ? attached.Name + "." + name : name;
            }

            return (declaringModule.Name != null) ? declaringModule.Name + "#" + name : name;
        }

        internal MSA.LambdaExpression/*!*/ TransformBody(AstGenerator/*!*/ gen, RubyScope/*!*/ declaringScope, RubyModule/*!*/ declaringModule,
            StrongBox<RefinementActivation> definitionRefinements) {
            return TransformBody(gen, ExactConstant(declaringScope, typeof(RubyScope)), ExactConstant(declaringModule, typeof(RubyModule)),
                QualifyFrameLabel(_name, declaringModule), definitionRefinements, true);
        }

        /// <summary>
        /// A compiled lambda keeps its object constants in an object[] and casts each one back on
        /// every load. Typed as their base class the declaring scope (a top-level, module or method
        /// scope) and module (a class, mostly) miss the inline exact-type check and go to the
        /// runtime's class-cast helper, twice per call of the method; typed as what they are, the
        /// cast is one compare, and the up-cast to the parameter type is free.
        /// </summary>
        private static MSA.Expression/*!*/ ExactConstant(object/*!*/ value, Type/*!*/ type) {
            return AstUtils.Convert(Ast.Constant(value, value.GetType()), type);
        }

        /// <summary>
        /// The body with the declaring scope and module given as expressions rather than baked-in
        /// objects. Ahead-of-time compilation (Util/aot) uses it: there, both are only known when
        /// the `def' runs, so they are parameters of a factory that builds the body's delegate.
        /// </summary>
        internal MSA.LambdaExpression/*!*/ TransformBody(AstGenerator/*!*/ gen, MSA.Expression/*!*/ declaringScope, MSA.Expression/*!*/ declaringModule,
            string/*!*/ frameLabel, StrongBox<RefinementActivation> definitionRefinements) {
            return TransformBody(gen, declaringScope, declaringModule, frameLabel, definitionRefinements, false);
        }

        /// <summary>
        /// <paramref name="allowLightFrame"/>: whether the body may be a light frame (see TransformLightFrame),
        /// whose scope description is an object constant - so not where the body is compiled ahead of time.
        /// </summary>
        private MSA.LambdaExpression/*!*/ TransformBody(AstGenerator/*!*/ gen, MSA.Expression/*!*/ declaringScope, MSA.Expression/*!*/ declaringModule,
            string/*!*/ frameLabel, StrongBox<RefinementActivation> definitionRefinements, bool allowLightFrame) {
            string encodedName = RubyStackTraceBuilder.EncodeMethodName(frameLabel, gen.SourcePath, Location, gen.DebugMode);

            // a body built on demand adds its locals to the scope: build it before they are allocated
            Body methodBody = Body;

            AstParameters parameters;
            ScopeBuilder scope = DefineLocals(out parameters);

            var scopeVariable = scope.DefineHiddenVariable("#scope", typeof(RubyMethodScope));
            var selfParameter = parameters[0];
            var blockParameter = parameters[1];

            // exclude block parameter even if it is explicitly specified:
            int visiblePrameterCountAndSignatureFlags = (parameters.Count - 2) << 2;
            if (_parameters.Block != null) {
                visiblePrameterCountAndSignatureFlags |= RubyMethodScope.HasBlockFlag;
            }
            if (_parameters.Unsplat != null) {
                visiblePrameterCountAndSignatureFlags |= RubyMethodScope.HasUnsplatFlag;
            }

            gen.EnterMethodDefinition(
                scope,
                selfParameter,
                scopeVariable,
                blockParameter,
                _name,
                _parameters
            );

            // Blocks written inside this method are labelled "block in <this frame's label>",
            // so they need the qualified form rather than the bare method name.
            gen.CurrentMethod.FrameLabel = frameLabel;

            // profiling:
            MSA.Expression profileStart = AstUtils.Empty();
            MSA.Expression profileEnd = AstUtils.Empty();

            if (gen.Profiler != null) {
                int profileTickIndex = gen.Profiler.GetTickIndex(encodedName);
                var stampVariable = scope.DefineHiddenVariable("#stamp", typeof(long));
                profileStart = Ast.Assign(stampVariable, Methods.Stopwatch_GetTimestamp.OpCall());
                profileEnd = Methods.UpdateProfileTicks.OpCall(AstUtils.Constant(profileTickIndex), stampVariable);
            }

            MSA.ParameterExpression unwinder;
            
            // see RubyScope.GetActiveRefinements:
            MSA.Expression recordRefinements = (definitionRefinements != null)
                ? (MSA.Expression)Ast.Assign(
                    Ast.Property(scopeVariable, typeof(RubyMethodScope).GetProperty("DefinitionRefinements")),
                    Ast.Constant(definitionRefinements, typeof(StrongBox<RefinementActivation>)))
                : AstUtils.Empty();

            // :methods coverage: one call, counted where the frame is entered
            MSA.Expression countCall;
            var methodCoverage = gen.MethodCoverage;
            if (methodCoverage != null) {
                countCall = Ast.Call(AstUtils.Constant(methodCoverage), typeof(MethodCoverage).GetMethod("Count"));
            } else {
                countCall = AstUtils.Empty();
            }

            gen.BindingParameters = true;
            MSA.Expression optionalsInitialization = _parameters.TransformOptionalsInitialization(gen);
            gen.BindingParameters = false;
            gen.CountParameterAssignments(_parameters);
            MSA.Expression statements = methodBody.TransformResult(gen, ResultOperation.Return);

            // A body that never needs its scope makes none (see TransformLightFrame).
            LightFrame light = null;
            if (allowLightFrame && LightFramesEnabled && definitionRefinements == null && gen.Profiler == null && !gen.TraceEnabled &&
                !gen.CompilerOptions.IsEval && !scope.HasClosures && IsLightFrameCandidate()) {
                light = TransformLightFrame(gen, scope, scopeVariable, selfParameter, blockParameter, declaringScope, declaringModule,
                    visiblePrameterCountAndSignatureFlags, ref statements);
            }

            // The handler only takes the MethodUnwinder of a `return' from a block of this method. It
            // used to take every exception, for IsMethodUnwinderTargetFrame to capture the backtrace in,
            // but a filter here is a catch and a rethrow (TryStatementBuilder: DynamicMethods have no
            // filters), and on .NET 8 each is a full CLR exception dispatch of its own: a raise that left
            // n compiled methods was thrown n + 1 times. Any other exception now goes through with its
            // CLR trace growing as it passes, and the frame that finally handles it - a rescue, a block,
            // an interpreted frame, the top level - captures the same backtrace from there.
            MSA.Expression body = AstUtils.Try(
                countCall,
                recordRefinements,
                profileStart,
                optionalsInitialization,
                statements
            ).Filter(unwinder = Ast.Parameter(typeof(MethodUnwinder), "#u"),
                // a light frame makes a scope for the filter only when a MethodUnwinder passes:
                Methods.IsMethodUnwinderTargetFrame.OpCall((light != null) ? Ast.Coalesce(scopeVariable, light.MakeScope()) : (MSA.Expression)scopeVariable, unwinder),
                Ast.Return(gen.ReturnLabel, Methods.GetMethodUnwinderReturnValue.OpCall(unwinder))
            ).Finally(  
                // leave frame:
                (light != null) ? Methods.LeaveLightMethodFrame.OpCall(light.Rfc, scopeVariable) : Methods.LeaveMethodFrame.OpCall(scopeVariable),
                Ast.Empty(),
                profileEnd
            );

            // TracePoint :return: falling off the end reports the line of `end'; an explicit
            // `return' reports its own line and jumps past this hook (see ReturnStatement).
            if (gen.Traceable) {
                body = new TraceReturnExpression((light != null) ? light.LazyScope : scopeVariable, gen.AddReturnTarget(body), TraceEvents.Return, gen.SourcePath, Location.End.Line);
                if (gen.CurrentMethod.ExplicitReturnLabel != null) {
                    body = Ast.Label(gen.CurrentMethod.ExplicitReturnLabel, body);
                }
            } else {
                body = gen.AddReturnTarget(body);
            }

            // A body with keyword parameters is never a light frame (IsLightFrameCandidate).
            MSA.Expression variableNames = null;
            if (light == null) {
                variableNames = scope.GetVariableNamesExpression();
                if (variableNames is MSA.ConstantExpression names) {
                    SuperCall.RecordKeywordSignature((string[])names.Value, _parameters.Signature);
                }
            }

            body = scope.CreateScope(
                scopeVariable,
                (light != null) ? light.Enter(scope) : Methods.CreateMethodScope.OpCall(new AstExpressions {
                    scope.MakeLocalsStorage(),
                    variableNames,
                    Ast.Constant(visiblePrameterCountAndSignatureFlags),
                    declaringScope,
                    declaringModule,
                    Ast.Constant(_name),
                    selfParameter, blockParameter,
                    EnterInterpretedFrameExpression.Instance
                }),
                body
            );

            gen.LeaveMethodDefinition();

            return CreateLambda(encodedName, parameters, new CompiledFrameExpression(body, encodedName, Location.Start.Line));
        }

        #region Light frames

        /// <summary>
        /// IR_LIGHT_FRAMES=0 turns light frames off, to compare against or to rule them out; IR_LIGHT_FRAMES=verbose
        /// names on stderr each method compiled as one, and each candidate whose transformed body needed its scope.
        /// </summary>
        internal static readonly bool LightFramesEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_FRAMES") != "0";
        private static readonly bool LightFramesVerbose = Environment.GetEnvironmentVariable("IR_LIGHT_FRAMES") == "verbose";

        /// <summary>
        /// The pieces of a method body that runs without a RubyMethodScope (see TransformLightFrame).
        /// </summary>
        private sealed class LightFrame {
            internal MSA.ParameterExpression/*!*/ Rfc;
            internal MSA.ParameterExpression/*!*/ Callee;
            internal MSA.Expression/*!*/ LazyScope;
            internal Func<MSA.Expression>/*!*/ MakeScope;
            internal Func<MSA.Expression>/*!*/ MakeScopeIfTraced;
            internal MSA.Expression/*!*/ Name;

            /// <summary>
            /// Replaces CreateMethodScope: the locals, the safe point and the call's flow-control frame, the alias
            /// the method was called by, and a scope only if a TracePoint listens for calls.
            /// </summary>
            internal MSA.Expression/*!*/ Enter(ScopeBuilder/*!*/ scope) {
                return Ast.Block(
                    scope.MakeLocalsStorage(),
                    Ast.Assign(Rfc, Methods.EnterLightMethod.OpCall(_blockParameter)),
                    Ast.Assign(Callee, Methods.TakePendingCallee.OpCall(Name)),
                    MakeScopeIfTraced()
                );
            }

            internal MSA.Expression _blockParameter;
        }

        /// <summary>
        /// A method whose body can never need its scope at runtime - yield and return over self, its parameters,
        /// its locals, instance variables, constants and literals: `def tap1; yield self; self; end',
        /// `def initialize(a, b); @a = a; @b = b; end', `def limit = MAX'. Every call site takes the scope, and so do
        /// global variables and string interpolation, so this is the call-free body. Instance variables and constants
        /// take a scope as well, but only for its runtime and its lexical chain, which the declaring scope has too
        /// (LightFrameRewriter.ScopeOperandIndex).
        /// The transformed body is still checked (LightFrameRewriter); this is the cheap filter.
        /// </summary>
        private bool IsLightFrameCandidate() {
            if (_parameters.Optional.Length > 0 || _parameters.Unsplat != null || _parameters.SuperSplat != null ||
                _parameters.LeadingMandatoryCount != _parameters.Mandatory.Length) {
                return false;
            }
            var signature = _parameters.Signature;
            if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords)) {
                return false;
            }
            foreach (var parameter in _parameters.Mandatory) {
                if (!(parameter is LocalVariable)) {
                    return false;
                }
            }
            var body = Body;
            if (body == null || body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                return false;
            }
            if (body.Statements != null) {
                foreach (var statement in body.Statements) {
                    if (!IsScopeFree(statement)) {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool IsScopeFree(Expression/*!*/ node) {
            if (node is LocalVariable || node is Literal || node is SelfReference || node is InstanceVariable) {
                return true;
            }
            var constant = node as ConstantVariable;
            if (constant != null) {
                return IsCachedConstant(constant);
            }
            var assignment = node as SimpleAssignmentExpression;
            if (assignment != null) {
                return assignment.Operation == null && (assignment.Left is LocalVariable || assignment.Left is InstanceVariable) &&
                    IsScopeFree(assignment.Right);
            }
            var yieldCall = node as YieldCall;
            if (yieldCall != null) {
                return yieldCall.Block == null && AreScopeFree(yieldCall.Arguments);
            }
            // `(yield self; self)', the body of an endless def written with several statements
            var block = node as BlockExpression;
            if (block != null) {
                foreach (var statement in block.Statements) {
                    if (!IsScopeFree(statement)) {
                        return false;
                    }
                }
                return true;
            }
            var jump = node as ReturnStatement;
            if (jump != null) {
                return AreScopeFree(jump.Arguments);
            }
            return false;
        }

        // A, ::A, A::B::C - the reads ConstantVariable.MakeCachedRead makes - not {expr}::A
        private static bool IsCachedConstant(ConstantVariable/*!*/ constant) {
            var qualifier = constant.Qualifier;
            return qualifier == null || (qualifier is ConstantVariable && IsCachedConstant((ConstantVariable)qualifier));
        }

        private static bool AreScopeFree(Arguments arguments) {
            if (arguments != null) {
                foreach (var argument in arguments.Expressions) {
                    if (!IsScopeFree(argument)) {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Makes the body run without a RubyMethodScope, if the transformed statements use theirs only where a light
        /// frame can stand in for it: the post-yield test (LightMethodYield, against the call's flow-control frame) and
        /// the TracePoint hooks, which only evaluate their scope while a TracePoint listens and get it made on demand.
        /// The call's flow-control frame is what a library method with a block has (RubyOps.CreateRfcForMethod): the
        /// literal block itself, so a break from it still ends this call. Returns null, leaving the statements as they
        /// were, for a body that needs its scope after all.
        /// </summary>
        private LightFrame TransformLightFrame(AstGenerator/*!*/ gen, ScopeBuilder/*!*/ scope, MSA.ParameterExpression/*!*/ scopeVariable,
            MSA.Expression/*!*/ selfParameter, MSA.Expression/*!*/ blockParameter, MSA.Expression/*!*/ declaringScope,
            MSA.Expression/*!*/ declaringModule, int visibleParameterCountAndSignatureFlags, ref MSA.Expression/*!*/ statements) {

            var names = scope.GetVariableNamesExpression() as MSA.ConstantExpression;
            if (names == null) {
                return null;
            }

            var light = new LightFrame();
            light.Rfc = scope.DefineHiddenVariable("#rfc", typeof(RuntimeFlowControl));
            light.Callee = scope.DefineHiddenVariable("#callee", typeof(string));
            light.Name = AstUtils.Constant(_name);
            light._blockParameter = blockParameter;

            var shape = Ast.Constant(new MethodFrameShape((string[])names.Value, visibleParameterCountAndSignatureFlags, _name));
            Func<MethodInfo, MSA.Expression> make = method => method.OpCall(new AstExpressions {
                shape, declaringScope, declaringModule, scope.LocalsTupleVariable, selfParameter, blockParameter,
                EnterInterpretedFrameExpression.Instance, light.Rfc, light.Callee
            });
            light.MakeScope = () => make(Methods.CreateMethodScopeLazy);
            light.MakeScopeIfTraced = () => make(Methods.CreateMethodScopeIfTraced);
            light.LazyScope = Ast.Coalesce(scopeVariable, Ast.Assign(scopeVariable, light.MakeScope()));

            var rewriter = new LightFrameRewriter(scopeVariable, light.LazyScope, light.Rfc, declaringScope);
            var rewritten = rewriter.Visit(statements);
            if (LightFramesVerbose) {
                Console.Error.WriteLine("[light] {0} {1} ({2}:{3})", rewriter.NeedsScope ? "needs scope:" : "light:", _name, gen.SourcePath, Location.Start.Line);
            }
            if (rewriter.NeedsScope) {
                return null;
            }
            statements = rewritten;
            return light;
        }

        /// <summary>
        /// Points a scope-free method body at its light frame: MethodYield to LightMethodYield, the TracePoint hooks
        /// at the scope made on demand. Any other use of the scope, or a node it cannot look into, means the body needs
        /// its scope after all.
        /// </summary>
        private sealed class LightFrameRewriter : MSA.ExpressionVisitor {
            private readonly MSA.ParameterExpression/*!*/ _scope;
            private readonly MSA.Expression/*!*/ _lazyScope;
            private readonly MSA.Expression/*!*/ _rfc;
            private readonly MSA.Expression/*!*/ _declaringScope;
            private int _inTraceHook;

            internal bool NeedsScope;

            internal LightFrameRewriter(MSA.ParameterExpression/*!*/ scope, MSA.Expression/*!*/ lazyScope, MSA.Expression/*!*/ rfc,
                MSA.Expression/*!*/ declaringScope) {
                _scope = scope;
                _lazyScope = lazyScope;
                _rfc = rfc;
                _declaringScope = declaringScope;
            }

            /// <summary>
            /// The operand of a helper that takes the scope only for what a method scope shares with the scope the method
            /// was defined in, so the declaring scope can be passed instead: the runtime (instance variables), and the
            /// lexical chain a constant is looked up in - a method scope has no module of its own, so the lookup of one
            /// starts at its parent, which is the declaring scope. -1 for any other helper.
            /// </summary>
            private static int ScopeOperandIndex(MethodInfo/*!*/ method) {
                if (method == Methods.GetInstanceVariable || method == Methods.IsDefinedInstanceVariable ||
                    method == Methods.GetUnqualifiedConstant || method == Methods.GetQualifiedConstant ||
                    method == Methods.GetMissingConstant || method == Methods.GetGlobalMissingConstant ||
                    method == Methods.IsDefinedUnqualifiedConstant || method == Methods.IsDefinedGlobalConstant ||
                    method == Methods.IsDefinedQualifiedConstant) {
                    return 0;
                }
                if (method == Methods.SetInstanceVariable) {
                    return 2;
                }
                return -1;
            }

            protected override MSA.Expression/*!*/ VisitParameter(MSA.ParameterExpression/*!*/ node) {
                if (node == _scope) {
                    if (_inTraceHook > 0) {
                        return _lazyScope;
                    }
                    NeedsScope = true;
                }
                return node;
            }

            protected override MSA.Expression/*!*/ VisitMethodCall(MSA.MethodCallExpression/*!*/ node) {
                if (node.Method == Methods.MethodYield && node.Arguments.Count == 3 && node.Arguments[0] == _scope) {
                    // the raw scope variable, which is null unless a hook made the scope
                    return Methods.LightMethodYield.OpCall(_rfc, _scope, Visit(node.Arguments[1]), Visit(node.Arguments[2]));
                }
                int index = ScopeOperandIndex(node.Method);
                if (index >= 0 && index < node.Arguments.Count && node.Arguments[index] == _scope &&
                    node.Method.GetParameters()[index].ParameterType == typeof(RubyScope) && node.Object == null) {
                    var arguments = new MSA.Expression[node.Arguments.Count];
                    for (int i = 0; i < arguments.Length; i++) {
                        arguments[i] = (i == index) ? _declaringScope : Visit(node.Arguments[i]);
                    }
                    return Ast.Call(node.Method, arguments);
                }
                return base.VisitMethodCall(node);
            }

            protected override MSA.Expression/*!*/ VisitExtension(MSA.Expression/*!*/ node) {
                if (node is TraceLineExpression) {
                    _inTraceHook++;
                    try {
                        return base.VisitExtension(node);
                    } finally {
                        _inTraceHook--;
                    }
                }
                var traceReturn = node as TraceReturnExpression;
                if (traceReturn != null) {
                    // only the scope operand is the hook's; the value is the method's own code
                    _inTraceHook++;
                    MSA.Expression hookScope;
                    try {
                        hookScope = Visit(traceReturn.Scope);
                    } finally {
                        _inTraceHook--;
                    }
                    return traceReturn.Update(hookScope, Visit(traceReturn.Value));
                }
                if (node is EnterInterpretedFrameExpression || node is SourceLineMarker) {
                    return node;
                }
                if (node is CompiledFrameExpression) {
                    return base.VisitExtension(node);
                }
                NeedsScope = true;
                return node;
            }
        }

        #endregion

        private static MSA.LambdaExpression/*!*/ CreateLambda(string/*!*/ name, AstParameters/*!*/ parameters, MSA.Expression/*!*/ body) {
            var result = MethodDispatcher.CreateRubyMethodLambda(body, name, parameters);
            if (result != null) {
                return result;
            } 

            // to many parameters for Func<> delegate -> use object[]:
            MSA.ParameterExpression array = Ast.Parameter(typeof(object[]), "#params");
            var actualParameters = new AstParameters() { parameters[0], parameters[1], array };
            // Drop the two implicit parameters - self and the block - so that what is left is
            // exactly the declared parameters, in order, which is how they are read back out of
            // the object[].  Both live at the front, so both removals are at index 0: removing
            // index 1 second would take the *first declared* parameter instead of the block, and
            // leave the block's Proc-typed variable to be assigned an object out of the array.
            parameters.RemoveAt(0);
            parameters.RemoveAt(0);

            // ReadOnlyCollectionBuilder's int constructor takes a capacity, not a length, so the
            // collection starts empty - assigning through the indexer would be an index out of
            // range on a list of zero items.
            var bodyWithParamInit = new AstExpressions(parameters.Count + 1);
            for (int i = 0; i < parameters.Count; i++) {
                bodyWithParamInit.Add(Ast.Assign(parameters[i], Ast.ArrayIndex(array, AstUtils.Constant(i))));
            }
            bodyWithParamInit.Add(body);

            return Ast.Lambda<Func<object, Proc, object[], object>>(
                Ast.Block(
                    parameters, 
                    bodyWithParamInit
                ),
                name,
                actualParameters
            );
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            return Methods.DefineMethod.OpCall(
                (_target != null) ? AstUtils.Box(_target.TransformRead(gen)) : AstUtils.Constant(null),
                gen.CurrentScopeVariable,
                Ast.Constant(new RubyMethodBody(this, gen.Document, gen.Encoding, gen.Coverage))
            );
        }
    }
}

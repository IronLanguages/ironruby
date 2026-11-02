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
using System.Reflection;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using Microsoft.Scripting.Utils;
using AstFactory = IronRuby.Compiler.Ast.AstFactory;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;
using System.Diagnostics;

namespace IronRuby.Runtime.Calls {
    using Ast = MSA.Expression;
    using Microsoft.Scripting;

    public sealed class RubyMethodInfo : RubyMemberInfo {
        // Delegate type for methods with many parameters.
        internal static readonly Type ParamsArrayDelegateType = typeof(Func<object, Proc, object[], object>);

        private readonly RubyMethodBody/*!*/ _body;
        private readonly RubyScope/*!*/ _declaringScope;

        // The singleton copy Module#module_function makes: its frames are labelled "M.foo" where
        // the instance method's are "M#foo", so it runs a compilation of its own.
        private bool _isModuleFunctionCopy;

        public string/*!*/ DefinitionName { get { return _body.Name; } }
        public Parameters/*!*/ Parameters { get { return _body.Ast.Parameters; } }
        public MSA.SymbolDocumentInfo Document { get { return _body.Document; } }
        public SourceSpan SourceSpan { get { return _body.Ast.Location; } }
        public RubyScope/*!*/ DeclaringScope { get { return _declaringScope; } }
        internal RubyMethodBody/*!*/ Body { get { return _body; } }
        internal bool UsesBlock { get { return _body.Ast.UsesBlock; } }

        // method:
        internal RubyMethodInfo(RubyMethodBody/*!*/ body, RubyScope/*!*/ declaringScope, RubyModule/*!*/ declaringModule, RubyMemberFlags flags)
            : base(flags, declaringModule) {
            Assert.NotNull(body, declaringModule);

            _body = body;
            _declaringScope = declaringScope;
            declaringScope.MarkCaptured();
        }

        protected internal override RubyMemberInfo/*!*/ Copy(RubyMemberFlags flags, RubyModule/*!*/ module) {
            return new RubyMethodInfo(_body, _declaringScope, module, flags) { _isModuleFunctionCopy = _isModuleFunctionCopy };
        }

        internal RubyMethodInfo/*!*/ CopyAsModuleFunction(RubyMemberFlags flags, RubyModule/*!*/ singletonClass) {
            return new RubyMethodInfo(_body, _declaringScope, singletonClass, flags) { _isModuleFunctionCopy = true };
        }

        /// <summary>
        /// The same body written as if it had been defined in another lexical place, which is what
        /// Refinement#import_methods needs - see RubyModule.ImportMethod.
        /// </summary>
        internal RubyMethodInfo/*!*/ CopyWithScope(RubyScope/*!*/ scope, RubyModule/*!*/ module) {
            return new RubyMethodInfo(_body, scope, module, Flags);
        }

        public override bool IsEquivalentTo(RubyMemberInfo/*!*/ other) {
            if (ReferenceEquals(this, other)) {
                return true;
            }
            // `alias` and visibility changes copy the info but share the body.
            var info = other as RubyMethodInfo;
            return info != null && ReferenceEquals(_body, info._body);
        }
        
        public override int GetEquivalenceHashCode() {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_body);
        }

        public override RubyMemberInfo TrySelectOverload(Type/*!*/[]/*!*/ parameterTypes) {
            return parameterTypes.Length >= Parameters.Mandatory.Length
                && (Parameters.Unsplat != null || parameterTypes.Length <= Parameters.Mandatory.Length + Parameters.Optional.Length)
                && CollectionUtils.TrueForAll(parameterTypes, (type) => type == typeof(object)) ? this : null;
        }

        public override MemberInfo/*!*/[]/*!*/ GetMembers() {
            return new MemberInfo[] { GetDelegate().GetMethodInfo() };
        }

        public override RubyParameterSignature GetParameterSignature() {
            return _body.Ast.Parameters.Signature;
        }

        /// <summary>
        /// Module#ruby2_keywords. The flag goes on the body, which every alias of the method
        /// shares, so marking one marks them all - before or after the alias was made.
        /// </summary>
        public bool TrySetRuby2Keywords() {
            if (!KeepsTrailingHashInRest) {
                return false;
            }

            _body.Ruby2Keywords = true;
            return true;
        }

        /// <summary>
        /// Whether a trailing hash the call supplied as keyword arguments ends up staying in the
        /// rest parameter. It does not when the method declares keywords of its own: the prologue
        /// takes the hash back off the end, and it can only recognise it while it still says it is
        /// keyword arguments. This is also exactly the shape ruby2_keywords is allowed on.
        /// </summary>
        /// <summary>
        /// Whether the method's prologue is interested in a trailing hash that says it is the
        /// call's keyword arguments - because it declares keyword parameters, or because it
        /// declares `**nil` and has to refuse them.
        /// </summary>
        internal bool TakesOrRefusesKeywords {
            get {
                var signature = _body.Ast.Parameters.Signature;
                return signature != null && (signature.TakesKeywords || signature.RefusesKeywords);
            }
        }

        internal bool KeepsTrailingHashInRest {
            get {
                var signature = _body.Ast.Parameters.Signature;
                return (signature != null)
                    ? signature.AcceptsRuby2Keywords
                    : (_body.Ast.Parameters.Unsplat != null);
            }
        }

        public override int GetArity() {
            // the declared signature, when the front end recorded one, is the only thing that
            // knows about keywords: they are not in Parameters, having been lowered away
            var signature = _body.Ast.Parameters.Signature;
            if (signature != null) {
                return signature.GetArity(true);
            }

            if (Parameters.Unsplat != null || Parameters.Optional.Length > 0) {
                return -Parameters.Mandatory.Length - 1;
            } else {
                return Parameters.Mandatory.Length;
            }
        }

        public override RubyArray/*!*/ GetRubyParameterArray() {
            var context = _declaringScope.RubyContext;

            var signature = _body.Ast.Parameters.Signature;
            if (signature != null) {
                return signature.GetParameterArray(context, true);
            }

            var reqSymbol = context.CreateAsciiSymbol("req");
            var optSymbol = context.CreateAsciiSymbol("opt");
            var ps =_body.Ast.Parameters;

            RubyArray result = new RubyArray();
            for (int i = 0; i < ps.LeadingMandatoryCount; i++) {
                result.Add(new RubyArray { reqSymbol, context.EncodeIdentifier(((LocalVariable)ps.Mandatory[i]).Name) });
            }

            foreach (var p in ps.Optional) {
                result.Add(new RubyArray { optSymbol, context.EncodeIdentifier(((LocalVariable)p.Left).Name) });
            }

            if (ps.Unsplat != null) {
                result.Add(new RubyArray { context.CreateAsciiSymbol("rest"), context.EncodeIdentifier(((LocalVariable)ps.Unsplat).Name) });
            }

            for (int i = ps.LeadingMandatoryCount; i < ps.Mandatory.Length; i++) {
                result.Add(new RubyArray { reqSymbol, context.EncodeIdentifier(((LocalVariable)ps.Mandatory[i]).Name) });
            }

            if (ps.Block != null) {
                result.Add(new RubyArray { context.CreateAsciiSymbol("block"), context.EncodeIdentifier(ps.Block.Name) });
            }

            return result;
        }

        public MethodDeclaration/*!*/ GetSyntaxTree() {
            return _body.Ast;
        }

        internal Delegate/*!*/ GetDelegate() {
            return _isModuleFunctionCopy
                ? _body.GetModuleFunctionDelegate(_declaringScope, DeclaringModule)
                : _body.GetDelegate(_declaringScope, DeclaringModule);
        }

        #region Dynamic Sites

        internal override MemberDispatcher GetDispatcher(Type/*!*/ delegateType, RubyCallSignature signature, object target, int version) {
            return GetDispatcher(delegateType, signature, target, version, null, false);
        }

        /// <summary>
        /// The dispatcher of a call a refinement could change, which also tests that the method's refinement record holds
        /// <paramref name="refinements"/> (see MethodDispatcher.CreateRefinedRubyObjectDispatcher).
        /// </summary>
        internal MemberDispatcher GetRefinedDispatcher(Type/*!*/ delegateType, RubyCallSignature signature, object target, int version,
            RefinementActivation/*!*/ refinements, bool inBlock) {
            Assert.NotNull(refinements);
            return GetDispatcher(delegateType, signature, target, version, refinements, inBlock);
        }

        private MemberDispatcher GetDispatcher(Type/*!*/ delegateType, RubyCallSignature signature, object target, int version,
            RefinementActivation refinements, bool inBlock) {

            if (Parameters.Unsplat != null) {
                return null;
            }

            // The keywords of a method that declares nothing but mandatory positionals besides them
            // were lowered onto one trailing optional parameter (see PrismAstBridge.LowerKeywords and
            // RefuseKeywords). A call that fills it - a keyword call - passes the keyword hash to it
            // unchanged, which is all the rule would do (BuildCallNoFlow leaves the trailing hash of a
            // method that takes or refuses keywords alone). A call that omits it still gets a rule:
            // CreateDispatcher refuses a site whose argument count differs.
            int parameterCount = Parameters.Mandatory.Length;
            if (Parameters.Optional.Length > 0) {
                if (!TakesOrRefusesKeywords) {
                    return null;
                }
                parameterCount += Parameters.Optional.Length;
            }

            if (!(target is IRubyObject)) {
                return null;
            }

            var method = GetDelegate();
            var dispatcher = (refinements != null) ?
                MethodDispatcher.CreateRefinedRubyObjectDispatcher(delegateType, method, parameterCount, signature.HasBlock, version, refinements, inBlock) :
                MethodDispatcher.CreateRubyObjectDispatcher(delegateType, method, parameterCount, signature.HasScope, signature.HasBlock, version);
            if (dispatcher != null && !_isModuleFunctionCopy) {
                _body.RegisterDispatcher(dispatcher, method);
            }
            return dispatcher;
        }

        internal override void BuildCallNoFlow(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ name) {
            Assert.NotNull(metaBuilder, args, name);

            // 2 implicit args: self, block
            var argsBuilder = new ArgsBuilder(2, Parameters.Mandatory.Length, Parameters.LeadingMandatoryCount, Parameters.Optional.Length, Parameters.Unsplat != null);
            argsBuilder.SetImplicit(0, AstUtils.Box(args.TargetExpression));
            argsBuilder.SetImplicit(1, args.Signature.HasBlock ? AstUtils.Convert(args.GetBlockExpression(), typeof(Proc)) : AstFactory.NullOfProc);
            argsBuilder.AddCallArguments(metaBuilder, args);

            if (metaBuilder.Error) {
                return;
            }

            // box explicit arguments:
            var boxedArguments = argsBuilder.GetArguments();

            // The rest parameter holds on to whatever the call put in it, so a trailing hash that
            // was the keyword arguments of *this* call must stop saying so - and a ruby2_keywords
            // method wants it marked the other way instead. The flag is read here, at call time,
            // rather than baked into the rule: ruby2_keywords can be applied to a method that has
            // already been called.
            if (Parameters.Unsplat != null && KeepsTrailingHashInRest) {
                int restIndex = argsBuilder.UnsplatParameterIndex;
                boxedArguments[restIndex] = Methods.NormalizeRestArgument.OpCall(
                    AstUtils.Convert(boxedArguments[restIndex], typeof(RubyArray)),
                    AstUtils.Constant(_body)
                );
            }

            // The same for a method that declares no keyword parameters at all: there the hash a
            // call site built for `f(a: 1)` is simply a positional argument, and the method is
            // free to keep it. Unmark it, or the next call that does take keywords would read a
            // hash it was handed positionally as its keyword arguments and report an arity error
            // (see PrismAstBridge.KeywordSeparationChecks). `**nil` is not this case: its
            // prologue takes the hash like any keyword-accepting method, only to reject it.
            if (!TakesOrRefusesKeywords && argsBuilder.LastArgumentParameterIndex >= 0) {
                int lastIndex = argsBuilder.LastArgumentParameterIndex;
                boxedArguments[lastIndex] = Methods.ClearKeywordArguments.OpCall(
                    AstUtils.Box(boxedArguments[lastIndex])
                );
            }

            for (int i = 2; i < boxedArguments.Length; i++) {
                boxedArguments[i] = AstUtils.Box(boxedArguments[i]);
            }

            var method = GetDelegate();
            if (method.GetType() == ParamsArrayDelegateType) {
                // Func<object, Proc, object[], object>
                boxedArguments = new[] {
                    boxedArguments[0],
                    boxedArguments[1],
                    Ast.NewArrayInit(typeof(object), ArrayUtils.ShiftLeft(boxedArguments, 2))
                };
            }

            // Called through an alias: the body's scope has to learn the name for __callee__.
            if (name != DefinitionName) {
                int last = boxedArguments.Length - 1;
                var lastArgument = boxedArguments[last];
                boxedArguments[last] = Ast.Convert(
                    Methods.MarkAliasCall.OpCall(AstUtils.Box(lastArgument), AstUtils.Constant(name), AstUtils.Constant(DefinitionName)),
                    lastArgument.Type
                );
            }

            metaBuilder.Result = AstFactory.CallDelegate(method, boxedArguments);
        }

        /// <summary>
        /// Takes current result and wraps it into try-filter(MethodUnwinder)-finally block that ensures correct "break" behavior for 
        /// Ruby method calls with a block given in arguments.
        /// 
        /// Sets up a RFC frame similarly to MethodDeclaration.
        /// </summary>
        public static void RuleControlFlowBuilder(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            Debug.Assert(args.Signature.HasBlock);
            if (metaBuilder.Error) {
                return;
            }

            // TODO (improvement):
            // We don't special case null block here, although we could (we would need a test for that then).
            // We could also statically know (via call-site flag) that the current method is not a proc-converter (passed by ref),
            // which would make such calls faster.
            var rfcVariable = metaBuilder.GetTemporary(typeof(RuntimeFlowControl), "#rfc");
            var resultVariable = metaBuilder.GetTemporary(typeof(object), "#result");
            MSA.ParameterExpression unwinder;

            metaBuilder.Result = Ast.Block(
                // initialize frame (RFC):
                Ast.Assign(rfcVariable, Methods.CreateRfcForMethod.OpCall(AstUtils.Convert(args.GetBlockExpression(), typeof(Proc)))),
                AstUtils.Try(
                    Ast.Assign(resultVariable, metaBuilder.Result)
                ).Filter(unwinder = Ast.Parameter(typeof(MethodUnwinder), "#unwinder"),
                    Ast.Equal(Ast.Field(unwinder, MethodUnwinder.TargetFrameField), rfcVariable),

                    // return unwinder.ReturnValue;
                    Ast.Assign(resultVariable, Ast.Field(unwinder, MethodUnwinder.ReturnValueField))

                ).Finally(
                    // we need to mark the RFC dead snce the block might escape and break later:
                    Methods.LeaveMethodFrame.OpCall(rfcVariable)
                ), 
                resultVariable
            );
        }

        #endregion
    }
}

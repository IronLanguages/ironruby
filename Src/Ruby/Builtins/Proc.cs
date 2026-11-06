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

using System.Linq.Expressions;

using System;
using System.Diagnostics;
using System.Dynamic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {
    using Ast = Expression;
    using AstFactory = IronRuby.Compiler.Ast.AstFactory;
    using AstUtils = Microsoft.Scripting.Ast.Utils;

    using BlockCallTarget0 = Func<BlockParam, object, object>;
    using BlockCallTarget1 = Func<BlockParam, object, object, object>;
    using BlockCallTarget2 = Func<BlockParam, object, object, object, object>;
    using BlockCallTarget3 = Func<BlockParam, object, object, object, object, object>;
    using BlockCallTarget4 = Func<BlockParam, object, object, object, object, object, object>;
    using BlockCallTargetN = Func<BlockParam, object, object[], object>;
    using BlockCallTargetUnsplatN = Func<BlockParam, object, object[], RubyArray, object>;
    using BlockCallTargetUnsplatProcN = Func<BlockParam, object, object[], RubyArray, Proc, object>;

    public enum ProcKind {
        Block,
        Proc,
        Lambda
    }

    /// <remarks>
    /// A Proc is also a <see cref="RuntimeFlowControl"/>: a literal block passed to a call is a new Proc
    /// that only that call converts, so the Proc itself stands for the converting frame (see
    /// <see cref="RubyOps.CreateRfcForMethod"/> and <see cref="RubyOps.CreateBfcForLibraryMethod"/>)
    /// instead of a separate RuntimeFlowControl object allocated for every such call.
    /// </remarks>
    public partial class Proc : RuntimeFlowControl, IDuplicable {
        // Self object captured by the block definition, if any.
        // Although we could load self from scope in Ruby defined blocks, we cannot do so when we don't have a scope.
        private readonly object _self;

        // The scope that defines this block.
        private readonly RubyScope/*!*/ _scope;

        private readonly BlockDispatcher/*!*/ _dispatcher;

        // TODO: can we remove _kind and use _method and Converter fields intead?
        // The ProcKind in the low bits, and SelfConverterFlag.
        private int _kind;
        private const int KindMask = 3;

        // The proc is its own converter: a call it was the literal block of converted it (see Converter).
        private const int SelfConverterFlag = 4;

        /// <summary>
        /// The state few procs have, out of line: a Proc is allocated for every call with a literal block.
        /// </summary>
        private sealed class RareState {
            public RubyLambdaMethodInfo Method;
            public RefinementActivation RefinementOverride;
            public string SymbolName;
        }

        private RareState _rare;

        private RareState/*!*/ Rare {
            get { return _rare ?? (_rare = new RareState()); }
        }

        /// <summary>
        /// The frame that converted this block to a proc. No field of its own: a proc is only ever active as a
        /// frame (RuntimeFlowControl) while it is its own converter, so the base class's field holds any other
        /// converter, and SelfConverterFlag says the converter is this proc - active or not.
        /// </summary>
        internal RuntimeFlowControl Converter {
            get {
                var converter = _activeFlowControlScope;
                return (converter != null) ? converter : ((_kind & SelfConverterFlag) != 0) ? this : null;
            }
            set {
                if (value == this) {
                    _kind |= SelfConverterFlag;
                } else {
                    _kind &= ~SelfConverterFlag;
                    _activeFlowControlScope = value;
                }
            }
        }

        /// <summary>
        /// The call this literal block is passed to converts it: the proc becomes that call's active frame.
        /// </summary>
        internal void ConvertAsFrame() {
            _kind = (int)ProcKind.Proc | SelfConverterFlag;
            _activeFlowControlScope = this;
        }

        public ProcKind Kind {
            get { return (ProcKind)(_kind & KindMask); }
            // friend: RuntimeFlowControl
            internal set { _kind = (_kind & ~KindMask) | (int)value; }
        }

        internal RubyLambdaMethodInfo Method {
            get { var r = _rare; return (r != null) ? r.Method : null; }
        }

        public BlockDispatcher/*!*/ Dispatcher {
            get { return _dispatcher; }
        }

        public object Self {
            get { return _self; }
        }

        public RubyScope/*!*/ LocalScope {
            get { return _scope; }
        }

        /// <summary>
        /// Set while Module#refine runs this block: the refinements the block body sees, instead of the
        /// ones its defining scope sees.  A refine block implicitly activates the refinements of the module
        /// it is being defined in, and only for the duration of the block - which is a lexical extent that
        /// exists only at runtime, so it rides on the Proc rather than on a scope object.
        /// </summary>
        internal RefinementActivation RefinementOverride {
            get { var r = _rare; return (r != null) ? r.RefinementOverride : null; }
            set { if (value != null || _rare != null) { Rare.RefinementOverride = value; } }
        }

        /// <summary>
        /// The symbol this proc came from, for a proc Symbol#to_proc built; null otherwise.
        /// Proc#to_s prints it as "(&amp;:name)".
        /// </summary>
        public string SymbolName {
            get { var r = _rare; return (r != null) ? r.SymbolName : null; }
            internal set { if (value != null || _rare != null) { Rare.SymbolName = value; } }
        }

        public string SourcePath {
            get { return _dispatcher.SourcePath; }
        }

        public int SourceLine {
            get { return _dispatcher.SourceLine; }
        }

        #region Construction, Conversion

        internal Proc(ProcKind kind, object self, RubyScope/*!*/ scope, BlockDispatcher/*!*/ dispatcher) {
            Assert.NotNull(scope, dispatcher);
            Debug.Assert(dispatcher.Method != null);
            _kind = (int)kind;
            _self = self;
            _scope = scope;
            _dispatcher = dispatcher;
            // (no RubyScope.MarkCaptured here: a literal block makes a Proc over its enclosing scope on every
            // call, and a block with a nested block literal never reuses its scope - see BlockDefinition)
        }

        protected Proc(Proc/*!*/ proc)
            : this(proc.Kind, proc.Self, proc.LocalScope, proc.Dispatcher) {
            Converter = proc.Converter;
            SymbolName = proc.SymbolName;
        }

        /// <summary>
        /// Creates a copy of the proc that has the same target, context and self object as this block.
        /// </summary>
        public Proc/*!*/ Create(Proc/*!*/ proc) {
            return new Proc(proc);
        }

        /// <summary>
        /// Creates a lambda Proc that has the same target, context and self object as this block.
        /// Doesn't preserve the class of the Proc.
        /// </summary>
        public Proc/*!*/ ToLambda(RubyLambdaMethodInfo method) {
            Proc result = new Proc(this);
            result.Kind = ProcKind.Lambda;
            if (method != null) {
                result.Rare.Method = method;
            }
            return result;
        }

        /// <summary>
        /// Creates a copy of the proc that has the same target, context, self object as this instance.
        /// Doesn't copy instance data.
        /// Preserves the class of the Proc.
        /// </summary>
        public virtual Proc/*!*/ Copy() {
            return new Proc(this);
        }

        // Proc doesn't have "initialize_copy", it's entirely initialized in dup:
        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = Copy();
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        public static RubyLambdaMethodInfo/*!*/ ToLambdaMethodInfo(Proc/*!*/ block, string/*!*/ definitionName, RubyMethodVisibility visibility,
            RubyModule/*!*/ owner) {
            return new RubyLambdaMethodInfo(block, definitionName, (RubyMemberFlags)visibility, owner);
        }

        #endregion

        #region Dynamic Operations: Invoke

        internal void BuildInvoke(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            Assert.NotNull(metaBuilder, args);

            var convertedTarget = AstUtils.Convert(args.TargetExpression, typeof(Proc));

            // test for target type:
            metaBuilder.AddTypeRestriction(args.Target.GetType(), args.TargetExpression);

            BuildCall(
                metaBuilder,
                convertedTarget,                       // proc object  
                Methods.GetProcSelf.OpCall(convertedTarget),  // self captured by the block closure
                args
            );
        }

        /// <summary>
        /// From control flow perspective it "calls" the proc.
        /// </summary>
        internal static void BuildCall(
            MetaObjectBuilder/*!*/ metaBuilder,
            Expression/*!*/ procExpression,     // proc object
            Expression/*!*/ selfExpression,     // self passed to the proc
            CallArguments/*!*/ args             // user arguments passed to the proc
        ) {
            var bfcVariable = metaBuilder.GetTemporary(typeof(BlockParam), "#bfc");
            var resultVariable = metaBuilder.GetTemporary(typeof(object), "#result");
            ParameterExpression unwinder;

            metaBuilder.Result = Ast.Block(
                Ast.Assign(bfcVariable, Methods.CreateBfcForProcCall.OpCall(AstUtils.Convert(procExpression, typeof(Proc)))),
                AstUtils.Try(
                    Ast.Assign(resultVariable, Methods.MethodProcCall.OpCall(bfcVariable, 
                        AstFactory.YieldExpression(
                            args.RubyContext,
                            args.GetSimpleArgumentExpressions(),
                            args.GetSplattedArgumentExpression(),
                            args.GetRhsArgumentExpression(),
                            args.GetBlockExpression(),
                            bfcVariable,
                            selfExpression
                        )
                    ))
                ).Filter(unwinder = Ast.Parameter(typeof(Exception), "#u"), Methods.IsLambdaUnwinderTarget.OpCall(bfcVariable, unwinder),
                    // a return in a block nested in the lambda:
                    Ast.Assign(resultVariable, Methods.GetLambdaUnwinderReturnValue.OpCall(unwinder))
                ).Finally(
                    Methods.LeaveProcCall.OpCall(bfcVariable)
                ),
                resultVariable
            );
        }

        #endregion

        #region Call // TODO: generate

        // Call overloads don't check parameter count, this is done by Proc#call.

        public object Call(Proc procArg) {
            var blockParam = RubyOps.CreateBfcForProcCall(this);
            try {
                return RubyOps.MethodProcCall(blockParam, RubyOps.Yield0(procArg, _self, blockParam));
            } catch (LambdaUnwinder unwinder) when (unwinder.Target == blockParam) {
                return unwinder.ReturnValue;
            } finally {
                blockParam.IsActiveLambdaCall = false;
            }
        }

        public object Call(Proc procArg, object arg1) {
            var blockParam = RubyOps.CreateBfcForProcCall(this);
            try {
                // lambda calls are weird:
                var result = (Kind == ProcKind.Lambda) ?
                    RubyOps.YieldNoAutoSplat1(arg1, procArg, _self, blockParam) :
                    RubyOps.Yield1(arg1, procArg, _self, blockParam);

                return RubyOps.MethodProcCall(blockParam, result);
            } catch (LambdaUnwinder unwinder) when (unwinder.Target == blockParam) {
                return unwinder.ReturnValue;
            } finally {
                blockParam.IsActiveLambdaCall = false;
            }
        }

        public object Call(Proc procArg, object arg1, object arg2) {
            var blockParam = RubyOps.CreateBfcForProcCall(this);
            try {
                return RubyOps.MethodProcCall(blockParam, RubyOps.Yield2(arg1, arg2, procArg, _self, blockParam));
            } catch (LambdaUnwinder unwinder) when (unwinder.Target == blockParam) {
                return unwinder.ReturnValue;
            } finally {
                blockParam.IsActiveLambdaCall = false;
            }
        }

        public object Call(Proc procArg, object arg1, object arg2, object arg3) {
            var blockParam = RubyOps.CreateBfcForProcCall(this);
            try {
                return RubyOps.MethodProcCall(blockParam, RubyOps.Yield3(arg1, arg2, arg3, procArg, _self, blockParam));
            } catch (LambdaUnwinder unwinder) when (unwinder.Target == blockParam) {
                return unwinder.ReturnValue;
            } finally {
                blockParam.IsActiveLambdaCall = false;
            }
        }

        public object Call(Proc procArg, object arg1, object arg2, object arg3, object arg4) {
            var blockParam = RubyOps.CreateBfcForProcCall(this);
            try {
                return RubyOps.MethodProcCall(blockParam, RubyOps.Yield4(arg1, arg2, arg3, arg4, procArg, _self, blockParam));
            } catch (LambdaUnwinder unwinder) when (unwinder.Target == blockParam) {
                return unwinder.ReturnValue;
            } finally {
                blockParam.IsActiveLambdaCall = false;
            }
        }

        public object Call(Proc procArg, params object[]/*!*/ args) {
            switch (args.Length) {
                case 0: return Call(procArg);
                case 1: return Call(procArg, args[0]);
                case 2: return Call(procArg, args[0], args[1]);
                case 3: return Call(procArg, args[0], args[1], args[2]);
                case 4: return Call(procArg, args[0], args[1], args[2], args[3]);
            }

            var blockParam = RubyOps.CreateBfcForProcCall(this);
            try {
                return RubyOps.MethodProcCall(blockParam, RubyOps.YieldN(args, procArg, _self, blockParam));
            } catch (LambdaUnwinder unwinder) when (unwinder.Target == blockParam) {
                return unwinder.ReturnValue;
            } finally {
                blockParam.IsActiveLambdaCall = false;
            }
        }

        public object CallN(Proc procArg, object[]/*!*/ args) {
            Debug.Assert(args.Length > 4);
            var blockParam = RubyOps.CreateBfcForProcCall(this);
            try {
                return RubyOps.MethodProcCall(blockParam, RubyOps.YieldN(args, procArg, _self, blockParam));
            } catch (LambdaUnwinder unwinder) when (unwinder.Target == blockParam) {
                return unwinder.ReturnValue;
            } finally {
                blockParam.IsActiveLambdaCall = false;
            }
        }

        #endregion

        #region Block helper methods

        public static Proc/*!*/ Create(RubyContext/*!*/ context, BlockCallTarget1/*!*/ clrMethod) {
            return Create(context, 1, BlockDispatcher.MakeAttributes(BlockSignatureAttributes.None, -1), clrMethod);
        }

        public static Proc/*!*/ Create(RubyContext/*!*/ context, BlockCallTarget2/*!*/ clrMethod) {
            return Create(context, 2, BlockSignatureAttributes.None, clrMethod);
        }

        public static Proc/*!*/ Create(RubyContext/*!*/ context, BlockCallTarget3/*!*/ clrMethod) {
            return Create(context, 3, BlockSignatureAttributes.None, clrMethod);
        }

        public static Proc/*!*/ Create(RubyContext/*!*/ context, int parameterCount, BlockCallTargetUnsplatN/*!*/ clrMethod) {
            return Create(context, parameterCount, BlockSignatureAttributes.HasUnsplatParameter, clrMethod);
        }

        public static Proc/*!*/ Create(RubyContext/*!*/ context, int parameterCount, BlockSignatureAttributes signatureAttributes, Delegate/*!*/ clrMethod) {
            // scope is used to get to the execution context:
            return new Proc(ProcKind.Block, null, context.EmptyScope,
                BlockDispatcher.Create(parameterCount, signatureAttributes, null, 0).SetMethod(clrMethod)
            );
        }

        /// <summary>
        /// Creates a proc that invokes a method of given name.
        /// <code>
        /// Proc.new do |*args| 
        ///   raise ArgumentException if args.size == 0
        ///   obj.methodName(args.delete_at(0), args) 
        /// end
        /// </code>
        /// </summary>
        public static Proc/*!*/ CreateMethodInvoker(RubyScope/*!*/ scope, string/*!*/ methodName) {
            ContractUtils.RequiresNotNull(scope, "scope");

            // TODO: 
            // This should pass a proc parameter (use BlockDispatcherUnsplatProcN).
            // MRI 1.9.2 doesn't do so though (see http://redmine.ruby-lang.org/issues/show/3792).

            // the block a symbol proc is called with goes on to the method it invokes:
            // [1, 2].map(&:foo) { ... } passes the block through to #foo
            var site = CallSite<Func<CallSite, object, object, Proc, object, object>>.Create(
                RubyCallAction.Make(
                    scope.RubyContext, methodName,
                    new RubyCallSignature(0, RubyCallFlags.HasScope | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock)
                )
            );

            var block = new BlockCallTargetUnsplatProcN((blockParam, self, args, unsplat, procArg) => {
                Debug.Assert(args.Length == 0);
                if (unsplat.Count == 0) {
                    throw RubyExceptions.CreateArgumentError("no receiver given");
                }
                object target = unsplat[0];
                unsplat.RemoveAt(0);
                return site.Target(site, scope, target, procArg, unsplat);
            });

            var procDispatcher = new BlockDispatcherUnsplatProcN(0,
                BlockDispatcher.MakeAttributes(
                    BlockSignatureAttributes.HasUnsplatParameter | BlockSignatureAttributes.HasProcParameter, -1),
                null, 0
            );

            procDispatcher.SetMethod(block);
            procDispatcher.ParameterSignature = SymbolProcSignature;

            // MRI's symbol proc is a lambda: it reports #lambda? true, arity -2 and
            // [[:req], [:rest]] for #parameters
            scope.MarkCaptured();
            return new Proc(ProcKind.Lambda, scope.SelfObject, scope, procDispatcher) { SymbolName = methodName };
        }

        /// <summary>
        /// A proc with no Ruby source behind it. MRI's Proc#curry, Proc#>> and Proc#<< all answer
        /// with one of these: it takes *args, so it reports [[:rest]] and arity -1, it has no
        /// #source_location, and there is no scope for it to hand out as a #binding.
        /// </summary>
        public static Proc/*!*/ CreateNative(RubyContext/*!*/ context, ProcKind kind,
            Func<BlockParam, object, object[], RubyArray, Proc, object>/*!*/ body) {

            // the proc parameter matters: without one, a block passed to the composed or curried
            // proc cannot be handed on to the proc underneath it
            var result = Create(context, 0, BlockDispatcher.MakeAttributes(
                BlockSignatureAttributes.HasUnsplatParameter | BlockSignatureAttributes.HasProcParameter, -1), body);
            result.Kind = kind;
            result.Dispatcher.ParameterSignature = NativeProcSignature;
            return result;
        }

        private static readonly RubyParameterSignature/*!*/ NativeProcSignature = new RubyParameterSignature(
            new[] { new RubyParameterSignature.Parameter("rest", null) },
            0, 0, 0, true, 0, false, false
        );

        private static readonly RubyParameterSignature/*!*/ SymbolProcSignature = new RubyParameterSignature(
            new[] {
                new RubyParameterSignature.Parameter("req", null),
                new RubyParameterSignature.Parameter("rest", null)
            },
            1, 0, 0, true, 0, false, false
        );

        #endregion

        public override string/*!*/ ToString() {
            return String.Format("{0}: {1}:{2}", Kind, SourcePath ?? "(unknown)", SourceLine);
        }
    }
}

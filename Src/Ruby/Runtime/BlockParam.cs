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
using System.Diagnostics;
using System.Reflection;
using Microsoft.Scripting;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;
using AstFactory = IronRuby.Compiler.Ast.AstFactory;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using IronRuby.Compiler.Generation;
using System.Dynamic;

namespace IronRuby.Runtime {
    using Ast = MSA.Expression;
    using System.Collections;
    
    public enum BlockReturnReason {
        Undefined = 0,
        Retry = 1,
        Return = 2,
        Break = 3,
    }

    public sealed class BlockReturnResult {
        internal static BlockReturnResult Retry = new BlockReturnResult();

        internal readonly object ReturnValue;
        internal readonly RuntimeFlowControl TargetFrame; // non-null for return, null for retry

        private BlockReturnResult() {
        }

        internal BlockReturnResult(RuntimeFlowControl/*!*/ targetFrame, object returnValue) {
            Assert.NotNull(targetFrame);
            TargetFrame = targetFrame;
            ReturnValue = returnValue;
        }

        public MethodUnwinder/*!*/ ToUnwinder() {
            return new MethodUnwinder(TargetFrame, ReturnValue);
        }
    }

    public enum BlockCallerKind {
        Yield,
        Call
    }

    internal sealed class MissingBlockParam {
        /// <remarks>
        /// LimitType must be MissingBlockParam (overload resolution, <see cref="RubyParameterBinder.PrepareParametersBinding"/>).
        /// Restriction should be empty: used only for !HasBlock call-sites => the site will never be reused for a call with a block.
        /// </remarks>
        internal sealed class Meta : DynamicMetaObject, IRestrictedMetaObject {
            internal static readonly DynamicMetaObject Instance = new Meta();

            private Meta()
                : base(AstUtils.Constant(null, typeof(MissingBlockParam)), BindingRestrictions.Empty) {
                Debug.Assert(LimitType == typeof(MissingBlockParam));
            }

            public DynamicMetaObject Restrict(Type/*!*/ type) {
                Debug.Assert(type == typeof(BlockParam) || type == typeof(MissingBlockParam));
                return this;
            }
        }

    }

    public sealed partial class BlockParam {
        // -- in --
        private readonly Proc/*!*/ _proc;
        // The small state is packed into bytes, which keeps a BlockParam at 48 bytes: one is allocated for every
        // call of a library method with a block, and for every yield and Proc#call.
        // bit 0: BlockCallerKind.Call; bit 1: the library method call taking this BlockParam is a proc converter;
        // bit 2: instance_eval/instance_exec - definitions go to self's singleton class, made when one needs it.
        private byte _callerFlags;
        private const int CallFlag = 1;
        private const int LibProcConverterFlag = 2;
        private const int LazySingletonFlag = 4;

        // filled by module_eval: if not null than method definition uses the module
        private RubyModule _methodLookupModule;
        
        // the thread the call taking the block runs on; a break cannot reach that call from another.
        // 0 for a yield or a Proc#call, which run the block on the thread that made the BlockParam.
        private readonly int _threadId;

        // the block is the body of a Thread (see RubyBlockScope.IsClosureScope)
        public bool IsThreadRoot { get; set; }
        
        // -- out --
        // bits 0-1: BlockReturnReason; bits 2-3: the ProcKind of the block that set it;
        // bit 4: a call of the block through this BlockParam has finished (see ParkScope)
        private byte _flow;
        private const int CalledFlag = 16;

        // The break target while ReturnReason is Break. While it is Undefined: null, or the scope of the last
        // call of the block that nothing captured, for the next call through this BlockParam to reuse (see
        // ReusableScope) - no field of its own, a BlockParam is allocated for every call with a block.
        private RuntimeFlowControl _targetFrame;

        internal BlockCallerKind CallerKind { get { return (_callerFlags & CallFlag) != 0 ? BlockCallerKind.Call : BlockCallerKind.Yield; } }

        /// <summary>
        /// The scope ParkScope kept, or null.
        /// </summary>
        internal RubyBlockScope ReusableScope {
            get { return ((_flow & 3) == (int)BlockReturnReason.Undefined) ? (RubyBlockScope)_targetFrame : null; }
        }

        // False if there is no ReusableScope; a quick test for the common case.
        internal bool MayHaveReusableScope {
            get { return _targetFrame != null; }
        }

        // The scope is taken for a call of the block (it was the ReusableScope).
        internal void TakeReusableScope() {
            _targetFrame = null;
        }

        /// <summary>
        /// Keeps the scope of a call of the block that finished normally, for the next call to reuse; not on the
        /// first call through this BlockParam, which most often is the only one (a library method calling its
        /// block once), nor after a break/return/retry, which use the target frame, nor if something captured it.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool ParkScope(RubyBlockScope/*!*/ scope) {
            int flow = _flow;
            if (flow == CalledFlag) {
                if (scope.IsCaptured) {
                    return false;
                }
                _targetFrame = scope;
                return true;
            }
            if (flow == 0) {
                _flow = CalledFlag;
            }
            return false;
        }

        // True while a lambda called through Proc#call (or as a define_method body) is running, so
        // a return in a block nested in it can unwind to that call.
        internal bool IsActiveLambdaCall;
        internal ProcKind SourceProcKind { get { return (ProcKind)((_flow >> 2) & 3); } }
        internal BlockReturnReason ReturnReason { get { return (BlockReturnReason)(_flow & 3); } set { _flow = (byte)((_flow & ~3) | (int)value); } }
        internal RuntimeFlowControl TargetFrame { get { return _targetFrame; } }
        internal RubyModule MethodLookupModule { get { return _methodLookupModule; } set { _methodLookupModule = value; } }

        /// <summary>
        /// The block runs as a module body would (module_eval, instance_eval, instance_exec): a `def', `alias' or
        /// `undef' in it goes to <see cref="GetMethodLookupModule"/>.
        /// </summary>
        internal bool IsModuleEval {
            get { return _methodLookupModule != null || (_callerFlags & LazySingletonFlag) != 0; }
        }

        /// <summary>
        /// instance_eval/instance_exec of an object that has no singleton class yet: as in MRI (a singleton cref), the
        /// class is made only when the block defines something into it. Making it up front gave every receiver - each
        /// relation an ActiveRecord scope is evaluated on, each controller a callback runs on - a class of its own,
        /// which every call site the object then reached had to bind afresh, and which #clone copied.
        /// </summary>
        internal void SetLazySingletonMethodLookup() {
            _methodLookupModule = null;
            _callerFlags |= LazySingletonFlag;
        }

        /// <summary>
        /// The module a definition in the block goes to; <paramref name="self"/> is the block's self, whose singleton
        /// class it is when the block was instance_eval'd.
        /// </summary>
        internal RubyModule GetMethodLookupModule(object self) {
            var module = _methodLookupModule;
            if (module == null && (_callerFlags & LazySingletonFlag) != 0) {
                _methodLookupModule = module = RubyContext.GetOrCreateSingletonClass(self);
            }
            return module;
        }
        // Is the library method call taking this BlockParam a proc converter?
        // Used only for BlockParams that are passed to library method calls.
        internal bool IsLibProcConverter { get { return (_callerFlags & LibProcConverterFlag) != 0; } }

        /// <summary>
        /// True if the library method got a literal block rather than a Proc object passed with &amp;:
        /// the block was not a Proc until this call made it one - or until a library method that is
        /// still running and handed it on did (send), which is how MRI passes a block handler through.
        /// A block a Ruby method received has that method's scope as its converter, so a block that
        /// travels through a Ruby method (&amp;b, super) is a Proc object here, as it is in MRI.
        /// </summary>
        public bool IsLiteralBlock {
            get {
                if (IsLibProcConverter) {
                    return true;
                }
                // a call that converted a literal block stands for its frame by that Proc (see CreateRfcForMethod):
                var converter = _proc.Converter;
                return converter is Proc && converter.IsActiveMethod;
            }
        }
        
        public Proc/*!*/ Proc { get { return _proc; } }

        [Emitted]
        public object Self { get { return _proc.Self; } }

        public RubyContext RubyContext {
            get { return _proc.LocalScope.RubyContext; }
        }

        public bool IsMethod {
            get { return _proc.Method != null; }
        }

        internal static PropertyInfo/*!*/ SelfProperty { get { return typeof(BlockParam).GetDeclaredProperty("Self"); } }

        // friend: RubyOps
        internal BlockParam(Proc/*!*/ proc, BlockCallerKind callerKind, bool isLibProcConverter) {
            _threadId = Environment.CurrentManagedThreadId;
            _callerFlags = (byte)((callerKind == BlockCallerKind.Call ? CallFlag : 0) | (isLibProcConverter ? LibProcConverterFlag : 0));
            _proc = proc;
        }

        // A BlockParam that is only ever used on the thread creating it, for the duration of a call:
        // a yield or a Proc#call. There the thread check in Break always passes, so skip it.
        internal BlockParam(Proc/*!*/ proc, BlockCallerKind callerKind) {
            _callerFlags = (byte)(callerKind == BlockCallerKind.Call ? CallFlag : 0);
            _proc = proc;
        }

        // ReuseBfcForYield: the flow-control state is cleared, but the scope the last call kept (see ParkScope) stays.
        internal void ResetForNextYield() {
            if ((_flow & ~CalledFlag) != 0) {
                _targetFrame = null;
            }
            _flow = CalledFlag;
        }

        internal void SetFlowControl(BlockReturnReason reason, RuntimeFlowControl targetFrame, ProcKind sourceProcKind) {
            Debug.Assert((reason == BlockReturnReason.Break) == (targetFrame != null));

            _flow = (byte)((int)reason | ((int)sourceProcKind << 2));
            _targetFrame = targetFrame;
        }

        internal object GetUnwinderResult(EvalUnwinder/*!*/ unwinder) {
            Debug.Assert(unwinder != null);
            SetFlowControl(unwinder.Reason, unwinder.TargetFrame, unwinder.SourceProcKind);
            return unwinder.ReturnValue;
        }

        #region Library Block Yield Helpers

        /// <summary>
        /// Must be called on the result of RubyOps.Yield. Implements post-yield control flow operation.
        /// </summary>
        /// <remarks>
        /// Used by library methods that take a block. The binder creates an instance of BlockParam holding on RFC if necessary.
        /// A library method that creates a block yet doesn't take one needs to manage RFC on its own.
        /// </remarks>
        internal bool BlockJumped(object returnValue) {
            // the block finished normally (no break/return/retry), which is what nearly every yield does:
            if ((_flow & 3) == (int)BlockReturnReason.Undefined) {
                return false;
            }

            // if this method is a proc converter then the current frame is Proc.Converter, otherwise it is not available:
            return RubyOps.MethodYieldRfc(IsLibProcConverter ? _proc.Converter : null, this, returnValue);
        }

        public bool Returning(object returnValue, out object result) {
            if (ReturnReason == BlockReturnReason.Return) {
                result = ((BlockReturnResult)returnValue).ReturnValue;
                return true;
            }
            result = null;
            return false;
        }

        /// <summary>
        /// Propagates control flow (break/return) from the yielded block to the enclosing block.
        /// </summary>
        public object PropagateFlow(BlockParam/*!*/ yieldedBlock, object returnValue) {
            if (yieldedBlock.ReturnReason == BlockReturnReason.Break) {
                return Break(returnValue);
            } else {
                ReturnReason = yieldedBlock.ReturnReason;
                return returnValue;
            }
        }

        /// <summary>
        /// Breaks from the current block.
        /// </summary>
        public object Break(object returnValue) {
            // break in a lambda returns from the lambda, like return does (see RubyOps.BlockReturn),
            // also when the lambda was passed as a block and yielded to:
            if (_proc.Kind == ProcKind.Lambda) {
                return returnValue;
            }

            // proc whose home frame is gone (e.g. Proc#call on an orphaned block), or one running
            // on another thread than the call it would break out of (Thread.new { break }):
            if (_proc.Converter == null || (_threadId != 0 && _threadId != Environment.CurrentManagedThreadId)) {
                throw new LocalJumpError("break from proc-closure", "break", returnValue);
            }

            // unwind to proc converter:
            SetFlowControl(BlockReturnReason.Break, _proc.Converter, _proc.Kind);
            return returnValue;
        }

        public bool Yield(out object blockResult) {
            return BlockJumped(blockResult = RubyOps.Yield0(null, Self, this));
        }

        public bool Yield(object arg1, out object blockResult) {
            return BlockJumped(blockResult = RubyOps.Yield1(arg1, null, Self, this));
        }

        public bool Yield(object arg1, object arg2, out object blockResult) {
            return BlockJumped(blockResult = RubyOps.Yield2(arg1, arg2, null, Self, this));
        }

        public bool Yield(object arg1, object arg2, object arg3, out object blockResult) {
            return BlockJumped(blockResult = RubyOps.Yield3(arg1, arg2, arg3, null, Self, this));
        }

        public bool Yield(object arg1, object arg2, object arg3, object arg4, out object blockResult) {
            return BlockJumped(blockResult = RubyOps.Yield4(arg1, arg2, arg3, arg4, null, Self, this));
        }

        public bool Yield(object[]/*!*/ args, out object blockResult) {
            return BlockJumped(blockResult = RubyOps.Yield(args, null, Self, this));
        }

        public bool YieldSplat(IList/*!*/ args, out object blockResult) {
            return BlockJumped(blockResult = RubyOps.YieldSplat0(args, null, Self, this));
        }

        #endregion

        #region Dynamic Operations

        /// <summary>
        /// "yields" to the proc.
        /// </summary>
        internal void BuildInvoke(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            Assert.NotNull(metaBuilder, args);
            Debug.Assert(!args.Signature.HasBlock);

            var convertedTarget = AstUtils.Convert(args.TargetExpression, typeof(BlockParam));

            // test for target type:
            metaBuilder.AddTypeRestriction(args.Target.GetType(), args.TargetExpression);

            metaBuilder.Result = AstFactory.YieldExpression(
                args.RubyContext,
                args.GetSimpleArgumentExpressions(),
                args.GetSplattedArgumentExpression(),
                args.GetRhsArgumentExpression(),
                args.GetBlockExpression(),                    
                convertedTarget,                              // block param
                Ast.Property(convertedTarget, SelfProperty)   // self
            );
        }

        #endregion

    }

    #region RubyOps

    public static partial class RubyOps {

        [Emitted]
        public static bool IsProcConverterTarget(BlockParam/*!*/ bfc, MethodUnwinder/*!*/ unwinder) {
            Debug.Assert(unwinder != null);
            return bfc.IsLibProcConverter && unwinder.TargetFrame == bfc.Proc.Converter;
        }
        
        [Emitted]
        public static BlockParam/*!*/ CreateBfcForYield(Proc proc) {
            if (proc != null) {
                return new BlockParam(proc, BlockCallerKind.Yield);
            } else {
                throw RubyExceptions.NoBlockGiven();
            }
        }

        /// <summary>
        /// A yield site that runs again in the same frame - a loop around `yield` - yields to the same
        /// proc every time, and its BlockParam is only looked at right after each yield returns
        /// (MethodYield/BlockYield), so the site hands the previous one back with its flow-control
        /// state cleared instead of allocating another.
        /// </summary>
        [Emitted]
        public static BlockParam/*!*/ ReuseBfcForYield(BlockParam previous, Proc proc) {
            if (previous != null && previous.Proc == proc) {
                previous.ResetForNextYield();
                return previous;
            }
            return CreateBfcForYield(proc);
        }

        [Emitted]
        public static BlockParam/*!*/ CreateBfcForProcCall(Proc/*!*/ proc) {
            Assert.NotNull(proc);
            return new BlockParam(proc, BlockCallerKind.Call) { IsActiveLambdaCall = proc.Kind == ProcKind.Lambda };
        }
        
        [Emitted]
        public static BlockParam/*!*/ CreateBfcForLibraryMethod(Proc/*!*/ proc) {
            Assert.NotNull(proc);
            bool isProcConverter;

            if (proc.Kind == ProcKind.Block) {
                // the literal block is new for this call: it is the call's flow-control frame (see CreateRfcForMethod)
                proc.ConvertAsFrame();
                isProcConverter = true;
            } else {
                isProcConverter = false;
            }

            return new BlockParam(proc, BlockCallerKind.Yield, isProcConverter);
        }

        [Emitted] 
        public static void LeaveProcConverter(BlockParam/*!*/ bfc) {
            Debug.Assert(bfc.Proc != null);
            if (bfc.IsLibProcConverter) {
                Debug.Assert(bfc.Proc.Converter != null);
                bfc.Proc.Converter.LeaveMethod();
            }
        }
    }

    #endregion
}

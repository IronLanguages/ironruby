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

using System;
using System.Reflection;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Compiler.Generation;

namespace IronRuby.Runtime {

    public abstract class StackUnwinder : Exception {
        [Emitted]
        public readonly object ReturnValue;

        internal static FieldInfo ReturnValueField { get { return typeof(StackUnwinder).GetDeclaredField("ReturnValue"); } }

        public StackUnwinder(object returnValue) {
            // we don't need exact number in multi-threaded scenarios:
            InstanceCount++;
            ReturnValue = returnValue;
        }

        // used for control-flow optimization testing:
        internal static int InstanceCount = 0;
    }
    
    /// <summary>
    /// Return (thrown or passed as a return value) and retry (retry singleton).
    /// </summary>
    public sealed class MethodUnwinder : StackUnwinder {
        [Emitted]
        public readonly RuntimeFlowControl/*!*/ TargetFrame;

        internal static FieldInfo TargetFrameField { get { return typeof(MethodUnwinder).GetDeclaredField("TargetFrame"); } }

        internal MethodUnwinder(RuntimeFlowControl/*!*/ targetFrame, object returnValue)
            : base(returnValue) {
            Assert.NotNull(targetFrame);
            TargetFrame = targetFrame;
        }
    }

    /// <summary>
    /// Retry/Break.
    /// </summary>
    public sealed class EvalUnwinder : StackUnwinder {
        private readonly RuntimeFlowControl _targetFrame;
        private readonly ProcKind _sourceProcKind;

        [Emitted]
        public readonly BlockReturnReason Reason;
        
        internal static FieldInfo ReasonField { get { return typeof(EvalUnwinder).GetDeclaredField("Reason"); } }

        internal RuntimeFlowControl TargetFrame { get { return _targetFrame; } }
        internal ProcKind SourceProcKind { get { return _sourceProcKind; } }

        internal EvalUnwinder(BlockReturnReason reason, object returnValue) 
            : this(reason, null, ProcKind.Block, returnValue) {
        }

        internal EvalUnwinder(BlockReturnReason reason, RuntimeFlowControl targetFrame, ProcKind sourceProcKind, object returnValue)
            : base(returnValue) {

            Reason = reason;
            _targetFrame = targetFrame;
            _sourceProcKind = sourceProcKind;
        }
    }

    /// <summary>
    /// Redo/Next.
    /// </summary>
    /// <summary>
    /// A return in a block that is lexically inside a lambda leaves the lambda (not the method
    /// around it). Thrown to the call of the lambda, identified by the call's BlockParam.
    /// </summary>
    public sealed class LambdaUnwinder : StackUnwinder {
        // the BlockParam of a Proc#call, or the RubyBlockScope of the lambda body's invocation
        internal readonly object/*!*/ Target;

        internal LambdaUnwinder(object/*!*/ target, object returnValue)
            : base(returnValue) {
            Target = target;
        }
    }

    public sealed class BlockUnwinder : StackUnwinder {
        [Emitted]
        public readonly bool IsRedo;

        internal static FieldInfo IsRedoField { get { return typeof(BlockUnwinder).GetDeclaredField("IsRedo"); } }

        internal BlockUnwinder(object returnValue, bool isRedo)
            : base(returnValue) {
            IsRedo = isRedo;
        }
    }
}

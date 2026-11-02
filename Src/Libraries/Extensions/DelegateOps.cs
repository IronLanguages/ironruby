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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    using InvokeStorage = CallSiteStorage<Func<CallSite, object, Proc, RubyArray, object>>;

    /// <summary>
    /// A CLR delegate is a lambda as far as Ruby is concerned: it can be called the ways a Proc can, passed as
    /// a block (&amp;delegate goes through #to_proc), composed and curried. Everything ends up calling the
    /// delegate's Invoke method through a dynamic site, so the arguments get the same conversions a direct
    /// call to #invoke gives them.
    /// </summary>
    [RubyClass(Extends = typeof(Delegate), Restrictions = ModuleRestrictions.None)]
    [Includes(typeof(ICloneable), typeof(ISerializable))] // a library class does not get the CLR interfaces mixed in by itself
    public static class DelegateOps {
        private static readonly RubyCallSignature InvokeSignature =
            new RubyCallSignature(0, RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock);

        // Invoke's shape depends only on the delegate type
        private static readonly ConcurrentDictionary<Type, RubyParameterSignature>/*!*/ _signatures =
            new ConcurrentDictionary<Type, RubyParameterSignature>();

        private static object Invoke(InvokeStorage/*!*/ storage, Delegate/*!*/ self, Proc block, RubyArray/*!*/ args) {
            var site = storage.GetCallSite("Invoke", InvokeSignature);
            return site.Target(site, self, block, args);
        }

        #region call, (), [], ===, yield

        [RubyMethod("call"), RubyMethod("[]"), RubyMethod("==="), RubyMethod("yield")]
        [RubyStackTraceHidden]
        public static object Call(InvokeStorage/*!*/ invokeStorage, BlockParam block, Delegate/*!*/ self, params object[]/*!*/ args) {
            return Invoke(invokeStorage, self, block != null ? block.Proc : null, RubyOps.MakeArrayN(args));
        }

        #endregion

        #region arity, parameters, lambda?

        [RubyMethod("arity")]
        public static int GetArity(Delegate/*!*/ self) {
            return GetSignature(self).GetArity(true);
        }

        [RubyMethod("parameters")]
        public static RubyArray/*!*/ GetParameters(RubyContext/*!*/ context, Delegate/*!*/ self) {
            return GetSignature(self).GetParameterArray(context, true);
        }

        [RubyMethod("lambda?")]
        public static bool IsLambda(Delegate/*!*/ self) {
            return true;
        }

        private static RubyParameterSignature/*!*/ GetSignature(Delegate/*!*/ self) {
            return _signatures.GetOrAdd(self.GetType(), MakeSignature);
        }

        /// <summary>
        /// Invoke's parameters the way Method#parameters would describe them. An out parameter is left out:
        /// the caller does not pass it, its value comes back with the result.
        /// </summary>
        private static RubyParameterSignature/*!*/ MakeSignature(Type/*!*/ delegateType) {
            var parameters = new List<RubyParameterSignature.Parameter>();
            int required = 0, optional = 0;
            bool hasRest = false;

            MethodInfo invoke = delegateType.GetMethod("Invoke");
            if (invoke != null) {
                foreach (var parameter in invoke.GetParameters()) {
                    string name = String.IsNullOrEmpty(parameter.Name) ? null : parameter.Name;
                    if (parameter.ParameterType.IsByRef && parameter.IsOut && !parameter.IsIn) {
                        continue;
                    } else if (parameter.IsDefined(typeof(ParamArrayAttribute), false)) {
                        parameters.Add(new RubyParameterSignature.Parameter("rest", name));
                        hasRest = true;
                    } else if (parameter.IsOptional || parameter.HasDefaultValue) {
                        parameters.Add(new RubyParameterSignature.Parameter("opt", name));
                        optional++;
                    } else {
                        parameters.Add(new RubyParameterSignature.Parameter("req", name));
                        required++;
                    }
                }
            }

            return new RubyParameterSignature(parameters.ToArray(), required, optional, 0, hasRest, 0, false, false);
        }

        #endregion

        #region to_proc, curry, >>, <<

        /// <summary>
        /// A lambda that invokes the delegate, so that &amp;delegate can be passed where a block is expected. Its
        /// arity and parameters are Invoke's, which is also what a lambda checks the argument count against.
        /// </summary>
        [RubyMethod("to_proc")]
        public static Proc/*!*/ ToProc(InvokeStorage/*!*/ invokeStorage, Delegate/*!*/ self) {
            var result = Proc.CreateNative(invokeStorage.Context, ProcKind.Lambda, (blockParam, procSelf, args, unsplat, procArg) =>
                Invoke(invokeStorage, self, procArg, unsplat)
            );
            result.Dispatcher.ParameterSignature = GetSignature(self);
            return result;
        }

        // Method#curry, #>> and #<< are their Proc counterparts applied to the method's #to_proc, and so are these.

        [RubyMethod("curry")]
        public static Proc/*!*/ Curry(InvokeStorage/*!*/ invokeStorage, Delegate/*!*/ self) {
            return ProcOps.Curry(invokeStorage.Context, ToProc(invokeStorage, self));
        }

        [RubyMethod("curry")]
        public static Proc/*!*/ Curry(InvokeStorage/*!*/ invokeStorage, Delegate/*!*/ self, [DefaultProtocol]int arity) {
            return ProcOps.Curry(invokeStorage.Context, ToProc(invokeStorage, self), arity);
        }

        [RubyMethod(">>")]
        public static Proc/*!*/ ComposeForward(InvokeStorage/*!*/ invokeStorage, RespondToStorage/*!*/ respondTo,
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ callStorage, Delegate/*!*/ self, object other) {
            return ProcOps.ComposeForward(respondTo, callStorage, ToProc(invokeStorage, self), other);
        }

        [RubyMethod("<<")]
        public static Proc/*!*/ ComposeBackward(InvokeStorage/*!*/ invokeStorage, RespondToStorage/*!*/ respondTo,
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ callStorage,
            InvokeStorage/*!*/ splatStorage, Delegate/*!*/ self, object other) {
            return ProcOps.ComposeBackward(respondTo, callStorage, splatStorage, ToProc(invokeStorage, self), other);
        }

        #endregion
    }
}

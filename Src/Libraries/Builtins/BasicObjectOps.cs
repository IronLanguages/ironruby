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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    [RubyClass("BasicObject", Extends = typeof(BasicObject), Restrictions = ModuleRestrictions.NoNameMapping | ModuleRestrictions.NotPublished | ModuleRestrictions.NoUnderlyingType)]
    public static class BasicObjectOps {
        // RubyConstructor implemented by BasicObject ctors

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.Empty)]
        public static object Reinitialize(object self) {
            // takes no arguments: Object.new(1), and a `super` passing arguments up to here, are
            // "wrong number of arguments (given 1, expected 0)" in MRI; answers nil, as MRI's does
            return null;
        }

        #region singleton_method_added, singleton_method_removed, singleton_method_undefined, method_missing

        [RubyMethod("singleton_method_added", RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.Empty)]
        public static void MethodAdded(object self, object methodName) {
            // nop
        }

        [RubyMethod("singleton_method_removed", RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.Empty)]
        public static void MethodRemoved(object self, object methodName) {
            // nop
        }

        [RubyMethod("singleton_method_undefined", RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.Empty)]
        public static void MethodUndefined(object self, object methodName) {
            // nop
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("method_missing", RubyMethodAttributes.PrivateInstance)]
        [RubyStackTraceHidden]
        public static object MethodMissing(RubyContext/*!*/ context, object/*!*/ self, [NotNull]RubySymbol/*!*/ name, params object[]/*!*/ args) {
            // the arguments of the call that found no method are what NoMethodError#args reports,
            // and the way the call was made (implicit receiver or not) is what #private_call? reports
            var error = NoMethodErrorOps.SetArguments(RubyExceptions.CreateMethodMissing(context, self, name.ToString()), args);
            RubyExceptionData.GetInstance(error).PrivateCall = RubyOps.MethodMissingPrivateCall;
            RubyOps.MethodMissingPrivateCall = false;
            throw error;
        }

        #endregion

        #region __send__

        [RubyMethod("__send__")]
        public static object SendMessage(RubyScope/*!*/ scope, object self) {
            return KernelOps.SendMessage(scope, self);
        }

        // ARGS: 0
        [RubyMethod("__send__")]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName) {
            return KernelOps.SendMessage(storage, scope, self, methodName);
        }

        // ARGS: 0&
        [RubyMethod("__send__")]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName) {
            return KernelOps.SendMessage(storage, scope, block, self, methodName);
        }

        // ARGS: 1
        [RubyMethod("__send__")]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1) {
            return KernelOps.SendMessage(storage, scope, self, methodName, arg1);
        }

        // ARGS: 1&
        [RubyMethod("__send__")]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1) {
            return KernelOps.SendMessage(storage, scope, block, self, methodName, arg1);
        }

        // ARGS: 2
        [RubyMethod("__send__")]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2) {
            return KernelOps.SendMessage(storage, scope, self, methodName, arg1, arg2);
        }

        // ARGS: 2&
        [RubyMethod("__send__")]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2) {
            return KernelOps.SendMessage(storage, scope, block, self, methodName, arg1, arg2);
        }

        // ARGS: 3
        [RubyMethod("__send__")]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2, object arg3) {
            return KernelOps.SendMessage(storage, scope, self, methodName, arg1, arg2, arg3);
        }

        // ARGS: 3&
        [RubyMethod("__send__")]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2, object arg3) {
            return KernelOps.SendMessage(storage, scope, block, self, methodName, arg1, arg2, arg3);
        }

        // ARGS: N
        [RubyMethod("__send__")]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            params object[]/*!*/ args) {
            return KernelOps.SendMessage(storage, scope, self, methodName, args);
        }

        // ARGS: N&
        [RubyMethod("__send__")]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, Proc, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            params object[]/*!*/ args) {
            return KernelOps.SendMessage(storage, scope, block, self, methodName, args);
        }

        #endregion

        #region ==, !=, !, equal?

        // equal? is an alias of == in BasicObject, so both names sit on both overloads (an
        // UnboundMethod for one is == to one for the other).
        [RubyMethod("==")]
        [RubyMethod("equal?")]
        public static bool ValueEquals([NotNull]IRubyObject/*!*/ self, object other) {
            return self.BaseEquals(other);
        }

        [RubyMethod("==")]
        [RubyMethod("equal?")]
        public static bool ValueEquals(object self, object other) {
            // a String, Regexp or Time compares its contents in Equals; for them this is identity
            if (self is MutableString || self is RubyRegex || self is RubyTime) {
                return self == other;
            }
            return Object.Equals(self, other);
        }

        [RubyMethod("!")]
        public static bool Not(object self) {
            return RubyOps.IsFalse(self);
        }

        [RubyMethod("!=")]
        public static bool ValueNotEquals(BinaryOpStorage/*!*/ eql, object self, object other) {
            var site = eql.GetCallSite("==", 1);
            return RubyOps.IsFalse(site.Target(site, self, other));
        }
        
        /// <summary>
        /// #__id__ belongs to BasicObject, not to Kernel: it is one of the handful of methods a
        /// BasicObject has, and code that hides #object_id still expects it.
        /// </summary>
        [RubyMethod("__id__")]
        public static object GetObjectId(RubyContext/*!*/ context, object self) {
            return ClrInteger.Narrow(RubyUtils.GetObjectId(context, self));
        }

        #endregion

        #region instance_eval, instance_exec

        /// <summary>
        /// instance_eval takes either a block and nothing else, or a string and up to two more
        /// arguments saying where it came from - and it has to count them itself, because the two
        /// shapes report different arities. The string, the file name and the line are each
        /// converted the way MRI converts them, so an object answering #to_str is a perfectly
        /// good file name.
        /// </summary>
        [RubyMethod("instance_eval")]
        public static object Evaluate(ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyScope/*!*/ scope, BlockParam block, object self, [NotNull]params object[]/*!*/ args) {

            if (block != null) {
                if (args.Length != 0) {
                    throw RubyExceptions.CreateArgumentError(
                        "wrong number of arguments (given {0}, expected 0)", args.Length);
                }
                // The receiver is passed to the block: `obj.instance_eval { |o| o }` is obj.
                return RubyUtils.EvaluateInSingleton(self, block, new object[] { self });
            }

            if (args.Length < 1 || args.Length > 3) {
                throw RubyExceptions.CreateArgumentError(
                    "wrong number of arguments (given {0}, expected 1..3)", args.Length);
            }

            MutableString code = Protocols.CastToString(toStr, args[0]);
            MutableString file = (args.Length > 1 && args[1] != null) ? Protocols.CastToString(toStr, args[1]) : null;
            int line = (args.Length > 2) ? Protocols.CastToFixnum(toInt, args[2]) : 1;

            // MRI looks constants up in the receiver's class if it has no singleton class yet
            RubyClass immediate = scope.RubyContext.GetImmediateClassOf(self);
            RubyClass singleton = scope.RubyContext.GetOrCreateSingletonClass(self);
            return RubyUtils.Evaluate(code, scope, self, singleton, immediate.IsSingletonClass ? null : immediate, file, line);
        }

        // Not [NotNull] params: a lone nil argument binds as the params array itself, and
        // [NotNull] made instance_exec(nil) { } a TypeError - which is every ActiveRecord
        // association scope, `relation.instance_exec(owner, &scope)` with no owner.
        [RubyMethod("instance_exec")]
        public static object InstanceExec(BlockParam block, object self, params object[] args) {
            args = args ?? new object[] { null };
            if (block == null) {
                throw RubyExceptions.CreateLocalJumpError("no block given");
            }
            return RubyUtils.EvaluateInSingleton(self, block, args);
        }

        #endregion
    }
}

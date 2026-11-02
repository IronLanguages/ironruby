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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    using EachSite = Func<CallSite, object, Proc, object>;

    internal interface IEnumerator {
        object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block);
    }

    /// <summary>
    /// A wrapper that provides "each" method for an arbitrary object. 
    /// A call to "each" on the instance of Enumerator is forwarded to a call to a method of a given name on the wrapped object.
    /// </summary>
    [RubyClass("Enumerator"), Includes(typeof(Enumerable))]
    public class Enumerator {
        internal sealed class Wrapper : IEnumerator {
            private readonly object _targetObject;
            private readonly string/*!*/ _targetName;
            private readonly object[]/*!*/ _targetArguments;

            public Wrapper(object targetObject, string targetName, object[] targetArguments) {
                _targetObject = targetObject;
                _targetName = targetName ?? "each";
                _targetArguments = targetArguments ?? ArrayUtils.EmptyObjects;
            }

            public object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block) {
                // The arguments were recorded when the enumerator was made. A hash among them
                // that a ruby2_keywords method was carrying has to go back to being keyword
                // arguments now, exactly as it would at a splatted call site - `enum_for(:find,
                // *paths, ignore_error: true)' has to reach #find as keywords.
                return KernelOps.SendMessageOpt(scope, block, _targetObject, _targetName,
                    RubyOps.RestoreRuby2Keywords(_targetArguments)
                );
            }

            internal object TargetObject { get { return _targetObject; } }
            internal string/*!*/ TargetName { get { return _targetName; } }
            internal object[]/*!*/ TargetArguments { get { return _targetArguments; } }
        }

        internal sealed class DelegateWrapper : IEnumerator {
            private readonly Func<RubyScope, BlockParam, object>/*!*/ _each;

            public DelegateWrapper(Func<RubyScope, BlockParam, object>/*!*/ each) {
                _each = each;
            }

            public object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block) {
                return _each(scope, block);
            }
        }

        private IEnumerator/*!*/ _impl;

        /// <summary>
        /// What `Enumerator#size` is computed from. MRI attaches a size function to every
        /// enumerator a builtin returns; there is no equivalent slot here, so instead the receiver
        /// and a tag naming the arithmetic are recorded and the prelude does the sum. Null
        /// <see cref="SizeOp"/> means "unknown", which is the nil MRI returns for a generator.
        /// </summary>
        internal object SizeSource;
        internal string SizeOp;
        internal object SizeArg;

        public Enumerator()
            : this(null, null, null) {
        }

        internal Enumerator(IEnumerator/*!*/ impl) {
            Assert.NotNull(impl);
            _impl = impl;
        }

        internal Enumerator(Func<RubyScope, BlockParam, object>/*!*/ impl) {
            Assert.NotNull(impl);
            _impl = new DelegateWrapper(impl);
        }

        /// <summary>Same as the delegate constructor, but records what #size is computed from.</summary>
        internal static Enumerator/*!*/ Sized(Func<RubyScope, BlockParam, object>/*!*/ impl, object sizeSource, string/*!*/ sizeOp) {
            return Sized(impl, sizeSource, sizeOp, null);
        }

        internal static Enumerator/*!*/ Sized(Func<RubyScope, BlockParam, object>/*!*/ impl, object sizeSource, string/*!*/ sizeOp, object sizeArg) {
            return new Enumerator(impl) { SizeSource = sizeSource, SizeOp = sizeOp, SizeArg = sizeArg };
        }

        public Enumerator(object targetObject, string targetName, params object[] targetArguments) {
            Reinitialize(this, targetObject, targetName, targetArguments);
        }

        [RubyConstructor]
        public static Enumerator/*!*/ Create(RubyClass/*!*/ self, object targetObject, [DefaultProtocol, Optional]string targetName,
            params object[]/*!*/ targetArguments) {

            return Reinitialize(new Enumerator(), targetObject, targetName, targetArguments);
        }

        /// <summary>
        /// #dup and #clone: the copy iterates the same target with the same arguments (the
        /// instance variables a prelude-built enumerator keeps its generator in are copied by
        /// the runtime). Without this a copy had no target and failed with "undefined method
        /// `each' for nil".
        /// </summary>
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static Enumerator/*!*/ InitializeCopy(Enumerator/*!*/ self, [NotNull]Enumerator/*!*/ other) {
            self._impl = other._impl;
            self.SizeSource = other.SizeSource;
            self.SizeOp = other.SizeOp;
            self.SizeArg = other.SizeArg;
            return self;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static Enumerator/*!*/ Reinitialize(Enumerator/*!*/ self, object targetObject, [DefaultProtocol, Optional]string targetName,
            params object[] targetArguments) {

            self._impl = new Wrapper(targetObject, targetName, targetArguments);
            return self;
        }

        /// <summary>
        /// What the prelude's #initialize calls. #initialize itself mixes an optional parameter
        /// with a params array, and that combination does not survive being called from Ruby -
        /// every argument lands in the params array and the method name defaults to "each". Three
        /// mandatory parameters bind unambiguously.
        /// </summary>
        [RubyMethod("__enum_init__", RubyMethodAttributes.PrivateInstance)]
        public static Enumerator/*!*/ InitializeFrom(Enumerator/*!*/ self, object targetObject,
            [DefaultProtocol, NotNull]string/*!*/ targetName, [NotNull]RubyArray/*!*/ targetArguments) {

            self._impl = new Wrapper(targetObject, targetName, targetArguments.ToArray());
            return self;
        }

        [RubyMethod("each_without_generator")]
        public static object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block, Enumerator/*!*/ self) {
            return self._impl.Each(scope, block);
        }

        /// <summary>
        /// Enumerator#each. Iterating a method-based enumerator
        /// calls the method with the frame that called #each, as MRI's C #each does, so a Regexp
        /// match the method makes (String#scan) lands in the $~ the block reads. Anything else -
        /// arguments, a generator, no block - is the prelude's __ir_each_impl__.
        /// </summary>
        [RubyMethod("each")]
        public static object EachFromCaller(CallSiteStorage<Func<CallSite, object, Proc, System.Collections.IList, object>>/*!*/ implStorage,
            RubyScope/*!*/ scope, BlockParam block, Enumerator/*!*/ self, params object[]/*!*/ args) {

            object generator;
            if (args.Length == 0 && block != null && self._impl != null &&
                !(scope.RubyContext.TryGetInstanceVariable(self, "@generator", out generator) && generator != null)) {
                return self._impl.Each(scope, block);
            }

            var site = implStorage.GetCallSite("__ir_each_impl__",
                new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock | RubyCallFlags.HasSplattedArgument));
            return site.Target(site, self, (block != null) ? block.Proc : null, new RubyArray(args));
        }

        /// <summary>
        /// [receiver, :method, [args]] for an enumerator built from a method name, nil otherwise.
        /// The prelude needs it for #inspect and for the object #rewind forwards to.
        /// </summary>
        [RubyMethod("__enum_target__")]
        public static object GetTarget(RubyContext/*!*/ context, Enumerator/*!*/ self) {
            var wrapper = self._impl as Wrapper;
            if (wrapper == null) {
                return null;
            }
            var result = new RubyArray(3);
            result.Add(wrapper.TargetObject);
            result.Add(context.CreateSymbol(wrapper.TargetName, RubyEncoding.UTF8));
            result.Add(new RubyArray(wrapper.TargetArguments));
            return result;
        }

        /// <summary>
        /// [receiver, "op", arg] describing how to compute #size, or nil when it is unknown.
        /// </summary>
        [RubyMethod("__enum_size_info__")]
        public static object GetSizeInfo(RubyContext/*!*/ context, Enumerator/*!*/ self) {
            if (self.SizeOp == null) {
                return null;
            }
            var result = new RubyArray(3);
            result.Add(self.SizeSource);
            result.Add(context.CreateSymbol(self.SizeOp, RubyEncoding.UTF8));
            result.Add(self.SizeArg);
            return result;
        }

        #region external iteration over an Array

        /// <summary>
        /// Where Enumerator#next is in an Array it walks without a fiber (see GetArrayCursor).
        /// Held in an instance variable, so that a #dup shares it the way it would share the fiber.
        /// </summary>
        public sealed class ArrayCursor {
            internal readonly RubyArray/*!*/ Array;
            internal readonly System.Threading.Thread/*!*/ Thread;
            internal int Index;

            internal ArrayCursor(RubyArray/*!*/ array, System.Threading.Thread/*!*/ thread) {
                Array = array;
                Thread = thread;
            }
        }

        /// <summary>
        /// A cursor for an enumerator whose iteration is nothing but the built-in Array#each over an
        /// Array (`ary.each`, `ary.to_enum`), or nil. Enumerator#next then steps through the Array by
        /// index instead of suspending #each in a fiber - a thread of its own here. The two cannot be
        /// told apart: Array#each runs no Ruby code between elements and re-reads the length before
        /// each one, which is exactly what the cursor does, so an Array that grows or shrinks half
        /// way through behaves the same, and #each's result (the Array) is StopIteration#result.
        /// </summary>
        [RubyMethod("__ir_array_cursor__", RubyMethodAttributes.PrivateInstance)]
        public static ArrayCursor GetArrayCursor(RubyContext/*!*/ context, Enumerator/*!*/ self) {
            var wrapper = self._impl as Wrapper;
            RubyArray array;
            if (wrapper == null || wrapper.TargetName != "each" || wrapper.TargetArguments.Length != 0 ||
                (array = wrapper.TargetObject as RubyArray) == null) {
                return null;
            }

            object generator;
            if (context.TryGetInstanceVariable(self, "@generator", out generator) && generator != null) {
                return null;
            }

            // Neither the enumerator's #each nor the Array's may have been replaced (redefined,
            // aliased, overridden in a subclass or a singleton, prepended to, or removed).
            if (!IsBuiltinMethod(context, self, "each", typeof(Enumerator), "EachFromCaller") ||
                !IsBuiltinMethod(context, array, "each", typeof(IListOps), "Each")) {
                return null;
            }

            return new ArrayCursor(array, RubyUtils.CurrentRubyThread);
        }

        // External iteration needs the target/generator, not the public Enumerator that owns
        // the fiber handle. Preserve custom #each receivers, where detaching could change Ruby
        // behavior; those can still retain themselves through their suspended user frames.
        [RubyMethod("__enum_iteration_source__", RubyMethodAttributes.PrivateInstance)]
        public static Enumerator GetIterationSource(RubyContext context, Enumerator self) {
            if (context.GetImmediateClassOf(self) != context.GetClass(typeof(Enumerator)) ||
                !IsBuiltinMethod(context, self, "each", typeof(Enumerator), "EachFromCaller")) {
                return self;
            }
            var source = new Enumerator(self._impl);
            object generator;
            if (context.TryGetInstanceVariable(self, "@generator", out generator)) {
                context.SetInstanceVariable(source, "@generator", generator);
            }
            return source;
        }

        private static bool IsBuiltinMethod(RubyContext/*!*/ context, object target, string/*!*/ name, Type/*!*/ declaringType, string/*!*/ clrName) {
            var method = context.ResolveMethod(target, name, VisibilityContext.AllVisible);
            if (!method.Found || !(method.Info is RubyLibraryMethodInfo)) {
                return false;
            }
            var members = method.Info.GetMembers();
            if (members.Length == 0) {
                return false;
            }
            foreach (var member in members) {
                if (member.DeclaringType != declaringType || member.Name != clrName) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The next element as a one-element Array (what the fiber would have yielded), nil at the
        /// end, false when called from a thread other than the one that started the iteration -
        /// where the fiber would raise FiberError.
        /// </summary>
        [RubyMethod("__ir_array_cursor_next__", RubyMethodAttributes.PrivateInstance)]
        public static object ArrayCursorNext(Enumerator/*!*/ self, [NotNull]ArrayCursor/*!*/ cursor) {
            if (cursor.Thread != RubyUtils.CurrentRubyThread) {
                return ScriptingRuntimeHelpers.False;
            }
            RubyArray array = cursor.Array;
            int index = cursor.Index;
            if (index < array.Count) {
                cursor.Index = index + 1;
                RubyArray values = new RubyArray(1);
                values.Add(array[index]);
                return values;
            }
            return null;
        }

        [RubyMethod("__ir_array_cursor_source__", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ ArrayCursorSource(Enumerator/*!*/ self, [NotNull]ArrayCursor/*!*/ cursor) {
            return cursor.Array;
        }

        #endregion

        // TODO: 1.9:
        // :each_with_index, :each_with_object, :with_index, :with_object, :next, :rewind
    }
}

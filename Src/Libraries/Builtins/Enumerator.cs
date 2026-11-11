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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    internal interface IEnumerator {
        object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block);
    }

    /// <summary>
    /// A wrapper that provides "each" method for an arbitrary object. 
    /// A call to "each" on the instance of Enumerator is forwarded to a call to a method of a given name on the wrapped object.
    /// </summary>
    [RubyClass("Enumerator"), Includes(typeof(Enumerable))]
    public class Enumerator : IRubyInstanceDataOwner {
        // Its external iteration (#next, #peek, #rewind in ruby4.rb) keeps its state in instance
        // variables, so they live here rather than in the runtime's table of instance data.
        private OwnedInstanceData _instanceData;

        // An instance of a Ruby subclass (Enumerator::Lazy, or a user's) is an IRubyObject with instance
        // data of its own, which every other path reads: that is the one to hand out then.
        bool IRubyInstanceDataOwner.TryGetInstanceData(RubyContext/*!*/ context, out RubyInstanceData data) {
            var rubyObject = this as IRubyObject;
            if (rubyObject != null) {
                data = rubyObject.TryGetInstanceData();
                return true;
            }
            return _instanceData.TryGet(context, out data);
        }

        RubyInstanceData IRubyInstanceDataOwner.GetInstanceData(RubyContext/*!*/ context) {
            var rubyObject = this as IRubyObject;
            return (rubyObject != null) ? rubyObject.GetInstanceData() : _instanceData.GetOrCreate(context);
        }

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
        [ReceivesKeywords]
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
        [ReceivesKeywords]
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

        [RubyMethod("__each_without_generator__")]
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
        [ReceivesKeywords]
        public static object EachFromCaller(CallSiteStorage<Func<CallSite, object, Proc, System.Collections.IList, object>>/*!*/ implStorage,
            RubyScope/*!*/ scope, BlockParam block, Enumerator/*!*/ self, params object[]/*!*/ args) {

            object generator;
            if (args.Length == 0 && block != null && self._impl != null &&
                !(scope.RubyContext.TryGetInstanceVariable(self, RubyUtils.GetCoreStateInstanceVariableName("@__generator__"), out generator) && generator != null)) {
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

        #region external iteration: #next, #peek, #next_values, #peek_values, #feed

        // MRI's enumerator_next and friends suspend the #each they run in a fiber, which is a thread
        // of its own here: a resume / Fiber.yield round trip is a thread switch each way. Most
        // enumerators are over a built-in method that runs no Ruby code between two yields - Array#each,
        // Hash#each_pair, Integer#times... - and for those an ExternalCursor does what the method does,
        // one element per #next, with no fiber at all. Everything else (a generator, a lazy chain, a
        // method that has been redefined) goes on the prelude's fiber (__iter_slow__ in ruby4.rb).
        //
        // The cursor is chosen when the iteration starts, at the first #next or #peek, because that is
        // when MRI calls the method (next_i): a method redefined after the enumerator was made but
        // before then is the one that runs, and one redefined later does not stop the iteration that
        // is under way.

        // The iteration under way without a fiber, or null.
        private ExternalCursor _cursor;
        // The iteration under way, or the one that ended, runs on the prelude's fiber.
        private bool _onFiber;
        // Enumerator#feed: what the suspended yield answers once the iteration goes on.
        private object _feedValue;
        private bool _hasFeed;

        // __iter_slow__'s operations.
        private const int SlowAdvance = 0, SlowPeek = 1, SlowAcrossThreads = 2;

        /// <summary>
        /// Where the iteration of a built-in method has got to. A cursor belongs to the Ruby thread
        /// that started it, as the fiber would; its state changes under a lock, since another thread
        /// may take a value it peeked (MRI hands that one out without looking at the thread).
        /// </summary>
        internal abstract class ExternalCursor {
            internal readonly Thread/*!*/ Thread;
            // What the method answers (StopIteration#result): its receiver, for all of them.
            internal readonly object Receiver;
            internal bool Started, Peeked, Done;
            // The values of the step #peek has taken: the value, or an Array of them (Multiple).
            internal object PeekValue;
            internal StopIteration StopException;

            protected ExternalCursor(object receiver) {
                Receiver = receiver;
                Thread = RubyUtils.CurrentRubyThread;
            }

            /// <summary>Yields more than one value per step (each_with_index): the value is then an Array of them.</summary>
            internal virtual bool Multiple {
                get { return false; }
            }

            /// <summary>The next step of the iteration, false at its end.</summary>
            internal abstract bool MoveNext(out object value);

            /// <summary>The method has returned: what it does on its way out.</summary>
            internal virtual void Finish() {
            }
        }

        // Array#each, #each_index: the length and the element are read afresh for every step, as
        // rb_ary_each does, so an Array that grows or shrinks half way through is walked the same.
        private sealed class ArrayCursor : ExternalCursor {
            private readonly RubyArray/*!*/ _array;
            private readonly bool _indices;
            private int _index;

            internal ArrayCursor(RubyArray/*!*/ array, bool indices) : base(array) {
                _array = array;
                _indices = indices;
            }

            internal override bool MoveNext(out object value) {
                int index = _index;
                if (index < _array.Count) {
                    _index = index + 1;
                    value = _indices ? ScriptingRuntimeHelpers.Int32ToObject(index) : _array[index];
                    return true;
                }
                value = null;
                return false;
            }
        }

        // Enumerable#each_with_index over Array#each: [element, index], two values per step.
        private sealed class ArrayWithIndexCursor : ExternalCursor {
            private readonly RubyArray/*!*/ _array;
            private int _index;

            internal ArrayWithIndexCursor(RubyArray/*!*/ array) : base(array) {
                _array = array;
            }

            internal override bool Multiple {
                get { return true; }
            }

            internal override bool MoveNext(out object value) {
                int index = _index;
                if (index < _array.Count) {
                    _index = index + 1;
                    var values = new RubyArray(2);
                    values.Add(_array[index]);
                    values.Add(ScriptingRuntimeHelpers.Int32ToObject(index));
                    value = values;
                    return true;
                }
                value = null;
                return false;
            }
        }

        // Array#reverse_each (rb_ary_reverse_each): from the length the iteration started with, down,
        // moving the position back when the Array has shrunk below it in the meantime.
        private sealed class ArrayReverseCursor : ExternalCursor {
            private readonly RubyArray/*!*/ _array;
            private int _index;
            private bool _yielded;

            internal ArrayReverseCursor(RubyArray/*!*/ array) : base(array) {
                _array = array;
                _index = array.Count;
            }

            internal override bool MoveNext(out object value) {
                if (_yielded) {
                    int count = _array.Count;
                    if (count < _index) {
                        _index = count;
                    }
                }
                if (_index > 0) {
                    _index--;
                    _yielded = true;
                    value = _array[_index];
                    return true;
                }
                value = null;
                return false;
            }
        }

        // Hash#each/#each_pair, #each_key, #each_value, walked in place (see Hash.TryGetNextEntry): an
        // entry deleted ahead of the walk is skipped and a value overwritten is seen overwritten. The
        // hash is being iterated - no new key, no #rehash - from the first step to the end of the walk,
        // as in MRI, where it stays so for good if the walk never gets to its end (#rewind, or a dropped
        // enumerator): MRI's fiber never runs hash_foreach_ensure then.
        private sealed class HashCursor : ExternalCursor {
            internal const int Pairs = 0, Keys = 1, Values = 2;
            private readonly Hash/*!*/ _hash;
            private readonly int _kind;
            private int _index;
            private bool _begun, _iterating;

            internal HashCursor(Hash/*!*/ hash, int kind) : base(hash) {
                _hash = hash;
                _kind = kind;
            }

            internal override bool MoveNext(out object value) {
                if (!_begun) {
                    _begun = true;
                    // rb_hash_foreach does not iterate an empty hash at all
                    if (_hash.Count == 0) {
                        value = null;
                        return false;
                    }
                    _hash.BeginIteration();
                    _iterating = true;
                }
                object key, entryValue;
                if (_hash.TryGetNextEntry(ref _index, out key, out entryValue)) {
                    switch (_kind) {
                        case Keys: value = CustomStringDictionary.ObjToNull(key); break;
                        case Values: value = entryValue; break;
                        default: value = IDictionaryOps.MakeArray(key, entryValue); break;
                    }
                    return true;
                }
                value = null;
                return false;
            }

            internal override void Finish() {
                if (_iterating) {
                    _iterating = false;
                    _hash.EndIteration();
                }
            }
        }

        // Range#each over Integers, Integer#times, #upto and #downto: the ends are fixed when the
        // iteration starts. Last is inclusive; an endless range runs to the end of long, which nobody
        // reaches with #next.
        private sealed class IntegerCursor : ExternalCursor {
            private long _next;
            private readonly long _last;
            private readonly bool _down;
            private bool _done;

            internal IntegerCursor(object receiver, long first, long last, bool down) : base(receiver) {
                _next = first;
                _last = last;
                _down = down;
                _done = down ? first < last : first > last;
            }

            internal override bool MoveNext(out object value) {
                if (_done) {
                    value = null;
                    return false;
                }
                long current = _next;
                value = Protocols.Normalize(current);
                if (current == _last) {
                    _done = true;
                } else {
                    _next = _down ? current - 1 : current + 1;
                }
                return true;
            }
        }

        // String#each_byte (rb_str_each_byte) reads the length and the byte afresh for every step.
        private sealed class ByteCursor : ExternalCursor {
            private readonly MutableString/*!*/ _string;
            private int _index;

            internal ByteCursor(MutableString/*!*/ str) : base(str) {
                _string = str;
            }

            internal override bool MoveNext(out object value) {
                int index = _index;
                if (index < _string.GetByteCount()) {
                    _index = index + 1;
                    value = ScriptingRuntimeHelpers.Int32ToObject((int)_string.GetByte(index));
                    return true;
                }
                value = null;
                return false;
            }
        }

        // String#each_char (rb_str_each_char) walks a frozen copy of the string, made as it starts:
        // what the string becomes later is not seen.
        private sealed class CharCursor : ExternalCursor {
            private readonly MutableString/*!*/ _copy;
            private readonly MutableString.CharacterEnumerator/*!*/ _chars;

            internal CharCursor(MutableString/*!*/ str) : base(str) {
                _copy = MutableString.Create(str);
                _chars = _copy.GetCharacters();
            }

            internal override bool MoveNext(out object value) {
                if (_chars.MoveNext()) {
                    value = _chars.Current.ToMutableString(_copy.Encoding);
                    return true;
                }
                value = null;
                return false;
            }
        }

        // Struct#each (rb_struct_each): every member read when its turn comes.
        private sealed class StructCursor : ExternalCursor {
            private readonly RubyStruct/*!*/ _struct;
            private int _index;

            internal StructCursor(RubyStruct/*!*/ str) : base(str) {
                _struct = str;
            }

            internal override bool MoveNext(out object value) {
                int index = _index;
                if (index < _struct.ItemCount) {
                    _index = index + 1;
                    value = _struct[index];
                    return true;
                }
                value = null;
                return false;
            }
        }

        // The prelude's own definitions of methods a cursor stands in for (registered by
        // __iter_cursor_method__), per runtime: a method that is still one of these has not been
        // replaced.
        private sealed class PreludeMethods {
            internal RubyMemberInfo EachWithIndex;
            internal RubyMemberInfo StructEach;
        }

        private static readonly object _preludeMethodsKey = new object();

        private static PreludeMethods/*!*/ GetPreludeMethods(RubyContext/*!*/ context) {
            return (PreludeMethods)context.GetOrCreateLibraryData(_preludeMethodsKey, () => new PreludeMethods());
        }

        /// <summary>
        /// Called by the prelude right after it defines a method that wraps a built-in one a cursor
        /// can stand in for (Enumerable#each_with_index, Struct#each).
        /// </summary>
        [RubyMethod("__iter_cursor_method__", RubyMethodAttributes.PublicSingleton)]
        public static object RegisterCursorMethod(RubyContext/*!*/ context, object self, [NotNull]RubyModule/*!*/ module, [DefaultProtocol, NotNull]string/*!*/ name) {
            var method = module.ResolveMethod(name, VisibilityContext.AllVisible);
            if (method.Found) {
                var methods = GetPreludeMethods(context);
                if (name == "each_with_index") {
                    methods.EachWithIndex = method.Info;
                } else if (name == "each") {
                    methods.StructEach = method.Info;
                }
            }
            return null;
        }

        private static bool IsPreludeMethod(RubyContext/*!*/ context, object target, string/*!*/ name, RubyMemberInfo expected) {
            if (expected == null) {
                return false;
            }
            var method = context.ResolveMethod(target, name, VisibilityContext.AllVisible);
            return method.Found && ReferenceEquals(method.Info, expected);
        }

        /// <summary>
        /// A cursor for this enumerator's iteration, or null when it has to run on a fiber: it is not a
        /// built-in method over one of its own receivers, or one of the methods involved - the
        /// enumerator's #each, the target method, and what that one calls - has been replaced
        /// (redefined, aliased over, overridden in a subclass or a singleton class, prepended to).
        /// </summary>
        private static ExternalCursor TryCreateCursor(RubyContext/*!*/ context, Enumerator/*!*/ self) {
            var wrapper = self._impl as Wrapper;
            if (wrapper == null) {
                return null;
            }

            object generator;
            if (context.TryGetInstanceVariable(self, RubyUtils.GetCoreStateInstanceVariableName("@__generator__"), out generator) && generator != null) {
                return null;
            }

            if (!IsBuiltinMethod(context, self, "each", typeof(Enumerator), "EachFromCaller")) {
                return null;
            }

            object target = wrapper.TargetObject;
            string name = wrapper.TargetName;
            object[] args = wrapper.TargetArguments;

            if (args.Length == 1 && target is int && args[0] is int) {
                int from = (int)target, to = (int)args[0];
                if (name == "upto" && IsBuiltinMethod(context, target, name, typeof(Integer), "UpTo")) {
                    return new IntegerCursor(target, from, to, false);
                }
                if (name == "downto" && IsBuiltinMethod(context, target, name, typeof(Integer), "DownTo")) {
                    return new IntegerCursor(target, from, to, true);
                }
                return null;
            }
            if (args.Length != 0) {
                return null;
            }

            RubyArray array;
            Hash hash;
            Range range;
            MutableString str;
            RubyStruct rubyStruct;
            switch (name) {
                case "each":
                    if ((array = target as RubyArray) != null) {
                        return IsBuiltinMethod(context, target, name, typeof(IListOps), "Each") ? new ArrayCursor(array, false) : null;
                    }
                    if ((hash = target as Hash) != null) {
                        return IsBuiltinMethod(context, target, name, typeof(IDictionaryOps), "Each", "EachPair") ? new HashCursor(hash, HashCursor.Pairs) : null;
                    }
                    if ((range = target as Range) != null) {
                        return TryCreateRangeCursor(context, range);
                    }
                    if ((rubyStruct = target as RubyStruct) != null) {
                        return IsPreludeMethod(context, target, name, GetPreludeMethods(context).StructEach) &&
                            IsBuiltinMethod(context, target, "__struct_each__", typeof(RubyStructOps), "Each") ? new StructCursor(rubyStruct) : null;
                    }
                    return null;

                case "each_pair":
                    return (hash = target as Hash) != null && IsBuiltinMethod(context, target, name, typeof(IDictionaryOps), "Each", "EachPair") ?
                        new HashCursor(hash, HashCursor.Pairs) : null;

                case "each_key":
                    return (hash = target as Hash) != null && IsBuiltinMethod(context, target, name, typeof(IDictionaryOps), "EachKey") ?
                        new HashCursor(hash, HashCursor.Keys) : null;

                case "each_value":
                    return (hash = target as Hash) != null && IsBuiltinMethod(context, target, name, typeof(IDictionaryOps), "EachValue") ?
                        new HashCursor(hash, HashCursor.Values) : null;

                case "each_index":
                    return (array = target as RubyArray) != null && IsBuiltinMethod(context, target, name, typeof(IListOps), "EachIndex") ?
                        new ArrayCursor(array, true) : null;

                case "reverse_each":
                    return (array = target as RubyArray) != null && IsBuiltinMethod(context, target, name, typeof(IListOps), "ReverseEach") ?
                        new ArrayReverseCursor(array) : null;

                case "each_with_index":
                    // the prelude's #each_with_index, the built-in one it hands a block to, and the #each that one calls
                    return (array = target as RubyArray) != null &&
                        IsPreludeMethod(context, target, name, GetPreludeMethods(context).EachWithIndex) &&
                        IsBuiltinMethod(context, target, "__each_with_index_without_args__", typeof(Enumerable), "GetEachWithIndexEnumerator", "EachWithIndex") &&
                        IsBuiltinMethod(context, target, "each", typeof(IListOps), "Each") ? new ArrayWithIndexCursor(array) : null;

                case "times":
                    if (target is int && IsBuiltinMethod(context, target, name, typeof(Integer), "Times")) {
                        int count = (int)target;
                        return new IntegerCursor(target, 0, (long)count - 1, false);
                    }
                    return null;

                case "each_byte":
                    return (str = target as MutableString) != null && IsBuiltinMethod(context, target, name, typeof(MutableStringOps), "EachByte") ?
                        new ByteCursor(str) : null;

                case "each_char":
                    return (str = target as MutableString) != null && IsBuiltinMethod(context, target, name, typeof(MutableStringOps), "EachChar") ?
                        new CharCursor(str) : null;
            }
            return null;
        }

        // An Integer range whose end is an Integer that fits a long, or none: range_each's fixnum loops.
        private static ExternalCursor TryCreateRangeCursor(RubyContext/*!*/ context, Range/*!*/ range) {
            if (!(range.Begin is int) || !IsBuiltinMethod(context, range, "each", typeof(RangeOps), "GetEachEnumerator", "Each")) {
                return null;
            }
            long first = (int)range.Begin;
            long last;
            object end = range.End;
            if (end == null) {
                last = Int64.MaxValue;
            } else if (end is int) {
                last = (int)end;
            } else if (end is BigInteger && (BigInteger)end >= Int64.MinValue && (BigInteger)end <= Int64.MaxValue) {
                last = (long)(BigInteger)end;
            } else {
                return null;
            }
            if (end != null && range.ExcludeEnd) {
                if (last == Int64.MinValue) {
                    return null;
                }
                last--;
            }
            return new IntegerCursor(range, first, last, false);
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
            if (context.TryGetInstanceVariable(self, RubyUtils.GetCoreStateInstanceVariableName("@__generator__"), out generator)) {
                context.SetInstanceVariable(source, RubyUtils.GetCoreStateInstanceVariableName("@__generator__"), generator);
            }
            return source;
        }

        private static bool IsBuiltinMethod(RubyContext/*!*/ context, object target, string/*!*/ name, Type/*!*/ declaringType, params string[]/*!*/ clrNames) {
            var method = context.ResolveMethod(target, name, VisibilityContext.AllVisible);
            if (!method.Found || !(method.Info is RubyLibraryMethodInfo)) {
                return false;
            }
            var members = method.Info.GetMembers();
            if (members.Length == 0) {
                return false;
            }
            foreach (var member in members) {
                if (member.DeclaringType != declaringType || Array.IndexOf(clrNames, member.Name) < 0) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The cursor of the iteration under way or about to start, or null when it is the fiber's.
        /// Raises FiberError (through the prelude) for a cursor another thread started, unless it
        /// has a peeked value to hand out or has ended, which MRI looks at first.
        /// </summary>
        private static ExternalCursor GetCursor(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self) {
            RubyContext context = slowStorage.Context;
            if (context.IsObjectFrozen(self)) {
                throw RubyExceptions.CreateObjectFrozenError(context, self);
            }

            ExternalCursor cursor = self._cursor;
            if (cursor == null) {
                if (self._onFiber) {
                    return null;
                }
                ExternalCursor created = TryCreateCursor(context, self);
                if (created == null) {
                    self._onFiber = true;
                    return null;
                }
                // Two threads that start the iteration together: one of them gets the cursor, and the
                // other one is "across threads" (nothing has been done to the receiver yet).
                cursor = Interlocked.CompareExchange(ref self._cursor, created, null) ?? created;
            }

            if (cursor.Thread != RubyUtils.CurrentRubyThread && !cursor.Peeked && !cursor.Done) {
                var site = slowStorage.GetCallSite("__iter_slow__", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
                site.Target(site, self, ScriptingRuntimeHelpers.Int32ToObject(SlowAcrossThreads));
            }
            return cursor;
        }

        /// <summary>
        /// Makes sure the cursor holds the values of the next step (in PeekValue) and answers them,
        /// or raises StopIteration at the end; <paramref name="consume"/> takes them out as well.
        /// Ends with the cursor's lock released: everything that can raise happens outside it.
        /// </summary>
        private static object CursorStep(RubyContext/*!*/ context, Enumerator/*!*/ self, ExternalCursor/*!*/ cursor, bool consume) {
            StopIteration stop = null;
            bool first = false;
            object value;
            using (new RubyUtils.UninterruptedLock(cursor)) {
                if (!cursor.Peeked) {
                    if (cursor.Done) {
                        stop = cursor.StopException;
                    } else {
                        // the suspended yield returns (next_ii), taking the fed value with it
                        if (cursor.Started) {
                            self._feedValue = null;
                            self._hasFeed = false;
                        }
                        cursor.Started = true;
                        if (cursor.MoveNext(out value)) {
                            cursor.PeekValue = value;
                            cursor.Peeked = true;
                        } else {
                            cursor.Done = true;
                            cursor.Finish();
                            self._feedValue = null;
                            self._hasFeed = false;
                            stop = cursor.StopException = CreateStopIteration(context, cursor.Receiver, null);
                            first = true;
                        }
                    }
                }

                if (stop == null) {
                    value = cursor.PeekValue;
                    if (consume) {
                        cursor.Peeked = false;
                        cursor.PeekValue = null;
                    }
                    return value;
                }
            }

            // get_next_values: the end is raised once as such, and from then on as a new StopIteration
            // whose cause is the first one
            throw first ? stop : CreateStopIteration(context, cursor.Receiver, stop);
        }

        private static StopIteration/*!*/ CreateStopIteration(RubyContext/*!*/ context, object result, StopIteration cause) {
            var stop = new StopIteration("iteration reached an end");
            context.SetInstanceVariable(stop, RubyUtils.GetCoreStateInstanceVariableName("@__result__"), result);
            if (cause != null) {
                RubyExceptionData.GetInstance(stop).SetCause(cause);
            }
            return stop;
        }

        private static RubyArray/*!*/ SlowStep(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self, int op) {
            var site = slowStorage.GetCallSite("__iter_slow__", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
            return (RubyArray)site.Target(site, self, ScriptingRuntimeHelpers.Int32ToObject(op));
        }

        // ary2sv: no values are nil, one is that value, more are the Array of them (a copy for #peek)
        private static object ValuesToValue(RubyArray/*!*/ values, bool copy) {
            switch (values.Count) {
                case 0: return null;
                case 1: return values[0];
                default: return copy ? new RubyArray(values) : values;
            }
        }

        private static RubyArray/*!*/ CursorValues(ExternalCursor/*!*/ cursor, object value, bool copy) {
            if (cursor.Multiple) {
                return copy ? new RubyArray((RubyArray)value) : (RubyArray)value;
            }
            var values = new RubyArray(1);
            values.Add(value);
            return values;
        }

        [RubyMethod("next")]
        public static object Next(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self) {
            var cursor = GetCursor(slowStorage, self);
            if (cursor == null) {
                return ValuesToValue(SlowStep(slowStorage, self, SlowAdvance), false);
            }
            return CursorStep(slowStorage.Context, self, cursor, true);
        }

        [RubyMethod("peek")]
        public static object Peek(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self) {
            var cursor = GetCursor(slowStorage, self);
            if (cursor == null) {
                return ValuesToValue(SlowStep(slowStorage, self, SlowPeek), true);
            }
            object value = CursorStep(slowStorage.Context, self, cursor, false);
            return cursor.Multiple ? new RubyArray((RubyArray)value) : value;
        }

        [RubyMethod("next_values")]
        public static RubyArray/*!*/ NextValues(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self) {
            var cursor = GetCursor(slowStorage, self);
            if (cursor == null) {
                return SlowStep(slowStorage, self, SlowAdvance);
            }
            return CursorValues(cursor, CursorStep(slowStorage.Context, self, cursor, true), false);
        }

        [RubyMethod("peek_values")]
        public static RubyArray/*!*/ PeekValues(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self) {
            var cursor = GetCursor(slowStorage, self);
            if (cursor == null) {
                return new RubyArray(SlowStep(slowStorage, self, SlowPeek));
            }
            return CursorValues(cursor, CursorStep(slowStorage.Context, self, cursor, false), true);
        }

        /// <summary>
        /// The value the suspended yield inside the method will answer (the next #next lets it go
        /// on); one at a time.
        /// </summary>
        [RubyMethod("feed")]
        public static object Feed(RubyContext/*!*/ context, Enumerator/*!*/ self, object value) {
            if (context.IsObjectFrozen(self)) {
                throw RubyExceptions.CreateObjectFrozenError(context, self);
            }
            if (self._hasFeed) {
                throw RubyExceptions.CreateTypeError("feed value already set");
            }
            self._feedValue = value;
            self._hasFeed = true;
            return null;
        }

        /// <summary>
        /// For the fiber: takes the fed value, if any, as the suspended yield returns.
        /// </summary>
        [RubyMethod("__iter_take_feed__", RubyMethodAttributes.PrivateInstance)]
        public static object TakeFeed(Enumerator/*!*/ self) {
            object value = self._feedValue;
            self._feedValue = null;
            self._hasFeed = false;
            return value;
        }

        /// <summary>
        /// Forgets the external iteration, whichever way it ran (#rewind, #initialize_copy). A hash a
        /// cursor was walking stays iterated, as MRI's stays for a fiber nobody resumes.
        /// </summary>
        [RubyMethod("__iter_reset__", RubyMethodAttributes.PrivateInstance)]
        public static object ResetExternalIteration(Enumerator/*!*/ self) {
            self._cursor = null;
            self._onFiber = false;
            self._feedValue = null;
            self._hasFeed = false;
            return null;
        }

        /// <summary>
        /// Whether a cursor is in the middle of an iteration: started, and not at its end. Nil when
        /// the iteration is the fiber's to tell.
        /// </summary>
        [RubyMethod("__iter_cursor_in_progress__", RubyMethodAttributes.PrivateInstance)]
        public static object IsCursorInProgress(Enumerator/*!*/ self) {
            var cursor = self._cursor;
            if (cursor == null) {
                return null;
            }
            return ScriptingRuntimeHelpers.BooleanToObject((cursor.Started || cursor.Peeked) && !cursor.Done);
        }

        #endregion
    }
}

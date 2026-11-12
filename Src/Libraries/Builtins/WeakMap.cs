/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 * By using this source code in any fashion, you are agreeing to be bound by the terms of the
 * Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    /// <summary>
    /// ObjectSpace::WeakMap: keys compared by identity, and an entry goes away once its key or its
    /// value is collected - what lib/weakref.rb builds on. What MRI keeps as an immediate (nil, true,
    /// false, a fixnum, a flonum, a Symbol) is never collected; IronRuby boxes those, so they are held
    /// strongly and compared by value.
    /// </summary>
    [RubyClass("WeakMap", DefineIn = typeof(ObjectSpace), Inherits = typeof(object)), Includes(typeof(Enumerable))]
    public class WeakMap : RubyObject {
        private sealed class Entry {
            public readonly int Hash;
            public readonly object Key;   // WeakReference, or the immediate itself
            public object Value;          // ditto
            public Entry Next;

            public Entry(int hash, object key, object value) {
                Hash = hash;
                Key = key;
                Value = value;
            }
        }

        private readonly Dictionary<int, Entry>/*!*/ _buckets = new Dictionary<int, Entry>();
        private int _count;
        private int _pruneAt = 16;

        public WeakMap(RubyClass/*!*/ cls)
            : base(cls) {
        }

        #region Entries

        private static bool IsImmediate(object obj) {
            return obj == null || obj is bool || obj is int || obj is long || obj is double || obj is RubySymbol;
        }

        private static int GetHash(object obj) {
            return obj == null ? 0 : IsImmediate(obj) ? obj.GetHashCode() : RuntimeHelpers.GetHashCode(obj);
        }

        private static object Wrap(object obj) {
            return IsImmediate(obj) ? obj : new WeakReference(obj);
        }

        // False if the object has been collected.
        private static bool TryUnwrap(object reference, out object obj) {
            var weak = reference as WeakReference;
            if (weak != null) {
                obj = weak.Target;
                return obj != null;
            }
            obj = reference;
            return true;
        }

        private static bool Matches(Entry/*!*/ entry, object key) {
            object entryKey;
            if (!TryUnwrap(entry.Key, out entryKey)) {
                return false;
            }
            return IsImmediate(key) ? object.Equals(entryKey, key) : ReferenceEquals(entryKey, key);
        }

        private static bool IsAlive(Entry/*!*/ entry) {
            object obj;
            return TryUnwrap(entry.Key, out obj) && TryUnwrap(entry.Value, out obj);
        }

        private Entry FindNoLock(object key) {
            Entry entry;
            if (_buckets.TryGetValue(GetHash(key), out entry)) {
                for (; entry != null; entry = entry.Next) {
                    if (Matches(entry, key) && IsAlive(entry)) {
                        return entry;
                    }
                }
            }
            return null;
        }

        private bool TryGetNoLock(object key, out object value) {
            var entry = FindNoLock(key);
            if (entry != null && TryUnwrap(entry.Value, out value)) {
                return true;
            }
            value = null;
            return false;
        }

        private bool RemoveNoLock(object key, out object value) {
            int hash = GetHash(key);
            Entry first;
            if (_buckets.TryGetValue(hash, out first)) {
                Entry previous = null;
                for (var entry = first; entry != null; previous = entry, entry = entry.Next) {
                    if (Matches(entry, key) && TryUnwrap(entry.Value, out value)) {
                        Unlink(hash, previous, entry);
                        return true;
                    }
                }
            }
            value = null;
            return false;
        }

        private void Unlink(int hash, Entry previous, Entry/*!*/ entry) {
            if (previous != null) {
                previous.Next = entry.Next;
            } else if (entry.Next != null) {
                _buckets[hash] = entry.Next;
            } else {
                _buckets.Remove(hash);
            }
            _count--;
        }

        // Drops the entries whose key or value has been collected.
        private void PruneNoLock() {
            var hashes = new List<int>(_buckets.Keys);
            foreach (int hash in hashes) {
                Entry previous = null;
                for (var entry = _buckets[hash]; entry != null; entry = entry.Next) {
                    if (IsAlive(entry)) {
                        previous = entry;
                    } else {
                        Unlink(hash, previous, entry);
                        if (!_buckets.ContainsKey(hash)) {
                            break;
                        }
                    }
                }
            }
            _pruneAt = Math.Max(16, _count * 2);
        }

        private List<KeyValuePair<object, object>>/*!*/ GetLiveEntries() {
            lock (_buckets) {
                PruneNoLock();
                var result = new List<KeyValuePair<object, object>>(_count);
                foreach (var first in _buckets.Values) {
                    for (var entry = first; entry != null; entry = entry.Next) {
                        object key, value;
                        if (TryUnwrap(entry.Key, out key) && TryUnwrap(entry.Value, out value)) {
                            result.Add(new KeyValuePair<object, object>(key, value));
                        }
                    }
                }
                return result;
            }
        }

        #endregion

        [RubyMethod("[]")]
        public static object GetValue(WeakMap/*!*/ self, object key) {
            lock (self._buckets) {
                object value;
                self.TryGetNoLock(key, out value);
                return value;
            }
        }

        [RubyMethod("[]=")]
        public static object SetValue(WeakMap/*!*/ self, object key, object value) {
            lock (self._buckets) {
                var entry = self.FindNoLock(key);
                if (entry != null) {
                    entry.Value = Wrap(value);
                    return value;
                }

                if (self._count >= self._pruneAt) {
                    self.PruneNoLock();
                }

                int hash = GetHash(key);
                entry = new Entry(hash, Wrap(key), Wrap(value));
                Entry first;
                if (self._buckets.TryGetValue(hash, out first)) {
                    entry.Next = first;
                }
                self._buckets[hash] = entry;
                self._count++;
                return value;
            }
        }

        [RubyMethod("key?")]
        [RubyMethod("include?")]
        [RubyMethod("member?")]
        public static bool HasKey(WeakMap/*!*/ self, object key) {
            lock (self._buckets) {
                object value;
                return self.TryGetNoLock(key, out value);
            }
        }

        [RubyMethod("delete")]
        public static object Delete(BlockParam block, WeakMap/*!*/ self, object key) {
            object value;
            bool removed;
            lock (self._buckets) {
                removed = self.RemoveNoLock(key, out value);
            }
            if (removed) {
                return value;
            }
            if (block != null) {
                object result;
                block.Yield(key, out result);
                return result;
            }
            return null;
        }

        [RubyMethod("size")]
        [RubyMethod("length")]
        public static int GetSize(WeakMap/*!*/ self) {
            lock (self._buckets) {
                self.PruneNoLock();
                return self._count;
            }
        }

        [RubyMethod("keys")]
        public static RubyArray/*!*/ GetKeys(WeakMap/*!*/ self) {
            var result = new RubyArray();
            foreach (var entry in self.GetLiveEntries()) {
                result.Add(entry.Key);
            }
            return result;
        }

        [RubyMethod("values")]
        public static RubyArray/*!*/ GetValues(WeakMap/*!*/ self) {
            var result = new RubyArray();
            foreach (var entry in self.GetLiveEntries()) {
                result.Add(entry.Value);
            }
            return result;
        }

        [RubyMethod("each")]
        [RubyMethod("each_pair")]
        public static object Each(BlockParam block, WeakMap/*!*/ self) {
            var entries = self.GetLiveEntries();
            if (entries.Count > 0 && block == null) {
                // wmap_each and friends yield from C: MRI's message has no "(yield)"
                throw RubyExceptions.CreateLocalJumpError("no block given");
            }
            foreach (var entry in entries) {
                object result;
                if (block.Yield(entry.Key, entry.Value, out result)) {
                    return result;
                }
            }
            return self;
        }

        [RubyMethod("each_key")]
        public static object EachKey(BlockParam block, WeakMap/*!*/ self) {
            var entries = self.GetLiveEntries();
            if (entries.Count > 0 && block == null) {
                throw RubyExceptions.CreateLocalJumpError("no block given");
            }
            foreach (var entry in entries) {
                object result;
                if (block.Yield(entry.Key, out result)) {
                    return result;
                }
            }
            return self;
        }

        [RubyMethod("each_value")]
        public static object EachValue(BlockParam block, WeakMap/*!*/ self) {
            var entries = self.GetLiveEntries();
            if (entries.Count > 0 && block == null) {
                throw RubyExceptions.CreateLocalJumpError("no block given");
            }
            foreach (var entry in entries) {
                object result;
                if (block.Yield(entry.Value, out result)) {
                    return result;
                }
            }
            return self;
        }

        // MRI shows the objects as #<Class:0x...> whatever their #inspect (rb_any_to_s), and only
        // the immediates through #inspect.
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, WeakMap/*!*/ self) {
            var result = RubyUtils.ObjectToMutableStringPrefix(context, self);
            bool first = true;
            foreach (var entry in self.GetLiveEntries()) {
                result.Append(first ? ": " : ", ");
                first = false;
                AppendObject(context, result, entry.Key);
                result.Append(" => ");
                AppendObject(context, result, entry.Value);
            }
            return result.Append('>');
        }

        private static void AppendObject(RubyContext/*!*/ context, MutableString/*!*/ str, object obj) {
            if (IsImmediate(obj)) {
                str.Append(context.Inspect(obj));
            } else {
                str.Append(RubyUtils.ObjectToMutableString(context, obj));
            }
        }
    }
}

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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// The insertion-ordered backing store of <see cref="Hash"/>.
    ///
    /// Ruby has guaranteed that a Hash enumerates in insertion order since 1.9, and
    /// <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> does not: it puts a newly
    /// added entry into the free slot that a removed entry left behind, so a single #delete makes
    /// every later #each, #keys, #to_a and #inspect report the wrong order.  Hash used to derive
    /// from Dictionary and inherit that bug.
    ///
    /// The layout here is the one CRuby's st_table, CPython's dict and .NET 9's
    /// <c>OrderedDictionary{TKey,TValue}</c> all use - an "index table plus entry array":
    ///
    ///   _entries   entries in *insertion order*; a removed entry becomes a tombstone in place
    ///              (<c>Next == TombstoneNext</c>) so the entries after it keep their positions.
    ///   _buckets   the hash table proper: a 1-based index into _entries, 0 meaning empty.
    ///              Collisions chain through <c>Entry.Next</c>, exactly as Dictionary does.
    ///   _count     slots of _entries in use, live entries and tombstones together.
    ///   _liveCount the Ruby Hash#size.
    ///
    /// Enumeration walks _entries[0.._count) and skips tombstones, which is insertion order by
    /// construction.  Assigning to an existing key writes the value into the entry it is already
    /// in, so it neither moves nor replaces the key object; #delete followed by a re-insert
    /// appends, so the key does move to the end.  Both are what Ruby specifies.
    ///
    /// Tombstones are reclaimed by a compaction pass, not eagerly.  Compaction only runs when the
    /// entry array is full AND at least a quarter of it is dead, so each pass frees at least a
    /// quarter of the capacity and at least that many appends must happen before the next one -
    /// amortized O(1) per insert.  Compacting on every delete instead (or on any tombstone at
    /// all) is what makes this shape go quadratic on a delete/insert churn, which is why the
    /// threshold is a fraction of the capacity and not a constant.  A delete of the *last* entry
    /// is reclaimed immediately, which keeps Hash#shift-style and push/pop-style use from
    /// accumulating tombstones at all.
    ///
    /// Thread safety is the same as Dictionary's: none.  Concurrent mutation corrupts the
    /// structure, and mutation during enumeration is detected through _version.  As with
    /// Dictionary, the comparer dispatches into Ruby (#hash and #eql?) and so can re-enter and
    /// mutate the hash; the lookup loops therefore index the array afresh rather than hold a
    /// managed pointer across such a call, so a re-entrant comparer can produce a nonsensical
    /// result but not memory corruption.
    /// </summary>
    public partial class Hash {
        private struct Entry {
            /// <summary>The comparer's hash code of <see cref="Key"/>. Meaningless in a tombstone.</summary>
            public int HashCode;
            /// <summary>
            /// Index of the next entry in this bucket's chain, -1 at the end of a chain, and
            /// <see cref="TombstoneNext"/> for a slot whose entry has been removed.
            /// </summary>
            public int Next;
            public object Key;
            public object Value;
        }

        private const int TombstoneNext = -2;

        private int[] _buckets;
        private Entry[] _entries;
        private ulong _fastModMultiplier;
        private int _count;
        private int _liveCount;
        /// <summary>
        /// The lowest index of _entries that can still hold a live entry: every enumeration starts
        /// here. Without it, Hash#shift - which removes the first entry over and over - would leave
        /// a growing run of tombstones at the front that every later shift has to walk past, and a
        /// loop of shifts would be quadratic.
        /// </summary>
        private int _start;
        private int _version;
        // Changes with every mutation, an overwrite of an existing key's value included (which
        // _version leaves alone, so that enumerators don't fail on it). See MutationStamp.
        private int _stamp;
        private IEqualityComparer<object>/*!*/ _comparer;

        private KeyCollection _keys;
        private ValueCollection _values;

        #region Sizing

        // The same prime table Dictionary uses: prime bucket counts keep a poor hash function
        // from degenerating into one long chain, which a power-of-two mask would not.
        private static readonly int[]/*!*/ _Primes = {
            3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521,
            631, 761, 919, 1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419,
            10103, 12143, 14591, 17519, 21023, 25229, 30293, 36353, 43627, 52361, 62851, 75431,
            90523, 108631, 130363, 156437, 187751, 225307, 270371, 324449, 389357, 467237, 560689,
            672827, 807403, 968897, 1162687, 1395263, 1674319, 2009191, 2411033, 2893249, 3471899,
            4166287, 4999559, 5999471, 7199369
        };

        private const int MaxPrimeArrayLength = 0x7FFFFFC3;

        private static bool IsPrime(int candidate) {
            if ((candidate & 1) == 0) {
                return candidate == 2;
            }
            int limit = (int)Math.Sqrt(candidate);
            for (int divisor = 3; divisor <= limit; divisor += 2) {
                if ((candidate % divisor) == 0) {
                    return false;
                }
            }
            return true;
        }

        private static int GetPrime(int min) {
            for (int i = 0; i < _Primes.Length; i++) {
                int prime = _Primes[i];
                if (prime >= min) {
                    return prime;
                }
            }
            for (int i = (min | 1); i < Int32.MaxValue; i += 2) {
                if (IsPrime(i)) {
                    return i;
                }
            }
            return min;
        }

        private static int ExpandPrime(int oldSize) {
            int newSize = 2 * oldSize;
            if ((uint)newSize > MaxPrimeArrayLength && MaxPrimeArrayLength > oldSize) {
                return MaxPrimeArrayLength;
            }
            return GetPrime(newSize);
        }

        /// <summary>
        /// Daniel Lemire's fast alternative to a hardware division, the one Dictionary uses on
        /// 64-bit. Valid because the divisor is always one of the primes above.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint FastMod(uint value, uint divisor, ulong multiplier) {
            Debug.Assert(divisor <= Int32.MaxValue);
            return (uint)(((((multiplier * value) >> 32) + 1) * divisor) >> 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int BucketIndex(int hashCode) {
            return (int)FastMod((uint)hashCode, (uint)_buckets.Length, _fastModMultiplier);
        }

        private void Initialize(int capacity) {
            int size = GetPrime(capacity);
            _buckets = new int[size];
            _entries = new Entry[size];
            _fastModMultiplier = UInt64.MaxValue / (uint)size + 1;
            _count = 0;
            _liveCount = 0;
            _start = 0;
        }

        /// <summary>
        /// Makes room for one more entry once <c>_count == _entries.Length</c>: either reclaims the
        /// tombstones in place, when there are enough of them to be worth a pass, or grows.
        /// </summary>
        private void MakeRoom() {
            Debug.Assert(_count == _entries.Length);
            int dead = _count - _liveCount;
            if (dead > _entries.Length / 4) {
                CompactInPlace();
            } else {
                Resize(ExpandPrime(_entries.Length));
            }
            Debug.Assert(_count < _entries.Length);
        }

        /// <summary>
        /// Slides the live entries down over the tombstones, keeping their relative order, and
        /// rebuilds the buckets. Capacity is unchanged, so nothing is allocated.
        /// </summary>
        private void CompactInPlace() {
            Entry[] entries = _entries;
            int count = _count;
            int dst = 0;
            for (int src = _start; src < count; src++) {
                ref Entry e = ref entries[src];
                if (e.Next != TombstoneNext) {
                    if (dst != src) {
                        entries[dst] = e;
                    }
                    dst++;
                }
            }
            Array.Clear(entries, dst, count - dst);
            _count = dst;
            _start = 0;
            Debug.Assert(dst == _liveCount);
            RebuildBuckets();
        }

        private void Resize(int newSize) {
            Entry[] oldEntries = _entries;
            int oldCount = _count;

            var entries = new Entry[newSize];
            int dst = 0;
            if (oldEntries != null) {
                if (_liveCount == oldCount - _start) {
                    // No tombstones to skip, which is the usual case for a hash that is simply
                    // growing: a bulk copy, the same as Dictionary's own Resize does.
                    Array.Copy(oldEntries, _start, entries, 0, _liveCount);
                    dst = _liveCount;
                } else {
                    for (int src = _start; src < oldCount; src++) {
                        if (oldEntries[src].Next != TombstoneNext) {
                            entries[dst++] = oldEntries[src];
                        }
                    }
                }
            }

            _entries = entries;
            _buckets = new int[newSize];
            _fastModMultiplier = UInt64.MaxValue / (uint)newSize + 1;
            _count = dst;
            _start = 0;
            // The bucket array was just allocated, so it is already all zeros: only link.
            LinkBuckets();
        }

        /// <summary>
        /// Re-chains every live entry from the hash codes already stored in it. Does not call the
        /// comparer, so it is safe to use while a Ruby-visible operation is in flight; Hash#rehash,
        /// which does have to re-ask the keys for their hash codes, goes through
        /// <c>IDictionaryOps.Rehash</c> instead.
        /// </summary>
        private void RebuildBuckets() {
            Array.Clear(_buckets, 0, _buckets.Length);
            LinkBuckets();
        }

        /// <summary>
        /// Builds the bucket array of a hash whose entries were laid out without one (see
        /// <c>AppendUnlinked</c>), on the first operation that probes. The entry array is already
        /// prime-sized and the multiplier set, and the array is published only once every entry is
        /// linked, so a reader racing with this sees either no buckets (and links its own, writing
        /// the same chains) or complete ones.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private int[]/*!*/ LinkPendingBuckets() {
            Entry[] entries = _entries;
            var buckets = new int[entries.Length];
            for (int i = _start; i < _count; i++) {
                ref Entry e = ref entries[i];
                if (e.Next != TombstoneNext) {
                    int bucket = (int)FastMod((uint)e.HashCode, (uint)buckets.Length, _fastModMultiplier);
                    e.Next = buckets[bucket] - 1;
                    buckets[bucket] = i + 1;
                }
            }
            System.Threading.Volatile.Write(ref _buckets, buckets);
            return buckets;
        }

        private void LinkBuckets() {
            int[] buckets = _buckets;
            Entry[] entries = _entries;
            for (int i = _start; i < _count; i++) {
                ref Entry e = ref entries[i];
                if (e.Next != TombstoneNext) {
                    int bucket = BucketIndex(e.HashCode);
                    e.Next = buckets[bucket] - 1;
                    buckets[bucket] = i + 1;
                }
            }
        }

        /// <summary>
        /// Grows the entry array so that <paramref name="capacity"/> entries fit without another
        /// reallocation. Mirrors Dictionary.EnsureCapacity.
        /// </summary>
        public int EnsureCapacity(int capacity) {
            if (capacity < 0) {
                throw new ArgumentOutOfRangeException("capacity");
            }
            if (_entries == null) {
                Initialize(capacity);
                return _entries.Length;
            }
            if (_entries.Length - (_count - _liveCount) >= capacity) {
                return _entries.Length;
            }
            Resize(GetPrime(capacity));
            return _entries.Length;
        }

        /// <summary>
        /// Drops the unused capacity, reclaiming any tombstones along the way.
        /// </summary>
        public void TrimExcess() {
            TrimExcess(_liveCount);
        }

        public void TrimExcess(int capacity) {
            if (capacity < _liveCount) {
                throw new ArgumentOutOfRangeException("capacity");
            }
            int newSize = GetPrime(capacity);
            if (_entries == null) {
                Initialize(capacity);
            } else if (newSize < _entries.Length || _count != _liveCount) {
                Resize(newSize);
            }
        }

        #endregion

        #region Lookup

        /// <summary>
        /// Walks the bucket chain and hands back the entry itself, so that reading the value
        /// does not cost a second bounds-checked index into the entry array. It is the shape
        /// Dictionary.FindValue has, for the same reason.
        /// </summary>
        private ref Entry FindEntryRef(object key, out bool found) {
            if (key == null) {
                throw new ArgumentNullException("key");
            }
            Entry[] entries = _entries;
            if (entries != null) {
                IEqualityComparer<object> comparer = _comparer;
                int hashCode = comparer.GetHashCode(key);
                int[] buckets = _buckets ?? LinkPendingBuckets();
                int i = buckets[(int)FastMod((uint)hashCode, (uint)buckets.Length, _fastModMultiplier)] - 1;
                int collisions = 0;
                while ((uint)i < (uint)entries.Length) {
                    ref Entry e = ref entries[i];
                    if (e.HashCode == hashCode && comparer.Equals(e.Key, key)) {
                        found = true;
                        return ref e;
                    }
                    i = e.Next;
                    if (++collisions > entries.Length) {
                        break;
                    }
                }
            }
            found = false;
            return ref _NoEntry[0];
        }

        // The "not found" target of FindEntryRef. Never read, never written.
        private static readonly Entry[]/*!*/ _NoEntry = new Entry[1];

        public bool ContainsKey(object key) {
            bool found;
            FindEntryRef(key, out found);
            return found;
        }

        public bool TryGetValue(object key, out object value) {
            bool found;
            ref Entry e = ref FindEntryRef(key, out found);
            if (found) {
                value = e.Value;
                return true;
            }
            value = null;
            return false;
        }

        public bool ContainsValue(object value) {
            Entry[] entries = _entries;
            int count = _count;
            if (value == null) {
                for (int i = _start; i < count; i++) {
                    ref Entry e = ref entries[i];
                    if (e.Next != TombstoneNext && e.Value == null) {
                        return true;
                    }
                }
                return false;
            }
            var comparer = EqualityComparer<object>.Default;
            for (int i = _start; i < count; i++) {
                ref Entry e = ref entries[i];
                if (e.Next != TombstoneNext && comparer.Equals(e.Value, value)) {
                    return true;
                }
            }
            return false;
        }

        public object this[object key] {
            get {
                bool found;
                ref Entry e = ref FindEntryRef(key, out found);
                if (!found) {
                    throw new KeyNotFoundException();
                }
                return e.Value;
            }
            set {
                Insert(key, value, InsertOverwrite);
            }
        }

        #endregion

        #region Mutation

        private const int InsertOverwrite = 0;
        private const int InsertThrow = 1;
        private const int InsertKeep = 2;

        /// <summary>
        /// Adds the entry, or, when the key is already present, does what
        /// <paramref name="behavior"/> says and returns false. An existing key keeps its position
        /// in the insertion order and keeps its original key object; only the value is replaced -
        /// Ruby requires both.
        /// </summary>
        private bool Insert(object key, object value, int behavior) {
            if (key == null) {
                throw new ArgumentNullException("key");
            }
            if (_buckets == null) {
                if (_entries == null) {
                    Initialize(0);
                } else {
                    LinkPendingBuckets();
                }
            }

            IEqualityComparer<object> comparer = _comparer;
            int hashCode = comparer.GetHashCode(key);

            Entry[] entries = _entries;
            int bucket = BucketIndex(hashCode);
            int i = _buckets[bucket] - 1;
            int collisions = 0;
            while ((uint)i < (uint)entries.Length) {
                ref Entry existing = ref entries[i];
                if (existing.HashCode == hashCode && comparer.Equals(existing.Key, key)) {
                    if (behavior == InsertOverwrite) {
                        existing.Value = value;
                        _stamp++;
                        return false;
                    }
                    if (behavior == InsertThrow) {
                        throw new ArgumentException("An item with the same key has already been added.");
                    }
                    return false;
                }
                i = existing.Next;
                if (++collisions > entries.Length) {
                    break;
                }
            }

            if (_iterationLevel > 0) {
                throw RubyExceptions.CreateRuntimeError("can't add a new key into hash during iteration");
            }

            if (_count == _entries.Length) {
                // MakeRoom reallocates or re-chains, so the bucket has to be found again.
                MakeRoom();
                bucket = BucketIndex(hashCode);
            }

            int index = _count++;
            ref int b = ref _buckets[bucket];
            ref Entry e = ref _entries[index];
            e.HashCode = hashCode;
            e.Next = b - 1;
            e.Key = key;
            e.Value = value;
            b = index + 1;
            _liveCount++;
            _version++;
            _stamp++;
            return true;
        }

        public void Add(object key, object value) {
            Insert(key, value, InsertThrow);
        }

        public bool TryAdd(object key, object value) {
            return Insert(key, value, InsertKeep);
        }

        public bool Remove(object key) {
            object ignored;
            return Remove(key, out ignored);
        }

        public bool Remove(object key, out object value) {
            if (key == null) {
                throw new ArgumentNullException("key");
            }
            if (_buckets == null) {
                if (_entries == null) {
                    value = null;
                    return false;
                }
                LinkPendingBuckets();
            }

            IEqualityComparer<object> comparer = _comparer;
            int hashCode = comparer.GetHashCode(key);

            Entry[] entries = _entries;
            int bucket = BucketIndex(hashCode);
            int last = -1;
            int i = _buckets[bucket] - 1;
            int collisions = 0;
            while ((uint)i < (uint)entries.Length) {
                ref Entry found = ref entries[i];
                if (found.HashCode == hashCode && comparer.Equals(found.Key, key)) {
                    value = found.Value;
                    if (last < 0) {
                        _buckets[BucketIndex(hashCode)] = found.Next + 1;
                    } else {
                        entries[last].Next = found.Next;
                    }
                    found.HashCode = 0;
                    found.Next = TombstoneNext;
                    found.Key = null;
                    found.Value = null;
                    _liveCount--;
                    _version++;
                    _stamp++;
                    // Removing the newest entry needs no tombstone at all; dropping it (and any
                    // tombstones it was sitting on) keeps push/pop patterns from growing the
                    // entry array. Removing the oldest one moves the enumeration start instead,
                    // which is the Hash#shift case.
                    if (i == _count - 1) {
                        int n = i;
                        while (n > _start && entries[n - 1].Next == TombstoneNext) {
                            n--;
                        }
                        _count = n;
                    } else if (i == _start) {
                        int n = i + 1;
                        while (n < _count && entries[n].Next == TombstoneNext) {
                            n++;
                        }
                        _start = n;
                    }
                    if (_liveCount == 0) {
                        // Nothing is linked into a bucket any more, so the whole entry array is
                        // free again: a hash emptied by repeated #delete or #shift starts over
                        // rather than keeping a run of tombstones in front of it forever.
                        _count = 0;
                        _start = 0;
                    } else if (_start > _count) {
                        _start = _count;
                    }
                    return true;
                }
                last = i;
                i = found.Next;
                if (++collisions > entries.Length) {
                    break;
                }
            }
            value = null;
            return false;
        }

        public void Clear() {
            if (_count > 0) {
                if (_buckets != null) {
                    Array.Clear(_buckets, 0, _buckets.Length);
                }
                Array.Clear(_entries, 0, _count);
                _count = 0;
                _liveCount = 0;
                _start = 0;
            }
            _version++;
            _stamp++;
        }

        #endregion

        #region Collection surface

        public int Count {
            get { return _liveCount; }
        }

        public IEqualityComparer<object>/*!*/ Comparer {
            get { return _comparer; }
        }

        public KeyCollection/*!*/ Keys {
            get { return _keys ?? (_keys = new KeyCollection(this)); }
        }

        public ValueCollection/*!*/ Values {
            get { return _values ?? (_values = new ValueCollection(this)); }
        }

        ICollection<object> IDictionary<object, object>.Keys {
            get { return Keys; }
        }

        ICollection<object> IDictionary<object, object>.Values {
            get { return Values; }
        }

        IEnumerable<object> IReadOnlyDictionary<object, object>.Keys {
            get { return Keys; }
        }

        IEnumerable<object> IReadOnlyDictionary<object, object>.Values {
            get { return Values; }
        }

        bool ICollection<KeyValuePair<object, object>>.IsReadOnly {
            get { return false; }
        }

        void ICollection<KeyValuePair<object, object>>.Add(KeyValuePair<object, object> item) {
            Add(item.Key, item.Value);
        }

        bool ICollection<KeyValuePair<object, object>>.Contains(KeyValuePair<object, object> item) {
            bool found;
            ref Entry e = ref FindEntryRef(item.Key, out found);
            return found && EqualityComparer<object>.Default.Equals(e.Value, item.Value);
        }

        bool ICollection<KeyValuePair<object, object>>.Remove(KeyValuePair<object, object> item) {
            bool found;
            ref Entry e = ref FindEntryRef(item.Key, out found);
            if (found && EqualityComparer<object>.Default.Equals(e.Value, item.Value)) {
                return Remove(item.Key);
            }
            return false;
        }

        /// <summary>
        /// Changes with every mutation of the hash, value overwrites included: an iteration that
        /// took a snapshot of the entries can use it for as long as this stays the same.
        /// </summary>
        public int MutationStamp {
            get { return _stamp; }
        }

        /// <summary>Keys and values in insertion order, in one pass over the entries.</summary>
        public void CopyKeysAndValues(object[]/*!*/ keys, object[]/*!*/ values) {
            Entry[] entries = _entries;
            int count = _count;
            int index = 0;
            for (int i = _start; i < count; i++) {
                ref Entry e = ref entries[i];
                if (e.Next != TombstoneNext) {
                    keys[index] = e.Key;
                    values[index++] = e.Value;
                }
            }
        }

        public void CopyTo(KeyValuePair<object, object>[]/*!*/ array, int index) {
            CheckCopyToArgs(array != null ? array.Length : 0, array == null, index);
            Entry[] entries = _entries;
            int count = _count;
            for (int i = _start; i < count; i++) {
                ref Entry e = ref entries[i];
                if (e.Next != TombstoneNext) {
                    array[index++] = new KeyValuePair<object, object>(e.Key, e.Value);
                }
            }
        }

        private void CheckCopyToArgs(int arrayLength, bool isNull, int index) {
            if (isNull) {
                throw new ArgumentNullException("array");
            }
            if ((uint)index > (uint)arrayLength) {
                throw new ArgumentOutOfRangeException("index");
            }
            if (arrayLength - index < _liveCount) {
                throw new ArgumentException("Destination array is not long enough.");
            }
        }

        #endregion

        #region Non-generic IDictionary

        // Dictionary<object, object> implemented the non-generic interfaces too, and code that
        // sees a Ruby Hash as a plain IDictionary (the YAML representer, DLR interop) relies on it.

        bool IDictionary.IsFixedSize {
            get { return false; }
        }

        bool IDictionary.IsReadOnly {
            get { return false; }
        }

        ICollection IDictionary.Keys {
            get { return Keys; }
        }

        ICollection IDictionary.Values {
            get { return Values; }
        }

        object IDictionary.this[object key] {
            get {
                if (key == null) {
                    return null;
                }
                bool found;
                ref Entry e = ref FindEntryRef(key, out found);
                return found ? e.Value : null;
            }
            set { this[key] = value; }
        }

        void IDictionary.Add(object key, object value) {
            Add(key, value);
        }

        bool IDictionary.Contains(object key) {
            return key != null && ContainsKey(key);
        }

        void IDictionary.Remove(object key) {
            if (key != null) {
                Remove(key);
            }
        }

        IDictionaryEnumerator IDictionary.GetEnumerator() {
            return new Enumerator(this, Enumerator.DictEntry);
        }

        bool ICollection.IsSynchronized {
            get { return false; }
        }

        object ICollection.SyncRoot {
            get { return this; }
        }

        void ICollection.CopyTo(Array/*!*/ array, int index) {
            if (array == null) {
                throw new ArgumentNullException("array");
            }
            var pairs = array as KeyValuePair<object, object>[];
            if (pairs != null) {
                CopyTo(pairs, index);
                return;
            }
            CheckCopyToArgs(array.Length, false, index);
            var entriesArray = array as DictionaryEntry[];
            Entry[] entries = _entries;
            int count = _count;
            if (entriesArray != null) {
                for (int i = _start; i < count; i++) {
                    ref Entry e = ref entries[i];
                    if (e.Next != TombstoneNext) {
                        entriesArray[index++] = new DictionaryEntry(e.Key, e.Value);
                    }
                }
                return;
            }
            var objects = array as object[];
            if (objects == null) {
                throw new ArgumentException("Invalid array type.");
            }
            for (int i = _start; i < count; i++) {
                ref Entry e = ref entries[i];
                if (e.Next != TombstoneNext) {
                    objects[index++] = new KeyValuePair<object, object>(e.Key, e.Value);
                }
            }
        }

        #endregion

        #region Enumeration

        public Enumerator GetEnumerator() {
            return new Enumerator(this, Enumerator.KeyValuePair);
        }

        IEnumerator<KeyValuePair<object, object>> IEnumerable<KeyValuePair<object, object>>.GetEnumerator() {
            return new Enumerator(this, Enumerator.KeyValuePair);
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return new Enumerator(this, Enumerator.KeyValuePair);
        }

        /// <summary>
        /// Walks the entries in insertion order, skipping tombstones. A struct, like
        /// Dictionary's, so that <c>foreach</c> over a Hash still allocates nothing.
        /// </summary>
        public struct Enumerator : IEnumerator<KeyValuePair<object, object>>, IDictionaryEnumerator {
            internal const int KeyValuePair = 1;
            internal const int DictEntry = 2;

            private readonly Hash/*!*/ _hash;
            private readonly int _version;
            private readonly int _getEnumeratorRetType;
            private int _index;
            private KeyValuePair<object, object> _current;

            internal Enumerator(Hash/*!*/ hash, int getEnumeratorRetType) {
                _hash = hash;
                _version = hash._version;
                _getEnumeratorRetType = getEnumeratorRetType;
                _index = hash._start;
                _current = default(KeyValuePair<object, object>);
            }

            public bool MoveNext() {
                if (_version != _hash._version) {
                    throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                }
                Entry[] entries = _hash._entries;
                while (_index < _hash._count) {
                    int i = _index++;
                    ref Entry e = ref entries[i];
                    if (e.Next != TombstoneNext) {
                        _current = new KeyValuePair<object, object>(e.Key, e.Value);
                        return true;
                    }
                }
                _current = default(KeyValuePair<object, object>);
                return false;
            }

            public KeyValuePair<object, object> Current {
                get { return _current; }
            }

            public void Dispose() {
            }

            object IEnumerator.Current {
                get {
                    CheckState();
                    if (_getEnumeratorRetType == DictEntry) {
                        return new DictionaryEntry(_current.Key, _current.Value);
                    }
                    return _current;
                }
            }

            void IEnumerator.Reset() {
                if (_version != _hash._version) {
                    throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                }
                _index = _hash._start;
                _current = default(KeyValuePair<object, object>);
            }

            private void CheckState() {
                if (_index == 0 || _current.Key == null && _index > _hash._count) {
                    throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                }
            }

            DictionaryEntry IDictionaryEnumerator.Entry {
                get {
                    CheckState();
                    return new DictionaryEntry(_current.Key, _current.Value);
                }
            }

            object IDictionaryEnumerator.Key {
                get {
                    CheckState();
                    return _current.Key;
                }
            }

            object IDictionaryEnumerator.Value {
                get {
                    CheckState();
                    return _current.Value;
                }
            }
        }

        public sealed class KeyCollection : ICollection<object>, ICollection, IReadOnlyCollection<object> {
            private readonly Hash/*!*/ _hash;

            internal KeyCollection(Hash/*!*/ hash) {
                _hash = hash;
            }

            public int Count {
                get { return _hash._liveCount; }
            }

            bool ICollection<object>.IsReadOnly {
                get { return true; }
            }

            void ICollection<object>.Add(object item) {
                throw new NotSupportedException();
            }

            void ICollection<object>.Clear() {
                throw new NotSupportedException();
            }

            bool ICollection<object>.Remove(object item) {
                throw new NotSupportedException();
            }

            public bool Contains(object item) {
                return _hash.ContainsKey(item);
            }

            public void CopyTo(object[]/*!*/ array, int index) {
                _hash.CheckCopyToArgs(array != null ? array.Length : 0, array == null, index);
                Entry[] entries = _hash._entries;
                int count = _hash._count;
                for (int i = _hash._start; i < count; i++) {
                    ref Entry e = ref entries[i];
                    if (e.Next != TombstoneNext) {
                        array[index++] = e.Key;
                    }
                }
            }

            void ICollection.CopyTo(Array/*!*/ array, int index) {
                var objects = array as object[];
                if (objects != null) {
                    CopyTo(objects, index);
                    return;
                }
                _hash.CheckCopyToArgs(array != null ? array.Length : 0, array == null, index);
                Entry[] entries = _hash._entries;
                int count = _hash._count;
                for (int i = _hash._start; i < count; i++) {
                    ref Entry e = ref entries[i];
                    if (e.Next != TombstoneNext) {
                        array.SetValue(e.Key, index++);
                    }
                }
            }

            bool ICollection.IsSynchronized {
                get { return false; }
            }

            object ICollection.SyncRoot {
                get { return _hash; }
            }

            public Enumerator GetEnumerator() {
                return new Enumerator(_hash);
            }

            IEnumerator<object> IEnumerable<object>.GetEnumerator() {
                return new Enumerator(_hash);
            }

            IEnumerator IEnumerable.GetEnumerator() {
                return new Enumerator(_hash);
            }

            public struct Enumerator : IEnumerator<object> {
                private readonly Hash/*!*/ _hash;
                private readonly int _version;
                private int _index;
                private object _current;

                internal Enumerator(Hash/*!*/ hash) {
                    _hash = hash;
                    _version = hash._version;
                    _index = hash._start;
                    _current = null;
                }

                public bool MoveNext() {
                    if (_version != _hash._version) {
                        throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                    }
                    Entry[] entries = _hash._entries;
                    while (_index < _hash._count) {
                        int i = _index++;
                        ref Entry e = ref entries[i];
                        if (e.Next != TombstoneNext) {
                            _current = e.Key;
                            return true;
                        }
                    }
                    _current = null;
                    return false;
                }

                public object Current {
                    get { return _current; }
                }

                public void Dispose() {
                }

                void IEnumerator.Reset() {
                    _index = _hash._start;
                    _current = null;
                }
            }
        }

        public sealed class ValueCollection : ICollection<object>, ICollection, IReadOnlyCollection<object> {
            private readonly Hash/*!*/ _hash;

            internal ValueCollection(Hash/*!*/ hash) {
                _hash = hash;
            }

            public int Count {
                get { return _hash._liveCount; }
            }

            bool ICollection<object>.IsReadOnly {
                get { return true; }
            }

            void ICollection<object>.Add(object item) {
                throw new NotSupportedException();
            }

            void ICollection<object>.Clear() {
                throw new NotSupportedException();
            }

            bool ICollection<object>.Remove(object item) {
                throw new NotSupportedException();
            }

            public bool Contains(object item) {
                return _hash.ContainsValue(item);
            }

            public void CopyTo(object[]/*!*/ array, int index) {
                _hash.CheckCopyToArgs(array != null ? array.Length : 0, array == null, index);
                Entry[] entries = _hash._entries;
                int count = _hash._count;
                for (int i = _hash._start; i < count; i++) {
                    ref Entry e = ref entries[i];
                    if (e.Next != TombstoneNext) {
                        array[index++] = e.Value;
                    }
                }
            }

            void ICollection.CopyTo(Array/*!*/ array, int index) {
                var objects = array as object[];
                if (objects != null) {
                    CopyTo(objects, index);
                    return;
                }
                _hash.CheckCopyToArgs(array != null ? array.Length : 0, array == null, index);
                Entry[] entries = _hash._entries;
                int count = _hash._count;
                for (int i = _hash._start; i < count; i++) {
                    ref Entry e = ref entries[i];
                    if (e.Next != TombstoneNext) {
                        array.SetValue(e.Value, index++);
                    }
                }
            }

            bool ICollection.IsSynchronized {
                get { return false; }
            }

            object ICollection.SyncRoot {
                get { return _hash; }
            }

            public Enumerator GetEnumerator() {
                return new Enumerator(_hash);
            }

            IEnumerator<object> IEnumerable<object>.GetEnumerator() {
                return new Enumerator(_hash);
            }

            IEnumerator IEnumerable.GetEnumerator() {
                return new Enumerator(_hash);
            }

            public struct Enumerator : IEnumerator<object> {
                private readonly Hash/*!*/ _hash;
                private readonly int _version;
                private int _index;
                private object _current;

                internal Enumerator(Hash/*!*/ hash) {
                    _hash = hash;
                    _version = hash._version;
                    _index = hash._start;
                    _current = null;
                }

                public bool MoveNext() {
                    if (_version != _hash._version) {
                        throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                    }
                    Entry[] entries = _hash._entries;
                    while (_index < _hash._count) {
                        int i = _index++;
                        ref Entry e = ref entries[i];
                        if (e.Next != TombstoneNext) {
                            _current = e.Value;
                            return true;
                        }
                    }
                    _current = null;
                    return false;
                }

                public object Current {
                    get { return _current; }
                }

                public void Dispose() {
                }

                void IEnumerator.Reset() {
                    _index = _hash._start;
                    _current = null;
                }
            }
        }

        #endregion
    }
}

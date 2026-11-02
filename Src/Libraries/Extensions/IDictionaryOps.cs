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
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Utils;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using Microsoft.Scripting;

namespace IronRuby.Builtins {

    // TODO: IDictionary<TKey, TValue> instead of IDictionary<object, object>?
    //       (need support for extension methods on generic interfaces first)
    //       (IDictionary isn't a good solution because it doesn't have TryGetValue)
    [RubyModule(Extends = typeof(IDictionary<object, object>), Restrictions = ModuleRestrictions.None)]
    [Includes(typeof(Enumerable))]
    public static class IDictionaryOps {

        #region Helper methods

        // Make a 2 element array
        internal static RubyArray/*!*/ MakeArray(KeyValuePair<object, object> pair) {
            RubyArray list = new RubyArray(2);
            list.Add(CustomStringDictionary.ObjToNull(pair.Key));
            list.Add(pair.Value);
            return list;
        }

        internal static RubyArray/*!*/ MakeArray(object key, object value) {
            RubyArray list = new RubyArray(2);
            list.Add(CustomStringDictionary.ObjToNull(key));
            list.Add(value);
            return list;
        }

        // Replaces the data in dest with the data from src
        internal static T ReplaceData<T>(T dest, IEnumerable<KeyValuePair<object, object>> src) where T : IDictionary<object, object> {
            dest.Clear();
            foreach (KeyValuePair<object, object> pair in src) {
                dest[pair.Key] = pair.Value;
            }
            return dest;
        }

        // Copies key,value pairs into an array. Needed for iteration over the hash while mutating it.
        private static KeyValuePair<object, object>[]/*!*/ CopyKeyValuePairs(IDictionary<object, object>/*!*/ dict) {
            KeyValuePair<object, object>[] pairs = new KeyValuePair<object, object>[dict.Count];
            dict.CopyTo(pairs, 0);
            return pairs;
        }

        #endregion

        [RubyMethod("==")]
        public static bool Equals(RespondToStorage/*!*/ respondTo, BinaryOpStorage/*!*/ equals, IDictionary<object, object>/*!*/ self, object other) {
            return Protocols.RespondTo(respondTo, other, "to_hash") && Protocols.IsEqual(equals, other, self);
        }

        [MultiRuntimeAware]
        private static RubyUtils.RecursionTracker _EqualsTracker = new RubyUtils.RecursionTracker();

        [RubyMethod("==")]
        public static bool Equals(BinaryOpStorage/*!*/ equals, IDictionary<object, object>/*!*/ self, [NotNull]IDictionary<object, object>/*!*/ other) {
            Assert.NotNull(self, other);

            if (ReferenceEquals(self, other)) {
                return true;
            }

            if (self.Count != other.Count) {
                return false;
            }

            // Two non-empty hashes that differ in their compare_by_identity flag are never equal
            // (empty ones still are) - see rb_hash_equal.
            if (self.Count != 0) {
                var selfHash = self as Hash;
                var otherHash = other as Hash;
                if (selfHash != null && otherHash != null && selfHash.ComparesByIdentity != otherHash.ComparesByIdentity) {
                    return false;
                }
            }

            using (IDisposable handleSelf = _EqualsTracker.TrackObject(self), handleOther = _EqualsTracker.TrackObject(other)) {
                if (handleSelf == null && handleOther == null) {
                    // both dictionaries went recursive:
                    return true;
                }

                // Each key value pair must be the same
                var site = equals.GetCallSite("==");
                foreach (KeyValuePair<object, object> pair in self) {
                    object value;
                    if (!other.TryGetValue(pair.Key, out value) || !Protocols.IsEqual(site, pair.Value, value)) {
                        return false;
                    }
                }
            }

            return true;
        }

        [RubyMethod("[]")]
        public static object GetElement(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ self, object key) {
            object result;
            if (!self.TryGetValue(CustomStringDictionary.NullToObj(key), out result)) {
                return null;
            }
            return result;
        }

        // TODO: remove, frozen check should be implemented in Hash indexer
        [RubyMethod("[]=")]
        [RubyMethod("store")]
        public static object SetElement(RubyContext/*!*/ context, Hash/*!*/ self, object key, object value) {
            self.RequireNotFrozen();
            return RubyUtils.SetHashElement(context, self, key, value);
        }

        [RubyMethod("[]=")]
        [RubyMethod("store")]
        public static object SetElement(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ self, object key, object value) {
            return RubyUtils.SetHashElement(context, self, key, value);
        }

        // TODO: remove, frozen check should be implemented in Hash.Clear
        [RubyMethod("clear")]
        public static IDictionary<object, object>/*!*/ Clear(Hash/*!*/ self) {
            self.RequireNotFrozen();
            self.Clear();
            return self;
        }

        [RubyMethod("clear")]
        public static IDictionary<object, object>/*!*/ Clear(IDictionary<object, object>/*!*/ self) {
            self.Clear();
            return self;
        }

        // We don't define "dup" here because "dup" shouldn't show up on builtin types like Hash
        // (Kernel#dup just works for these types)
        private static IDictionary<object, object>/*!*/ Duplicate(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            IDictionary<object, object>/*!*/ self) {

            // Call Kernel#dup, then copy items
            var copy = (IDictionary<object, object>)KernelOps.Duplicate(initializeCopyStorage, allocateStorage, self);
            return ReplaceData(copy, self);
        }

        [RubyMethod("default")]
        public static object GetDefaultValue(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ self, [Optional]object key) {
            return null;
        }

        [RubyMethod("default_proc")]
        public static Proc GetDefaultProc(IDictionary<object, object>/*!*/ self) {
            return null;
        }

        #region delete, delete_if

        [RubyMethod("delete")]
        public static object Delete(BlockParam block, Hash/*!*/ self, object key) {
            self.RequireNotFrozen();
            return Delete(block, (IDictionary<object, object>)self, key);
        }

        [RubyMethod("delete")]
        public static object Delete(BlockParam block, IDictionary<object, object>/*!*/ self, object key) {
            object value;
            if (!self.TryGetValue(CustomStringDictionary.NullToObj(key), out value)) {
                // key not found, call the block if it was passed in
                if (block != null) {
                    object result;
                    block.Yield(key, out result);
                    return result;
                }
                return null;
            }
            self.Remove(CustomStringDictionary.NullToObj(key));
            return value;
        }

        [RubyMethod("delete_if")]
        public static object DeleteIf(BlockParam block, Hash/*!*/ self) {
            self.RequireNotFrozen();
            return DeleteIf(block, (IDictionary<object, object>)self);
        }

        [RubyMethod("delete_if")]
        public static object DeleteIf(BlockParam block, IDictionary<object, object>/*!*/ self) {
            if (self.Count > 0 && block == null) {
                throw RubyExceptions.NoBlockGiven();
            }

            // Make a copy of the keys to delete, so we don't modify the collection
            // while iterating over it
            RubyArray keysToDelete = new RubyArray();

            Hash hash = self as Hash;
            if (hash != null) {
                hash.BeginIteration();
            }
            try {
                // over a snapshot: the block may delete from the hash (it may not add to it)
                foreach (var pair in CopyKeyValuePairs(self)) {
                    object result;
                    if (block.Yield(CustomStringDictionary.ObjToNull(pair.Key), pair.Value, out result)) {
                        return result;
                    }

                    // Delete the key, unless 'false' or 'nil' is returned
                    if (RubyOps.IsTrue(result)) {
                        keysToDelete.Add(pair.Key);
                    }
                }
            } finally {
                if (hash != null) {
                    hash.EndIteration();
                }
            }

            foreach (object key in keysToDelete) {
                self.Remove(key);
            }

            return self;
        }

        #endregion

        #region each, each_pair, each_key, each_value

        /// <summary>
        /// The value an entry of the snapshot has now. A key that is not found was deleted by the
        /// block - or it is a key whose hash has drifted since it was stored, a Hash that is its
        /// own key being the usual case; MRI still visits such an entry, since it walks the table.
        /// The block may not add entries, so the count can only have dropped if something was
        /// deleted: while it has not, the entry is still there and its snapshot value stands.
        /// </summary>
        private static bool TryGetCurrentValue(IDictionary<object, object>/*!*/ self, KeyValuePair<object, object>[]/*!*/ snapshot, int index, out object value) {
            if (self.TryGetValue(snapshot[index].Key, out value)) {
                return true;
            }
            if (self.Count < snapshot.Length) {
                return false;
            }
            value = snapshot[index].Value;
            return true;
        }

        [RubyMethod("each")]
        [RubyMethod("each_pair")]
        public static Enumerator/*!*/ Each(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ self) {
            return new Enumerator(self, "each");
        }

        [RubyMethod("each")]
        [RubyMethod("each_pair")]
        public static object Each(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, IDictionary<object, object>/*!*/ self) {
            if (self.Count > 0) {
                bool yieldPair = IsPairYieldOptimizable(block);

                // A Ruby Hash: keys and values come out of one pass over its entries, and each value
                // is used as is while the block leaves the hash alone; once it mutates it, the rest is
                // looked up as below. That is the same answer without hashing every key a second
                // time - which, for a key of a Ruby class, was a #hash and an #eql? call per entry.
                // Adding a key is what MRI forbids while a hash is being iterated, and the Hash
                // itself raises for that between BeginIteration and EndIteration.
                var hash = self as Hash;
                if (hash != null) {
                    int count = hash.Count;
                    object[] hashKeys = new object[count];
                    object[] hashValues = new object[count];
                    hash.CopyKeysAndValues(hashKeys, hashValues);
                    int stamp = hash.MutationStamp;
                    hash.BeginIteration();
                    try {
                        for (int i = 0; i < count; i++) {
                            object key = hashKeys[i];
                            object value;
                            if (hash.MutationStamp == stamp) {
                                value = hashValues[i];
                            } else if (!hash.TryGetValue(key, out value)) {
                                if (hash.Count < count) {
                                    // deleted by the block before its turn came: MRI skips it
                                    continue;
                                }
                                // a key whose hash has drifted since it was stored, a Hash that is
                                // its own key being the usual case: MRI walks the table and still
                                // visits it
                                value = hashValues[i];
                            }
                            object result;
                            if (yieldPair
                                ? block.Yield(CustomStringDictionary.ObjToNull(key), value, out result)
                                : block.Yield(MakeArray(key, value), out result)) {
                                return result;
                            }
                        }
                    } finally {
                        hash.EndIteration();
                    }
                    return self;
                }

                // Any other IDictionary: the block may change it, so iterate over a copy of the
                // entries and look each value up when it is its turn: a value the block replaced is
                // seen replaced, and a key the block deleted is skipped, as in MRI.
                var pairs = CopyKeyValuePairs(self);
                for (int i = 0; i < pairs.Length; i++) {
                    object value;
                    if (!TryGetCurrentValue(self, pairs, i, out value)) {
                        continue;
                    }
                    object result;
                    if (yieldPair
                        ? block.Yield(CustomStringDictionary.ObjToNull(pairs[i].Key), value, out result)
                        : block.Yield(MakeArray(pairs[i].Key, value), out result)) {
                        return result;
                    }
                }
            }

            return self;
        }

        /// <summary>
        /// MRI's rb_block_pair_yield_optimizable: a non-lambda block that needs at least two
        /// arguments gets key and value as two values rather than as one pair. For a Ruby block
        /// that is the same thing (the pair would be splatted), but it lets Enumerable#map hand a
        /// Hash's key and value on to a two-argument Method: {1 => 2}.map(&method(:m)).
        /// </summary>
        private static bool IsPairYieldOptimizable(BlockParam/*!*/ block) {
            var proc = block.Proc;
            if (proc.Kind == ProcKind.Lambda || proc.Dispatcher.ParameterSignature != null) {
                return false;
            }
            int arity = proc.Dispatcher.Arity;
            return (arity >= 0 ? arity : -arity - 1) > 1;
        }
        
        [RubyMethod("each_key")]
        public static Enumerator/*!*/ EachKey(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ self) {
            return new Enumerator(self, "each_key");
        }

        [RubyMethod("each_key")]
        public static object EachKey(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, IDictionary<object, object>/*!*/ self) {
            if (self.Count > 0) {
                // See Each: a copy of the entries, deleted ones skipped, no new ones allowed.
                var pairs = CopyKeyValuePairs(self);

                Hash hash = self as Hash;
                if (hash != null) {
                    hash.BeginIteration();
                }
                try {
                    for (int i = 0; i < pairs.Length; i++) {
                        object value;
                        if (!TryGetCurrentValue(self, pairs, i, out value)) {
                            continue;
                        }
                        object result;
                        if (block.Yield(CustomStringDictionary.ObjToNull(pairs[i].Key), out result)) {
                            return result;
                        }
                    }
                } finally {
                    if (hash != null) {
                        hash.EndIteration();
                    }
                }
            }

            return self;
        }

        [RubyMethod("each_value")]
        public static Enumerator/*!*/ EachValue(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ self) {
            return new Enumerator(self, "each_value");
        }

        [RubyMethod("each_value")]
        public static object EachValue(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, IDictionary<object, object>/*!*/ self) {
            if (self.Count > 0) {
                // See Each: a copy of the entries, deleted ones skipped, no new ones allowed.
                var pairs = CopyKeyValuePairs(self);

                Hash hash = self as Hash;
                if (hash != null) {
                    hash.BeginIteration();
                }
                try {
                    for (int i = 0; i < pairs.Length; i++) {
                        object value;
                        if (!TryGetCurrentValue(self, pairs, i, out value)) {
                            continue;
                        }
                        object result;
                        if (block.Yield(value, out result)) {
                            return result;
                        }
                    }
                } finally {
                    if (hash != null) {
                        hash.EndIteration();
                    }
                }
            }

            return self;
        }

        #endregion

        [RubyMethod("empty?")]
        public static bool Empty(IDictionary<object, object>/*!*/ self) {
            return self.Count == 0;
        }

        [RubyMethod("fetch")]
        public static object Fetch(RubyContext/*!*/ context, BlockParam block, IDictionary<object, object>/*!*/ self, object key, [Optional]object defaultValue) {
            // MRI warns whenever both are given, even if the key is present
            if (block != null && defaultValue != Missing.Value) {
                context.ReportWarning("block supersedes default value argument");
            }

            object result;
            if (self.TryGetValue(CustomStringDictionary.NullToObj(key), out result)) {
                return result;
            }

            if (block != null) {
                block.Yield(key, out result);
                return result;
            }

            if (defaultValue == Missing.Value) {
                throw RubyExceptions.CreateIndexError("key not found");
            }

            return defaultValue;
        }

        [RubyMethod("has_key?")]
        [RubyMethod("include?")]
        [RubyMethod("key?")]
        [RubyMethod("member?")]
        public static bool HasKey(IDictionary<object, object>/*!*/ self, object key) {
            return self.ContainsKey(CustomStringDictionary.NullToObj(key));
        }

        [RubyMethod("has_value?")]
        [RubyMethod("value?")]
        public static bool HasValue(BinaryOpStorage/*!*/ equals, IDictionary<object, object>/*!*/ self, object value) {
            foreach (KeyValuePair<object, object> pair in self) {
                if (Protocols.IsEqual(equals, pair.Value, value)) {
                    return true;
                }
            }
            return false;
        }

        [RubyMethod("invert")]
        public static Hash/*!*/ Invert(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ self) {
            // invert returns a Hash, even from subclasses
            Hash hash = new Hash(context.EqualityComparer, self.Count);
            foreach (KeyValuePair<object, object> pair in self) {
                hash[CustomStringDictionary.NullToObj(pair.Value)] = CustomStringDictionary.ObjToNull(pair.Key);
            }
            return hash;
        }

        [RubyMethod("keys")]
        public static RubyArray/*!*/ GetKeys(IDictionary<object, object>/*!*/ self) {
            RubyArray keys = new RubyArray(self.Count);
            foreach (object key in self.Keys) {
                keys.Add(CustomStringDictionary.ObjToNull(key));
            }
            return keys;
        }

        [RubyMethod("length")]
        [RubyMethod("size")]
        public static int Length(IDictionary<object, object>/*!*/ self) {
            return self.Count;
        }

        [RubyMethod("merge")]
        public static object Merge(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            BlockParam block, IDictionary<object, object>/*!*/ self, 
            [DefaultProtocol, NotNull]IDictionary<object, object>/*!*/ hash) {

            return Update(block, Duplicate(initializeCopyStorage, allocateStorage, self), hash);
        }

        [RubyMethod("merge!")]
        [RubyMethod("update")]
        public static object Update(BlockParam block, Hash/*!*/ self, [DefaultProtocol, NotNull]IDictionary<object, object>/*!*/ hash) {
            self.RequireNotFrozen();
            return Update(block, (IDictionary<object, object>)self, hash);
        }

        [RubyMethod("merge!")]
        [RubyMethod("update")]
        public static object Update(BlockParam block, IDictionary<object, object>/*!*/ self, [DefaultProtocol, NotNull]IDictionary<object, object>/*!*/ hash) {
            if (block == null) {
                foreach (var pair in CopyKeyValuePairs(hash)) {
                    self[CustomStringDictionary.NullToObj(pair.Key)] = pair.Value;
                }
            } else {
                foreach (var pair in CopyKeyValuePairs(hash)) {
                    object key = pair.Key, newValue = pair.Value, oldValue;
                    if (self.TryGetValue(key, out oldValue)) {
                        if (block.Yield(CustomStringDictionary.ObjToNull(key), oldValue, pair.Value, out newValue)) {
                            return newValue;
                        }
                    }
                    self[key] = newValue;
                }
            }
            return self;
        }

        [RubyMethod("rehash")]
        public static IDictionary<object, object> Rehash(Hash/*!*/ self) {
            self.RequireNotFrozen();
            if (self.IsIterating) {
                throw RubyExceptions.CreateRuntimeError("rehash during iteration");
            }
            return Rehash((IDictionary<object, object>)self);
        }

        [RubyMethod("rehash")]
        public static IDictionary<object, object> Rehash(IDictionary<object, object>/*!*/ self) {
            return ReplaceData(self, CopyKeyValuePairs(self));
        }

        // This works like delete_if, not reject!
        // (because it needs to return the new collection)
        [RubyMethod("reject")]
        public static object Reject(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            BlockParam block, IDictionary<object, object>/*!*/ self) {

            return DeleteIf(block, Duplicate(initializeCopyStorage, allocateStorage, self));
        }

        // This works like delete_if, but returns nil if no elements were removed
        [RubyMethod("reject!")]
        public static object RejectMutate(BlockParam block, Hash/*!*/ self) {
            self.RequireNotFrozen();
            return RejectMutate(block, (IDictionary<object, object>)self);
        }

        [RubyMethod("reject!")]
        public static object RejectMutate(BlockParam block, IDictionary<object, object>/*!*/ self) {

            // Make a copy of the keys to delete, so we don't modify the collection
            // while iterating over it
            RubyArray keysToDelete = new RubyArray();

            Hash hash = self as Hash;
            if (hash != null) {
                hash.BeginIteration();
            }
            try {
                foreach (KeyValuePair<object,object> pair in CopyKeyValuePairs(self)) {
                    object result;
                    if (block.Yield(CustomStringDictionary.ObjToNull(pair.Key), pair.Value, out result)) {
                        return result;
                    }

                    // Delete the key, unless 'false' or 'nil' is returned
                    if (RubyOps.IsTrue(result)) {
                        keysToDelete.Add(pair.Key);
                    }
                }
            } finally {
                if (hash != null) {
                    hash.EndIteration();
                }
            }

            foreach (object key in keysToDelete) {
                self.Remove(key);
            }

            return keysToDelete.Count == 0 ? null : self;
        }

        [RubyMethod("replace")]
        public static Hash/*!*/ Replace(RubyContext/*!*/ context, Hash/*!*/ self, [DefaultProtocol, NotNull]IDictionary<object, object>/*!*/ other) {
            self.RequireNotFrozen();
            if (self.IsIterating) {
                throw RubyExceptions.CreateRuntimeError("can't replace hash during iteration");
            }
            return IDictionaryOps.ReplaceData(self, other);
        }

        [RubyMethod("select")]
        public static Enumerator/*!*/ Select(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ self) {
            return new Enumerator(self, "select");
        }

        [RubyMethod("select")]
        public static object Select(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, IDictionary<object, object>/*!*/ self) {
            Hash result = new Hash(context);
            var selfHash = self as Hash;
            if (selfHash != null && selfHash.ComparesByIdentity) {
                result.CompareByIdentity();
            }

            foreach (var pair in CopyKeyValuePairs(self)) {
                object blockResult;
                if (block.Yield(CustomStringDictionary.ObjToNull(pair.Key), pair.Value, out blockResult)) {
                    return blockResult;
                }

                if (RubyOps.IsTrue(blockResult)) {
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        [RubyMethod("shift")]
        public static object Shift(Hash/*!*/ self) {
            self.RequireNotFrozen();
            return Shift((IDictionary<object, object>)self);
        }

        [RubyMethod("shift")]
        public static object Shift(IDictionary<object, object>/*!*/ self) {
            if (self.Count == 0) {
                return null;
            }

            IEnumerator<KeyValuePair<object, object>> e = self.GetEnumerator();
            e.MoveNext();
            KeyValuePair<object, object> pair = e.Current;
            self.Remove(pair.Key);

            return MakeArray(pair);
        }

        [RubyMethod("sort")]
        public static object Sort(ComparisonStorage/*!*/ comparisonStorage, BlockParam block, IDictionary<object, object>/*!*/ self) {
            return ArrayOps.SortInPlace(comparisonStorage, block, ToArray(self));
        }

        [RubyMethod("to_a")]
        public static RubyArray/*!*/ ToArray(IDictionary<object, object>/*!*/ self) {
            RubyArray result = new RubyArray(self.Count);
            foreach (KeyValuePair<object, object> pair in self) {
                result.Add(MakeArray(pair));
            }
            return result;
        }

        [RubyMethod("flatten")]
        public static IList/*!*/ Flatten(ConversionStorage<IList>/*!*/ tryToAry, IDictionary<object, object>/*!*/ self,
            [DefaultProtocol, DefaultParameterValue(1)] int maxDepth) {

            if (maxDepth == 0) {
                return ToArray(self);
            } 
            
            if (maxDepth > 0) {
                maxDepth--;
            }

            RubyArray result = new RubyArray();
            IList list;
            foreach (KeyValuePair<object, object> pair in self) {
                if (maxDepth != 0 && (list = Protocols.TryCastToArray(tryToAry, pair.Key)) != null) {
                    IListOps.Flatten(tryToAry, list, maxDepth - 1, result);
                } else {
                    result.Add(pair.Key);
                }

                if (maxDepth != 0 && (list = Protocols.TryCastToArray(tryToAry, pair.Value)) != null) {
                    IListOps.Flatten(tryToAry, list, maxDepth - 1, result);
                } else {
                    result.Add(pair.Value);
                }
            }
            return result;
        }

        [RubyMethod("to_hash")]
        public static IDictionary<object, object> ToHash(IDictionary<object, object>/*!*/ self) {
            return self;
        }

        [RubyMethod("to_s")]
        [RubyMethod("inspect")]
        public static MutableString/*!*/ ToMutableString(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ self) {

            using (IDisposable handle = RubyUtils.InfiniteInspectTracker.TrackObject(self)) {
                if (handle == null) {
                    return MutableString.CreateAscii("{...}");
                }

                MutableString str = MutableString.CreateMutable(RubyEncoding.Binary);
                str.Append('{');
                bool first = true;
                foreach (var entry in self) {
                    if (first) {
                        first = false;
                    } else {
                        str.Append(", ");
                    }
                    str.Append(context.Inspect(CustomStringDictionary.ObjToNull(entry.Key)));
                    str.Append("=>");
                    str.Append(context.Inspect(entry.Value));
                }
                str.Append('}');
                return str;
            }
        }
        
        [RubyMethod("values")]
        public static RubyArray/*!*/ GetValues(IDictionary<object, object>/*!*/ self) {
            return new RubyArray(self.Values);
        }

        [RubyMethod("values_at")]
        public static RubyArray/*!*/ ValuesAt(BinaryOpStorage/*!*/ storage, IDictionary<object, object>/*!*/ self, params object[]/*!*/ keys) {
            RubyArray values = new RubyArray();
            foreach (object key in keys) {
                // A missing key answers the default, as #[] does: Hash.new(5).values_at(:x) is [5].
                values.Add(HashOps.GetElement(storage, self, key));
            }
            return values;
        }
    }

}

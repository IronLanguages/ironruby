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
using Microsoft.Scripting.Utils;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Conversions;
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
            // Hash#dup, #merge, #replace, Hash[hash]: copy the entries with the hash codes they
            // already have instead of re-inserting, and so re-hashing, every key.
            var destHash = dest as Hash;
            var srcHash = src as Hash;
            if (destHash != null && srcHash != null && destHash.TryCopyEntriesFrom(srcHash)) {
                return dest;
            }

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

        // The Hash overload exists for the frozen check: Hash's indexer does not make it (see Hash).
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

        // The Hash overload exists for the frozen check: Hash.Clear does not make it (see Hash).
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

        // delete_if, keep_if, reject!, select! and filter! answer an Enumerator without a block - before the
        // frozen check, as MRI's RETURN_SIZED_ENUMERATOR comes first - and with one remove the pairs the block
        // condemns: answers true for delete_if and reject!, false for keep_if, select! and filter!.

        [RubyMethod("delete_if")]
        public static object DeleteIf(BlockParam block, IDictionary<object, object>/*!*/ self) {
            if (block == null) {
                return new Enumerator(self, "delete_if");
            }
            object breakResult;
            return RemoveWhere(block, self, true, out breakResult) >= 0 ? self : breakResult;
        }

        [RubyMethod("keep_if")]
        public static object KeepIf(BlockParam block, IDictionary<object, object>/*!*/ self) {
            if (block == null) {
                return new Enumerator(self, "keep_if");
            }
            object breakResult;
            return RemoveWhere(block, self, false, out breakResult) >= 0 ? self : breakResult;
        }

        /// <summary>reject!: delete_if that answers nil when nothing was removed.</summary>
        [RubyMethod("reject!")]
        public static object RejectMutate(BlockParam block, IDictionary<object, object>/*!*/ self) {
            if (block == null) {
                return new Enumerator(self, "reject!");
            }
            object breakResult;
            int removed = RemoveWhere(block, self, true, out breakResult);
            return removed < 0 ? breakResult : removed == 0 ? null : self;
        }

        /// <summary>select! and filter!: keep_if that answers nil when nothing was removed.</summary>
        [RubyMethod("select!")]
        public static object SelectMutate(BlockParam block, IDictionary<object, object>/*!*/ self) {
            return SelectMutate(block, self, "select!");
        }

        // a method of its own to have a frame of its own (see IListOps.Map)
        [RubyMethod("filter!")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("SelectMutate")]
        public static object FilterMutate(BlockParam block, IDictionary<object, object>/*!*/ self) {
            return SelectMutate(block, self, "filter!");
        }

        private static object SelectMutate(BlockParam block, IDictionary<object, object>/*!*/ self, string/*!*/ name) {
            if (block == null) {
                return new Enumerator(self, name);
            }
            object breakResult;
            int removed = RemoveWhere(block, self, false, out breakResult);
            return removed < 0 ? breakResult : removed == 0 ? null : self;
        }

        /// <summary>
        /// Yields each pair, over a snapshot as the block may delete from the hash, and removes those for which
        /// the block's truth is <paramref name="removeWhen"/>. Answers how many were removed, or -1 when the block
        /// broke out of the loop with <paramref name="breakResult"/>.
        /// </summary>
        private static int RemoveWhere(BlockParam/*!*/ block, IDictionary<object, object>/*!*/ self, bool removeWhen, out object breakResult) {
            breakResult = null;
            Hash hash = self as Hash;
            if (hash != null) {
                hash.RequireNotFrozen();
                hash.BeginIteration();
            }

            var keysToDelete = new List<object>();
            try {
                foreach (var pair in CopyKeyValuePairs(self)) {
                    object result;
                    if (block.Yield(CustomStringDictionary.ObjToNull(pair.Key), pair.Value, out result)) {
                        breakResult = result;
                        return -1;
                    }
                    if (RubyOps.IsTrue(result) == removeWhen) {
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
            return keysToDelete.Count;
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
        public static Enumerator/*!*/ Each(RubyContext/*!*/ context, [CalledName]string/*!*/ name, IDictionary<object, object>/*!*/ self) {
            return new Enumerator(self, name);
        }

        // a method of its own to have a frame of its own (see IListOps.Map)
        [RubyMethod("each_pair")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("Each")]
        public static object EachPair(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, IDictionary<object, object>/*!*/ self) {
            return Each(context, block, self);
        }

        [RubyMethod("each")]
        public static object Each(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, IDictionary<object, object>/*!*/ self) {
            if (self.Count > 0) {
                bool yieldPair = IsPairYieldOptimizable(block);

                // A Ruby Hash is walked in place, entry by entry (see Hash.TryGetNextEntry): a value
                // the block replaces is seen replaced, a key it deletes is skipped, and adding a key
                // raises between BeginIteration and EndIteration, as in MRI. Copying the entries up
                // front instead made every iteration O(n) before its first yield, so {...}.first(1)
                // or an #each the block breaks out of early paid for the whole hash.
                var hash = self as Hash;
                if (hash != null) {
                    hash.BeginIteration();
                    try {
                        int index = 0;
                        object key, value;
                        while (hash.TryGetNextEntry(ref index, out key, out value)) {
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
                // See Each: a Hash is walked in place, deleted entries skipped, no new ones allowed.
                Hash hash = self as Hash;
                if (hash != null) {
                    hash.BeginIteration();
                    try {
                        int index = 0;
                        object key, value;
                        while (hash.TryGetNextEntry(ref index, out key, out value)) {
                            object result;
                            if (block.Yield(CustomStringDictionary.ObjToNull(key), out result)) {
                                return result;
                            }
                        }
                    } finally {
                        hash.EndIteration();
                    }
                    return self;
                }

                // Any other IDictionary: a copy of the entries, the deleted ones skipped.
                var pairs = CopyKeyValuePairs(self);
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
                // See Each: a Hash is walked in place, deleted entries skipped, no new ones allowed.
                Hash hash = self as Hash;
                if (hash != null) {
                    hash.BeginIteration();
                    try {
                        int index = 0;
                        object key, value;
                        while (hash.TryGetNextEntry(ref index, out key, out value)) {
                            object result;
                            if (block.Yield(value, out result)) {
                                return result;
                            }
                        }
                    } finally {
                        hash.EndIteration();
                    }
                    return self;
                }

                // Any other IDictionary: a copy of the entries, the deleted ones skipped.
                var pairs = CopyKeyValuePairs(self);
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
            }

            return self;
        }

        #endregion

        [RubyMethod("empty?")]
        public static bool Empty(IDictionary<object, object>/*!*/ self) {
            return self.Count == 0;
        }

        // Looks the key up in the table itself, as MRI does: neither #[], #key? nor the default
        // value or proc take part.
        [RubyMethod("fetch")]
        public static object Fetch(BinaryOpStorage/*!*/ newStorage, BlockParam block, IDictionary<object, object>/*!*/ self,
            object key, [Optional]object defaultValue) {
            RubyContext context = newStorage.Context;

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
                var error = CreateKeyError(context, newStorage, self, key);
                // MRI's rb_key_err_raise raises it through rb_exc_raise, which a TracePoint on :raise sees
                // (as it saw the prelude's Hash#fetch raise it)
                if ((TracePoint.ActiveEvents & (int)TraceEvents.Raise) != 0) {
                    TracePoint.OnException(TraceEvents.Raise, null, context, error);
                }
                throw error;
            }

            return defaultValue;
        }

        /// <summary>
        /// MRI's rb_key_err_raise: KeyError.new("key not found: #{key.inspect}"), then the receiver
        /// and the key - which KeyError#receiver and #key answer - set on it directly. KeyError is
        /// one of the exception classes defined in Ruby (see the KeyError reopening in
        /// Src/StdLib/ironruby/ruby4.rb); until it is, this is an IndexError.
        /// </summary>
        internal static Exception/*!*/ CreateKeyError(RubyContext/*!*/ context, BinaryOpStorage/*!*/ newStorage, object receiver, object key) {
            MutableString message = MutableString.CreateMutable(RubyEncoding.UTF8).Append("key not found: ").Append(context.Inspect(key));

            object keyErrorClass;
            if (context.ObjectClass.TryGetConstant(null, "KeyError", out keyErrorClass)) {
                var site = newStorage.GetCallSite("new");
                Exception exception = site.Target(site, keyErrorClass, message) as Exception;
                if (exception != null) {
                    context.SetInstanceVariable(exception, RubyUtils.GetCoreStateInstanceVariableName("@__receiver__"), receiver);
                    context.SetInstanceVariable(exception, RubyUtils.GetCoreStateInstanceVariableName("@__key__"), key);
                    return exception;
                }
            }

            return RubyExceptions.CreateIndexError(message.ToString());
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
            var hash = self as Hash;
            if (hash != null) {
                return hash.GetKeyArray();
            }
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

        /// <summary>
        /// merge(*others): a copy of the receiver - its class, default and compare_by_identity flag included -
        /// updated with each of the others in turn.
        /// </summary>
        [RubyMethod("merge")]
        public static object Merge(
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            BlockParam block, IDictionary<object, object>/*!*/ self, params object[]/*!*/ others) {

            return Update(toHash, block, Duplicate(initializeCopyStorage, allocateStorage, self), others);
        }

        /// <summary>
        /// merge!(*others) and update(*others): each other hash (or #to_hash of one) in turn, the block - if
        /// any - deciding the value of a key both have. A frozen receiver is an error even with nothing to add.
        /// </summary>
        [RubyMethod("merge!")]
        [RubyMethod("update")]
        public static object Update(ConversionStorage<IDictionary<object, object>>/*!*/ toHash, BlockParam block,
            IDictionary<object, object>/*!*/ self, params object[]/*!*/ others) {

            (self as Hash)?.RequireNotFrozen();
            foreach (object other in others) {
                var hash = other as IDictionary<object, object>;
                if (hash == null) {
                    var site = toHash.GetSite(ConvertToHashAction.Make(toHash.Context));
                    hash = site.Target(site, other);
                }

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

        /// <summary>
        /// reject: a new plain Hash - not of the receiver's class, without its default, with its
        /// compare_by_identity flag - of the pairs for which the block is false.
        /// </summary>
        [RubyMethod("reject")]
        public static object Reject(RubyContext/*!*/ context, BlockParam block, IDictionary<object, object>/*!*/ self) {
            if (block == null) {
                return new Enumerator(self, "reject");
            }
            return Filter(context, block, self, false);
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
        [RubyMethod("filter")]
        public static Enumerator/*!*/ Select(RubyContext/*!*/ context, [CalledName]string/*!*/ name, IDictionary<object, object>/*!*/ self) {
            return new Enumerator(self, name);
        }

        [RubyMethod("select")]
        public static object Select(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, IDictionary<object, object>/*!*/ self) {
            return Filter(context, block, self, true);
        }

        // a method of its own to have a frame of its own (see IListOps.Map)
        [RubyMethod("filter")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("Select")]
        public static object Filter(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, IDictionary<object, object>/*!*/ self) {
            return Filter(context, block, self, true);
        }

        private static object Filter(RubyContext/*!*/ context, BlockParam/*!*/ block, IDictionary<object, object>/*!*/ self, bool keepWhen) {
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

                if (RubyOps.IsTrue(blockResult) == keepWhen) {
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// The first pair, removed, or nil for an empty hash: since Ruby 3.0 the default is not consulted.
        /// </summary>
        [RubyMethod("shift")]
        public static object Shift(IDictionary<object, object>/*!*/ self) {
            (self as Hash)?.RequireNotFrozen();
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
            return Inspect(context, self, self);
        }

        /// <summary>
        /// Hash#inspect's format since Ruby 3.4 - {a: 1, "b" => 2, "c d": 3} - for a CLR dictionary.
        /// </summary>
        internal static MutableString/*!*/ Inspect(RubyContext/*!*/ context, object/*!*/ self, IEnumerable<KeyValuePair<object, object>>/*!*/ entries) {
            using (IDisposable handle = RubyUtils.InfiniteInspectTracker.TrackObject(self)) {
                if (handle == null) {
                    return MutableString.CreateAscii("{...}");
                }

                // US-ASCII until an inspected key or value brings another encoding, as for Array#inspect.
                MutableString str = MutableString.CreateMutable(RubyEncoding.Ascii);
                str.Append('{');
                bool first = true;
                foreach (var entry in entries) {
                    if (first) {
                        first = false;
                    } else {
                        str.Append(", ");
                    }

                    object key = CustomStringDictionary.ObjToNull(entry.Key);
                    if (key is RubySymbol symbol) {
                        // MRI's symbol_key_needs_quote: bare when the symbol inspects bare and the name
                        // reads back as a label (not `a=`, `+`, `@a`, `$a`, `!`).
                        MutableString inspected = context.Inspect(symbol);
                        string name = symbol.ToString();
                        bool bare = !inspected.ToString().StartsWith(":\"", StringComparison.Ordinal) && name.Length > 0 &&
                            "@$!".IndexOf(name[0]) < 0 && "+-*/`%^&|]<=>~@".IndexOf(name[name.Length - 1]) < 0;
                        AppendInspected(str, bare ? inspected.GetSlice(1) : context.Inspect(symbol.String));
                        str.Append(": ");
                    } else {
                        AppendInspected(str, context.Inspect(key));
                        str.Append(" => ");
                    }
                    AppendInspected(str, context.Inspect(entry.Value));
                }
                str.Append('}');
                return str;
            }
        }

        private static void AppendInspected(MutableString/*!*/ str, MutableString/*!*/ item) {
            if (str.Encoding == RubyEncoding.Ascii && item.Encoding != RubyEncoding.Ascii) {
                str.ForceEncoding(item.Encoding);
            }
            str.Append(item);
        }
        
        [RubyMethod("values")]
        public static RubyArray/*!*/ GetValues(IDictionary<object, object>/*!*/ self) {
            var hash = self as Hash;
            if (hash != null) {
                return hash.GetValueArray();
            }
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

    /// <summary>
    /// Any other CLR dictionary - Dictionary&lt;string, int&gt;, Hashtable - inspects as a Hash does.
    /// </summary>
    [RubyModule(Extends = typeof(IDictionary), Restrictions = ModuleRestrictions.None)]
    public static class NonGenericIDictionaryOps {
        [RubyMethod("to_s")]
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, IDictionary/*!*/ self) {
            return IDictionaryOps.Inspect(context, self, Entries(self));
        }

        private static IEnumerable<KeyValuePair<object, object>> Entries(IDictionary/*!*/ self) {
            foreach (DictionaryEntry entry in self) {
                yield return new KeyValuePair<object, object>(entry.Key, entry.Value);
            }
        }
    }
}

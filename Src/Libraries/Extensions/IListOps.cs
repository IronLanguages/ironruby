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
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Scripting;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Builtins {
    using EachSite = Func<CallSite, object, Proc, object>;

    [RubyModule(Extends = typeof(IList), Restrictions = ModuleRestrictions.None)]
    [Includes(typeof(Enumerable))]
    public static class IListOps {
        
        #region Helpers

        // MRI: Some operations check frozen flag even if they don't change the array content.
        private static void RequireNotFrozen(IList/*!*/ self) {
            RubyArray array = self as RubyArray;
            if (array != null) {
                array.RequireNotFrozen();
            }
        }

        internal static int NormalizeIndex(IList/*!*/ list, int index) {
            return NormalizeIndex(list.Count, index);
        }

        internal static int NormalizeIndexThrowIfNegative(IList/*!*/ list, int index) {
            int original = index;
            index = NormalizeIndex(list.Count, index);
            if (index < 0) {
                // MRI: a[-5] = 1 on a 2-element array => "index -5 too small for array; minimum: -2"
                throw RubyExceptions.CreateIndexError("index {0} too small for array; minimum: {1}", original, -list.Count);
            }
            return index;
        }

        internal static int NormalizeIndex(int count, int index) {
            return index < 0 ? index + count : index;
        }

        internal static bool NormalizeRange(int listCount, ref int start, ref int count) {
            start = NormalizeIndex(listCount, start);
            if (start < 0 || start > listCount || count < 0) {
                return false;
            }

            if (count != 0) {
                count = start + count > listCount ? listCount - start : count;
            }

            return true;
        }

        internal static bool NormalizeRange(ConversionStorage<int>/*!*/ fixnumCast, int listCount, Range/*!*/ range, out int begin, out int count) {
            // A beginless range starts at 0 and an endless one runs to the end; both
            // endpoints became nil-able in 2.6/2.7 and used to raise TypeError here.
            begin = (range.Begin == null) ? 0 : Protocols.CastToFixnum(fixnumCast, range.Begin);
            bool endless = range.End == null;
            int end = endless ? listCount : Protocols.CastToFixnum(fixnumCast, range.End);

            begin = NormalizeIndex(listCount, begin);

            if (begin < 0 || begin > listCount) {
                count = 0;
                return false;
            }

            if (!endless) {
                end = NormalizeIndex(listCount, end);
            }

            count = (range.ExcludeEnd && !endless) ? end - begin : end - begin + 1;
            if (endless) {
                count = listCount - begin;
            }
            return true;
        }

        /// <summary>
        /// MRI's rb_range_beg_len with errors ON (Array#values_at): a beginning past the end of
        /// the list is kept rather than clamped, so the caller can pad the answer with nil out to
        /// the range's full length, while a beginning before the first element is a RangeError.
        /// </summary>
        internal static void NormalizeRangeNoClamp(ConversionStorage<int>/*!*/ fixnumCast, int listCount, Range/*!*/ range,
            out int begin, out int count) {

            object rawBegin = range.Begin;
            begin = (rawBegin == null) ? 0 : Protocols.CastToFixnum(fixnumCast, rawBegin);
            bool endless = range.End == null;
            int end = endless ? -1 : Protocols.CastToFixnum(fixnumCast, range.End);
            bool excludeEnd = range.ExcludeEnd && !endless;

            int originalBegin = begin;
            if (begin < 0) {
                begin += listCount;
                if (begin < 0) {
                    throw RubyExceptions.CreateRangeError("{0}{1}{2} out of range",
                        originalBegin, excludeEnd ? "..." : "..", endless ? "" : end.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (end < 0) {
                end += listCount;
            }
            if (!excludeEnd) {
                end++;
            }

            count = end - begin;
            if (count < 0) {
                count = 0;
            }
        }

        private static bool InRangeNormalized(IList/*!*/ list, ref int index) {
            if (index < 0) {
                index = index + list.Count;
            }
            return index >= 0 && index < list.Count;
        }

        private static IList/*!*/ GetResultRange(UnaryOpStorage/*!*/ allocateStorage, IList/*!*/ list, int index, int count) {
            IList result = CreateResultArray(allocateStorage, list);
            int stop = index + count;
            for (int i = index; i < stop; i++) {
                result.Add(list[i]);
            }
            return result;
        }

        private static void InsertRange(IList/*!*/ list, int index, IList/*!*/ items, int start, int count) {
            RubyArray array;
            List<object> listOfObject;
            ICollection<object> collection;
            if ((array = list as RubyArray) != null) {
                array.InsertRange(index, items, start, count);
            } else if ((listOfObject = list as List<object>) != null && ((collection = items as ICollection<object>) != null) && start == 0 && count == collection.Count) {
                listOfObject.InsertRange(index, collection);
            } else {
                for (int i = 0; i < count; i++) {
                    list.Insert(index + i, items[start + i]);
                }
            }
        }

        internal static void RemoveRange(IList/*!*/ collection, int index, int count) {
            if (count <= 1) {
                if (count > 0) {
                    collection.RemoveAt(index);
                }
                return;
            }

            List<object> list;
            RubyArray array;
            if ((array = collection as RubyArray) != null) {
                array.RemoveRange(index, count);
            } else if ((list = collection as List<object>) != null) {
                list.RemoveRange(index, count);
            } else {
                for (int i = index + count - 1; i >= index; i--) {
                    collection.RemoveAt(i);
                }
            }
        }

        internal static void AddRange(IList/*!*/ collection, IList/*!*/ items) {
            RubyArray array;

            int count = items.Count;
            if (count <= 1) {
                if (count > 0) {
                    collection.Add(items[0]);
                } else if ((array = collection as RubyArray) != null) {
                    array.RequireNotFrozen();
                }
                return;
            }

            array = collection as RubyArray;
            if (array != null) {
                array.AddRange(items);
            } else {
                for (int i = 0; i < count; i++) {
                    collection.Add(items[i]);
                }
            }
        }

        private static IList/*!*/ CreateResultArray(UnaryOpStorage/*!*/ allocateStorage, IList/*!*/ list) {
            // Since Ruby 3.0 none of these methods hand back the receiver's subclass:
            // MyArray[1, 2, 3].reverse, #uniq, #flatten, #[0, 2] and the rest are all
            // plain Arrays.
            var array = list as RubyArray;
            if (array != null) {
                return new RubyArray();
            }
            
            // interop - call a default ctor to get an instance:
            var allocate = allocateStorage.GetCallSite("allocate", 0);
            var cls = allocateStorage.Context.GetClassOf(list);
            var result = allocate.Target(allocate, cls) as IList;
            if (result != null) {
                return result;
            }

            throw RubyExceptions.CreateTypeError("{0}#allocate should return IList", cls.Name);
        }

        // The reverse walks below are MRI's (rb_ary_reverse_each, rb_ary_rindex): index Count - 1
        // down to 0, and when the block has shrunk the array below the index just visited, on from
        // its new end - elements appended meanwhile are not visited, and none is visited twice.

        #endregion

        #region initialize_copy, replace, clear, to_a, to_ary

        [RubyMethod("replace")]
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static IList/*!*/ Replace(IList/*!*/ self, [NotNull, DefaultProtocol]IList/*!*/ other) {
            // frozen is checked first, even for the no-op below (MRI: rb_ary_modify_check)
            RequireNotFrozen(self);
            // ary.replace(ary) has to be a no-op, and clearing first would make it empty the
            // array instead.  Rails runs into exactly this: Rails::Command#with_argv does
            // ARGV.replace(argv) where argv *is* ARGV, so `rails db:migrate db:seed` lost
            // every task after the first and railsbench served 404s from an unseeded database.
            if (ReferenceEquals(self, other)) {
                return self;
            }
            self.Clear();
            AddRange(self, other);
            return self;
        }

        [RubyMethod("clear")]
        public static IList Clear(IList/*!*/ self) {
            self.Clear();
            return self;
        }

        [RubyMethod("to_a")]
        [RubyMethod("to_ary")]
        public static RubyArray/*!*/ ToArray(IList/*!*/ self) {
            RubyArray list = new RubyArray(self.Count);
            foreach (object item in self) {
                list.Add(item);
            }
            return list;
        }
        
        #endregion

        #region *, +, concat, product

        [RubyMethod("*")]
        public static IList/*!*/ Repeat(UnaryOpStorage/*!*/ allocateStorage, IList/*!*/ self, int repeat) {
            if (repeat < 0) {
                throw RubyExceptions.CreateArgumentError("negative argument");
            }

            IList result = CreateResultArray(allocateStorage, self);
            RubyArray array = result as RubyArray;
            if (array != null) {
                array.AddCapacity(self.Count * repeat);
            }
            
            for (int i = 0; i < repeat; ++i) {
                AddRange(result, self);
            }

            allocateStorage.Context.TaintObjectBy(result, self);
            return result;
        }

        [RubyMethod("*")]
        public static MutableString Repeat(JoinConversionStorage/*!*/ conversions, IList/*!*/ self, [NotNull]MutableString/*!*/ separator) {
            return Join(conversions, self, separator);
        }

        [RubyMethod("*")]
        public static object Repeat(UnaryOpStorage/*!*/ allocateStorage, JoinConversionStorage/*!*/ conversions,
            IList/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> repeat) {

            if (repeat.IsFixnum()) {
                return Repeat(allocateStorage, self, repeat.Fixnum());
            } else {
                return Repeat(conversions, self, repeat.String());
            }
        }

        [RubyMethod("+")]
        public static RubyArray/*!*/ Concatenate(IList/*!*/ self, [DefaultProtocol, NotNull]IList/*!*/ other) {
            RubyArray result = new RubyArray(self.Count + other.Count);
            AddRange(result, self);
            AddRange(result, other);
            return result;
        }

        /// <summary>
        /// Since 2.4 #concat takes any number of arrays. They are all converted before anything is
        /// appended, which is what makes `a.concat(a, a)` append the original contents twice rather
        /// than chasing its own tail.
        /// </summary>
        [RubyMethod("concat")]
        public static IList/*!*/ Concat(ConversionStorage<IList>/*!*/ arrayCast, IList/*!*/ self, params object[]/*!*/ others) {
            RequireNotFrozen(self);

            var converted = new IList[others.Length];
            for (int i = 0; i < others.Length; i++) {
                if (others[i] == null) {
                    // nil converts to a null reference rather than failing, so it has to be
                    // turned away here or it arrives as a null IList.
                    throw RubyExceptions.CreateImplicitConversionError("NilClass", "Array");
                }
                IList other = Protocols.CastToArray(arrayCast, others[i]);
                // A snapshot, not the receiver itself: the first append would otherwise make
                // the second one see what the first one wrote.
                converted[i] = ReferenceEquals(other, self) ? new RubyArray(other) : other;
            }

            foreach (var other in converted) {
                AddRange(self, other);
            }
            return self;
        }

        [RubyMethod("-")]
        public static RubyArray/*!*/ Difference(UnaryOpStorage/*!*/ hashStorage, BinaryOpStorage/*!*/ eqlStorage, 
            IList/*!*/ self, [DefaultProtocol, NotNull]IList/*!*/ other) {

            RubyArray result = new RubyArray();
            
            // cost: (|self| + |other|) * (hash + eql) + dict
            var remove = new Dictionary<object, bool>(new EqualityComparer(hashStorage, eqlStorage));
            bool removeNull = false;
            foreach (var item in other) {
                if (item != null) {
                    remove[item] = true;
                } else {
                    removeNull = true;
                }
            }

            foreach (var item in self) {
                if (!(item != null ? remove.ContainsKey(item) : removeNull)) {
                    result.Add(item);
                }
            }

            return result;
        }

        internal static int IndexOf(CallSite<Func<CallSite, object, object, object>>/*!*/ equalitySite, IList/*!*/ self, object item) {
            for (int i = 0; i < self.Count; i++) {
                if (Protocols.IsTrue(equalitySite.Target(equalitySite, item, self[i]))) {
                    return i;
                }
            }
            return -1;
        }

        [RubyMethod("product")]
        public static object Product(BlockParam block, IList/*!*/ self, [DefaultProtocol, NotNullItems]params IList/*!*/[]/*!*/ arrays) {
            var result = (block == null) ? new RubyArray() : null;

            // If any of the lists is empty the product is empty; MRI short-circuits before the size check.
            bool empty = self.Count == 0;
            if (!empty) {
                for (int i = 0; i < arrays.Length; i++) {
                    if (arrays[i].Count == 0) {
                        empty = true;
                        break;
                    }
                }
            }

            if (empty) {
                return (block == null) ? (object)result : self;
            }

            // MRI refuses to even start building a product whose size doesn't fit into a native integer.
            // Without this check (0..100).to_a.product(a, a, ... ) loops essentially forever.
            long size = self.Count;
            for (int i = 0; i < arrays.Length; i++) {
                long count = arrays[i].Count;
                if (size > Int64.MaxValue / count) {
                    throw RubyExceptions.CreateRangeError("too big to product");
                }
                size *= count;
            }
            if (size > Int32.MaxValue) {
                throw RubyExceptions.CreateRangeError("too big to product");
            }

            int[] indices = new int[1 + arrays.Length];
            while (true) {
                var current = new RubyArray(indices.Length);
                for (int i = 0; i < indices.Length; i++) {
                    current[i] = GetNth(i, self, arrays)[indices[i]];
                }

                if (block != null) {
                    object blockResult;
                    if (block.Yield(current, out blockResult)) {
                        return blockResult;
                    }
                } else {
                    result.Add(current);
                }

                // increment indices:
                for (int i = indices.Length - 1; i >= 0; i--) {
                    int newIndex = indices[i] + 1;
                    if (newIndex < GetNth(i, self, arrays).Count) {
                        indices[i] = newIndex;
                        break;
                    } else if (i > 0) {
                        indices[i] = 0;
                    } else {
                        return (block == null) ? (object)result : self;
                    }
                }
            }
        }

        private static IList GetNth(int n, IList first, IList[] items) {
            return (n == 0) ? first : items[n - 1];
        }

        #endregion

        #region ==, <=>, eql?, hash

        [RubyMethod("==")]
        public static bool Equals(RespondToStorage/*!*/ respondTo, BinaryOpStorage/*!*/ equals, IList/*!*/ self, object other) {
            return Protocols.RespondTo(respondTo, other, "to_ary") && Protocols.IsEqual(equals, other, self);
        }

        [MultiRuntimeAware]
        private static RubyUtils.RecursionTracker _EqualsTracker = new RubyUtils.RecursionTracker();

        [RubyMethod("==")]
        public static bool Equals(BinaryOpStorage/*!*/ equals, IList/*!*/ self, [NotNull]IList/*!*/ other) {
            Assert.NotNull(self, other);

            if (ReferenceEquals(self, other)) {
                return true;
            }

            if (self.Count != other.Count) {
                return false;
            }

            using (IDisposable handleSelf = _EqualsTracker.TrackObject(self), handleOther = _EqualsTracker.TrackObject(other)) {
                if (handleSelf == null && handleOther == null) {
                    // both arrays went recursive:
                    return true;
                }

                var site = equals.GetCallSite("==");
                for (int i = 0; i < self.Count; ++i) {
                    if (!Protocols.IsEqual(site, self[i], other[i])) {
                        return false;
                    }
                }
            }

            return true;
        }

        [MultiRuntimeAware]
        private static RubyUtils.RecursionTracker _ComparisonTracker = new RubyUtils.RecursionTracker();

        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ comparisonStorage, ConversionStorage<IList>/*!*/ toAry, IList/*!*/ self, object other) {
            IList otherArray = Protocols.TryCastToArray(toAry, other);
            return (otherArray != null) ? Compare(comparisonStorage, self, otherArray) : null;
        }

        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ comparisonStorage, IList/*!*/ self, [NotNull]IList/*!*/ other) {
            using (IDisposable handleSelf = _ComparisonTracker.TrackObject(self), handleOther = _ComparisonTracker.TrackObject(other)) {
                if (handleSelf == null && handleOther == null) {
                    // both arrays went recursive:
                    return ScriptingRuntimeHelpers.Int32ToObject(0);
                }

                int limit = Math.Min(self.Count, other.Count);
                var compare = comparisonStorage.GetCallSite("<=>");

                for (int i = 0; i < limit; i++) {
                    object result = compare.Target(compare, self[i], other[i]);
                    if (!(result is int) || (int)result != 0) {
                        return result;
                    }
                }

                return ScriptingRuntimeHelpers.Int32ToObject(Math.Sign(self.Count - other.Count));
            }
        }

        [RubyMethod("eql?")]
        public static bool HashEquals(BinaryOpStorage/*!*/ eqlStorage, IList/*!*/ self, object other) {
            return RubyArray.Equals(eqlStorage, self, other);
        }

        [RubyMethod("hash")]
        public static int GetHashCode(UnaryOpStorage/*!*/ hashStorage, ConversionStorage<int>/*!*/ fixnumCast, IList/*!*/ self) {
            return RubyArray.GetHashCode(hashStorage, fixnumCast, self);
        }

        #endregion

        #region slice, [], at

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static object GetElement(ConversionStorage<IntegerValue>/*!*/ integerConversion, IList/*!*/ self, object index) {
            if (index is int) {
                return GetElement(self, (int)index);
            }
            // A Float index is truncated the way MRI's rb_num2long does it - directly, never through
            // a redefined Float#to_int - and one outside a long is a RangeError.
            if (index is double) {
                return GetElement(self, (double)index);
            }
            return GetElement(self, Protocols.CastToInteger(integerConversion, index));
        }

        private static object GetElement(IList/*!*/ self, IntegerValue index) {
            if (!index.IsFixnum) {
                // MRI indexes with a long, so an index that fits one is merely past the end of any list.
                long _;
                if (!index.Bignum.AsInt64(out _)) {
                    throw RubyExceptions.CreateRangeError("bignum too big to convert into 'long'");
                }
                return null;
            }
            return GetElement(self, index.Fixnum);
        }

        public static object GetElement(IList/*!*/ self, int index) {
            return InRangeNormalized(self, ref index) ? self[index] : null;
        }

        private static object GetElement(IList/*!*/ self, double index) {
            if (Double.IsNaN(index) || index >= 9.2233720368547758E18 || index < -9.2233720368547758E18) {
                throw RubyExceptions.CreateRangeError(String.Format("float {0} out of range of integer", index));
            }
            long truncated = (long)index;
            return (truncated > Int32.MaxValue || truncated < Int32.MinValue) ? null : GetElement(self, (int)truncated);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static IList GetElements(UnaryOpStorage/*!*/ allocateStorage, 
            IList/*!*/ self, [DefaultProtocol]int index, [DefaultProtocol]int count) {

            if (!NormalizeRange(self.Count, ref index, ref count)) {
                return null;
            }

            return GetResultRange(allocateStorage, self, index, count);
        }

        /// <summary>
        /// Since 2.6 an Enumerator::ArithmeticSequence - what (0..).step(2) answers - can be used
        /// to slice an array, taking every step'th element of the stretch it describes. The class
        /// is written in Ruby and so is the index arithmetic; this overload exists only so that the
        /// sequence reaches it instead of being offered to the Integer conversion, which is the one
        /// thing it cannot do.
        /// </summary>
        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static object GetElements(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slice,
            IList/*!*/ self, [NotNull]Enumerator/*!*/ sequence) {

            var site = slice.GetCallSite("__arithmetic_slice__", 1);
            return site.Target(site, self, sequence);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static IList GetElements(ConversionStorage<int>/*!*/ fixnumCast, UnaryOpStorage/*!*/ allocateStorage, 
            IList/*!*/ self, [NotNull]Range/*!*/ range) {

            int start, count;
            if (!NormalizeRange(fixnumCast, self.Count, range, out start, out count)) {
                return null;
            }

            return count < 0 ? CreateResultArray(allocateStorage, self) : GetElements(allocateStorage, self, start, count);
        }

        [RubyMethod("at")]
        public static object At(ConversionStorage<IntegerValue>/*!*/ integerConversion, IList/*!*/ self, object index) {
            return GetElement(integerConversion, self, index);
        }

        #endregion

        #region []=

        public static void ExpandList(IList/*!*/ list, int index) {
            int diff = index - list.Count;
            for (int i = 0; i < diff; i++) {
                list.Add(null);
            }
        }

        public static void OverwriteOrAdd(IList/*!*/ list, int index, object value) {
            if (index < list.Count) {
                list[index] = value;
            } else {
                list.Add(value);
            }
        }

        public static void DeleteItems(IList/*!*/ list, int index, int length) {
            if (index >= list.Count) {
                ExpandList(list, index);
            } else {
                // normalize for max length
                if (index + length > list.Count) {
                    length = list.Count - index;
                }

                if (length == 0) {
                    RequireNotFrozen(list);
                } else {
                    RemoveRange(list, index, length);
                }
            }
        }

        // MRI checks frozen before converting the index, so a frozen array rejects a[:foo] = 1
        // with FrozenError rather than TypeError. Hence the untyped index parameters below.
        private static int CastIndex(ConversionStorage<int>/*!*/ fixnumCast, object index) {
            return index is int ? (int)index : Protocols.CastToFixnum(fixnumCast, index);
        }

        [RubyMethod("[]=")]
        public static object SetElement(ConversionStorage<int>/*!*/ fixnumCast, IList/*!*/ self, object index, object value) {
            RequireNotFrozen(self);
            int i = CastIndex(fixnumCast, index);
            RubyArray array = self as RubyArray;
            return array != null ? SetElement(array, i, value) : SetElement(self, i, value);
        }

        [RubyMethod("[]=")]
        public static object SetElement(ConversionStorage<IList>/*!*/ arrayTryCast, ConversionStorage<int>/*!*/ fixnumCast,
            IList/*!*/ self, object index, object length, object value) {
            RequireNotFrozen(self);
            return SetElement(arrayTryCast, self, CastIndex(fixnumCast, index), CastIndex(fixnumCast, length), value);
        }

        public static object SetElement(RubyArray/*!*/ self, int index, object value) {
            index = NormalizeIndexThrowIfNegative(self, index);

            if (index >= self.Count) {
                // MRI's rb_ary_store: an index the array cannot grow to (here, one past which
                // the count would overflow) is an IndexError, not an arithmetic accident.
                if (index == Int32.MaxValue) {
                    throw RubyExceptions.CreateIndexError("index {0} too big", index);
                }
                self.AddMultiple(index + 1 - self.Count, null);
            }

            return self[index] = value;
        }

        public static object SetElement(IList/*!*/ self, int index, object value) {
            index = NormalizeIndexThrowIfNegative(self, index);

            if (index < self.Count) {
                self[index] = value;
            } else {
                ExpandList(self, index);
                self.Add(value);
            }
            return value;
        }

        public static object SetElement(ConversionStorage<IList>/*!*/ arrayTryCast, IList/*!*/ self, 
            int index, int length, object value) {
            if (length < 0) {
                throw RubyExceptions.CreateIndexError("negative length ({0})", length);
            }

            index = NormalizeIndexThrowIfNegative(self, index);

            IList valueAsList = value as IList;
            if (valueAsList == null) {
                valueAsList = Protocols.TryCastToArray(arrayTryCast, value);
            }

            if (valueAsList != null && valueAsList.Count == 0) {
                DeleteItems(self, index, length);
            } else {
                if (valueAsList == null) {
                    Insert(self, index, value);
                    
                    if (length > 0) {
                        RemoveRange(self, index + 1, Math.Min(length, self.Count - index - 1));
                    }
                } else {
                    if (value == self) {
                        var newList = new object[self.Count];
                        self.CopyTo(newList, 0);
                        valueAsList = newList;
                    }

                    ExpandList(self, index);

                    int limit = length > valueAsList.Count ? valueAsList.Count : length;

                    for (int i = 0; i < limit; i++) {
                        OverwriteOrAdd(self, index + i, valueAsList[i]);
                    }

                    if (length < valueAsList.Count) {
                        InsertRange(self, index + limit, valueAsList, limit, valueAsList.Count - limit);
                    } else {
                        RemoveRange(self, index + limit, Math.Min(length - valueAsList.Count, self.Count - (index + limit)));
                    }
                }
            }

            return value;
        }

        [RubyMethod("[]=")]
        public static object SetElement(ConversionStorage<IList>/*!*/ arrayTryCast, ConversionStorage<int>/*!*/ fixnumCast, 
            IList/*!*/ self, [NotNull]Range/*!*/ range, object value) {

            int start, count;
            RangeToStartAndCount(fixnumCast, range, self.Count, out start, out count);
            return SetElement(arrayTryCast, self, start, count, value);
        }

        private static void RangeToStartAndCount(ConversionStorage<int>/*!*/ fixnumCast, Range/*!*/ range, int length, out int start, out int count) {
            start = (range.Begin == null) ? 0 : Protocols.CastToFixnum(fixnumCast, range.Begin);
            bool endless = range.End == null;
            int end = endless ? length : Protocols.CastToFixnum(fixnumCast, range.End);

            int originalStart = start;
            start = start < 0 ? start + length : start;
            if (start < 0) {
                // The range is reported as it was written, not as it was normalized: MRI says
                // "-9..0 out of range" for a[-9..0], not "-6..0".
                throw RubyExceptions.CreateRangeError("{0}..{1} out of range", originalStart, endless ? "" : end.ToString());
            }

            if (endless) {
                count = Math.Max(length - start, 0);
                return;
            }

            end = end < 0 ? end + length : end;
            count = Math.Max(range.ExcludeEnd ? end - start : end - start + 1, 0);
        }

        #endregion

        #region &, |

        [RubyMethod("&")]
        public static RubyArray/*!*/ Intersection(UnaryOpStorage/*!*/ hashStorage, BinaryOpStorage/*!*/ eqlStorage, 
            IList/*!*/ self, [DefaultProtocol, NotNull]IList/*!*/ other) {
            Dictionary<object, bool> items = new Dictionary<object, bool>(new EqualityComparer(hashStorage, eqlStorage));
            RubyArray result = new RubyArray();
            // nil cannot be a Dictionary key, so it is tracked on the side.
            bool nilInOther = false, nilTaken = false;

            // first get the items in the RHS
            foreach (object item in other) {
                if (item == null) {
                    nilInOther = true;
                } else {
                    items[item] = true;
                }
            }

            // now, go through the items in the LHS, adding ones that were also in the RHS
            // this ensures that we return the items in the correct order
            foreach (object item in self) {
                if (item == null) {
                    if (nilInOther && !nilTaken) {
                        nilTaken = true;
                        result.Add(null);
                    }
                    continue;
                }

                if (items.Remove(item)) {
                    result.Add(item);
                }
            }

            return result;
        }

        private static void AddUniqueItems(IList/*!*/ list, IList/*!*/ result, Dictionary<object, bool> seen, ref bool nilSeen) {
            foreach (object item in list) {
                if (item == null) {
                    if (!nilSeen) {
                        nilSeen = true;
                        result.Add(null);
                    }
                    continue;
                }

                if (!seen.ContainsKey(item)) {
                    seen.Add(item, true);
                    result.Add(item);
                }
            }
        }

        [RubyMethod("|")]
        public static RubyArray/*!*/ Union(UnaryOpStorage/*!*/ hashStorage, BinaryOpStorage/*!*/ eqlStorage, 
            IList/*!*/ self, [DefaultProtocol, NotNull]IList/*!*/ other) {
            var seen = new Dictionary<object, bool>(new EqualityComparer(hashStorage, eqlStorage));
            bool nilSeen = false;
            var result = new RubyArray();

            // Union merges the two arrays, removing duplicates
            AddUniqueItems(self, result, seen, ref nilSeen);

            AddUniqueItems(other, result, seen, ref nilSeen);

            return result;
        }

        #endregion

        #region assoc, rassoc

        /// <summary>
        /// MRI asks a non-Array element for #to_ary before giving up on it, so an object standing
        /// in for a pair is found by #assoc and #rassoc like a real one. An element that is already
        /// an Array is never asked.
        /// </summary>
        public static IList GetContainerOf(ConversionStorage<IList>/*!*/ tryToAry, BinaryOpStorage/*!*/ equals,
            IList list, int index, object item) {

            foreach (object current in list) {
                IList subArray = current as IList ?? Protocols.TryCastToArray(tryToAry, current);
                if (subArray != null && subArray.Count > index) {
                    if (Protocols.IsEqual(equals, subArray[index], item)) {
                        return subArray;
                    }
                }
            }
            return null;
        }

        [RubyMethod("assoc")]
        public static IList GetContainerOfFirstItem(ConversionStorage<IList>/*!*/ tryToAry, BinaryOpStorage/*!*/ equals,
            IList/*!*/ self, object item) {
            return GetContainerOf(tryToAry, equals, self, 0, item);
        }

        [RubyMethod("rassoc")]
        public static IList GetContainerOfSecondItem(ConversionStorage<IList>/*!*/ tryToAry, BinaryOpStorage/*!*/ equals,
            IList/*!*/ self, object item) {
            return GetContainerOf(tryToAry, equals, self, 1, item);
        }

        #endregion

        #region collect!, map!, compact, compact!

        [RubyMethod("collect!")]
        [RubyMethod("map!")]
        public static object CollectInPlace(BlockParam collector, IList/*!*/ self) {
            if (collector == null) {
                // The Enumerator itself is fine to hand out; its #each comes back through here
                // with a block and raises then, which is what MRI does.
                return new Enumerator(self, "collect!");
            }
            // The check belongs to the method rather than to the loop, so that an empty frozen
            // array - where the loop never runs - raises too.
            RequireNotFrozen(self);
            return CollectInPlaceImpl(collector, self);
        }

        private static object CollectInPlaceImpl(BlockParam/*!*/ collector, IList/*!*/ self) {
            int i = 0;
            while (i < self.Count) {
                object result;
                if (collector.Yield(self[i], out result)) {
                    return result;
                }
                self[i] = result;
                i++;
            }

            return self;
        }

        [RubyMethod("compact")]
        public static IList/*!*/ Compact(UnaryOpStorage/*!*/ allocateStorage, IList/*!*/ self) {
            var array = self as RubyArray;
            if (array != null) {
                // The common case: no interface dispatch, no enumerator, and the result is
                // allocated at its final size in one go.
                int count = array.Count;
                var compacted = new RubyArray(count);
                for (int i = 0; i < count; i++) {
                    object item = array[i];
                    if (item != null) {
                        compacted.Add(item);
                    }
                }
                allocateStorage.Context.TaintObjectBy(compacted, self);
                return compacted;
            }

            IList result = CreateResultArray(allocateStorage, self);

            foreach (object item in self) {
                if (item != null) {
                    result.Add(item);
                }
            }

            allocateStorage.Context.TaintObjectBy(result, self);

            return result;
        }

        [RubyMethod("compact!")]
        public static IList CompactInPlace(IList/*!*/ self) {
            RequireNotFrozen(self);

            bool changed = false;
            int i = 0;
            while (i < self.Count) {
                if (self[i] == null) {
                    changed = true;
                    self.RemoveAt(i);
                } else {
                    i++;
                }
            }
            return changed ? self : null;
        }

        #endregion

        #region delete, delete_at, reject, reject!

        public static bool Remove(BinaryOpStorage/*!*/ equals, IList/*!*/ self, object item) {
            int i = 0;
            bool removed = false;
            while (i < self.Count) {
                if (Protocols.IsEqual(equals, self[i], item)) {
                    self.RemoveAt(i);
                    removed = true;
                } else {
                    i++;
                }
            }
            return removed;
        }

        [RubyMethod("delete")]
        public static object Delete(BinaryOpStorage/*!*/ equals, IList/*!*/ self, object item) {
            return Remove(equals, self, item) ? item : null;
        }

        [RubyMethod("delete")]
        public static object Delete(BinaryOpStorage/*!*/ equals, BlockParam block, IList/*!*/ self, object item) {
            bool removed = Remove(equals, self, item);

            if (!removed && block != null) {
                object result;
                block.Yield(out result);
                return result;
            }
            return removed ? item : null;
        }

        [RubyMethod("delete_at")]
        public static object DeleteAt(IList/*!*/ self, [DefaultProtocol]int index) {
            index = index < 0 ? index + self.Count : index;
            if (index < 0 || index >= self.Count) {
                return null;
            }

            object result = GetElement(self, index);
            self.RemoveAt(index);
            return result;
        }

        [RubyMethod("delete_if")]
        public static object DeleteIf(BlockParam block, IList/*!*/ self) {
            return (block != null) ? DeleteIfImpl(block, self) : new Enumerator(self, "delete_if");
        }

        private static object DeleteIfImpl(BlockParam/*!*/ block, IList/*!*/ self) {
            bool changed, jumped;
            object result = DeleteIf(block, self, out changed, out jumped);
            return jumped ? result : self;
        }

        [RubyMethod("reject!")]
        public static object RejectInPlace(BlockParam block, IList/*!*/ self) {
            return (block != null) ? RejectInPlaceImpl(block, self) : new Enumerator(self, "reject!");
        }

        private static object RejectInPlaceImpl(BlockParam/*!*/ block, IList/*!*/ self) {
            bool changed, jumped;
            object result = DeleteIf(block, self, out changed, out jumped);
            return jumped ? result : changed ? self : null;
        }

        [RubyMethod("reject")]
        public static object Reject(CallSiteStorage<EachSite>/*!*/ each, UnaryOpStorage/*!*/ allocate,
            BlockParam predicate, IList/*!*/ self) {
            return (predicate != null) ? RejectImpl(each, allocate, predicate, self) : new Enumerator(self, "reject");
        }

        private static object RejectImpl(CallSiteStorage<EachSite>/*!*/ each, UnaryOpStorage/*!*/ allocate, 
            BlockParam/*!*/ predicate, IList/*!*/ self) {

            IList result = CreateResultArray(allocate, self);

            for (int i = 0; i < self.Count; i++) {
                object item = self[i];
                object blockResult;
                if (predicate.Yield(item, out blockResult)) {
                    return blockResult;
                }

                if (RubyOps.IsFalse(blockResult)) {
                    result.Add(item);
                }
            }

            return result;
        }

        /// <summary>
        /// The engine behind #delete_if and #reject!. MRI does not remove anything while the block
        /// is running: it compacts the elements it is keeping towards the front of the array and
        /// shortens it once, at the end. That is why the receiver still has its original length
        /// when the block looks at it, and why a block that breaks out - or raises - leaves the
        /// array with the elements it had already rejected gone and the rest untouched.
        /// </summary>
        private static object DeleteIf(BlockParam/*!*/ block, IList/*!*/ self, out bool changed, out bool jumped) {
            Assert.NotNull(block, self);

            changed = false;
            jumped = false;

            RequireNotFrozen(self);

            int processed = 0, kept = 0;
            object jumpResult = null;
            try {
                while (processed < self.Count) {
                    object item = self[processed];
                    object result;
                    if (block.Yield(item, out result)) {
                        jumped = true;
                        jumpResult = result;
                        break;
                    }

                    processed++;
                    if (RubyOps.IsTrue(result)) {
                        changed = true;
                    } else {
                        if (kept != processed - 1) {
                            self[kept] = item;
                        }
                        kept++;
                    }
                }
            } finally {
                if (kept != processed && processed <= self.Count) {
                    int tail = self.Count - processed;
                    for (int j = 0; j < tail; j++) {
                        self[kept + j] = self[processed + j];
                    }
                    RemoveRange(self, kept + tail, self.Count - (kept + tail));
                }
            }
            return jumpResult;
        }

        #endregion

        #region each, each_index, reverse_each

        [RubyMethod("each")]
        public static Enumerator/*!*/ Each(IList/*!*/ self) {
            return new Enumerator(self, "each");
        }

        [RubyMethod("each")]
        public static object Each([NotNull]BlockParam/*!*/ block, IList/*!*/ self) {
            var array = self as RubyArray;
            if (array != null) {
                // the same loop, without an interface call for Count and the element every time round
                for (int i = 0; i < array.Count; i++) {
                    object result;
                    if (block.Yield(array[i], out result)) {
                        return result;
                    }
                }
                return self;
            }

            for (int i = 0; i < self.Count; i++) {
                object result;
                if (block.Yield(self[i], out result)) {
                    return result;
                }
            }
            return self;
        }

        #region map, collect, select

        // Array's own, as in MRI (rb_ary_collect, rb_ary_select): they walk the array rather than
        // run Enumerable's versions, which call #each with a block of their own that yields to the
        // given block - two block invocations and a packed argument array per element. Like MRI's,
        // they do not call an #each that a subclass defines.

        [RubyMethod("collect")]
        [RubyMethod("map")]
        public static Enumerator/*!*/ GetMapEnumerator(IList/*!*/ self) {
            return new Enumerator(self, "map");
        }

        [RubyMethod("collect")]
        [RubyMethod("map")]
        public static object Map([NotNull]BlockParam/*!*/ collector, IList/*!*/ self) {
            var result = new RubyArray(self.Count);
            var array = self as RubyArray;
            for (int i = 0; i < (array != null ? array.Count : self.Count); i++) {
                object blockResult;
                if (collector.Yield(array != null ? array[i] : self[i], out blockResult)) {
                    return blockResult;
                }
                result.Add(blockResult);
            }
            return result;
        }

        [RubyMethod("filter")]
        [RubyMethod("select")]
        public static Enumerator/*!*/ GetSelectEnumerator(IList/*!*/ self) {
            return new Enumerator(self, "select");
        }

        [RubyMethod("filter")]
        [RubyMethod("select")]
        public static object Select([NotNull]BlockParam/*!*/ predicate, IList/*!*/ self) {
            var result = new RubyArray();
            var array = self as RubyArray;
            for (int i = 0; i < (array != null ? array.Count : self.Count); i++) {
                object blockResult;
                if (predicate.Yield(array != null ? array[i] : self[i], out blockResult)) {
                    return blockResult;
                }
                if (Protocols.IsTrue(blockResult)) {
                    // MRI adds the element as it is once the block has run (rb_ary_elt)
                    result.Add(i < self.Count ? self[i] : null);
                }
            }
            return result;
        }

        #endregion

        [RubyMethod("each_index")]
        public static Enumerator/*!*/ EachIndex(IList/*!*/ self) {
            return new Enumerator(self, "each_index");
        }
        
        [RubyMethod("each_index")]
        public static object EachIndex([NotNull]BlockParam/*!*/ block, IList/*!*/ self) {
            int i = 0;
            while (i < self.Count) {
                object result;
                if (block.Yield(ScriptingRuntimeHelpers.Int32ToObject(i), out result)) {
                    return result;
                }
                i++;
            }

            return self;
        }

        [RubyMethod("reverse_each")]
        public static Enumerator/*!*/ ReverseEach(RubyArray/*!*/ self) {
            return new Enumerator(self, "reverse_each");
        }

        [RubyMethod("reverse_each")]
        public static object ReverseEach([NotNull]BlockParam/*!*/ block, RubyArray/*!*/ self) {
            for (int i = self.Count; i-- > 0; ) {
                object result;
                if (block.Yield(self[i], out result)) {
                    return result;
                }
                int count = self.Count;
                if (count < i) {
                    i = count;
                }
            }
            return self;
        }

        #endregion

        #region fetch

        [RubyMethod("fetch")]
        public static object Fetch(
            ConversionStorage<int>/*!*/ fixnumCast, 
            BlockParam outOfRangeValueProvider, 
            IList/*!*/ list, 
            object/*!*/ index, 
            [Optional]object defaultValue) {

            // MRI warns whenever both are given, even if the index is in range
            if (outOfRangeValueProvider != null && defaultValue != Missing.Value) {
                fixnumCast.Context.ReportWarning("block supersedes default value argument");
            }

            int convertedIndex = Protocols.CastToFixnum(fixnumCast, index);
            int originalIndex = convertedIndex;

            if (InRangeNormalized(list, ref convertedIndex)) {
                return list[convertedIndex];
            }

            if (outOfRangeValueProvider != null) {
                object result;
                outOfRangeValueProvider.Yield(index, out result);
                return result;
            }

            if (defaultValue == Missing.Value) {
                // MRI: [1].fetch(5) => "index 5 outside of array bounds: -1...1"
                throw RubyExceptions.CreateIndexError("index {0} outside of array bounds: {1}...{2}",
                    originalIndex, -list.Count, list.Count);
            }
            return defaultValue;
        }

        #endregion

        #region fill

        // Array#fill(obj, start = 0), fill(obj, start, length), fill(obj, range), and the block
        // forms fill { }, fill(start) { }, fill(start, length) { }, fill(range) { }. One Ruby
        // method dispatching by hand: as separate overloads the Integer- and Object-typed
        // positions competed when a block was given ("Found multiple methods"), and the arity
        // errors could not say 1..3 (no block) or 0..2 (block) the way MRI does.
        [RubyMethod("fill")]
        public static object Fill(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, IList/*!*/ self, params object[]/*!*/ args) {
            int begin, length;
            if (block == null) {
                if (args.Length < 1 || args.Length > 3) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 1..3)", args.Length);
                }
                object obj = args[0];
                if (args.Length == 1) {
                    return Fill(self, obj, 0);
                }
                if (args.Length == 2 && args[1] is Range) {
                    FillRange(fixnumCast, self, (Range)args[1], out begin, out length);
                    return Fill(self, obj, begin, length);
                }
                int start = (args[1] == null) ? 0 : Protocols.CastToFixnum(fixnumCast, args[1]);
                if (args.Length == 2 || args[2] == null) {
                    return Fill(self, obj, start);
                }
                return Fill(self, obj, start, Protocols.CastToFixnum(fixnumCast, args[2]));
            }

            if (args.Length > 2) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0..2)", args.Length);
            }
            if (args.Length == 0) {
                return Fill(block, self, 0);
            }
            if (args.Length == 1 && args[0] is Range) {
                FillRange(fixnumCast, self, (Range)args[0], out begin, out length);
                return Fill(block, self, begin, length);
            }
            int blockStart = (args[0] == null) ? 0 : Protocols.CastToFixnum(fixnumCast, args[0]);
            if (args.Length == 1 || args[1] == null) {
                return Fill(block, self, blockStart);
            }
            return Fill(block, self, blockStart, Protocols.CastToFixnum(fixnumCast, args[1]));
        }

        public static IList/*!*/ Fill(IList/*!*/ self, object obj, int start) {
            RequireNotFrozen(self);
            // Note: Array#fill(obj, start) is not equivalent to Array#fill(obj, start, 0)
            // (as per MRI behavior, the latter can expand the array if start > length, but the former doesn't)
            start = Math.Max(0, NormalizeIndex(self, start));

            for (int i = start; i < self.Count; i++) {
                self[i] = obj;
            }
            return self;
        }

        public static IList/*!*/ Fill(IList/*!*/ self, object obj, int start, int length) {
            RequireNotFrozen(self);
            // Note: Array#fill(obj, start) is not equivalent to Array#fill(obj, start, 0)
            // (as per MRI behavior, the latter can expand the array if start > length, but the former doesn't)
            start = Math.Max(0, NormalizeIndex(self, start));

            // start + length overflowed Int32 and produced a negative bound, after which the
            // loop below appended one element at a time until the process died. MRI reports
            // "argument too big" for a size it cannot represent; the CLR's is Int32.MaxValue.
            if (length > 0 && (long)start + length > Int32.MaxValue) {
                throw RubyExceptions.CreateArgumentError("argument too big");
            }

            ExpandList(self, Math.Min(start, start + length));

            for (int i = 0; i < length; i++) {
                OverwriteOrAdd(self, start + i, obj);
            }

            return self;
        }

        /// <summary>
        /// MRI normalizes the range against the current length and refuses one whose beginning
        /// lies before the first element - #fill does not extend an array backwards.
        /// </summary>
        private static void FillRange(ConversionStorage<int>/*!*/ fixnumCast, IList/*!*/ self, Range/*!*/ range,
            out int begin, out int length) {

            int rawBegin = (range.Begin == null) ? 0 : Protocols.CastToFixnum(fixnumCast, range.Begin);
            bool endless = range.End == null;
            int rawEnd = endless ? self.Count - 1 : Protocols.CastToFixnum(fixnumCast, range.End);

            begin = NormalizeIndex(self, rawBegin);
            if (begin < 0) {
                throw RubyExceptions.CreateRangeError("{0}..{1} out of range", rawBegin, endless ? "" : rawEnd.ToString());
            }

            int end = endless ? self.Count - 1 : NormalizeIndex(self, rawEnd);
            length = endless
                ? Math.Max(0, self.Count - begin)
                : Math.Max(0, end - begin + (range.ExcludeEnd ? 0 : 1));
        }

        public static object Fill([NotNull]BlockParam/*!*/ block, IList/*!*/ self, int start) {
            RequireNotFrozen(self);
            start = Math.Max(0, NormalizeIndex(self, start));

            // MRI fixes the end of the range before it starts yielding, so a block that appends to
            // the array doesn't extend the loop forever, and it stops early if the block shrank it.
            int end = self.Count;

            for (int i = start; i < end; i++) {
                object result;
                if (block.Yield(i, out result)) {
                    return result;
                }
                if (i >= self.Count) {
                    break;
                }
                self[i] = result;
            }
            return self;
        }

        public static object Fill([NotNull]BlockParam/*!*/ block, IList/*!*/ self, int start, int length) {
            RequireNotFrozen(self);
            start = Math.Max(0, NormalizeIndex(self, start));

            ExpandList(self, Math.Min(start, start + length));

            for (int i = start; i < start + length; i++) {
                object result;
                if (block.Yield(i, out result)) {
                    return result;
                }
                OverwriteOrAdd(self, i, result);
            }

            return self;
        }

        #endregion

        #region first, last

        [RubyMethod("first")]
        public static object First(IList/*!*/ self) {
            return self.Count == 0 ? null : self[0];
        }

        [RubyMethod("first")]
        public static IList/*!*/ First(IList/*!*/ self, [DefaultProtocol]int count) {
            if (count < 0) {
                throw RubyExceptions.CreateArgumentError("negative array size");
            }

            if (count > self.Count) {
                count = self.Count;
            }
            return new RubyArray(self, 0, count);
        }

        [RubyMethod("last")]
        public static object Last(IList/*!*/ self) {
            return self.Count == 0 ? null : self[self.Count - 1];
        }

        [RubyMethod("last")]
        public static IList/*!*/ Last(IList/*!*/ self, [DefaultProtocol]int count) {
            if (count < 0) {
                throw RubyExceptions.CreateArgumentError("negative array size");
            }

            if (count > self.Count) {
                count = self.Count;
            }
            return new RubyArray(self, self.Count - count, count);
        }

        #endregion

        #region flatten, flatten!

        private static int IndexOfList(ConversionStorage<IList>/*!*/ tryToAry, IList/*!*/ list, int start, out IList listItem) {
            for (int i = start; i < list.Count; i++) {
                listItem = Protocols.TryCastToArray(tryToAry, list[i]);
                if (listItem != null) {
                    return i;
                }
            }
            listItem = null;
            return -1;
        }

        /// <summary>
        /// Enumerates all items of the list recursively - if there are any items convertible to IList the items of that lists are enumerated as well.
        /// Returns null if there are no nested lists and so the list can be enumerated using a standard enumerator.
        /// </summary>
        public static IEnumerable<object> EnumerateRecursively(ConversionStorage<IList>/*!*/ tryToAry, IList/*!*/ list, int maxDepth, Func<IList, object>/*!*/ loopDetected) {
            if (maxDepth == 0) {
                return null;
            }
            
            IList nested;
            int nestedIndex = IndexOfList(tryToAry, list, 0, out nested);

            if (nestedIndex == -1) {
                return null;
            }

            return EnumerateRecursively(tryToAry, list, list, nested, nestedIndex, maxDepth, loopDetected);
        }

        private static IEnumerable<object>/*!*/ EnumerateRecursively(ConversionStorage<IList>/*!*/ tryToAry, IList/*!*/ root, 
            IList/*!*/ list, IList/*!*/ nested, int nestedIndex, int maxDepth, Func<IList, object>/*!*/ loopDetected) {

            Debug.Assert(nested != null);
            Debug.Assert(nestedIndex != -1);

            // A depth was asked for: the descent is bounded by it, and MRI does not look for a
            // cycle at all - `a = [1, 2]; a << a; a.flatten(1)` splices one level and stops,
            // where flattening all the way is the case that has to raise.
            bool detectLoops = maxDepth < 0;
            if (maxDepth < 0) {
                maxDepth = Int32.MaxValue;
            }

            // Each work item carries the depth of its list, rather than the depth being read off
            // the size of recursionPath: that set holds the lists being descended into for loop
            // detection, and it neither shrinks when a list's items have all been yielded (only
            // when its last work item is popped) nor grows before a list has been looked into.
            // Taking its size for the depth made `[[1, 2], [3, 4]].flatten(1)` stop after the
            // first nested list and answer [1, 2, [3, 4]].
            var worklist = new Stack<(IList List, int Start, int Depth)>();
            var recursionPath = new HashSet<object>(ReferenceEqualityComparer<object>.Instance);
            recursionPath.Add(root);
            int start = 0;
            int depth = 0;

            while (true) {
                // "list" is the list being visited by the current work item (there might be more work items visiting the same list)
                // "nestedIndex" is the index of "nested" in the "list"

                if (nestedIndex >= 0) {
                    // push a work item that will process the items following the nested list:
                    worklist.Push((list, nestedIndex + 1, depth));

                    // yield items preceding the nested list:
                    for (int i = start; i < nestedIndex; i++) {
                        yield return list[i];
                    }

                    // push a workitem for the nested list:
                    worklist.Push((nested, 0, depth + 1));
                } else {
                    // there is no nested list => yield all remaining items:
                    for (int i = start; i < list.Count; i++) {
                        yield return list[i];
                    }
                }

            next:
                if (worklist.Count == 0) {
                    break;
                }

                var workitem = worklist.Pop();
                list = workitem.List;
                start = workitem.Start;
                depth = workitem.Depth;

                // finishing nested list:
                if (start == list.Count) {
                    recursionPath.Remove(list);
                    goto next;
                }

                // starting nested list:
                if (detectLoops && start == 0 && recursionPath.Contains(list)) {
                    yield return loopDetected(list);
                    goto next;
                }

                // set the index to -1 if we would go deeper then we should:
                nestedIndex = (depth < maxDepth) ? IndexOfList(tryToAry, list, start, out nested) : -1;

                // starting nested list:
                if (start == 0 && nestedIndex != -1) {
                    recursionPath.Add(list);
                }
            }
        }

        internal static IList/*!*/ Flatten(ConversionStorage<IList>/*!*/ tryToAry, IList/*!*/ list, int maxDepth, IList/*!*/ result) {
            var recEnum = EnumerateRecursively(tryToAry, list, maxDepth, (_) => { 
                throw RubyExceptions.CreateArgumentError("tried to flatten recursive array"); 
            });

            if (recEnum != null) {
                foreach (var item in recEnum) {
                    result.Add(item);
                }
            } else {
                AddRange(result, list);
            }

            return result;
        }

        [RubyMethod("flatten")]
        public static IList/*!*/ Flatten(UnaryOpStorage/*!*/ allocateStorage, ConversionStorage<IList>/*!*/ tryToAry, IList/*!*/ self, 
            [DefaultProtocol, DefaultParameterValue(-1)] int maxDepth) {

            return Flatten(tryToAry, self, maxDepth, CreateResultArray(allocateStorage, self));
        }

        /// <summary>flatten(nil) flattens all the way down, like flatten with no argument.</summary>
        [RubyMethod("flatten")]
        public static IList/*!*/ Flatten(UnaryOpStorage/*!*/ allocateStorage, ConversionStorage<IList>/*!*/ tryToAry, IList/*!*/ self, DynamicNull maxDepth) {
            return Flatten(allocateStorage, tryToAry, self, -1);
        }

        [RubyMethod("flatten!")]
        public static IList FlattenInPlace(ConversionStorage<IList>/*!*/ tryToAry, RubyArray/*!*/ self, DynamicNull maxDepth) {
            return FlattenInPlace(tryToAry, self, -1);
        }

        [RubyMethod("flatten!")]
        public static IList FlattenInPlace(ConversionStorage<IList>/*!*/ tryToAry, RubyArray/*!*/ self, [DefaultProtocol, DefaultParameterValue(-1)] int maxDepth) {
            self.RequireNotFrozen();
            return FlattenInPlace(tryToAry, (IList)self, maxDepth);
        }
        
        [RubyMethod("flatten!")]
        public static IList FlattenInPlace(ConversionStorage<IList>/*!*/ tryToAry, IList/*!*/ self, [DefaultProtocol, DefaultParameterValue(-1)] int maxDepth) {
            if (maxDepth == 0) {
                return null;
            }
            
            IList nested;
            int nestedIndex = IndexOfList(tryToAry, self, 0, out nested);

            if (nestedIndex == -1) {
                return null;
            }

            var remaining = new object[self.Count - nestedIndex];
            for (int i = 0, j = nestedIndex; i < remaining.Length; i++) {
                remaining[i] = self[j++];
            }

            bool isRecursive = false;
            var recEnum = EnumerateRecursively(tryToAry, self, remaining, nested, 0, maxDepth, (rec) => {
                isRecursive = true;
                return rec;
            });

            // rewrite items following the first nested list (including the list):
            int itemCount = nestedIndex;
            foreach (var item in recEnum) {
                if (itemCount < self.Count) {
                    self[itemCount] = item;
                } else {
                    self.Add(item);
                }
                itemCount++;
            }

            // empty arrays can make the list shrink:
            while (self.Count > itemCount) {
                self.RemoveAt(self.Count - 1);
            }

            if (isRecursive) {
                throw RubyExceptions.CreateArgumentError("tried to flatten recursive array");
            }
            return self;
        }

        #endregion

        #region include?, find_index/index, rindex

        [RubyMethod("include?")]
        public static bool Include(BinaryOpStorage/*!*/ equals, IList/*!*/ self, object item) {
            return FindIndex(equals, null, self, item) != null;
        }

        [RubyMethod("find_index")]
        [RubyMethod("index")]
        public static Enumerator/*!*/ GetFindIndexEnumerator(BlockParam predicate, IList/*!*/ self) {
            Debug.Assert(predicate == null);
            // The Enumerator finds the index once it is given a block, so it reports no size:
            // it answers one number, not one element per element of the receiver.
            return new Enumerator(self, "find_index");
        }

        [RubyMethod("rindex")]
        public static Enumerator/*!*/ GetReverseIndexEnumerator(BlockParam predicate, IList/*!*/ self) {
            Debug.Assert(predicate == null);
            return new Enumerator(self, "rindex");
        }

        [RubyMethod("find_index")]
        [RubyMethod("index")]
        public static object FindIndex([NotNull]BlockParam/*!*/ predicate, IList/*!*/ self) {
            for (int i = 0; i < self.Count; i++) {
                object blockResult;
                if (predicate.Yield(self[i], out blockResult)) {
                    return blockResult;
                }
                
                if (Protocols.IsTrue(blockResult)) {
                    return ScriptingRuntimeHelpers.Int32ToObject(i);
                }
            }
            return null;
        }

        [RubyMethod("find_index")]
        [RubyMethod("index")]
        public static object FindIndex(BinaryOpStorage/*!*/ equals, BlockParam predicate, IList/*!*/ self, object value) {
            if (predicate != null) {
                equals.Context.ReportWarning("given block not used");
            }

            for (int i = 0; i < self.Count; i++) {
                if (Protocols.IsEqual(equals, self[i], value)) {
                    return ScriptingRuntimeHelpers.Int32ToObject(i);
                }
            }

            return null;
        }

        [RubyMethod("rindex")]
        public static object ReverseIndex([NotNull]BlockParam/*!*/ predicate, IList/*!*/ self) {
            for (int i = self.Count; i-- > 0; ) {
                object blockResult;
                if (predicate.Yield(self[i], out blockResult)) {
                    return blockResult;
                }

                if (Protocols.IsTrue(blockResult)) {
                    return ScriptingRuntimeHelpers.Int32ToObject(i);
                }
                int count = self.Count;
                if (count < i) {
                    i = count;
                }
            }
            return null;
        }

        [RubyMethod("rindex")]
        public static object ReverseIndex(BinaryOpStorage/*!*/ equals, BlockParam predicate, IList/*!*/ self, object item) {
            if (predicate != null) {
                equals.Context.ReportWarning("given block not used");
            } 
            
            for (int i = self.Count; i-- > 0; ) {
                if (Protocols.IsEqual(equals, self[i], item)) {
                    return ScriptingRuntimeHelpers.Int32ToObject(i);
                }
                // an == that shrank the array below the next index ends the search, as in MRI
                if (i > self.Count) {
                    break;
                }
            }
            return null;
        }

        #endregion

        #region indexes, indices, values_at

        [RubyMethod("indexes")]
        [RubyMethod("indices")]
        public static object Indexes(ConversionStorage<int>/*!*/ fixnumCast,
            UnaryOpStorage/*!*/ allocateStorage,
            IList/*!*/ self, params object[]/*!*/ values) {
            fixnumCast.Context.ReportWarning("Array#indexes and Array#indices are deprecated; use Array#values_at");

            RubyArray result = new RubyArray();

            for (int i = 0; i < values.Length; ++i) {
                Range range = values[i] as Range;
                if (range != null) {
                    IList fragment = GetElements(fixnumCast, allocateStorage, self, range);
                    if (fragment != null) {
                        result.Add(fragment);
                    }
                } else {
                    result.Add(GetElement(self, Protocols.CastToFixnum(fixnumCast, values[i])));
                }
            }

            return result;

        }

        [RubyMethod("values_at")]
        public static RubyArray/*!*/ ValuesAt(ConversionStorage<int>/*!*/ fixnumCast,
            UnaryOpStorage/*!*/ allocateStorage,
            IList/*!*/ self, params object[]/*!*/ values) {
            RubyArray result = new RubyArray();

            for (int i = 0; i < values.Length; i++) {
                Range range = values[i] as Range;
                if (range != null) {
                    // MRI asks for the range with errors on: a range reaching past the end of
                    // the array is padded with nil out to its full length (values_at(0..9) on a
                    // three-element array answers ten elements), but one that starts before the
                    // first element is a RangeError rather than an empty stretch.
                    int start, count;
                    NormalizeRangeNoClamp(fixnumCast, self.Count, range, out start, out count);

                    if (count > 0) {
                        int filled = result.Count + count;
                        if (start < self.Count) {
                            int available = start + count > self.Count ? self.Count - start : count;
                            result.AddRange(GetElements(allocateStorage, self, start, available));
                        }
                        while (result.Count < filled) {
                            result.Add(null);
                        }
                    }
                } else {
                    result.Add(GetElement(self, Protocols.CastToFixnum(fixnumCast, values[i])));
                }
            }

            return result;
        }

        #endregion

        #region join, to_s, inspect
        
        private static void JoinRecursive(JoinConversionStorage/*!*/ conversions, IList/*!*/ list, List<MutableString/*!*/>/*!*/ parts, 
            ref bool? isBinary, ref Dictionary<object, bool> seen) {

            foreach (object item in list) {
                if (item == null) {
                    parts.Add(null);
                    continue;
                }

                IList listItem = conversions.ToAry.Target(conversions.ToAry, item);
                if (listItem != null) {
                    bool _;
                    if (ReferenceEquals(listItem, list) || seen != null && seen.TryGetValue(listItem, out _)) {
                        throw RubyExceptions.CreateArgumentError("recursive array join");
                    }

                    if (seen == null) {
                        seen = new Dictionary<object, bool>(ReferenceEqualityComparer<object>.Instance);
                    }

                    seen.Add(listItem, true);
                    JoinRecursive(conversions, listItem, parts, ref isBinary, ref seen);
                    seen.Remove(listItem);
                    continue;
                }

                // try to_str first, then to_s:
                MutableString strItem = conversions.ToStr.Target(conversions.ToStr, item) ?? conversions.ToS.Target(conversions.ToS, item);
                parts.Add(strItem);
                isBinary = isBinary.HasValue ? (isBinary | strItem.IsBinary) : strItem.IsBinary;
            }
        }

        public static MutableString/*!*/ Join(JoinConversionStorage/*!*/ conversions, IList/*!*/ self, MutableString separator) {
            var parts = new List<MutableString>(self.Count);
            bool? isBinary = (separator != null) ? separator.IsBinary : (bool?)null;
            Dictionary<object, bool> seen = null;
            
            // build a list of strings to join:
            JoinRecursive(conversions, self, parts, ref isBinary, ref seen);
            if (parts.Count == 0) {
                return MutableString.CreateEmpty(RubyEncoding.Ascii);
            }

            if (separator != null && separator.IsBinary != isBinary && !separator.IsAscii()) {
                isBinary = true;
            }

            // calculate length:
            MutableString any = separator;
            int length = (separator != null) ? (isBinary.HasValue && isBinary.Value ? separator.GetByteCount() : separator.GetCharCount()) * (parts.Count - 1) : 0;
            for (int i = 0, n = parts.Count; i < n; i++) {
                var part = parts[i];
                if (part != null) {
                    length += (isBinary.HasValue && isBinary.Value) ? part.GetByteCount() : part.GetCharCount();
                    if (any == null) {
                        any = part;
                    }
                }
            }

            if (any == null) {
                return MutableString.CreateEmpty(RubyEncoding.Ascii);
            }

            var result = isBinary.HasValue && isBinary.Value ? 
                MutableString.CreateBinary(length, any.Encoding) :
                MutableString.CreateMutable(length, any.Encoding);

            for (int i = 0, n = parts.Count; i < n; i++) {
                var part = parts[i];

                if (separator != null && i > 0) {
                    result.Append(separator);
                }

                if (part != null) {
                    result.Append(part);
                    result.TaintBy(part);
                }
            }

            if (separator != null) {
                result.TaintBy(separator);
            }
            if (!result.IsTainted || !result.IsUntrusted) {
                result.TaintBy(self, conversions.Context);
            }
            return result;
        }

        [RubyMethod("join")]
        public static MutableString/*!*/ Join(JoinConversionStorage/*!*/ conversions, IList/*!*/ self) {
            return Join(conversions, self, DefaultSeparator(conversions.Context));
        }

        /// <summary>
        /// #join with no separator, or with an explicit nil, falls back to $, - and warns about it,
        /// because a global that silently changes what every #join in the program does is a trap.
        /// </summary>
        private static MutableString DefaultSeparator(RubyContext/*!*/ context) {
            var separator = context.ItemSeparator;
            if (separator != null) {
                context.ReportDeprecationWarning("$, is set to non-nil value");
            }
            return separator;
        }

        [RubyMethod("join")]
        public static MutableString/*!*/ JoinWithLazySeparatorConversion(
            JoinConversionStorage/*!*/ conversions, 
            ConversionStorage<MutableString>/*!*/ toStr,
            IList/*!*/ self, object separator) {

            // An empty array joins to an empty string without touching the separator - MRI does
            // not ask it for #to_str - but the $, warning belongs to the call rather than to the
            // work, so [].join(nil) still warns.
            if (self.Count == 0) {
                if (separator == null) {
                    DefaultSeparator(conversions.Context);
                }
                return MutableString.CreateEmpty(RubyEncoding.Ascii);
            }

            return Join(conversions, self,
                separator != null ? Protocols.CastToString(toStr, separator) : DefaultSeparator(conversions.Context));
        }

        [RubyMethod("to_s")]
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, IList/*!*/ self) {

            using (IDisposable handle = RubyUtils.InfiniteInspectTracker.TrackObject(self)) {
                if (handle == null) {
                    return MutableString.CreateAscii("[...]");
                }
                // MRI starts the result off as US-ASCII and takes the encoding of the first
                // element's inspection, so an empty array inspects as US-ASCII rather than as
                // binary, and ["\u3042"] keeps the element's encoding. US-ASCII gives way to any
                // other encoding, ASCII only content or not, which is how an array of strings
                // inspects in the encoding the answer is going to be read in.
                MutableString str = MutableString.CreateMutable(RubyEncoding.Ascii);
                str.Append('[');
                bool first = true;
                foreach (object obj in self) {
                    if (first) {
                        first = false;
                    } else {
                        str.Append(", ");
                    }
                    var item = context.Inspect(obj);
                    if (str.Encoding == RubyEncoding.Ascii && item.Encoding != RubyEncoding.Ascii) {
                        str.ForceEncoding(item.Encoding);
                    }
                    str.Append(item);
                }
                str.Append(']');
                return str;
            }
        }

        #endregion

        #region length, size, empty?, nitems

        [RubyMethod("length")]
        [RubyMethod("size")]
        public static int Length(IList/*!*/ self) {
            return self.Count;
        }

        // #count is not a synonym for #size: it also takes a value to compare against or a
        // block to test with. Registering Length under that name shadowed Enumerable#count
        // altogether, so [1, 2, 2].count(2) raised "wrong number of arguments".
        [RubyMethod("count")]
        public static int Count(IList/*!*/ self) {
            return self.Count;
        }

        [RubyMethod("count")]
        public static int Count(BinaryOpStorage/*!*/ equals, BlockParam block, IList/*!*/ self, object value) {
            if (block != null) {
                equals.Context.ReportWarning("given block not used");
            }

            int result = 0;
            for (int i = 0; i < self.Count; i++) {
                if (Protocols.IsEqual(equals, self[i], value)) {
                    result++;
                }
            }
            return result;
        }

        [RubyMethod("count")]
        public static object Count([NotNull]BlockParam/*!*/ block, IList/*!*/ self) {
            int result = 0;
            // The size is re-read every step, so a block that appends keeps being called.
            for (int i = 0; i < self.Count; i++) {
                object blockResult;
                if (block.Yield(self[i], out blockResult)) {
                    return blockResult;
                }
                if (Protocols.IsTrue(blockResult)) {
                    result++;
                }
            }
            return result;
        }

        // "none?" was aliased to "empty?" here, which is not what it means: [nil].none? is true
        // and [1].none? is false, and it takes a block or a pattern. Enumerable#none? does all of
        // that, and this definition only shadowed it.
        [RubyMethod("empty?")]
        public static bool Empty(IList/*!*/ self) {
            return self.Count == 0;
        }

        [RubyMethod("nitems")]
        public static int NumberOfNonNilItems(IList/*!*/ self) {
            int count = 0;
            foreach (object obj in self) {
                if (obj != null) {
                    count++;
                }
            }
            return count;
        }

        #endregion

        #region insert, push, pop, shift, unshift, <<

        [RubyMethod("insert")]
        public static IList/*!*/ Insert(IList/*!*/ self, [DefaultProtocol]int index, params object[]/*!*/ args) {
            if (args.Length == 0) {
                var array = self as RubyArray;
                if (array != null) {
                    array.RequireNotFrozen();
                }
                return self;
            }

            if (index == -1) {
                AddRange(self, args);
                return self;
            }

            int originalIndex = index;
            index = index < 0 ? index + self.Count + 1 : index;
            if (index < 0) {
                // MRI: [1,2].insert(-5, 3) => "index -5 too small for array; minimum: -3"
                throw RubyExceptions.CreateIndexError("index {0} too small for array; minimum: {1}", originalIndex, -self.Count - 1);
            }

            if (index >= self.Count) {
                ExpandList(self, index);
                AddRange(self, args);
                return self;
            }

            InsertRange(self, index, args, 0, args.Length);
            return self;
        }

        [RubyMethod("push")]
        public static IList/*!*/ Push(IList/*!*/ self, params object[]/*!*/ values) {
            AddRange(self, values);
            return self;
        }

        [RubyMethod("pop")]
        public static object Pop(IList/*!*/ self) {
            // MRI checks the frozen flag before it checks for an empty array, so popping
            // nothing off a frozen [] still raises.
            RequireNotFrozen(self);
            if (self.Count == 0) {
                return null;
            }

            object result = self[self.Count - 1];
            self.RemoveAt(self.Count - 1);
            return result;
        }

        [RubyMethod("pop")]
        public static object Pop(RubyContext/*!*/ context, IList/*!*/ self, [DefaultProtocol]int count) {
            RequireNotFrozen(self);

            if (count < 0) {
                throw RubyExceptions.CreateArgumentError("negative array size");
            }

            if (count == 0 || self.Count == 0) {
                return new RubyArray();
            }

            var normalizedCount = count <= self.Count ? count : self.Count;
            var index = self.Count - normalizedCount;

            var result = new RubyArray(self, index, normalizedCount);
            IListOps.RemoveRange(self, index, normalizedCount);
            return result;
        }

        [RubyMethod("shift")]
        public static object Shift(IList/*!*/ self) {
            RequireNotFrozen(self);
            if (self.Count == 0) {
                return null;
            }

            object result = self[0];
            self.RemoveAt(0);
            return result;
        }

        [RubyMethod("unshift")]
        public static IList/*!*/ Unshift(IList/*!*/ self, object/*!*/ arg) {
            self.Insert(0, arg);
            return self;
        }

        [RubyMethod("unshift")]
        public static IList/*!*/ Unshift(IList/*!*/ self, params object[]/*!*/ args) {
            InsertRange(self, 0, args, 0, args.Length);
            return self;
        }

        [RubyMethod("<<")]
        public static IList/*!*/ Append(IList/*!*/ self, object value) {
            self.Add(value);
            return self;
        }

        #endregion

        #region slice!

        [RubyMethod("slice!")]
        public static object SliceInPlace(IList/*!*/ self, [DefaultProtocol]int index) {
            RequireNotFrozen(self);
            index = index < 0 ? index + self.Count : index;
            if (index >= 0 && index < self.Count) {
                object result = self[index];
                self.RemoveAt(index);
                return result;
            } else {
                return null;
            }
        }

        [RubyMethod("slice!")]
        public static IList SliceInPlace(ConversionStorage<int>/*!*/ fixnumCast, UnaryOpStorage/*!*/ allocateStorage,
            IList/*!*/ self, [NotNull]Range/*!*/ range) {

            RequireNotFrozen(self);
            int start, count;
            if (!NormalizeRange(fixnumCast, self.Count, range, out start, out count)) {
                // MRI asks for the range with errors off here, so a range that starts
                // before the array answers nil rather than raising RangeError.
                return null;
            }
            return CutOut(allocateStorage, self, start, count);
        }

        [RubyMethod("slice!")]
        public static IList SliceInPlace(UnaryOpStorage/*!*/ allocateStorage,
            IList/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol]int length) {

            RequireNotFrozen(self);
            return CutOut(allocateStorage, self, start, length);
        }

        /// <summary>
        /// Removes and returns a stretch of the list. MRI's #slice! never grows the receiver
        /// and never raises for an index it cannot use: a negative length, a start before the
        /// first element or one past the last simply answers nil and leaves the list alone.
        /// </summary>
        private static IList CutOut(UnaryOpStorage/*!*/ allocateStorage, IList/*!*/ self, int start, int length) {
            if (length < 0) {
                return null;
            }

            if (start < 0) {
                start += self.Count;
                if (start < 0) {
                    return null;
                }
            } else if (start > self.Count) {
                return null;
            }

            if (start + length > self.Count) {
                length = self.Count - start;
            }

            IList result = GetResultRange(allocateStorage, self, start, length);
            if (length > 0) {
                RemoveRange(self, start, length);
            }
            return result;
        }

        #endregion

        #region sort, sort!

        [RubyMethod("sort")]
        public static object Sort(UnaryOpStorage/*!*/ allocateStorage, ComparisonStorage/*!*/ comparisonStorage, BlockParam block, IList/*!*/ self) {
            // TODO: this is not optimal because it makes an extra array copy
            // (only affects sorting of .NET types, do we need our own quicksort?)
            IList resultList = CreateResultArray(allocateStorage, self);
            StrongBox<object> breakResult;
            RubyArray result = ArrayOps.SortInPlace(comparisonStorage, block, ToArray(self), out breakResult);
            if (breakResult == null) {
                Replace(resultList, result);
                return resultList;
            } else {
                return breakResult.Value;
            }
        }

        [RubyMethod("sort!")]
        public static object SortInPlace(ComparisonStorage/*!*/ comparisonStorage, BlockParam block, IList/*!*/ self) {
            // this should always call ArrayOps.SortInPlace instead
            Debug.Assert(!(self is RubyArray));
            RequireNotFrozen(self);

            // TODO: this is not optimal because it makes an extra array copy
            // (only affects sorting of .NET types, do we need our own quicksort?)
            StrongBox<object> breakResult;
            RubyArray result = ArrayOps.SortInPlace(comparisonStorage, block, ToArray(self), out breakResult);
            if (breakResult == null) {
                Replace(self, result);
                return self;
            } else {
                return breakResult.Value;
            }
        }

        #endregion

        #region shuffle, shuffle!

        [RubyMethod("shuffle")]
        public static IList/*!*/ Shuffle(UnaryOpStorage/*!*/ allocateStorage, RubyArray/*!*/ self) {
            IList result = CreateResultArray(allocateStorage, self);
            if (self.Count == 0) {
                return result;
            }

            RubyArray array = result as RubyArray;
            if (array != null && array.Count < self.Count) {
                array.AddCapacity(self.Count - array.Count);
            }

            var generator = allocateStorage.Context.RandomNumberGenerator;

            result.Add(self[0]);
            for (int i = 1; i < self.Count; i++) {
                int j = generator.Next(i + 1);
                result.Add((j < result.Count) ? result[j] : null);
                result[j] = self[i];
            }

            return result;
        }

        [RubyMethod("shuffle!")]
        public static RubyArray/*!*/ ShuffleInPlace(RubyContext/*!*/ context, RubyArray/*!*/ self) {
            var generator = context.RandomNumberGenerator;
            for (int i = self.Count - 1; i >= 0; i--) {
                int j = generator.Next(i + 1);
                object value = self[i];
                self[i] = self[j];
                self[j] = value;
            }
            return self;
        }

        #endregion

        #region reverse, reverse!, transpose, uniq, uniq!

        [RubyMethod("reverse")]
        public static IList/*!*/ Reverse(UnaryOpStorage/*!*/ allocateStorage, IList/*!*/ self) {
            IList reversedList = CreateResultArray(allocateStorage, self);
            if (reversedList is RubyArray) {
                (reversedList as RubyArray).AddCapacity(self.Count);
            }
            for (int i = 0; i < self.Count; i++) {
                reversedList.Add(self[self.Count - i - 1]);
            }
            return reversedList;
        }

        [RubyMethod("reverse!")]
        public static IList/*!*/ InPlaceReverse(IList/*!*/ self) {
            int stop = self.Count / 2;
            int last = self.Count - 1;
            for (int i = 0; i < stop; i++) {
                int swap = last - i;
                object t = self[i];
                self[i] = self[swap];
                self[swap] = t;
            }
            return self;
        }

        [RubyMethod("transpose")]
        public static RubyArray/*!*/ Transpose(ConversionStorage<IList>/*!*/ arrayCast, IList/*!*/ self) {
            // Get the arrays. Note we need to check length as we go, so we call to_ary on all the
            // arrays we encounter before the error (if any).
            RubyArray result = new RubyArray();
            for (int i = 0; i < self.Count; i++) {
                IList list = Protocols.CastToArray(arrayCast, self[i]);

                if (i == 0) {
                    // initialize the result
                    result.AddCapacity(list.Count);
                    for (int j = 0; j < list.Count; j++) {
                        result.Add(new RubyArray());
                    }
                } else if (list.Count != result.Count) {
                    throw RubyExceptions.CreateIndexError("element size differs ({0} should be {1})", list.Count, result.Count);
                }

                // add items
                Debug.Assert(list.Count == result.Count);
                for (int j = 0; j < result.Count; j++) {
                    ((RubyArray)result[j]).Add(list[j]);
                }
            }

            return result;
        }

        [RubyMethod("uniq")]
        public static IList/*!*/ Unique(UnaryOpStorage/*!*/ allocateStorage, IList/*!*/ self) {
            IList result = CreateResultArray(allocateStorage, self);

            var seen = new Dictionary<object, bool>(allocateStorage.Context.EqualityComparer);
            bool nilSeen = false;
            
            AddUniqueItems(self, result, seen, ref nilSeen);
            return result;
        }

        [RubyMethod("uniq!")]
        public static IList UniqueSelf(UnaryOpStorage/*!*/ hashStorage, BinaryOpStorage/*!*/ eqlStorage, RubyArray/*!*/ self) {
            self.RequireNotFrozen();
            return UniqueSelf(hashStorage, eqlStorage, (IList)self);
        }

        [RubyMethod("uniq!")]
        public static IList UniqueSelf(UnaryOpStorage/*!*/ hashStorage, BinaryOpStorage/*!*/ eqlStorage, IList/*!*/ self) {
            var seen = new Dictionary<object, bool>(new EqualityComparer(hashStorage, eqlStorage));
            bool nilSeen = false;
            bool modified = false;
            int i = 0;
            while (i < self.Count) {
                object key = self[i];
                if (key != null && !seen.ContainsKey(key)) {
                    seen.Add(key, true);
                    i++;
                } else if (key == null && !nilSeen) {
                    nilSeen = true;
                    i++;
                } else {
                    self.RemoveAt(i);
                    modified = true;
                }
            }

            return modified ? self : null;
        }

        #endregion

        #region permutation, combination

        internal sealed class PermutationEnumerator : IEnumerator {
            private struct State {
                public readonly int i, j;
                public State(int i, int j) { this.i = i; this.j = j; }
            }

            private readonly IList/*!*/ _list;
            private readonly int? _size;

            public PermutationEnumerator(IList/*!*/ list, int? size) {
                _size = size;
                _list = list;
            }

            //
            // rec = lambda do |i|
            //   # State "j < -1"
            //   if i == result.length
            //     yield result.dup
            //     return
            //   end
            //
            //   j = i
            //   while j < values.length
            //     values[j], values[i] = values[i], values[j]
            //     result[i] = values[i]      
            //     rec.(i + 1)
            //     # State "j >= 0"
            //     j += 1
            //   end
            //
            //   while j > i
            //     j -= 1
            //     values[j], values[i] = values[i], values[j] 
            //   end
            // end
            //
            // rec.(0)
            //
            public object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block) {
                int size = _size ?? _list.Count;
                if (size < 0 || size > _list.Count) {
                    return _list;
                }

                var result = new object[size];
                var values = new object[_list.Count];
                _list.CopyTo(values, 0);
                var stack = new Stack<State>();
                stack.Push(new State(0, -1));

                while (stack.Count > 0) {
                    var entry = stack.Pop();
                    int i = entry.i;
                    int j = entry.j;

                    if (j < 0) {
                        if (i == result.Length) {
                            object blockResult;
                            if (block.Yield(RubyOps.MakeArrayN(result), out blockResult)) {
                                return blockResult;
                            }
                        } else {
                            result[i] = values[i];
                            stack.Push(new State(i, i));
                            stack.Push(new State(i + 1, -1));
                        }
                    } else {
                        j++;
                        if (j == values.Length) {
                            while (j > i) {
                                j--;
                                Xchg(values, i, j);
                            }
                        } else {
                            Xchg(values, i, j);
                            result[i] = values[i];
                            stack.Push(new State(i, j));
                            stack.Push(new State(i + 1, -1));
                        }
                    }
                }
                return _list;
            }

            private static void Xchg(object[]/*!*/ values, int i, int j) {
                object item = values[j];
                values[j] = values[i];
                values[i] = item;
            }
        }
        
        internal sealed class CombinationEnumerator : IEnumerator {
            private struct State {
                public readonly int i, j;
                public readonly bool init;
                public State(int i, int j, bool init) { this.i = i; this.j = j; this.init = init; }
            }

            private readonly IList/*!*/ _list;
            private readonly int? _size;

            public CombinationEnumerator(IList/*!*/ list, int? size) {
                _size = size;
                _list = list;
            }

            //   
            // rec = lambda do |i,j|
            //   # State "init"
            //   if j == result.length
            //     yield result.dup
            //     return
            //   end
            //   
            //   while i <= values.length - result.length + j
            //     result[j] = values[i]
            //     rec.(i + 1, j + 1)
            //     # State "!init"
            //     i += 1      
            //   end
            // end
            //   
            // rec.(0, 0)
            // 
            public object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block) {
                int size = _size ?? _list.Count;
                if (size < 0 || size > _list.Count) {
                    return _list;
                }
                var result = new object[size];
                var values = new object[_list.Count];
                _list.CopyTo(values, 0);
                var stack = new Stack<State>();
                stack.Push(new State(0, 0, true));

                while (stack.Count > 0) {
                    var entry = stack.Pop();
                    int i = entry.i;
                    int j = entry.j;

                    if (entry.init && j == result.Length) {
                        object blockResult;
                        if (block.Yield(RubyOps.MakeArrayN(result), out blockResult)) {
                            return blockResult;
                        }
                    } else {
                        if (!entry.init) {
                            i++;
                        }
                        if (i <= values.Length - result.Length + j) {
                            result[j] = values[i];
                            stack.Push(new State(i, j, false));
                            stack.Push(new State(i + 1, j + 1, true));
                        }
                    }
                }
                return _list;
            }
        }

        /// <summary>
        /// The optional length is converted with the usual Integer protocol - it was being taken as
        /// "no length given" whenever it was not already a Fixnum, so #combination("2") silently
        /// behaved like #combination.
        /// </summary>
        private static int? PermutationLength(ConversionStorage<int>/*!*/ fixnumCast, object size) {
            return (size == Missing.Value) ? (int?)null : Protocols.CastToFixnum(fixnumCast, size);
        }

        [RubyMethod("permutation")]
        public static object GetPermutations(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, IList/*!*/ self,
            [Optional]object size) {

            int? length = PermutationLength(fixnumCast, size);
            var enumerator = new PermutationEnumerator(self, length);
            if (block == null) {
                // How many permutations there will be is arithmetic, not iteration, so the
                // Enumerator can answer #size without running: n!/(n-k)!, and none at all when the
                // requested length cannot be taken from the array.
                return new Enumerator(enumerator) { SizeSource = self, SizeOp = "permutation", SizeArg = length };
            }

            return enumerator.Each(null, block);
        }
        
        [RubyMethod("combination")]
        public static object GetCombinations(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, IList/*!*/ self,
            object size) {

            int? length = PermutationLength(fixnumCast, size);
            var enumerator = new CombinationEnumerator(self, length);
            if (block == null) {
                return new Enumerator(enumerator) { SizeSource = self, SizeOp = "combination", SizeArg = length };
            }

            return enumerator.Each(null, block);
        }

        #endregion

        #region min, max, sort_by, each_with_object, zip

        // MRI gives Array its own #min, #max, #sort_by, #each_with_object and #zip rather than
        // letting it inherit Enumerable's. Enumerable's are written against #each, so every
        // element costs a Proc invocation, a BlockParam and an argument-packing array; walking
        // the list directly is what makes these two orders of magnitude faster.

        [RubyMethod("min")]
        public static object GetMinimum(ComparisonStorage/*!*/ comparisonStorage, BlockParam comparer, IList/*!*/ self) {
            return GetExtreme(comparisonStorage, comparer, self, -1);
        }

        [RubyMethod("min")]
        public static object GetMinimum(ConversionStorage<int>/*!*/ fixnumCast, ComparisonStorage/*!*/ comparisonStorage,
            BlockParam comparer, IList/*!*/ self, object count) {
            return GetExtremes(fixnumCast, comparisonStorage, comparer, self, count, -1);
        }

        [RubyMethod("max")]
        public static object GetMaximum(ComparisonStorage/*!*/ comparisonStorage, BlockParam comparer, IList/*!*/ self) {
            return GetExtreme(comparisonStorage, comparer, self, +1);
        }

        [RubyMethod("max")]
        public static object GetMaximum(ConversionStorage<int>/*!*/ fixnumCast, ComparisonStorage/*!*/ comparisonStorage,
            BlockParam comparer, IList/*!*/ self, object count) {
            return GetExtremes(fixnumCast, comparisonStorage, comparer, self, count, +1);
        }

        private static object GetExtreme(ComparisonStorage/*!*/ comparisonStorage, BlockParam comparer, IList/*!*/ self,
            int comparisonValue) {

            if (self.Count == 0) {
                return null;
            }

            object result = self[0];
            for (int i = 1; i < self.Count; i++) {
                object item = self[i];
                int compareResult;
                if (comparer != null) {
                    object blockResult;
                    if (comparer.Yield(item, result, out blockResult)) {
                        return blockResult;
                    }
                    if (blockResult == null) {
                        throw RubyExceptions.MakeComparisonError(comparisonStorage.Context, item, result);
                    }
                    compareResult = Protocols.ConvertCompareResult(comparisonStorage, blockResult);
                } else {
                    // MRI asks the current extreme how it compares to the candidate, so
                    // [1, nil].max fails as "comparison of Integer with nil"
                    compareResult = -Protocols.Compare(comparisonStorage, result, item);
                }

                if (compareResult == comparisonValue) {
                    result = item;
                }
            }
            return result;
        }

        // min(n) / max(n): the n smallest (largest) elements, as an Array, smallest (largest) first.
        private static object GetExtremes(ConversionStorage<int>/*!*/ fixnumCast, ComparisonStorage/*!*/ comparisonStorage,
            BlockParam comparer, IList/*!*/ self, object count, int comparisonValue) {

            if (count == null) {
                return GetExtreme(comparisonStorage, comparer, self, comparisonValue);
            }

            int n = Protocols.CastToFixnum(fixnumCast, count);
            if (n < 0) {
                throw RubyExceptions.CreateArgumentError("negative size ({0})", n);
            }

            StrongBox<object> breakResult;
            RubyArray sorted = ArrayOps.SortInPlace(comparisonStorage, comparer, ToArray(self), out breakResult);
            if (breakResult != null) {
                return breakResult.Value;
            }

            if (n > sorted.Count) {
                n = sorted.Count;
            }
            var result = new RubyArray(n);
            for (int i = 0; i < n; i++) {
                result.Add(sorted[comparisonValue > 0 ? sorted.Count - 1 - i : i]);
            }
            return result;
        }

        [RubyMethod("sort_by")]
        public static Enumerator/*!*/ GetSortByEnumerator(IList/*!*/ self) {
            return new Enumerator(self, "sort_by") { SizeSource = self, SizeOp = "same" };
        }

        [RubyMethod("sort_by")]
        public static object SortBy(ComparisonStorage/*!*/ comparisonStorage, [NotNull]BlockParam/*!*/ keySelector, IList/*!*/ self) {
            // self.Count is re-read on every step: MRI keeps iterating into elements the block
            // appends to the receiver (spec/core/array/shared/iterable_and_tolerating_size_increasing).
            var itemList = new List<object>(self.Count);
            var keyList = new List<object>(self.Count);

            for (int i = 0; i < self.Count; i++) {
                object item = self[i];
                itemList.Add(item);

                object key;
                if (keySelector.Yield(item, out key)) {
                    return key;
                }
                keyList.Add(key);
            }

            int count = itemList.Count;
            object[] items = itemList.ToArray();
            object[] keys = keyList.ToArray();
            MergeSortByKey(keys, items, new object[count], new object[count], 0, count, comparisonStorage);

            var result = new RubyArray(count);
            for (int i = 0; i < count; i++) {
                result.Add(items[i]);
            }
            return result;
        }

        /// <summary>
        /// Stable merge sort of <paramref name="items"/> by the parallel <paramref name="keys"/>.
        /// Stable, so equal keys keep their input order - which is what MRI's #sort_by does, and
        /// what its #min_by(n)/#max_by(n) rely on.
        /// </summary>
        private static void MergeSortByKey(object[]/*!*/ keys, object[]/*!*/ items, object[]/*!*/ keyBuffer, object[]/*!*/ itemBuffer,
            int start, int length, ComparisonStorage/*!*/ comparisonStorage) {

            if (length < 2) {
                return;
            }

            int half = length / 2;
            MergeSortByKey(keys, items, keyBuffer, itemBuffer, start, half, comparisonStorage);
            MergeSortByKey(keys, items, keyBuffer, itemBuffer, start + half, length - half, comparisonStorage);

            int left = start, right = start + half, target = start;
            int leftEnd = start + half, rightEnd = start + length;
            while (left < leftEnd && right < rightEnd) {
                if (Protocols.Compare(comparisonStorage, keys[right], keys[left]) < 0) {
                    keyBuffer[target] = keys[right];
                    itemBuffer[target++] = items[right++];
                } else {
                    keyBuffer[target] = keys[left];
                    itemBuffer[target++] = items[left++];
                }
            }
            while (left < leftEnd) {
                keyBuffer[target] = keys[left];
                itemBuffer[target++] = items[left++];
            }
            while (right < rightEnd) {
                keyBuffer[target] = keys[right];
                itemBuffer[target++] = items[right++];
            }

            Array.Copy(keyBuffer, start, keys, start, length);
            Array.Copy(itemBuffer, start, items, start, length);
        }

        [RubyMethod("each_with_object")]
        public static Enumerator/*!*/ GetEachWithObjectEnumerator(IList/*!*/ self, object memo) {
            return new Enumerator(self, "each_with_object", memo) { SizeSource = self, SizeOp = "same" };
        }

        [RubyMethod("each_with_object")]
        public static object EachWithObject([NotNull]BlockParam/*!*/ block, IList/*!*/ self, object memo) {
            for (int i = 0; i < self.Count; i++) {
                object result;
                if (block.Yield(self[i], memo, out result)) {
                    return result;
                }
            }
            return memo;
        }

        [RubyMethod("zip")]
        public static object Zip(BlockParam block, IList/*!*/ self, [DefaultProtocol, NotNullItems]params IList/*!*/[]/*!*/ others) {
            int count = self.Count;
            RubyArray results = (block == null) ? new RubyArray(count) : null;

            // MRI re-reads the length on every step of the block form, so a block that shrinks the
            // receiver cuts the iteration short; the array form fixes it up front.
            for (int i = 0; i < (block != null ? self.Count : count); i++) {
                var tuple = new RubyArray(others.Length + 1);
                tuple.Add(i < self.Count ? self[i] : null);
                for (int j = 0; j < others.Length; j++) {
                    IList other = others[j];
                    tuple.Add(i < other.Count ? other[i] : null);
                }

                if (block != null) {
                    object blockResult;
                    if (block.Yield(tuple, out blockResult)) {
                        return blockResult;
                    }
                } else {
                    results.Add(tuple);
                }
            }

            return results;
        }

        #endregion
    }
}

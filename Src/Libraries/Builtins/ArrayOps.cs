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
using System.Runtime.InteropServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Numerics;

namespace IronRuby.Builtins {
    
    /// <summary>
    /// Array inherits from Object, mixes in Enumerable.
    /// Ruby array is basically List{object}.
    /// </summary>
    [RubyClass("Array", Extends = typeof(RubyArray), Inherits = typeof(object)), Includes(typeof(IList), Copy = true)]
    public static class ArrayOps {

        #region Constructors

        // MRI defines new on the class itself (String.singleton_methods(false) includes it); it
        // does exactly what Class#new does.
        [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ New() {
            return new RuleGenerator(RuleGenerators.InstanceConstructor);
        }

        [RubyConstructor]
        public static RubyArray/*!*/ CreateArray(BlockParam block, RubyClass/*!*/ self) {
            WarnUnusedBlock(block, self.Context);
            return new RubyArray();
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ Reinitialize(RubyContext/*!*/ context, BlockParam block, RubyArray/*!*/ self) {
            WarnUnusedBlock(block, context);
            self.Clear();
            return self;
        }

        /// <summary>
        /// Array.new takes a block to fill the array with, so a block passed alongside no size at
        /// all is a mistake worth warning about - MRI does.
        /// </summary>
        private static void WarnUnusedBlock(BlockParam block, RubyContext/*!*/ context) {
            if (block != null) {
                context.ReportWarning("given block not used");
            }
        }

        [RubyConstructor]
        public static object CreateArray(ConversionStorage<Union<IList, int>>/*!*/ toAryToInt,
            BlockParam block, RubyClass/*!*/ self, [NotNull]object/*!*/ arrayOrSize) {

            if (arrayOrSize is BigInteger || arrayOrSize is long) {
                // A size too large for an Int32 is a size no array can have, and MRI says so as a
                // size error rather than as a number that will not fit in a machine word.
                return new RubyArray().AddMultiple(CheckArraySize(ToBigInteger(arrayOrSize)), null);
            }

            var site = toAryToInt.GetSite(CompositeConversionAction.Make(toAryToInt.Context, CompositeConversion.ToAryToInt));
            var union = site.Target(site, arrayOrSize);


            if (union.First != null) {
                // block ignored
                // TODO: implement copy-on-write
                return new RubyArray(union.First);
            } else if (block != null) {
                return CreateArray(block, union.Second);
            } else {
                return CreateArray(self, union.Second, null);
            }
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static object Reinitialize(ConversionStorage<Union<IList, int>>/*!*/ toAryToInt,
            BlockParam block, RubyArray/*!*/ self, [NotNull]object/*!*/ arrayOrSize) {

            var context = toAryToInt.Context;

            if (arrayOrSize is BigInteger || arrayOrSize is long) {
                return ReinitializeByRepeatedValue(context, self, ToBigInteger(arrayOrSize), null);
            }

            var site = toAryToInt.GetSite(CompositeConversionAction.Make(context, CompositeConversion.ToAryToInt));
            var union = site.Target(site, arrayOrSize);
            
            if (union.First != null) {
                // block ignored
                return Reinitialize(self, union.First);
            } else if (block != null) {
                return Reinitialize(block, self, union.Second);
            } else {
                return ReinitializeByRepeatedValue(context, self, union.Second, null);
            }
        }

        private static RubyArray/*!*/ Reinitialize(RubyArray/*!*/ self, IList/*!*/ other) {
            Assert.NotNull(self, other);
            if (other != self) {
                self.Clear();
                IListOps.AddRange(self, other);
            }
            return self;
        }

        private static object CreateArray(BlockParam/*!*/ block, int size) {
            return Reinitialize(block, new RubyArray(), size);
        }

        [RubyConstructor]
        public static RubyArray/*!*/ CreateArray(BlockParam/*!*/ block, RubyClass/*!*/ self, [DefaultProtocol]int size, object value) {
            return Reinitialize(block, new RubyArray(), size, value);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ Reinitialize(BlockParam/*!*/ block, RubyArray/*!*/ self, int size, object value) {
            block.RubyContext.ReportWarning("block supersedes default value argument");
            Reinitialize(block, self, size);
            return self;
        }

        private static object Reinitialize(BlockParam/*!*/ block, RubyArray/*!*/ self, int size) {
            CheckArraySize(size); 
            self.Clear();
            for (int i = 0; i < size; i++) {
                object item;
                if (block.Yield(i, out item)) {
                    return item;
                }
                self.Add(item);
            }

            return self;
        }

        // Array.new(nil) is a size that is not an Integer, not a missing array.
        [RubyConstructor]
        public static RubyArray/*!*/ CreateArray(RubyClass/*!*/ self, DynamicNull size) {
            throw RubyExceptions.CreateImplicitConversionError("nil", "Integer");
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ Reinitialize(RubyArray/*!*/ self, DynamicNull size) {
            throw RubyExceptions.CreateImplicitConversionError("nil", "Integer");
        }

        [RubyConstructor]
        public static RubyArray/*!*/ CreateArray(RubyClass/*!*/ self, [DefaultProtocol]int size, object value) {
            CheckArraySize(size); 
            return new RubyArray().AddMultiple(size, value);
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ ReinitializeByRepeatedValue(RubyContext/*!*/ context, RubyArray/*!*/ self, [DefaultProtocol]int size, object value) {
            CheckArraySize(size);
            self.Clear();
            self.AddMultiple(size, value);

            return self;
        }

        /// <summary>An Integer size as a BigInteger, whichever CLR type happens to carry it.</summary>
        private static BigInteger ToBigInteger(object/*!*/ size) {
            return size is long ? new BigInteger((long)size) : (BigInteger)size;
        }

        private static void CheckArraySize(int size) {
            if (size < 0) {
                throw RubyExceptions.CreateArgumentError("negative array size");
            }

            if (size > Int32.MaxValue / 8) {
                throw RubyExceptions.CreateArgumentError("array size too big");
            }
        }

        /// <summary>
        /// A size too large for an Int32 is still a size, and one no array can have: MRI answers
        /// the same ArgumentError it gives for one merely too large to allocate, rather than
        /// complaining that the number will not fit in a machine word.
        /// </summary>
        private static int CheckArraySize(BigInteger size) {
            if (size.Sign < 0) {
                throw RubyExceptions.CreateArgumentError("negative array size");
            }
            if (size > Int32.MaxValue / 8) {
                throw RubyExceptions.CreateArgumentError("array size too big");
            }
            return (int)size;
        }

        [RubyConstructor]
        public static RubyArray/*!*/ CreateArray(RubyClass/*!*/ self, [NotNull]BigInteger/*!*/ size, object value) {
            return new RubyArray().AddMultiple(CheckArraySize(size), value);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ ReinitializeByRepeatedValue(RubyContext/*!*/ context, RubyArray/*!*/ self,
            [NotNull]BigInteger/*!*/ size, object value) {

            int count = CheckArraySize(size);
            self.Clear();
            self.AddMultiple(count, value);

            return self;
        }

        [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ MakeArray(RubyClass/*!*/ self, params object[] args) {
            // neither "new" nor "initialize" is called:
            RubyArray result = RubyArray.CreateInstance(self);
            foreach (object obj in args) {
                result.Add(obj);
            }
            return result;
        }

        #endregion

        #region to_a, to_ary, try_convert

        [RubyMethod("to_a")]
        public static RubyArray/*!*/ ToArray(RubyArray/*!*/ self) {
            return self is RubyArray.Subclass ? new RubyArray(self) : self;
        }

        [RubyMethod("to_ary")]
        public static RubyArray/*!*/ ToExplicitArray(RubyArray/*!*/ self) {
            return self;
        }

        [RubyMethod("try_convert", RubyMethodAttributes.PublicSingleton)]
        public static IList TryConvert(ConversionStorage<IList>/*!*/ toAry, RubyClass/*!*/ self, object obj) {
            var site = toAry.GetSite(TryConvertToArrayAction.Make(toAry.Context));
            return site.Target(site, obj);
        }

        #endregion

        #region pack

        [RubyMethod("pack")]
        public static MutableString/*!*/ Pack(
            ConversionStorage<IntegerValue>/*!*/ integerConversion,
            ConversionStorage<double>/*!*/ floatConversion,
            ConversionStorage<MutableString>/*!*/ stringCast,
            ConversionStorage<MutableString>/*!*/ tosConversion,
            RubyArray/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ format,
            [Keywords, DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            RubyContext context = integerConversion.Context;
            if (!format.Encoding.IsAsciiIdentity) {
                throw RubyExceptions.CreateArgumentError("ASCII incompatible encoding: {0}", format.Encoding.Name);
            }

            MutableString buffer = null;
            if (options != null) {
                foreach (var option in options) {
                    var key = option.Key as RubySymbol;
                    if (key == null || key.ToString() != "buffer") {
                        throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", context.Inspect(option.Key));
                    }
                    // buffer: nil is no buffer
                    if (option.Value == null) {
                        continue;
                    }
                    buffer = option.Value as MutableString;
                    if (buffer == null) {
                        throw RubyExceptions.CreateTypeError("buffer must be String, not {0}",
                            context.GetClassDisplayName(option.Value));
                    }
                    buffer.RequireNotFrozen();
                }
            }

            return RubyEncoder.Pack(integerConversion, floatConversion, stringCast, tosConversion, self, format, buffer);
        }

        #endregion

        #region sort!, sort

        private sealed class BreakException : Exception {
        }

        [RubyMethod("sort")]
        public static object Sort(ComparisonStorage/*!*/ comparisonStorage, BlockParam block, RubyArray/*!*/ self) {
            // Since Ruby 3.0 #sort hands back a plain Array, not the receiver's subclass.
            RubyArray result = new RubyArray();
            IListOps.Replace(result, self);
            return SortInPlace(comparisonStorage, block, result);
        }

        [RubyMethod("sort!")]
        public static object SortInPlace(ComparisonStorage/*!*/ comparisonStorage, BlockParam block, RubyArray/*!*/ self) {
            // An array with fewer than two elements is already sorted, but MRI still refuses
            // to sort a frozen one.
            self.RequireNotFrozen();
            StrongBox<object> breakResult;
            RubyArray result = SortInPlace(comparisonStorage, block, self, out breakResult);
            if (breakResult != null) {
                return breakResult.Value;
            } else {
                return result;
            }
        }

        public static RubyArray/*!*/ SortInPlace(ComparisonStorage/*!*/ comparisonStorage, RubyArray/*!*/ self) {
            StrongBox<object> breakResult;
            RubyArray result = SortInPlace(comparisonStorage, null, self, out breakResult);
            Debug.Assert(result != null && breakResult == null);
            return result;
        }

        internal static RubyArray SortInPlace(ComparisonStorage/*!*/ comparisonStorage, BlockParam block, RubyArray/*!*/ self, out StrongBox<object> breakResult) {
            breakResult = null;
            var context = comparisonStorage.Context;

            if (block == null) {
                if (!TrySortWithoutDispatch(context, self)) {
                    MergeSort(self, (x, y) => Protocols.Compare(comparisonStorage, x, y));
                }
            } else {
                object nonRefBreakResult = null;
                try {
                    MergeSort(self, (x, y) =>
                    {
                        object result = null;
                        if (block.Yield(x, y, out result)) {
                            nonRefBreakResult = result;
                            throw new BreakException();
                        }

                        if (result == null) {
                            throw RubyExceptions.MakeComparisonError(context, x, y);
                        }

                        return Protocols.ConvertCompareResult(comparisonStorage, result);
                    });
                } catch (BreakException) {
                    breakResult = new StrongBox<object>(nonRefBreakResult);
                    return null;
                }
            }

            return self;
        }

        /// <summary>
        /// sort without a block over an array of only Strings, only Fixnums or only Floats (no NaN) - the
        /// common case - compares the elements directly instead of dispatching #<=> for every pair, as
        /// long as the class's #<=> is still the built-in one and no element is an instance of a
        /// subclass or has a singleton class. The comparisons are the ones those #<=> make, so the
        /// order is the same; anything else goes the general way.
        /// </summary>
        private static bool TrySortWithoutDispatch(RubyContext/*!*/ context, RubyArray/*!*/ self) {
            int count = self.Count;
            // (#<=> called from a C# sort ignores refinements, as rb_funcall does in MRI's sort)
            if (count < 2) {
                return false;
            }

            object first = self[0];
            if (first != null && first.GetType() == typeof(MutableString)) {
                var stringClass = context.GetClass(typeof(MutableString));
                var keys = new StringKey[count];
                bool allAscii = true;
                for (int i = 0; i < count; i++) {
                    object item = self[i];
                    if (item == null || item.GetType() != typeof(MutableString) || context.GetImmediateClassOf(item) != stringClass) {
                        return false;
                    }
                    var str = (MutableString)item;
                    keys[i] = new StringKey(str);
                    allAscii = allAscii && str.IsAscii();
                }
                if (!HasBuiltinComparison(stringClass)) {
                    return false;
                }
                if (allAscii && TryMakeAsciiKeys(self, out AsciiStringKey[] asciiKeys)) {
                    SortByKeys(self, asciiKeys);
                } else {
                    SortByKeys(self, keys);
                }
                return true;
            }

            if (first is int) {
                var keys = new int[count];
                for (int i = 0; i < count; i++) {
                    object item = self[i];
                    if (!(item is int)) {
                        return false;
                    }
                    keys[i] = (int)item;
                }
                if (!HasBuiltinComparison(context.GetClassOf(first))) {
                    return false;
                }
                SortByKeys(self, keys);
                return true;
            }

            if (first is double) {
                var keys = new double[count];
                for (int i = 0; i < count; i++) {
                    object item = self[i];
                    if (!(item is double) || Double.IsNaN((double)item)) {
                        return false;
                    }
                    keys[i] = (double)item;
                }
                if (!HasBuiltinComparison(context.GetClassOf(first))) {
                    return false;
                }
                SortByKeys(self, keys);
                return true;
            }

            return false;
        }

        // A struct, so that the generic sort below is compiled for it and calls MutableString.CompareTo
        // directly; over the class itself it would share one body and dispatch every comparison.
        // The most characters the keys below copy, all elements together: past it the copy is not
        // worth what it saves, and the general comparison (which copies nothing) is used.
        private const long MaxAsciiKeyChars = 16 * 1024 * 1024;

        /// <summary>
        /// Two ASCII strings compare by their bytes whatever their encodings (String#<=>,
        /// MutableString.CompareTo), and their bytes are their UTF-16 code units: an ordinal compare
        /// of the CLR strings - vectorized, no encoding checks per pair - is that compare. Each
        /// element is copied, an element the array holds many times once per occurrence, so the copies
        /// are capped in total (MaxAsciiKeyChars): 1000 references to one 1 MB string sort the general way.
        /// </summary>
        private static bool TryMakeAsciiKeys(RubyArray/*!*/ self, out AsciiStringKey[] keys) {
            int count = self.Count;
            long total = 0;
            for (int i = 0; i < count; i++) {
                total += ((MutableString)self[i]).GetLength(); // chars or bytes: the same for ASCII
                if (total > MaxAsciiKeyChars) {
                    keys = null;
                    return false;
                }
            }

            keys = new AsciiStringKey[count];
            for (int i = 0; i < count; i++) {
                keys[i] = new AsciiStringKey(((MutableString)self[i]).ToString());
            }
            return true;
        }

        private struct AsciiStringKey : IComparable<AsciiStringKey> {
            private readonly string/*!*/ _value;
            public AsciiStringKey(string/*!*/ value) { _value = value; }
            public int CompareTo(AsciiStringKey other) { return String.CompareOrdinal(_value, other._value); }
        }

        private struct StringKey : IComparable<StringKey> {
            private readonly MutableString/*!*/ _value;
            public StringKey(MutableString/*!*/ value) { _value = value; }
            public int CompareTo(StringKey other) { return _value.CompareTo(other._value); }
        }

        // The same stable merge sort as MergeSort, over keys of one type compared by their own CompareTo
        // (what String#<=>, Integer#<=> and Float#<=> do for two of them), carrying the elements along.
        private static void SortByKeys<TKey>(RubyArray/*!*/ self, TKey[]/*!*/ keys) where TKey : IComparable<TKey> {
            int count = keys.Length;
            object[] items = new object[count];
            self.CopyTo(items, 0);
            MergeSortKeys(keys, items, new TKey[count], new object[count], 0, count);
            for (int i = 0; i < count; i++) {
                self[i] = items[i];
            }
        }

        private static void MergeSortKeys<TKey>(TKey[]/*!*/ keys, object[]/*!*/ items, TKey[]/*!*/ keyBuffer, object[]/*!*/ itemBuffer,
            int start, int length) where TKey : IComparable<TKey> {

            if (length <= 12) {
                // insertion sort, stable: an element moves left only past strictly greater ones
                for (int i = start + 1; i < start + length; i++) {
                    TKey key = keys[i];
                    object item = items[i];
                    int j = i - 1;
                    while (j >= start && key.CompareTo(keys[j]) < 0) {
                        keys[j + 1] = keys[j];
                        items[j + 1] = items[j];
                        j--;
                    }
                    keys[j + 1] = key;
                    items[j + 1] = item;
                }
                return;
            }

            int half = length / 2;
            MergeSortKeys(keys, items, keyBuffer, itemBuffer, start, half);
            MergeSortKeys(keys, items, keyBuffer, itemBuffer, start + half, length - half);

            int left = start, right = start + half, target = start;
            int leftEnd = start + half, rightEnd = start + length;
            while (left < leftEnd && right < rightEnd) {
                if (keys[right].CompareTo(keys[left]) < 0) {
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

        private static bool HasBuiltinComparison(RubyClass/*!*/ cls) {
            var method = cls.ResolveMethod("<=>", VisibilityContext.AllVisible);
            return method.Found && method.Info is RubyLibraryMethodInfo;
        }

        /// <summary>
        /// List&lt;T&gt;.Sort cannot be used here: it wraps whatever the comparer throws in an
        /// InvalidOperationException (so a Ruby exception or a block's break never arrives
        /// intact) and .NET 5 added a consistency check that raises ArgumentException when
        /// the comparer disagrees with itself - which a Ruby block is perfectly entitled to
        /// do.  A plain merge sort has neither problem and is stable into the bargain.
        /// </summary>
        private static void MergeSort(RubyArray/*!*/ self, Comparison<object>/*!*/ comparison) {
            int count = self.Count;
            if (count < 2) {
                return;
            }

            object[] items = new object[count];
            self.CopyTo(items, 0);
            MergeSortRange(items, new object[count], 0, count, comparison);

            for (int i = 0; i < count; i++) {
                self[i] = items[i];
            }
        }

        private static void MergeSortRange(object[]/*!*/ items, object[]/*!*/ buffer, int start, int length,
            Comparison<object>/*!*/ comparison) {

            if (length < 2) {
                return;
            }

            int half = length / 2;
            MergeSortRange(items, buffer, start, half, comparison);
            MergeSortRange(items, buffer, start + half, length - half, comparison);

            int left = start, right = start + half, target = start;
            int leftEnd = start + half, rightEnd = start + length;
            while (left < leftEnd && right < rightEnd) {
                // Take from the left unless the right is strictly smaller, which keeps equal
                // elements in their original order. The earlier element is the receiver of
                // the comparison, as in MRI: [1, "a"].sort asks 1 <=> "a", not "a" <=> 1.
                buffer[target++] = comparison(items[left], items[right]) <= 0 ? items[left++] : items[right++];
            }
            while (left < leftEnd) {
                buffer[target++] = items[left++];
            }
            while (right < rightEnd) {
                buffer[target++] = items[right++];
            }

            Array.Copy(buffer, start, items, start, length);
        }
        #endregion

        #region reverse!

        [RubyMethod("reverse!")]
        public static RubyArray/*!*/ InPlaceReverse(RubyContext/*!*/ context, RubyArray/*!*/ self) {
            self.Reverse();
            return self;
        }

        #endregion

        #region sum

        /// <summary>
        /// Array#sum (no block) of an Array of Integers and Floats, starting from an Integer or Float,
        /// as the prelude's Enumerable#sum computes it - the plain fold until the first Float, then
        /// Kahan-Babuska compensated summation - in one pass over the storage instead of a block call
        /// per element. Answers nil, having changed nothing, when an element or the initial value is
        /// anything else, or when Integer#+ or Float#+ is not the built-in one: the prelude then
        /// does the whole sum.
        /// </summary>
        [RubyMethod("__numeric_sum__", RubyMethodAttributes.PrivateInstance)]
        public static object NumericSum(RubyContext/*!*/ context, IList/*!*/ self, object init) {
            if (!IsNumericSummand(init)) {
                return null;
            }
            if (!IListOps.IsBuiltinMethod(context.IntegerClass, "+") || !IListOps.IsBuiltinMethod(context.GetClass(typeof(double)), "+")) {
                return null;
            }

            int count = self.Count;
            int i = 0;
            double sum;

            if (!(init is double)) {
                // The Integer fold, in a long for as long as it fits: boxing every partial sum was
                // most of the cost.
                long total = (init is int) ? (int)init : (init is long) ? (long)init : 0;
                BigInteger bigTotal = (init is BigInteger) ? (BigInteger)init : BigInteger.Zero;
                bool big = init is BigInteger;
                for (; i < count; i++) {
                    object x = self[i];
                    if (x is int || x is long) {
                        long value = (x is int) ? (int)x : (long)x;
                        if (big) {
                            bigTotal += value;
                        } else {
                            long r = unchecked(total + value);
                            if (((total ^ r) & (value ^ r)) < 0) {
                                bigTotal = (BigInteger)total + value;
                                big = true;
                            } else {
                                total = r;
                            }
                        }
                    } else if (x is BigInteger) {
                        if (!big) {
                            bigTotal = total;
                            big = true;
                        }
                        bigTotal += (BigInteger)x;
                    } else if (x is double) {
                        break;
                    } else {
                        return null;
                    }
                }
                object acc = big ? Protocols.Normalize(bigTotal) : Protocols.Normalize(total);
                if (i == count) {
                    return acc;
                }
                // a Float: compensated summation from here on
                sum = SummandToDouble(acc);
            } else {
                // A Float to start with: Float + Integer is the plain fold until the first Float.
                sum = (double)init;
                for (; i < count; i++) {
                    object x = self[i];
                    if (x is double) {
                        break;
                    } else if (!IsNumericSummand(x)) {
                        return null;
                    }
                    sum += SummandToDouble(x);
                }
                if (i == count) {
                    return sum;
                }
            }

            double compensation = 0.0;
            for (; i < count; i++) {
                object x = self[i];
                if (!IsNumericSummand(x)) {
                    return null;
                }

                double value = SummandToDouble(x);
                // Nothing infinite can be compensated (see Enumerable#sum in the prelude): a NaN
                // stays, an infinity swallows the sum unless the sum is the other infinity, and a
                // finite number adds nothing to an infinite sum.
                if (Double.IsNaN(sum)) {
                    continue;
                } else if (Double.IsNaN(value)) {
                    sum = value;
                    compensation = 0.0;
                    continue;
                } else if (Double.IsInfinity(value)) {
                    sum = (Double.IsInfinity(sum) && Math.Sign(sum) == -Math.Sign(value)) ? Double.NaN : value;
                    compensation = 0.0;
                    continue;
                } else if (Double.IsInfinity(sum)) {
                    continue;
                }

                double t = sum + value;
                compensation += Math.Abs(sum) >= Math.Abs(value) ? (sum - t) + value : (value - t) + sum;
                sum = t;
            }
            return sum + compensation;
        }

        private static bool IsNumericSummand(object x) {
            return x is int || x is double || x is BigInteger || x is long;
        }

        private static double SummandToDouble(object x) {
            if (x is int) {
                return (int)x;
            } else if (x is double) {
                return (double)x;
            } else if (x is long) {
                return Protocols.ToDouble(new BigInteger((long)x));
            } else {
                return Protocols.ToDouble((BigInteger)x);
            }
        }

        #endregion
    }
}

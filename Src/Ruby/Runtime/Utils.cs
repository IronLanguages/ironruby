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

using System.Linq.Expressions;

using System;
using System.Diagnostics;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Numerics;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;

namespace IronRuby.Runtime {
    public static class Utils {
        public static readonly byte[] EmptyBytes = Array.Empty<byte>();
        public static readonly char[] EmptyChars = Array.Empty<char>();

        public static bool IsAscii(this string/*!*/ str) {
            return System.Text.Ascii.IsValid(str);
        }

        public static bool IsAscii(this byte[]/*!*/ bytes, int count) {
            return System.Text.Ascii.IsValid(new ReadOnlySpan<byte>(bytes, 0, count));
        }

        public static bool IsAscii(this char[]/*!*/ str, int count) {
            return System.Text.Ascii.IsValid(new ReadOnlySpan<char>(str, 0, count));
        }

        public static bool IsAscii(this char[]/*!*/ str, int start, int count) {
            return System.Text.Ascii.IsValid(new ReadOnlySpan<char>(str, start, count));
        }

        internal static bool IsBinary(this string/*!*/ str) {
            return str.AsSpan().IndexOfAnyExceptInRange('\0', '\u00ff') < 0;
        }

        internal static int GetCharacterCount(this string/*!*/ str) {
            return GetCharacterCount(str.AsSpan());
        }

        /// <summary>
        /// Calculates the number of Unicode characters in given array.
        /// </summary>
        internal static int GetCharacterCount(this char[]/*!*/ str, int count) {
            return GetCharacterCount(new ReadOnlySpan<char>(str, 0, count));
        }

        /// <summary>
        /// The number of UTF-16 code units less the number of well-formed surrogate pairs.
        /// </summary>
        internal static int GetCharacterCount(ReadOnlySpan<char> str) {
            int pairs = 0;
            int i = 0;
            while (true) {
                int j = str.Slice(i).IndexOfAnyInRange('\uD800', '\uDBFF');
                if (j < 0) {
                    break;
                }
                i += j + 1;
                if (i < str.Length && Char.IsLowSurrogate(str[i])) {
                    pairs++;
                    i++;
                }
            }
            return str.Length - pairs;
        }

        public static string/*!*/ ToAsciiString(this string/*!*/ str) {
            return MutableString.AppendUnicodeRepresentation(new StringBuilder(), str, MutableString.Escape.NonAscii, -1, -1).ToString();
        }

        public static int LastCharacter(this string/*!*/ str) {
            return str.Length == 0 ? -1 : str[str.Length - 1];
        }

        internal static IEnumerable<byte>/*!*/ EnumerateAsBytes(char[]/*!*/ data, int count) {
            for (int i = 0; i < count; i++) {
                yield return (byte)data[i];
            }
        }

        internal static IEnumerable<byte>/*!*/ EnumerateAsBytes(string/*!*/ data) {
            for (int i = 0; i < data.Length; i++) {
                yield return (byte)data[i];
            }
        }

        internal static IEnumerable<T>/*!*/ Enumerate<T>(T[]/*!*/ data, int count) {
            for (int i = 0; i < count; i++) {
                yield return data[i];
            }
        }

        internal const int MinListSize = 4;
        internal const int MinBufferSize = 16;

        internal static int GetExpandedSize<T>(T[]/*!*/ array, int minLength) {
            return Math.Max(minLength, Math.Max(1 + (array.Length << 1), typeof(T) == typeof(object) ? MinListSize : MinBufferSize));
        }

        internal static void Resize<T>(ref T[]/*!*/ array, int minLength) {
            if (array.Length < minLength) {
                Array.Resize(ref array, GetExpandedSize(array, minLength));
            }
        }

        internal static void TrimExcess<T>(ref T[] data, int count) {
            if (IsSparse(count, data.Length)) {
                Array.Resize(ref data, count);
            }
        }

        internal static bool IsSparse(int portionSize, int totalSize) {
            Debug.Assert(portionSize <= totalSize);
            return (long)portionSize * 10 < (long)totalSize * 9;
        }

        internal static void ResizeForInsertion<T>(ref T[]/*!*/ array, int itemCount, int index, int count) {
            int minLength = itemCount + count;
            T[] a;
            if (array.Length < minLength) {
                a = new T[GetExpandedSize(array, minLength)];
                Array.Copy(array, 0, a, 0, index);
            } else {
                a = array;
            }

            Array.Copy(array, index, a, index + count, itemCount - index);
            array = a;
        }

        internal static void Fill<T>(T[]/*!*/ array, int index, T item, int repeatCount) {
            // a block fill (vectorized where it can be; an element store at a time for an array
            // whose runtime type is not exactly T[])
            Array.Fill(array, item, index, repeatCount);
        }

        private static void Fill(byte[]/*!*/ src, int srcStart, byte[]/*!*/ dst, int dstStart, int count, int repeatCount) {
            // TODO: can be optimized for big repeatCount or big count:
            if (count == 1) {
                Fill(dst, dstStart, src[srcStart], repeatCount);
            } else {
                for (int i = 0; i < repeatCount; i++) {
                    for (int j = 0; j < count; j++) {
                        dst[dstStart++] = src[srcStart + j];
                    }
                }
            }
        }

        internal static T[]/*!*/ Concatenate<T>(T[]/*!*/ array1, T[]/*!*/ array2) {
            return Concatenate(array1, array1.Length, array2, array2.Length);
        }

        internal static T[]/*!*/ Concatenate<T>(params T[][] arrays) {
            int length = 0;
            foreach (var array in arrays) {
                length += array.Length;
            }

            T[] result = new T[length];
            length = 0;
            foreach (var array in arrays) {
                Array.Copy(array, 0, result, length, array.Length);
                length += array.Length;
            }
            return result;
        }

        /// <summary>
        /// A new array for a copy that fills every item of it. Not zeroed first (unless T holds
        /// references): for a long string the zeroing of a large object heap array cost as much as
        /// the copy itself.
        /// </summary>
        internal static T[]/*!*/ NewArrayToFill<T>(int length) {
            return GC.AllocateUninitializedArray<T>(length);
        }

        internal static T[]/*!*/ Concatenate<T>(T[]/*!*/ array1, int itemCount1, T[]/*!*/ array2, int itemCount2) {
            T[] result = NewArrayToFill<T>(itemCount1 + itemCount2);
            Array.Copy(array1, 0, result, 0, itemCount1);
            Array.Copy(array2, 0, result, itemCount1, itemCount2);
            return result;
        }

        internal static int Append<T>(ref T[]/*!*/ array, int itemCount, T item, int repeatCount) {
            Resize(ref array, itemCount + repeatCount);
            Fill(array, itemCount, item, repeatCount);
            return itemCount + repeatCount;
        }

        internal static int Append(ref char[]/*!*/ array, int itemCount, string/*!*/ other, int start, int count) {
            int newCount = itemCount + count;
            Resize(ref array, newCount);
            other.CopyTo(start, array, itemCount, count);
            return newCount;
        }

        internal static int Append<T>(ref T[]/*!*/ array, int itemCount, T[]/*!*/ other, int start, int count) {
            int newCount = itemCount + count;
            Resize(ref array, newCount);
            Array.Copy(other, start, array, itemCount, count);
            return newCount;
        }

        internal static int Append(ref byte[]/*!*/ array, int itemCount, string/*!*/ other, int start, int count, Encoding/*!*/ encoding) {
            var chars = other.AsSpan(start, count);
            int newCount = itemCount + encoding.GetByteCount(chars);
            Resize(ref array, newCount);
            encoding.GetBytes(chars, array.AsSpan(itemCount));
            return newCount;
        }

        internal static int Append(ref byte[]/*!*/ array, int itemCount, char[]/*!*/ other, int start, int count, Encoding/*!*/ encoding) {
            int newCount = itemCount + encoding.GetByteCount(other, start, count);
            Resize(ref array, newCount);
            encoding.GetBytes(other, start, count, array, itemCount);
            return newCount;
        }

        internal static int Append(ref byte[]/*!*/ array, int itemCount, char other, int repeatCount, Encoding/*!*/ encoding) {
            if (repeatCount == 0) {
                return itemCount;
            }

            char[] chars = new char[] { other };
            int charSize = encoding.GetByteCount(chars, 0, 1);
            int newCount = itemCount + charSize * repeatCount;
            Resize(ref array, newCount);

            encoding.GetBytes(chars, 0, 1, array, itemCount);
            Fill(array, itemCount, array, itemCount + charSize, charSize, repeatCount - 1);
            
            return newCount;
        }

        internal static int InsertAt<T>(ref T[]/*!*/ array, int itemCount, int index, T other, int repeatCount) {
            ResizeForInsertion(ref array, itemCount, index, repeatCount);
            Fill(array, index, other, repeatCount);
            return itemCount + repeatCount;
        }

        internal static int InsertAt(ref char[]/*!*/ array, int itemCount, int index, string/*!*/ other, int start, int count) {
            ResizeForInsertion(ref array, itemCount, index, count);
            other.CopyTo(start, array, index, count);
            return itemCount + count;
        }

        internal static int InsertAt<T>(ref T[]/*!*/ array, int itemCount, int index, T[]/*!*/ other, int start, int count) {
            ResizeForInsertion(ref array, itemCount, index, count);
            Array.Copy(other, start, array, index, count);
            return itemCount + count;
        }

        internal static int Remove<T>(ref T[]/*!*/ array, int itemCount, int start, int count) {
            T[] a;
            int remaining = itemCount - count;
            if (count == 0) {
                return itemCount;
            }
            if (remaining > MinBufferSize && remaining < itemCount / 2) {
                a = new T[remaining];
                Array.Copy(array, 0, a, 0, start);
            } else {
                a = array;
            }

            Array.Copy(array, start + count, a, start, remaining - start);
            array = a;
            return remaining;
        }

        /// <summary>
        /// <see cref="Remove{T}"/> for an array that is shared with another string (copy-on-write) and
        /// so must not be written to. Removing from the end keeps the array, which stays shared; any
        /// other removal copies the items that remain into an array of their own, which is no
        /// longer shared.
        /// </summary>
        internal static int RemoveShared<T>(ref T[]/*!*/ array, int itemCount, int start, int count, ref bool shared) {
            int remaining = itemCount - count;
            if (count == 0) {
                return itemCount;
            }
            if (start == remaining) {
                return remaining;
            }

            // the same capacity Remove would leave (zeroed: a write past the end relies on that)
            var a = new T[remaining > MinBufferSize && remaining < itemCount / 2 ? remaining : itemCount];
            Array.Copy(array, 0, a, 0, start);
            Array.Copy(array, start + count, a, start, remaining - start);
            array = a;
            shared = false;
            return remaining;
        }

        internal static T[]/*!*/ GetSlice<T>(this T[]/*!*/ array, int start, int count) {
            var copy = NewArrayToFill<T>(count);
            Array.Copy(array, start, copy, 0, count);
            return copy;
        }

        internal static T[]/*!*/ GetSlice<T>(this T[]/*!*/ array, int arrayLength, int start, int count) {
            count = NormalizeCount(arrayLength, start, count);
            var copy = NewArrayToFill<T>(count);
            if (count > 0) {
                Array.Copy(array, start, copy, 0, count);
            }
            return copy;
        }

        internal static string/*!*/ GetSlice(this string/*!*/ str, int start, int count) {
            count = NormalizeCount(str.Length, start, count);
            return count > 0 ? str.Substring(start, count) : String.Empty;
        }

        internal static string/*!*/ GetStringSlice(this char[]/*!*/ chars, int arrayLength, int start, int count) {
            count = NormalizeCount(arrayLength, start, count);
            return count > 0 ? new String(chars, start, count) : String.Empty;
        }

        /// <summary>
        /// Whether a slice of the first <paramref name="count"/> items of a string's array, of length
        /// <paramref name="arrayLength"/>, shares the array (copy-on-write, see MutableString.Content.Clone)
        /// rather than copying them - s[0, n], a #chomp or #rstrip. Only a long slice that keeps at
        /// least half of the array does: a short one is cheap to copy, and a small slice must not keep
        /// a large array alive - also not by slicing a slice, which is why this is the array's length
        /// and not the string's.
        /// </summary>
        internal static bool IsSharedPrefix(int arrayLength, int count) {
            return count >= SharedSliceMinLength && count >= arrayLength - (arrayLength >> 1);
        }

        internal const int SharedSliceMinLength = 256;

        internal static int NormalizeCount(int arrayLength, int start, int count) {
            if (count > arrayLength - start) {
                return start >= arrayLength ? 0 : arrayLength - start;
            } else {
                return count;
            }
        }

        internal static void NormalizeLastIndexOfIndices(int arrayLength, ref int start, ref int count) {
            if (start >= arrayLength) {
                count = arrayLength - (start - count + 1);
                start = arrayLength - 1;
            }
        }

        // The searches below are ordinal: an element matches when its numeric value is equal,
        // so a byte b matches the char (char)b and nothing else. They all go through the
        // runtime's vectorized span searches; a needle of the other element type is converted
        // first - widened, or narrowed after checking that it can match a byte at all.

        /// <summary>
        /// The index of the first occurrence of value in data[start, start + count), or -1.
        /// An empty value is found at start.
        /// </summary>
        private static int IndexOf<T>(ReadOnlySpan<T> data, ReadOnlySpan<T> value, int start, int count) where T : IEquatable<T> {
            count = NormalizeCount(data.Length, start, count);
            if (value.Length == 0) {
                return count >= 0 ? start : -1;
            }
            if (count < value.Length) {
                return -1;
            }
            int i = data.Slice(start, count).IndexOf(value);
            return i < 0 ? -1 : start + i;
        }

        /// <summary>
        /// The index of the last occurrence of value that lies entirely within
        /// data[start - count + 1, start], or -1. An empty value is found at start.
        /// </summary>
        private static int LastIndexOf<T>(ReadOnlySpan<T> data, ReadOnlySpan<T> value, int start, int count) where T : IEquatable<T> {
            NormalizeLastIndexOfIndices(data.Length, ref start, ref count);
            if (value.Length == 0) {
                return start;
            }
            if (count < value.Length) {
                return -1;
            }
            int finish = start - count + 1;
            int i = data.Slice(finish, count).LastIndexOf(value);
            return i < 0 ? -1 : finish + i;
        }

        private static char[]/*!*/ Widen(byte[]/*!*/ bytes) {
            var result = new char[bytes.Length];
            Encoding.Latin1.GetChars(bytes, result);
            return result;
        }

        /// <summary>
        /// The bytes equal to the chars of str, or null if a char is above ÿ and so can't
        /// be equal to any byte.
        /// </summary>
        private static byte[] Narrow(string/*!*/ str) {
            if (str.AsSpan().IndexOfAnyExceptInRange('\0', 'ÿ') >= 0) {
                return null;
            }
            var result = new byte[str.Length];
            Encoding.Latin1.GetBytes(str, result);
            return result;
        }

        internal static int IndexOf(byte[]/*!*/ array, int arrayLength, char value, int start, int count) {
            if (value > 0xff) {
                return -1;
            }
            byte b = (byte)value;
            return IndexOf(new ReadOnlySpan<byte>(array, 0, arrayLength), new ReadOnlySpan<byte>(ref b), start, count);
        }

        internal static int IndexOf(char[]/*!*/ array, int arrayLength, byte value, int start, int count) {
            char c = (char)value;
            return IndexOf(new ReadOnlySpan<char>(array, 0, arrayLength), new ReadOnlySpan<char>(ref c), start, count);
        }

        internal static int IndexOf(string/*!*/ str, byte value, int start, int count) {
            char c = (char)value;
            return IndexOf(str.AsSpan(), new ReadOnlySpan<char>(ref c), start, count);
        }

        /// <summary>
        /// Implements the same behavior as String.IndexOf on ASCII strings.
        /// </summary>
        internal static int IndexOf(byte[]/*!*/ array, int arrayLength, string/*!*/ str, int start, int count) {
            byte[] bytes = Narrow(str);
            if (bytes == null) {
                return -1;
            }
            return IndexOf(new ReadOnlySpan<byte>(array, 0, arrayLength), bytes, start, count);
        }

        /// <summary>
        /// Implements the same behavior as String.IndexOf on ASCII strings.
        /// </summary>
        internal static int IndexOf(char[]/*!*/ array, int arrayLength, string/*!*/ str, int start, int count) {
            return IndexOf(new ReadOnlySpan<char>(array, 0, arrayLength), str.AsSpan(), start, count);
        }

        /// <summary>
        /// Implements the same behavior as String.IndexOf on ASCII strings.
        /// </summary>
        internal static int IndexOf(byte[]/*!*/ array, int arrayLength, byte[]/*!*/ bytes, int start, int count) {
            return IndexOf(new ReadOnlySpan<byte>(array, 0, arrayLength), bytes, start, count);
        }

        /// <summary>
        /// Implements the same behavior as String.IndexOf on ASCII strings.
        /// </summary>
        internal static int IndexOf(char[]/*!*/ array, int arrayLength, byte[]/*!*/ bytes, int start, int count) {
            return IndexOf(new ReadOnlySpan<char>(array, 0, arrayLength), Widen(bytes), start, count);
        }

        /// <summary>
        /// Implements the same behavior as String.IndexOf on ASCII strings.
        /// </summary>
        internal static int IndexOf(string/*!*/ str, byte[]/*!*/ bytes, int start, int count) {
            return IndexOf(str.AsSpan(), Widen(bytes), start, count);
        }

        internal static int LastIndexOf(char[]/*!*/ array, int arrayLength, byte value, int start, int count) {
            char c = (char)value;
            return LastIndexOf(new ReadOnlySpan<char>(array, 0, arrayLength), new ReadOnlySpan<char>(ref c), start, count);
        }

        internal static int LastIndexOf(string/*!*/ str, byte value, int start, int count) {
            char c = (char)value;
            return LastIndexOf(str.AsSpan(), new ReadOnlySpan<char>(ref c), start, count);
        }

        /// <summary>
        /// Implements the same behavior as String.LastIndexOf on ASCII strings.
        /// </summary>
        internal static int LastIndexOf(byte[]/*!*/ array, int arrayLength, string/*!*/ value, int start, int count) {
            byte[] bytes = Narrow(value);
            if (bytes == null) {
                // (an empty value is always narrowed, so this can't skip its answer, start)
                return -1;
            }
            return LastIndexOf(new ReadOnlySpan<byte>(array, 0, arrayLength), bytes, start, count);
        }

        internal static int LastIndexOf(char[]/*!*/ array, int arrayLength, string/*!*/ value, int start, int count) {
            return LastIndexOf(new ReadOnlySpan<char>(array, 0, arrayLength), value.AsSpan(), start, count);
        }

        /// <summary>
        /// Implements the same behavior as String.LastIndexOf on ASCII strings.
        /// </summary>
        internal static int LastIndexOf(byte[]/*!*/ array, int arrayLength, byte[]/*!*/ value, int start, int count) {
            return LastIndexOf(new ReadOnlySpan<byte>(array, 0, arrayLength), value, start, count);
        }

        /// <summary>
        /// Implements the same behavior as String.LastIndexOf on ASCII strings.
        /// </summary>
        internal static int LastIndexOf(char[]/*!*/ array, int arrayLength, byte[]/*!*/ value, int start, int count) {
            return LastIndexOf(new ReadOnlySpan<char>(array, 0, arrayLength), Widen(value), start, count);
        }

        /// <summary>
        /// Implements the same behavior as String.LastIndexOf on ASCII strings.
        /// </summary>
        internal static int LastIndexOf(string/*!*/ str, byte[]/*!*/ value, int start, int count) {
            return LastIndexOf(str.AsSpan(), Widen(value), start, count);
        }

        // Ordinal comparison: the difference of the first elements that differ, or else -1, 0 or +1
        // as the first sequence is shorter than, as long as or longer than the second.
        private static int ValueCompareTo<T>(ReadOnlySpan<T> data, ReadOnlySpan<T> other) where T : IBinaryInteger<T> {
            int min = Math.Min(data.Length, other.Length);
            int i = data.Slice(0, min).CommonPrefixLength(other.Slice(0, min));
            return (i < min) ? Int32.CreateTruncating(data[i]) - Int32.CreateTruncating(other[i]) : data.Length.CompareTo(other.Length);
        }

        internal static int ValueCompareTo(this byte[]/*!*/ array, int itemCount, byte[]/*!*/ other) {
            return ValueCompareTo(array, itemCount, other, other.Length);
        }

        internal static int ValueCompareTo(this byte[]/*!*/ array, int itemCount, byte[]/*!*/ other, int otherCount) {
            return ValueCompareTo(new ReadOnlySpan<byte>(array, 0, itemCount), new ReadOnlySpan<byte>(other, 0, otherCount));
        }

        internal static int ValueCompareTo(this char[]/*!*/ array, int itemCount, char[]/*!*/ other, int otherCount) {
            return ValueCompareTo(new ReadOnlySpan<char>(array, 0, itemCount), new ReadOnlySpan<char>(other, 0, otherCount));
        }

        internal static int ValueCompareTo(this char[]/*!*/ array, int itemCount, string/*!*/ other) {
            return ValueCompareTo(new ReadOnlySpan<char>(array, 0, itemCount), other.AsSpan());
        }

        internal static int ValueCompareTo(this string/*!*/ str, string/*!*/ other) {
            return ValueCompareTo(str.AsSpan(), other.AsSpan());
        }

        internal static int ValueCompareTo(this byte[]/*!*/ array, int itemCount, string/*!*/ other) {
            int min = Math.Min(itemCount, other.Length);
            for (int i = 0; i < min; i++) {
                if (array[i] != other[i]) {
                    return (int)array[i] - other[i];
                }
            }
            return itemCount.CompareTo(other.Length);
        }

        internal static bool ValueEquals<T>(T[] array, int arrayCount, T[] other, int otherCount) {
            Debug.Assert(arrayCount <= array.Length);
            Debug.Assert(otherCount <= other.Length);

            if (arrayCount != otherCount) {
                return false;
            }

            for (int i = 0; i < arrayCount; i++) {
                if (!Object.Equals(array[i], other[i])) {
                    return false;
                }
            }

            return true;
        }

        internal static void AddRange(IList/*!*/ list, IList/*!*/ range) {
            Assert.NotNull(list, range);

            List<object> objList;
            IEnumerable<object> enumerableRange;
            RubyArray array;
            if ((array = list as RubyArray) != null) {
                array.AddRange(range);
            } else if ((objList = list as List<object>) != null && (enumerableRange = range as IEnumerable<object>) != null) {
                objList.AddRange(enumerableRange);
            } else {
                foreach (var item in range) {
                    list.Add(item);
                }
            }
        }

        [Conditional("DEBUG")]
        public static void Log(string/*!*/ message, string/*!*/ category) {
            Debug.WriteLine((object)message, category);
        }

        public static long DateTimeTicksFromStopwatch(long elapsedStopwatchTicks) {
            if (Stopwatch.IsHighResolution) {
                return (long)(((double)elapsedStopwatchTicks) * 10000000.0 / (double)Stopwatch.Frequency);
            }
            return elapsedStopwatchTicks;
        }

        public static char ToLowerHexDigit(this int digit) {
            return (char)((digit < 10) ? '0' + digit : 'a' + digit - 10);
        }

        public static char ToUpperHexDigit(this int digit) {
            return (char)((digit < 10) ? '0' + digit : 'A' + digit - 10);
        }

        public static char ToUpperInvariant(this char c) {
            return Char.ToUpperInvariant(c);
        }

        public static char ToLowerInvariant(this char c) {
            return Char.ToLowerInvariant(c);
        }

        internal static IEnumerable<Expression/*!*/>/*!*/ ToExpressions(this IEnumerable<DynamicMetaObject>/*!*/ metaObjects) {
            foreach (var metaObject in metaObjects) {
                yield return metaObject != null ? metaObject.Expression : null;
            }
        }

        internal static Action<RubyModule> CloneInvocationChain(Action<RubyModule> chain) {
            if (chain == null) {
                return null;
            }

            Action<RubyModule> result = _ => { };
            foreach (var d in chain.GetInvocationList()) {
                result += (Action<RubyModule>)d;
            }

            return result;
        }

        private sealed class CheckDecoderFallback : DecoderFallback {
            public bool HasInvalidCharacters { get; private set; }

            public CheckDecoderFallback() {
            }

            public override int MaxCharCount {
                get { return 1; }
            }

            public override DecoderFallbackBuffer CreateFallbackBuffer() {
                return new Buffer(this);
            }

            internal sealed class Buffer : DecoderFallbackBuffer {
                private readonly CheckDecoderFallback _fallback;

                public Buffer(CheckDecoderFallback/*!*/ fallback) {
                    _fallback = fallback;
                }

                public override bool Fallback(byte[]/*!*/ bytesUnknown, int index) {
                    _fallback.HasInvalidCharacters = true;
                    return true;
                }

                public override char GetNextChar() {
                    return '\0';
                }

                public override bool MovePrevious() {
                    return false;
                }

                public override int Remaining {
                    get { return 0; }
                }
            }
        }

        private sealed class CheckEncoderFallback : EncoderFallback {
            public bool HasInvalidCharacters { get; private set; }

            public CheckEncoderFallback() {
            }

            public override int MaxCharCount {
                get { return 1; }
            }

            public override EncoderFallbackBuffer CreateFallbackBuffer() {
                return new Buffer(this);
            }

            internal sealed class Buffer : EncoderFallbackBuffer {
                private readonly CheckEncoderFallback _fallback;

                public Buffer(CheckEncoderFallback/*!*/ fallback) {
                    _fallback = fallback;
                }

                public override bool Fallback(char charUnknown, int index) {
                    _fallback.HasInvalidCharacters = true;
                    return true;
                }

                public override bool Fallback(char charUnknownHigh, char charUnknownLow, int index) {
                    _fallback.HasInvalidCharacters = true;
                    return true;
                }

                public override char GetNextChar() {
                    return '\0';
                }

                public override bool MovePrevious() {
                    return false;
                }

                public override int Remaining {
                    get { return 0; }
                }
            }
        }

        // Replacing the Fallback only works for an encoding whose decoder routes bad input through
        // it. A hand-written Encoding - BinaryEncoding, or any of RubyOnlyEncodings - raises
        // straight from GetCharCount instead, and that exception used to escape String#valid_encoding?
        // as a bare CLR DecoderFallbackException. Bad input is bad input either way.
        internal static bool ContainsInvalidCharacters(byte[]/*!*/ bytes, int start, int count, Encoding/*!*/ encoding) {
            bool? valid = RubyOnlyEncodings.IsValid(encoding, bytes, start, count);
            if (valid.HasValue) {
                return !valid.Value;
            }

            var decoder = encoding.GetDecoder();
            var fallback = new CheckDecoderFallback();
            decoder.Fallback = fallback;
            try {
                decoder.GetCharCount(bytes, start, count, true);
            } catch (DecoderFallbackException) {
                return true;
            }
            return fallback.HasInvalidCharacters;
        }

        internal static bool ContainsInvalidCharacters(char[]/*!*/ chars, int start, int count, Encoding/*!*/ encoding) {
            return ContainsInvalidCharacters(new ReadOnlySpan<char>(chars, start, count), encoding);
        }

        internal static bool ContainsInvalidCharacters(ReadOnlySpan<char> chars, Encoding/*!*/ encoding) {
            // Every byte of a single byte encoding is valid in Ruby, see RubyOnlyEncodings.IsValid.
            if (encoding.IsSingleByte && encoding.CodePage != RubyEncoding.CodePageAscii && encoding.CodePage != RubyEncoding.CodePageBinary) {
                return false;
            }

            // Validity is a matter of shape there, and a sequence of the right shape that names no
            // character is held as escaped bytes, which no encoder takes: ask the bytes instead.
            if (encoding is IStructuredEncoding) {
                byte[] bytes;
                try {
                    bytes = RubyEncoding.GetRubyEncoding(encoding.CodePage).EscapingEncoding.GetBytes(chars.ToArray());
                } catch (EncoderFallbackException) {
                    return true;
                }
                return ContainsInvalidCharacters(bytes, 0, bytes.Length, encoding);
            }

            var encoder = encoding.GetEncoder();
            var fallback = new CheckEncoderFallback();
            encoder.Fallback = fallback;
            try {
                encoder.GetByteCount(chars, true);
            } catch (EncoderFallbackException) {
                return true;
            }
            return fallback.HasInvalidCharacters;
        }
    }
}


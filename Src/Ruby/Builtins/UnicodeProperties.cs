/* ****************************************************************************
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
using System.Text;
using System.Threading;

namespace IronRuby.Builtins {
    /// <summary>
    /// The code points of Onigmo's character properties - every \p{...} name CRuby accepts, and
    /// the POSIX bracket classes - as sorted lists of inclusive [low, high] ranges. The tables are
    /// in UnicodeProperties.Generated.cs (Util/gen-unicode-properties.rb); .NET's regex engine
    /// knows only the general categories and a few BMP blocks, so RegexpTransformer spells the
    /// rest out as explicit character classes built from these.
    /// </summary>
    internal static partial class UnicodeProperties {
        // Onigmo's PROPERTY_NAME_MAX_SIZE: the longest normalized name, plus one.
        private const int MaxNameLength = 46;

        // Both lazy: the generated half's static fields may not be initialized yet when this
        // half's are (C# does not order initializers across the files of a partial class).
        private static byte[] _bytes;
        private static int[][] _tables;

        /// <summary>
        /// A property name as Onigmo compares it: ' ', '-' and '_' dropped, ASCII lower-cased.
        /// Null for a name no property can have (a non-ASCII character, or too long).
        /// </summary>
        internal static string NormalizeName(string/*!*/ name) {
            var key = new StringBuilder(name.Length);
            foreach (char c in name) {
                if (c == ' ' || c == '-' || c == '_') {
                    continue;
                }
                if (c >= 0x80) {
                    return null;
                }
                key.Append((c >= 'A' && c <= 'Z') ? (char)(c + ('a' - 'A')) : c);
                if (key.Length >= MaxNameLength) {
                    return null;
                }
            }
            return key.ToString();
        }

        /// <summary>
        /// The table a normalized property name selects, or -1 for a name Onigmo does not know.
        /// </summary>
        internal static int Find(string/*!*/ normalizedName) {
            int index = Array.BinarySearch(_names, normalizedName, StringComparer.Ordinal);
            return (index >= 0) ? _nameTables[index] : -1;
        }

        /// <summary>
        /// The table of a POSIX bracket class ([[:alpha:]]: <paramref name="name"/> is "alpha"),
        /// or -1 for a name that is not one. Case-sensitive, as in Onigmo.
        /// </summary>
        internal static int FindPosix(string/*!*/ name) {
            int index = Array.BinarySearch(_posixNames, name, StringComparer.Ordinal);
            return (index >= 0) ? _posixTables[index] : -1;
        }

        /// <summary>
        /// The ranges of a table: [low0, high0, low1, high1, ...], sorted, disjoint and never
        /// adjacent. Decoded on first use and shared, so callers must not modify the array.
        /// </summary>
        internal static int[]/*!*/ GetRanges(int table) {
            int[][] tables = _tables;
            if (tables == null) {
                Interlocked.CompareExchange(ref _tables, new int[_tableNames.Length][], null);
                tables = _tables;
            }
            int[] result = tables[table];
            if (result == null) {
                // a race decodes the table twice, to equal arrays: harmless
                tables[table] = result = Decode(table);
            }
            return result;
        }

        private static int[]/*!*/ Decode(int table) {
            byte[] bytes = _bytes;
            if (bytes == null) {
                _bytes = bytes = Convert.FromBase64String(_data);
            }

            int position = _offsets[table];
            int count = ReadVarint(bytes, ref position);
            var result = new int[count * 2];
            int previous = -1;
            for (int i = 0; i < result.Length; i += 2) {
                int low = previous + 1 + ReadVarint(bytes, ref position);
                int high = low + ReadVarint(bytes, ref position);
                result[i] = low;
                result[i + 1] = high;
                previous = high;
            }
            return result;
        }

        private static int ReadVarint(byte[]/*!*/ bytes, ref int position) {
            int result = 0;
            int shift = 0;
            while (true) {
                byte b = bytes[position++];
                result |= (b & 0x7f) << shift;
                if (b < 0x80) {
                    return result;
                }
                shift += 7;
            }
        }
    }

    /// <summary>
    /// Set operations on code point range lists: int arrays [low0, high0, low1, high1, ...] of
    /// inclusive ranges, sorted, disjoint and never adjacent, within [0, MaxCodePoint].
    /// </summary>
    internal static class CodePointRanges {
        internal const int MaxCodePoint = 0x10ffff;

        internal static readonly int[] Empty = new int[0];
        internal static readonly int[] All = new int[] { 0, MaxCodePoint };

        internal static int[]/*!*/ Single(int low, int high) {
            return new int[] { low, high };
        }

        internal static int[]/*!*/ Complement(int[]/*!*/ ranges) {
            var result = new List<int>(ranges.Length + 2);
            int next = 0;
            for (int i = 0; i < ranges.Length; i += 2) {
                if (ranges[i] > next) {
                    result.Add(next);
                    result.Add(ranges[i] - 1);
                }
                next = ranges[i + 1] + 1;
            }
            if (next <= MaxCodePoint) {
                result.Add(next);
                result.Add(MaxCodePoint);
            }
            return result.ToArray();
        }

        internal static int[]/*!*/ Union(int[]/*!*/ a, int[]/*!*/ b) {
            if (a.Length == 0) {
                return b;
            }
            if (b.Length == 0) {
                return a;
            }

            var result = new List<int>(a.Length + b.Length);
            int i = 0, j = 0;
            while (i < a.Length || j < b.Length) {
                int low, high;
                if (j >= b.Length || (i < a.Length && a[i] <= b[j])) {
                    low = a[i];
                    high = a[i + 1];
                    i += 2;
                } else {
                    low = b[j];
                    high = b[j + 1];
                    j += 2;
                }

                int last = result.Count - 1;
                if (last > 0 && low <= result[last] + 1) {
                    if (high > result[last]) {
                        result[last] = high;
                    }
                } else {
                    result.Add(low);
                    result.Add(high);
                }
            }
            return result.ToArray();
        }

        internal static int[]/*!*/ Intersect(int[]/*!*/ a, int[]/*!*/ b) {
            var result = new List<int>();
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length) {
                int low = Math.Max(a[i], b[j]);
                int high = Math.Min(a[i + 1], b[j + 1]);
                if (low <= high) {
                    result.Add(low);
                    result.Add(high);
                }
                if (a[i + 1] < b[j + 1]) {
                    i += 2;
                } else {
                    j += 2;
                }
            }
            return result.ToArray();
        }

        internal static int[]/*!*/ Subtract(int[]/*!*/ a, int[]/*!*/ b) {
            return (b.Length == 0) ? a : Intersect(a, Complement(b));
        }

        internal static bool Contains(int[]/*!*/ ranges, int low, int high) {
            // the one range that could hold [low, high] entirely
            int lo = 0, hi = ranges.Length / 2 - 1;
            while (lo <= hi) {
                int mid = (lo + hi) >> 1;
                if (ranges[mid * 2 + 1] < low) {
                    lo = mid + 1;
                } else if (ranges[mid * 2] > low) {
                    hi = mid - 1;
                } else {
                    return ranges[mid * 2 + 1] >= high;
                }
            }
            return false;
        }

        internal static bool Equals(int[]/*!*/ a, int[]/*!*/ b) {
            if (a.Length != b.Length) {
                return false;
            }
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) {
                    return false;
                }
            }
            return true;
        }
    }
}

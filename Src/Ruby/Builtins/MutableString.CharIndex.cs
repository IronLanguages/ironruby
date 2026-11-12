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
using System.Runtime.CompilerServices;
using IronRuby.Compiler;

namespace IronRuby.Builtins {
    // Ruby character indices vs. CLR indices.
    //
    // A string's character representation is UTF-16, so a character above U+FFFF is two CLR
    // chars - a surrogate pair - while Ruby counts it as one. Every index Ruby code passes in
    // (String#[], #index's start, Regexp#match's pos, ...) is a *character* index and every CLR
    // API below - GetSlice, IndexOf, Remove, System.Text.RegularExpressions - takes and answers
    // *CLR* indices. Methods that hand an index across the boundary translate it here, with
    // ToClrIndex and ToCharacterIndex, and nowhere else.
    //
    // Nearly every string has no surrogate pair, and for it both are the identity. That is
    // decided by the NoSurrogates flag MutableString keeps anyway - worked out with the
    // ASCII flag in one vectorized pass, and kept across appends of pair-free content - so the
    // common case costs a flag test. Only a string that does hold a pair walks its content,
    // and a long one keeps a checkpoint table (every CheckpointInterval characters) so that
    // repeated indexing into it - a lexer slicing its source - is not a walk from the start
    // every time. For immutable content the table lives outside the string, in a weak table
    // keyed by the CLR string, so that no string pays a field for it and every string sharing
    // the content shares it; mutable content keeps its own, and any mutation sets
    // CharIndexChangedFlags, which retires it.
    //
    // A lone surrogate (an invalid byte of a broken string is carried as one) is a character
    // of one CLR char, as is everything else except a well-formed pair: that is the same rule
    // GetCharacterCount and the character enumerators follow.
    public partial class MutableString {
        private const int CheckpointInterval = 64;

        // Strings shorter than this (in CLR chars) are walked rather than indexed: a vectorized
        // walk over a few hundred chars is cheaper than the table lookup.
        private const int CharIndexTableThreshold = 256;

        private sealed class CharIndexTable {
            public int CharacterCount;
            // CLR index of character k * CheckpointInterval.
            public int[] Checkpoints;
        }

        // Keyed by the immutable CLR string it indexes.
        private static readonly ConditionalWeakTable<string, CharIndexTable> _charIndexTables =
            new ConditionalWeakTable<string, CharIndexTable>();

        /// <summary>
        /// True when the cached flags already say that character and CLR indices agree: there is
        /// no surrogate in the content, or it is all ASCII (which a string held as bytes knows
        /// without ever working out its surrogates). O(1), never scans.
        /// </summary>
        public bool KnowsCharIndexIsClrIndex {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                uint flags = _flags;
                return (flags & (SurrogatesUnknownFlag | NoSurrogatesFlag)) == NoSurrogatesFlag
                    || (flags & (AsciiUnknownFlag | IsAsciiFlag)) == IsAsciiFlag;
            }
        }

        /// <summary>
        /// True if the character representation holds a surrogate pair, i.e. some Ruby character
        /// is two CLR chars. Scans the content at most once per mutation; a binary or single-byte
        /// representation never does.
        /// </summary>
        public bool HasSurrogatePairs() {
            if (KnowsCharIndexIsClrIndex || HasSingleByteCharacters || IsEmpty) {
                return false;
            }
            PrepareForCharacterRead();
            return !IsBinary && HasSurrogates();
        }

        /// <summary>
        /// The CLR index of the character at <paramref name="charIndex"/> (a Ruby index, already
        /// normalized to be non-negative). An index past the last character maps past the last
        /// CLR char by the same distance, so that a range check done afterwards still fails.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ToClrIndex(int charIndex) {
            if (KnowsCharIndexIsClrIndex || charIndex <= 0) {
                return charIndex;
            }
            return ToClrIndexSlow(charIndex);
        }

        /// <summary>
        /// The Ruby character index of CLR index <paramref name="clrIndex"/>. An index between the
        /// two halves of a pair maps to the character after the pair.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ToCharacterIndex(int clrIndex) {
            if (KnowsCharIndexIsClrIndex || clrIndex <= 0) {
                return clrIndex;
            }
            return ToCharacterIndexSlow(clrIndex);
        }

        /// <summary>
        /// Translates a Ruby character range [start, start + count) to CLR indices in place.
        /// Both must already be normalized and non-negative.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ToClrRange(ref int start, ref int count) {
            if (KnowsCharIndexIsClrIndex) {
                return;
            }
            ToClrRangeSlow(ref start, ref count);
        }

        private void ToClrRangeSlow(ref int start, ref int count) {
            if (!HasSurrogatePairs()) {
                return;
            }
            int end = (long)start + count > Int32.MaxValue ? Int32.MaxValue : start + count;
            var span = GetCharSpan();
            var table = GetCharIndexTable(span);
            int clrStart = CharToClr(span, table, start);
            // a short range - s[i], s[i, 3] - is walked on from its start, not looked up again
            int clrEnd = (count < CheckpointInterval && clrStart <= span.Length)
                ? WalkToCharacter(span, clrStart, start, end)
                : CharToClr(span, table, end);
            start = clrStart;
            count = clrEnd - clrStart;
        }

        /// <summary>
        /// The CLR index <paramref name="clrIndex"/> moved forward by one character: past a whole
        /// surrogate pair, else by one. What an empty match has to advance by.
        /// </summary>
        public int NextCharacterClrIndex(int clrIndex) {
            if (KnowsCharIndexIsClrIndex) {
                return clrIndex + 1;
            }
            return NextCharacterClrIndexSlow(clrIndex);
        }

        private int NextCharacterClrIndexSlow(int clrIndex) {
            if (!HasSurrogatePairs()) {
                return clrIndex + 1;
            }
            var span = GetCharSpan();
            if (clrIndex >= 0 && clrIndex + 1 < span.Length && Tokenizer.IsHighSurrogate(span[clrIndex]) && Tokenizer.IsLowSurrogate(span[clrIndex + 1])) {
                return clrIndex + 2;
            }
            return clrIndex + 1;
        }

        /// <summary>
        /// Advances a CLR index past a whole pair given the text the regex ran on - the
        /// MutableString itself may not be in character representation at the time.
        /// </summary>
        internal static int NextCharacterClrIndex(string/*!*/ str, int clrIndex) {
            if (clrIndex >= 0 && clrIndex + 1 < str.Length && Char.IsHighSurrogate(str[clrIndex]) && Char.IsLowSurrogate(str[clrIndex + 1])) {
                return clrIndex + 2;
            }
            return clrIndex + 1;
        }

        private ReadOnlySpan<char> GetCharSpan() {
            var content = _content;
            if (content is CharArrayContent chars) {
                return chars.GetDataSpan();
            }
            if (content is StringContent str) {
                return str.Data.AsSpan();
            }
            // single-byte binary representation: no pairs
            return default(ReadOnlySpan<char>);
        }

        private int ToClrIndexSlow(int charIndex) {
            if (!HasSurrogatePairs()) {
                return charIndex;
            }
            var span = GetCharSpan();
            return CharToClr(span, GetCharIndexTable(span), charIndex);
        }

        private int ToCharacterIndexSlow(int clrIndex) {
            if (!HasSurrogatePairs()) {
                return clrIndex;
            }
            var span = GetCharSpan();
            return ClrToChar(span, GetCharIndexTable(span), clrIndex);
        }

        /// <summary>
        /// The number of Ruby characters when the content has surrogate pairs; the table makes
        /// it O(1) for a long string.
        /// </summary>
        private int GetCharacterCountWithPairs() {
            var span = GetCharSpan();
            var table = GetCharIndexTable(span);
            return table != null ? table.CharacterCount : span.Length - CountPairs(span);
        }

        /// <summary>
        /// ToCharacterIndex for the text a regex ran on (MatchData): the character index of CLR
        /// index <paramref name="clrIndex"/> in <paramref name="text"/>, which holds a pair.
        /// </summary>
        internal static int ToCharacterIndex(string/*!*/ text, int clrIndex) {
            if (clrIndex <= 0) {
                return clrIndex;
            }
            var span = text.AsSpan();
            return ClrToChar(span, GetStringTable(text), clrIndex);
        }

        private static int CharToClr(ReadOnlySpan<char> span, CharIndexTable table, int charIndex) {
            int clr = 0, chars = 0;
            if (table != null) {
                if (charIndex >= table.CharacterCount) {
                    return span.Length + (charIndex - table.CharacterCount);
                }
                int k = charIndex / CheckpointInterval;
                clr = table.Checkpoints[k];
                chars = k * CheckpointInterval;
            }
            return WalkToCharacter(span, clr, chars, charIndex);
        }

        private static int ClrToChar(ReadOnlySpan<char> span, CharIndexTable table, int clrIndex) {
            if (clrIndex >= span.Length) {
                int count = (table != null) ? table.CharacterCount : span.Length - CountPairs(span);
                return count + (clrIndex - span.Length);
            }

            int clr = 0, chars = 0;
            if (table != null) {
                // the last checkpoint at or before clrIndex
                int[] checkpoints = table.Checkpoints;
                int k = Array.BinarySearch(checkpoints, clrIndex);
                if (k < 0) {
                    k = ~k - 1;
                }
                clr = checkpoints[k];
                chars = k * CheckpointInterval;
            }

            // characters in [clr, clrIndex): its CLR chars less the pairs completed before clrIndex
            return chars + (clrIndex - clr) - CountPairs(span.Slice(clr, clrIndex - clr));
        }

        /// <summary>
        /// From CLR index <paramref name="clr"/>, which starts character <paramref name="chars"/>,
        /// walks to the CLR index of character <paramref name="target"/>.
        /// </summary>
        private static int WalkToCharacter(ReadOnlySpan<char> span, int clr, int chars, int target) {
            while (true) {
                int remaining = target - chars;
                if (remaining == 0) {
                    return clr;
                }
                int j = span.Slice(clr).IndexOfAnyInRange('\uD800', '\uDBFF');
                if (j < 0 || j >= remaining) {
                    // no pair before the target: every character on the way is one char
                    return clr + remaining;
                }
                clr += j;
                chars += j;
                clr += (clr + 1 < span.Length && Char.IsLowSurrogate(span[clr + 1])) ? 2 : 1;
                chars++;
                if (clr > span.Length) {
                    return span.Length + (target - chars);
                }
            }
        }

        /// <summary>
        /// Well-formed surrogate pairs that lie wholly in <paramref name="span"/>. A leading half
        /// at the very end does not count, so that a CLR index between the two halves of a pair
        /// maps to the character after it.
        /// </summary>
        private static int CountPairs(ReadOnlySpan<char> span) {
            int pairs = 0;
            int i = 0;
            while (true) {
                int j = span.Slice(i).IndexOfAnyInRange('\uD800', '\uDBFF');
                if (j < 0) {
                    return pairs;
                }
                i += j + 1;
                if (i < span.Length && Char.IsLowSurrogate(span[i])) {
                    pairs++;
                    i++;
                }
            }
        }

        /// <summary>
        /// The table for the current content, or null when the content is too short to need one.
        /// Immutable content (a CLR string) keys its table itself, so every string sharing it -
        /// a clone, a frozen copy, the text a regex ran on - shares the table too, and it never
        /// goes stale. Mutable content keeps its table, retired by a mutation.
        /// </summary>
        private CharIndexTable GetCharIndexTable(ReadOnlySpan<char> span) {
            if (span.Length < CharIndexTableThreshold) {
                return null;
            }

            var content = _content;
            if (content is StringContent str) {
                return GetStringTable(str.Data);
            }

            var chars = content as CharArrayContent;
            if (chars == null) {
                return null;
            }

            CharIndexTable table = chars.CharIndexTable as CharIndexTable;
            if (table != null && (_flags & CharIndexChangedFlags) == 0) {
                return table;
            }

            chars.CharIndexTable = table = BuildCharIndexTable(span);
            _flags &= ~CharIndexChangedFlags;
            return table;
        }

        private static CharIndexTable GetStringTable(string/*!*/ text) {
            if (text.Length < CharIndexTableThreshold) {
                return null;
            }
            CharIndexTable table;
            if (!TryGetTable(text, out table)) {
                table = BuildCharIndexTable(text.AsSpan());
                StoreTable(text, table);
            }
            return table;
        }

        // The last table looked up on this thread: indexing one string over and over - the
        // common case - then costs a reference compare rather than a weak-table lookup.
        [ThreadStatic]
        private static string _lastTableKey;
        [ThreadStatic]
        private static CharIndexTable _lastTable;

        private static bool TryGetTable(string/*!*/ key, out CharIndexTable table) {
            if (ReferenceEquals(_lastTableKey, key)) {
                table = _lastTable;
                return true;
            }
            if (_charIndexTables.TryGetValue(key, out table)) {
                _lastTableKey = key;
                _lastTable = table;
                return true;
            }
            return false;
        }

        private static void StoreTable(string/*!*/ key, CharIndexTable/*!*/ table) {
            _charIndexTables.AddOrUpdate(key, table);
            _lastTableKey = key;
            _lastTable = table;
        }

        private static CharIndexTable/*!*/ BuildCharIndexTable(ReadOnlySpan<char> span) {
            var checkpoints = new int[span.Length / CheckpointInterval + 2];
            int k = 0;
            int chars = 0;
            int clr = 0;
            while (true) {
                int phase = chars % CheckpointInterval;
                if (phase == 0) {
                    checkpoints[k++] = clr;
                }
                if (clr >= span.Length) {
                    break;
                }
                // Pair-free text is crossed in one step, as far as the next checkpoint.
                int step = CheckpointInterval - phase;
                int j = span.Slice(clr).IndexOfAnyInRange('\uD800', '\uDBFF');
                if (j < 0 || j >= step) {
                    int advance = Math.Min(step, span.Length - clr);
                    clr += advance;
                    chars += advance;
                    continue;
                }
                clr += j;
                chars += j;
                clr += (clr + 1 < span.Length && Char.IsLowSurrogate(span[clr + 1])) ? 2 : 1;
                chars++;
            }
            Array.Resize(ref checkpoints, k);
            return new CharIndexTable { CharacterCount = chars, Checkpoints = checkpoints };
        }
    }
}

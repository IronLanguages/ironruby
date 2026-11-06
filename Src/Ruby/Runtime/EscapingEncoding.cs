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
using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Unicode;
using IronRuby.Builtins;

namespace IronRuby.Runtime {

    /// <summary>
    /// An encoding that never refuses a byte string.
    ///
    /// A Ruby string is a bag of bytes with an encoding label, and the two are allowed to
    /// disagree: "\xFF" tagged UTF-8 is a perfectly ordinary thing for a Ruby program to hold, and
    /// MRI will happily reverse it, index it, slice it and print it. IronRuby represents strings
    /// either as bytes or as characters and switches between the two on demand, and that switch
    /// used to go through the strict decoder, so every character based operation on such a string
    /// raised - in Ruby, as a bare System::Text::DecoderFallbackException.
    ///
    /// This decodes a byte that cannot begin a character to a lone low surrogate, U+DC00 plus the
    /// byte, and encodes that surrogate back to exactly the byte it came from. The mapping is
    /// therefore lossless, which is the whole point: the byte survives a round trip through the
    /// character representation, so #reverse, #index, #[] and the rest can work in characters and
    /// still produce the original bytes. It is the same trick as Python's surrogateescape.
    ///
    /// One undecodable byte becomes one character, which is also how MRI counts them - "a\xFFb" is
    /// three characters long in UTF-8 - so character offsets agree with MRI's as well.
    ///
    /// The range U+DC80..U+DCFF cannot occur in text decoded from any of the encodings this is
    /// used with: UTF-8 and friends cannot encode an unpaired surrogate at all, and a single byte
    /// encoding has no character above U+FFFF to pair one with. Validity is *not* decided here -
    /// String#valid_encoding? still asks the strict encoding, which is what makes it able to
    /// answer false for a string this class decoded happily.
    /// </summary>
    internal sealed class EscapingEncoding : Encoding {
        /// <summary>Lone low surrogates from here up stand for a single undecodable byte.</summary>
        internal const char EscapeBase = (char)0xDC00;

        private readonly Encoding/*!*/ _inner;

        internal EscapingEncoding(Encoding/*!*/ inner)
            : base(inner.CodePage) {
            _inner = inner;
        }

        internal static bool IsEscapedByte(char c) {
            return c >= (char)0xDC80 && c <= (char)0xDCFF;
        }

        /// <summary>The longest character any encoding used here produces.</summary>
        private const int MaxSequenceLength = 4;

        #region Decoding

        /// <summary>
        /// Length in bytes of the character starting at <paramref name="index"/>, or 0 if the
        /// bytes there do not begin one.
        /// </summary>
        private int SequenceLength(byte[]/*!*/ bytes, int index, int limit) {
            for (int length = 1; length <= MaxSequenceLength && index + length <= limit; length++) {
                try {
                    _inner.GetCharCount(bytes, index, length);
                    return length;
                } catch (DecoderFallbackException) {
                    // not a whole character yet - try one more byte
                }
            }
            return 0;
        }

        private bool IsUtf8 {
            get { return _inner.CodePage == 65001; }
        }

        /// <summary>
        /// UTF-8 has a validator and a decoder that report where invalid data is instead of throwing,
        /// so a string with invalid bytes costs a linear walk rather than an exception or several
        /// per character (the generic path below), which made e.g. #length on such a string crawl.
        /// Same result as the generic path: each byte that doesn't start a whole character is escaped
        /// on its own - a maximal invalid subsequence never contains the start of a valid character.
        /// </summary>
        private static int DecodeUtf8Escaping(ReadOnlySpan<byte> src, char[] chars, int charIndex) {
            if (chars == null) {
                // counting: decode into scratch space (a byte never makes more than one UTF-16 unit)
                char[] scratch = ArrayPool<char>.Shared.Rent(Math.Max(src.Length, 1));
                try {
                    return DecodeUtf8Escaping(src, new Span<char>(scratch, 0, src.Length));
                } finally {
                    ArrayPool<char>.Shared.Return(scratch);
                }
            }
            return DecodeUtf8Escaping(src, new Span<char>(chars, charIndex, chars.Length - charIndex));
        }

        private static int DecodeUtf8Escaping(ReadOnlySpan<byte> src, Span<char> dst) {
            int j = 0;
            while (true) {
                int read, written;
                var status = Utf8.ToUtf16(src, dst.Slice(j), out read, out written, false, true);
                j += written;
                src = src.Slice(read);
                if (status != OperationStatus.InvalidData) {
                    Debug.Assert(status == OperationStatus.Done);
                    return j;
                }

                Rune rune;
                int consumed;
                Rune.DecodeFromUtf8(src, out rune, out consumed);
                if (consumed <= 0) {
                    consumed = 1;
                }
                for (int k = 0; k < consumed; k++) {
                    dst[j++] = (char)(EscapeBase + src[k]);
                }
                src = src.Slice(consumed);
            }
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            if (IsUtf8) {
                var span = new ReadOnlySpan<byte>(bytes, index, count);
                return Utf8.IsValid(span) ? _inner.GetCharCount(bytes, index, count) : DecodeUtf8Escaping(span, null, 0);
            }

            // The overwhelmingly common case is a string whose bytes are valid, and that costs one
            // call. Only a string that is not valid pays for the walk below.
            try {
                return _inner.GetCharCount(bytes, index, count);
            } catch (DecoderFallbackException) {
                // fall through
            }

            int limit = index + count;
            int result = 0;
            int i = index;
            while (i < limit) {
                int length = SequenceLength(bytes, i, limit);
                if (length == 0) {
                    result++;
                    i++;
                } else {
                    result += _inner.GetCharCount(bytes, i, length);
                    i += length;
                }
            }
            return result;
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            if (IsUtf8) {
                var span = new ReadOnlySpan<byte>(bytes, byteIndex, byteCount);
                return Utf8.IsValid(span) ? _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex) : DecodeUtf8Escaping(span, chars, charIndex);
            }

            try {
                return _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
            } catch (DecoderFallbackException) {
                // fall through
            }

            int limit = byteIndex + byteCount;
            int i = byteIndex, j = charIndex;
            while (i < limit) {
                int length = SequenceLength(bytes, i, limit);
                if (length == 0) {
                    chars[j++] = (char)(EscapeBase + bytes[i]);
                    i++;
                } else {
                    j += _inner.GetChars(bytes, i, length, chars, j);
                    i += length;
                }
            }
            return j - charIndex;
        }

        public override string/*!*/ GetString(byte[]/*!*/ bytes, int index, int count) {
            if (IsUtf8 && !Utf8.IsValid(new ReadOnlySpan<byte>(bytes, index, count))) {
                var escaped = new char[GetCharCount(bytes, index, count)];
                return new string(escaped, 0, GetChars(bytes, index, count, escaped, 0));
            }

            try {
                return _inner.GetString(bytes, index, count);
            } catch (DecoderFallbackException) {
                // fall through
            }

            var chars = new char[GetCharCount(bytes, index, count)];
            int written = GetChars(bytes, index, count, chars, 0);
            return new string(chars, 0, written);
        }

        #endregion

        #region Encoding

        /// <summary>
        /// A surrogate with no partner. An escaped byte is one of these by construction, but so is
        /// half of a pair that got separated - String#[] on a non-BMP character can produce one -
        /// and the inner encoder throws on both. Neither may take the process down: MRI holds the
        /// bytes and prints them.
        /// </summary>
        private static bool IsUnpaired(char[]/*!*/ chars, int index, int limit) {
            char c = chars[index];
            if (c < (char)0xD800 || c > (char)0xDFFF) {
                return false;
            }
            if (c <= (char)0xDBFF) {
                // High: paired only if a low surrogate follows.
                return index + 1 >= limit || chars[index + 1] < (char)0xDC00 || chars[index + 1] > (char)0xDFFF;
            }
            // Low: paired only if a high surrogate precedes.
            return index == 0 || chars[index - 1] < (char)0xD800 || chars[index - 1] > (char)0xDBFF;
        }

        /// <summary>
        /// Bytes for an unpaired surrogate: the byte it stands for if it is an escape, and
        /// otherwise the three byte form CESU-8 uses for a surrogate, which is what a broken
        /// sequence would have held in the first place.
        /// </summary>
        private static int UnpairedByteCount(char c) {
            return IsEscapedByte(c) ? 1 : 3;
        }

        private static int WriteUnpaired(char c, byte[]/*!*/ bytes, int at) {
            if (IsEscapedByte(c)) {
                bytes[at] = (byte)(c - EscapeBase);
                return 1;
            }
            bytes[at] = (byte)(0xE0 | (c >> 12));
            bytes[at + 1] = (byte)(0x80 | ((c >> 6) & 0x3F));
            bytes[at + 2] = (byte)(0x80 | (c & 0x3F));
            return 3;
        }

        /// <summary>Index of the first surrogate in chars[index, limit), or limit (vectorized).</summary>
        private static int NextSurrogate(char[]/*!*/ chars, int index, int limit) {
            int k = new ReadOnlySpan<char>(chars, index, limit - index).IndexOfAnyInRange((char)0xD800, (char)0xDFFF);
            return k < 0 ? limit : index + k;
        }

        private static bool ContainsUnpaired(char[]/*!*/ chars, int index, int count) {
            // vectorized pre-check: most text has no surrogates at all
            if (new ReadOnlySpan<char>(chars, index, count).IndexOfAnyInRange((char)0xD800, (char)0xDFFF) < 0) {
                return false;
            }

            int limit = index + count;
            for (int i = index; (i = NextSurrogate(chars, i, limit)) < limit; i++) {
                if (IsUnpaired(chars, i, limit)) {
                    return true;
                }
            }
            return false;
        }

        // Encoding's own string overloads copy the string into a char[] first - twice for GetBytes(string), which is how
        // every source file reaches prism (3 MB of the 60 MB `ir -e 1` allocates). Without surrogates there is nothing
        // unpaired, and the inner encoding answers as the char[] overloads below would.
        public override int GetByteCount(string/*!*/ s) {
            return HasNoSurrogates(s) ? _inner.GetByteCount(s) : base.GetByteCount(s);
        }

        public override byte[]/*!*/ GetBytes(string/*!*/ s) {
            return HasNoSurrogates(s) ? _inner.GetBytes(s) : base.GetBytes(s);
        }

        private static bool HasNoSurrogates(string s) {
            if (s == null) {
                return false;
            }
            // A plain loop rather than IndexOfAnyInRange: that generic method has no precompiled code and its JIT
            // compilation (~2 ms) takes longer than this loop does over the whole prelude.
            foreach (char c in s) {
                if (char.IsSurrogate(c)) {
                    return false;
                }
            }
            return true;
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            if (!ContainsUnpaired(chars, index, count)) {
                return _inner.GetByteCount(chars, index, count);
            }

            int result = 0;
            int runStart = index;
            int limit = index + count;
            for (int i = index; (i = NextSurrogate(chars, i, limit)) < limit; i++) {
                if (IsUnpaired(chars, i, limit)) {
                    if (i > runStart) {
                        result += _inner.GetByteCount(chars, runStart, i - runStart);
                    }
                    result += UnpairedByteCount(chars[i]);
                    runStart = i + 1;
                }
            }
            if (limit > runStart) {
                result += _inner.GetByteCount(chars, runStart, limit - runStart);
            }
            return result;
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            if (!ContainsUnpaired(chars, charIndex, charCount)) {
                return _inner.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
            }

            int written = 0;
            int runStart = charIndex;
            int limit = charIndex + charCount;
            for (int i = charIndex; (i = NextSurrogate(chars, i, limit)) < limit; i++) {
                if (IsUnpaired(chars, i, limit)) {
                    if (i > runStart) {
                        written += _inner.GetBytes(chars, runStart, i - runStart, bytes, byteIndex + written);
                    }
                    written += WriteUnpaired(chars[i], bytes, byteIndex + written);
                    runStart = i + 1;
                }
            }
            if (limit > runStart) {
                written += _inner.GetBytes(chars, runStart, limit - runStart, bytes, byteIndex + written);
            }
            return written;
        }

        #endregion

        public override int GetMaxByteCount(int charCount) {
            return _inner.GetMaxByteCount(charCount);
        }

        public override int GetMaxCharCount(int byteCount) {
            return _inner.GetMaxCharCount(byteCount);
        }

        public override string/*!*/ EncodingName {
            get { return _inner.EncodingName; }
        }

        public override string/*!*/ WebName {
            get { return _inner.WebName; }
        }

        public override bool IsSingleByte {
            get { return _inner.IsSingleByte; }
        }
    }
}

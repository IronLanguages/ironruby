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
using System.Text;
using IronRuby.Builtins;

namespace IronRuby.Runtime {

    /// <summary>
    /// Encodings Ruby has and .NET does not.
    ///
    /// .NET only knows Windows code pages, so several encodings that every Ruby program can name
    /// have no System.Text counterpart at all, and a couple of others exist under a name that means
    /// something different in Ruby.  Without these, Encoding.find("UTF-16") silently answered with
    /// .NET's "utf-16", which is Ruby's UTF-16LE - a different encoding with a different byte order
    /// and, unlike Ruby's UTF-16, not a dummy.
    ///
    /// Each class below carries a code page from RubyEncoding's private block so that it can key
    /// the same tables as a real .NET encoding without ever colliding with one.
    /// </summary>
    internal static class RubyOnlyEncodings {
        internal static Encoding/*!*/ Create(int codepage, bool throwOnError) {
            switch (codepage) {
                case RubyEncoding.CodePageUTF16: return new BomEncoding(codepage, 2, throwOnError);
                case RubyEncoding.CodePageUTF32: return new BomEncoding(codepage, 4, throwOnError);
                case RubyEncoding.CodePageCESU8: return new CesuEncoding(throwOnError);
                case RubyEncoding.CodePageTIS620: return new Tis620Encoding(throwOnError);
                case RubyEncoding.CodePageEmacsMule: return new EmacsMuleEncoding(codepage, "Emacs-Mule", throwOnError);

                // Ruby's stateless-ISO-2022-JP is a replica of Emacs-Mule under another name.
                case RubyEncoding.CodePageStatelessISO2022JP: return new EmacsMuleEncoding(codepage, "stateless-ISO-2022-JP", throwOnError);

                case RubyEncoding.CodePageShiftJIS: return new RenamedEncoding(codepage, "Shift_JIS", RubyEncoding.CodePageSJIS, throwOnError);
                case RubyEncoding.CodePageUTF8Mac: return new RenamedEncoding(codepage, "UTF8-MAC", RubyEncoding.CodePageUTF8, throwOnError);
                case RubyEncoding.CodePageCP51932: return new RenamedEncoding(codepage, "CP51932", RubyEncoding.CodePageEUCJP, throwOnError);
                case RubyEncoding.CodePageISO2022JP2: return new RenamedEncoding(codepage, "ISO-2022-JP-2", 50220, throwOnError);
                case RubyEncoding.CodePageGB12345: return new RenamedEncoding(codepage, "GB12345", 51936, throwOnError);

                case RubyEncoding.CodePageEUCJP: return new EucJpEncoding(codepage, "EUC-JP", throwOnError);
                case RubyEncoding.CodePageEucJpMs: return new EucJpEncoding(codepage, "eucJP-ms", throwOnError);
                case RubyEncoding.CodePageEUCTW: return new EucTwEncoding(throwOnError);

                case RubyEncoding.CodePageGB1988: return new SingleByteTableEncoding(codepage, "GB1988", null, throwOnError);
                case RubyEncoding.CodePageISO8859_10: return new SingleByteTableEncoding(codepage, "ISO-8859-10", SingleByteTableEncoding.Iso8859_10, throwOnError);
                case RubyEncoding.CodePageISO8859_14: return new SingleByteTableEncoding(codepage, "ISO-8859-14", SingleByteTableEncoding.Iso8859_14, throwOnError);
                case RubyEncoding.CodePageISO8859_16: return new SingleByteTableEncoding(codepage, "ISO-8859-16", SingleByteTableEncoding.Iso8859_16, throwOnError);
                default: throw new ArgumentOutOfRangeException("codepage");
            }
        }

        internal static bool IsRubyOnly(int codepage) {
            switch (codepage) {
                case RubyEncoding.CodePageUTF16:
                case RubyEncoding.CodePageUTF32:
                case RubyEncoding.CodePageCESU8:
                case RubyEncoding.CodePageTIS620:
                case RubyEncoding.CodePageEmacsMule:
                case RubyEncoding.CodePageStatelessISO2022JP:
                case RubyEncoding.CodePageShiftJIS:
                case RubyEncoding.CodePageUTF8Mac:
                case RubyEncoding.CodePageCP51932:
                case RubyEncoding.CodePageISO2022JP2:
                case RubyEncoding.CodePageGB12345:
                case RubyEncoding.CodePageEUCJP:
                case RubyEncoding.CodePageEucJpMs:
                case RubyEncoding.CodePageEUCTW:
                case RubyEncoding.CodePageGB1988:
                case RubyEncoding.CodePageISO8859_10:
                case RubyEncoding.CodePageISO8859_14:
                case RubyEncoding.CodePageISO8859_16:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// The code page whose character set properties (single or double byte) an encoding
        /// shares: the .NET code page a renamed encoding delegates to, or the code page itself.
        /// </summary>
        internal static int GetTableCodePage(int codepage) {
            switch (codepage) {
                case RubyEncoding.CodePageShiftJIS: return RubyEncoding.CodePageSJIS;
                case RubyEncoding.CodePageUTF8Mac: return RubyEncoding.CodePageUTF8;
                case RubyEncoding.CodePageCP51932: return RubyEncoding.CodePageEUCJP;
                case RubyEncoding.CodePageEucJpMs: return RubyEncoding.CodePageEUCJP;
                case RubyEncoding.CodePageGB12345: return 51936;
                case RubyEncoding.CodePageISO8859_10:
                case RubyEncoding.CodePageISO8859_14:
                case RubyEncoding.CodePageISO8859_16:
                case RubyEncoding.CodePageGB1988:
                    return 28591;
                default: return codepage;
            }
        }

        /// <summary>
        /// Whether the bytes are well formed in the encoding, for the encodings that can say so
        /// without decoding them; null for everything else. Ruby decides validity by the shape of
        /// a byte sequence alone, and several of these encodings have well formed sequences that
        /// name no character - those are valid, they just cannot be transcoded.
        /// </summary>
        internal static bool? IsValid(Encoding/*!*/ encoding, byte[]/*!*/ bytes, int index, int count) {
            var structured = encoding as IStructuredEncoding;
            if (structured != null) {
                return structured.FindInvalid(bytes, index, count) < 0;
            }

            // .NET's code page 932 decodes 0x80, 0xA0 and 0xFD-0xFF as characters of their own;
            // in Ruby's Shift_JIS and Windows-31J they begin nothing.
            if (encoding.CodePage == RubyEncoding.CodePageSJIS || encoding.CodePage == RubyEncoding.CodePageShiftJIS) {
                return FindInvalidShiftJis(bytes, index, count) < 0;
            }

            // Every byte of a single byte encoding is a character as far as Ruby is concerned,
            // even the ones its conversion table leaves undefined. US-ASCII is the exception: it
            // is seven bit.
            if (encoding.IsSingleByte && encoding.CodePage != RubyEncoding.CodePageAscii && encoding.CodePage != RubyEncoding.CodePageBinary) {
                return true;
            }
            return null;
        }

        private static int FindInvalidShiftJis(byte[]/*!*/ bytes, int index, int count) {
            int end = index + count;
            int i = index;
            while (i < end) {
                byte b = bytes[i];
                if (b < 0x80 || b >= 0xa1 && b <= 0xdf) {
                    i++;
                } else if (b >= 0x81 && b <= 0x9f || b >= 0xe0 && b <= 0xfc) {
                    if (i + 1 >= end) {
                        return i;
                    }
                    byte trail = bytes[i + 1];
                    if (trail < 0x40 || trail == 0x7f || trail > 0xfc) {
                        return i;
                    }
                    i += 2;
                } else {
                    return i;
                }
            }
            return -1;
        }
    }

    /// <summary>
    /// An encoding that knows which of its byte sequences are well formed without decoding them.
    /// </summary>
    internal interface IStructuredEncoding {
        /// <summary>The index of the first byte that is not part of a well formed sequence, or -1.</summary>
        int FindInvalid(byte[]/*!*/ bytes, int index, int count);
    }

    /// <summary>
    /// A .NET encoding under the name of a distinct Ruby encoding - Shift_JIS is .NET's code
    /// page 932 (which is Ruby's Windows-31J; Ruby's Shift_JIS lacks the Microsoft extensions), UTF8-MAC is UTF-8, and so on.
    /// Ruby keeps these apart, so they need a code page of their own; the bytes are the inner
    /// encoding's.
    /// </summary>
    internal sealed class RenamedEncoding : Encoding {
        private readonly Encoding/*!*/ _inner;
        private readonly string/*!*/ _name;

        internal RenamedEncoding(int codepage, string/*!*/ name, int innerCodepage, bool throwOnError)
            : base(codepage) {
            _name = name;
            _inner = throwOnError ?
                Encoding.GetEncoding(innerCodepage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback) :
                Encoding.GetEncoding(innerCodepage, EncoderFallback.ReplacementFallback, BinaryDecoderFallback.Instance);
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            return _inner.GetByteCount(chars, index, count);
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            return _inner.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            return _inner.GetCharCount(bytes, index, count);
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            return _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        }

        public override Decoder/*!*/ GetDecoder() {
            return _inner.GetDecoder();
        }

        public override Encoder/*!*/ GetEncoder() {
            return _inner.GetEncoder();
        }

        public override int GetMaxByteCount(int charCount) {
            return _inner.GetMaxByteCount(charCount);
        }

        public override int GetMaxCharCount(int byteCount) {
            return _inner.GetMaxCharCount(byteCount);
        }

        public override byte[]/*!*/ GetPreamble() {
            return new byte[0];
        }

        public override bool IsSingleByte {
            get { return _inner.IsSingleByte; }
        }

        public override string/*!*/ EncodingName {
            get { return _name; }
        }

        public override string/*!*/ WebName {
            get { return _name; }
        }
    }

    /// <summary>
    /// A single byte, ASCII compatible encoding .NET does not have, given as the characters of
    /// bytes 0x80-0xFF (U+FFFF where the byte is undefined). A null table passes the high bytes
    /// through as U+0080-U+00FF: that is for encodings Ruby ships no converter for, where only
    /// the byte matters.
    /// </summary>
    internal sealed class SingleByteTableEncoding : Encoding, IStructuredEncoding {
        private readonly bool _throwOnError;
        private readonly string/*!*/ _name;
        private readonly char[] _table;
        // built on first use: every Encoding constant makes one of these, and few are ever used to encode
        // (a Dictionary<char, byte> is also code ReadyToRun cannot precompile)
        private System.Collections.Generic.Dictionary<char, byte> _reverse;

        internal SingleByteTableEncoding(int codepage, string/*!*/ name, char[] table, bool throwOnError)
            : base(codepage) {
            _name = name;
            _table = table;
            _throwOnError = throwOnError;
        }

        private System.Collections.Generic.Dictionary<char, byte>/*!*/ GetReverseTable() {
            var reverse = _reverse;
            if (reverse == null) {
                reverse = new System.Collections.Generic.Dictionary<char, byte>();
                for (int i = 0; i < _table.Length; i++) {
                    if (_table[i] != '\uffff' && !reverse.ContainsKey(_table[i])) {
                        reverse.Add(_table[i], (byte)(0x80 + i));
                    }
                }
                _reverse = reverse;
            }
            return reverse;
        }

        public int FindInvalid(byte[]/*!*/ bytes, int index, int count) {
            return -1;
        }

        private int ToByte(char c) {
            if (c < 0x80) {
                return c;
            }
            if (_table == null) {
                return c <= 0xff ? c : -1;
            }
            byte b;
            return GetReverseTable().TryGetValue(c, out b) ? b : -1;
        }

        private bool TryGetChar(byte b, out char c) {
            if (b < 0x80 || _table == null) {
                c = (char)b;
                return true;
            }
            c = _table[b - 0x80];
            return c != '\uffff';
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            if (_throwOnError) {
                for (int i = 0; i < count; i++) {
                    if (ToByte(chars[index + i]) < 0) {
                        throw new EncoderFallbackException();
                    }
                }
            }
            return count;
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            for (int i = 0; i < charCount; i++) {
                int b = ToByte(chars[charIndex + i]);
                if (b < 0) {
                    if (_throwOnError) {
                        throw new EncoderFallbackException();
                    }
                    b = '?';
                }
                bytes[byteIndex + i] = (byte)b;
            }
            return charCount;
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            if (_throwOnError) {
                char c;
                for (int i = 0; i < count; i++) {
                    if (!TryGetChar(bytes[index + i], out c)) {
                        throw new DecoderFallbackException(null, new[] { bytes[index + i] }, index + i);
                    }
                }
            }
            return count;
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            for (int i = 0; i < byteCount; i++) {
                byte b = bytes[byteIndex + i];
                char c;
                if (!TryGetChar(b, out c)) {
                    if (_throwOnError) {
                        throw new DecoderFallbackException(null, new[] { b }, byteIndex + i);
                    }
                    // BinaryDecoderFallback's convention: the byte passes through as U+00mn.
                    c = (char)b;
                }
                chars[charIndex + i] = c;
            }
            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) {
            return charCount;
        }

        public override int GetMaxCharCount(int byteCount) {
            return byteCount;
        }

        public override bool IsSingleByte {
            get { return true; }
        }

        public override string/*!*/ EncodingName {
            get { return _name; }
        }

        public override string/*!*/ WebName {
            get { return _name; }
        }

        private static char[]/*!*/ Latin(string/*!*/ high) {
            // C1 controls at 0x80-0x9F, then the 96 characters given for 0xA0-0xFF.
            var result = new char[128];
            for (int i = 0; i < 32; i++) {
                result[i] = (char)(0x80 + i);
            }
            var parts = high.Split(' ');
            for (int i = 0; i < 96; i++) {
                result[32 + i] = (char)System.Convert.ToInt32(parts[i], 16);
            }
            return result;
        }

        internal static readonly char[]/*!*/ Iso8859_10 = Latin(
            "A0 104 112 122 12A 128 136 A7 13B 110 160 166 17D AD 16A 14A " +
            "B0 105 113 123 12B 129 137 B7 13C 111 161 167 17E 2015 16B 14B " +
            "100 C1 C2 C3 C4 C5 C6 12E 10C C9 118 CB 116 CD CE CF " +
            "D0 145 14C D3 D4 D5 D6 168 D8 172 DA DB DC DD DE DF " +
            "101 E1 E2 E3 E4 E5 E6 12F 10D E9 119 EB 117 ED EE EF " +
            "F0 146 14D F3 F4 F5 F6 169 F8 173 FA FB FC FD FE 138");

        internal static readonly char[]/*!*/ Iso8859_14 = Latin(
            "A0 1E02 1E03 A3 10A 10B 1E0A A7 1E80 A9 1E82 1E0B 1EF2 AD AE 178 " +
            "1E1E 1E1F 120 121 1E40 1E41 B6 1E56 1E81 1E57 1E83 1E60 1EF3 1E84 1E85 1E61 " +
            "C0 C1 C2 C3 C4 C5 C6 C7 C8 C9 CA CB CC CD CE CF " +
            "174 D1 D2 D3 D4 D5 D6 1E6A D8 D9 DA DB DC DD 176 DF " +
            "E0 E1 E2 E3 E4 E5 E6 E7 E8 E9 EA EB EC ED EE EF " +
            "175 F1 F2 F3 F4 F5 F6 1E6B F8 F9 FA FB FC FD 177 FF");

        internal static readonly char[]/*!*/ Iso8859_16 = Latin(
            "A0 104 105 141 20AC 201E 160 A7 161 A9 218 AB 179 AD 17A 17B " +
            "B0 B1 10C 142 17D 201D B6 B7 17E 10D 219 BB 152 153 178 17C " +
            "C0 C1 C2 102 C4 106 C6 C7 C8 C9 CA CB CC CD CE CF " +
            "110 143 D2 D3 D4 150 D6 15A 170 D9 DA DB DC 118 21A DF " +
            "E0 E1 E2 103 E4 107 E6 E7 E8 E9 EA EB EC ED EE EF " +
            "111 144 F2 F3 F4 151 F6 15B 171 F9 FA FB FC 119 21B FF");
    }

    /// <summary>
    /// A multibyte encoding decoded sequence by sequence: subclasses say how long the sequence at
    /// a position is and what it stands for, and this supplies a decoder that carries an
    /// unfinished sequence over from one call to the next, which the transcoder relies on - it
    /// feeds its decoder one byte at a time.
    /// </summary>
    internal abstract class MultiByteEncoding : Encoding, IStructuredEncoding {
        private readonly bool _throwOnError;
        private readonly string/*!*/ _name;

        protected MultiByteEncoding(int codepage, string/*!*/ name, bool throwOnError)
            : base(codepage,
                throwOnError ? EncoderFallback.ExceptionFallback : EncoderFallback.ReplacementFallback,
                throwOnError ? DecoderFallback.ExceptionFallback : (DecoderFallback)BinaryDecoderFallback.Instance) {
            _name = name;
            _throwOnError = throwOnError;
        }

        /// <summary>Longest sequence, in bytes.</summary>
        protected abstract int MaxSequenceLength { get; }

        /// <summary>
        /// Length of the well formed sequence at <paramref name="index"/>; 0 if the bytes up to
        /// <paramref name="end"/> are the beginning of one but it is cut short; or minus the
        /// number of bytes that form no sequence.
        /// </summary>
        protected abstract int Scan(byte[]/*!*/ bytes, int index, int end);

        /// <summary>
        /// Writes the characters of a well formed sequence and returns how many, or -1 if the
        /// sequence names no character.
        /// </summary>
        protected abstract int DecodeSequence(byte[]/*!*/ bytes, int index, int length, char[]/*!*/ chars, int charIndex);

        /// <summary>
        /// Writes the bytes for the character at <paramref name="index"/> and returns how many,
        /// or -1 if the encoding has no bytes for it. <paramref name="used"/> is the number of
        /// chars that make up the character.
        /// </summary>
        protected abstract int EncodeCharacter(char[]/*!*/ chars, int index, int end, byte[]/*!*/ bytes, int byteIndex, out int used);

        public int FindInvalid(byte[]/*!*/ bytes, int index, int count) {
            int end = index + count;
            int i = index;
            while (i < end) {
                int length = Scan(bytes, i, end);
                if (length <= 0) {
                    return i;
                }
                i += length;
            }
            return -1;
        }

        #region Encoding

        private int Encode(char[]/*!*/ chars, int index, int count, byte[] bytes, int byteIndex) {
            var buffer = new byte[8];
            int end = index + count;
            int i = index, written = 0;
            while (i < end) {
                int used;
                int length = EncodeCharacter(chars, i, end, buffer, 0, out used);
                if (length < 0) {
                    if (_throwOnError) {
                        throw new EncoderFallbackException();
                    }
                    buffer[0] = (byte)'?';
                    length = 1;
                }
                if (bytes != null) {
                    Array.Copy(buffer, 0, bytes, byteIndex + written, length);
                }
                written += length;
                i += used;
            }
            return written;
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            return Encode(chars, index, count, null, 0);
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            return Encode(chars, charIndex, charCount, bytes, byteIndex);
        }

        public override int GetMaxByteCount(int charCount) {
            return (charCount + 1) * MaxSequenceLength;
        }

        #endregion

        #region Decoding

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            return new SequenceDecoder(this).GetCharCount(bytes, index, count, true);
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            return new SequenceDecoder(this).GetChars(bytes, byteIndex, byteCount, chars, charIndex, true);
        }

        public override Decoder/*!*/ GetDecoder() {
            return new SequenceDecoder(this);
        }

        public override int GetMaxCharCount(int byteCount) {
            // a byte that forms no sequence falls back to at most one character per byte
            return byteCount + 1;
        }

        private sealed class SequenceDecoder : Decoder {
            private readonly MultiByteEncoding/*!*/ _encoding;
            private byte[]/*!*/ _pending = new byte[8];
            private int _pendingCount;

            internal SequenceDecoder(MultiByteEncoding/*!*/ encoding) {
                _encoding = encoding;
                Fallback = encoding.DecoderFallback;
            }

            public override void Reset() {
                _pendingCount = 0;
            }

            private int Run(byte[]/*!*/ bytes, int index, int count, char[] chars, int charIndex, bool flush, bool commit) {
                byte[] input;
                int start, end, pending = _pendingCount;
                if (pending == 0) {
                    input = bytes;
                    start = index;
                    end = index + count;
                } else {
                    input = new byte[pending + count];
                    Array.Copy(_pending, 0, input, 0, pending);
                    Array.Copy(bytes, index, input, pending, count);
                    start = 0;
                    end = input.Length;
                }
                if (commit) {
                    _pendingCount = 0;
                }

                var fallback = (Fallback ?? DecoderFallback.ReplacementFallback).CreateFallbackBuffer();
                var decoded = new char[_encoding.MaxSequenceLength + 2];
                int i = start, written = 0;
                while (i < end) {
                    int length = _encoding.Scan(input, i, end);
                    if (length == 0) {
                        if (!flush) {
                            if (commit) {
                                _pendingCount = end - i;
                                Array.Copy(input, i, _pending, 0, _pendingCount);
                            }
                            break;
                        }
                        length = -(end - i);
                    }

                    if (length > 0) {
                        int n = _encoding.DecodeSequence(input, i, length, decoded, 0);
                        if (n >= 0) {
                            if (chars != null) {
                                Array.Copy(decoded, 0, chars, charIndex + written, n);
                            }
                            written += n;
                            i += length;
                            continue;
                        }
                        length = -length;
                    }

                    var unknown = new byte[-length];
                    Array.Copy(input, i, unknown, 0, unknown.Length);
                    if (fallback.Fallback(unknown, Math.Max(0, i - start - pending))) {
                        while (fallback.Remaining > 0) {
                            char c = fallback.GetNextChar();
                            if (chars != null) {
                                chars[charIndex + written] = c;
                            }
                            written++;
                        }
                    }
                    i += unknown.Length;
                }
                return written;
            }

            public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
                return GetCharCount(bytes, index, count, false);
            }

            public override int GetCharCount(byte[]/*!*/ bytes, int index, int count, bool flush) {
                return Run(bytes, index, count, null, 0, flush, false);
            }

            public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
                return GetChars(bytes, byteIndex, byteCount, chars, charIndex, false);
            }

            public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex, bool flush) {
                return Run(bytes, byteIndex, byteCount, chars, charIndex, flush, true);
            }
        }

        #endregion

        public override bool IsSingleByte {
            get { return false; }
        }

        public override string/*!*/ EncodingName {
            get { return _name; }
        }

        public override string/*!*/ WebName {
            get { return _name; }
        }

        protected static bool IsGraphic(byte b) {
            return b >= 0xa1 && b <= 0xfe;
        }
    }

    /// <summary>
    /// EUC-JP as Ruby has it: ASCII, half-width katakana after 0x8E, JIS X 0208 in two bytes and
    /// JIS X 0212 in three bytes after 0x8F. .NET's own EUC-JP (code page 51932) has no JIS X 0212
    /// at all; its code page 20932 has both character sets, but not in EUC form, so the tables
    /// are read out of that one.
    /// </summary>
    internal sealed class EucJpEncoding : MultiByteEncoding {
        private static char[] _jis0208, _jis0212;
        private static System.Collections.Generic.Dictionary<char, int> _reverse;
        private static readonly object _lock = new object();

        internal EucJpEncoding(int codepage, string/*!*/ name, bool throwOnError)
            : base(codepage, name, throwOnError) {
        }

        protected override int MaxSequenceLength {
            get { return 3; }
        }

        protected override int Scan(byte[]/*!*/ bytes, int index, int end) {
            byte b = bytes[index];
            if (b < 0x80) {
                return 1;
            }
            int length;
            if (b == 0x8e || IsGraphic(b)) {
                length = 2;
            } else if (b == 0x8f) {
                length = 3;
            } else {
                return -1;
            }
            for (int i = 1; i < length; i++) {
                if (index + i >= end) {
                    return 0;
                }
                if (!IsGraphic(bytes[index + i])) {
                    return -i;
                }
            }
            return length;
        }

        private static void EnsureTables() {
            if (_reverse != null) {
                return;
            }
            lock (_lock) {
                if (_reverse != null) {
                    return;
                }
                var jis = Encoding.GetEncoding(20932, EncoderFallback.ExceptionFallback, new DecoderReplacementFallback("\uffff"));
                var jis0208 = new char[94 * 94];
                var jis0212 = new char[94 * 94];
                var pair = new byte[2];
                var decoded = new char[4];
                for (int row = 0; row < 94; row++) {
                    for (int cell = 0; cell < 94; cell++) {
                        pair[0] = (byte)(0xa1 + row);
                        pair[1] = (byte)(0xa1 + cell);
                        jis0208[row * 94 + cell] = jis.GetChars(pair, 0, 2, decoded, 0) == 1 ? decoded[0] : '\uffff';
                        pair[1] = (byte)(0x21 + cell);
                        jis0212[row * 94 + cell] = jis.GetChars(pair, 0, 2, decoded, 0) == 1 ? decoded[0] : '\uffff';
                    }
                }

                // Code page 20932 is a Microsoft table: it adds the NEC special characters (row 13
                // of JIS X 0208), the IBM extensions (rows 83 and 84 of JIS X 0212) and user defined
                // characters from row 85 on, none of which Ruby's EUC-JP has, and it differs from
                // Ruby in three places.
                for (int row = 0; row < 94; row++) {
                    if (row == 12 || row >= 84) {
                        ClearRow(jis0208, row);
                    }
                    if (row >= 82) {
                        ClearRow(jis0212, row);
                    }
                }
                jis0208[0 * 94 + 28] = '\u2014'; // 0xA1BD: EM DASH, not HORIZONTAL BAR
                jis0212[1 * 94 + 22] = '~';       // 0x8FA2B7
                jis0212[1 * 94 + 80] = '\u2116'; // 0x8FA2F1: NUMERO SIGN

                var reverse = new System.Collections.Generic.Dictionary<char, int>();
                for (int i = 0; i < jis0208.Length; i++) {
                    if (jis0208[i] != '\uffff' && !reverse.ContainsKey(jis0208[i])) {
                        reverse.Add(jis0208[i], ((0xa1 + i / 94) << 8) | (0xa1 + i % 94));
                    }
                }
                for (int i = 0; i < jis0212.Length; i++) {
                    if (jis0212[i] != '\uffff' && !reverse.ContainsKey(jis0212[i])) {
                        reverse.Add(jis0212[i], 0x8f0000 | ((0xa1 + i / 94) << 8) | (0xa1 + i % 94));
                    }
                }
                // Ruby still encodes HORIZONTAL BAR where 20932 put it
                if (!reverse.ContainsKey('\u2015')) {
                    reverse.Add('\u2015', 0xa1bd);
                }

                _jis0208 = jis0208;
                _jis0212 = jis0212;
                System.Threading.Thread.MemoryBarrier();
                _reverse = reverse;
            }
        }

        private static void ClearRow(char[]/*!*/ table, int row) {
            for (int cell = 0; cell < 94; cell++) {
                table[row * 94 + cell] = '\uffff';
            }
        }

        protected override int DecodeSequence(byte[]/*!*/ bytes, int index, int length, char[]/*!*/ chars, int charIndex) {
            char c;
            switch (length) {
                case 1:
                    c = (char)bytes[index];
                    break;

                case 2:
                    if (bytes[index] == 0x8e) {
                        byte kana = bytes[index + 1];
                        if (kana > 0xdf) {
                            return -1;
                        }
                        c = (char)(0xff61 + kana - 0xa1);
                    } else {
                        EnsureTables();
                        c = _jis0208[(bytes[index] - 0xa1) * 94 + bytes[index + 1] - 0xa1];
                    }
                    break;

                default:
                    EnsureTables();
                    c = _jis0212[(bytes[index + 1] - 0xa1) * 94 + bytes[index + 2] - 0xa1];
                    break;
            }
            if (c == '\uffff') {
                return -1;
            }
            chars[charIndex] = c;
            return 1;
        }

        protected override int EncodeCharacter(char[]/*!*/ chars, int index, int end, byte[]/*!*/ bytes, int byteIndex, out int used) {
            char c = chars[index];
            used = 1;
            if (c < 0x80) {
                bytes[byteIndex] = (byte)c;
                return 1;
            }
            if (c >= 0xff61 && c <= 0xff9f) {
                bytes[byteIndex] = 0x8e;
                bytes[byteIndex + 1] = (byte)(c - 0xff61 + 0xa1);
                return 2;
            }
            if (Char.IsHighSurrogate(c) && index + 1 < end && Char.IsLowSurrogate(chars[index + 1])) {
                used = 2;
                return -1;
            }
            EnsureTables();
            int code;
            if (!_reverse.TryGetValue(c, out code)) {
                return -1;
            }
            if (code > 0xffff) {
                bytes[byteIndex] = 0x8f;
                bytes[byteIndex + 1] = (byte)(code >> 8);
                bytes[byteIndex + 2] = (byte)code;
                return 3;
            }
            bytes[byteIndex] = (byte)(code >> 8);
            bytes[byteIndex + 1] = (byte)code;
            return 2;
        }
    }

    /// <summary>
    /// EUC-TW: ASCII, CNS 11643 plane 1 in two bytes, and any plane in four bytes after 0x8E.
    /// .NET has no CNS 11643 table, so, as with Emacs-Mule, a byte is carried across as itself
    /// and what this decides is which byte runs are well formed.
    /// </summary>
    internal sealed class EucTwEncoding : MultiByteEncoding {
        internal EucTwEncoding(bool throwOnError)
            : base(RubyEncoding.CodePageEUCTW, "EUC-TW", throwOnError) {
        }

        protected override int MaxSequenceLength {
            get { return 4; }
        }

        protected override int Scan(byte[]/*!*/ bytes, int index, int end) {
            byte b = bytes[index];
            if (b < 0x80) {
                return 1;
            }
            int length;
            if (IsGraphic(b)) {
                length = 2;
            } else if (b == 0x8e) {
                length = 4;
            } else {
                return -1;
            }
            for (int i = 1; i < length; i++) {
                if (index + i >= end) {
                    return 0;
                }
                byte c = bytes[index + i];
                if (length == 4 && i == 1 ? !(c >= 0xa1 && c <= 0xb0) : !IsGraphic(c)) {
                    return -i;
                }
            }
            return length;
        }

        protected override int DecodeSequence(byte[]/*!*/ bytes, int index, int length, char[]/*!*/ chars, int charIndex) {
            for (int i = 0; i < length; i++) {
                chars[charIndex + i] = (char)bytes[index + i];
            }
            return length;
        }

        protected override int EncodeCharacter(char[]/*!*/ chars, int index, int end, byte[]/*!*/ bytes, int byteIndex, out int used) {
            used = 1;
            char c = chars[index];
            if (c > 0xff) {
                return -1;
            }
            bytes[byteIndex] = (byte)c;
            return 1;
        }
    }

    /// <summary>
    /// Ruby's "UTF-16" and "UTF-32": the byte order is carried by a BOM instead of by the encoding
    /// name.  Both are dummy encodings in Ruby - a string tagged with one of them is a byte string,
    /// because nothing can be said about a character until the BOM has been read.
    ///
    /// Ruby writes big endian with a BOM and reads whichever order the BOM asks for, defaulting to
    /// big endian when there is none, which is what this implements.
    /// </summary>
    internal sealed class BomEncoding : Encoding {
        private readonly int _unit;         // bytes per code unit: 2 for UTF-16, 4 for UTF-32
        private readonly Encoding/*!*/ _bigEndian;
        private readonly Encoding/*!*/ _littleEndian;
        private readonly string/*!*/ _name;

        internal BomEncoding(int codepage, int unit, bool throwOnError)
            : base(codepage) {
            _unit = unit;
            _name = unit == 2 ? "UTF-16" : "UTF-32";
            if (unit == 2) {
                _bigEndian = new UnicodeEncoding(true, false, throwOnError);
                _littleEndian = new UnicodeEncoding(false, false, throwOnError);
            } else {
                _bigEndian = new UTF32Encoding(true, false, throwOnError);
                _littleEndian = new UTF32Encoding(false, false, throwOnError);
            }
        }

        /// <summary>Big endian BOM: FE FF, or 00 00 FE FF.</summary>
        private byte[]/*!*/ Bom {
            get { return _unit == 2 ? new byte[] { 0xfe, 0xff } : new byte[] { 0x00, 0x00, 0xfe, 0xff }; }
        }

        /// <summary>
        /// Reports which endianness the input asks for and how many bytes of BOM to skip.
        /// </summary>
        private Encoding/*!*/ Sniff(byte[]/*!*/ bytes, int index, int count, out int skip) {
            if (count >= _unit) {
                if (_unit == 2) {
                    if (bytes[index] == 0xfe && bytes[index + 1] == 0xff) {
                        skip = 2;
                        return _bigEndian;
                    }
                    if (bytes[index] == 0xff && bytes[index + 1] == 0xfe) {
                        skip = 2;
                        return _littleEndian;
                    }
                } else {
                    if (bytes[index] == 0 && bytes[index + 1] == 0 && bytes[index + 2] == 0xfe && bytes[index + 3] == 0xff) {
                        skip = 4;
                        return _bigEndian;
                    }
                    if (bytes[index] == 0xff && bytes[index + 1] == 0xfe && bytes[index + 2] == 0 && bytes[index + 3] == 0) {
                        skip = 4;
                        return _littleEndian;
                    }
                }
            }

            skip = 0;
            return _bigEndian;
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            return _unit + _bigEndian.GetByteCount(chars, index, count);
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            var bom = Bom;
            Array.Copy(bom, 0, bytes, byteIndex, bom.Length);
            return bom.Length + _bigEndian.GetBytes(chars, charIndex, charCount, bytes, byteIndex + bom.Length);
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            int skip;
            var inner = Sniff(bytes, index, count, out skip);
            return inner.GetCharCount(bytes, index + skip, count - skip);
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            int skip;
            var inner = Sniff(bytes, byteIndex, byteCount, out skip);
            return inner.GetChars(bytes, byteIndex + skip, byteCount - skip, chars, charIndex);
        }

        public override int GetMaxByteCount(int charCount) {
            return _unit + _bigEndian.GetMaxByteCount(charCount);
        }

        public override int GetMaxCharCount(int byteCount) {
            return _bigEndian.GetMaxCharCount(byteCount);
        }

        public override byte[]/*!*/ GetPreamble() {
            return Bom;
        }

        public override string/*!*/ EncodingName {
            get { return _name; }
        }

        public override string/*!*/ WebName {
            get { return _name; }
        }
    }

    /// <summary>
    /// Emacs-Mule: ASCII plus multibyte sequences introduced by a byte in 0x81-0x99 and continued
    /// by bytes of 0xA0 and above. Ruby has it; .NET does not, and Ruby ships no converter to or
    /// from it, so nothing here has to name the characters those sequences stand for - a byte is
    /// carried across as itself, and what this encoding decides is which byte runs are well formed
    /// in the first place, for #valid_encoding? and the like.
    /// </summary>
    internal sealed class EmacsMuleEncoding : Encoding, IStructuredEncoding {
        private readonly bool _throwOnError;
        private readonly string/*!*/ _name;

        internal EmacsMuleEncoding(int codepage, string/*!*/ name, bool throwOnError)
            : base(codepage) {
            _name = name;
            _throwOnError = throwOnError;
        }

        int IStructuredEncoding.FindInvalid(byte[]/*!*/ bytes, int index, int count) {
            return FindInvalid(bytes, index, count);
        }

        /// <summary>The length of the sequence a byte introduces, or 0 if it introduces none.</summary>
        private static int SequenceLength(byte lead) {
            if (lead < 0x80) {
                return 1;
            }
            if (lead >= 0x81 && lead <= 0x8f) {
                return 2;
            }
            if (lead >= 0x90 && lead <= 0x99) {
                return 3;
            }
            return 0;
        }

        /// <summary>The index of the first byte that is not part of a well formed sequence, or -1.</summary>
        private static int FindInvalid(byte[]/*!*/ bytes, int index, int count) {
            int end = index + count;
            int i = index;
            while (i < end) {
                int length = SequenceLength(bytes[i]);
                if (length == 0 || end - i < length) {
                    return i;
                }
                for (int j = 1; j < length; j++) {
                    // A continuation byte is 0xa0 or above; anything else cuts the sequence short.
                    if (bytes[i + j] < 0xa0) {
                        return i;
                    }
                }
                i += length;
            }
            return -1;
        }

        /// <summary>See CesuEncoding.Undecodable - BytesUnknown must not be left null.</summary>
        private static DecoderFallbackException/*!*/ Undecodable(byte[]/*!*/ bytes, int index) {
            return new DecoderFallbackException(null, new[] { bytes[index] }, index);
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            if (_throwOnError) {
                for (int i = 0; i < count; i++) {
                    if (chars[index + i] > 0xff) {
                        throw new EncoderFallbackException();
                    }
                }
            }
            return count;
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            for (int i = 0; i < charCount; i++) {
                char c = chars[charIndex + i];
                if (c > 0xff) {
                    if (_throwOnError) {
                        throw new EncoderFallbackException();
                    }
                    c = '?';
                }
                bytes[byteIndex + i] = (byte)c;
            }
            return charCount;
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            if (_throwOnError) {
                int invalid = FindInvalid(bytes, index, count);
                if (invalid >= 0) {
                    throw Undecodable(bytes, invalid);
                }
            }
            return count;
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            if (_throwOnError) {
                int invalid = FindInvalid(bytes, byteIndex, byteCount);
                if (invalid >= 0) {
                    throw Undecodable(bytes, invalid);
                }
            }
            for (int i = 0; i < byteCount; i++) {
                // BinaryDecoderFallback's convention: the byte passes through as U+00mn.
                chars[charIndex + i] = (char)bytes[byteIndex + i];
            }
            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) {
            return charCount;
        }

        public override int GetMaxCharCount(int byteCount) {
            return byteCount;
        }

        public override bool IsSingleByte {
            get { return false; }
        }

        public override string/*!*/ EncodingName {
            get { return _name; }
        }

        public override string/*!*/ WebName {
            get { return _name; }
        }
    }

    /// <summary>
    /// CESU-8: UTF-8 applied to UTF-16 code units, so a non-BMP character is two three byte
    /// sequences (one per surrogate) rather than one four byte sequence.  Ruby ships it; .NET does
    /// not, and .NET's UTF8Encoding rejects the lone surrogates it is made of.
    /// </summary>
    internal sealed class CesuEncoding : Encoding {
        private readonly bool _throwOnError;

        internal CesuEncoding(bool throwOnError)
            : base(RubyEncoding.CodePageCESU8) {
            _throwOnError = throwOnError;
        }

        private static int ByteCount(char c) {
            if (c < 0x80) {
                return 1;
            }
            return c < 0x800 ? 2 : 3;
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            int result = 0;
            for (int i = 0; i < count; i++) {
                result += ByteCount(chars[index + i]);
            }
            return result;
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            int j = byteIndex;
            for (int i = 0; i < charCount; i++) {
                int c = chars[charIndex + i];
                if (c < 0x80) {
                    bytes[j++] = (byte)c;
                } else if (c < 0x800) {
                    bytes[j++] = (byte)(0xc0 | (c >> 6));
                    bytes[j++] = (byte)(0x80 | (c & 0x3f));
                } else {
                    bytes[j++] = (byte)(0xe0 | (c >> 12));
                    bytes[j++] = (byte)(0x80 | ((c >> 6) & 0x3f));
                    bytes[j++] = (byte)(0x80 | (c & 0x3f));
                }
            }
            return j - byteIndex;
        }

        /// <summary>
        /// Length in bytes of the sequence starting at <paramref name="index"/>, or 0 if the bytes
        /// there are not a well formed CESU-8 sequence.
        /// </summary>
        private static int SequenceLength(byte[]/*!*/ bytes, int index, int limit) {
            byte b = bytes[index];
            int length;
            if (b < 0x80) {
                return 1;
            } else if (b >= 0xc2 && b <= 0xdf) {
                length = 2;
            } else if (b >= 0xe0 && b <= 0xef) {
                length = 3;
            } else {
                return 0;
            }

            if (index + length > limit) {
                return 0;
            }
            for (int i = 1; i < length; i++) {
                if ((bytes[index + i] & 0xc0) != 0x80) {
                    return 0;
                }
            }
            return length;
        }

        /// <summary>
        /// DecoderFallbackException has to be built with the bytes it rejected: the parameterless
        /// constructor leaves BytesUnknown null, and RubyExceptions.CreateInvalidByteSequenceError
        /// formats that property, so a null there surfaces as a CLR ArgumentNullException instead
        /// of Ruby's Encoding::InvalidByteSequenceError.
        /// </summary>
        private static DecoderFallbackException/*!*/ Undecodable(byte[]/*!*/ bytes, int index) {
            return new DecoderFallbackException(null, new[] { bytes[index] }, index);
        }

        private char Decode(byte[]/*!*/ bytes, int index, int length) {
            switch (length) {
                case 1: return (char)bytes[index];
                case 2: return (char)(((bytes[index] & 0x1f) << 6) | (bytes[index + 1] & 0x3f));
                default: return (char)(((bytes[index] & 0x0f) << 12) | ((bytes[index + 1] & 0x3f) << 6) | (bytes[index + 2] & 0x3f));
            }
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            int limit = index + count;
            int result = 0;
            int i = index;
            while (i < limit) {
                int length = SequenceLength(bytes, i, limit);
                if (length == 0) {
                    if (_throwOnError) {
                        throw Undecodable(bytes, i);
                    }
                    // BinaryDecoderFallback's convention: an undecodable byte becomes one char.
                    length = 1;
                }
                i += length;
                result++;
            }
            return result;
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            int limit = byteIndex + byteCount;
            int i = byteIndex, j = charIndex;
            while (i < limit) {
                int length = SequenceLength(bytes, i, limit);
                if (length == 0) {
                    if (_throwOnError) {
                        throw Undecodable(bytes, i);
                    }
                    chars[j++] = (char)bytes[i++];
                    continue;
                }
                chars[j++] = Decode(bytes, i, length);
                i += length;
            }
            return j - charIndex;
        }

        public override int GetMaxByteCount(int charCount) {
            return charCount * 3;
        }

        public override int GetMaxCharCount(int byteCount) {
            return byteCount;
        }

        public override bool IsSingleByte {
            get { return false; }
        }

        public override string/*!*/ EncodingName {
            get { return "CESU-8"; }
        }

        public override string/*!*/ WebName {
            get { return "CESU-8"; }
        }
    }

    /// <summary>
    /// TIS-620, the Thai national standard.  .NET only has Windows-874, which is TIS-620 plus the
    /// Windows C1 repertoire (the Euro sign at 0x80, the smart quotes, and NBSP at 0xa0); Ruby
    /// keeps the two apart, and resolving "TIS-620" to Windows-874 made a byte like 0x80 decode
    /// where Ruby says it is undefined.
    /// </summary>
    internal sealed class Tis620Encoding : Encoding {
        private readonly bool _throwOnError;

        internal Tis620Encoding(bool throwOnError)
            : base(RubyEncoding.CodePageTIS620) {
            _throwOnError = throwOnError;
        }

        // 0x00-0x7f is ASCII; 0xa1-0xda and 0xdf-0xfb map onto the Thai block in order; everything
        // else is undefined.
        private static bool IsDefinedByte(byte b) {
            return b < 0x80 || b >= 0xa1 && b <= 0xda || b >= 0xdf && b <= 0xfb;
        }

        private static char ToChar(byte b) {
            return b < 0x80 ? (char)b : (char)(0x0e00 + (b - 0xa0));
        }

        /// <summary>See CesuEncoding.Undecodable - BytesUnknown must not be left null.</summary>
        private static DecoderFallbackException/*!*/ Undecodable(byte[]/*!*/ bytes, int index) {
            return new DecoderFallbackException(null, new[] { bytes[index] }, index);
        }

        private static int ToByte(char c) {
            if (c < 0x80) {
                return c;
            }
            int b = c - 0x0e00 + 0xa0;
            if (c >= 0x0e01 && b <= 0xff && IsDefinedByte((byte)b)) {
                return b;
            }
            return -1;
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            if (_throwOnError) {
                for (int i = 0; i < count; i++) {
                    if (ToByte(chars[index + i]) < 0) {
                        throw new EncoderFallbackException();
                    }
                }
            }
            return count;
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            for (int i = 0; i < charCount; i++) {
                int b = ToByte(chars[charIndex + i]);
                if (b < 0) {
                    if (_throwOnError) {
                        throw new EncoderFallbackException();
                    }
                    b = '?';
                }
                bytes[byteIndex + i] = (byte)b;
            }
            return charCount;
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            if (_throwOnError) {
                for (int i = 0; i < count; i++) {
                    if (!IsDefinedByte(bytes[index + i])) {
                        throw Undecodable(bytes, index + i);
                    }
                }
            }
            return count;
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            for (int i = 0; i < byteCount; i++) {
                byte b = bytes[byteIndex + i];
                if (!IsDefinedByte(b)) {
                    if (_throwOnError) {
                        throw Undecodable(bytes, byteIndex + i);
                    }
                    // BinaryDecoderFallback's convention: the byte passes through as U+00mn.
                    chars[charIndex + i] = (char)b;
                } else {
                    chars[charIndex + i] = ToChar(b);
                }
            }
            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) {
            return charCount;
        }

        public override int GetMaxCharCount(int byteCount) {
            return byteCount;
        }

        public override bool IsSingleByte {
            get { return true; }
        }

        public override string/*!*/ EncodingName {
            get { return "TIS-620"; }
        }

        public override string/*!*/ WebName {
            get { return "TIS-620"; }
        }
    }
}

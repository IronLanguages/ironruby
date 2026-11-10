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
        /// <summary>
        /// The encoding for a code page whose Ruby encoding System.Text does not supply as it is,
        /// or null for one it does.
        /// </summary>
        internal static Encoding TryCreate(int codepage, bool throwOnError) {
            switch (codepage) {
                case RubyEncoding.CodePageUTF16: return new BomEncoding(codepage, 2, throwOnError);
                case RubyEncoding.CodePageUTF32: return new BomEncoding(codepage, 4, throwOnError);
                case RubyEncoding.CodePageCESU8: return new CesuEncoding(throwOnError);
                case RubyEncoding.CodePageTIS620: return new Tis620Encoding(throwOnError);
                case RubyEncoding.CodePageEmacsMule: return new EmacsMuleEncoding(codepage, "Emacs-Mule", throwOnError);

                // Ruby's stateless-ISO-2022-JP is a replica of Emacs-Mule under another name.
                case RubyEncoding.CodePageStatelessISO2022JP: return new EmacsMuleEncoding(codepage, "stateless-ISO-2022-JP", throwOnError);
                case RubyEncoding.CodePageStatelessISO2022JPKDDI: return new EmacsMuleEncoding(codepage, "stateless-ISO-2022-JP-KDDI", throwOnError);

                case RubyEncoding.CodePageUTF8Mac: return new RenamedEncoding(codepage, "UTF8-MAC", RubyEncoding.CodePageUTF8, throwOnError);
                case RubyEncoding.CodePageUTF8DoCoMo: return new RenamedEncoding(codepage, "UTF8-DoCoMo", RubyEncoding.CodePageUTF8, throwOnError);
                case RubyEncoding.CodePageUTF8KDDI: return new RenamedEncoding(codepage, "UTF8-KDDI", RubyEncoding.CodePageUTF8, throwOnError);
                case RubyEncoding.CodePageUTF8SoftBank: return new RenamedEncoding(codepage, "UTF8-SoftBank", RubyEncoding.CodePageUTF8, throwOnError);
                // Ruby's CP51932 has EUC-JP's shape (enc/euc_jp.c replicates EUC-JP for it) and
                // the characters of .NET's code page 51932, whose own shape is wider.
                case RubyEncoding.CodePageCP51932: return new DoubleByteEncoding(codepage, "CP51932", DoubleByteEncoding.EucJp, RubyEncoding.CodePageEUCJP, throwOnError);
                case RubyEncoding.CodePageISO2022JP2: return new RenamedEncoding(codepage, "ISO-2022-JP-2", 50220, throwOnError);
                case RubyEncoding.CodePageCP50220: return new RenamedEncoding(codepage, "CP50220", 50220, throwOnError);
                case RubyEncoding.CodePageISO2022JPKDDI: return new RenamedEncoding(codepage, "ISO-2022-JP-KDDI", 50220, throwOnError);
                case RubyEncoding.CodePageCP852: return new RenamedEncoding(codepage, "CP852", 852, throwOnError);
                case RubyEncoding.CodePageCP855: return new RenamedEncoding(codepage, "CP855", 855, throwOnError);

                case RubyEncoding.CodePageEUCJP: return new EucJpEncoding(codepage, "EUC-JP", throwOnError);
                case RubyEncoding.CodePageEucJpMs: return new EucJpEncoding(codepage, "eucJP-ms", throwOnError);
                case RubyEncoding.CodePageEUCJIS2004: return new EucJpEncoding(codepage, "EUC-JIS-2004", throwOnError);
                case RubyEncoding.CodePageEUCTW: return new EucTwEncoding(throwOnError);

                // .NET's code page 932 is Microsoft's Shift_JIS, which Ruby calls Windows-31J; it
                // decodes 0x80, 0xA0 and 0xFD-0xFF as characters of their own, which in Ruby's
                // Shift_JIS family begin nothing. Ruby's narrower Shift_JIS, the carriers' variants
                // and MacJapanese are read with the same table.
                case RubyEncoding.CodePageSJIS: return new DoubleByteEncoding(codepage, "Windows-31J", DoubleByteEncoding.ShiftJis, codepage, throwOnError);
                case RubyEncoding.CodePageShiftJIS: return new DoubleByteEncoding(codepage, "Shift_JIS", DoubleByteEncoding.ShiftJis, RubyEncoding.CodePageSJIS, throwOnError);
                case RubyEncoding.CodePageSJISDoCoMo: return new DoubleByteEncoding(codepage, "SJIS-DoCoMo", DoubleByteEncoding.ShiftJis, RubyEncoding.CodePageSJIS, throwOnError);
                case RubyEncoding.CodePageSJISKDDI: return new DoubleByteEncoding(codepage, "SJIS-KDDI", DoubleByteEncoding.ShiftJis, RubyEncoding.CodePageSJIS, throwOnError);
                case RubyEncoding.CodePageSJISSoftBank: return new DoubleByteEncoding(codepage, "SJIS-SoftBank", DoubleByteEncoding.ShiftJis, RubyEncoding.CodePageSJIS, throwOnError);
                case RubyEncoding.CodePageMacJapanese: return new DoubleByteEncoding(codepage, "MacJapanese", DoubleByteEncoding.ShiftJis, codepage, throwOnError);

                // GBK and CP949 read 0x80 as a character of its own (the euro sign), but no byte
                // above it.
                case 936: return new DoubleByteEncoding(codepage, "GBK", DoubleByteEncoding.Gbk, codepage, throwOnError);
                case 949: return new DoubleByteEncoding(codepage, "CP949", DoubleByteEncoding.Cp949, codepage, throwOnError);

                // .NET's Big5 (code page 950) is Microsoft's, which also reads a lead byte below 0xA1;
                // Ruby's Big5 family does not, whatever their repertoires add.
                case RubyEncoding.CodePageBig5: return new DoubleByteEncoding(codepage, "Big5", DoubleByteEncoding.Big5, RubyEncoding.CodePageBig5, throwOnError);
                case RubyEncoding.CodePageBig5HKSCS: return new DoubleByteEncoding(codepage, "Big5-HKSCS", DoubleByteEncoding.Big5, RubyEncoding.CodePageBig5, throwOnError);
                case RubyEncoding.CodePageBig5UAO: return new DoubleByteEncoding(codepage, "Big5-UAO", DoubleByteEncoding.Big5, RubyEncoding.CodePageBig5, throwOnError);
                case RubyEncoding.CodePageCP950: return new DoubleByteEncoding(codepage, "CP950", DoubleByteEncoding.Big5, RubyEncoding.CodePageBig5, throwOnError);
                case RubyEncoding.CodePageCP951: return new DoubleByteEncoding(codepage, "CP951", DoubleByteEncoding.Big5, RubyEncoding.CodePageBig5, throwOnError);

                // EUC: two bytes of 0xA1-0xFE. .NET's code pages decode more than that.
                case 51936: return new DoubleByteEncoding(codepage, "GB2312", DoubleByteEncoding.Euc, 51936, throwOnError);
                case RubyEncoding.CodePageGB12345: return new DoubleByteEncoding(codepage, "GB12345", DoubleByteEncoding.Euc, 51936, throwOnError);
                case 51949: return new DoubleByteEncoding(codepage, "EUC-KR", DoubleByteEncoding.Euc, 51949, throwOnError);

                // Ruby has no converter for these, so it never reads their bytes above 0x7F as
                // anything but bytes.
                case RubyEncoding.CodePageGB1988: return new SingleByteTableEncoding(codepage, "GB1988", SingleByteTableEncoding.Undefined, throwOnError);
                case RubyEncoding.CodePageMacCentEuro: return new SingleByteTableEncoding(codepage, "macCentEuro", SingleByteTableEncoding.Undefined, throwOnError);
                case RubyEncoding.CodePageMacThai: return new SingleByteTableEncoding(codepage, "macThai", SingleByteTableEncoding.Undefined, throwOnError);
                case RubyEncoding.CodePageWindows1258: return new SingleByteTableEncoding(codepage, "Windows-1258", SingleByteTableEncoding.Undefined, throwOnError);

                case RubyEncoding.CodePageISO8859_10: return new SingleByteTableEncoding(codepage, "ISO-8859-10", SingleByteTableEncoding.Iso8859_10, throwOnError);
                case RubyEncoding.CodePageISO8859_11: return new SingleByteTableEncoding(codepage, "ISO-8859-11", SingleByteTableEncoding.Iso8859_11, throwOnError);
                case RubyEncoding.CodePageISO8859_14: return new SingleByteTableEncoding(codepage, "ISO-8859-14", SingleByteTableEncoding.Iso8859_14, throwOnError);
                case RubyEncoding.CodePageISO8859_16: return new SingleByteTableEncoding(codepage, "ISO-8859-16", SingleByteTableEncoding.Iso8859_16, throwOnError);

                // .NET's tables for these give every byte a character; Ruby's leave some out.
                case 874: return SingleByteTableEncoding.FromCodePage(codepage, "Windows-874", "81 82 83 84 86 87 88 89 8A 8B 8C 8D 8E 8F 90 98 99 9A 9B 9C 9D 9E 9F DB DC DD DE FC FD FE FF", throwOnError);
                case 1250: return SingleByteTableEncoding.FromCodePage(codepage, "Windows-1250", "81 83 88 90 98", throwOnError);
                case 1251: return SingleByteTableEncoding.FromCodePage(codepage, "Windows-1251", "98", throwOnError);
                case 1252: return SingleByteTableEncoding.FromCodePage(codepage, "Windows-1252", "81 8D 8F 90 9D", throwOnError);
                case 1253: return SingleByteTableEncoding.FromCodePage(codepage, "Windows-1253", "81 88 8A 8C 8D 8E 8F 90 98 9A 9C 9D 9E 9F AA D2 FF", throwOnError);
                case 1254: return SingleByteTableEncoding.FromCodePage(codepage, "Windows-1254", "81 8D 8E 8F 90 9D 9E", throwOnError);
                case 1255: return SingleByteTableEncoding.FromCodePage(codepage, "Windows-1255", "81 8A 8C 8D 8E 8F 90 9A 9C 9D 9E 9F D9 DA DB DC DD DE DF FB FC FF", throwOnError);
                case 1257: return SingleByteTableEncoding.FromCodePage(codepage, "Windows-1257", "81 83 88 8A 8C 90 98 9A 9C 9F A1 A5", throwOnError);
                case 28593: return SingleByteTableEncoding.FromCodePage(codepage, "ISO-8859-3", "A5 AE BE C3 D0 E3 F0", throwOnError);
                case 28596: return SingleByteTableEncoding.FromCodePage(codepage, "ISO-8859-6", "A1 A2 A3 A5 A6 A7 A8 A9 AA AB AE AF B0 B1 B2 B3 B4 B5 B6 B7 B8 B9 BA BC BD BE C0 DB DC DD DE DF F3 F4 F5 F6 F7 F8 F9 FA FB FC FD FE FF", throwOnError);
                case 28597: return SingleByteTableEncoding.FromCodePage(codepage, "ISO-8859-7", "AE D2 FF", throwOnError);
                case 28598: return SingleByteTableEncoding.FromCodePage(codepage, "ISO-8859-8", "A1 BF C0 C1 C2 C3 C4 C5 C6 C7 C8 C9 CA CB CC CD CE CF D0 D1 D2 D3 D4 D5 D6 D7 D8 D9 DA DB DC DD DE FB FC FF", throwOnError);
                case 857: return SingleByteTableEncoding.FromCodePage(codepage, "IBM857", "D5 E7 F2", throwOnError);
                case 864: return SingleByteTableEncoding.FromCodePage(codepage, "IBM864", "9B 9C A6 FF", throwOnError);
                case 869: return SingleByteTableEncoding.FromCodePage(codepage, "IBM869", "80 81 82 83 84 85 87 93 94", throwOnError);
                case 10000: return SingleByteTableEncoding.FromCodePage(codepage, "macRoman", "F0", throwOnError);
                case 10006: return SingleByteTableEncoding.FromCodePage(codepage, "macGreek", "FF", throwOnError);
                default: return null;
            }
        }

        /// <summary>
        /// The code page whose character set properties (single or double byte) an encoding
        /// shares: the .NET code page a renamed encoding delegates to, or the code page itself.
        /// </summary>
        internal static int GetTableCodePage(int codepage) {
            switch (codepage) {
                case RubyEncoding.CodePageShiftJIS:
                case RubyEncoding.CodePageSJISDoCoMo:
                case RubyEncoding.CodePageSJISKDDI:
                case RubyEncoding.CodePageSJISSoftBank:
                    return RubyEncoding.CodePageSJIS;
                case RubyEncoding.CodePageUTF8Mac:
                case RubyEncoding.CodePageUTF8DoCoMo:
                case RubyEncoding.CodePageUTF8KDDI:
                case RubyEncoding.CodePageUTF8SoftBank:
                    return RubyEncoding.CodePageUTF8;
                case RubyEncoding.CodePageCP51932:
                case RubyEncoding.CodePageEucJpMs:
                case RubyEncoding.CodePageEUCJIS2004:
                    return RubyEncoding.CodePageEUCJP;
                case RubyEncoding.CodePageBig5HKSCS:
                case RubyEncoding.CodePageBig5UAO:
                case RubyEncoding.CodePageCP950:
                case RubyEncoding.CodePageCP951:
                    return RubyEncoding.CodePageBig5;
                case RubyEncoding.CodePageGB12345: return 51936;
                case RubyEncoding.CodePageCP852: return 852;
                case RubyEncoding.CodePageCP855: return 855;
                case RubyEncoding.CodePageISO8859_10:
                case RubyEncoding.CodePageISO8859_11:
                case RubyEncoding.CodePageISO8859_14:
                case RubyEncoding.CodePageISO8859_16:
                case RubyEncoding.CodePageGB1988:
                    return 28591;
                default: return codepage;
            }
        }

        /// <summary>
        /// The runs of bytes String#scrub replaces, as offset and length pairs, the way MRI's
        /// enc_str_scrub finds them; null for an encoding this does not cover (the Unicode ones,
        /// which ruby4.rb's scrub handles itself, and the single byte ones). A byte that begins no
        /// character is a run of its own, except that in an encoding of three or four byte
        /// characters the longest start of a character that is cut short by a bad byte is one
        /// run, as is whatever is left at the end of the string when it runs out mid character.
        /// That is decided on the bytes, not on characters: "\xA1\x39" is one run in GB18030
        /// even though "\x39" alone is a valid character.
        /// </summary>
        internal static System.Collections.Generic.List<int> FindScrubRuns(Encoding/*!*/ encoding, byte[]/*!*/ bytes, int count) {
            var multiByte = encoding as MultiByteEncoding;
            int maxLength;
            if (multiByte != null) {
                maxLength = multiByte.MaxLength;
            } else if (encoding.CodePage == 54936) {
                maxLength = 4;
            } else {
                return null;
            }

            var runs = new System.Collections.Generic.List<int>();
            int p = 0;
            while (p < count) {
                int length = (multiByte != null) ? multiByte.PreciseLength(bytes, p, count) : ScanGb18030(bytes, p, count);
                if (length > 0) {
                    p += length;
                    continue;
                }
                if (length == 0) {
                    runs.Add(p);
                    runs.Add(count - p);
                    break;
                }

                int clen = Math.Min(maxLength, count - p);
                if (clen <= 2) {
                    clen = 1;
                } else {
                    for (clen--; clen > 1; clen--) {
                        int prefix = (multiByte != null) ? multiByte.PreciseLength(bytes, p, p + clen) : ScanGb18030(bytes, p, p + clen);
                        if (prefix == 0) {
                            break;
                        }
                    }
                }
                runs.Add(p);
                runs.Add(clen);
                p += clen;
            }
            return runs;
        }

        // GB18030 as enc/gb18030.c has it: 0x81-0xFE leads either a second byte of 0x40-0x7E or
        // 0x80-0xFE, or a four byte sequence of 0x30-0x39, 0x81-0xFE, 0x30-0x39. The length of
        // the sequence at index, 0 if it is cut short, or -1 if it is not one.
        private static int ScanGb18030(byte[]/*!*/ bytes, int index, int end) {
            byte b = bytes[index];
            if (b < 0x80) {
                return 1;
            }
            if (b == 0x80 || b == 0xff) {
                return -1;
            }
            if (index + 1 >= end) {
                return 0;
            }
            b = bytes[index + 1];
            if (b >= 0x40 && b <= 0xfe && b != 0x7f) {
                return 2;
            }
            if (b < 0x30 || b > 0x39) {
                return -1;
            }
            if (index + 2 >= end) {
                return 0;
            }
            b = bytes[index + 2];
            if (b < 0x81 || b == 0xff) {
                return -1;
            }
            if (index + 3 >= end) {
                return 0;
            }
            b = bytes[index + 3];
            return (b >= 0x30 && b <= 0x39) ? 4 : -1;
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

            // Every byte of a single byte encoding is a character as far as Ruby is concerned,
            // even the ones its conversion table leaves undefined. US-ASCII is the exception: it
            // is seven bit.
            if (encoding.IsSingleByte && encoding.CodePage != RubyEncoding.CodePageAscii && encoding.CodePage != RubyEncoding.CodePageBinary) {
                return true;
            }
            return null;
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
    /// bytes 0x80-0xFF (U+FFFF where the byte is undefined, which Ruby still counts as a
    /// character: it just has no counterpart in any other encoding).
    /// </summary>
    internal sealed class SingleByteTableEncoding : Encoding, IStructuredEncoding {
        private readonly bool _throwOnError;
        private readonly string/*!*/ _name;
        private readonly char[]/*!*/ _table;
        // built on first use: every Encoding constant makes one of these, and few are ever used to encode
        // (a Dictionary<char, byte> is also code ReadyToRun cannot precompile)
        private System.Collections.Generic.Dictionary<char, byte> _reverse;

        internal SingleByteTableEncoding(int codepage, string/*!*/ name, char[]/*!*/ table, bool throwOnError)
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
            byte b;
            return GetReverseTable().TryGetValue(c, out b) ? b : -1;
        }

        private bool TryGetChar(byte b, out char c) {
            if (b < 0x80) {
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

        /// <summary>
        /// No byte above 0x7F stands for a character: for the encodings Ruby has no converter for,
        /// where such a byte is valid, but only ever a byte.
        /// </summary>
        internal static readonly char[]/*!*/ Undefined = new string('￿', 128).ToCharArray();

        /// <summary>
        /// A .NET single byte code page with the bytes Ruby's table leaves undefined taken out:
        /// .NET reads them as the C1 control or private use character of the same number, so
        /// "\x81" in Windows-1252 would transcode where MRI refuses it as undefined.
        /// <paramref name="undefined"/> lists them in hex.
        /// </summary>
        internal static SingleByteTableEncoding/*!*/ FromCodePage(int codepage, string/*!*/ name, string/*!*/ undefined, bool throwOnError) {
            var bytes = new byte[128];
            for (int i = 0; i < bytes.Length; i++) {
                bytes[i] = (byte)(0x80 + i);
            }
            char[] table = Encoding.GetEncoding(codepage).GetChars(bytes);
            foreach (var hex in undefined.Split(' ')) {
                table[System.Convert.ToInt32(hex, 16) - 0x80] = '￿';
            }
            return new SingleByteTableEncoding(codepage, name, table, throwOnError);
        }

        /// <summary>ISO-8859-11: TIS-620 plus the C1 controls and NBSP.</summary>
        internal static readonly char[]/*!*/ Iso8859_11 = CreateIso8859_11();

        private static char[]/*!*/ CreateIso8859_11() {
            var result = new char[128];
            for (int b = 0x80; b <= 0xff; b++) {
                result[b - 0x80] = b <= 0xa0 ? (char)b : (b <= 0xda || b >= 0xdf && b <= 0xfb) ? (char)(0x0e00 + b - 0xa0) : '￿';
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
        private bool _lossless;

        // A two byte sequence of the right shape that names no character - one the table leaves
        // out - is still one character to Ruby. In the lossless form of an encoding, which the
        // string representation decodes with (see RubyEncoding.EscapingEncoding), it reads as
        // the code point in plane 15 that carries its two bytes, and writes back as those bytes;
        // the strict form, the one transcoding uses, refuses it. Decoded bytes that are not
        // well formed are escaped by the EscapingEncoding as usual, one character per byte.
        private const int LosslessBase = 0xF0000;

        /// <summary>See LosslessBase.</summary>
        internal Encoding/*!*/ AsLossless() {
            var result = (MultiByteEncoding)MemberwiseClone();
            result._lossless = true;
            return result;
        }

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
        /// Scan as the transcoder reads the bytes, where that is narrower than the encoding's
        /// shape: MRI's CP51932 converter has no three byte form, so to it 0x8F is a bad byte
        /// of its own although "\x8F\xA1\xA1" is a valid CP51932 string. The lossless form,
        /// which holds a string's characters, reads the shape.
        /// </summary>
        protected virtual int TranscodingScan(byte[]/*!*/ bytes, int index, int end) {
            return Scan(bytes, index, end);
        }

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

        /// <summary>See Scan: MRI's rb_enc_precise_mbclen, for String#scrub.</summary>
        internal int PreciseLength(byte[]/*!*/ bytes, int index, int end) {
            return Scan(bytes, index, end);
        }

        /// <summary>MRI's rb_enc_mbmaxlen.</summary>
        internal int MaxLength {
            get { return MaxSequenceLength; }
        }

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
                if (length < 0 && _lossless) {
                    length = EncodeLossless(chars, i, end, buffer, out used);
                }
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

        // Three and four byte sequences (EUC-JP's JIS X 0212 0x8F b1 b2, EUC-TW's CNS planes
        // 0x8E p b1 b2) do not fit a plane the way two bytes do, so they are numbered densely
        // over the 94 x 94 cells of their table: the three byte ones from LosslessBase, the four
        // byte ones from there too, on into plane 16 and, for the rest of EUC-TW's sixteen
        // CNS planes, on in plane 14. (EUC-TW decodes nothing beyond ASCII, so no character of
        // its own can be mistaken for one of these; EUC-JP has no four byte sequences.)
        private const int CellCount = 94 * 94;
        private const int ShortLosslessRange = 0x8000;
        private const int LongLosslessBase = 0x100000;
        private const int ExtraLosslessBase = 0xE0000;
        private const int ExtraLosslessStart = ShortLosslessRange + 0x10000;

        private static int Cell(byte b1, byte b2) {
            return (b1 - 0xa1) * 94 + (b2 - 0xa1);
        }

        /// <summary>
        /// The plane 15 or 16 code point a well formed sequence the table has no character for
        /// reads as in the lossless form, or -1 if it has none.
        /// </summary>
        private static int ToLosslessCodePoint(byte[]/*!*/ bytes, int index, int length) {
            if (length == 2) {
                return LosslessBase + (bytes[index] << 8 | bytes[index + 1]);
            }
            if (length == 3 && bytes[index] == 0x8f && IsGraphic(bytes[index + 1]) && IsGraphic(bytes[index + 2])) {
                return LosslessBase + Cell(bytes[index + 1], bytes[index + 2]);
            }
            if (length == 4 && bytes[index] == 0x8e && bytes[index + 1] >= 0xa1 && IsGraphic(bytes[index + 2]) && IsGraphic(bytes[index + 3])) {
                int cell = (bytes[index + 1] - 0xa1) * CellCount + Cell(bytes[index + 2], bytes[index + 3]);
                if (cell < ShortLosslessRange) {
                    return LosslessBase + cell;
                }
                if (cell < ExtraLosslessStart) {
                    return LongLosslessBase + cell - ShortLosslessRange;
                }
                cell -= ExtraLosslessStart;
                return (cell <= 0xffff) ? ExtraLosslessBase + cell : -1;
            }
            return -1;
        }

        private int EncodeLossless(char[]/*!*/ chars, int index, int end, byte[]/*!*/ bytes, out int used) {
            used = 1;
            if (index + 1 < end && Char.IsSurrogatePair(chars[index], chars[index + 1])) {
                int codepoint = Char.ConvertToUtf32(chars[index], chars[index + 1]);
                int length = -1;
                if (codepoint >= LosslessBase + 0x8000 && codepoint <= LosslessBase + 0xffff) {
                    bytes[0] = (byte)(codepoint >> 8);
                    bytes[1] = (byte)codepoint;
                    length = 2;
                } else if (codepoint >= LosslessBase && codepoint < LosslessBase + ShortLosslessRange || codepoint >= LongLosslessBase ||
                    codepoint >= ExtraLosslessBase && codepoint < LosslessBase) {
                    int cell = (codepoint >= LongLosslessBase) ? codepoint - LongLosslessBase + ShortLosslessRange :
                        (codepoint < LosslessBase) ? codepoint - ExtraLosslessBase + ExtraLosslessStart :
                        codepoint - LosslessBase;
                    if (cell < CellCount) {
                        // EUC-JP's three bytes, if this is an encoding that has them
                        bytes[0] = 0x8f;
                        bytes[1] = (byte)(0xa1 + cell / 94);
                        bytes[2] = (byte)(0xa1 + cell % 94);
                        length = 3;
                    }
                    if (length < 0 || Scan(bytes, 0, 3) != 3) {
                        bytes[0] = 0x8e;
                        bytes[1] = (byte)(0xa1 + cell / CellCount);
                        bytes[2] = (byte)(0xa1 + cell % CellCount / 94);
                        bytes[3] = (byte)(0xa1 + cell % 94);
                        length = 4;
                    }
                }
                if (length > 0 && Scan(bytes, 0, length) == length) {
                    used = 2;
                    return length;
                }
            }
            return -1;
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
                    int length = _encoding._lossless ? _encoding.Scan(input, i, end) : _encoding.TranscodingScan(input, i, end);
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
                        int codepoint;
                        if (n < 0 && _encoding._lossless && (codepoint = ToLosslessCodePoint(input, i, length)) >= 0) {
                            codepoint -= 0x10000;
                            decoded[0] = (char)(0xD800 + (codepoint >> 10));
                            decoded[1] = (char)(0xDC00 + (codepoint & 0x3ff));
                            n = 2;
                        }
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
    /// A double byte encoding whose shape is Ruby's - which bytes lead a character, which follow
    /// one, which high bytes stand alone - with the characters themselves taken from a .NET code
    /// page. .NET's tables accept sequences Ruby calls invalid (Microsoft's Big5 has lead bytes
    /// below 0xA1, its EUC code pages decode C1 bytes), and Ruby measures, splits and validates a
    /// string by shape alone, so the shape cannot be left to .NET. A sequence of the right shape
    /// that the table has no character for is still valid; it just cannot be transcoded.
    /// A layout can also have EUC-JP's three byte form (0x8F and two bytes of 0xA1-0xFE), which no
    /// .NET table used here decodes, so those sequences are always ones without a character.
    /// </summary>
    internal sealed class DoubleByteEncoding : MultiByteEncoding {
        internal sealed class Layout {
            internal readonly bool[]/*!*/ Lead = new bool[256];
            internal readonly bool[]/*!*/ Trail = new bool[256];
            internal readonly bool[]/*!*/ Single = new bool[256];

            /// <summary>The byte that begins a three byte sequence, or -1.</summary>
            internal readonly int ThreeByteLead;

            internal Layout(int[]/*!*/ lead, int[]/*!*/ trail, int[]/*!*/ single)
                : this(lead, trail, single, -1) {
            }

            internal Layout(int[]/*!*/ lead, int[]/*!*/ trail, int[]/*!*/ single, int threeByteLead) {
                ThreeByteLead = threeByteLead;
                Fill(Lead, lead);
                Fill(Trail, trail);
                Fill(Single, single);
                for (int b = 0; b < 0x80; b++) {
                    Single[b] = true;
                }
            }

            // pairs of inclusive bounds
            private static void Fill(bool[]/*!*/ set, int[]/*!*/ ranges) {
                for (int i = 0; i < ranges.Length; i += 2) {
                    for (int b = ranges[i]; b <= ranges[i + 1]; b++) {
                        set[b] = true;
                    }
                }
            }
        }

        internal static readonly Layout/*!*/ Big5 = new Layout(new[] { 0xa1, 0xfe }, new[] { 0x40, 0x7e, 0xa1, 0xfe }, new int[0]);
        internal static readonly Layout/*!*/ Euc = new Layout(new[] { 0xa1, 0xfe }, new[] { 0xa1, 0xfe }, new int[0]);
        // Ruby's EUC-JP family (enc/euc_jp.c): 0x8E also leads two bytes, whatever follows it.
        internal static readonly Layout/*!*/ EucJp = new Layout(new[] { 0x8e, 0x8e, 0xa1, 0xfe }, new[] { 0xa1, 0xfe }, new int[0], 0x8f);
        internal static readonly Layout/*!*/ ShiftJis = new Layout(new[] { 0x81, 0x9f, 0xe0, 0xfc }, new[] { 0x40, 0x7e, 0x80, 0xfc }, new[] { 0xa1, 0xdf });
        internal static readonly Layout/*!*/ Gbk = new Layout(new[] { 0x81, 0xfe }, new[] { 0x40, 0x7e, 0x80, 0xfe }, new[] { 0x80, 0x80 });
        internal static readonly Layout/*!*/ Cp949 = new Layout(new[] { 0x81, 0xfe }, new[] { 0x41, 0x5a, 0x61, 0x7a, 0x81, 0xfe }, new[] { 0x80, 0x80 });

        private readonly Layout/*!*/ _layout;
        private readonly Encoding/*!*/ _table;

        // Lead bytes that the table reads as a character on their own: .NET's EUC-KR (code page
        // 51949) has 0xAD-0xAF and 0xFE as single bytes, so it splits "\xFE\xFE", one character
        // to Ruby, in two.
        private readonly bool[]/*!*/ _tableSingle = new bool[256];

        internal DoubleByteEncoding(int codepage, string/*!*/ name, Layout/*!*/ layout, int tableCodePage, bool throwOnError)
            : base(codepage, name, throwOnError) {
            _layout = layout;
            _table = Encoding.GetEncoding(tableCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

            var single = new byte[1];
            for (int b = 0x80; b <= 0xff; b++) {
                if (layout.Lead[b] || b == layout.ThreeByteLead) {
                    single[0] = (byte)b;
                    try {
                        _tableSingle[b] = _table.GetCharCount(single, 0, 1) > 0;
                    } catch (DecoderFallbackException) {
                        // a lead byte, as it should be
                    }
                }
            }
        }

        protected override int MaxSequenceLength {
            get { return _layout.ThreeByteLead >= 0 ? 3 : 2; }
        }

        /// <summary>
        /// Whether the bytes are well formed and the table reads them sequence by sequence as Ruby
        /// does: no three byte sequence (the table has none) and no lead byte it reads alone.
        /// </summary>
        private bool FitsTable(byte[]/*!*/ bytes, int index, int count) {
            int end = index + count;
            int i = index;
            while (i < end) {
                int length = Scan(bytes, i, end);
                if (length <= 0 || length == 3 || length == 2 && _tableSingle[bytes[i]]) {
                    return false;
                }
                i += length;
            }
            return true;
        }

        // The table reads a well formed run just as the sequence by sequence loop does, only much
        // faster; the loop is for what it refuses (and for the lossless form's plane 15 stand-ins).

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            if (FitsTable(bytes, index, count)) {
                try {
                    return _table.GetCharCount(bytes, index, count);
                } catch (DecoderFallbackException) {
                    // a sequence the table has no character for
                }
            }
            return base.GetCharCount(bytes, index, count);
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            if (FitsTable(bytes, byteIndex, byteCount)) {
                try {
                    return _table.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
                } catch (DecoderFallbackException) {
                    // a sequence the table has no character for
                }
            }
            return base.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        }

        /// <summary>The table's bytes for the characters, if all of them are in a shape of this encoding.</summary>
        private byte[] TryEncodeWithTable(char[]/*!*/ chars, int index, int count) {
            byte[] result;
            try {
                result = _table.GetBytes(chars, index, count);
            } catch (EncoderFallbackException) {
                return null;
            }
            return FindInvalid(result, 0, result.Length) < 0 ? result : null;
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            byte[] bytes = TryEncodeWithTable(chars, index, count);
            return bytes != null ? bytes.Length : base.GetByteCount(chars, index, count);
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            byte[] encoded = TryEncodeWithTable(chars, charIndex, charCount);
            if (encoded == null) {
                return base.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
            }
            Array.Copy(encoded, 0, bytes, byteIndex, encoded.Length);
            return encoded.Length;
        }

        protected override int Scan(byte[]/*!*/ bytes, int index, int end) {
            byte b = bytes[index];
            if (_layout.Single[b]) {
                return 1;
            }
            if (b == _layout.ThreeByteLead) {
                for (int i = 1; i < 3; i++) {
                    if (index + i >= end) {
                        return 0;
                    }
                    if (!IsGraphic(bytes[index + i])) {
                        return -i;
                    }
                }
                return 3;
            }
            if (!_layout.Lead[b]) {
                return -1;
            }
            if (index + 1 >= end) {
                return 0;
            }
            return _layout.Trail[bytes[index + 1]] ? 2 : -1;
        }

        protected override int TranscodingScan(byte[]/*!*/ bytes, int index, int end) {
            return (bytes[index] == _layout.ThreeByteLead) ? -1 : Scan(bytes, index, end);
        }

        protected override int DecodeSequence(byte[]/*!*/ bytes, int index, int length, char[]/*!*/ chars, int charIndex) {
            if (bytes[index] < 0x80) {
                chars[charIndex] = (char)bytes[index];
                return 1;
            }
            if (length == 3 || _tableSingle[bytes[index]]) {
                return -1;
            }
            int n;
            try {
                n = _table.GetChars(bytes, index, length, chars, charIndex);
            } catch (DecoderFallbackException) {
                return -1;
            }
            // one character, not the table's reading of the bytes as two
            return (n == 1 || n == 2 && Char.IsSurrogatePair(chars[charIndex], chars[charIndex + 1])) ? n : -1;
        }

        protected override int EncodeCharacter(char[]/*!*/ chars, int index, int end, byte[]/*!*/ bytes, int byteIndex, out int used) {
            char c = chars[index];
            used = (Char.IsHighSurrogate(c) && index + 1 < end && Char.IsLowSurrogate(chars[index + 1])) ? 2 : 1;
            if (c < 0x80) {
                bytes[byteIndex] = (byte)c;
                return 1;
            }
            if (used == 2) {
                return -1;
            }
            int length;
            try {
                length = _table.GetBytes(chars, index, 1, bytes, byteIndex);
            } catch (EncoderFallbackException) {
                return -1;
            }
            // what the table spells in a shape this encoding does not have is not one of its characters
            return Scan(bytes, byteIndex, byteIndex + length) == length ? length : -1;
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

        // Ruby has no converter for EUC-TW, so beyond ASCII its sequences name no character; the
        // lossless form still reads each as one (see MultiByteEncoding.LosslessBase).
        protected override int DecodeSequence(byte[]/*!*/ bytes, int index, int length, char[]/*!*/ chars, int charIndex) {
            if (length != 1) {
                return -1;
            }
            chars[charIndex] = (char)bytes[index];
            return 1;
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
    /// What MRI's stateless-ISO-2022-JP transcoders read and write: ASCII, and JIS X 0208 as 0x92
    /// followed by the character's two EUC-JP bytes - nothing else, so a JIS X 0212 or half width
    /// kana character, which EUC-JP has, is undefined. (A string in the encoding is still held by
    /// EmacsMuleEncoding, whose shape Ruby gives it; this is RubyEncoding.TranscodingEncoding.)
    /// </summary>
    internal sealed class StatelessIso2022JpEncoding : MultiByteEncoding {
        internal StatelessIso2022JpEncoding(int codepage, string/*!*/ name)
            : base(codepage, name, true) {
        }

        private static Encoding/*!*/ EucJp {
            get { return RubyEncoding.EUCJP.StrictEncoding; }
        }

        protected override int MaxSequenceLength {
            get { return 3; }
        }

        protected override int Scan(byte[]/*!*/ bytes, int index, int end) {
            byte b = bytes[index];
            if (b < 0x80) {
                return 1;
            }
            if (b != 0x92) {
                return -1;
            }
            for (int i = 1; i < 3; i++) {
                if (index + i >= end) {
                    return 0;
                }
                if (!IsGraphic(bytes[index + i])) {
                    return -i;
                }
            }
            return 3;
        }

        protected override int DecodeSequence(byte[]/*!*/ bytes, int index, int length, char[]/*!*/ chars, int charIndex) {
            if (length == 1) {
                chars[charIndex] = (char)bytes[index];
                return 1;
            }
            try {
                return EucJp.GetChars(bytes, index + 1, 2, chars, charIndex);
            } catch (DecoderFallbackException) {
                return -1;
            }
        }

        protected override int EncodeCharacter(char[]/*!*/ chars, int index, int end, byte[]/*!*/ bytes, int byteIndex, out int used) {
            char c = chars[index];
            used = (Char.IsHighSurrogate(c) && index + 1 < end && Char.IsLowSurrogate(chars[index + 1])) ? 2 : 1;
            if (c < 0x80) {
                bytes[byteIndex] = (byte)c;
                return 1;
            }
            byte[] euc;
            try {
                euc = EucJp.GetBytes(chars, index, used);
            } catch (EncoderFallbackException) {
                return -1;
            }
            if (euc.Length != 2 || !IsGraphic(euc[0]) || !IsGraphic(euc[1])) {
                return -1;
            }
            bytes[byteIndex] = 0x92;
            bytes[byteIndex + 1] = euc[0];
            bytes[byteIndex + 2] = euc[1];
            return 3;
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

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
using System.Dynamic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Numerics;

namespace IronRuby.Builtins {
    public static class RubyEncoder {
        #region UTF8

        private static void GetCodePointByteCount(int codepoint, out int count, out int mark) {
            if (codepoint < 0x80) {
                count = 1;
                mark = 0;
            } else if (codepoint < 0x800) {
                count = 2;
                mark = 0xc0;
            } else if (codepoint < 0x10000) {
                count = 3;
                mark = 0xe0;
            } else if (codepoint < 0x200000) {
                count = 4;
                mark = 0xf0;
            } else if (codepoint < 0x4000000) {
                count = 5;
                mark = 0xf8;
            } else {
                count = 6;
                mark = 0xfc;
            }
        }

        private static void WriteUtf8CodePoint(Stream/*!*/ stream, int codepoint) {
            //       0 -       7F  0xxxxxxx
            //      80 -      7FF  110yyyxx 10xxxxxx
            //     800 -     FFFF  1110yyyy 10yyyyxx 10xxxxxx
            //   10000 -   1FFFFF  11110zzz 10zzyyyy 10yyyyxx 10xxxxxx
            //  200000 -  3FFFFFF  111110vv 10vvvvzz 10zzzzyy 10yyyyyy 10xxxxxx
            // 4000000 - 7FFFFFFF  1111110v 10vvvvuu 10uuuuzz 10zzzzyy 10yyyyxx 10xxxxxx
            if (codepoint < 0) {
                throw RubyExceptions.CreateRangeError("pack(U): value out of range");
            }
            int count, mark;
            GetCodePointByteCount(codepoint, out count, out mark);
            
            stream.WriteByte((byte)((codepoint >> (6 * (count - 1))) | mark));
            switch (count) {
                case 6: stream.WriteByte((byte)((codepoint >> 24) & 0x3f | 0x80)); goto case 5;
                case 5: stream.WriteByte((byte)((codepoint >> 18) & 0x3f | 0x80)); goto case 4;
                case 4: stream.WriteByte((byte)((codepoint >> 12) & 0x3f | 0x80)); goto case 3;
                case 3: stream.WriteByte((byte)((codepoint >> 6) & 0x3f | 0x80)); goto case 2;
                case 2: stream.WriteByte((byte)(codepoint & 0x3f | 0x80)); break;
            }
        }

        private static int ReadUtf8CodePoint(MutableString/*!*/ data, ref int index) {
            int length = data.GetByteCount();
            if (index >= length) {
                return -1;
            }
            int b = data.GetByte(index++);
            int count, mask;
            if ((b & 0x80) == 0) {
                count = 1;
                mask = 0xff;
            } else if ((b & 0xe0) == 0xc0) {
                count = 2;
                mask = 0x1f;
            } else if ((b & 0xf0) == 0xe0) {
                count = 3;
                mask = 0x0f;
            } else if ((b & 0xf8) == 0xf0) {
                count = 4;
                mask = 0x07;
            } else if ((b & 0xfc) == 0xf8) {
                count = 5;
                mask = 0x03;
            } else if ((b & 0xfe) == 0xfc) {
                count = 6;
                mask = 0x01;
            } else {
                throw RubyExceptions.CreateArgumentError("malformed UTF-8 character");
            }

            int codepoint = b & mask;
            for (int i = 1; i < count; i++) {
                if (index >= length) {
                    throw RubyExceptions.CreateArgumentError(
                        "malformed UTF-8 character (expected {0} bytes, given {1} bytes)", count, i
                    );
                }
                b = data.GetByte(index++);
                if ((b & 0xc0) != 0x80) {
                    throw RubyExceptions.CreateArgumentError("malformed UTF-8 character");
                }

                codepoint = (codepoint << 6) | (b & 0x3f);
            }

            int requiredCount;
            int mark;
            GetCodePointByteCount(codepoint, out requiredCount, out mark);
            if (requiredCount != count) {
                throw RubyExceptions.CreateArgumentError("redundant UTF-8 sequence");
            }
            return codepoint;
        }

        #endregion

        #region BER

        private static void WriteBer(Stream/*!*/ stream, IntegerValue value) {
            if (value.IsFixnum) {
                if (value.Fixnum < 0) {
                    throw RubyExceptions.CreateArgumentError("can't compress negative numbers");
                }
                int f = value.Fixnum;
                bool write = false;
                for (int shift = 28; shift > 0; shift -= 7) {
                    int b = (f >> shift) & 0x7f;
                    if (b != 0 || write) {
                        stream.WriteByte((byte)(b | (1 << 7)));
                        write = true;
                    }
                }
                stream.WriteByte((byte)(f & 0x7f));
            } else {
                BigInteger bignum = value.Bignum;
                if (bignum.Sign < 0) {
                    throw RubyExceptions.CreateArgumentError("can't compress negative numbers");
                }

                // not very efficient but good enough:
                uint[] words = bignum.GetWords();
                int i = words.Length;
                uint carry = 0;
                bool write = false;
                int shift = (Ceil(32 * i, 7) - 7) % 32;
                uint w = words[--i];
                while (true) {
                    if (shift == 0 && i == 0) {
                        stream.WriteByte((byte)(w & 0x7f));
                        break;
                    } else {
                        uint b = carry | (w >> shift) & 0x7f;
                        carry = 0;

                        write |= (b != 0);
                        if (write) {
                            stream.WriteByte((byte)(b | (1 << 7)));
                        }

                        if (shift < 7) {
                            carry = (w & (uint)((1 << shift) - 1)) << (7 - shift);
                            w = words[--i];
                        }
                    }

                    // (shift -= 7) mod 32:
                    shift = (shift + 32 - 7) % 32;
                }
            }
        }

        private static object ReadBer(MutableString/*!*/ data, ref int index) {
            int i = index;
            try {
                // skip initial zeroes:
                while (data.GetByte(i) == 0x80) { i++; }
                index = i;
                while ((data.GetByte(i) & 0x80) != 0) { i++; }
            } catch (IndexOutOfRangeException) {
                index = i;
                return null;
            }
            int byteCount = i - index + 1;

            if (byteCount <= 9) { // 9 * 7 == 63 < sizeof(long) * 8
                long result = 0;
                i = index;
                for (int j = 0; j < byteCount; j++, i++) {
                    result = (result << 7) | ((long)data.GetByte(i) & 0x7f);
                }
                index = i;
                return Protocols.Normalize(result);
            }

            uint[] words = new uint[CeilDiv(byteCount * 7, 32)];
            int shift = 0;
            int windex = 0;
            long w = 0;
            for (int j = 0; j < byteCount; j++, i--) {
                w |= ((long)data.GetByte(i) & 0x7f) << shift;
                if (shift >= 32 - 7) {
                    words[windex++] = (uint)(w & 0xffffffff);
                    w = w >> 32;
                }

                shift = (shift + 7) % 32;
            }
            if (w > 0) {
                words[windex] |= (uint)w;
            }
            index += byteCount;
            return BigIntegerCompat.Create(+1, words);
        }

        #endregion

        #region Base64

        private static byte[] _Base64Table = new byte[] { 
            (byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'E', (byte)'F', (byte)'G', (byte)'H', (byte)'I', (byte)'J', 
            (byte)'K', (byte)'L', (byte)'M', (byte)'N', (byte)'O', (byte)'P', (byte)'Q', (byte)'R', (byte)'S', (byte)'T', 
            (byte)'U', (byte)'V', (byte)'W', (byte)'X', (byte)'Y', (byte)'Z', (byte)'a', (byte)'b', (byte)'c', (byte)'d', 
            (byte)'e', (byte)'f', (byte)'g', (byte)'h', (byte)'i', (byte)'j', (byte)'k', (byte)'l', (byte)'m', (byte)'n', 
            (byte)'o', (byte)'p', (byte)'q', (byte)'r', (byte)'s', (byte)'t', (byte)'u', (byte)'v', (byte)'w', (byte)'x', 
            (byte)'y', (byte)'z', (byte)'0', (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7', 
            (byte)'8', (byte)'9', (byte)'+', (byte)'/'
        };

        public static void WriteBase64(Stream/*!*/ stream, MutableString/*!*/ str, int bytesPerLine) {
            ContractUtils.RequiresNotNull(stream, "stream");
            ContractUtils.RequiresNotNull(str, "str");
            ContractUtils.Requires(bytesPerLine > 2 || bytesPerLine == 0, "bytesPerLine");

            // pack("m0") is one unbroken line with no trailing newline, which is what
            // callers wanting a bare Base64 token (Digest#base64digest) need.
            bool singleLine = bytesPerLine == 0;

            bytesPerLine = bytesPerLine - bytesPerLine % 3;
            int a, b, c;
            int length = str.GetByteCount();
            int remainingBytes = length % 3;
            int triples = length - remainingBytes;
            byte[] table = _Base64Table;
            int lineLength = 0;
            for (int i = 0; i < triples; i += 3) {
                a = str.GetByte(i);
                b = str.GetByte(i + 1);
                c = str.GetByte(i + 2);

                stream.WriteByte(table[(a & 0xfc) >> 2]);
                stream.WriteByte(table[((a & 3) << 4) | ((b & 240) >> 4)]);
                stream.WriteByte(table[((b & 15) << 2) | ((c & 0xc0) >> 6)]);
                stream.WriteByte(table[c & 0x3f]);
                lineLength += 3;

                if (!singleLine && lineLength == bytesPerLine) {
                    stream.WriteByte((byte)'\n');
                    lineLength = 0;
                }
            }

            if (remainingBytes == 0) {
                if (!singleLine && lineLength != 0) {
                    stream.WriteByte((byte)'\n');
                }
                return;
            }

            a = str.GetByte(triples);
            stream.WriteByte(table[(a & 0xfc) >> 2]);
            switch (remainingBytes) {
                case 1:
                    stream.WriteByte(table[(a & 3) << 4]);
                    stream.WriteByte((byte)'=');
                    break;

                case 2:
                    b = str.GetByte(triples + 1);
                    stream.WriteByte(table[((a & 3) << 4) | ((b & 240) >> 4)]);
                    stream.WriteByte(table[(b & 15) << 2]);
                    break;
            }
            stream.WriteByte((byte)'=');
            if (!singleLine) {
                stream.WriteByte((byte)'\n');
            }
        }
        
        private static MutableString/*!*/ ReadBase64(MutableString/*!*/ data, ref int offset) {
            int length = data.GetByteCount();
            var result = MutableString.CreateBinary();

            while (true) {
                // Two characters are the least that encodes anything. Beyond that, running out
                // of input is not a reason to discard what has already been read: MRI decodes
                // "U2VuZCByZWluZm9yY2VtZW50cw" without its padding, and takes the "3d" out of
                // "3d}}", so a missing third or fourth character just ends the string early.
                int a = DecodeBase64Byte(data, length, true, ref offset);
                int b = DecodeBase64Byte(data, length, true, ref offset);
                if (a < 0 || b < 0) {
                    break;
                }

                int c = DecodeBase64Byte(data, length, false, ref offset);
                int d = (c >= 0) ? DecodeBase64Byte(data, length, false, ref offset) : -2;

                int buffer = (a << 18) | (b << 12);
                result.Append((byte)((buffer >> 16) & 0xff));

                // -1 is the end of the input and -2 is a '='; either one ends the string.
                if (c < 0) {
                    break;
                }
                buffer |= (c << 6);
                result.Append((byte)((buffer >> 8) & 0xff));

                if (d < 0) {
                    break;
                }
                buffer |= d;
                result.Append((byte)(buffer & 0xff));
            }

            return result;
        }

        /// <summary>
        /// The "m0" directive. Where "m" ignores anything that is not base64, this accepts only
        /// canonical input - the alphabet, a length that is a multiple of four, and padding that
        /// is both correct and last. A newline is enough to fail it, which is the whole point of
        /// Base64.strict_decode64.
        /// </summary>
        private static MutableString/*!*/ ReadStrictBase64(MutableString/*!*/ data, ref int offset) {
            int length = data.GetByteCount();
            var result = MutableString.CreateBinary();

            int buffer = 0;
            int bits = 0;
            int padding = 0;
            int consumed = 0;

            while (offset < length) {
                int c = data.GetByte(offset++);
                consumed++;

                if (c == '=') {
                    padding++;
                    continue;
                }

                int value = Base64Value(c);
                if (value < 0 || padding > 0) {
                    throw RubyExceptions.CreateArgumentError("invalid base64");
                }

                buffer = (buffer << 6) | value;
                bits += 6;
                if (bits == 24) {
                    result.Append((byte)((buffer >> 16) & 0xff));
                    result.Append((byte)((buffer >> 8) & 0xff));
                    result.Append((byte)(buffer & 0xff));
                    buffer = 0;
                    bits = 0;
                }
            }

            if (consumed % 4 != 0) {
                throw RubyExceptions.CreateArgumentError("invalid base64");
            }

            switch (bits) {
                case 0:
                    if (padding != 0) {
                        throw RubyExceptions.CreateArgumentError("invalid base64");
                    }
                    break;

                case 12:
                    if (padding != 2) {
                        throw RubyExceptions.CreateArgumentError("invalid base64");
                    }
                    result.Append((byte)((buffer >> 4) & 0xff));
                    break;

                case 18:
                    if (padding != 1) {
                        throw RubyExceptions.CreateArgumentError("invalid base64");
                    }
                    result.Append((byte)((buffer >> 10) & 0xff));
                    result.Append((byte)((buffer >> 2) & 0xff));
                    break;

                default:
                    throw RubyExceptions.CreateArgumentError("invalid base64");
            }

            return result;
        }

        /// <summary>The character's place in the base64 alphabet, or -1 if it has none.</summary>
        private static int Base64Value(int c) {
            if (unchecked((uint)c - 'A' <= (uint)'Z' - 'A')) {
                return c - 'A';
            }
            if (unchecked((uint)c - 'a' <= (uint)'z' - 'a')) {
                return c - 'a' + 'Z' - 'A' + 1;
            }
            if (unchecked((uint)c - '0' <= (uint)'9' - '0')) {
                return c - '0' + 2 * ('Z' - 'A' + 1);
            }
            switch (c) {
                case '+': return 62;
                case '/': return 63;
            }
            return -1;
        }

        private static int DecodeBase64Byte(MutableString/*!*/ data, int length, bool skipEquals, ref int offset) {
            while (true) {
                if (offset >= length) {
                    return -1;
                }

                int c = data.GetByte(offset++);
                if (unchecked((uint)c - 'A' <= (uint)'Z' - 'A')) {
                    return c - 'A';
                }

                if (unchecked((uint)c - 'a' <= (uint)'z' - 'a')) {
                    return c - 'a' + 'Z' - 'A' + 1;
                }

                if (unchecked((uint)c - '0' <= (uint)'9' - '0')) {
                    return c - '0' + 2 * ('Z' - 'A' + 1);
                }

                switch (c) {
                    case '+': return 62;
                    case '/': return 63;
                    case '=':
                        if (skipEquals) {
                            break;
                        }
                        offset--;
                        return -2;
                }
            }
        }

        #endregion

        #region Printed Quotable

        /// <summary>
        /// Printable characters: [33, 60], [62, 126], space (32), tab (9) but not at the end of line
        /// Escaped: others =XX
        /// Soft eolns (could be inserted anywhere, Ruby: after each count + 1 characters): =\n
        /// </summary>
        private static void WritePrintedQuotable(Stream/*!*/ stream, MutableString/*!*/ str, int bytesPerLine) {
            bytesPerLine++;
            int lineLength = 0;
            int length = str.GetByteCount();
            for (int i = 0; i < length; i++) {
                byte c = str.GetByte(i);
                if (c >= 33 && c <= 60 || c >= 62 && c <= 126 || c == 9 || c == 32) {
                    stream.WriteByte(c);
                    lineLength++;

                    // A tab or a space at the end of a line would not survive the journey - mail
                    // transports are allowed to strip trailing whitespace - so a soft break goes
                    // in behind it to make it no longer the last thing on the line.
                    if ((c == 9 || c == 32) && i + 1 < length && str.GetByte(i + 1) == (byte)'\n') {
                        stream.WriteByte((byte)'=');
                        stream.WriteByte((byte)'\n');
                        lineLength = 0;
                    }
                } else if (c == (byte)'\n') {
                    stream.WriteByte(c);
                    lineLength = 0;
                } else {
                    stream.WriteByte((byte)'=');
                    stream.WriteByte((byte)(c >> 4).ToUpperHexDigit());
                    stream.WriteByte((byte)(c & 0x0f).ToUpperHexDigit());
                    lineLength += 3;
                }

                if (lineLength >= bytesPerLine) {
                    stream.WriteByte((byte)'=');
                    stream.WriteByte((byte)'\n');
                    lineLength = 0;
                }
            }

            if (lineLength > 0) {
                stream.WriteByte((byte)'=');
                stream.WriteByte((byte)'\n');
            }
        }

        private static MutableString/*!*/ ReadQuotedPrintable(MutableString/*!*/ data, ref int index) {
            MutableString result = MutableString.CreateBinary();
            int length = data.GetByteCount();
            int i = index;
            while (i < length) {
                byte c = data.GetByte(i++);
                if (c == '=') {
                    // An "=" that does not begin a complete escape is not an error and does not
                    // end the string: it was never an escape, so it and whatever followed it
                    // stay exactly as they were found.
                    if (i >= length) {
                        result.Append((byte)'=');
                        break;
                    }

                    c = data.GetByte(i);
                    if (c == '\n') {
                        i++;
                        continue;
                    } 
                    if (c == '\r' && i + 1 < length && data.GetByte(i + 1) == '\n') {
                        i += 2;
                        continue;
                    }

                    int hi = Tokenizer.ToDigit(c);
                    if (hi >= 16) {
                        result.Append((byte)'=');
                        continue;
                    }
                    i++;

                    if (i >= length) {
                        result.Append((byte)'=');
                        result.Append(c);
                        break;
                    }

                    int lo = Tokenizer.ToDigit(data.GetByte(i));
                    if (lo >= 16) {
                        result.Append((byte)'=');
                        result.Append(c);
                        continue;
                    }

                    i++;
                    result.Append((byte)((hi << 4) | lo));
                } else {
                    result.Append(c);
                }
            }
            index = i;
            return result;
        }

        #endregion

        #region UU (Unix-Unix)

        private const char UUEncodeZero = '`';

        private static void EncodeUU(byte[]/*!*/ input, int bytesPerLine, Stream/*!*/ output) {
            ContractUtils.RequiresNotNull(input, "input");
            ContractUtils.RequiresNotNull(output, "output");

            if (input.Length == 0) {
                return;
            }

            bytesPerLine = bytesPerLine - bytesPerLine % 3;

            int remains = input.Length % bytesPerLine;
            int lines = input.Length / bytesPerLine;
            int inputOffset = 0;

            // encode full lines:
            for (int i = 0; i < lines; i++) {
                output.WriteByte(EncodeUUByte(bytesPerLine));

                for (int j = 0; j < bytesPerLine / 3; j++) {
                    EncodeUUTriple(output, input[inputOffset], input[inputOffset + 1], input[inputOffset + 2]);
                    inputOffset += 3;
                }

                output.WriteByte((byte)'\n');
            }

            // encode remaining bytes (if any):
            if (remains > 0) {
                output.WriteByte(EncodeUUByte(remains));
                int triples = remains / 3;
                remains %= 3;

                // full triples:
                for (int i = 0; i < triples; i++) {
                    EncodeUUTriple(output, input[inputOffset], input[inputOffset + 1], input[inputOffset + 2]);
                    inputOffset += 3;
                }

                // remaining bytes:
                if (remains == 1) {
                    EncodeUUTriple(output, input[inputOffset], 0, 0);
                } else if (remains == 2) {
                    EncodeUUTriple(output, input[inputOffset], input[inputOffset + 1], 0);
                }

                output.WriteByte((byte)'\n');
            }
        }

        private static MutableString/*!*/ ReadUU(MutableString/*!*/ data, ref int position) {
            var input = new MutableStringStream(data);
            var output = new MutableStringStream();
            input.Position = position;
            ReadUU(input, output);
            position = (int)input.Position;
            output.Dispose();
            return output.String;
        }

        private static bool ReadUU(Stream/*!*/ input, Stream/*!*/ output) {
            while (true) {
                int lineLength = input.ReadByte();
                if (lineLength == -1) {
                    return true;
                }

                lineLength = DecodeUUByte(lineLength);

                int remains = lineLength % 3;
                int triples = lineLength / 3;

                for (int i = 0; i < triples; i++) {
                    int a = DecodeUUByte(input.ReadByte());
                    int b = DecodeUUByte(input.ReadByte());
                    int c = DecodeUUByte(input.ReadByte());
                    int d = input.ReadByte();
                    if (d == -1) {
                        return false;
                    }
                    d = DecodeUUByte(d);

                    output.WriteByte((byte)((a << 2 | b >> 4) & 0xff));
                    output.WriteByte((byte)((b << 4 | c >> 2) & 0xff));
                    output.WriteByte((byte)((c << 6 | d) & 0xff));
                }

                if (remains > 0) {
                    int a = DecodeUUByte(input.ReadByte());
                    int b = DecodeUUByte(input.ReadByte());
                    int c = DecodeUUByte(input.ReadByte());
                    int d = input.ReadByte();
                    if (d == -1) {
                        return false;
                    }
                    d = DecodeUUByte(d);

                    output.WriteByte((byte)(a << 2 | b >> 4));

                    if (remains == 2) {
                        output.WriteByte((byte)(b << 4 | c >> 2));
                    }
                }

                if (input.ReadByte() != '\n') {
                    return false;
                }
            }
        }

        private static void EncodeUUTriple(Stream/*!*/ output, int a, int b, int c) {
            output.WriteByte(EncodeUUByte(a >> 2));
            output.WriteByte(EncodeUUByte(((a << 4) | (b >> 4)) & 0x3f));
            output.WriteByte(EncodeUUByte(((b << 2) | (c >> 6)) & 0x3f));
            output.WriteByte(EncodeUUByte(c & 0x3f));
        }

        private static byte EncodeUUByte(int b) {
            Debug.Assert(b <= 0x3f);
            return (byte)((b == 0) ? '`' : (0x20 + b));
        }

        private static byte DecodeUUByte(int c) {
            return (byte)((c - 0x20) & 0x3f);
        }

        #endregion

        #region Binary

        private static void WriteBits(Stream/*!*/ stream, int? countDef, bool reverse, MutableString/*!*/ str) {
            int length = str.GetByteCount();
            int count = countDef ?? length;

            int bits = Math.Min(count, length);
            int paddingBits = (8 - bits % 8) % 8;

            long resultLength = stream.Length +
                (bits + paddingBits) / 8 +
                ((count - bits) + (count - bits) % 2) / 2;

            // estimate the total length:
            stream.SetLength(resultLength);

            int b = 0;
            int i = 0;
            int s = reverse ? 0 : 7;
            int ds = reverse ? +1 : -1;
            while (i < bits) {
                b |= (str.GetByte(i++) & 1) << s;
                s += ds;
                if (i % 8 == 0) {
                    stream.WriteByte((byte)b);
                    b = 0;
                    s = reverse ? 0 : 7;
                }
            }

            if (paddingBits > 0) {
                stream.WriteByte((byte)b);
            }

            stream.Position = resultLength;
        }

        private static MutableString/*!*/ ReadBits(MutableString/*!*/ data, int? bitCount, ref int offset, bool lowestFirst) {
            int inputSize = data.GetByteCount() - offset;
            int outputSize = inputSize * 8;
            if (bitCount.HasValue) {
                int c = CeilDiv(bitCount.Value, 8);
                if (c <= inputSize) {
                    inputSize = c;
                    outputSize = bitCount.Value;
                }
            }

            // The pieces are always '0' and '1', and MRI hands them back as US-ASCII rather than
            // in the receiver's encoding or ASCII-8BIT.
            var result = MutableString.CreateMutable(outputSize, RubyEncoding.Ascii);
            if (outputSize == 0) {
                return result;
            }

            while (true) {
                int b = data.GetByte(offset++);
                for (int i = 0; i < 8; i++) {
                    result.Append((byte)('0' + ((b >> (lowestFirst ? i : 7 - i)) & 1)));
                    if (--outputSize == 0) {
                        return result;
                    }
                }
            }
        }

        #endregion

        #region Numbers

        private static int Ceil(int n, int d) {
            return CeilDiv(n, d) * d;
        }

        private static int CeilDiv(int n, int d) {
            return (n + d - 1) / d;
        }

        // TODO: move to MutableString?

        private static ulong ReadUInt64(MutableString/*!*/ data, ref int index, bool swap) {
            int i = index;
            index += 8;
            if (swap) {
                return
                    ((ulong)data.GetByte(i + 0) << 56) |
                    ((ulong)data.GetByte(i + 1) << 48) |
                    ((ulong)data.GetByte(i + 2) << 40) |
                    ((ulong)data.GetByte(i + 3) << 32) |
                    ((ulong)data.GetByte(i + 4) << 24) |
                    ((ulong)data.GetByte(i + 5) << 16) |
                    ((ulong)data.GetByte(i + 6) << 8) |
                    ((ulong)data.GetByte(i + 7));
            } else {
                return
                    ((ulong)data.GetByte(i + 7) << 56) |
                    ((ulong)data.GetByte(i + 6) << 48) |
                    ((ulong)data.GetByte(i + 5) << 40) |
                    ((ulong)data.GetByte(i + 4) << 32) |
                    ((ulong)data.GetByte(i + 3) << 24) |
                    ((ulong)data.GetByte(i + 2) << 16) |
                    ((ulong)data.GetByte(i + 1) << 8) |
                    ((ulong)data.GetByte(i + 0));
            }
        }

        internal static void Write(Stream/*!*/ stream, ulong n, bool swap) {
            if (swap) {
                stream.WriteByte((byte)(n >> 56));
                stream.WriteByte((byte)((n >> 48) & 0xff));
                stream.WriteByte((byte)((n >> 40) & 0xff));
                stream.WriteByte((byte)((n >> 32) & 0xff));
                stream.WriteByte((byte)((n >> 24) & 0xff));
                stream.WriteByte((byte)((n >> 16) & 0xff));
                stream.WriteByte((byte)((n >> 8) & 0xff));
                stream.WriteByte((byte)(n & 0xff));
            } else {
                stream.WriteByte((byte)(n & 0xff));
                stream.WriteByte((byte)((n >> 8) & 0xff));
                stream.WriteByte((byte)((n >> 16) & 0xff));
                stream.WriteByte((byte)((n >> 24) & 0xff));
                stream.WriteByte((byte)((n >> 32) & 0xff));
                stream.WriteByte((byte)((n >> 40) & 0xff));
                stream.WriteByte((byte)((n >> 48) & 0xff));
                stream.WriteByte((byte)(n >> 56));
            }
        }

        private static uint ReadUInt32(MutableString/*!*/ data, ref int index, bool swap) {
            int i = index;
            index += 4;
            if (swap) {
                return
                    ((uint)data.GetByte(i + 0) << 24) |
                    ((uint)data.GetByte(i + 1) << 16) |
                    ((uint)data.GetByte(i + 2) << 8) |
                    ((uint)data.GetByte(i + 3));
            } else {
                return
                    ((uint)data.GetByte(i + 3) << 24) |
                    ((uint)data.GetByte(i + 2) << 16) |
                    ((uint)data.GetByte(i + 1) << 8) |
                    ((uint)data.GetByte(i + 0));
            }
        }

        internal static void Write(Stream/*!*/ stream, uint n, bool swap) {
            if (swap) {
                stream.WriteByte((byte)(n >> 24));
                stream.WriteByte((byte)((n >> 16) & 0xff));
                stream.WriteByte((byte)((n >> 8) & 0xff));
                stream.WriteByte((byte)(n & 0xff));
            } else {
                stream.WriteByte((byte)(n & 0xff));
                stream.WriteByte((byte)((n >> 8) & 0xff));
                stream.WriteByte((byte)((n >> 16) & 0xff));
                stream.WriteByte((byte)(n >> 24));
            }
        }

        private static ushort ReadUInt16(MutableString/*!*/ data, ref int index, bool swap) {
            int i = index;
            index += 2;
            if (swap) {
                return (ushort)((data.GetByte(i + 0) << 8) | data.GetByte(i + 1));
            } else {
                return (ushort)((data.GetByte(i + 1) << 8) | data.GetByte(i + 0));
            }
        }

        private static void Write(Stream/*!*/ stream, ushort n, bool swap) {
            if (swap) {
                stream.WriteByte((byte)((n >> 8) & 0xff));
                stream.WriteByte((byte)(n & 0xff));
            } else {
                stream.WriteByte((byte)(n & 0xff));
                stream.WriteByte((byte)((n >> 8) & 0xff));
            }
        }

        private static double Int64BitsToDouble(long value) {
            return BitConverter.Int64BitsToDouble(value);
        }

        private static long DoubleToInt64Bits(double value) {
            return BitConverter.DoubleToInt64Bits(value);
        }

        private static double ReadDouble(MutableString/*!*/ data, ref int index, bool swap) {
            return Int64BitsToDouble(unchecked((long)ReadUInt64(data, ref index, swap)));
        }

        private static void WriteDouble(ConversionStorage<double>/*!*/ floatConversion,
            Stream/*!*/ stream, RubyArray/*!*/ self, int i, int count, bool swap) {
            for (int j = 0; j < count; j++) {
                Write(stream, unchecked((ulong)DoubleToInt64Bits(ToPackFloat(floatConversion, GetPackArg(self, i + j)))), swap);
            }
        }

        private static float ReadSingle(MutableString/*!*/ data, ref int index, bool swap) {
            byte[] bytes = data.GetBinarySlice(index, sizeof(float));
            if (swap) {
                byte b = bytes[0];
                bytes[0] = bytes[3];
                bytes[3] = b;
                b = bytes[1];
                bytes[1] = bytes[2];
                bytes[2] = b;
            }
            index += 4;
            return BitConverter.ToSingle(bytes, 0);
        }

        private static void WriteSingle(ConversionStorage<double>/*!*/ floatConversion,
            Stream/*!*/ stream, RubyArray/*!*/ self, int i, int count, bool swap) {
            for (int j = 0; j < count; j++) {
                byte[] bytes = BitConverter.GetBytes((float)ToPackFloat(floatConversion, GetPackArg(self, i + j)));
                if (swap) {
                    stream.WriteByte(bytes[3]);
                    stream.WriteByte(bytes[2]);
                    stream.WriteByte(bytes[1]);
                    stream.WriteByte(bytes[0]);
                } else {
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
        }

        private static void WriteUInt64(ConversionStorage<IntegerValue>/*!*/ integerConversion,
            Stream/*!*/ stream, RubyArray/*!*/ self, int i, int count, bool swap) {

            for (int j = 0; j < count; j++) {
                object value = GetPackArg(self, i + j);
                if (value == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion of nil into Integer");
                }

                IntegerValue integer = Protocols.CastToInteger(integerConversion, value);
                ulong u;
                if (integer.IsFixnum) {
                    Write(stream, unchecked((ulong)integer.Fixnum), swap);
                } else if (integer.Bignum.Abs().AsUInt64(out u)) {
                    if (integer.Bignum.Sign < 0) {
                        u = unchecked(~u + 1);
                    }
                    Write(stream, u, swap);
                } else {
                    throw RubyExceptions.CreateRangeError("bignum out of range (-2**64, 2**64)");
                }
            }
        }

        private static void WriteUInt32(ConversionStorage<IntegerValue>/*!*/ integerConversion,
            Stream/*!*/ stream, RubyArray/*!*/ self, int i, int count, bool swap) {
            for (int j = 0; j < count; j++) {
                long value = Protocols.CastToInt64Unchecked(integerConversion, GetPackArg(self, i + j));
                if (value <= -0x100000000L || value >= 0x100000000L) {
                    throw RubyExceptions.CreateRangeError("bignum out of range (-2**32, 2**32)");
                }
                Write(stream, unchecked((uint)value), swap);
            }
        }

        private static void WriteUInt16(ConversionStorage<IntegerValue>/*!*/ integerConversion,
            Stream/*!*/ stream, RubyArray/*!*/ self, int i, int count, bool swap) {
            for (int j = 0; j < count; j++) {
                long value = Protocols.CastToInt64Unchecked(integerConversion, GetPackArg(self, i + j));
                if (value <= -0x100000000L || value >= 0x100000000L) {
                    throw RubyExceptions.CreateRangeError("bignum out of range (-2**32, 2**32)");
                }
                Write(stream, unchecked((ushort)value), swap);
            }
        }

        private static double ToPackFloat(ConversionStorage<double>/*!*/ floatConversion, object value) {
            if (value is MutableString) {
                throw RubyExceptions.CreateTypeError("can't convert String into Float");
            }
            double result = Protocols.CastToFloat(floatConversion, value);
            // the CLR's NaN has its sign bit set; MRI packs the positive quiet NaN
            return Double.IsNaN(result) ? BitConverter.Int64BitsToDouble(0x7FF8000000000000L) : result;
        }
        private static void WriteInteger(ConversionStorage<IntegerValue>/*!*/ integerConversion,
            Stream/*!*/ stream, RubyArray/*!*/ self, int i, int count, int width, bool swap) {

            byte[] buffer = new byte[width];
            for (int j = 0; j < count; j++) {
                object value = GetPackArg(self, i + j);
                if (value == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion of nil into Integer");
                }

                IntegerValue integer = Protocols.CastToInteger(integerConversion, value);
                if (integer.IsFixnum) {
                    long n = integer.Fixnum;
                    for (int b = 0; b < width; b++) {
                        buffer[b] = unchecked((byte)(n >> (8 * Math.Min(b, 7))));
                    }
                    // Sign-extend past 8 bytes.
                    byte fill = (byte)(n < 0 ? 0xff : 0x00);
                    for (int b = 8; b < width; b++) {
                        buffer[b] = fill;
                    }
                } else {
                    byte[] bytes = integer.Bignum.ToByteArray();   // little-endian two's complement
                    byte fill = (byte)(integer.Bignum.Sign < 0 ? 0xff : 0x00);
                    for (int b = 0; b < width; b++) {
                        buffer[b] = b < bytes.Length ? bytes[b] : fill;
                    }
                }

                if (swap) {
                    for (int b = width - 1; b >= 0; b--) {
                        stream.WriteByte(buffer[b]);
                    }
                } else {
                    stream.Write(buffer, 0, width);
                }
            }
        }
        private static void FromHex(Stream/*!*/ stream, MutableString/*!*/ str, int nibbleCount, bool swap) {
            int maxCount = Math.Min(nibbleCount, str.GetByteCount());
            for (int i = 0, j = 0; i < (nibbleCount + 1) / 2; i++, j += 2) {
                int hiNibble = (j < maxCount) ? FromHexDigit(str.GetByte(j)) : 0;
                int loNibble = (j + 1 < maxCount) ? FromHexDigit(str.GetByte(j + 1)) : 0;
                Debug.Assert(hiNibble >= 0 && hiNibble < 16 && loNibble >= 0 && loNibble < 16);

                int c = (swap) ? (loNibble << 4) | hiNibble : (hiNibble << 4) | loNibble;
                stream.WriteByte((byte)c);
            }
        }

        // hexa digits -> values
        private static int FromHexDigit(int c) {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) {
                return (c - 'a' + 10) & 15;
            }
            return c & 15;
        }

        #endregion

        #region FormatDirective

        /// <summary>
        /// Byte order asked for by a directive's '&lt;' or '&gt;' modifier. Most directives have no
        /// choice: 'n' and 'N' are always big endian, 'v' and 'V' always little, and the rest take
        /// the machine's order unless a modifier overrides it.
        /// </summary>
        private enum Endianness {
            Native = 0,
            Little = 1,
            Big = 2,
        }

        private struct FormatDirective {
            internal readonly char Directive;
            internal readonly int? Count; // null means *
            internal readonly Endianness Order;

            internal FormatDirective(char directive, int? count)
                : this(directive, count, Endianness.Native) {
            }

            internal FormatDirective(char directive, int? count, Endianness order) {
                Directive = directive;
                Count = count;
                Order = order;
            }

            /// <summary>
            /// True when this directive's bytes are to be read or written most significant first.
            /// </summary>
            /// <summary>True when the bytes have to come out reversed on this machine.</summary>
            internal bool Swap {
                get { return IsBigEndian == BitConverter.IsLittleEndian; }
            }

            internal bool IsBigEndian {
                get {
                    switch (Order) {
                        case Endianness.Little: return false;
                        case Endianness.Big: return true;
                        default: return !BitConverter.IsLittleEndian;
                    }
                }
            }

            /// <summary>
            /// sizeof(long) for the platform we are running on. Windows is LLP64 (4) even at
            /// 64 bits; every Unix we run on is LP64, where it follows the pointer size.
            /// </summary>
            private static int NativeLongSize {
                get {
                    return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 4 : IntPtr.Size;
                }
            }

            /// <summary>
            /// The types '&lt;', '&gt;', '!' and '_' may follow. MRI spells the set out in the error
            /// message, so the two have to stay in step.
            /// </summary>
            private const string ModifiableTypes = "sSiIlLqQjJ";

            private static void RequireModifiable(char directive, char modifier) {
                if (ModifiableTypes.IndexOf(directive) < 0) {
                    throw RubyExceptions.CreateArgumentError("'{0}' allowed only after types {1}", modifier, ModifiableTypes);
                }
            }

            /// <summary>
            /// Resolves a type whose width is the platform's rather than fixed: 'l'/'L' under '!'
            /// or '_', and 'j'/'J', which are always pointer sized.
            /// </summary>
            private static char MapNative(char c) {
                switch (c) {
                    case 'l': return NativeLongSize == 4 ? 'i' : 'q';
                    case 'L': return NativeLongSize == 4 ? 'I' : 'Q';
                    case 'j': return IntPtr.Size == 4 ? 'i' : 'q';
                    case 'J': return IntPtr.Size == 4 ? 'I' : 'Q';
                    default: return c;
                }
            }

            /// <summary>
            /// MRI renders the offending format back into the message with escapes, so
            /// "C\0C".unpack reports unknown unpack directive '\x00' in 'C\0C'.
            /// </summary>
            private static string/*!*/ Escape(string/*!*/ format) {
                var result = new StringBuilder(format.Length);
                foreach (char c in format) {
                    switch (c) {
                        case '\0': result.Append("\\0"); break;
                        case '\n': result.Append("\\n"); break;
                        case '\r': result.Append("\\r"); break;
                        case '\t': result.Append("\\t"); break;
                        case '\\': result.Append("\\\\"); break;
                        default:
                            if (c < ' ' || c > '~') {
                                result.Append("\\x").Append(((int)c).ToString("X2"));
                            } else {
                                result.Append(c);
                            }
                            break;
                    }
                }
                return result.ToString();
            }

            private static string/*!*/ DescribeDirective(char c) {
                if (c < ' ' || c > '~') {
                    return "\\x" + ((int)c).ToString("X2");
                }
                return c.ToString();
            }

            internal static Exception/*!*/ UnknownDirective(char c, string/*!*/ format, bool packing) {
                return RubyExceptions.CreateArgumentError("unknown {0} directive '{1}' in '{2}'",
                    packing ? "pack" : "unpack", DescribeDirective(c), Escape(format)
                );
            }

            /// <summary>Characters a format may contain freely; MRI skips anything ISSPACE.</summary>
            private static bool IsFormatSpace(char c) {
                return c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';
            }

            /// <summary>
            /// The directive letters MRI knows. Both pack and unpack accept the same set; 'k',
            /// 'o', 'r', 't', 'y' and their upper case forms are not directives, and a format
            /// containing one is an error rather than something to skip over.
            /// </summary>
            private const string KnownDirectives = "aAZbBhHcCsSiIlLqQjJnNvVUwmMuPpDdFfEeGgxX@";

            private static bool IsKnownDirective(char c) {
                return KnownDirectives.IndexOf(c) >= 0;
            }

            internal static IEnumerable<FormatDirective>/*!*/ Enumerate(string/*!*/ format) {
                return Enumerate(format, false);
            }

            internal static IEnumerable<FormatDirective>/*!*/ Enumerate(string/*!*/ format, bool packing) {
                for (int i = 0; i < format.Length; i++) {
                    char c = format[i];
                    if (c == '%') {
                        throw RubyExceptions.CreateArgumentError("% is not supported");
                    }

                    if (c == '#') {
                        do { i++; } while (i < format.Length && format[i] != '\n');
                        continue;
                    }

                    if (IsFormatSpace(c)) {
                        continue;
                    }

                    // Anything else that is not a directive is an error. Silently skipping it -
                    // which is what this used to do - turned "abc".unpack("1") into [] instead of
                    // the ArgumentError ruby/spec asks for, and hid every typo in a format.
                    if (!IsKnownDirective(c)) {
                        throw UnknownDirective(c, format, packing);
                    }

                    i++;

                    // Modifiers, in any order and any number: '<'/'>' choose a byte order and
                    // '!'/'_' ask for the platform's width of the C type. MRI accepts "s!<" and
                    // "s<!" alike but rejects a second byte order modifier, even a repeat.
                    var order = Endianness.Native;
                    bool native = false;
                    while (i < format.Length) {
                        char modifier = format[i];
                        if (modifier == '<' || modifier == '>') {
                            RequireModifiable(c, modifier);
                            if (order != Endianness.Native) {
                                throw RubyExceptions.CreateRangeError("Can't use both '<' and '>'");
                            }
                            order = modifier == '<' ? Endianness.Little : Endianness.Big;
                        } else if (modifier == '!' || modifier == '_') {
                            // MRI treats ! and _ identically: both mean "the native size of this C
                            // type". Dropping ! made pack('l!') four bytes wide on LP64, which is
                            // how mspec's PlatformGuard::C_LONG_SIZE came out as 32 on Linux.
                            RequireModifiable(c, modifier);
                            native = true;
                        } else {
                            break;
                        }
                        i++;
                    }

                    // 'j' and 'J' are pointer sized whether or not a modifier says so.
                    if (native || c == 'j' || c == 'J') {
                        c = MapNative(c);
                    }

                    int? count = 1;
                    char c2 = (i < format.Length) ? format[i] : '\0';

                    if (Tokenizer.IsDecimalDigit(c2)) {
                        int pos1 = i;
                        i++;
                        while (i < format.Length && Tokenizer.IsDecimalDigit(format[i])) {
                            i++;
                        }
                        // MRI reads the count as a C long (CVE-2018-8778): one that does not fit is
                        // an error, and one that fits a long but not an int cannot be satisfied anyway.
                        long longCount;
                        if (!Int64.TryParse(format.Substring(pos1, (i - pos1)), NumberStyles.None, CultureInfo.InvariantCulture, out longCount)) {
                            throw RubyExceptions.CreateRangeError("pack length too big");
                        }
                        count = (int)Math.Min(longCount, Int32.MaxValue);
                        i--;
                    } else if (c2 == '*') {
                        count = null;
                    } else {
                        i--;
                        if (c == '@' && !packing) {
                            // "@" with no count is the one place the two directions part company:
                            // pack seeks to 1, like every other directive's default count, while
                            // unpack seeks to the start.
                            count = 0;
                        }
                    }

                    yield return new FormatDirective(c, count, order);
                }
            }
        }

        #endregion

        #region Pack

        public static MutableString/*!*/ Pack(
            ConversionStorage<IntegerValue>/*!*/ integerConversion,
            ConversionStorage<double>/*!*/ floatConversion,
            ConversionStorage<MutableString>/*!*/ stringCast,
            ConversionStorage<MutableString>/*!*/ tosConversion,
            RubyArray/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ format, MutableString buffer) {

            // With buffer: the result is appended to the string given, which is what comes
            // back, keeping its own encoding.  '@' then addresses the buffer from its start.
            MutableString basis = buffer ?? MutableString.CreateBinary();
            RubyArray pointers = null;
            using (MutableStringStream stream = new MutableStringStream(basis)) {
                stream.Position = basis.GetByteCount();
                int i = 0;

                // MRI decides the result encoding from the directives it used: US-ASCII
                // until something can emit an arbitrary byte, UTF-8 if only 'U' did.
                // 1 = still ASCII, 2 = UTF-8, 0 = binary.
                int encodingInfo = 1;

                foreach (FormatDirective directive in FormatDirective.Enumerate(format.ConvertToString(), true)) {
                    int count = directive.Count ?? self.Count - i;

                    switch (directive.Directive) {
                        case 'U':
                            if (encodingInfo == 1) {
                                encodingInfo = 2;
                            }
                            break;

                        case 'm':
                        case 'M':
                        case 'u':
                            break;

                        default:
                            encodingInfo = 0;
                            break;
                    }

                    MutableString str;
                    switch (directive.Directive) {
                        case '@':
                            count = 0;
                            // "@N" seeks to N and "@*" to the start; a bare "@" carries the
                            // ordinary default count of 1.
                            stream.SetLength(stream.Position = directive.Count ?? 0);
                            break;

                        case 'A':
                        case 'a':
                        case 'Z':
                            count = 1;
                            WriteString(stream, directive, ToMutableString(stringCast, stream, GetPackArg(self, i)));
                            break;

                        case 'B':
                        case 'b':
                            count = 1;
                            WriteBits(
                                stream,
                                directive.Count,
                                directive.Directive == 'b',
                                str = GetPackArg(self, i) != null ? ToMutableString(stringCast, stream, GetPackArg(self, i)) : MutableString.FrozenEmpty
                            );
                            break;

                        case 'd': // 8-byte native-endian
                        case 'D':
                            WriteDouble(floatConversion, stream, self, i, count, false);
                            break;

                        case 'e': // 4-byte little-endian
                            WriteSingle(floatConversion, stream, self, i, count, !BitConverter.IsLittleEndian);
                            break;

                        case 'E': // 8-byte little-endian
                            WriteDouble(floatConversion, stream, self, i, count, !BitConverter.IsLittleEndian);
                            break;

                        case 'f': // 4-byte native-endian
                        case 'F':
                            WriteSingle(floatConversion, stream, self, i, count, false);
                            break;

                        case 'g': // 4-byte big-endian
                            WriteSingle(floatConversion, stream, self, i, count, BitConverter.IsLittleEndian);
                            break;

                        case 'G': // 8-byte big-endian
                            WriteDouble(floatConversion, stream, self, i, count, BitConverter.IsLittleEndian);
                            break;

                        case 'h':
                        case 'H':
                            // nil is not skipped: it is a string with no digits in it, and the
                            // count still asks for that many nibbles, so the padding goes out.
                            object hexArg = GetPackArg(self, i);
                            if (hexArg != null) {
                                str = ToMutableString(stringCast, stream, hexArg);
                                FromHex(stream, str, directive.Count ?? str.GetByteCount(), directive.Directive == 'h');
                            } else {
                                FromHex(stream, MutableString.FrozenEmpty, directive.Count ?? 1, directive.Directive == 'h');
                            }
                            count = 1;
                            break;

                        case 'Q':
                        case 'q': // (un)signed 8-byte, native order unless '<'/'>' said otherwise
                            WriteInteger(integerConversion, stream, self, i, count, 8, directive.Swap);
                            break;

                        case 'l':
                        case 'i':
                        case 'L':
                        case 'I': // (un)signed 4-byte
                            WriteInteger(integerConversion, stream, self, i, count, 4, directive.Swap);
                            break;

                        case 'N': // (un)signed 4-byte big-endian
                            WriteInteger(integerConversion, stream, self, i, count, 4, BitConverter.IsLittleEndian);
                            break;

                        case 'n': // (un)signed 2-byte big-endian
                            WriteInteger(integerConversion, stream, self, i, count, 2, BitConverter.IsLittleEndian);
                            break;

                        case 'V': // (un)signed 4-byte little-endian
                            WriteInteger(integerConversion, stream, self, i, count, 4, !BitConverter.IsLittleEndian);
                            break;

                        case 'v': // (un)signed 2-byte little-endian
                            WriteInteger(integerConversion, stream, self, i, count, 2, !BitConverter.IsLittleEndian);
                            break;

                        case 's': // (un)signed 2-byte
                        case 'S':
                            WriteInteger(integerConversion, stream, self, i, count, 2, directive.Swap);
                            break;

                        case 'c': // (un)signed 1-byte
                        case 'C':
                            WriteInteger(integerConversion, stream, self, i, count, 1, false);
                            break;

                        case 'm': // Base64
                            if (GetPackArg(self, i) == null) {
                                throw RubyExceptions.CreateImplicitConversionError("nil", "String");
                            }
                            WriteBase64(stream, ToMutableString(stringCast, stream, GetPackArg(self, i)),
                                // count 0 means a single unbroken line; 1 and 2 fall back to the default 45
                                (directive.Count.HasValue && (directive.Count.Value > 2 || directive.Count.Value == 0)) ? directive.Count.Value : 45
                            );
                            count = 1;
                            break;

                        case 'M': // quoted-printable, MIME encoding
                            count = 1;
                            if (GetPackArg(self, i) != null) {
                                var site = tosConversion.GetSite(ConvertToSAction.Make(tosConversion.Context));
                                MutableString s = site.Target(site, GetPackArg(self, i));
                                stream.String.TaintBy(s);
                                WritePrintedQuotable(stream, s,
                                    (directive.Count.HasValue && directive.Count.Value >= 2) ? directive.Count.Value : 72
                                );
                            }
                            break;

                        case 'u': // UU-encoded
                            if (GetPackArg(self, i) == null) {
                                throw RubyExceptions.CreateImplicitConversionError("nil", "String");
                            }
                            RubyEncoder.EncodeUU(ToMutableString(stringCast, stream, GetPackArg(self, i)).ToByteArray(), 
                                (directive.Count.HasValue && directive.Count.Value > 2) ? directive.Count.Value : 45, 
                                stream
                            );
                            count = 1;
                            break;

                        case 'w': // BER-encoded
                            for (int j = 0; j < count; j++) {
                                WriteBer(stream, Protocols.CastToInteger(integerConversion, GetPackArg(self, i + j)));
                            }
                            break;

                        case 'U': // UTF8 code point
                            for (int j = 0; j < count; j++) {
                                IntegerValue codePoint = Protocols.CastToInteger(integerConversion, GetPackArg(self, i + j));
                                long scalar;
                                if (codePoint.IsFixnum) {
                                    scalar = codePoint.Fixnum;
                                } else if (!Int64.TryParse(codePoint.Bignum.ToString(), out scalar)) {
                                    // MRI converts to long first, so a value that does not even fit
                                    // there is reported as a conversion failure, not a bad code point.
                                    throw RubyExceptions.CreateRangeError("bignum too big to convert into 'long'");
                                }
                                if (scalar < 0 || scalar > 0x7fffffff) {
                                    throw RubyExceptions.CreateRangeError("pack(U): value out of range");
                                }
                                RubyEncoder.WriteUtf8CodePoint(stream, codePoint.Fixnum);
                            }
                            break;

                        case 'X':
                            count = 0;
                            int len3 = directive.Count.HasValue ? directive.Count.Value : 0;
                            if (len3 > stream.Position) {
                                throw RubyExceptions.CreateArgumentError("X outside of string");
                            }
                            stream.String.Write((int)stream.Position - len3, 0, len3);
                            stream.Position -= len3;
                            break;

                        case 'p':
                        case 'P': {
                            // MRI writes the address of the string and remembers the string
                            // itself alongside the result, so that unpacking can find it again.
                            // The address is a real one, of a native block holding the string's
                            // bytes (PackedPointers), so C can read and - through Fiddle - write
                            // the string as it does in MRI. The list of strings travels as an
                            // instance variable, which is what makes it survive #dup and not
                            // survive a round trip through a Symbol - exactly the rule MRI's
                            // associated-object table follows.
                            count = 1;
                            object pointerArg = GetPackArg(self, i);
                            long pointer = 0;
                            if (pointerArg != null) {
                                MutableString target = ToMutableString(stringCast, stream, pointerArg);
                                if (target != null) {
                                    if (pointers == null) {
                                        pointers = new RubyArray();
                                    }
                                    pointers.Add(target);
                                    pointer = PackedPointers.AddressOf(target);
                                }
                            }
                            WritePointer(stream, pointer);
                            break;
                        }

                        case 'x':
                            count = 0;
                            int len4 = directive.Count.HasValue ? directive.Count.Value : 0;
                            stream.String.Write((int)stream.Position, 0, len4);
                            stream.Position += len4;
                            break;

                        default:
                            count = 0;
                            break;
                    }

                    i += count;
                }
                stream.SetLength(stream.Position);
                MutableString result = stream.String.TaintBy(format);
                if (pointers != null) {
                    stringCast.Context.SetInstanceVariable(result, PackedPointersVariable, pointers);
                }
                if (buffer == null) {
                    if (encodingInfo == 1) {
                        result.ForceEncoding(RubyEncoding.Ascii);
                    } else if (encodingInfo == 2) {
                        result.ForceEncoding(RubyEncoding.UTF8);
                    }
                }
                return result;
            }
        }

        /// <summary>Where a packed string keeps the strings its 'p' and 'P' pointers refer to.</summary>
        private const string PackedPointersVariable = "@__packed_pointers__";

        private static void WritePointer(Stream/*!*/ stream, long pointer) {
            byte[] bytes = BitConverter.GetBytes(pointer);
            stream.Write(bytes, 0, IntPtr.Size);
        }

        private static long ReadPointer(MutableString/*!*/ data, ref int index) {
            long pointer = 0;
            for (int i = 0; i < IntPtr.Size; i++) {
                pointer |= (long)data.GetByte(index + i) << (8 * i);
            }
            index += IntPtr.Size;
            return pointer;
        }

        /// <summary>
        /// The string a packed pointer refers to. It can only be found through the list the
        /// packing left on that same string, so bytes that merely look like a pointer - a copy
        /// made through a Symbol, say - have nothing to point at and MRI says so.
        /// </summary>
        private static object Dereference(RubyContext/*!*/ context, MutableString/*!*/ self, long pointer, FormatDirective directive) {
            if (pointer == 0) {
                return null;
            }

            // As MRI's: one of the strings packed into this one, the one whose bytes are at that
            // address now - a string that has outgrown its block since is at another.
            object stored;
            RubyArray pointers;
            MutableString target = null;
            if (context.TryGetInstanceVariable(self, PackedPointersVariable, out stored) && (pointers = stored as RubyArray) != null) {
                foreach (object candidate in pointers) {
                    var str = candidate as MutableString;
                    if (str != null && PackedPointers.IsAddressOf(str, pointer)) {
                        target = str;
                        break;
                    }
                }
            }
            if (target == null) {
                throw RubyExceptions.CreateArgumentError("no associated pointer");
            }

            if (directive.Directive == 'p') {
                return target.Clone();
            }

            // 'P' takes a length in characters, and never more than the string holds - MRI stops
            // at the NUL that ends the C string it thinks it is reading.
            int count = directive.Count ?? target.GetCharCount();
            return target.GetSlice(0, Math.Min(count, target.GetCharCount()));
        }

        private static MutableString ToMutableString(ConversionStorage<MutableString>/*!*/ stringCast, MutableStringStream/*!*/ stream, object value) {
            var site = stringCast.GetSite(ConvertToStrAction.Make(stringCast.Context));
            var result = site.Target(site, value);
            if (result != null) {
                stream.String.TaintBy(result);
            }
            return result;
        }

        private static object GetPackArg(RubyArray/*!*/ array, int index) {
            if (index >= array.Count) {
                throw RubyExceptions.CreateArgumentError("too few arguments");
            }
            return array[index];                    
        }

        private static void WriteString(Stream/*!*/ stream, FormatDirective directive, MutableString str) {
            // TODO (opt): unneccessary copy of byte[]
            byte[] bytes = str != null ? str.ToByteArray() : Utils.EmptyBytes;
            int dataLen;
            int paddedLen;
            if (directive.Count.HasValue) {
                paddedLen = directive.Count.Value;
                dataLen = Math.Min(bytes.Length, paddedLen);
            } else {
                paddedLen = bytes.Length;
                dataLen = bytes.Length;
            }
            stream.Write(bytes, 0, dataLen);
            if (paddedLen > dataLen) {
                byte fill = (directive.Directive == 'A') ? (byte)' ' : (byte)0;
                for (int j = 0; j < (paddedLen - dataLen); j++) {
                    stream.WriteByte(fill);
                }
            }
            if (directive.Directive == 'Z' && !directive.Count.HasValue) {
                stream.WriteByte((byte)0);
            }
        }

        #endregion

        #region Unpack

        public static RubyArray/*!*/ Unpack(RubyContext/*!*/ context, MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ format) {
            return Unpack(context, self, format, 0);
        }

        public static RubyArray/*!*/ Unpack(RubyContext/*!*/ context, MutableString/*!*/ self, [NotNull]MutableString/*!*/ format, int offset) {
            RubyArray result = new RubyArray(1 + self.Length / 2);

            // TODO: encodings
            int position = offset;
            int length = self.GetByteCount();
            foreach (FormatDirective directive in FormatDirective.Enumerate(format.ToString())) {
                int count, maxCount;
                int nilCount = 0;
                switch (directive.Directive) {
                    case '@':
                        if (directive.Count.HasValue) {
                            if (directive.Count.Value > length) {
                                throw RubyExceptions.CreateArgumentError("@ outside of string");
                            }
                            position = directive.Count.Value > 0 ? directive.Count.Value : 0;
                        } else {
                            // "*" stands for what is left of the string, and "@" then seeks to
                            // that offset - counted from the start, so "C@*C" lands on the last
                            // byte rather than past the end.
                            position = length - position;
                        }
                        break;

                    case 'A':
                    case 'a':
                    case 'Z':
                        // Unpacking takes bytes out of a string rather than characters, so what
                        // comes back is binary whatever the string it came from was tagged as.
                        MutableString unpacked = ReadString(self, directive.Directive, directive.Count, ref position);
                        unpacked.ForceEncoding(RubyEncoding.Binary);
                        result.Add(unpacked);
                        break;

                    case 'B':
                    case 'b':
                        result.Add(ReadBits(self, directive.Count, ref position, directive.Directive == 'b'));
                        break;

                    case 'p':
                    case 'P':
                        if (length - position < IntPtr.Size) {
                            result.Add(null);
                            position = length;
                            break;
                        }
                        result.Add(Dereference(context, self, ReadPointer(self, ref position), directive));
                        break;

                    case 'c':
                        count = CalculateCounts(length - position, directive.Count, sizeof(sbyte), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add((int)unchecked((sbyte)self.GetByte(position++)));
                        }
                        break;

                    case 'C':
                        count = CalculateCounts(length - position, directive.Count, sizeof(byte), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add((int)self.GetByte(position++));
                        }
                        break;

                    case 'd': // 8-byte native-endian
                    case 'D':
                        count = CalculateCounts(length - position, directive.Count, sizeof(double), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(ReadDouble(self, ref position, false));
                        }
                        break;

                    case 'e': // 4-byte little-endian
                        count = CalculateCounts(length - position, directive.Count, sizeof(float), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add((double)ReadSingle(self, ref position, !BitConverter.IsLittleEndian));
                        }
                        break;

                    case 'E': // 8-byte little-endian
                        count = CalculateCounts(length - position, directive.Count, sizeof(double), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(ReadDouble(self, ref position, !BitConverter.IsLittleEndian));
                        }
                        break;

                    case 'f': // 4-byte native-endian
                    case 'F':
                        count = CalculateCounts(length - position, directive.Count, sizeof(float), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add((double)ReadSingle(self, ref position, false));
                        }
                        break;

                    case 'g': // 4-byte big-endian
                        count = CalculateCounts(length - position, directive.Count, sizeof(float), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add((double)ReadSingle(self, ref position, BitConverter.IsLittleEndian));
                        }
                        break;

                    case 'G': // 8-byte big-endian
                        count = CalculateCounts(length - position, directive.Count, sizeof(double), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(ReadDouble(self, ref position, BitConverter.IsLittleEndian));
                        }
                        break;

                    case 'i':
                    case 'l': // signed 4-byte, byte order from the directive
                        count = CalculateCounts(length - position, directive.Count, sizeof(int), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(unchecked((int)ReadUInt32(self, ref position, directive.IsBigEndian)));
                        }
                        break;

                    case 'I':
                    case 'L': // unsigned 4-byte, byte order from the directive
                        count = CalculateCounts(length - position, directive.Count, sizeof(uint), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(Protocols.Normalize(ReadUInt32(self, ref position, directive.IsBigEndian)));
                        }
                        break;

                    case 'N': // unsigned 4-byte big-endian
                        count = CalculateCounts(length - position, directive.Count, sizeof(uint), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(Protocols.Normalize(ReadUInt32(self, ref position, BitConverter.IsLittleEndian)));
                        }
                        break;

                    case 'n': // unsigned 2-byte big-endian
                        count = CalculateCounts(length - position, directive.Count, sizeof(ushort), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(ScriptingRuntimeHelpers.Int32ToObject(ReadUInt16(self, ref position, BitConverter.IsLittleEndian)));
                        }
                        break;

                    case 'v': // unsigned 2-byte little-endian
                        count = CalculateCounts(length - position, directive.Count, sizeof(ushort), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(ScriptingRuntimeHelpers.Int32ToObject(ReadUInt16(self, ref position, !BitConverter.IsLittleEndian)));
                        }
                        break;

                    case 'V': // unsigned 4-byte little-endian
                        count = CalculateCounts(length - position, directive.Count, sizeof(uint), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(Protocols.Normalize(ReadUInt32(self, ref position, !BitConverter.IsLittleEndian)));
                        }
                        break;

                    case 'q': // signed 8-byte, byte order from the directive
                        count = CalculateCounts(length - position, directive.Count, sizeof(long), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(Protocols.Normalize(unchecked((long)ReadUInt64(self, ref position, directive.IsBigEndian))));
                        }
                        break;

                    case 'Q': // unsigned 8-byte, byte order from the directive
                        count = CalculateCounts(length - position, directive.Count, sizeof(ulong), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(Protocols.Normalize(ReadUInt64(self, ref position, directive.IsBigEndian)));
                        }
                        break;

                    case 'm': // Base64
                        result.Add(directive.Count == 0
                            ? ReadStrictBase64(self, ref position)
                            : ReadBase64(self, ref position));
                        break;

                    case 'M': // quoted-printable, MIME encoding
                        result.Add(ReadQuotedPrintable(self, ref position));
                        break;

                    case 's': // signed 2-byte, byte order from the directive
                        count = CalculateCounts(length - position, directive.Count, sizeof(short), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(ScriptingRuntimeHelpers.Int32ToObject(unchecked((short)ReadUInt16(self, ref position, directive.IsBigEndian))));
                        }
                        break;

                    case 'S': // unsigned 2-byte, byte order from the directive
                        count = CalculateCounts(length - position, directive.Count, sizeof(ushort), out nilCount);
                        for (int j = 0; j < count; j++) {
                            result.Add(ScriptingRuntimeHelpers.Int32ToObject(ReadUInt16(self, ref position, directive.IsBigEndian)));
                        }
                        break;

                    case 'U':
                        count = directive.Count ?? Int32.MaxValue;                            
                        for (int i = 0; i < count; i++) {
                            int cp = ReadUtf8CodePoint(self, ref position);
                            if (cp == -1) {
                                break;
                            }
                            result.Add(cp);
                        }
                        break;

                    case 'w': // BER encoding
                        count = directive.Count ?? Int32.MaxValue;
                        for (int i = 0; i < count; i++) {
                            object value = ReadBer(self, ref position);

                            if (value == null) {
                                break;
                            }
                            result.Add(value);
                        }
                        break;

                    case 'u': // UU decoding                        
                        result.Add(ReadUU(self, ref position));
                        break;

                    case 'X':
                        int len3 = directive.Count.HasValue ? directive.Count.Value : (int)(length - position);
                        if (len3 > position) {
                            throw RubyExceptions.CreateArgumentError("X outside of string");
                        }
                        position -= len3;
                        break;

                    case 'x':
                        int newPos = directive.Count.HasValue ? (int)position + directive.Count.Value : (int)length;
                        if (newPos > length) {
                            throw RubyExceptions.CreateArgumentError("x outside of string");
                        }
                        position = newPos;
                        break;

                    case 'h':
                    case 'H':
                        maxCount = (int)(length - position) * 2;
                        result.Add(ToHex(self, ref position, Math.Min(directive.Count ?? maxCount, maxCount), directive.Directive == 'h'));
                        break;
                }
                for (int i = 0; i < nilCount; i++) {
                    result.Add(null);
                }
            }
            return result;
        }

        private static int CalculateCounts(int remaining, int? count, int size, out int leftover) {
            int maxCount = remaining / size;
            if (!count.HasValue) {
                leftover = 0;
                return maxCount;
            } else if (count.Value <= maxCount) {
                leftover = 0;
                return count.Value;
            } else {
                leftover = count.Value - maxCount;
                return maxCount;
            }
        }

        private static MutableString/*!*/ ToHex(MutableString/*!*/ data, ref int index, int nibbleCount, bool swap) {
            int wholeChars = nibbleCount / 2;
            // Hex digits only, and MRI hands them back as US-ASCII.
            MutableString hex = MutableString.CreateMutable(nibbleCount, RubyEncoding.Ascii);

            for (int i = 0; i < wholeChars; i++) {
                byte b = data.GetByte(index++);
                char loNibble = (b & 0x0F).ToLowerHexDigit();
                char hiNibble = ((b & 0xF0) >> 4).ToLowerHexDigit();

                if (swap) {
                    hex.Append(loNibble);
                    hex.Append(hiNibble);
                } else {
                    hex.Append(hiNibble);
                    hex.Append(loNibble);
                }
            }

            // the last nibble:
            if ((nibbleCount & 1) != 0) {
                int b = data.GetByte(index++);
                if (swap) {
                    hex.Append((b & 0x0F).ToLowerHexDigit());
                } else {
                    hex.Append(((b & 0xF0) >> 4).ToLowerHexDigit());
                }
            }

            return hex;
        }

        private static MutableString/*!*/ ReadString(MutableString/*!*/ data, int trimMode, int? count, ref int offset) {
            int start = offset;
            int e = data.GetByteCount();
            if (count.HasValue) {
                int end = start + count.Value;
                if (end < e) {
                    e = end;
                }
            }

            offset = e;
            byte b;
            switch (trimMode) {
                case 'A':
                    while (--e >= start && ((b = data.GetByte(e)) == 0 || b == ' ')) { }
                    e++;
                    break;

                case 'Z':
                    int i = start;
                    while (i < e && data.GetByte(i) != 0) { i++; }
                    if (!count.HasValue) {
                        offset = i + 1;
                    }
                    e = i;
                    break;
            }

            return data.GetSlice(start, e - start);
        }

        #endregion
    }
}

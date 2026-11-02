/* ****************************************************************************
 *
 * yajl 1.x as yajl-ruby carries it (ext/yajl: yajl_lex.c, yajl_parser.c, yajl.c,
 * yajl_encode.c, yajl_gen.c, yajl_buf.c), in C#.
 *
 * A function-by-function port: the streaming lexer with its token buffer for tokens
 * that straddle chunks, the parser's state stack, string unescaping (including yajl's
 * own reading of surrogate pairs), the generator with its 256-deep state array and
 * html_safe/entities escaping, and the verbose error string with its context window.
 * Errors are values here as they are in C; the Ruby binding decides what to raise.
 *
 * yajl is Copyright (c) 2007-2010, Lloyd Hilaiel, under the ISC license; yajl-ruby's
 * changes are Copyright (c) 2008-2011 Brian Lopez, under the MIT license.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Text;

namespace IronRuby.StandardLibrary.Yajl {

    /// <summary>yajl_buf: a growable byte buffer.</summary>
    internal sealed class YajlBuf {
        internal byte[]/*!*/ Data = new byte[64];
        internal int Length;

        internal void Append(byte[]/*!*/ src, int start, int count) {
            if (count <= 0) {
                return;
            }
            Ensure(count);
            Buffer.BlockCopy(src, start, Data, Length, count);
            Length += count;
        }

        internal void Append(byte b) {
            Ensure(1);
            Data[Length++] = b;
        }

        internal void Append(string/*!*/ ascii) {
            Ensure(ascii.Length);
            for (int i = 0; i < ascii.Length; i++) {
                Data[Length++] = (byte)ascii[i];
            }
        }

        private void Ensure(int count) {
            if (Length + count > Data.Length) {
                int size = Data.Length;
                while (Length + count > size) {
                    size *= 2;
                }
                Array.Resize(ref Data, size);
            }
        }

        internal void Clear() {
            Length = 0;
        }

        internal void Truncate(int length) {
            Length = length;
        }

        internal byte[]/*!*/ ToArray() {
            var result = new byte[Length];
            Buffer.BlockCopy(Data, 0, result, 0, Length);
            return result;
        }
    }

    internal enum YajlTok {
        Bool, Colon, Comma, Eof, Error, LeftBrace, LeftBracket, Null, RightBrace, RightBracket,
        Integer, Double, String, StringWithEscapes, Comment
    }

    internal enum YajlLexError {
        Ok = 0, StringInvalidUtf8, StringInvalidEscapedChar, StringInvalidJsonChar, StringInvalidHexChar,
        InvalidChar, InvalidString, MissingIntegerAfterDecimal, MissingIntegerAfterExponent,
        MissingIntegerAfterMinus, UnallowedComment, AllocFailed
    }

    /// <summary>yajl_lex.c</summary>
    internal sealed class YajlLexer {
        private YajlLexError _error;
        private readonly YajlBuf/*!*/ _buf = new YajlBuf();
        private int _bufOff;
        private bool _bufInUse;
        private readonly bool _allowComments;
        private readonly bool _validateUtf8;

        internal YajlLexer(bool allowComments, bool validateUtf8) {
            _allowComments = allowComments;
            _validateUtf8 = validateUtf8;
        }

        internal YajlLexError Error {
            get { return _error; }
        }

        /// <summary>yajl_lex_realloc</summary>
        internal void Reset() {
            _error = YajlLexError.Ok;
            _buf.Clear();
            _bufOff = 0;
            _bufInUse = false;
        }

        internal static string/*!*/ TokName(YajlTok tok) {
            switch (tok) {
                case YajlTok.Bool: return "bool";
                case YajlTok.Colon: return "colon";
                case YajlTok.Comma: return "comma";
                case YajlTok.Comment: return "comment";
                case YajlTok.Eof: return "eof";
                case YajlTok.Error: return "error";
                case YajlTok.LeftBrace: return "open_array";
                case YajlTok.LeftBracket: return "open_object";
                case YajlTok.Null: return "null";
                case YajlTok.Integer: return "integer";
                case YajlTok.Double: return "double";
                case YajlTok.RightBrace: return "close_array";
                case YajlTok.RightBracket: return "close_object";
                case YajlTok.String: return "string";
                case YajlTok.StringWithEscapes: return "string_with_escapes";
            }
            return "unknown";
        }

        private const int VEC = 1, IJC = 2, VHC = 4;
        private static readonly byte[] CharLookupTable = BuildTable();

        private static byte[] BuildTable() {
            var t = new byte[256];
            for (int i = 0; i < 0x20; i++) t[i] = IJC;
            t['"'] = VEC | IJC;
            t['/'] = VEC;
            for (int i = '0'; i <= '9'; i++) t[i] = VHC;
            for (int i = 'A'; i <= 'F'; i++) t[i] = VHC;
            t['\\'] = VEC | IJC;
            for (int i = 'a'; i <= 'f'; i++) t[i] = VHC;
            t['b'] = VEC | VHC;
            t['f'] = VEC | VHC;
            t['n'] = VEC;
            t['r'] = VEC;
            t['t'] = VEC;
            return t;
        }

        private byte ReadChar(byte[]/*!*/ txt, ref int off) {
            if (_bufInUse && _buf.Length != 0 && _bufOff < _buf.Length) {
                return _buf.Data[_bufOff++];
            }
            return txt[off++];
        }

        private void UnreadChar(ref int off) {
            if (off > 0) {
                off--;
            } else {
                _bufOff--;
            }
        }

        private YajlTok Utf8Char(byte[]/*!*/ txt, int len, ref int offset, byte curChar) {
            if (curChar <= 0x7f) {
                return YajlTok.String;
            } else if ((curChar >> 5) == 0x6) {
                if (offset >= len) return YajlTok.Eof;
                curChar = ReadChar(txt, ref offset);
                if ((curChar >> 6) == 0x2) return YajlTok.String;
            } else if ((curChar >> 4) == 0x0e) {
                if (offset >= len) return YajlTok.Eof;
                curChar = ReadChar(txt, ref offset);
                if ((curChar >> 6) == 0x2) {
                    if (offset >= len) return YajlTok.Eof;
                    curChar = ReadChar(txt, ref offset);
                    if ((curChar >> 6) == 0x2) return YajlTok.String;
                }
            } else if ((curChar >> 3) == 0x1e) {
                if (offset >= len) return YajlTok.Eof;
                curChar = ReadChar(txt, ref offset);
                if ((curChar >> 6) == 0x2) {
                    if (offset >= len) return YajlTok.Eof;
                    curChar = ReadChar(txt, ref offset);
                    if ((curChar >> 6) == 0x2) {
                        if (offset >= len) return YajlTok.Eof;
                        curChar = ReadChar(txt, ref offset);
                        if ((curChar >> 6) == 0x2) return YajlTok.String;
                    }
                }
            }
            return YajlTok.Error;
        }

        private YajlTok LexString(byte[]/*!*/ txt, int len, ref int offset) {
            YajlTok tok = YajlTok.Error;
            bool hasEscapes = false;

            for (;;) {
                if (offset >= len) { tok = YajlTok.Eof; break; }
                byte curChar = ReadChar(txt, ref offset);

                if (curChar == '"') {
                    tok = YajlTok.String;
                    break;
                } else if (curChar == '\\') {
                    hasEscapes = true;
                    if (offset >= len) { tok = YajlTok.Eof; break; }
                    curChar = ReadChar(txt, ref offset);
                    if (curChar == 'u') {
                        bool bad = false;
                        for (int i = 0; i < 4; i++) {
                            if (offset >= len) { tok = YajlTok.Eof; bad = true; break; }
                            curChar = ReadChar(txt, ref offset);
                            if ((CharLookupTable[curChar] & VHC) == 0) {
                                UnreadChar(ref offset);
                                _error = YajlLexError.StringInvalidHexChar;
                                bad = true;
                                break;
                            }
                        }
                        if (bad) break;
                    } else if ((CharLookupTable[curChar] & VEC) == 0) {
                        UnreadChar(ref offset);
                        _error = YajlLexError.StringInvalidEscapedChar;
                        break;
                    }
                } else if ((CharLookupTable[curChar] & IJC) != 0) {
                    UnreadChar(ref offset);
                    _error = YajlLexError.StringInvalidJsonChar;
                    break;
                } else if (_validateUtf8) {
                    YajlTok t = Utf8Char(txt, len, ref offset, curChar);
                    if (t == YajlTok.Eof) {
                        tok = YajlTok.Eof;
                        break;
                    } else if (t == YajlTok.Error) {
                        _error = YajlLexError.StringInvalidUtf8;
                        break;
                    }
                }
            }
            if (hasEscapes && tok == YajlTok.String) {
                tok = YajlTok.StringWithEscapes;
            }
            return tok;
        }

        private YajlTok LexNumber(byte[]/*!*/ txt, int len, ref int offset) {
            YajlTok tok = YajlTok.Integer;
            byte c;

            if (offset >= len) return YajlTok.Eof;
            c = ReadChar(txt, ref offset);

            if (c == '-') {
                if (offset >= len) return YajlTok.Eof;
                c = ReadChar(txt, ref offset);
            }

            if (c == '0') {
                if (offset >= len) return YajlTok.Eof;
                c = ReadChar(txt, ref offset);
            } else if (c >= '1' && c <= '9') {
                do {
                    if (offset >= len) return YajlTok.Eof;
                    c = ReadChar(txt, ref offset);
                } while (c >= '0' && c <= '9');
            } else {
                UnreadChar(ref offset);
                _error = YajlLexError.MissingIntegerAfterMinus;
                return YajlTok.Error;
            }

            if (c == '.') {
                int numRd = 0;
                if (offset >= len) return YajlTok.Eof;
                c = ReadChar(txt, ref offset);
                while (c >= '0' && c <= '9') {
                    numRd++;
                    if (offset >= len) return YajlTok.Eof;
                    c = ReadChar(txt, ref offset);
                }
                if (numRd == 0) {
                    UnreadChar(ref offset);
                    _error = YajlLexError.MissingIntegerAfterDecimal;
                    return YajlTok.Error;
                }
                tok = YajlTok.Double;
            }

            if (c == 'e' || c == 'E') {
                if (offset >= len) return YajlTok.Eof;
                c = ReadChar(txt, ref offset);
                if (c == '+' || c == '-') {
                    if (offset >= len) return YajlTok.Eof;
                    c = ReadChar(txt, ref offset);
                }
                if (c >= '0' && c <= '9') {
                    do {
                        if (offset >= len) return YajlTok.Eof;
                        c = ReadChar(txt, ref offset);
                    } while (c >= '0' && c <= '9');
                } else {
                    UnreadChar(ref offset);
                    _error = YajlLexError.MissingIntegerAfterExponent;
                    return YajlTok.Error;
                }
                tok = YajlTok.Double;
            }

            UnreadChar(ref offset);
            return tok;
        }

        private YajlTok LexComment(byte[]/*!*/ txt, int len, ref int offset) {
            YajlTok tok = YajlTok.Comment;
            byte c;

            if (offset >= len) return YajlTok.Eof;
            c = ReadChar(txt, ref offset);

            if (c == '/') {
                do {
                    if (offset >= len) return YajlTok.Eof;
                    c = ReadChar(txt, ref offset);
                } while (c != '\n');
            } else if (c == '*') {
                for (;;) {
                    if (offset >= len) return YajlTok.Eof;
                    c = ReadChar(txt, ref offset);
                    if (c == '*') {
                        if (offset >= len) return YajlTok.Eof;
                        c = ReadChar(txt, ref offset);
                        if (c == '/') {
                            break;
                        } else {
                            UnreadChar(ref offset);
                        }
                    }
                }
            } else {
                _error = YajlLexError.InvalidChar;
                tok = YajlTok.Error;
            }
            return tok;
        }

        private YajlTok LexWord(byte[]/*!*/ txt, int len, ref int offset, string/*!*/ want, YajlTok ok) {
            int w = 0;
            do {
                if (offset >= len) {
                    return YajlTok.Eof;
                }
                byte c = ReadChar(txt, ref offset);
                if (c != want[w]) {
                    UnreadChar(ref offset);
                    _error = YajlLexError.InvalidString;
                    return YajlTok.Error;
                }
            } while (++w < want.Length);
            return ok;
        }

        /// <summary>
        /// yajl_lex_lex.  outBuf/outStart/outLen describe the token's text (for strings without
        /// the quotes); outBuf is either jsonText or the lexer's own buffer.
        /// </summary>
        internal YajlTok Lex(byte[]/*!*/ jsonText, int jsonTextLen, ref int offset,
            out byte[] outBuf, out int outStart, out int outLen) {

            YajlTok tok = YajlTok.Error;
            int startOffset = offset;

            outBuf = null;
            outStart = 0;
            outLen = 0;

            for (;;) {
                if (offset >= jsonTextLen) {
                    tok = YajlTok.Eof;
                    goto lexed;
                }

                byte c = ReadChar(jsonText, ref offset);

                switch (c) {
                    case (byte)'{': tok = YajlTok.LeftBracket; goto lexed;
                    case (byte)'}': tok = YajlTok.RightBracket; goto lexed;
                    case (byte)'[': tok = YajlTok.LeftBrace; goto lexed;
                    case (byte)']': tok = YajlTok.RightBrace; goto lexed;
                    case (byte)',': tok = YajlTok.Comma; goto lexed;
                    case (byte)':': tok = YajlTok.Colon; goto lexed;
                    case (byte)'\t': case (byte)'\n': case (byte)'\v': case (byte)'\f': case (byte)'\r': case (byte)' ':
                        startOffset++;
                        break;
                    case (byte)'t':
                        tok = LexWord(jsonText, jsonTextLen, ref offset, "rue", YajlTok.Bool);
                        goto lexed;
                    case (byte)'f':
                        tok = LexWord(jsonText, jsonTextLen, ref offset, "alse", YajlTok.Bool);
                        goto lexed;
                    case (byte)'n':
                        tok = LexWord(jsonText, jsonTextLen, ref offset, "ull", YajlTok.Null);
                        goto lexed;
                    case (byte)'"':
                        tok = LexString(jsonText, jsonTextLen, ref offset);
                        goto lexed;
                    case (byte)'-':
                    case (byte)'0': case (byte)'1': case (byte)'2': case (byte)'3': case (byte)'4':
                    case (byte)'5': case (byte)'6': case (byte)'7': case (byte)'8': case (byte)'9':
                        UnreadChar(ref offset);
                        tok = LexNumber(jsonText, jsonTextLen, ref offset);
                        goto lexed;
                    case (byte)'/':
                        if (!_allowComments) {
                            UnreadChar(ref offset);
                            _error = YajlLexError.UnallowedComment;
                            tok = YajlTok.Error;
                            goto lexed;
                        }
                        tok = LexComment(jsonText, jsonTextLen, ref offset);
                        if (tok == YajlTok.Comment) {
                            tok = YajlTok.Error;
                            _buf.Clear();
                            _bufInUse = false;
                            startOffset = offset;
                            break;
                        }
                        goto lexed;
                    default:
                        _error = YajlLexError.InvalidChar;
                        tok = YajlTok.Error;
                        goto lexed;
                }
            }

        lexed:
            if (tok == YajlTok.Eof || _bufInUse) {
                if (!_bufInUse) _buf.Clear();
                _bufInUse = true;
                _buf.Append(jsonText, startOffset, offset - startOffset);
                _bufOff = 0;

                if (tok != YajlTok.Eof) {
                    outBuf = _buf.Data;
                    outStart = 0;
                    outLen = _buf.Length;
                    _bufInUse = false;
                }
            } else if (tok != YajlTok.Error) {
                outBuf = jsonText;
                outStart = startOffset;
                outLen = offset - startOffset;
            }

            if (tok == YajlTok.String || tok == YajlTok.StringWithEscapes) {
                outStart++;
                outLen -= 2;
            }
            return tok;
        }

        /// <summary>yajl_lex_peek</summary>
        internal YajlTok Peek(byte[]/*!*/ jsonText, int jsonTextLen, int offset) {
            int bufLen = _buf.Length;
            int bufOff = _bufOff;
            bool bufInUse = _bufInUse;
            byte[] outBuf;
            int outStart, outLen;

            YajlTok tok = Lex(jsonText, jsonTextLen, ref offset, out outBuf, out outStart, out outLen);
            if (tok == YajlTok.Eof) {
                return tok;
            }
            _bufOff = bufOff;
            _bufInUse = bufInUse;
            _buf.Truncate(bufLen);
            return tok;
        }

        internal static string/*!*/ ErrorToString(YajlLexError error) {
            switch (error) {
                case YajlLexError.Ok: return "ok, no error";
                case YajlLexError.StringInvalidUtf8: return "invalid bytes in UTF8 string.";
                case YajlLexError.StringInvalidEscapedChar: return "inside a string, '\\' occurs before a character which it may not.";
                case YajlLexError.StringInvalidJsonChar: return "invalid character inside string.";
                case YajlLexError.StringInvalidHexChar: return "invalid (non-hex) character occurs after '\\u' inside string.";
                case YajlLexError.InvalidChar: return "invalid char in json text.";
                case YajlLexError.InvalidString: return "invalid string in json text.";
                case YajlLexError.MissingIntegerAfterExponent: return "malformed number, a digit is required after the exponent.";
                case YajlLexError.MissingIntegerAfterDecimal: return "malformed number, a digit is required after the decimal point.";
                case YajlLexError.MissingIntegerAfterMinus: return "malformed number, a digit is required after the minus sign.";
                case YajlLexError.UnallowedComment: return "probable comment found in input text, comments are not enabled.";
                case YajlLexError.AllocFailed: return "allocation failed";
            }
            return "unknown error code";
        }
    }

    /// <summary>yajl_encode.c</summary>
    internal static class YajlEncode {
        private const string HexChars = "0123456789ABCDEF";

        private static byte At(byte[]/*!*/ str, int start, int len, int i) {
            // the C reads a NUL-terminated Ruby string: one past the end is NUL
            return i < len ? str[start + i] : (byte)0;
        }

        /// <summary>yajl_string_encode2</summary>
        internal static void Encode(YajlBuf/*!*/ output, byte[]/*!*/ str, int start, int len, int htmlSafe) {
            int beg = 0, end = 0;
            while (end < len) {
                int increment = 1;
                string escaped = null;
                byte c = str[start + end];
                switch (c) {
                    case (byte)'\r': escaped = "\\r"; break;
                    case (byte)'\n': escaped = "\\n"; break;
                    case (byte)'\\': escaped = "\\\\"; break;
                    case (byte)'"': escaped = "\\\""; break;
                    case (byte)'\f': escaped = "\\f"; break;
                    case (byte)'\b': escaped = "\\b"; break;
                    case (byte)'\t': escaped = "\\t"; break;
                    case (byte)'/':
                        if (htmlSafe == 1 || htmlSafe == 2) {
                            escaped = "\\/";
                        }
                        break;
                    case 0xe2:
                        if (htmlSafe == 2) {
                            if (len - end >= 2 && At(str, start, len, end + 1) == 0x80) {
                                byte third = At(str, start, len, end + 2);
                                if (third == 0xa8) {
                                    increment = 3;
                                    escaped = "\\u2028";
                                    break;
                                }
                                if (third == 0xa9) {
                                    increment = 3;
                                    escaped = "\\u2029";
                                    break;
                                }
                            }
                            // sic: falls through to the '<' case, which escapes the 0xE2 byte
                            escaped = "\\u00" + HexChars[c >> 4] + HexChars[c & 0x0F];
                        }
                        break;
                    case (byte)'<':
                    case (byte)'>':
                    case (byte)'&':
                        if (htmlSafe == 2) {
                            escaped = "\\u00" + HexChars[c >> 4] + HexChars[c & 0x0F];
                        }
                        break;
                    default:
                        if (c < 32) {
                            escaped = "\\u00" + HexChars[c >> 4] + HexChars[c & 0x0F];
                        }
                        break;
                }
                if (escaped != null) {
                    output.Append(str, start + beg, end - beg);
                    output.Append(escaped);
                    end += increment;
                    beg = end;
                } else {
                    ++end;
                }
            }
            output.Append(str, start + beg, end - beg);
        }

        private static uint HexToDigit(uint val, byte[]/*!*/ hex, int at) {
            for (int i = 0; i < 4; i++) {
                int c = hex[at + i];
                if (c >= 'A') c = (c & ~0x20) - 7;
                c -= '0';
                val = (val << 4) | (uint)(c & 0xff);
            }
            return val;
        }

        private static void Utf32ToUtf8(uint codepoint, YajlBuf/*!*/ output) {
            if (codepoint < 0x80) {
                output.Append((byte)codepoint);
            } else if (codepoint < 0x0800) {
                output.Append((byte)((codepoint >> 6) | 0xC0));
                output.Append((byte)((codepoint & 0x3F) | 0x80));
            } else if (codepoint < 0x10000) {
                output.Append((byte)((codepoint >> 12) | 0xE0));
                output.Append((byte)(((codepoint >> 6) & 0x3F) | 0x80));
                output.Append((byte)((codepoint & 0x3F) | 0x80));
            } else if (codepoint < 0x200000) {
                output.Append((byte)((codepoint >> 18) | 0xF0));
                output.Append((byte)(((codepoint >> 12) & 0x3F) | 0x80));
                output.Append((byte)(((codepoint >> 6) & 0x3F) | 0x80));
                output.Append((byte)((codepoint & 0x3F) | 0x80));
            } else {
                output.Append((byte)'?');
            }
        }

        /// <summary>yajl_string_decode</summary>
        internal static void Decode(YajlBuf/*!*/ output, byte[]/*!*/ str, int start, int len) {
            int beg = 0, end = 0;
            while (end < len) {
                if (str[start + end] == '\\') {
                    output.Append(str, start + beg, end - beg);
                    string unescaped = "?";
                    switch (str[start + (++end)]) {
                        case (byte)'r': unescaped = "\r"; break;
                        case (byte)'n': unescaped = "\n"; break;
                        case (byte)'\\': unescaped = "\\"; break;
                        case (byte)'/': unescaped = "/"; break;
                        case (byte)'"': unescaped = "\""; break;
                        case (byte)'f': unescaped = "\f"; break;
                        case (byte)'b': unescaped = "\b"; break;
                        case (byte)'t': unescaped = "\t"; break;
                        case (byte)'u': {
                                uint codepoint = HexToDigit(0, str, start + (++end));
                                end += 3;
                                if ((codepoint & 0xFC00) == 0xD800) {
                                    if (end + 2 < len && str[start + end + 1] == '\\' && str[start + end + 2] == 'u') {
                                        end++;
                                        uint surrogate = HexToDigit(0, str, start + end + 2);
                                        codepoint =
                                            (((codepoint & 0x3F) << 10) |
                                             ((((codepoint >> 6) & 0xF) + 1) << 16) |
                                             (surrogate & 0x3FF));
                                        end += 5;
                                    } else {
                                        unescaped = "?";
                                        break;
                                    }
                                }
                                Utf32ToUtf8(codepoint, output);
                                beg = ++end;
                                continue;
                            }
                    }
                    output.Append(unescaped);
                    beg = ++end;
                } else {
                    end++;
                }
            }
            output.Append(str, start + beg, end - beg);
        }
    }

    internal enum YajlStatus { Ok, ClientCanceled, InsufficientData, Error, AllocFailed }

    /// <summary>The callbacks yajl_ext.c registers (yajl_callbacks).</summary>
    internal interface IYajlCallbacks {
        void Null();
        void Boolean(bool value);
        void Number(byte[]/*!*/ buf, int start, int len);
        void String(byte[]/*!*/ buf, int start, int len);
        void StartMap();
        void MapKey(byte[]/*!*/ buf, int start, int len);
        void EndMap();
        void StartArray();
        void EndArray();
    }

    /// <summary>yajl_handle: yajl.c and yajl_parser.c</summary>
    internal sealed class YajlHandle {
        private enum State : byte {
            Start = 0, ParseComplete, ParseError, LexicalError, MapStart, MapSep, MapNeedVal, MapGotVal,
            MapNeedKey, ArrayStart, ArrayGotVal, ArrayNeedVal
        }

        private readonly IYajlCallbacks/*!*/ _callbacks;
        private readonly YajlLexer/*!*/ _lexer;
        private string _parseError;
        private int _bytesConsumed;
        private readonly YajlBuf/*!*/ _decodeBuf = new YajlBuf();
        private byte[]/*!*/ _stack = new byte[128];
        private int _used;

        internal YajlHandle(IYajlCallbacks/*!*/ callbacks, bool allowComments, bool checkUtf8) {
            _callbacks = callbacks;
            _lexer = new YajlLexer(allowComments, checkUtf8);
            Push(State.Start);
        }

        private State Current {
            get { return (State)_stack[_used - 1]; }
        }

        private void Set(State s) {
            _stack[_used - 1] = (byte)s;
        }

        private void Push(State s) {
            if (_used == _stack.Length) {
                Array.Resize(ref _stack, _stack.Length + 128);
            }
            _stack[_used++] = (byte)s;
        }

        private void Pop() {
            _used--;
        }

        internal static string/*!*/ StatusToString(YajlStatus stat) {
            switch (stat) {
                case YajlStatus.Ok: return "ok, no error";
                case YajlStatus.ClientCanceled: return "client canceled parse";
                case YajlStatus.InsufficientData: return "eof was met before the parse could complete";
                case YajlStatus.Error: return "parse error";
                case YajlStatus.AllocFailed: return "allocation failed";
            }
            return "unknown";
        }

        /// <summary>yajl_parse_complete: a space ends a number the lexer is still holding.</summary>
        internal YajlStatus ParseComplete() {
            return Parse(new byte[] { (byte)' ' }, 1);
        }

        /// <summary>yajl_do_parse</summary>
        internal YajlStatus Parse(byte[]/*!*/ jsonText, int jsonTextLen) {
            YajlTok tok;
            byte[] buf;
            int bufStart, bufLen;
            _bytesConsumed = 0;

        around_again:
            switch (Current) {
                case State.ParseComplete:
                    return YajlStatus.Ok;
                case State.LexicalError:
                case State.ParseError:
                    return YajlStatus.Error;
                case State.Start:
                case State.MapNeedVal:
                case State.ArrayNeedVal:
                case State.ArrayStart: {
                        State stateToPush = State.Start;

                        tok = _lexer.Lex(jsonText, jsonTextLen, ref _bytesConsumed, out buf, out bufStart, out bufLen);

                        switch (tok) {
                            case YajlTok.Eof:
                                return YajlStatus.InsufficientData;
                            case YajlTok.Error:
                                Set(State.LexicalError);
                                goto around_again;
                            case YajlTok.String:
                                _callbacks.String(buf, bufStart, bufLen);
                                break;
                            case YajlTok.StringWithEscapes:
                                _decodeBuf.Clear();
                                YajlEncode.Decode(_decodeBuf, buf, bufStart, bufLen);
                                _callbacks.String(_decodeBuf.Data, 0, _decodeBuf.Length);
                                break;
                            case YajlTok.Bool:
                                _callbacks.Boolean(buf[bufStart] == 't');
                                break;
                            case YajlTok.Null:
                                _callbacks.Null();
                                break;
                            case YajlTok.LeftBracket:
                                _callbacks.StartMap();
                                stateToPush = State.MapStart;
                                break;
                            case YajlTok.LeftBrace:
                                _callbacks.StartArray();
                                stateToPush = State.ArrayStart;
                                break;
                            case YajlTok.Integer:
                            case YajlTok.Double:
                                _callbacks.Number(buf, bufStart, bufLen);
                                break;
                            case YajlTok.RightBrace:
                                if (Current == State.ArrayStart) {
                                    _callbacks.EndArray();
                                    Pop();
                                    goto around_again;
                                }
                                goto case YajlTok.Colon;
                            case YajlTok.Colon:
                            case YajlTok.Comma:
                            case YajlTok.RightBracket:
                                Set(State.ParseError);
                                _parseError = "unallowed token at this point in JSON text";
                                goto around_again;
                            default:
                                Set(State.ParseError);
                                _parseError = "invalid token, internal error";
                                goto around_again;
                        }
                        {
                            State s = Current;
                            if (s == State.Start) {
                                _lexer.Reset();
                            } else if (s == State.MapNeedVal) {
                                Set(State.MapGotVal);
                            } else {
                                Set(State.ArrayGotVal);
                            }
                        }
                        if (stateToPush != State.Start) {
                            Push(stateToPush);
                        }
                        goto around_again;
                    }
                case State.MapStart:
                case State.MapNeedKey: {
                        tok = _lexer.Lex(jsonText, jsonTextLen, ref _bytesConsumed, out buf, out bufStart, out bufLen);
                        switch (tok) {
                            case YajlTok.Eof:
                                return YajlStatus.InsufficientData;
                            case YajlTok.Error:
                                Set(State.LexicalError);
                                goto around_again;
                            case YajlTok.StringWithEscapes:
                                _decodeBuf.Clear();
                                YajlEncode.Decode(_decodeBuf, buf, bufStart, bufLen);
                                buf = _decodeBuf.Data;
                                bufStart = 0;
                                bufLen = _decodeBuf.Length;
                                goto case YajlTok.String;
                            case YajlTok.String:
                                _callbacks.MapKey(buf, bufStart, bufLen);
                                Set(State.MapSep);
                                goto around_again;
                            case YajlTok.RightBracket:
                                if (Current == State.MapStart) {
                                    _callbacks.EndMap();
                                    Pop();
                                    goto around_again;
                                }
                                goto default;
                            default:
                                Set(State.ParseError);
                                _parseError = "invalid object key (must be a string)";
                                goto around_again;
                        }
                    }
                case State.MapSep: {
                        tok = _lexer.Lex(jsonText, jsonTextLen, ref _bytesConsumed, out buf, out bufStart, out bufLen);
                        switch (tok) {
                            case YajlTok.Colon:
                                Set(State.MapNeedVal);
                                goto around_again;
                            case YajlTok.Eof:
                                return YajlStatus.InsufficientData;
                            case YajlTok.Error:
                                Set(State.LexicalError);
                                goto around_again;
                            default:
                                Set(State.ParseError);
                                _parseError = "object key and value must be separated by a colon (':')";
                                goto around_again;
                        }
                    }
                case State.MapGotVal: {
                        tok = _lexer.Lex(jsonText, jsonTextLen, ref _bytesConsumed, out buf, out bufStart, out bufLen);
                        switch (tok) {
                            case YajlTok.RightBracket:
                                _callbacks.EndMap();
                                Pop();
                                goto around_again;
                            case YajlTok.Comma:
                                Set(State.MapNeedKey);
                                goto around_again;
                            case YajlTok.Eof:
                                return YajlStatus.InsufficientData;
                            case YajlTok.Error:
                                Set(State.LexicalError);
                                goto around_again;
                            default:
                                Set(State.ParseError);
                                _parseError = "after key and value, inside map, I expect ',' or '}'";
                                // try to restore error offset
                                if (_bytesConsumed >= bufLen) _bytesConsumed -= bufLen;
                                else _bytesConsumed = 0;
                                goto around_again;
                        }
                    }
                case State.ArrayGotVal: {
                        tok = _lexer.Lex(jsonText, jsonTextLen, ref _bytesConsumed, out buf, out bufStart, out bufLen);
                        switch (tok) {
                            case YajlTok.RightBrace:
                                _callbacks.EndArray();
                                Pop();
                                goto around_again;
                            case YajlTok.Comma:
                                Set(State.ArrayNeedVal);
                                goto around_again;
                            case YajlTok.Eof:
                                return YajlStatus.InsufficientData;
                            case YajlTok.Error:
                                Set(State.LexicalError);
                                goto around_again;
                            default:
                                Set(State.ParseError);
                                _parseError = "after array element, I expect ',' or ']'";
                                goto around_again;
                        }
                    }
            }
            throw new InvalidOperationException("yajl: unknown parser state");
        }

        /// <summary>
        /// yajl_render_error_string(hand, jsonText, jsonTextLen, verbose = 1), as the Ruby string
        /// rb_exc_new2 makes of it - which stops at the first NUL.
        /// </summary>
        internal byte[]/*!*/ RenderError(byte[]/*!*/ jsonText, int jsonTextLen) {
            int offset = _bytesConsumed;
            string errorType, errorText = null;

            if (Current == State.ParseError) {
                errorType = "parse";
                errorText = _parseError;
            } else if (Current == State.LexicalError) {
                errorType = "lexical";
                errorText = YajlLexer.ErrorToString(_lexer.Error);
            } else {
                errorType = "unknown";
            }

            var result = new YajlBuf();
            result.Append(errorType);
            result.Append(" error");
            if (errorText != null) {
                result.Append(": ");
                result.Append(errorText);
            }
            result.Append("\n");

            // the context: up to 30 bytes either side, the error at column 41
            int spacesNeeded = offset < 30 ? 40 - offset : 10;
            int start = offset >= 30 ? offset - 30 : 0;
            int end = offset + 30 > jsonTextLen ? jsonTextLen : offset + 30;
            var text = new YajlBuf();
            for (int i = 0; i < spacesNeeded; i++) {
                text.Append((byte)' ');
            }
            for (; start < end; start++) {
                byte c = jsonText[start];
                text.Append(c != '\n' && c != '\r' ? c : (byte)' ');
            }
            text.Append((byte)'\n');
            // strcat stops at a NUL in the context
            int textLen = Array.IndexOf(text.Data, (byte)0, 0, text.Length);
            result.Append(text.Data, 0, textLen < 0 ? text.Length : textLen);
            result.Append("                     (right here) ------^\n");
            return result.ToArray();
        }
    }

    internal enum YajlGenStatus {
        Ok = 0, KeysMustBeStrings, MaxDepthExceeded, InErrorState, GenerationComplete, InvalidNumber,
        NoBuf, DepthUnderflow, AllocError
    }

    /// <summary>yajl_gen.c</summary>
    internal sealed class YajlGen {
        internal const int MaxDepth = 256;   // YAJL_MAX_DEPTH

        private enum GenState { Start, MapStart, MapKey, MapVal, ArrayStart, InArray, Complete, Error }

        private uint _depth;
        private readonly bool _pretty;
        private readonly byte[]/*!*/ _indent;
        // one slot more than the C's array: INCREMENT_DEPTH writes nothing at 256, but keeps us in bounds
        private readonly GenState[]/*!*/ _state = new GenState[MaxDepth + 1];
        private readonly int _htmlSafe;
        internal readonly YajlBuf/*!*/ Buf = new YajlBuf();

        internal YajlGen(bool beautify, byte[] indentString, int htmlSafe) {
            _pretty = beautify;
            byte[] indent = indentString;
            if (indent != null) {
                foreach (byte b in indent) {
                    if (b == 0) {
                        break;
                    }
                    if (b != '\n' && b != '\v' && b != '\f' && b != '\t' && b != '\r' && b != ' ') {
                        indent = null;
                        break;
                    }
                }
            }
            if (indent == null) {
                indent = new byte[] { (byte)' ', (byte)' ' };
            } else {
                int nul = Array.IndexOf(indent, (byte)0);
                if (nul >= 0) {
                    Array.Resize(ref indent, nul);
                }
            }
            _indent = indent;
            _htmlSafe = htmlSafe;
        }

        private GenState Cur {
            get { return _state[Math.Min(_depth, (uint)MaxDepth)]; }
            set { _state[Math.Min(_depth, (uint)MaxDepth)] = value; }
        }

        private void InsertSep() {
            if (Cur == GenState.MapKey || Cur == GenState.InArray) {
                Buf.Append((byte)',');
                if (_pretty) Buf.Append((byte)'\n');
            } else if (Cur == GenState.MapVal) {
                Buf.Append((byte)':');
                if (_pretty) Buf.Append((byte)' ');
            }
        }

        private void InsertWhitespace() {
            if (_pretty) {
                if (Cur != GenState.MapVal) {
                    for (uint i = 0; i < _depth; i++) {
                        Buf.Append(_indent, 0, _indent.Length);
                    }
                }
            }
        }

        private bool IsKeyPosition {
            get { return Cur == GenState.MapKey || Cur == GenState.MapStart; }
        }

        private YajlGenStatus InvalidState() {
            if (Cur == GenState.Error) return YajlGenStatus.InErrorState;
            if (Cur == GenState.Complete) return YajlGenStatus.GenerationComplete;
            return YajlGenStatus.Ok;
        }

        private void AppendedAtom() {
            switch (Cur) {
                case GenState.MapStart:
                case GenState.MapKey:
                    Cur = GenState.MapVal;
                    break;
                case GenState.ArrayStart:
                    Cur = GenState.InArray;
                    break;
                case GenState.MapVal:
                    Cur = GenState.MapKey;
                    break;
            }
        }

        /// <summary>yajl_gen_long</summary>
        internal YajlGenStatus Long(long val) {
            var s = InvalidState(); if (s != YajlGenStatus.Ok) return s;
            if (IsKeyPosition) return YajlGenStatus.KeysMustBeStrings;
            InsertSep(); InsertWhitespace();
            if (val < 0) {
                Buf.Append((byte)'-');
                ulong uval = 1 + (ulong)(-(val + 1));
                Buf.Append(uval.ToString(System.Globalization.CultureInfo.InvariantCulture));
            } else {
                Buf.Append(val.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AppendedAtom();
            return YajlGenStatus.Ok;
        }

        /// <summary>yajl_gen_number: the text as given</summary>
        internal YajlGenStatus Number(byte[]/*!*/ s, int start, int len) {
            var st = InvalidState(); if (st != YajlGenStatus.Ok) return st;
            if (IsKeyPosition) return YajlGenStatus.KeysMustBeStrings;
            InsertSep(); InsertWhitespace();
            Buf.Append(s, start, len);
            AppendedAtom();
            return YajlGenStatus.Ok;
        }

        internal YajlGenStatus String(byte[]/*!*/ str, int start, int len) {
            var st = InvalidState(); if (st != YajlGenStatus.Ok) return st;
            InsertSep(); InsertWhitespace();
            Buf.Append((byte)'"');
            YajlEncode.Encode(Buf, str, start, len, _htmlSafe);
            Buf.Append((byte)'"');
            AppendedAtom();
            return YajlGenStatus.Ok;
        }

        internal YajlGenStatus Null() {
            var st = InvalidState(); if (st != YajlGenStatus.Ok) return st;
            if (IsKeyPosition) return YajlGenStatus.KeysMustBeStrings;
            InsertSep(); InsertWhitespace();
            Buf.Append("null");
            AppendedAtom();
            return YajlGenStatus.Ok;
        }

        internal YajlGenStatus Bool(bool value) {
            var st = InvalidState(); if (st != YajlGenStatus.Ok) return st;
            if (IsKeyPosition) return YajlGenStatus.KeysMustBeStrings;
            InsertSep(); InsertWhitespace();
            Buf.Append(value ? "true" : "false");
            AppendedAtom();
            return YajlGenStatus.Ok;
        }

        private YajlGenStatus Open(GenState state, byte bracket) {
            var st = InvalidState(); if (st != YajlGenStatus.Ok) return st;
            if (IsKeyPosition) return YajlGenStatus.KeysMustBeStrings;
            InsertSep(); InsertWhitespace();
            if (++_depth >= MaxDepth) return YajlGenStatus.MaxDepthExceeded;
            Cur = state;
            Buf.Append(bracket);
            if (_pretty) Buf.Append((byte)'\n');
            return YajlGenStatus.Ok;
        }

        private YajlGenStatus Close(byte bracket) {
            var st = InvalidState(); if (st != YajlGenStatus.Ok) return st;
            if (--_depth >= MaxDepth) return YajlGenStatus.DepthUnderflow;
            if (_pretty) Buf.Append((byte)'\n');
            AppendedAtom();
            InsertWhitespace();
            Buf.Append(bracket);
            return YajlGenStatus.Ok;
        }

        internal YajlGenStatus MapOpen() { return Open(GenState.MapStart, (byte)'{'); }
        internal YajlGenStatus MapClose() { return Close((byte)'}'); }
        internal YajlGenStatus ArrayOpen() { return Open(GenState.ArrayStart, (byte)'['); }
        internal YajlGenStatus ArrayClose() { return Close((byte)']'); }
    }
}

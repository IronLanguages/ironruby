/* ****************************************************************************
 *
 * IronRuby's implementation of the oj gem's C extension: dumping.
 *
 * A port of dump.c, dump_strict.c, dump_compat.c, code.c and the dump half of
 * rails.c (oj 3.17.6) for the :strict, :null, :compat and :rails modes.  The output
 * buffer is bytes, as upstream's is, and each writer appends what the C appends in
 * the order it does, down to the quirks - an empty Array or Hash nested past
 * :max_nesting, the "Too deeply nested" that a shared (not circular) reference gets
 * under :circular - because "the same JSON as CRuby's oj" is the point.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using IronRuby.Builtins;
using Range = IronRuby.Builtins.Range;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Oj {

    /// <summary>rb_type, as far as the dumpers tell types apart.</summary>
    internal enum RType {
        None, Object, Class, Module, Float, String, Regexp, Array, Hash, Struct, Bignum, File, Data, Match,
        Complex, Rational, Nil, True, False, Symbol, Fixnum,
    }

    /// <summary>struct _out.</summary>
    internal sealed class Out {
        internal byte[]/*!*/ Buf = new byte[4096];
        internal int Cur;
        internal Options/*!*/ Opts;
        internal int Indent;
        internal int Depth;
        internal bool OmitNil;
        internal bool OmitNullByte;
        internal bool KeyFilterOff;
        internal object[]/*!*/ Argv = new object[0];
        internal Dictionary<object, long> CircCache;
        internal long CircCnt;
        internal RailsOpts Ropts;
        internal readonly RubyContext/*!*/ Context;
        internal readonly OjState/*!*/ State;

        internal Out(RubyContext/*!*/ context, Options/*!*/ opts) {
            Context = context;
            State = OjState.Get(context);
            Opts = opts;
        }

        internal int Argc { get { return Argv.Length; } }

        private void Grow(int more) {
            int size = Math.Max(Buf.Length * 2, Cur + more + 64);
            Array.Resize(ref Buf, size);
        }

        internal void Add(byte b) {
            if (Cur >= Buf.Length) {
                Grow(1);
            }
            Buf[Cur++] = b;
        }

        internal void Add(char c) {
            Add((byte)c);
        }

        internal void Add(byte[]/*!*/ bytes) {
            Add(bytes, 0, bytes.Length);
        }

        internal void Add(byte[]/*!*/ bytes, int offset, int count) {
            if (Cur + count > Buf.Length) {
                Grow(count);
            }
            Buffer.BlockCopy(bytes, offset, Buf, Cur, count);
            Cur += count;
        }

        internal void Add(string/*!*/ ascii) {
            if (Cur + ascii.Length > Buf.Length) {
                Grow(ascii.Length);
            }
            for (int i = 0; i < ascii.Length; i++) {
                Buf[Cur++] = (byte)ascii[i];
            }
        }

        internal byte Last { get { return Cur > 0 ? Buf[Cur - 1] : (byte)0; } }

        /// <summary>fill_indent.</summary>
        internal void FillIndent(int cnt) {
            if (0 < Indent) {
                cnt *= Indent;
                Add((byte)'\n');
                if (Cur + cnt > Buf.Length) {
                    Grow(cnt);
                }
                for (int i = 0; i < cnt; i++) {
                    Buf[Cur++] = (byte)' ';
                }
            }
        }

        /// <summary>The dump_opts newline and indent_str repeated depth times.</summary>
        internal void AddNlIndent(byte[]/*!*/ nl, int depth) {
            if (0 < nl.Length) {
                Add(nl);
            }
            if (0 < Opts.IndentStr.Length) {
                for (int i = depth; 0 < i; i--) {
                    Add(Opts.IndentStr);
                }
            }
        }

        internal void AddSeparator() {
            if (0 < Opts.BeforeSep.Length) {
                Add(Opts.BeforeSep);
            }
            Add((byte)':');
            if (0 < Opts.AfterSep.Length) {
                Add(Opts.AfterSep);
            }
        }

        /// <summary>rb_utf8_str_new_cstr(out.buf): stops at the first NUL.</summary>
        internal MutableString/*!*/ ToUtf8CString() {
            int len = Array.IndexOf(Buf, (byte)0, 0, Cur);
            if (len < 0) {
                len = Cur;
            }
            return MutableString.CreateBinary(len, RubyEncoding.UTF8).Append(Buf, 0, len);
        }
    }

    internal static class OjDump {
        internal const int MaxDepth = 1000;

        private static readonly byte[] InfVal = Encoding.ASCII.GetBytes("3.0e14159265358979323846");
        private static readonly byte[] NinfVal = Encoding.ASCII.GetBytes("-3.0e14159265358979323846");
        private static readonly byte[] NanVal = Encoding.ASCII.GetBytes("3.3e14159265358979323846");

        private const string HexChars = "0123456789abcdef";

        #region the escape tables

        private static byte[]/*!*/ Table(string/*!*/ s) {
            var t = new byte[256];
            for (int i = 0; i < 256; i++) {
                t[i] = (byte)s[i];
            }
            return t;
        }

        // JSON standard except newlines are not escaped
        private static readonly byte[] NewlineFriendly = Table(
            "66666666221622666666666666666666" +
            "11211111111111111111111111111111" +
            "11111111111111111111111111112111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111");

        // JSON standard
        private static readonly byte[] HibitFriendly = Table(
            "66666666222622666666666666666666" +
            "11211111111111111111111111111111" +
            "11111111111111111111111111112111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111");

        // JSON standard but escape forward slashes
        private static readonly byte[] SlashFriendly = Table(
            "66666666222622666666666666666666" +
            "11211111111111121111111111111111" +
            "11111111111111111111111111112111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111");

        // high bit set characters are always encoded as unicode
        private static readonly byte[] AsciiFriendly = Table(
            "66666666222622666666666666666666" +
            "11211111111111111111111111111111" +
            "11111111111111111111111111112111" +
            "11111111111111111111111111111116" +
            "33333333333333333333333333333333" +
            "33333333333333333333333333333333" +
            "33333333333333333333333333333333" +
            "33333333333333333333333333333333");

        // XSS safe mode
        private static readonly byte[] XssFriendly = Table(
            "66666666222622666666666666666666" +
            "11211161111111121111111111116161" +
            "11111111111111111111111111112111" +
            "11111111111111111111111111111116" +
            "33333333333333333333333333333333" +
            "33333333333333333333333333333333" +
            "33333333333333333333333333333333" +
            "33333333333333333333333333333333");

        // JSON XSS combo
        private static readonly byte[] HixssFriendly = Table(
            "66666666222622666666666666666666" +
            "11211111111111111111111111111111" +
            "11111111111111111111111111112111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11611111111111111111111111111111");

        // Rails XSS combo
        private static readonly byte[] RailsXssFriendly = Table(
            "66666666222622666666666666666666" +
            "11211161111111111111111111116161" +
            "11111111111111111111111111112111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11611111111111111111111111111111");

        // Rails HTML non-escape
        private static readonly byte[] RailsFriendly = Table(
            "66666666222622666666666666666666" +
            "11211111111111111111111111111111" +
            "11111111111111111111111111112111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111" +
            "11111111111111111111111111111111");

        private static long TableSize(byte[]/*!*/ str, int start, int len, byte[]/*!*/ table) {
            long size = 0;
            for (int i = start; i < start + len; i++) {
                size += table[str[i]];
            }
            return size - (long)len * '0';
        }

        #endregion

        #region errors

        internal static Exception/*!*/ RaiseStrict(RubyContext/*!*/ context, object obj, bool newline) {
            return RubyExceptions.CreateTypeError(String.Format("Failed to dump {0} Object to JSON in strict mode.{1}",
                context.GetClassOf(obj).Name, newline ? "\n" : ""));
        }

        private static Exception/*!*/ GeneratorError(Out/*!*/ output, string/*!*/ message) {
            return output.State.NewException(output.State.JsonGeneratorErrorClass(output.Context), message);
        }

        private static Exception/*!*/ InvalidUnicode(Out/*!*/ output, byte[]/*!*/ str, int start, int end, int pos) {
            var code = new StringBuilder("[");
            for (int i = pos; i < end && i - pos < 5; i++) {
                byte c = str[i];
                code.Append(HexChars[(c >> 4) & 0x0F]).Append(HexChars[c & 0x0F]).Append(' ');
            }
            code.Length--;
            code.Append(']');
            return GeneratorError(output, String.Format("Invalid Unicode {0} at {1}", code, pos - start));
        }

        private static Exception/*!*/ DebugRaise(Out/*!*/ output, byte[]/*!*/ str, int start, int cnt, int line) {
            var buf = new StringBuilder();
            int end = start + Math.Min(cnt, 32);
            for (int s = start; s < end; s++) {
                // %02x of a (signed) char
                sbyte b = (sbyte)str[s];
                buf.Append(' ').Append(b < 0 ? ((uint)(int)b).ToString("x8") : b.ToString("x2"));
            }
            return GeneratorError(output, String.Format("Partial character in string. {0} @ {1}", buf, line));
        }

        #endregion

        #region strings

        private static void DumpHex(Out/*!*/ output, byte c) {
            output.Add((byte)HexChars[(c >> 4) & 0x0F]);
            output.Add((byte)HexChars[c & 0x0F]);
        }

        /// <summary>dump_unicode: writes the UTF-8 sequence at str as \u escapes, answers its last byte.</summary>
        private static int DumpUnicode(Out/*!*/ output, byte[]/*!*/ s, int str, int end, int orig) {
            uint code;
            byte b = s[str];
            int cnt;
            if (0xC0 == (0xE0 & b)) {
                cnt = 1;
                code = (uint)(b & 0x1F);
            } else if (0xE0 == (0xF0 & b)) {
                cnt = 2;
                code = (uint)(b & 0x0F);
            } else if (0xF0 == (0xF8 & b)) {
                cnt = 3;
                code = (uint)(b & 0x07);
            } else if (0xF8 == (0xFC & b)) {
                cnt = 4;
                code = (uint)(b & 0x03);
            } else if (0xFC == (0xFE & b)) {
                cnt = 5;
                code = (uint)(b & 0x01);
            } else {
                throw InvalidUnicode(output, s, orig, end, str);
            }
            str++;
            for (; 0 < cnt; cnt--, str++) {
                if (end <= str || 0x80 != (0xC0 & s[str])) {
                    throw InvalidUnicode(output, s, orig, end, str);
                }
                code = (code << 6) | (uint)(s[str] & 0x3F);
            }
            if (0xFFFF < code) {
                code -= 0x10000;
                uint c1 = ((code >> 10) & 0x3FF) + 0xD800;
                code = (code & 0x3FF) + 0xDC00;
                output.Add("\\u");
                for (int i = 3; 0 <= i; i--) {
                    output.Add((byte)HexChars[(int)(c1 >> (i * 4)) & 0x0F]);
                }
            }
            output.Add("\\u");
            for (int i = 3; 0 <= i; i--) {
                output.Add((byte)HexChars[(int)(code >> (i * 4)) & 0x0F]);
            }
            return str - 1;
        }

        /// <summary>check_unicode: answers the index after a valid sequence.</summary>
        private static int CheckUnicode(Out/*!*/ output, byte[]/*!*/ s, int str, int end, int orig) {
            byte b = s[str];
            int cnt;
            if (0xC0 == (0xE0 & b)) {
                cnt = 1;
            } else if (0xE0 == (0xF0 & b)) {
                cnt = 2;
            } else if (0xF0 == (0xF8 & b)) {
                cnt = 3;
            } else if (0xF8 == (0xFC & b)) {
                cnt = 4;
            } else if (0xFC == (0xFE & b)) {
                cnt = 5;
            } else {
                throw InvalidUnicode(output, s, orig, end, str);
            }
            str++;
            for (; 0 < cnt; cnt--, str++) {
                if (end <= str || 0x80 != (0xC0 & s[str])) {
                    throw InvalidUnicode(output, s, orig, end, str);
                }
            }
            return str;
        }

        private static int ProcessCharacter(Out/*!*/ output, byte action, byte[]/*!*/ s, int str, int end, int orig,
            bool doUnicodeValidation, ref int checkStart) {

            switch (action) {
                case (byte)'1':
                    if (doUnicodeValidation && checkStart <= str) {
                        if (0 != (0x80 & s[str])) {
                            if (0xC0 == (0xC0 & s[str])) {
                                checkStart = CheckUnicode(output, s, str, end, orig);
                            } else {
                                throw InvalidUnicode(output, s, orig, end, str);
                            }
                        }
                    }
                    output.Add(s[str]);
                    break;
                case (byte)'2':
                    output.Add((byte)'\\');
                    switch (s[str]) {
                        case (byte)'\\': output.Add((byte)'\\'); break;
                        case (byte)'\b': output.Add((byte)'b'); break;
                        case (byte)'\t': output.Add((byte)'t'); break;
                        case (byte)'\n': output.Add((byte)'n'); break;
                        case (byte)'\f': output.Add((byte)'f'); break;
                        case (byte)'\r': output.Add((byte)'r'); break;
                        default: output.Add(s[str]); break;
                    }
                    break;
                case (byte)'3':
                    if (0xE2 == s[str] && doUnicodeValidation && 2 <= end - str) {
                        if (0x80 == s[str + 1] && (0xA8 == s[str + 2] || 0xA9 == s[str + 2])) {
                            str = DumpUnicode(output, s, str, end, orig);
                        } else {
                            checkStart = CheckUnicode(output, s, str, end, orig);
                            output.Add(s[str]);
                        }
                        break;
                    }
                    str = DumpUnicode(output, s, str, end, orig);
                    break;
                case (byte)'6':
                    if (s[str] < 0x80) {
                        if (0 == s[str] && output.Opts.OmitNullByte) {
                            break;
                        }
                        output.Add("\\u00");
                        DumpHex(output, s[str]);
                    } else {
                        if (0xE2 == s[str] && doUnicodeValidation && 2 <= end - str) {
                            if (0x80 == s[str + 1] && (0xA8 == s[str + 2] || 0xA9 == s[str + 2])) {
                                str = DumpUnicode(output, s, str, end, orig);
                            } else {
                                checkStart = CheckUnicode(output, s, str, end, orig);
                                output.Add(s[str]);
                            }
                            break;
                        }
                        str = DumpUnicode(output, s, str, end, orig);
                    }
                    break;
            }
            return str;
        }

        /// <summary>oj_dump_cstr.</summary>
        internal static void DumpCstr(Out/*!*/ output, byte[]/*!*/ s, int start, int cnt) {
            byte[] cmap;
            long size;
            bool hasHi = false;
            bool doUnicodeValidation = false;

            switch (output.Opts.EscapeMode) {
                case Esc.NL:
                    cmap = NewlineFriendly;
                    size = TableSize(s, start, cnt, cmap);
                    break;
                case Esc.ASCII:
                    cmap = AsciiFriendly;
                    size = TableSize(s, start, cnt, cmap);
                    break;
                case Esc.Slash:
                    hasHi = true;
                    cmap = SlashFriendly;
                    size = TableSize(s, start, cnt, cmap);
                    break;
                case Esc.XSS:
                    cmap = XssFriendly;
                    size = TableSize(s, start, cnt, cmap);
                    break;
                case Esc.JX: {
                        cmap = HixssFriendly;
                        size = TableSize(s, start, cnt, cmap);
                        for (int i = start; i < start + cnt; i++) {
                            if (0 != (0x80 & s[i])) {
                                size++;
                                break;
                            }
                        }
                        doUnicodeValidation = true;
                        break;
                    }
                case Esc.RailsX:
                case Esc.Rails: {
                        cmap = output.Opts.EscapeMode == Esc.RailsX ? RailsXssFriendly : RailsFriendly;
                        size = TableSize(s, start, cnt, cmap);
                        for (int i = start; i < start + cnt; i++) {
                            if (0 != (0x80 & s[i])) {
                                hasHi = true;
                                break;
                            }
                        }
                        doUnicodeValidation = true;
                        break;
                    }
                default:
                    cmap = HibitFriendly;
                    size = TableSize(s, start, cnt, cmap);
                    break;
            }

            output.Add((byte)'"');
            int str = start;
            int end = start + cnt;
            if (cnt == size && !hasHi) {
                output.Add(s, start, cnt);
                output.Add((byte)'"');
                str = end;
            } else {
                int checkStart = str;
                for (; str < end; str++) {
                    str = ProcessCharacter(output, cmap[s[str]], s, str, end, start, doUnicodeValidation, ref checkStart);
                }
                output.Add((byte)'"');
            }

            if (doUnicodeValidation && 0 < str - start && 0 != (0x80 & s[str - 1])) {
                byte c = s[str - 1];
                int scnt = str - start;
                int i;

                // the last UTF-8 byte must be 10xxxxxx and its lead byte must say how many follow
                if (0 != (0x40 & c)) {
                    throw DebugRaise(output, s, start, cnt, 1406);
                }
                for (i = 1; i < scnt && i < 4; i++) {
                    c = s[str - 1 - i];
                    if (0x80 != (0xC0 & c)) {
                        switch (i) {
                            case 1:
                                if (0xC0 != (0xE0 & c)) {
                                    throw DebugRaise(output, s, start, cnt, 1414);
                                }
                                break;
                            case 2:
                                if (0xE0 != (0xF0 & c)) {
                                    throw DebugRaise(output, s, start, cnt, 1419);
                                }
                                break;
                            case 3:
                                if (0xF0 != (0xF8 & c)) {
                                    throw DebugRaise(output, s, start, cnt, 1424);
                                }
                                break;
                        }
                        break;
                    }
                }
                if (i == scnt || 4 <= i) {
                    throw DebugRaise(output, s, start, cnt, 1434);
                }
            }
        }

        internal static void DumpCstr(Out/*!*/ output, byte[]/*!*/ s) {
            DumpCstr(output, s, 0, s.Length);
        }

        internal static void DumpCstr(Out/*!*/ output, string/*!*/ s) {
            DumpCstr(output, Encoding.UTF8.GetBytes(s));
        }

        /// <summary>The bytes oj_dump_str writes: UTF-8, converted when the String can be.</summary>
        internal static byte[]/*!*/ Utf8Bytes(RubyContext/*!*/ context, MutableString/*!*/ str) {
            if (str.Encoding != RubyEncoding.UTF8) {
                // rb_str_conv_enc answers the String unchanged when it cannot be converted
                try {
                    if (!(str.Encoding.IsAsciiIdentity && str.IsAscii())) {
                        var converted = Sites.Call(context, str, "encode", RubyEncoding.UTF8) as MutableString;
                        if (converted != null) {
                            return converted.ToByteArray();
                        }
                    }
                } catch (Exception) {
                    // fall through to the raw bytes
                }
            }
            return str.ToByteArray();
        }

        internal static void DumpStr(Out/*!*/ output, MutableString/*!*/ str) {
            DumpCstr(output, Utf8Bytes(output.Context, str));
        }

        internal static void DumpSym(Out/*!*/ output, RubySymbol/*!*/ sym) {
            DumpCstr(output, sym.String.ToByteArray());
        }

        /// <summary>oj_dump_class: rb_class2name.</summary>
        internal static void DumpClass(Out/*!*/ output, RubyModule/*!*/ module) {
            DumpCstr(output, ClassName(output.Context, module));
        }

        internal static string/*!*/ ClassName(RubyContext/*!*/ context, RubyModule/*!*/ module) {
            return module.Name ?? context.Inspect(module).ToString();
        }

        /// <summary>oj_safe_string_convert.</summary>
        internal static MutableString/*!*/ SafeToS(RubyContext/*!*/ context, object obj) {
            object r = Sites.Call(context, obj, "to_s");
            return Sites.StringValue(context, r);
        }

        internal static void DumpObjToS(Out/*!*/ output, object obj) {
            DumpCstr(output, SafeToS(output.Context, obj).ToByteArray());
        }

        internal static void DumpRaw(Out/*!*/ output, byte[]/*!*/ bytes) {
            output.Add(bytes);
        }

        internal static void DumpRawJson(Out/*!*/ output, object obj, int depth) {
            object jv = Sites.Call(output.Context, obj, "raw_json", ScriptingRuntimeHelpers.Int32ToObject(depth),
                ScriptingRuntimeHelpers.Int32ToObject(output.Indent));
            DumpRaw(output, Sites.StringValue(output.Context, jv).ToByteArray());
        }

        #endregion

        #region numbers

        internal static void DumpNil(Out/*!*/ output) {
            output.Add("null");
        }

        internal static void DumpTrue(Out/*!*/ output) {
            output.Add("true");
        }

        internal static void DumpFalse(Out/*!*/ output) {
            output.Add("false");
        }

        /// <summary>oj_dump_fixnum.</summary>
        internal static void DumpFixnum(Out/*!*/ output, long num) {
            bool dumpAsString = output.Opts.IntRangeMax != 0 && output.Opts.IntRangeMin != 0 &&
                (output.Opts.IntRangeMax < num || output.Opts.IntRangeMin > num);
            if (dumpAsString) {
                output.Add((byte)'"');
            }
            output.Add(num.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (dumpAsString) {
                output.Add((byte)'"');
            }
        }

        /// <summary>oj_dump_bignum.</summary>
        internal static void DumpBignum(Out/*!*/ output, BigInteger num) {
            bool dumpAsString = output.Opts.IntRangeMax != 0 || output.Opts.IntRangeMin != 0;
            if (dumpAsString) {
                output.Add((byte)'"');
            }
            output.Add(num.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (dumpAsString) {
                output.Add((byte)'"');
            }
        }

        /// <summary>d == (double)(long long int)d, with the x86-64 conversion of an out of range double.</summary>
        internal static bool IsLongLong(double d) {
            if (d >= -9.2233720368547758e18 && d < 9.2233720368547758e18) {
                return (double)(long)d == d;
            }
            return d == -9.2233720368547758e18;
        }

        /// <summary>snprintf("%.1f") of such a double.</summary>
        internal static string/*!*/ DotOne(double d) {
            return CFormat.Format("%.1f", d);
        }

        /// <summary>oj_dump_float_printf.</summary>
        internal static string/*!*/ FloatPrintf(RubyContext/*!*/ context, object obj, double d, string/*!*/ format) {
            string s = CFormat.Format(format, d);
            if (s.Length >= 64) {
                s = s.Substring(0, 63);
            }
            if (17 <= s.Length && (s.EndsWith("0001", StringComparison.Ordinal) || s.EndsWith("9999", StringComparison.Ordinal))) {
                s = SafeToS(context, obj).ToString();
            }
            return s;
        }

        internal static double ToDouble(RubyContext/*!*/ context, object obj) {
            if (obj is double) {
                return (double)obj;
            }
            return Sites.ToDouble(context, obj);
        }

        #endregion

        #region type classification

        internal static RType TypeOf(RubyContext/*!*/ context, object obj) {
            if (obj == null) {
                return RType.Nil;
            }
            if (obj is bool) {
                return (bool)obj ? RType.True : RType.False;
            }
            if (obj is int) {
                return RType.Fixnum;
            }
            if (obj is double) {
                return RType.Float;
            }
            if (obj is MutableString) {
                return RType.String;
            }
            if (obj is RubySymbol) {
                return RType.Symbol;
            }
            if (obj is RubyArray) {
                return RType.Array;
            }
            if (obj is Hash) {
                return RType.Hash;
            }
            if (obj is BigInteger) {
                return Options.IsFixnum(obj) ? RType.Fixnum : RType.Bignum;
            }
            if (obj is long) {
                long l = (long)obj;
                return (l >= -(1L << 62) && l < (1L << 62)) ? RType.Fixnum : RType.Bignum;
            }
            if (obj is RubyClass) {
                return RType.Class;
            }
            if (obj is RubyModule) {
                return RType.Module;
            }
            if (obj is RubyRegex) {
                return RType.Regexp;
            }
            if (obj is RubyStruct || obj is Range) {
                return RType.Struct;
            }
            if (obj is RubyIO) {
                return RType.File;
            }
            if (obj is MatchData) {
                return RType.Match;
            }
            RubyClass cls = context.GetClassOf(obj);
            OjState state = OjState.Get(context);
            if (state.IsComplex(context, cls)) {
                return RType.Complex;
            }
            if (state.IsRational(context, cls)) {
                return RType.Rational;
            }
            return RType.Object;
        }

        internal static long ToFixnum(object obj) {
            if (obj is int) {
                return (int)obj;
            }
            if (obj is long) {
                return (long)obj;
            }
            return (long)(BigInteger)obj;
        }

        internal static BigInteger ToBignum(object obj) {
            if (obj is long) {
                return (long)obj;
            }
            return (BigInteger)obj;
        }

        #endregion

        #region circular references and key selection

        /// <summary>oj_check_circular: 0 when not in use, -1 for an object already seen.</summary>
        internal static long CheckCircular(Out/*!*/ output, object obj) {
            if (YesNo.Yes == output.Opts.Circular) {
                if (output.CircCache == null) {
                    output.CircCache = new Dictionary<object, long>(ReferenceEqualityComparer.Instance);
                }
                long id;
                if (!output.CircCache.TryGetValue(obj, out id)) {
                    output.CircCnt++;
                    id = output.CircCnt;
                    output.CircCache[obj] = id;
                } else {
                    return -1;
                }
                return id;
            }
            return 0;
        }

        private static bool KeyListed(string/*!*/ list, string/*!*/ key) {
            return list.Contains(":" + key + ":");
        }

        /// <summary>oj_key_skip.</summary>
        internal static bool KeySkip(object key, string only, string except) {
            string skey = null;
            if (key is MutableString) {
                skey = ((MutableString)key).ToString();
                int nul = skey.IndexOf('\0');
                if (nul >= 0) {
                    skey = skey.Substring(0, nul);
                }
            } else if (key is RubySymbol) {
                skey = ((RubySymbol)key).ToString();
            }
            if (skey != null && skey.Length > 0) {
                return (only != null && !KeyListed(only, skey)) || (except != null && KeyListed(except, skey));
            }
            return only != null;
        }

        #endregion

        #region shared entry

        /// <summary>oj_dump_obj_to_json_using_params.</summary>
        internal static void DumpObjToJson(Out/*!*/ output, object obj, Options/*!*/ copts, object[]/*!*/ argv) {
            output.CircCnt = 0;
            output.Opts = copts;
            output.Indent = copts.Indent;
            output.Argv = argv;
            output.Ropts = null;
            output.CircCache = null;
            switch (copts.Mode) {
                case OjMode.Strict: DumpStrictVal(obj, 0, output, false); break;
                case OjMode.Null: DumpStrictVal(obj, 0, output, true); break;
                case OjMode.Compat: DumpCompatVal(obj, 0, output, YesNo.Yes == copts.ToJson); break;
                case OjMode.Rails: OjRails.DumpRailsTop(obj, 0, output); break;
                case OjMode.Object: throw OjState.NotImplemented("Oj :object mode");
                case OjMode.Custom: throw OjState.NotImplemented("Oj :custom mode");
                case OjMode.Wab: throw OjState.NotImplemented("Oj :wab mode");
                default: throw OjState.NotImplemented("Oj :custom mode");
            }
            if (0 < output.Indent) {
                byte last = output.Last;
                if (last == ']' || last == '}') {
                    output.Add((byte)'\n');
                }
            }
        }

        #endregion

        #region :strict and :null

        private static void DumpStrictFloat(Out/*!*/ output, object obj) {
            double d = ToDouble(output.Context, obj);
            if (0.0 == d) {
                output.Add("0.0");
                return;
            }
            char nd = output.Opts.NanDump;
            if (Nan.Auto == nd) {
                nd = Nan.Raise;
            }
            if (Double.IsPositiveInfinity(d) || Double.IsNegativeInfinity(d) || Double.IsNaN(d)) {
                switch (nd) {
                    case Nan.Raise:
                    case Nan.Word:
                        throw RaiseStrict(output.Context, obj, true);
                    case Nan.Null:
                        output.Add("null");
                        return;
                    default:
                        output.Add(Double.IsNaN(d) ? NanVal : (d > 0 ? InfVal : NinfVal));
                        return;
                }
            }
            if (IsLongLong(d)) {
                output.Add(DotOne(d));
            } else if (0 == output.Opts.FloatPrec) {
                string s = SafeToS(output.Context, obj).ToString();
                if (s.Length >= 64) {
                    s = s.Substring(0, 63);
                }
                output.Add(s);
            } else {
                output.Add(FloatPrintf(output.Context, obj, d, output.Opts.FloatFmt));
            }
        }

        private static void DumpStrictArray(Out/*!*/ output, RubyArray/*!*/ a, int depth, bool nullMode) {
            int d2 = depth + 1;
            if (YesNo.Yes == output.Opts.Circular) {
                if (0 > CheckCircular(output, a)) {
                    DumpNil(output);
                    return;
                }
            }
            int cnt = a.Count;
            output.Add((byte)'[');
            if (0 == cnt) {
                output.Add((byte)']');
                return;
            }
            cnt--;
            for (int i = 0; i <= cnt; i++) {
                if (output.Opts.Use) {
                    output.AddNlIndent(output.Opts.ArrayNl, d2);
                } else {
                    output.FillIndent(d2);
                }
                DumpStrictVal(i < a.Count ? a[i] : null, d2, output, nullMode);
                if (i < cnt) {
                    output.Add((byte)',');
                }
            }
            if (output.Opts.Use) {
                output.AddNlIndent(output.Opts.ArrayNl, depth);
            } else {
                output.FillIndent(depth);
            }
            output.Add((byte)']');
        }

        private static void DumpStrictHash(Out/*!*/ output, Hash/*!*/ obj, int depth, bool nullMode) {
            if (YesNo.Yes == output.Opts.Circular) {
                if (0 > CheckCircular(output, obj)) {
                    DumpNil(output);
                    return;
                }
            }
            int cnt = obj.Count;
            output.Add((byte)'{');
            if (0 == cnt) {
                output.Add((byte)'}');
                return;
            }
            output.Depth = depth + 1;
            foreach (var pair in Snapshot(obj)) {
                object key = CustomStringDictionary.ObjToNull(pair.Key);
                object value = pair.Value;
                int d = output.Depth;
                if (!(key is MutableString) && !(key is RubySymbol)) {
                    throw RubyExceptions.CreateTypeError(String.Format(
                        "In :strict and :null mode all Hash keys must be Strings or Symbols, not {0}.\n",
                        output.Context.GetClassOf(key).Name));
                }
                if (output.OmitNil && value == null) {
                    continue;
                }
                if (output.Opts.Only != null || output.Opts.Except != null) {
                    if (KeySkip(key, output.Opts.Only, output.Opts.Except)) {
                        continue;
                    }
                }
                if (!output.Opts.Use) {
                    output.FillIndent(d);
                    DumpKey(output, key);
                    output.Add((byte)':');
                } else {
                    output.AddNlIndent(output.Opts.HashNl, d);
                    DumpKey(output, key);
                    output.AddSeparator();
                }
                DumpStrictVal(value, d, output, nullMode);
                output.Depth = d;
                output.Add((byte)',');
            }
            if (',' == output.Last) {
                output.Cur--;
            }
            if (!output.Opts.Use) {
                output.FillIndent(depth);
            } else {
                output.AddNlIndent(output.Opts.HashNl, depth);
            }
            output.Add((byte)'}');
        }

        internal static IEnumerable<KeyValuePair<object, object>>/*!*/ Snapshot(Hash/*!*/ hash) {
            return new List<KeyValuePair<object, object>>(hash);
        }

        internal static void DumpKey(Out/*!*/ output, object key) {
            if (key is MutableString) {
                DumpStr(output, (MutableString)key);
            } else if (key is RubySymbol) {
                DumpSym(output, (RubySymbol)key);
            } else {
                DumpStr(output, SafeToS(output.Context, key));
            }
        }

        private static bool IsBigDecimal(Out/*!*/ output, object obj) {
            return ReferenceEquals(output.Context.GetClassOf(obj), OjState.BigDecimalClass(output.Context));
        }

        /// <summary>oj_dump_strict_val / oj_dump_null_val.</summary>
        internal static void DumpStrictVal(object obj, int depth, Out/*!*/ output, bool nullMode) {
            if (MaxDepth < depth) {
                throw new OutOfMemoryException("Too deeply nested.\n");
            }
            RType type = TypeOf(output.Context, obj);
            switch (type) {
                case RType.Nil: DumpNil(output); return;
                case RType.True: DumpTrue(output); return;
                case RType.False: DumpFalse(output); return;
                case RType.Fixnum: DumpFixnum(output, ToFixnum(obj)); return;
                case RType.Bignum: DumpBignum(output, ToBignum(obj)); return;
                case RType.Float: DumpStrictFloat(output, obj); return;
                case RType.String: DumpStr(output, (MutableString)obj); return;
                case RType.Symbol: DumpSym(output, (RubySymbol)obj); return;
                case RType.Array: DumpStrictArray(output, (RubyArray)obj, depth, nullMode); return;
                case RType.Hash: DumpStrictHash(output, (Hash)obj, depth, nullMode); return;
                case RType.Object:
                case RType.Data:
                    if (IsBigDecimal(output, obj)) {
                        DumpRaw(output, SafeToS(output.Context, obj).ToByteArray());
                    } else if (nullMode) {
                        DumpNil(output);
                    } else {
                        throw RaiseStrict(output.Context, obj, true);
                    }
                    return;
            }
            if (nullMode) {
                DumpNil(output);
                return;
            }
            throw RaiseStrict(output.Context, obj, true);
        }

        #endregion

        #region :compat

        private static Exception/*!*/ JsonError(Out/*!*/ output, string/*!*/ message, string/*!*/ className) {
            return output.State.NewException(output.State.JsonErrorClass(output.Context, className), message);
        }

        private static void DumpToJson(Out/*!*/ output, object obj) {
            RubyContext context = output.Context;
            object rs;
            if (0 == OjState.MethodArity(context, obj, "to_json")) {
                rs = Sites.Call(context, obj, "to_json");
            } else {
                rs = Sites.CallN(context, obj, "to_json", output.Argv);
            }
            output.Add(Sites.StringValue(context, rs).ToByteArray());
        }

        private static void DumpCompatArray(Out/*!*/ output, RubyArray/*!*/ a, int depth, bool asOk) {
            int d2 = depth + 1;
            long id = CheckCircular(output, a);
            if (0 > id) {
                throw JsonError(output, "Too deeply nested", "NestingError");
            }
            if (asOk && !output.State.UseArrayAlt && !IsExactly(output.Context, a, typeof(RubyArray)) &&
                Sites.RespondTo(output.Context, a, "to_json")) {
                DumpToJson(output, a);
                return;
            }
            int cnt = a.Count;
            output.Add((byte)'[');
            if (0 == cnt) {
                output.Add((byte)']');
                return;
            }
            cnt--;
            for (int i = 0; i <= cnt; i++) {
                if (output.Opts.Use) {
                    output.AddNlIndent(output.Opts.ArrayNl, d2);
                } else {
                    output.FillIndent(d2);
                }
                DumpCompatVal(i < a.Count ? a[i] : null, d2, output, true);
                if (i < cnt) {
                    output.Add((byte)',');
                }
            }
            if (output.Opts.Use) {
                output.AddNlIndent(output.Opts.ArrayNl, depth);
            } else {
                output.FillIndent(depth);
            }
            output.Add((byte)']');
        }

        private static void DumpCompatHash(Out/*!*/ output, Hash/*!*/ obj, int depth, bool asOk) {
            long id = CheckCircular(output, obj);
            if (0 > id) {
                throw JsonError(output, "Too deeply nested", "NestingError");
            }
            if (asOk && !output.State.UseHashAlt && !IsExactly(output.Context, obj, typeof(Hash)) &&
                Sites.RespondTo(output.Context, obj, "to_json")) {
                DumpToJson(output, obj);
                return;
            }
            int cnt = obj.Count;
            if (0 == cnt) {
                output.Add("{}");
                return;
            }
            output.Add((byte)'{');
            output.Depth = depth + 1;
            foreach (var pair in Snapshot(obj)) {
                object key = CustomStringDictionary.ObjToNull(pair.Key);
                object value = pair.Value;
                int d = output.Depth;
                if (output.OmitNil && value == null) {
                    continue;
                }
                if (output.Opts.Only != null || output.Opts.Except != null) {
                    if (KeySkip(key, output.Opts.Only, output.Opts.Except)) {
                        continue;
                    }
                }
                if (!output.Opts.Use) {
                    output.FillIndent(d);
                } else {
                    output.AddNlIndent(output.Opts.HashNl, d);
                }
                DumpKey(output, key);
                if (!output.Opts.Use) {
                    output.Add((byte)':');
                } else {
                    output.AddSeparator();
                }
                DumpCompatVal(value, d, output, true);
                output.Depth = d;
                output.Add((byte)',');
            }
            if (',' == output.Last) {
                output.Cur--;
            }
            if (!output.Opts.Use) {
                output.FillIndent(depth);
            } else {
                output.AddNlIndent(output.Opts.HashNl, depth);
            }
            output.Add((byte)'}');
        }

        internal static bool IsExactly(RubyContext/*!*/ context, object obj, Type/*!*/ type) {
            return ReferenceEquals(context.GetClassOf(obj), context.GetClass(type));
        }

        /// <summary>The JSON gem is inconsistent about infinity; this is dump_compat.c's dump_float.</summary>
        private static void DumpCompatFloat(Out/*!*/ output, object obj) {
            double d = ToDouble(output.Context, obj);
            if (0.0 == d) {
                output.Add("0.0");
            } else if (Double.IsPositiveInfinity(d)) {
                if (Nan.Word == output.Opts.NanDump) {
                    output.Add("Infinity");
                } else {
                    throw JsonError(output, "Infinity not allowed in JSON.", "GeneratorError");
                }
            } else if (Double.IsNegativeInfinity(d)) {
                if (Nan.Word == output.Opts.NanDump) {
                    output.Add("-Infinity");
                } else {
                    throw JsonError(output, "-Infinity not allowed in JSON.", "GeneratorError");
                }
            } else if (Double.IsNaN(d)) {
                if (Nan.Word == output.Opts.NanDump) {
                    output.Add("NaN");
                } else {
                    throw JsonError(output, "NaN not allowed in JSON.", "GeneratorError");
                }
            } else if (IsLongLong(d)) {
                output.Add(DotOne(d));
            } else if (output.State.RailsFloatOpt) {
                output.Add(FloatPrintf(output.Context, obj, d, "%0.16g"));
            } else {
                output.Add(SafeToS(output.Context, obj).ToByteArray());
            }
        }

        private static void DumpCompatBignum(Out/*!*/ output, object obj) {
            MutableString rs;
            if (output.State.UseBignumAlt) {
                rs = MutableString.CreateAscii(ToBignum(obj).ToString(System.Globalization.CultureInfo.InvariantCulture));
            } else {
                rs = Sites.Call(output.Context, obj, "to_s") as MutableString;
                if (rs == null) {
                    throw RubyExceptions.CreateTypeError("wrong argument type (expected String)");
                }
            }
            bool dumpAsString = output.Opts.IntRangeMin != 0 || output.Opts.IntRangeMax != 0;
            if (dumpAsString) {
                output.Add((byte)'"');
            }
            output.Add(rs.ToByteArray());
            if (dumpAsString) {
                output.Add((byte)'"');
            }
        }

        private static void DumpObjClassname(Out/*!*/ output, string/*!*/ classname, int depth) {
            int d2 = depth + 1;
            output.Add((byte)'{');
            output.FillIndent(d2);
            output.Add((byte)'"');
            if (output.Opts.CreateId != null) {
                output.Add(output.Opts.CreateId);
            }
            output.Add((byte)'"');
            output.AddSeparator();
            output.Add((byte)'"');
            output.Add(Encoding.UTF8.GetBytes(classname));
            output.Add((byte)'"');
        }

        private static void DumpValuesArray(Out/*!*/ output, IList<object>/*!*/ values, int depth) {
            int d2 = depth + 1;
            output.Add((byte)'[');
            if (values.Count == 0) {
                output.Add((byte)']');
                return;
            }
            for (int i = 0; i < values.Count; i++) {
                if (output.Opts.Use) {
                    output.AddNlIndent(output.Opts.ArrayNl, d2);
                } else {
                    output.FillIndent(d2);
                }
                DumpCompatVal(values[i], d2, output, true);
                if (i + 1 < values.Count) {
                    output.Add((byte)',');
                }
            }
            if (output.Opts.Use) {
                output.AddNlIndent(output.Opts.ArrayNl, depth);
            } else {
                output.FillIndent(depth);
            }
            output.Add((byte)']');
        }

        /// <summary>An attribute for oj_code_attrs: a name and a value or an integer.</summary>
        internal struct Attr {
            internal string Name;
            internal object Value;
            internal bool IsNum;
            internal long Num;

            internal Attr(string name, object value) {
                Name = name;
                Value = value;
                IsNum = false;
                Num = 0;
            }

            internal Attr(string name, long num) {
                Name = name;
                Value = null;
                IsNum = true;
                Num = num;
            }
        }

        /// <summary>oj_code_attrs.</summary>
        internal static void CodeAttrs(Out/*!*/ output, object obj, Attr[]/*!*/ attrs, int depth, bool withClass) {
            int d2 = depth + 1;
            int d3 = d2 + 1;
            string classname = output.Context.GetClassOf(obj).Name;
            bool noComma = true;

            output.Add((byte)'{');
            if (withClass) {
                output.FillIndent(d2);
                output.Add((byte)'"');
                if (output.Opts.CreateId != null) {
                    output.Add(output.Opts.CreateId);
                }
                output.Add((byte)'"');
                output.AddSeparator();
                output.Add((byte)'"');
                output.Add(Encoding.UTF8.GetBytes(classname));
                output.Add((byte)'"');
                noComma = false;
            }
            foreach (var attr in attrs) {
                if (noComma) {
                    noComma = false;
                } else {
                    output.Add((byte)',');
                }
                output.FillIndent(d2);
                output.Add((byte)'"');
                output.Add(attr.Name);
                output.Add((byte)'"');
                output.AddSeparator();
                if (attr.IsNum) {
                    output.Add(attr.Num.ToString(System.Globalization.CultureInfo.InvariantCulture));
                } else {
                    DumpCompatVal(attr.Value, d3, output, true);
                }
            }
            output.FillIndent(depth);
            output.Add((byte)'}');
        }

        /// <summary>oj_code_dump over oj_compat_codes: the classes Oj.add_to_json turned on.</summary>
        internal static bool CodeDump(Out/*!*/ output, object obj, int depth) {
            RubyContext context = output.Context;
            OjState state = output.State;
            if (state.ActiveCodes.Count == 0) {
                return false;
            }
            RubyClass cls = context.GetClassOf(obj);
            string name = null;
            foreach (var code in OjState.CompatCodeNames) {
                if (!state.ActiveCodes.Contains(code)) {
                    continue;
                }
                object c = state.ResolveCodeClass(context, code);
                if (ReferenceEquals(c, cls)) {
                    name = code;
                    break;
                }
            }
            if (name == null) {
                return false;
            }
            switch (name) {
                case "BigDecimal":
                    CodeAttrs(output, obj, new[] { new Attr("b", Sites.Call(context, obj, "_dump")) }, depth, true);
                    break;
                case "Complex":
                    CodeAttrs(output, obj, new[] {
                        new Attr("r", Sites.Call(context, obj, "real")),
                        new Attr("i", Sites.Call(context, obj, "imag")),
                    }, depth, true);
                    break;
                case "Date":
                    CodeAttrs(output, obj, new[] {
                        new Attr("y", Sites.Call(context, obj, "year")),
                        new Attr("m", Sites.Call(context, obj, "month")),
                        new Attr("d", Sites.Call(context, obj, "day")),
                        new Attr("sg", Sites.Call(context, obj, "start")),
                    }, depth, true);
                    break;
                case "DateTime":
                    CodeAttrs(output, obj, new[] {
                        new Attr("y", Sites.Call(context, obj, "year")),
                        new Attr("m", Sites.Call(context, obj, "month")),
                        new Attr("d", Sites.Call(context, obj, "day")),
                        new Attr("H", Sites.Call(context, obj, "hour")),
                        new Attr("M", Sites.Call(context, obj, "min")),
                        new Attr("S", Sites.Call(context, obj, "sec")),
                        new Attr("of", SafeToS(context, Sites.Call(context, obj, "offset"))),
                        new Attr("sg", Sites.Call(context, obj, "start")),
                    }, depth, true);
                    break;
                case "OpenStruct":
                    CodeAttrs(output, obj, new[] { new Attr("t", Sites.Call(context, obj, "table")) }, depth, true);
                    break;
                case "Range": {
                        int d3 = depth + 2;
                        DumpObjClassname(output, cls.Name, depth);
                        output.Add((byte)',');
                        output.FillIndent(d3);
                        output.Add("\"a\"");
                        output.AddSeparator();
                        DumpValuesArray(output, new object[] {
                            Sites.Call(context, obj, "begin"),
                            Sites.Call(context, obj, "end"),
                            Sites.Call(context, obj, "exclude_end?"),
                        }, depth);
                        output.FillIndent(depth);
                        output.Add((byte)'}');
                        break;
                    }
                case "Rational":
                    CodeAttrs(output, obj, new[] {
                        new Attr("n", Sites.Call(context, obj, "numerator")),
                        new Attr("d", Sites.Call(context, obj, "denominator")),
                    }, depth, true);
                    break;
                case "Regexp":
                    CodeAttrs(output, obj, new[] {
                        new Attr("o", Sites.Call(context, obj, "options")),
                        new Attr("s", Sites.Call(context, obj, "source")),
                    }, depth, true);
                    break;
                case "Time":
                    CodeAttrs(output, obj, new[] {
                        new Attr("s", ToFixnum(Sites.Call(context, obj, "tv_sec"))),
                        new Attr("n", ToFixnum(Sites.Call(context, obj, "tv_nsec"))),
                    }, depth, true);
                    break;
                default:
                    return false;
            }
            return true;
        }

        private static void ExceptionAlt(Out/*!*/ output, object obj, int depth) {
            RubyContext context = output.Context;
            int d3 = depth + 2;
            DumpObjClassname(output, context.GetClassOf(obj).Name, depth);
            output.Add((byte)',');
            output.FillIndent(d3);
            output.Add("\"m\"");
            output.AddSeparator();
            DumpStr(output, Sites.StringValue(context, Sites.Call(context, obj, "message")));
            output.Add((byte)',');
            output.FillIndent(d3);
            output.Add("\"b\"");
            output.AddSeparator();
            object bt = Sites.Call(context, obj, "backtrace");
            if (bt is RubyArray) {
                DumpCompatArray(output, (RubyArray)bt, depth, false);
            } else {
                DumpNil(output);
            }
            output.FillIndent(depth);
            output.Add((byte)'}');
        }

        /// <summary>dump_compat.c's dump_obj: only the first call checks for to_json.</summary>
        private static void DumpCompatObj(Out/*!*/ output, object obj, int depth, bool asOk) {
            RubyContext context = output.Context;
            if (CodeDump(output, obj, depth)) {
                return;
            }
            if (output.State.UseExceptionAlt && obj is Exception) {
                ExceptionAlt(output, obj, depth);
                return;
            }
            if (YesNo.Yes == output.Opts.RawJson && Sites.RespondTo(context, obj, "raw_json")) {
                DumpRawJson(output, obj, depth);
                return;
            }
            if (asOk && Sites.RespondTo(context, obj, "to_json")) {
                DumpToJson(output, obj);
                return;
            }
            DumpObjToS(output, obj);
        }

        private static void DumpCompatStruct(Out/*!*/ output, object obj, int depth, bool asOk) {
            RubyContext context = output.Context;
            if (CodeDump(output, obj, depth)) {
                return;
            }
            if (obj is Range && IsExactly(context, obj, typeof(Range))) {
                var range = (Range)obj;
                output.Add((byte)'"');
                DumpCompatVal(range.Begin, 0, output, false);
                output.Add("..");
                if (range.ExcludeEnd) {
                    output.Add((byte)'.');
                }
                DumpCompatVal(range.End, 0, output, false);
                output.Add((byte)'"');
                return;
            }
            if (asOk && Sites.RespondTo(context, obj, "to_json")) {
                DumpToJson(output, obj);
                return;
            }
            if (output.State.UseStructAlt && obj is RubyStruct) {
                int d3 = depth + 2;
                string classname = context.GetClassOf(obj).Name;
                if (classname == null || classname.StartsWith("#", StringComparison.Ordinal)) {
                    throw JsonError(output, "Only named structs are supported.", "JSONError");
                }
                var st = (RubyStruct)obj;
                var values = new List<object>();
                int cnt = Math.Min(st.ItemCount, 99);
                for (int i = 0; i < cnt; i++) {
                    values.Add(st[i]);
                }
                DumpObjClassname(output, classname, depth);
                output.Add((byte)',');
                output.FillIndent(d3);
                output.Add("\"v\"");
                output.AddSeparator();
                DumpValuesArray(output, values, depth);
                output.FillIndent(depth);
                output.Add((byte)'}');
            } else {
                DumpObjToS(output, obj);
            }
        }

        /// <summary>set_state_depth: JSON::Ext::Generator::State, looked up the way upstream does.</summary>
        private static void SetStateDepth(Out/*!*/ output, object state, int depth) {
            RubyContext context = output.Context;
            if (OjState.Constant(context, context.ObjectClass, "JSON") == null) {
                Sites.Call(context, context.ObjectClass, "require", MutableString.CreateAscii("oj/json"));
            }
            object json = OjState.Constant(context, context.ObjectClass, "JSON");
            object ext = Sites.Call(context, json, "const_get", context.CreateAsciiSymbol("Ext"));
            object generator = Sites.Call(context, ext, "const_get", context.CreateAsciiSymbol("Generator"));
            object stateClass = Sites.Call(context, generator, "const_get", context.CreateAsciiSymbol("State"));
            if (ReferenceEquals(context.GetClassOf(state), stateClass)) {
                Sites.Call(context, state, "depth=", ScriptingRuntimeHelpers.Int32ToObject(depth));
            }
        }

        /// <summary>oj_dump_compat_val.</summary>
        internal static void DumpCompatVal(object obj, int depth, Out/*!*/ output, bool asOk) {
            RType type = TypeOf(output.Context, obj);

            // An empty Array or Hash is assumed to have content, so it is too deep while a
            // scalar at the same depth is not.
            if (output.Opts.MaxDepth <= depth) {
                if (RType.Array == type || RType.Hash == type) {
                    if (0 < output.Argc) {
                        SetStateDepth(output, output.Argv[0], depth);
                    }
                    throw JsonError(output, "Too deeply nested", "NestingError");
                }
            }
            switch (type) {
                case RType.Nil: DumpNil(output); return;
                case RType.True: DumpTrue(output); return;
                case RType.False: DumpFalse(output); return;
                case RType.Fixnum: DumpFixnum(output, ToFixnum(obj)); return;
                case RType.Bignum: DumpCompatBignum(output, obj); return;
                case RType.Float: DumpCompatFloat(output, obj); return;
                case RType.String: DumpStr(output, (MutableString)obj); return;
                case RType.Symbol: DumpSym(output, (RubySymbol)obj); return;
                case RType.Array: DumpCompatArray(output, (RubyArray)obj, depth, asOk); return;
                case RType.Hash: DumpCompatHash(output, (Hash)obj, depth, asOk); return;
                case RType.Class:
                case RType.Module: DumpClass(output, (RubyModule)obj); return;
                case RType.Struct: DumpCompatStruct(output, obj, depth, asOk); return;
                case RType.Object:
                case RType.Data:
                case RType.Regexp:
                case RType.Complex:
                case RType.Rational: DumpCompatObj(output, obj, depth, asOk); return;
            }
            DumpNil(output);
        }

        #endregion
    }
}

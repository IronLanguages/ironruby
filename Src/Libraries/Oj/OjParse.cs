/* ****************************************************************************
 *
 * IronRuby's implementation of the oj gem's C extension: parsing.
 *
 * A port of parse.c with the callbacks of strict.c (:strict and :null) and
 * compat.c (:compat and :rails), oj 3.17.6.  The input is the document's UTF-8
 * bytes with a NUL after them, which is what the C walks, so everything that
 * depends on it - a NUL byte ending the document, the "(after a.b[2])" path, the
 * "at line 1, column 14 [parse.c:936]" location and the 127-byte message limit -
 * comes out as upstream's does.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Oj {

    internal static class Next {
        internal const char None = '\0';
        internal const char ArrayNew = 'a';
        internal const char ArrayElement = 'e';
        internal const char ArrayComma = ',';
        internal const char HashNew = 'h';
        internal const char HashKey = 'k';
        internal const char HashColon = ':';
        internal const char HashValue = 'v';
        internal const char HashComma = 'n';

        internal static string/*!*/ Describe(char n) {
            switch (n) {
                case ArrayNew: return "array element or close";
                case ArrayElement: return "array element";
                case ArrayComma: return "comma";
                case HashNew: return "hash pair or close";
                case HashKey: return "hash key";
                case HashColon: return "colon";
                case HashValue: return "hash value";
                case HashComma: return "comma";
            }
            return "nothing";
        }
    }

    /// <summary>struct _val.</summary>
    internal sealed class Val {
        internal object Value;           // Undef until set
        internal bool HasKey;            // key != NULL
        internal byte[] KeyBuf;          // the json (a key in place) or a copy (an escaped key)
        internal int KeyStart;
        internal int KeyLen;
        internal object KeyVal;          // Undef: not computed
        internal byte[] Classname;
        internal char Next;
    }

    /// <summary>struct _numInfo.</summary>
    internal struct NumInfo {
        internal long I;
        internal long Num;
        internal long Div;
        internal long Di;
        internal int Str;
        internal int Len;
        internal long Exp;
        internal bool Big;
        internal bool Infinity;
        internal bool Nan;
        internal bool Neg;
        internal bool HasExp;
        internal bool NoBig;
        internal char BigdecLoad;
    }

    internal sealed class ParseInfo {
        internal static readonly object Undef = new object();

        internal readonly RubyContext/*!*/ Context;
        internal readonly OjState/*!*/ State;
        internal byte[]/*!*/ Json = new byte[] { 0 };
        internal int Cur;
        internal int End;
        internal Options/*!*/ Opts;
        internal bool Compat;             // compat.c's callbacks rather than strict.c's
        internal int MaxDepth;
        internal object ErrClass;         // pi->err_class
        internal object ErrClas;          // pi->err.clas, null when there is no error
        internal string ErrMsg;
        internal BlockParam Block;        // pi->proc == Qnil
        internal bool Broke;              // the block said break
        internal object BreakResult;
        internal bool HasProc;            // pi->proc != Qundef

        internal readonly List<Val>/*!*/ Stack = new List<Val>();
        internal int Tail;                // stack->tail - stack->head

        internal ParseInfo(RubyContext/*!*/ context, Options/*!*/ opts) {
            Context = context;
            State = OjState.Get(context);
            Opts = opts;
            Stack.Add(new Val { Value = Undef, KeyVal = Undef, Next = Next.None });
        }

        internal bool HasError { get { return ErrClas != null; } }

        internal Val Head { get { return Stack[0]; } }

        internal Val Peek() {
            return Tail > 0 ? Stack[Tail - 1] : null;
        }

        internal Val Pop() {
            if (Tail > 0) {
                Tail--;
                return Stack[Tail];
            }
            return null;
        }

        internal void Push(object value, char next) {
            if (Tail >= Stack.Count) {
                Stack.Add(new Val());
            }
            Val v = Stack[Tail];
            v.Value = value;
            v.Next = next;
            v.Classname = null;
            v.HasKey = false;
            v.KeyBuf = null;
            v.KeyStart = 0;
            v.KeyLen = 0;
            v.KeyVal = Undef;
            Tail++;
        }

        internal object HeadValue {
            get {
                object v = Stack[0].Value;
                return ReferenceEquals(v, Undef) ? null : v;
            }
        }

        internal byte At(int i) {
            return i < Json.Length ? Json[i] : (byte)0;
        }
    }

    internal static class OjParse {
        private const long ExpMax = 100000;
        private const int DecMax = 15;

        #region errors

        /// <summary>oj_set_error_at.</summary>
        internal static void SetErrorAt(ParseInfo/*!*/ pi, object errClas, string/*!*/ file, int line, string/*!*/ message) {
            var msg = new List<byte>(Encoding.UTF8.GetBytes(message));
            const int end = 254;
            if (msg.Count > 254) {
                msg.RemoveRange(252, msg.Count - 252);
            }
            pi.ErrClas = errClas;
            if (msg.Count + 8 <= end) {
                msg.AddRange(Encoding.ASCII.GetBytes(" (after "));
                int start = msg.Count;
                for (int i = 0; i < pi.Tail; i++) {
                    Val vp = pi.Stack[i];
                    if (end <= msg.Count + 1 + vp.KeyLen) {
                        break;
                    }
                    if (vp.HasKey) {
                        if (start < msg.Count) {
                            msg.Add((byte)'.');
                        }
                        for (int k = 0; k < vp.KeyLen; k++) {
                            msg.Add(vp.KeyBuf[vp.KeyStart + k]);
                        }
                    } else if (vp.Value is RubyArray) {
                        if (end <= msg.Count + 12) {
                            break;
                        }
                        msg.AddRange(Encoding.ASCII.GetBytes("[" + ((RubyArray)vp.Value).Count + "]"));
                    }
                }
                msg.Add((byte)')');
            }

            // _oj_err_set_with_location
            int current = pi.Cur - 1;
            int n = 1;
            int col = 1;
            for (; 0 < current && '\n' != pi.At(current); current--) {
                col++;
            }
            for (; 0 < current; current--) {
                if ('\n' == pi.At(current)) {
                    n++;
                }
            }
            msg.AddRange(Encoding.ASCII.GetBytes(String.Format(" at line {0}, column {1} [{2}:{3}]", n, col, file, line)));
            if (msg.Count > 126) {
                msg.RemoveRange(126, msg.Count - 126);
            }
            pi.ErrMsg = DecodeLenient(msg.ToArray());
        }

        private static string/*!*/ DecodeLenient(byte[]/*!*/ bytes) {
            return Encoding.UTF8.GetString(bytes);
        }

        private static void Error(ParseInfo/*!*/ pi, int line, string/*!*/ message) {
            SetErrorAt(pi, pi.State.OjParseErrorClass(pi.Context), "parse.c", line, message);
        }

        #endregion

        #region callbacks

        private static readonly byte[] Empty = new byte[0];

        /// <summary>oj_cstr_to_value.</summary>
        private static object CstrToValue(ParseInfo/*!*/ pi, byte[]/*!*/ str, int start, int len) {
            var rstr = MutableString.CreateBinary(len, RubyEncoding.UTF8).Append(str, start, len);
            if (len < pi.Opts.CacheStr) {
                return RubyOps.InternFrozenString(rstr);
            }
            return rstr;
        }

        /// <summary>oj_calc_hash_key.</summary>
        private static object CalcHashKey(ParseInfo/*!*/ pi, Val/*!*/ parent) {
            if (!ReferenceEquals(parent.KeyVal, ParseInfo.Undef)) {
                return parent.KeyVal;
            }
            byte[] buf = parent.KeyBuf ?? Empty;
            if (YesNo.Yes == pi.Opts.SymKey) {
                var s = MutableString.CreateBinary(parent.KeyLen, RubyEncoding.UTF8).Append(buf, parent.KeyStart, parent.KeyLen);
                return pi.Context.CreateSymbol(s, false);
            }
            var rkey = MutableString.CreateBinary(parent.KeyLen, RubyEncoding.UTF8).Append(buf, parent.KeyStart, parent.KeyLen);
            if (YesNo.Yes != pi.Opts.CacheKeys) {
                return rkey.Freeze();
            }
            return RubyOps.InternFrozenString(rkey);
        }

        private static object StartHash(ParseInfo/*!*/ pi) {
            if (pi.Opts.HashClass != null) {
                return Sites.Call(pi.Context, pi.Opts.HashClass, "new");
            }
            return new Hash(pi.Context);
        }

        private static object StartArray(ParseInfo/*!*/ pi) {
            if (pi.Compat && pi.Opts.ArrayClass != null) {
                return Sites.Call(pi.Context, pi.Opts.ArrayClass, "new");
            }
            return new RubyArray();
        }

        private static void HashAset(ParseInfo/*!*/ pi, object hash, object key, object value, bool viaMethod) {
            var h = hash as Hash;
            if (viaMethod || h == null) {
                Sites.Call(pi.Context, hash, "[]=", key, value);
            } else {
                RubyUtils.SetHashElement(pi.Context, h, key, value);
            }
        }

        private static bool IsPlainHash(ParseInfo/*!*/ pi, object value) {
            return value is Hash && ReferenceEquals(pi.Context.GetClassOf(value), pi.Context.GetClass(typeof(Hash)));
        }

        private static void ArrayPush(ParseInfo/*!*/ pi, object array, object value) {
            var a = array as RubyArray;
            if (a != null) {
                a.Add(value);
            } else {
                Sites.Call(pi.Context, array, "<<", value);
            }
        }

        private static bool CreateIdMatches(ParseInfo/*!*/ pi, Val/*!*/ kval) {
            byte[] cid = pi.Opts.CreateId;
            if (!ReferenceEquals(kval.KeyVal, ParseInfo.Undef) || YesNo.Yes != pi.Opts.CreateOk || cid == null) {
                return false;
            }
            if (cid.Length != kval.KeyLen || cid.Length == 0 && kval.KeyLen == 0) {
                // *create_id == *key compares the first bytes, which for two empty strings are both NUL
                return cid.Length == 0 && kval.KeyLen == 0;
            }
            for (int i = 0; i < cid.Length; i++) {
                if (cid[i] != kval.KeyBuf[kval.KeyStart + i]) {
                    return false;
                }
            }
            return true;
        }

        private static void HashSetCstr(ParseInfo/*!*/ pi, Val/*!*/ kval, byte[]/*!*/ str, int start, int len) {
            Val parent = pi.Peek();
            if (!pi.Compat) {
                object v = CstrToValue(pi, str, start, len);
                HashAset(pi, parent.Value, CalcHashKey(pi, kval), v, false);
                return;
            }
            if (CreateIdMatches(pi, kval)) {
                parent.Classname = new byte[len];
                Buffer.BlockCopy(str, start, parent.Classname, 0, len);
                return;
            }
            object rstr = CstrToValue(pi, str, start, len);
            object rkey = CalcHashKey(pi, kval);
            if (YesNo.Yes == pi.Opts.CreateOk && !pi.Opts.StrRx.IsEmpty) {
                object clas = pi.Opts.StrRx.Match(pi.Context, str, start, len);
                if (clas != null) {
                    rstr = Sites.Call(pi.Context, clas, "json_create", rstr);
                }
            }
            HashAset(pi, parent.Value, rkey, rstr, !IsPlainHash(pi, parent.Value));
        }

        private static void HashSetValue(ParseInfo/*!*/ pi, Val/*!*/ parent, object value) {
            object hash = pi.Peek().Value;
            HashAset(pi, hash, CalcHashKey(pi, parent), value, pi.Compat && !IsPlainHash(pi, parent.Value));
        }

        private static void HashSetNum(ParseInfo/*!*/ pi, Val/*!*/ parent, ref NumInfo ni) {
            if (!pi.Compat) {
                if (ni.Infinity || ni.Nan) {
                    SetErrorAt(pi, pi.State.OjParseErrorClass(pi.Context), "strict.c", 101, "not a number or other value");
                }
                object v = NumAsValue(pi, ref ni);
                HashAset(pi, pi.Peek().Value, CalcHashKey(pi, parent), v, false);
                return;
            }
            object rval = NumAsValue(pi, ref ni);
            HashAset(pi, pi.Peek().Value, CalcHashKey(pi, parent), rval, !IsPlainHash(pi, parent.Value));
        }

        private static void ArrayAppendCstr(ParseInfo/*!*/ pi, byte[]/*!*/ str, int start, int len) {
            object rstr = CstrToValue(pi, str, start, len);
            if (pi.Compat && YesNo.Yes == pi.Opts.CreateOk && !pi.Opts.StrRx.IsEmpty) {
                object clas = pi.Opts.StrRx.Match(pi.Context, str, start, len);
                if (clas != null) {
                    ArrayPush(pi, pi.Peek().Value, Sites.Call(pi.Context, clas, "json_create", rstr));
                    return;
                }
            }
            ArrayPush(pi, pi.Peek().Value, rstr);
        }

        private static void ArrayAppendNum(ParseInfo/*!*/ pi, ref NumInfo ni) {
            if (!pi.Compat) {
                if (ni.Infinity || ni.Nan) {
                    SetErrorAt(pi, pi.State.OjParseErrorClass(pi.Context), "strict.c", 129, "not a number or other value");
                }
                ArrayPush(pi, pi.Peek().Value, NumAsValue(pi, ref ni));
                return;
            }
            Val parent = pi.Peek();
            object rval = NumAsValue(pi, ref ni);
            if (!pi.State.UseArrayAlt && !(parent.Value is RubyArray &&
                ReferenceEquals(pi.Context.GetClassOf(parent.Value), pi.Context.GetClass(typeof(RubyArray))))) {
                Sites.Call(pi.Context, parent.Value, "<<", rval);
            } else {
                ArrayPush(pi, parent.Value, rval);
            }
        }

        private static void ArrayAppendValue(ParseInfo/*!*/ pi, object value) {
            ArrayPush(pi, pi.Peek().Value, value);
        }

        private static void AddCstr(ParseInfo/*!*/ pi, byte[]/*!*/ str, int start, int len) {
            object rstr = CstrToValue(pi, str, start, len);
            if (pi.Compat && YesNo.Yes == pi.Opts.CreateOk && !pi.Opts.StrRx.IsEmpty) {
                object clas = pi.Opts.StrRx.Match(pi.Context, str, start, len);
                if (clas != null) {
                    pi.Head.Value = Sites.Call(pi.Context, clas, "json_create", rstr);
                    return;
                }
            }
            pi.Head.Value = rstr;
        }

        private static void AddNum(ParseInfo/*!*/ pi, ref NumInfo ni) {
            if (!pi.Compat && (ni.Infinity || ni.Nan)) {
                SetErrorAt(pi, pi.State.OjParseErrorClass(pi.Context), "strict.c", 76, "not a number or other value");
            }
            pi.Head.Value = NumAsValue(pi, ref ni);
        }

        private static void AddValueCallback(ParseInfo/*!*/ pi, object value) {
            pi.Head.Value = value;
        }

        private static void EndHash(ParseInfo/*!*/ pi) {
            if (!pi.Compat) {
                return;
            }
            Val parent = pi.Peek();
            if (parent.Classname != null) {
                object clas = pi.State.NameToClass(pi, parent.Classname, pi.Context.GetClass(typeof(ArgumentException)));
                if (!ReferenceEquals(clas, ParseInfo.Undef)) {
                    if (!Sites.RespondTo(pi.Context, clas, "json_creatable?") ||
                        Sites.Call(pi.Context, clas, "json_creatable?") is bool && (bool)Sites.Call(pi.Context, clas, "json_creatable?") == true) {
                        parent.Value = Sites.Call(pi.Context, clas, "json_create", parent.Value);
                    }
                }
                parent.Classname = null;
            }
        }

        #endregion

        #region the scanner

        private static void NextNonWhite(ParseInfo/*!*/ pi) {
            for (; ; pi.Cur++) {
                switch (pi.At(pi.Cur)) {
                    case (byte)' ':
                    case (byte)'\t':
                    case (byte)'\f':
                    case (byte)'\n':
                    case (byte)'\r':
                        break;
                    default:
                        return;
                }
            }
        }

        private static void SkipComment(ParseInfo/*!*/ pi) {
            if ('*' == pi.At(pi.Cur)) {
                pi.Cur++;
                for (; pi.Cur < pi.End; pi.Cur++) {
                    if ('*' == pi.At(pi.Cur) && '/' == pi.At(pi.Cur + 1)) {
                        pi.Cur += 2;
                        return;
                    } else if (pi.End <= pi.Cur) {
                        Error(pi, 49, "comment not terminated");
                        return;
                    }
                }
            } else if ('/' == pi.At(pi.Cur)) {
                for (; ; pi.Cur++) {
                    switch (pi.At(pi.Cur)) {
                        case (byte)'\n':
                        case (byte)'\r':
                        case (byte)'\f':
                        case 0:
                            return;
                    }
                }
            } else {
                Error(pi, 64, "invalid comment format");
            }
        }

        private static void AddValue(ParseInfo/*!*/ pi, object rval) {
            Val parent = pi.Peek();
            if (parent == null) {
                AddValueCallback(pi, rval);
                return;
            }
            switch (parent.Next) {
                case Next.ArrayNew:
                case Next.ArrayElement:
                    ArrayAppendValue(pi, rval);
                    parent.Next = Next.ArrayComma;
                    break;
                case Next.HashValue:
                    HashSetValue(pi, parent, rval);
                    ReleaseKey(pi, parent);
                    parent.Next = Next.HashComma;
                    break;
                default:
                    Error(pi, 98, String.Format("expected {0}", Next.Describe(parent.Next)));
                    break;
            }
        }

        /// <summary>An escaped key was a copy; upstream frees it once its value is set.</summary>
        private static void ReleaseKey(ParseInfo/*!*/ pi, Val/*!*/ parent) {
            if (parent.HasKey && 0 < parent.KeyLen && !ReferenceEquals(parent.KeyBuf, pi.Json)) {
                parent.HasKey = false;
                parent.KeyBuf = null;
            }
        }

        private static void ReadNull(ParseInfo/*!*/ pi) {
            if ('u' == pi.At(pi.Cur++) && 'l' == pi.At(pi.Cur++) && 'l' == pi.At(pi.Cur++)) {
                AddValue(pi, null);
            } else {
                Error(pi, 110, "expected null");
            }
        }

        private static void ReadTrue(ParseInfo/*!*/ pi) {
            if ('r' == pi.At(pi.Cur++) && 'u' == pi.At(pi.Cur++) && 'e' == pi.At(pi.Cur++)) {
                AddValue(pi, ScriptingRuntimeHelpers.True);
            } else {
                Error(pi, 118, "expected true");
            }
        }

        private static void ReadFalse(ParseInfo/*!*/ pi) {
            if ('a' == pi.At(pi.Cur++) && 'l' == pi.At(pi.Cur++) && 's' == pi.At(pi.Cur++) && 'e' == pi.At(pi.Cur++)) {
                AddValue(pi, ScriptingRuntimeHelpers.False);
            } else {
                Error(pi, 126, "expected false");
            }
        }

        private static uint ReadHex(ParseInfo/*!*/ pi, int h) {
            uint b = 0;
            for (int i = 0; i < 4; i++, h++) {
                b = b << 4;
                byte c = pi.At(h);
                if ('0' <= c && c <= '9') {
                    b += (uint)(c - '0');
                } else if ('A' <= c && c <= 'F') {
                    b += (uint)(c - 'A' + 10);
                } else if ('a' <= c && c <= 'f') {
                    b += (uint)(c - 'a' + 10);
                } else {
                    Error(pi, 143, "invalid hex character");
                    return 0;
                }
            }
            return b;
        }

        private static void UnicodeToChars(ParseInfo/*!*/ pi, List<byte>/*!*/ buf, uint code) {
            if (0x7F >= code) {
                buf.Add((byte)code);
            } else if (0x7FF >= code) {
                buf.Add((byte)(0xC0 | (code >> 6)));
                buf.Add((byte)(0x80 | (0x3F & code)));
            } else if (0xFFFF >= code) {
                buf.Add((byte)(0xE0 | (code >> 12)));
                buf.Add((byte)(0x80 | ((code >> 6) & 0x3F)));
                buf.Add((byte)(0x80 | (0x3F & code)));
            } else if (0x1FFFFF >= code) {
                buf.Add((byte)(0xF0 | (code >> 18)));
                buf.Add((byte)(0x80 | ((code >> 12) & 0x3F)));
                buf.Add((byte)(0x80 | ((code >> 6) & 0x3F)));
                buf.Add((byte)(0x80 | (0x3F & code)));
            } else if (0x3FFFFFF >= code) {
                buf.Add((byte)(0xF8 | (code >> 24)));
                buf.Add((byte)(0x80 | ((code >> 18) & 0x3F)));
                buf.Add((byte)(0x80 | ((code >> 12) & 0x3F)));
                buf.Add((byte)(0x80 | ((code >> 6) & 0x3F)));
                buf.Add((byte)(0x80 | (0x3F & code)));
            } else if (0x7FFFFFFF >= code) {
                buf.Add((byte)(0xFC | (code >> 30)));
                buf.Add((byte)(0x80 | ((code >> 24) & 0x3F)));
                buf.Add((byte)(0x80 | ((code >> 18) & 0x3F)));
                buf.Add((byte)(0x80 | ((code >> 12) & 0x3F)));
                buf.Add((byte)(0x80 | ((code >> 6) & 0x3F)));
                buf.Add((byte)(0x80 | (0x3F & code)));
            } else {
                Error(pi, 179, "invalid Unicode character");
            }
        }

        private static int ScanString(ParseInfo/*!*/ pi, int str) {
            byte[] json = pi.Json;
            int end = pi.End;
            for (; str < end; str++) {
                byte c = json[str];
                if (c == 0 || c == '\\' || c == '"') {
                    break;
                }
            }
            return str;
        }

        /// <summary>Hands a finished string to the parent: the part of read_str and read_escaped_str they share.</summary>
        private static void StringDone(ParseInfo/*!*/ pi, byte[]/*!*/ buf, int start, int len, bool inPlace, int line) {
            Val parent = pi.Peek();
            if (parent == null) {
                AddCstr(pi, buf, start, len);
                return;
            }
            switch (parent.Next) {
                case Next.ArrayNew:
                case Next.ArrayElement:
                    ArrayAppendCstr(pi, buf, start, len);
                    parent.Next = Next.ArrayComma;
                    break;
                case Next.HashNew:
                case Next.HashKey:
                    // hash_key is a no-op in these modes: the key is kept as it is
                    parent.KeyVal = ParseInfo.Undef;
                    parent.HasKey = true;
                    if (inPlace) {
                        parent.KeyBuf = pi.Json;
                        parent.KeyStart = start;
                    } else {
                        parent.KeyBuf = new byte[len];
                        Buffer.BlockCopy(buf, start, parent.KeyBuf, 0, len);
                        parent.KeyStart = 0;
                    }
                    parent.KeyLen = len;
                    parent.Next = Next.HashColon;
                    break;
                case Next.HashValue:
                    HashSetCstr(pi, parent, buf, start, len);
                    ReleaseKey(pi, parent);
                    parent.Next = Next.HashComma;
                    break;
                default:
                    Error(pi, line, String.Format("expected {0}, not a string", Next.Describe(parent.Next)));
                    break;
            }
        }

        private static void ReadEscapedStr(ParseInfo/*!*/ pi, int start) {
            var buf = new List<byte>();
            for (int i = start; i < pi.Cur; i++) {
                buf.Add(pi.Json[i]);
            }

            int s;
            for (s = pi.Cur; '"' != pi.At(s);) {
                int scanned = ScanString(pi, s);
                if (scanned >= pi.End || 0 == pi.At(scanned)) {
                    Error(pi, 408, "quoted string not terminated");
                    return;
                }
                for (int i = s; i < scanned; i++) {
                    buf.Add(pi.Json[i]);
                }
                s = scanned;

                if ('\\' == pi.At(s)) {
                    s++;
                    if (pi.End <= s) {
                        Error(pi, 418, "quoted string not terminated");
                        return;
                    }
                    switch (pi.At(s)) {
                        case (byte)'n': buf.Add((byte)'\n'); break;
                        case (byte)'r': buf.Add((byte)'\r'); break;
                        case (byte)'t': buf.Add((byte)'\t'); break;
                        case (byte)'f': buf.Add((byte)'\f'); break;
                        case (byte)'b': buf.Add((byte)'\b'); break;
                        case (byte)'"': buf.Add((byte)'"'); break;
                        case (byte)'/': buf.Add((byte)'/'); break;
                        case (byte)'\\': buf.Add((byte)'\\'); break;
                        case (byte)'u': {
                                s++;
                                uint code = ReadHex(pi, s);
                                if (0 == code && pi.HasError) {
                                    return;
                                }
                                s += 3;
                                if (0xD800 <= code && code <= 0xDFFF) {
                                    uint c1 = (code - 0xD800) & 0x3FF;
                                    uint c2;
                                    s++;
                                    if ('\\' != pi.At(s) || 'u' != pi.At(s + 1)) {
                                        if (YesNo.Yes == pi.Opts.AllowInvalid) {
                                            s--;
                                            UnicodeToChars(pi, buf, code);
                                            break;
                                        }
                                        pi.Cur = s;
                                        Error(pi, 450, "invalid escaped character");
                                        return;
                                    }
                                    s += 2;
                                    c2 = ReadHex(pi, s);
                                    if (0 == c2 && pi.HasError) {
                                        return;
                                    }
                                    s += 3;
                                    c2 = (c2 - 0xDC00) & 0x3FF;
                                    code = ((c1 << 10) | c2) + 0x10000;
                                }
                                UnicodeToChars(pi, buf, code);
                                if (pi.HasError) {
                                    return;
                                }
                                break;
                            }
                        default:
                            // the json gem claims this is not an error, despite ECMA-404
                            if (OjMode.Compat == pi.Opts.Mode) {
                                buf.Add(pi.At(s));
                                break;
                            }
                            pi.Cur = s;
                            Error(pi, 477, "invalid escaped character");
                            return;
                    }
                    s++;
                }
            }
            byte[] bytes = buf.ToArray();
            StringDone(pi, bytes, 0, bytes.Length, false, 523);
            pi.Cur = s + 1;
        }

        private static void ReadStr(ParseInfo/*!*/ pi) {
            int str = pi.Cur;
            pi.Cur = ScanString(pi, pi.Cur);
            if (pi.End <= pi.Cur) {
                Error(pi, 539, "quoted string not terminated");
                return;
            }
            if (0 == pi.At(pi.Cur)) {
                Error(pi, 543, "NULL byte in string");
                return;
            }
            if ('\\' == pi.At(pi.Cur)) {
                ReadEscapedStr(pi, str);
                return;
            }
            StringDone(pi, pi.Json, str, pi.Cur - str, true, 588);
            pi.Cur++;
        }

        private static bool CaseEqualsToEnd(ParseInfo/*!*/ pi, string/*!*/ value, int start) {
            // strcasecmp(value, str): str runs to the NUL after the document
            int len = pi.End - start;
            if (len != value.Length) {
                return false;
            }
            for (int i = 0; i < len; i++) {
                if (Char.ToLowerInvariant((char)pi.Json[start + i]) != Char.ToLowerInvariant(value[i])) {
                    return false;
                }
            }
            return true;
        }

        private static void ReadNum(ParseInfo/*!*/ pi) {
            var ni = new NumInfo();
            Val parent = pi.Peek();
            ni.Str = pi.Cur;
            ni.Div = 1;
            if (OjMode.Compat == pi.Opts.Mode) {
                ni.NoBig = !pi.Opts.CompatBigdec;
                ni.BigdecLoad = pi.Opts.CompatBigdec ? (char)1 : (char)0;
            } else {
                ni.NoBig = (BigLoad.FloatDec == pi.Opts.BigdecLoad || BigLoad.FastDec == pi.Opts.BigdecLoad ||
                    BigLoad.RubyDec == pi.Opts.BigdecLoad);
                ni.BigdecLoad = pi.Opts.BigdecLoad;
            }

            if ('-' == pi.At(pi.Cur)) {
                pi.Cur++;
                ni.Neg = true;
            } else if ('+' == pi.At(pi.Cur)) {
                if (OjMode.Strict == pi.Opts.Mode) {
                    Error(pi, 628, "not a number or other value");
                    return;
                }
                pi.Cur++;
            }
            if ('I' == pi.At(pi.Cur)) {
                if (YesNo.No == pi.Opts.AllowNan || !Matches(pi, pi.Cur, "Infinity")) {
                    Error(pi, 635, "not a number or other value");
                    return;
                }
                pi.Cur += 8;
                ni.Infinity = true;
            } else if ('N' == pi.At(pi.Cur) || 'n' == pi.At(pi.Cur)) {
                if ('a' != pi.At(pi.Cur + 1) || ('N' != pi.At(pi.Cur + 2) && 'n' != pi.At(pi.Cur + 2))) {
                    Error(pi, 642, "not a number or other value");
                    return;
                }
                pi.Cur += 3;
                ni.Nan = true;
            } else {
                int decCnt = 0;
                bool zero1 = false;

                for (; '0' == pi.At(pi.Cur); pi.Cur++) {
                    zero1 = true;
                }
                for (; '0' <= pi.At(pi.Cur) && pi.At(pi.Cur) <= '9'; pi.Cur++) {
                    int d = pi.At(pi.Cur) - '0';
                    if (0 != ni.I) {
                        decCnt++;
                    }
                    ni.I = unchecked(ni.I * 10 + d);
                }
                if (0 != ni.I && zero1 && OjMode.Compat == pi.Opts.Mode) {
                    Error(pi, 665, "not a number");
                    return;
                }
                if (Int64.MaxValue <= ni.I || DecMax < decCnt) {
                    ni.Big = true;
                }

                if ('.' == pi.At(pi.Cur)) {
                    pi.Cur++;
                    // A trailing . is not a valid decimal; it is let through except when
                    // mimicking the json gem or in strict mode.
                    if (OjMode.Strict == pi.Opts.Mode || OjMode.Compat == pi.Opts.Mode) {
                        int pos = pi.Cur - ni.Str;
                        if (1 == pos || (2 == pos && ni.Neg)) {
                            Error(pi, 680, "not a number");
                            return;
                        }
                        if (pi.At(pi.Cur) < '0' || '9' < pi.At(pi.Cur)) {
                            Error(pi, 684, "not a number");
                            return;
                        }
                    }
                    for (; '0' <= pi.At(pi.Cur) && pi.At(pi.Cur) <= '9'; pi.Cur++) {
                        int d = pi.At(pi.Cur) - '0';
                        if (0 != ni.Num || 0 != ni.I) {
                            decCnt++;
                        }
                        ni.Num = unchecked(ni.Num * 10 + d);
                        ni.Div = unchecked(ni.Div * 10);
                        ni.Di++;
                    }
                }
                if (Int64.MaxValue <= ni.Div || DecMax < decCnt) {
                    if (!ni.NoBig) {
                        ni.Big = true;
                    }
                }

                if ('e' == pi.At(pi.Cur) || 'E' == pi.At(pi.Cur)) {
                    bool eneg = false;
                    ni.HasExp = true;
                    pi.Cur++;
                    if ('-' == pi.At(pi.Cur)) {
                        pi.Cur++;
                        eneg = true;
                    } else if ('+' == pi.At(pi.Cur)) {
                        pi.Cur++;
                    }
                    for (; '0' <= pi.At(pi.Cur) && pi.At(pi.Cur) <= '9'; pi.Cur++) {
                        ni.Exp = unchecked(ni.Exp * 10 + (pi.At(pi.Cur) - '0'));
                        if (ExpMax <= ni.Exp) {
                            ni.Big = true;
                        }
                    }
                    if (eneg) {
                        ni.Exp = -ni.Exp;
                    }
                }
                ni.Len = pi.Cur - ni.Str;
            }
            // the reserved values for Infinity and NaN
            if (ni.Big) {
                if (CaseEqualsToEnd(pi, "3.0e14159265358979323846", ni.Str)) {
                    ni.Infinity = true;
                } else if (CaseEqualsToEnd(pi, "-3.0e14159265358979323846", ni.Str)) {
                    ni.Infinity = true;
                    ni.Neg = true;
                } else if (CaseEqualsToEnd(pi, "3.3e14159265358979323846", ni.Str)) {
                    ni.Nan = true;
                }
            }
            if (OjMode.Compat == pi.Opts.Mode) {
                if (pi.Opts.CompatBigdec) {
                    ni.Big = true;
                }
            } else if (BigLoad.BigDec == pi.Opts.BigdecLoad) {
                ni.Big = true;
            }
            if (parent == null) {
                AddNum(pi, ref ni);
                return;
            }
            switch (parent.Next) {
                case Next.ArrayNew:
                case Next.ArrayElement:
                    ArrayAppendNum(pi, ref ni);
                    parent.Next = Next.ArrayComma;
                    break;
                case Next.HashValue:
                    HashSetNum(pi, parent, ref ni);
                    ReleaseKey(pi, parent);
                    parent.Next = Next.HashComma;
                    break;
                default:
                    Error(pi, 767, String.Format("expected {0}", Next.Describe(parent.Next)));
                    break;
            }
        }

        private static bool Matches(ParseInfo/*!*/ pi, int at, string/*!*/ text) {
            for (int i = 0; i < text.Length; i++) {
                if (pi.At(at + i) != text[i]) {
                    return false;
                }
            }
            return true;
        }

        private static string/*!*/ NumText(ParseInfo/*!*/ pi, ref NumInfo ni) {
            return Encoding.ASCII.GetString(pi.Json, ni.Str, ni.Len);
        }

        /// <summary>The length strtod would consume from the start of text.</summary>
        private static int StrtodLength(string/*!*/ text) {
            int i = 0;
            if (i < text.Length && (text[i] == '-' || text[i] == '+')) {
                i++;
            }
            int digits = 0;
            while (i < text.Length && Char.IsDigit(text[i])) {
                i++;
                digits++;
            }
            if (i < text.Length && text[i] == '.') {
                int j = i + 1;
                int frac = 0;
                while (j < text.Length && Char.IsDigit(text[j])) {
                    j++;
                    frac++;
                }
                if (digits + frac > 0) {
                    i = j;
                    digits += frac;
                }
            }
            if (digits == 0) {
                return 0;
            }
            if (i < text.Length && (text[i] == 'e' || text[i] == 'E')) {
                int j = i + 1;
                if (j < text.Length && (text[j] == '-' || text[j] == '+')) {
                    j++;
                }
                int k = j;
                while (k < text.Length && Char.IsDigit(text[k])) {
                    k++;
                }
                if (k > j) {
                    i = k;
                }
            }
            return i;
        }

        private static double ParseDouble(string/*!*/ text) {
            string t = text;
            if (t.EndsWith(".", StringComparison.Ordinal)) {
                t = t + "0";
            }
            t = t.Replace(".e", ".0e").Replace(".E", ".0E");
            if (t.StartsWith(".", StringComparison.Ordinal) || t.StartsWith("-.", StringComparison.Ordinal) || t.StartsWith("+.", StringComparison.Ordinal)) {
                t = t.Replace(".", "0.");
            }
            return Double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static object Integer(BigInteger value) {
            return Protocols.Normalize(value);
        }

        /// <summary>oj_num_as_value.</summary>
        private static object NumAsValue(ParseInfo/*!*/ pi, ref NumInfo ni) {
            if (ni.Infinity) {
                return ni.Neg ? Double.NegativeInfinity : Double.PositiveInfinity;
            }
            if (ni.Nan) {
                return Double.NaN;
            }
            if (1 == ni.Div && 0 == ni.Exp && !ni.HasExp) {
                long limit = pi.Opts.MaxIntegerDigits;
                if (0 < limit) {
                    long digitCount = ni.Len - (ni.Neg ? 1 : 0);
                    if (digitCount > limit) {
                        SetErrorAt(pi, pi.ErrClass ?? pi.State.OjParseErrorClass(pi.Context), "parse.c", 986,
                            String.Format("integer exceeds :max_integer_digits ({0} > {1})", digitCount, limit));
                    }
                }
                if (ni.Big) {
                    string text = NumText(pi, ref ni);
                    BigInteger big;
                    if (!BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out big)) {
                        big = BigInteger.Zero;
                    }
                    return Integer(big);
                }
                return Protocols.Normalize(ni.Neg ? unchecked(-ni.I) : ni.I);
            }

            string num = NumText(pi, ref ni);
            if (ni.Big) {
                if (ni.NoBig) {
                    // BigDecimal(str).to_f: the same correctly rounded double
                    return ParseDouble(num.Substring(0, Math.Max(StrtodLength(num), 1)));
                }
                try {
                    return Sites.Call(pi.Context, pi.Context.ObjectClass, "BigDecimal", MutableString.CreateAscii(num));
                } catch (Exception) {
                    throw pi.State.NewException(pi.State.OjParseErrorClass(pi.Context), "Invalid value for BigDecimal()");
                }
            }
            if (BigLoad.FastDec == ni.BigdecLoad) {
                // upstream multiplies in long double; this is the same arithmetic in double
                double ld = (double)ni.I * (double)ni.Div + (double)ni.Num;
                int x = (int)(ni.Exp - ni.Di);
                if (0 < x) {
                    ld *= Math.Pow(10.0, x);
                } else if (x < 0) {
                    ld /= Math.Pow(10.0, -x);
                }
                if (ni.Neg) {
                    ld = -ld;
                }
                return ld;
            }
            if (BigLoad.RubyDec == ni.BigdecLoad) {
                int n = StrtodLength(num);
                return n == 0 ? 0.0 : ParseDouble(num.Substring(0, n));
            }
            int consumed = StrtodLength(num);
            if (consumed != ni.Len) {
                throw pi.State.NewException(pi.ErrClass ?? pi.State.OjParseErrorClass(pi.Context), "Invalid float");
            }
            return ParseDouble(num);
        }

        private static void ArrayStart(ParseInfo/*!*/ pi) {
            object v = StartArray(pi);
            pi.Push(v, Next.ArrayNew);
        }

        private static void ArrayEnd(ParseInfo/*!*/ pi) {
            Val array = pi.Pop();
            if (array == null) {
                Error(pi, 785, "unexpected array close");
            } else if (Next.ArrayComma != array.Next && Next.ArrayNew != array.Next) {
                Error(pi, 790, String.Format("expected {0}, not an array close", Next.Describe(array.Next)));
            } else {
                AddValue(pi, array.Value);
            }
        }

        private static void HashStart(ParseInfo/*!*/ pi) {
            object v = StartHash(pi);
            pi.Push(v, Next.HashNew);
        }

        private static void HashEnd(ParseInfo/*!*/ pi) {
            Val hash = pi.Peek();
            if (hash == null) {
                Error(pi, 810, "unexpected hash close");
            } else if (Next.HashComma != hash.Next && Next.HashNew != hash.Next) {
                Error(pi, 815, String.Format("expected {0}, not a hash close", Next.Describe(hash.Next)));
            } else {
                EndHash(pi);
                pi.Pop();
                AddValue(pi, hash.Value);
            }
        }

        private static void Comma(ParseInfo/*!*/ pi) {
            Val parent = pi.Peek();
            if (parent == null) {
                Error(pi, 829, "unexpected comma");
            } else if (Next.ArrayComma == parent.Next) {
                parent.Next = Next.ArrayElement;
            } else if (Next.HashComma == parent.Next) {
                parent.Next = Next.HashKey;
            } else {
                Error(pi, 835, "unexpected comma");
            }
        }

        private static void Colon(ParseInfo/*!*/ pi) {
            Val parent = pi.Peek();
            if (parent != null && Next.HashColon == parent.Next) {
                parent.Next = Next.HashValue;
            } else {
                Error(pi, 845, "unexpected colon");
            }
        }

        /// <summary>
        /// oj_parse2.  The whole document is one call, so it is compiled optimized up front
        /// rather than left to tiered compilation's on-stack replacement.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        internal static void Parse2(ParseInfo/*!*/ pi) {
            bool first = true;
            int start = 0;

            pi.Cur = 0;
            pi.ErrClas = null;
            while (true) {
                if (0 < pi.MaxDepth && pi.MaxDepth <= pi.Tail - 1) {
                    object errClas = pi.State.JsonErrorClass(pi.Context, "NestingError");
                    SetErrorAt(pi, errClas, "parse.c", 859, "Too deeply nested.");
                    pi.ErrClass = errClas;
                    return;
                }
                NextNonWhite(pi);
                if (first) {
                    // no tokens at all is a parse error, as JSON.parse and JavaScript say
                    if (0 == pi.At(pi.Cur) && YesNo.No == pi.Opts.EmptyString) {
                        Error(pi, 868, "unexpected character");
                    }
                } else {
                    if (0 != pi.At(pi.Cur)) {
                        Error(pi, 875, "unexpected characters after the JSON document");
                    }
                }

                byte c = pi.At(pi.Cur++);
                switch (c) {
                    case (byte)'{': HashStart(pi); break;
                    case (byte)'}': HashEnd(pi); break;
                    case (byte)':': Colon(pi); break;
                    case (byte)'[': ArrayStart(pi); break;
                    case (byte)']': ArrayEnd(pi); break;
                    case (byte)',': Comma(pi); break;
                    case (byte)'"': ReadStr(pi); break;
                    case (byte)'+':
                        if (OjMode.Compat == pi.Opts.Mode) {
                            Error(pi, 890, "unexpected character");
                            return;
                        }
                        pi.Cur--;
                        ReadNum(pi);
                        break;
                    case (byte)'-':
                    case (byte)'0':
                    case (byte)'1':
                    case (byte)'2':
                    case (byte)'3':
                    case (byte)'4':
                    case (byte)'5':
                    case (byte)'6':
                    case (byte)'7':
                    case (byte)'8':
                    case (byte)'9':
                        pi.Cur--;
                        ReadNum(pi);
                        break;
                    case (byte)'I':
                    case (byte)'N':
                        if (YesNo.Yes == pi.Opts.AllowNan) {
                            pi.Cur--;
                            ReadNum(pi);
                        } else {
                            Error(pi, 916, "unexpected character");
                        }
                        break;
                    case (byte)'t': ReadTrue(pi); break;
                    case (byte)'f': ReadFalse(pi); break;
                    case (byte)'n':
                        if ('u' == pi.At(pi.Cur)) {
                            ReadNull(pi);
                        } else {
                            pi.Cur--;
                            ReadNum(pi);
                        }
                        break;
                    case (byte)'/':
                        SkipComment(pi);
                        if (first) {
                            continue;
                        }
                        break;
                    case 0:
                        pi.Cur--;
                        return;
                    default:
                        Error(pi, 936, "unexpected character");
                        return;
                }
                if (pi.HasError) {
                    return;
                }
                if (pi.Tail == 0) {
                    if (pi.HasProc) {
                        int len = pi.Cur - start;
                        object result;
                        if (pi.Block != null && pi.Block.Yield(pi.HeadValue, ScriptingRuntimeHelpers.Int32ToObject(start),
                                ScriptingRuntimeHelpers.Int32ToObject(len), out result)) {
                            pi.Broke = true;
                            pi.BreakResult = result;
                            return;
                        }
                    } else {
                        first = false;
                    }
                    start = pi.Cur;
                }
            }
        }

        #endregion

        #region oj_pi_parse

        private static bool EmptyOk(Options/*!*/ o) {
            switch (o.Mode) {
                case OjMode.Object:
                case OjMode.Wab:
                    return true;
                case OjMode.Compat:
                case OjMode.Rails:
                    return false;
            }
            return YesNo.Yes == o.EmptyString;
        }

        /// <summary>The bytes of an input String, converted to UTF-8 when it can be.</summary>
        private static byte[]/*!*/ InputBytes(RubyContext/*!*/ context, MutableString/*!*/ input) {
            return OjDump.Utf8Bytes(context, input);
        }

        /// <summary>
        /// oj_reader_init and oj_reader_read: an IO is read with readpartial or read, 4092 bytes
        /// at a time as the reader's first buffer asks for them; a read answering more than asked
        /// is an IOError, and anything other than TypeError or EOFError raised by the IO comes
        /// back as its own class with the reader's position.  In :compat mode an object that is
        /// neither is read through #to_s.
        /// </summary>
        private static byte[]/*!*/ ReadStream(ParseInfo/*!*/ pi, object input) {
            RubyContext context = pi.Context;
            string method;
            if (Sites.RespondTo(context, input, "readpartial")) {
                method = "readpartial";
            } else if (Sites.RespondTo(context, input, "read")) {
                method = "read";
            } else if (OjMode.Compat == pi.Opts.Mode) {
                return InputBytes(context, OjDump.SafeToS(context, input));
            } else {
                throw RubyExceptions.CreateArgumentError("parser io argument must be a String or respond to readpartial() or read().\n");
            }

            const int request = 4096 - 4;
            var all = new List<byte>();
            while (true) {
                object rstr;
                try {
                    rstr = Sites.Call(context, input, method, ScriptingRuntimeHelpers.Int32ToObject(request));
                    if (rstr == null) {
                        break;
                    }
                    byte[] chunk = Sites.StringValue(context, rstr).ToByteArray();
                    if (chunk.Length > request) {
                        throw RubyExceptions.CreateIOError("read returned more than the requested number of bytes");
                    }
                    if (chunk.Length == 0) {
                        break;
                    }
                    all.AddRange(chunk);
                } catch (Exception e) {
                    RubyClass ec = context.GetClassOf(e);
                    if (ReferenceEquals(ec, OjState.Constant(context, context.ObjectClass, "TypeError")) ||
                        ReferenceEquals(ec, OjState.Constant(context, context.ObjectClass, "EOFError"))) {
                        break;
                    }
                    throw pi.State.NewException(ec, "at line 1, column 0\n");
                }
            }
            return all.ToArray();
        }

        /// <summary>oj_pi_parse (and oj_pi_sparse, which reads an IO whole here).</summary>
        internal static object Parse(ParseInfo/*!*/ pi, object[]/*!*/ argv, BlockParam block, bool yieldOk) {
            RubyContext context = pi.Context;
            if (argv.Length < 1) {
                throw RubyExceptions.CreateArgumentError("Wrong number of arguments to parse.");
            }
            object input = argv[0];
            if (2 <= argv.Length) {
                if (argv[1] is Hash) {
                    pi.Opts.Parse(context, argv[1], false);
                } else if (3 <= argv.Length && argv[2] is Hash) {
                    pi.Opts.Parse(context, argv[2], false);
                }
            }
            if (yieldOk && block != null) {
                pi.HasProc = true;
                pi.Block = block;
            } else {
                pi.HasProc = false;
            }

            byte[] bytes;
            if (input is MutableString) {
                if (OjMode.Compat == pi.Opts.Mode) {
                    if (YesNo.No == pi.Opts.Nilnil && ((MutableString)input).GetByteCount() == 0) {
                        throw pi.State.NewException(pi.State.JsonParserErrorClass(context), "An empty string is not a valid JSON string.");
                    }
                }
                bytes = InputBytes(context, (MutableString)input);
            } else if (input == null) {
                if (YesNo.Yes == pi.Opts.Nilnil) {
                    return null;
                }
                throw RubyExceptions.CreateTypeError("Nil is not a valid JSON source.");
            } else {
                RubyClass clas = context.GetClassOf(input);
                if (ReferenceEquals(clas, pi.State.StringIOClass(context))) {
                    bytes = InputBytes(context, Sites.StringValue(context, Sites.Call(context, input, "string")));
                } else {
                    // oj_pi_sparse's reader (reader.c), read to the end up front
                    bytes = ReadStream(pi, input);
                }
            }
            pi.Json = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, pi.Json, 0, bytes.Length);
            pi.End = bytes.Length;

            Exception raised = null;
            try {
                Parse2(pi);
            } catch (Exception e) {
                raised = e;
            }

            if (pi.Broke) {
                return pi.BreakResult;
            }
            if (ReferenceEquals(pi.Head.Value, ParseInfo.Undef) && !EmptyOk(pi.Opts)) {
                if (YesNo.No == pi.Opts.Nilnil || (OjMode.Compat == pi.Opts.Mode && 0 < pi.Cur)) {
                    SetErrorAt(pi, pi.State.JsonParserErrorClass(context), "parse.c", 1266, "Empty input");
                }
            }
            object result = pi.HeadValue;

            if (!pi.HasError) {
                object errClass = pi.State.OjParseErrorClass(context);
                bool checkTermination = true;
                if (raised != null) {
                    RubyClass ec = context.GetClassOf(raised);
                    if (!ReferenceEquals(ec, context.GetClass(typeof(ArgumentException)))) {
                        errClass = ec;
                    }
                    if (!(raised is System.IO.IOException)) {
                        checkTermination = false;
                    }
                }
                Val v;
                if (checkTermination && null != (v = pi.Peek())) {
                    switch (v.Next) {
                        case Next.ArrayNew:
                        case Next.ArrayElement:
                        case Next.ArrayComma:
                            SetErrorAt(pi, errClass, "parse.c", 1293, "Array not terminated");
                            break;
                        case Next.HashNew:
                        case Next.HashKey:
                        case Next.HashColon:
                        case Next.HashValue:
                        case Next.HashComma:
                            SetErrorAt(pi, errClass, "parse.c", 1299, "Hash/Object not terminated");
                            break;
                        default:
                            SetErrorAt(pi, errClass, "parse.c", 1301, "not terminated");
                            break;
                    }
                }
            }

            if (pi.HasError) {
                if (pi.ErrClass != null) {
                    pi.ErrClas = pi.ErrClass;
                }
                if ((OjMode.Compat == pi.Opts.Mode || OjMode.Rails == pi.Opts.Mode) && YesNo.Yes != pi.Opts.Safe) {
                    // the json gem wants the message UTF-8 and the whole document after it
                    var msg = MutableString.Create(pi.ErrMsg, RubyEncoding.UTF8);
                    msg.Append(" in '");
                    int nul = Array.IndexOf(pi.Json, (byte)0);
                    msg.Append(pi.Json, 0, nul < 0 ? pi.End : nul);
                    object clas = pi.ErrClas;
                    if (ReferenceEquals(clas, pi.State.OjParseErrorClass(context))) {
                        clas = pi.State.JsonParserErrorClass(context);
                    }
                    throw pi.State.NewException(clas, msg);
                }
                throw pi.State.NewException(pi.ErrClas, MutableString.Create(pi.ErrMsg, RubyEncoding.UTF8));
            } else if (raised != null) {
                // a throw starts the CLR trace over; this frame is the one that caught it, so the capture still has its callers
                RubyExceptionData.GetInstance(raised).CaptureExceptionTrace(context);
                throw raised;
            }

            if (YesNo.No == pi.Opts.QuirksMode) {
                RType t = OjDump.TypeOf(context, result);
                switch (t) {
                    case RType.Nil:
                    case RType.True:
                    case RType.False:
                    case RType.Fixnum:
                    case RType.Float:
                    case RType.Class:
                    case RType.String:
                    case RType.Symbol:
                        throw pi.State.NewException(pi.ErrClass ?? pi.State.OjParseErrorClass(context), "unexpected non-document value");
                }
            }
            return result;
        }

        #endregion
    }
}

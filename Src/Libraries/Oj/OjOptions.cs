/* ****************************************************************************
 *
 * IronRuby's implementation of the oj gem's C extension: the options.
 *
 * A port of struct _options and of oj.c's option handling (oj 3.17.6):
 * Oj.default_options, Oj.default_options= and the options Hash every call takes.
 * The fields keep the C encoding - 'y'/'n'/0 for the YesNo flags, a character per
 * mode - so the code that reads them can be compared with upstream line by line.
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

    internal static class OjMode {
        internal const char Strict = 's';
        internal const char Object = 'o';
        internal const char Null = 'n';
        internal const char Compat = 'c';
        internal const char Rails = 'r';
        internal const char Custom = 'C';
        internal const char Wab = 'w';
    }

    internal static class Esc {
        internal const char NL = 'n';
        internal const char JSON = 'j';
        internal const char Slash = 's';
        internal const char XSS = 'x';
        internal const char ASCII = 'a';
        internal const char JX = 'g';       // json gem
        internal const char RailsX = 'r';   // rails xss
        internal const char Rails = 'R';    // rails, no html escape
    }

    internal static class Nan {
        internal const char Auto = 'a';
        internal const char Null = 'n';
        internal const char Huge = 'h';
        internal const char Word = 'w';
        internal const char Raise = 'r';
    }

    internal static class BigLoad {
        internal const char BigDec = 'b';
        internal const char FloatDec = 'f';
        internal const char AutoDec = 'a';
        internal const char FastDec = 'F';
        internal const char RubyDec = 'r';
    }

    internal static class YesNo {
        internal const char Yes = 'y';
        internal const char No = 'n';
        internal const char NotSet = '\0';
    }

    /// <summary>struct _rxClass: the :match_string patterns, each a Regexp and the class to create.</summary>
    internal sealed class RxClass {
        internal readonly List<KeyValuePair<object, object>>/*!*/ Entries = new List<KeyValuePair<object, object>>();

        internal bool IsEmpty { get { return Entries.Count == 0; } }

        internal object Match(RubyContext/*!*/ context, byte[]/*!*/ bytes, int start, int len) {
            foreach (var entry in Entries) {
                var str = MutableString.CreateBinary(len, RubyEncoding.Binary).Append(bytes, start, len);
                if (Sites.Call(context, entry.Key, "match", str) != null) {
                    return entry.Value;
                }
            }
            return null;
        }
    }

    /// <summary>struct _options, dump_opts included.</summary>
    internal sealed class Options {
        internal int Indent;
        internal char Circular = YesNo.No;
        internal char AutoDefine = YesNo.No;
        internal char SymKey = YesNo.No;
        internal char EscapeMode = Esc.JSON;
        internal char Mode = OjMode.Object;
        internal char ClassCache = YesNo.Yes;
        internal char TimeFormat = 'u';
        internal char BigdecAsNum = YesNo.NotSet;
        internal char BigdecLoad = BigLoad.AutoDec;
        internal bool CompatBigdec;
        internal char ToHash = YesNo.No;
        internal char ToJson = YesNo.No;
        internal char AsJson = YesNo.No;
        internal char RawJson = YesNo.No;
        internal char Nilnil = YesNo.No;
        internal char EmptyString = YesNo.Yes;
        internal char AllowGc = YesNo.Yes;
        internal char QuirksMode = YesNo.Yes;
        internal char AllowInvalid = YesNo.No;
        internal char CreateOk = YesNo.No;
        internal char AllowNan = YesNo.Yes;
        internal char Trace = YesNo.No;
        internal char Safe = YesNo.No;
        internal bool SecPrecSet;
        internal char IgnoreUnder = YesNo.No;
        internal char CacheKeys = YesNo.Yes;
        internal int CacheStr;
        internal long IntRangeMin;
        internal long IntRangeMax;
        internal long MaxIntegerDigits;
        internal byte[] CreateId = Encoding.ASCII.GetBytes("json_class");
        internal int SecPrec = 9;
        internal int FloatPrec = 16;
        internal string FloatFmt = "%0.15g";
        internal object HashClass;
        internal object ArrayClass;
        internal object[] Ignore;

        // dump_opts
        internal bool Use;
        internal byte[] IndentStr = Empty;
        internal byte[] BeforeSep = Empty;
        internal byte[] AfterSep = Empty;
        internal byte[] HashNl = Empty;
        internal byte[] ArrayNl = Empty;
        internal char NanDump = Nan.Auto;
        internal bool OmitNil;
        internal bool OmitNullByte;
        internal int MaxDepth = OjDump.MaxDepth;
        internal string Only;
        internal string Except;

        internal RxClass StrRx = new RxClass();

        internal static readonly byte[] Empty = new byte[0];

        internal Options/*!*/ Clone() {
            var result = (Options)MemberwiseClone();
            return result;
        }

        /// <summary>mimic_object_to_json_options, which Oj.mimic_JSON makes the defaults.</summary>
        internal static Options/*!*/ MimicObjectToJson() {
            var o = new Options();
            o.Indent = 0;
            o.Circular = YesNo.No;
            o.AutoDefine = YesNo.No;
            o.SymKey = YesNo.No;
            o.EscapeMode = Esc.JX;
            o.Mode = OjMode.Compat;
            o.ClassCache = YesNo.No;
            o.TimeFormat = 'r';
            o.BigdecAsNum = YesNo.No;
            o.BigdecLoad = BigLoad.RubyDec;
            o.CompatBigdec = false;
            o.ToHash = YesNo.No;
            o.ToJson = YesNo.No;
            o.AsJson = YesNo.No;
            o.RawJson = YesNo.No;
            o.Nilnil = YesNo.No;
            o.EmptyString = YesNo.No;
            o.AllowGc = YesNo.Yes;
            o.QuirksMode = YesNo.Yes;
            o.AllowInvalid = YesNo.Yes;
            o.CreateOk = YesNo.No;
            o.AllowNan = YesNo.No;
            o.Trace = YesNo.No;
            o.Safe = YesNo.No;
            o.SecPrecSet = false;
            o.IgnoreUnder = YesNo.No;
            o.CacheKeys = YesNo.Yes;
            o.CacheStr = 0;
            o.SecPrec = 3;
            o.FloatPrec = 0;
            o.FloatFmt = "%0.16g";
            o.NanDump = Nan.Raise;
            o.MaxDepth = 100;
            return o;
        }

        #region parsing an options Hash

        private static bool Is(RubyContext/*!*/ context, object key, string/*!*/ name) {
            var sym = key as RubySymbol;
            return sym != null && sym.Equals(context.CreateAsciiSymbol(name));
        }

        private static string/*!*/ KeyName(object key) {
            var sym = key as RubySymbol;
            return sym != null ? sym.ToString() : null;
        }

        private bool SetYesNo(RubyContext/*!*/ context, string/*!*/ name, object value) {
            char v;
            if (value == null) {
                v = YesNo.NotSet;
            } else if (value is bool && (bool)value) {
                v = YesNo.Yes;
            } else if (value is bool) {
                v = YesNo.No;
            } else {
                v = '?';
            }

            switch (name) {
                case "circular":
                case "auto_define":
                case "symbol_keys":
                case "class_cache":
                case "bigdecimal_as_decimal":
                case "use_to_hash":
                case "use_to_json":
                case "use_as_json":
                case "use_raw_json":
                case "nilnil":
                case "allow_blank":
                case "empty_string":
                case "allow_gc":
                case "quirks_mode":
                case "allow_invalid_unicode":
                case "allow_nan":
                case "trace":
                case "safe":
                case "ignore_under":
                case "create_additions":
                case "cache_keys":
                    break;
                default:
                    return false;
            }
            if (v == '?') {
                throw RubyExceptions.CreateArgumentError(String.Format("{0} must be true, false, or nil.", name));
            }
            switch (name) {
                case "circular": Circular = v; break;
                case "auto_define": AutoDefine = v; break;
                case "symbol_keys": SymKey = v; break;
                case "class_cache": ClassCache = v; break;
                case "bigdecimal_as_decimal": BigdecAsNum = v; break;
                case "use_to_hash": ToHash = v; break;
                case "use_to_json": ToJson = v; break;
                case "use_as_json": AsJson = v; break;
                case "use_raw_json": RawJson = v; break;
                case "nilnil": Nilnil = v; break;
                case "allow_blank": Nilnil = v; break;
                case "empty_string": EmptyString = v; break;
                case "allow_gc": AllowGc = v; break;
                case "quirks_mode": QuirksMode = v; break;
                case "allow_invalid_unicode": AllowInvalid = v; break;
                case "allow_nan": AllowNan = v; break;
                case "trace": Trace = v; break;
                case "safe": Safe = v; break;
                case "ignore_under": IgnoreUnder = v; break;
                case "create_additions": CreateOk = v; break;
                case "cache_keys": CacheKeys = v; break;
            }
            return true;
        }

        private static byte[]/*!*/ OptionBytes(RubyContext/*!*/ context, object value, int limit, string/*!*/ what) {
            var str = value as MutableString;
            if (str == null) {
                throw Sites.WrongType(context, value, "String");
            }
            byte[] bytes = str.ToByteArray();
            if (bytes.Length >= limit) {
                throw RubyExceptions.CreateArgumentError(String.Format("{0} string is limited to {1} characters.", what, limit));
            }
            // strcpy stops at the first NUL
            int nul = Array.IndexOf(bytes, (byte)0);
            if (nul >= 0) {
                Array.Resize(ref bytes, nul);
            }
            return bytes;
        }

        private static bool IsClass(object v) {
            return v is RubyClass;
        }

        private static void CheckClass(RubyContext/*!*/ context, object v) {
            if (!(v is RubyClass)) {
                throw Sites.WrongType(context, v, "Class");
            }
        }

        private static string MakeOnlyValue(RubyContext/*!*/ context, object v) {
            if (v == null) {
                return null;
            }
            var list = v as RubyArray;
            if (list != null) {
                var names = new List<string>();
                foreach (object x in list) {
                    if (x is RubySymbol) {
                        names.Add(((RubySymbol)x).ToString());
                    } else if (x is MutableString) {
                        names.Add(((MutableString)x).ToString());
                    } else {
                        throw RubyExceptions.CreateArgumentError(":only and :except must be nil, symbol, string, or array.");
                    }
                }
                int size = 0;
                foreach (var n in names) {
                    size += n.Length + 1;
                }
                if (size == 0) {
                    return null;
                }
                var sb = new StringBuilder(":");
                foreach (var n in names) {
                    sb.Append(n).Append(':');
                }
                return sb.ToString();
            }
            if (v is MutableString) {
                return ":" + ((MutableString)v).ToString() + ":";
            }
            if (v is RubySymbol) {
                return ":" + ((RubySymbol)v).ToString() + ":";
            }
            throw RubyExceptions.CreateArgumentError(":only and zzz :except must be nil, symbol, string, or array.");
        }

        private static void ValidateFloatFormat(string/*!*/ str) {
            int cnt = 0;
            for (int s = 0; s < str.Length; s++) {
                if (str[s] != '%') {
                    continue;
                }
                s++;
                if (s < str.Length && str[s] == '%') {
                    continue;
                }
                if (1 < ++cnt) {
                    throw RubyExceptions.CreateArgumentError(":float_format must not have more than one directive.");
                }
                for (; s < str.Length && "-+ #0".IndexOf(str[s]) >= 0; s++) {
                }
                for (; s < str.Length && '0' <= str[s] && str[s] <= '9'; s++) {
                }
                if (s < str.Length && str[s] == '.') {
                    for (s++; s < str.Length && '0' <= str[s] && str[s] <= '9'; s++) {
                    }
                }
                if (s < str.Length && str[s] == 'l') {
                    s++;
                }
                if (str.Length <= s || "aAeEfgG".IndexOf(str[s]) < 0) {
                    throw RubyExceptions.CreateArgumentError(":float_format directive must be one of aAeEfgG and take no argument of its own.");
                }
            }
        }

        /// <summary>parse_one_option.  Answers whether the key named an option Oj knows.</summary>
        internal bool ParseOne(RubyContext/*!*/ context, object k, object v, bool isDefaults) {
            string name = KeyName(k);
            if (name == null) {
                return false;
            }
            if (SetYesNo(context, name, v)) {
                return true;
            }
            switch (name) {
                case "indent":
                    if (v == null) {
                        IndentStr = Empty;
                        Indent = 0;
                    } else if (v is int) {
                        IndentStr = Empty;
                        if (16 < (int)v) {
                            throw RubyExceptions.CreateArgumentError("indent is limited to 16 characters.");
                        }
                        Indent = (int)v;
                    } else if (v is MutableString) {
                        byte[] bytes = ((MutableString)v).ToByteArray();
                        if (16 <= bytes.Length) {
                            throw RubyExceptions.CreateArgumentError("indent string is limited to 16 characters.");
                        }
                        IndentStr = bytes;
                        Indent = 0;
                    } else {
                        throw RubyExceptions.CreateTypeError("indent must be a Fixnum, String, or nil.");
                    }
                    break;

                case "float_precision": {
                        if (!(v is int || v is BigInteger)) {
                            throw RubyExceptions.CreateArgumentError(":float_precision must be a Integer.");
                        }
                        int n = Sites.ToInt(context, v);
                        if (0 >= n) {
                            FloatFmt = "";
                            FloatPrec = 0;
                        } else {
                            if (20 < n) {
                                n = 20;
                            }
                            FloatFmt = "%0." + n + "g";
                            FloatPrec = n;
                        }
                        break;
                    }

                case "cache_str":
                case "cache_string": {
                        if (!(v is int || v is BigInteger)) {
                            throw RubyExceptions.CreateArgumentError(":cache_str must be a Integer.");
                        }
                        int n = Sites.ToInt(context, v);
                        CacheStr = n <= 0 ? 0 : Math.Min(n, 32);
                        break;
                    }

                case "second_precision": {
                        if (!(v is int || v is BigInteger)) {
                            throw RubyExceptions.CreateArgumentError(":second_precision must be a Integer.");
                        }
                        int n = Sites.ToInt(context, v);
                        if (0 > n) {
                            n = 0;
                            SecPrecSet = false;
                        } else if (9 < n) {
                            n = 9;
                            SecPrecSet = true;
                        } else {
                            SecPrecSet = true;
                        }
                        SecPrec = n;
                        break;
                    }

                case "mode":
                    Mode = ModeFromSymbol(context, v);
                    break;

                case "time_format":
                    if (Is(context, v, "unix")) {
                        TimeFormat = 'u';
                    } else if (Is(context, v, "unix_zone")) {
                        TimeFormat = 'z';
                    } else if (Is(context, v, "xmlschema")) {
                        TimeFormat = 'x';
                    } else if (Is(context, v, "ruby")) {
                        TimeFormat = 'r';
                    } else {
                        throw RubyExceptions.CreateArgumentError(":time_format must be :unix, :unix_zone, :xmlschema, or :ruby.");
                    }
                    break;

                case "escape_mode":
                    if (Is(context, v, "newline")) {
                        EscapeMode = Esc.NL;
                    } else if (Is(context, v, "json")) {
                        EscapeMode = Esc.JSON;
                    } else if (Is(context, v, "slash")) {
                        EscapeMode = Esc.Slash;
                    } else if (Is(context, v, "xss_safe")) {
                        EscapeMode = Esc.XSS;
                    } else if (Is(context, v, "ascii")) {
                        EscapeMode = Esc.ASCII;
                    } else if (Is(context, v, "unicode_xss")) {
                        EscapeMode = Esc.JX;
                    } else {
                        throw RubyExceptions.CreateArgumentError(":encoding must be :newline, :json, :xss_safe, :unicode_xss, or :ascii.");
                    }
                    break;

                case "bigdecimal_load":
                    if (v == null) {
                        return true;
                    }
                    if (Is(context, v, "bigdecimal") || (v is bool && (bool)v)) {
                        BigdecLoad = BigLoad.BigDec;
                    } else if (Is(context, v, "float")) {
                        BigdecLoad = BigLoad.FloatDec;
                    } else if (Is(context, v, "fast")) {
                        BigdecLoad = BigLoad.FastDec;
                    } else if (Is(context, v, "auto") || (v is bool && !(bool)v)) {
                        BigdecLoad = BigLoad.AutoDec;
                    } else {
                        throw RubyExceptions.CreateArgumentError(":bigdecimal_load must be :bigdecimal, :float, or :auto.");
                    }
                    break;

                case "compat_bigdecimal":
                    if (v == null) {
                        return true;
                    }
                    CompatBigdec = v is bool && (bool)v;
                    break;

                case "decimal_class":
                    if (ReferenceEquals(v, context.GetClass(typeof(double)))) {
                        CompatBigdec = false;
                    } else if (ReferenceEquals(v, OjState.BigDecimalClass(context))) {
                        CompatBigdec = true;
                    } else {
                        throw RubyExceptions.CreateArgumentError(":decimal_class must be BigDecimal or Float.");
                    }
                    break;

                case "create_id":
                    if (v == null) {
                        CreateId = null;
                    } else if (v is MutableString) {
                        CreateId = ((MutableString)v).ToByteArray();
                    } else {
                        throw RubyExceptions.CreateArgumentError(":create_id must be string.");
                    }
                    break;

                case "space":
                    AfterSep = v == null ? Empty : OptionBytes(context, v, 16, "space");
                    break;
                case "space_before":
                    BeforeSep = v == null ? Empty : OptionBytes(context, v, 16, "sapce_before");
                    break;
                case "object_nl":
                    HashNl = v == null ? Empty : OptionBytes(context, v, 16, "object_nl");
                    break;
                case "array_nl":
                    ArrayNl = v == null ? Empty : OptionBytes(context, v, 16, "array_nl");
                    break;

                case "nan":
                    if (v == null) {
                        return true;
                    }
                    if (Is(context, v, "null")) {
                        NanDump = Nan.Null;
                    } else if (Is(context, v, "huge")) {
                        NanDump = Nan.Huge;
                    } else if (Is(context, v, "word")) {
                        NanDump = Nan.Word;
                    } else if (Is(context, v, "raise")) {
                        NanDump = Nan.Raise;
                    } else if (Is(context, v, "auto")) {
                        NanDump = Nan.Auto;
                    } else {
                        throw RubyExceptions.CreateArgumentError(":nan must be :null, :huge, :word, :raise, or :auto.");
                    }
                    break;

                case "omit_nil":
                    if (v == null) {
                        return true;
                    }
                    if (!(v is bool)) {
                        throw RubyExceptions.CreateArgumentError(":omit_nil must be true or false.");
                    }
                    OmitNil = (bool)v;
                    break;

                case "omit_null_byte":
                    if (v == null) {
                        return true;
                    }
                    if (!(v is bool)) {
                        throw RubyExceptions.CreateArgumentError(":omit_null_byte must be true or false.");
                    }
                    OmitNullByte = (bool)v;
                    break;

                case "ascii_only":
                    if (v is bool) {
                        EscapeMode = (bool)v ? Esc.ASCII : Esc.JSON;
                    }
                    break;

                case "hash_class":
                case "object_class":
                    if (v == null) {
                        HashClass = null;
                    } else {
                        CheckClass(context, v);
                        HashClass = v;
                    }
                    break;

                case "array_class":
                    if (v == null) {
                        ArrayClass = null;
                    } else {
                        CheckClass(context, v);
                        ArrayClass = v;
                    }
                    break;

                case "ignore":
                    Ignore = null;
                    if (v != null) {
                        var list = v as RubyArray;
                        if (list == null) {
                            throw Sites.WrongType(context, v, "Array");
                        }
                        if (list.Count > 0) {
                            Ignore = list.ToArray();
                        }
                    }
                    break;

                case "integer_range":
                    if (v == null) {
                        return true;
                    }
                    if (v is Range && ReferenceEquals(context.GetClassOf(v), context.GetClass(typeof(Range)))) {
                        object min = Sites.Call(context, v, "begin");
                        object max = Sites.Call(context, v, "end");
                        if (!IsFixnum(min) || !IsFixnum(max)) {
                            throw RubyExceptions.CreateArgumentError(":integer_range range bounds is not Fixnum.");
                        }
                        IntRangeMin = ToLong(min);
                        IntRangeMax = ToLong(max);
                    } else if (!(v is bool && !(bool)v)) {
                        throw RubyExceptions.CreateArgumentError(":integer_range must be a range of Fixnum.");
                    }
                    break;

                case "max_integer_digits":
                    if (v == null || (v is bool && !(bool)v)) {
                        MaxIntegerDigits = 0;
                    } else if (IsFixnum(v)) {
                        long n = ToLong(v);
                        if (n < 0) {
                            throw RubyExceptions.CreateArgumentError(":max_integer_digits must be >= 0.");
                        }
                        MaxIntegerDigits = n;
                    } else {
                        throw RubyExceptions.CreateArgumentError(":max_integer_digits must be a non-negative Integer.");
                    }
                    break;

                case "symbol_keys_":
                    break;

                case "symbolize_names":
                    if (v == null) {
                        return true;
                    }
                    SymKey = (v is bool && (bool)v) ? YesNo.Yes : YesNo.No;
                    break;

                case "max_nesting":
                    if (v is bool && (bool)v) {
                        MaxDepth = 100;
                    } else if (v == null || v is bool) {
                        MaxDepth = OjDump.MaxDepth;
                    } else if (IsFixnum(v)) {
                        MaxDepth = Sites.ToInt(context, v);
                        if (0 >= MaxDepth) {
                            MaxDepth = OjDump.MaxDepth;
                        }
                    }
                    break;

                case "float_format": {
                        var str = v as MutableString;
                        if (str == null) {
                            throw Sites.WrongType(context, v, "String");
                        }
                        if (6 < str.GetByteCount()) {
                            throw RubyExceptions.CreateArgumentError(":float_format must be 6 bytes or less.");
                        }
                        ValidateFloatFormat(str.ToString());
                        FloatFmt = str.ToString();
                        break;
                    }

                case "only":
                    Only = MakeOnlyValue(context, v);
                    break;

                case "except":
                    Except = MakeOnlyValue(context, v);
                    break;

                default:
                    return false;
            }
            return true;
        }

        internal static bool IsFixnum(object v) {
            if (v is int) {
                return true;
            }
            if (v is BigInteger) {
                var b = (BigInteger)v;
                return b >= -(BigInteger.One << 62) && b < (BigInteger.One << 62);
            }
            return false;
        }

        internal static long ToLong(object v) {
            if (v is int) {
                return (int)v;
            }
            return (long)(BigInteger)v;
        }

        internal static char ModeFromSymbol(RubyContext/*!*/ context, object v) {
            if (Is(context, v, "wab")) {
                return OjMode.Wab;
            } else if (Is(context, v, "object")) {
                return OjMode.Object;
            } else if (Is(context, v, "strict")) {
                return OjMode.Strict;
            } else if (Is(context, v, "compat") || Is(context, v, "json")) {
                return OjMode.Compat;
            } else if (Is(context, v, "null")) {
                return OjMode.Null;
            } else if (Is(context, v, "custom")) {
                return OjMode.Custom;
            } else if (Is(context, v, "rails")) {
                return OjMode.Rails;
            }
            throw RubyExceptions.CreateArgumentError(":mode must be :object, :strict, :compat, :null, :custom, :rails, or :wab.");
        }

        /// <summary>oj_parse_options_consumed.</summary>
        internal bool Parse(RubyContext/*!*/ context, object ropts, bool isDefaults) {
            var hash = ropts as Hash;
            if (hash == null) {
                return false;
            }
            bool consumed = false;
            foreach (var pair in new List<KeyValuePair<object, object>>(hash)) {
                if (ParseOne(context, CustomStringDictionary.ObjToNull(pair.Key), pair.Value, isDefaults)) {
                    consumed = true;
                }
            }
            object ms;
            if (hash.TryGetValue(context.CreateAsciiSymbol("match_string"), out ms) && ms != null) {
                consumed = true;
            }
            ParseMatchString(context, hash);

            Use = (0 < IndentStr.Length || 0 < AfterSep.Length || 0 < BeforeSep.Length || 0 < HashNl.Length || 0 < ArrayNl.Length);
            return consumed;
        }

        /// <summary>oj_parse_opt_match_string: the patterns replace any there were.</summary>
        internal void ParseMatchString(RubyContext/*!*/ context, Hash/*!*/ ropts) {
            object v;
            if (!ropts.TryGetValue(context.CreateAsciiSymbol("match_string"), out v) || v == null) {
                return;
            }
            var hash = v as Hash;
            if (hash == null) {
                throw Sites.WrongType(context, v, "Hash");
            }
            var rx = new RxClass();
            foreach (var pair in hash) {
                object key = CustomStringDictionary.ObjToNull(pair.Key);
                if (!(pair.Value is RubyClass)) {
                    throw RubyExceptions.CreateArgumentError("for :match_string, the hash values must be a Class.");
                }
                if (key is RubyRegex) {
                    rx.Entries.Add(new KeyValuePair<object, object>(key, pair.Value));
                } else if (key is MutableString) {
                    if (((MutableString)key).GetByteCount() >= 256) {
                        throw RubyExceptions.CreateArgumentError("expressions must be less than 256 characters");
                    }
                    // Upstream compiles these with POSIX regcomp (and with Ruby's Regexp on Windows).
                    object regex = Sites.Call(context, context.GetClass(typeof(RubyRegex)), "new", key);
                    rx.Entries.Add(new KeyValuePair<object, object>(regex, pair.Value));
                } else {
                    throw RubyExceptions.CreateArgumentError("for :match_string, keys must either a String or RegExp.");
                }
            }
            StrRx = rx;
        }

        #endregion

        #region Oj.default_options

        private static object YN(char c) {
            return c == YesNo.Yes ? ScriptingRuntimeHelpers.True : (c == YesNo.No ? ScriptingRuntimeHelpers.False : null);
        }

        private static object BytesOrNil(byte[] bytes) {
            return bytes.Length == 0 ? null : MutableString.CreateBinary(bytes, RubyEncoding.Binary);
        }

        private static object OnlyArray(RubyContext/*!*/ context, string str) {
            if (str == null || str.Length <= 2) {
                return null;
            }
            var result = new RubyArray();
            foreach (var part in str.Substring(1, str.Length - 2).Split(':')) {
                result.Add(context.CreateSymbol(part, RubyEncoding.UTF8));
            }
            return result;
        }

        /// <summary>get_def_opts.</summary>
        internal Hash/*!*/ ToRubyHash(RubyContext/*!*/ context) {
            var opts = new Hash(context);
            Func<string, RubySymbol> sym = context.CreateAsciiSymbol;

            if (IndentStr.Length == 0) {
                opts[sym("indent")] = ScriptingRuntimeHelpers.Int32ToObject(Indent);
            } else {
                opts[sym("indent")] = MutableString.CreateBinary(IndentStr, RubyEncoding.Binary);
            }
            opts[sym("second_precision")] = ScriptingRuntimeHelpers.Int32ToObject(SecPrec);
            opts[sym("circular")] = YN(Circular);
            opts[sym("class_cache")] = YN(ClassCache);
            opts[sym("auto_define")] = YN(AutoDefine);
            opts[sym("symbol_keys")] = YN(SymKey);
            opts[sym("bigdecimal_as_decimal")] = YN(BigdecAsNum);
            opts[sym("create_additions")] = YN(CreateOk);
            opts[sym("use_to_json")] = YN(ToJson);
            opts[sym("use_to_hash")] = YN(ToHash);
            opts[sym("use_as_json")] = YN(AsJson);
            opts[sym("use_raw_json")] = YN(RawJson);
            opts[sym("nilnil")] = YN(Nilnil);
            opts[sym("empty_string")] = YN(EmptyString);
            opts[sym("allow_gc")] = YN(AllowGc);
            opts[sym("quirks_mode")] = YN(QuirksMode);
            opts[sym("allow_invalid_unicode")] = YN(AllowInvalid);
            opts[sym("allow_nan")] = YN(AllowNan);
            opts[sym("trace")] = YN(Trace);
            opts[sym("safe")] = YN(Safe);
            opts[sym("float_precision")] = ScriptingRuntimeHelpers.Int32ToObject(FloatPrec);
            opts[sym("float_format")] = MutableString.CreateAscii(FloatFmt);
            opts[sym("cache_str")] = ScriptingRuntimeHelpers.Int32ToObject(CacheStr);
            opts[sym("ignore_under")] = YN(IgnoreUnder);
            opts[sym("cache_keys")] = YN(CacheKeys);

            string mode;
            switch (Mode) {
                case OjMode.Strict: mode = "strict"; break;
                case OjMode.Compat: mode = "compat"; break;
                case OjMode.Null: mode = "null"; break;
                case OjMode.Custom: mode = "custom"; break;
                case OjMode.Rails: mode = "rails"; break;
                case OjMode.Wab: mode = "wab"; break;
                default: mode = "object"; break;
            }
            opts[sym("mode")] = sym(mode);

            if (IntRangeMax != 0 || IntRangeMin != 0) {
                opts[sym("integer_range")] = Sites.Call(context, context.GetClass(typeof(Range)), "new",
                    Protocols.Normalize(IntRangeMin), Protocols.Normalize(IntRangeMax));
            } else {
                opts[sym("integer_range")] = null;
            }
            opts[sym("max_integer_digits")] = Protocols.Normalize(MaxIntegerDigits);

            string esc;
            switch (EscapeMode) {
                case Esc.NL: esc = "newline"; break;
                case Esc.Slash: esc = "slash"; break;
                case Esc.XSS: esc = "xss_safe"; break;
                case Esc.ASCII: esc = "ascii"; break;
                case Esc.JX: esc = "unicode_xss"; break;
                default: esc = "json"; break;
            }
            opts[sym("escape_mode")] = sym(esc);

            string tf;
            switch (TimeFormat) {
                case 'x': tf = "xmlschema"; break;
                case 'r': tf = "ruby"; break;
                case 'z': tf = "unix_zone"; break;
                default: tf = "unix"; break;
            }
            opts[sym("time_format")] = sym(tf);

            string bl;
            switch (BigdecLoad) {
                case BigLoad.BigDec: bl = "bigdecimal"; break;
                case BigLoad.FloatDec: bl = "float"; break;
                case BigLoad.FastDec: bl = "fast"; break;
                default: bl = "auto"; break;
            }
            opts[sym("bigdecimal_load")] = sym(bl);
            opts[sym("compat_bigdecimal")] = CompatBigdec ? ScriptingRuntimeHelpers.True : ScriptingRuntimeHelpers.False;
            opts[sym("create_id")] = CreateId == null ? null : MutableString.CreateBinary(CreateId, RubyEncoding.Binary);
            opts[sym("space")] = BytesOrNil(AfterSep);
            opts[sym("space_before")] = BytesOrNil(BeforeSep);
            opts[sym("object_nl")] = BytesOrNil(HashNl);
            opts[sym("array_nl")] = BytesOrNil(ArrayNl);

            string nan;
            switch (NanDump) {
                case Nan.Null: nan = "null"; break;
                case Nan.Raise: nan = "raise"; break;
                case Nan.Word: nan = "word"; break;
                case Nan.Huge: nan = "huge"; break;
                default: nan = "auto"; break;
            }
            opts[sym("nan")] = sym(nan);
            opts[sym("omit_nil")] = OmitNil ? ScriptingRuntimeHelpers.True : ScriptingRuntimeHelpers.False;
            opts[sym("omit_null_byte")] = OmitNullByte ? ScriptingRuntimeHelpers.True : ScriptingRuntimeHelpers.False;
            opts[sym("hash_class")] = HashClass;
            opts[sym("array_class")] = ArrayClass;
            opts[sym("only")] = OnlyArray(context, Only);
            opts[sym("except")] = OnlyArray(context, Except);
            if (Ignore == null) {
                opts[sym("ignore")] = null;
            } else {
                var a = new RubyArray();
                a.AddRange(Ignore);
                opts[sym("ignore")] = a;
            }
            return opts;
        }

        #endregion
    }
}

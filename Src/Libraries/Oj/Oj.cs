/* ****************************************************************************
 *
 * IronRuby's implementation of the oj gem's C extension.
 *
 * oj 3.17.6 is a large C extension (ext/oj) under a little Ruby (lib/oj).  The Ruby
 * is vendored unchanged under Src/StdLib/ironruby/oj; this is the C, for the modes
 * most applications use Oj for - :strict, :null, :compat and :rails, dump and load -
 * and for Oj.mimic_JSON and Oj::Rails, which is how multi_json and Rails reach it.
 * The other modes (:object, :custom, :wab) and classes (Oj::Doc, Oj::StringWriter,
 * Oj::StreamWriter, Oj::Parser, the Saj and Scp callback parsers) raise
 * NotImplementedError naming themselves rather than answering differently.
 *
 * The Ruby-visible glue that upstream builds with the C API - redefining JSON's
 * methods for mimic_JSON, ActiveSupport's encoder settings for set_encoder - is
 * Ruby, in Src/StdLib/ironruby/oj/oj.rb, over the primitives here.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Oj {

    #region dynamic calls

    /// <summary>rb_funcall and friends, through call sites cached per runtime.</summary>
    internal sealed class Sites {
        private readonly RubyContext/*!*/ _context;
        private readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>> _sites0 =
            new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>>();
        private readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>> _sites1 =
            new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>>();
        private readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object, object>>> _sites2 =
            new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object, object>>>();
        private readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>> _splat =
            new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>>();

        private Sites(RubyContext/*!*/ context) {
            _context = context;
        }

        private static readonly object _key = new object();

        private static Sites/*!*/ Get(RubyContext/*!*/ context) {
            return (Sites)context.GetOrCreateLibraryData(_key, () => new Sites(context));
        }

        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name) {
            var self = Get(context);
            var site = self._sites0.GetOrAdd(name, n => CallSite<Func<CallSite, object, object>>.Create(
                RubyCallAction.Make(self._context, n, RubyCallSignature.WithImplicitSelf(0))));
            return site.Target(site, target);
        }

        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name, object arg1) {
            var self = Get(context);
            var site = self._sites1.GetOrAdd(name, n => CallSite<Func<CallSite, object, object, object>>.Create(
                RubyCallAction.Make(self._context, n, RubyCallSignature.WithImplicitSelf(1))));
            return site.Target(site, target, arg1);
        }

        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name, object arg1, object arg2) {
            var self = Get(context);
            var site = self._sites2.GetOrAdd(name, n => CallSite<Func<CallSite, object, object, object, object>>.Create(
                RubyCallAction.Make(self._context, n, RubyCallSignature.WithImplicitSelf(2))));
            return site.Target(site, target, arg1, arg2);
        }

        /// <summary>rb_funcall2 with an argument vector.</summary>
        internal static object CallN(RubyContext/*!*/ context, object target, string/*!*/ name, object[]/*!*/ args) {
            switch (args.Length) {
                case 0: return Call(context, target, name);
                case 1: return Call(context, target, name, args[0]);
                case 2: return Call(context, target, name, args[0], args[1]);
            }
            var self = Get(context);
            var site = self._splat.GetOrAdd(name, n => CallSite<Func<CallSite, object, object, object>>.Create(
                RubyCallAction.Make(self._context, n, RubyCallSignature.WithSplat(0))));
            var list = new RubyArray(args.Length);
            list.AddRange(args);
            return site.Target(site, target, list);
        }

        internal static bool RespondTo(RubyContext/*!*/ context, object target, string/*!*/ name) {
            return Protocols.IsTrue(Call(context, target, "respond_to?", context.EncodeIdentifier(name)));
        }

        internal static string/*!*/ TypeName(RubyContext/*!*/ context, object obj) {
            if (obj == null) {
                return "nil";
            }
            if (obj is bool) {
                return (bool)obj ? "true" : "false";
            }
            return context.GetClassName(obj);
        }

        internal static Exception/*!*/ WrongType(RubyContext/*!*/ context, object obj, string/*!*/ expected) {
            return RubyExceptions.CreateTypeError("wrong argument type {0} (expected {1})", TypeName(context, obj), expected);
        }

        internal static MutableString/*!*/ StringValue(RubyContext/*!*/ context, object obj) {
            var str = obj as MutableString;
            if (str != null) {
                return str;
            }
            if (obj != null && !(obj is bool) && RespondTo(context, obj, "to_str")) {
                str = Call(context, obj, "to_str") as MutableString;
                if (str != null) {
                    return str;
                }
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into String", TypeName(context, obj));
        }

        internal static int ToInt(RubyContext/*!*/ context, object obj) {
            if (obj is int) {
                return (int)obj;
            }
            if (obj is BigInteger) {
                var big = (BigInteger)obj;
                if (big >= Int32.MinValue && big <= Int32.MaxValue) {
                    return (int)big;
                }
                throw RubyExceptions.CreateRangeError("integer {0} too big to convert to 'int'", big);
            }
            if (obj is double) {
                return (int)(double)obj;
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into Integer", TypeName(context, obj));
        }

        internal static double ToDouble(RubyContext/*!*/ context, object obj) {
            if (obj is double) {
                return (double)obj;
            }
            if (obj is int) {
                return (int)obj;
            }
            if (obj is BigInteger) {
                return (double)(BigInteger)obj;
            }
            object f = Call(context, obj, "to_f");
            if (f is double) {
                return (double)f;
            }
            throw RubyExceptions.CreateTypeError("can't convert {0} into Float", TypeName(context, obj));
        }
    }

    #endregion

    /// <summary>The globals of the C extension, per runtime.</summary>
    internal sealed class OjState {
        private static readonly object _key = new object();

        internal static OjState/*!*/ Get(RubyContext/*!*/ context) {
            return (OjState)context.GetOrCreateLibraryData(_key, () => new OjState(context));
        }

        private readonly RubyContext/*!*/ _context;

        private OjState(RubyContext/*!*/ context) {
            _context = context;
        }

        internal Options/*!*/ Defaults = new Options();

        // Oj.add_to_json
        internal bool UseStructAlt, UseExceptionAlt, UseBignumAlt, UseHashAlt, UseArrayAlt;
        internal readonly HashSet<string>/*!*/ ActiveCodes = new HashSet<string>();
        internal static readonly string[] CompatCodeNames = {
            "BigDecimal", "Complex", "Date", "DateTime", "OpenStruct", "Range", "Rational", "Regexp", "Time",
        };
        private readonly Dictionary<string, object>/*!*/ _codeClasses = new Dictionary<string, object>();

        // Oj::Rails
        internal bool RailsHashOpt, RailsArrayOpt, RailsFloatOpt, StringWriterOptimized;
        internal RailsOpts/*!*/ GlobalRopts = new RailsOpts();
        internal bool EscapeHtml = true;
        internal bool XmlTime = true;
        internal object ActiveRecordBase;

        // mimic_JSON
        internal object JsonParserErrorOverride;
        internal object JsonGeneratorErrorOverride;
        internal object StateClass;

        // oj_class_intern's cache: a class name resolved once, hit or miss
        internal readonly Dictionary<string, object>/*!*/ ClassCache = new Dictionary<string, object>();

        internal static Exception/*!*/ NotImplemented(string/*!*/ what) {
            return RubyExceptions.CreateNotImplementedError(what + " is not implemented on IronRuby");
        }

        /// <summary>rb_raise(clas, "%s", message): an instance of clas made with clas.new(message).</summary>
        internal Exception/*!*/ NewException(object clas, object message) {
            object msg = message is string ? MutableString.Create((string)message, RubyEncoding.UTF8) : message;
            var result = Sites.Call(_context, clas, "new", msg) as Exception;
            if (result == null) {
                return RubyExceptions.CreateRuntimeError(message.ToString());
            }
            return result;
        }

        #region classes

        internal static object Constant(RubyContext/*!*/ context, RubyModule/*!*/ module, string/*!*/ name) {
            object value;
            return module.TryGetConstant(null, name, out value) ? value : null;
        }

        internal static object BigDecimalClass(RubyContext/*!*/ context) {
            return Constant(context, context.ObjectClass, "BigDecimal");
        }

        internal object OjParseErrorClass(RubyContext/*!*/ context) {
            var oj = Constant(context, context.ObjectClass, "Oj") as RubyModule;
            return oj != null ? Constant(context, oj, "ParseError") : context.StandardErrorClass;
        }

        internal object JsonParserErrorClass(RubyContext/*!*/ context) {
            return JsonParserErrorOverride ?? context.GetClass(typeof(EncodingError));
        }

        internal object JsonGeneratorErrorClass(RubyContext/*!*/ context) {
            return JsonGeneratorErrorOverride ?? context.GetClass(typeof(EncodingError));
        }

        /// <summary>oj_get_json_err_class: defines JSON and the class under it when missing.</summary>
        internal object JsonErrorClass(RubyContext/*!*/ context, string/*!*/ name) {
            return Sites.Call(context, Constant(context, context.ObjectClass, "Oj"), "__json_error_class",
                MutableString.CreateAscii(name));
        }

        internal RubyClass StringIOClass(RubyContext/*!*/ context) {
            return Constant(context, context.ObjectClass, "StringIO") as RubyClass;
        }

        internal bool IsComplex(RubyContext/*!*/ context, RubyClass/*!*/ cls) {
            return ReferenceEquals(cls, Constant(context, context.ObjectClass, "Complex"));
        }

        internal bool IsRational(RubyContext/*!*/ context, RubyClass/*!*/ cls) {
            return ReferenceEquals(cls, Constant(context, context.ObjectClass, "Rational"));
        }

        /// <summary>code.c's path2class, remembered: a class not defined the first time stays undefined.</summary>
        internal object ResolveCodeClass(RubyContext/*!*/ context, string/*!*/ name) {
            object cls;
            if (!_codeClasses.TryGetValue(name, out cls)) {
                cls = OjRails.ResolveClassPath(context, name) ?? ParseInfo.Undef;
                _codeClasses[name] = cls;
            }
            return cls;
        }

        internal void SetCodeClass(string/*!*/ name, object cls) {
            _codeClasses[name] = cls;
        }

        internal static int MethodArity(RubyContext/*!*/ context, object obj, string/*!*/ name) {
            object method = Sites.Call(context, obj, "method", context.EncodeIdentifier(name));
            object arity = Sites.Call(context, method, "arity");
            return arity is int ? (int)arity : -1;
        }

        /// <summary>oj_name2class for the :compat create_id: resolve.c's resolve_classpath, through intern.c's cache.</summary>
        internal object NameToClass(ParseInfo/*!*/ pi, byte[]/*!*/ name, object errorClass) {
            RubyContext context = pi.Context;
            string sname = Encoding.UTF8.GetString(name);
            bool cached = YesNo.No != pi.Opts.ClassCache;
            object result;
            if (cached && ClassCache.TryGetValue(sname, out result)) {
                return result;
            }

            result = context.ObjectClass;
            string[] parts = sname.Split(new[] { "::" }, StringSplitOptions.None);
            bool bad = false;
            for (int i = 0; i < parts.Length; i++) {
                string part = parts[i];
                if (part.IndexOf(':') >= 0 || (part.Length == 0 && i > 0 && i < parts.Length - 1)) {
                    bad = true;
                    break;
                }
                object value;
                var module = (RubyModule)result;
                if (part.Length > 0 && module.TryGetConstant(null, part, out value) && value is RubyModule) {
                    result = value;
                } else {
                    if (i < parts.Length - 1) {
                        bad = true;
                        break;
                    }
                    string shown = sname.Length >= 1024 ? sname.Substring(0, 1023) : sname;
                    OjParse.SetErrorAt(pi, errorClass, cached ? "intern.c" : "resolve.c", cached ? 238 : 67,
                        String.Format("class '{0}' is not defined", shown));
                    if (errorClass != null) {
                        pi.ErrClass = errorClass;
                    }
                    result = ParseInfo.Undef;
                    break;
                }
            }
            if (bad) {
                result = ParseInfo.Undef;
            }
            if (cached) {
                ClassCache[sname] = result;
            }
            return result;
        }

        #endregion
    }

    [RubyModule("Oj")]
    public static class OjOps {

        private static OjState/*!*/ State(RubyContext/*!*/ context) {
            return OjState.Get(context);
        }

        #region default options

        [RubyMethod("default_options", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ GetDefaultOptions(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            return State(context).Defaults.ToRubyHash(context);
        }

        [RubyMethod("default_options=", RubyMethodAttributes.PublicSingleton)]
        public static object SetDefaultOptions(RubyContext/*!*/ context, RubyModule/*!*/ self, object opts) {
            if (!(opts is Hash)) {
                throw Sites.WrongType(context, opts, "Hash");
            }
            State(context).Defaults.Parse(context, opts, true);
            return null;
        }

        #endregion

        #region load

        private static ParseInfo/*!*/ NewParseInfo(RubyContext/*!*/ context, bool compat) {
            var pi = new ParseInfo(context, State(context).Defaults.Clone());
            pi.Compat = compat;
            return pi;
        }

        private static object StrictParse(RubyContext/*!*/ context, BlockParam block, object[]/*!*/ args) {
            var pi = NewParseInfo(context, false);
            return OjParse.Parse(pi, args, block, true);
        }

        private static object CompatParse(RubyContext/*!*/ context, object[]/*!*/ args, bool emptyString) {
            var pi = NewParseInfo(context, true);
            pi.MaxDepth = 0;
            pi.Opts.AllowNan = YesNo.Yes;
            pi.Opts.Nilnil = YesNo.Yes;
            pi.Opts.EmptyString = emptyString ? YesNo.Yes : YesNo.No;
            return OjParse.Parse(pi, args, null, false);
        }

        private static char LoadMode(RubyContext/*!*/ context, object[]/*!*/ args, bool checkNil) {
            char mode = State(context).Defaults.Mode;
            if (2 <= args.Length) {
                object ropts = args[1];
                if (!checkNil || ropts != null || OjMode.Compat != mode) {
                    var hash = ropts as Hash;
                    if (hash == null) {
                        throw Sites.WrongType(context, ropts, "Hash");
                    }
                    object v;
                    if (hash.TryGetValue(context.CreateAsciiSymbol("mode"), out v) && v != null) {
                        mode = Options.ModeFromSymbol(context, v);
                    }
                }
            }
            return mode;
        }

        [RubyMethod("load", RubyMethodAttributes.PublicSingleton)]
        public static object Load(RubyContext/*!*/ context, BlockParam block, RubyModule/*!*/ self, params object[]/*!*/ args) {
            if (1 > args.Length) {
                throw RubyExceptions.CreateArgumentError("Wrong number of arguments to load().");
            }
            switch (LoadMode(context, args, true)) {
                case OjMode.Strict:
                case OjMode.Null:
                    return StrictParse(context, block, args);
                case OjMode.Compat:
                case OjMode.Rails:
                    return CompatParse(context, args, false);
                case OjMode.Custom:
                    throw OjState.NotImplemented("Oj :custom mode");
                case OjMode.Wab:
                    throw OjState.NotImplemented("Oj :wab mode");
            }
            throw OjState.NotImplemented("Oj :object mode");
        }

        [RubyMethod("load_file", RubyMethodAttributes.PublicSingleton)]
        public static object LoadFile(RubyContext/*!*/ context, BlockParam block, RubyModule/*!*/ self, params object[]/*!*/ args) {
            if (1 > args.Length) {
                throw RubyExceptions.CreateArgumentError("Wrong number of arguments to load().");
            }
            string path = Sites.StringValue(context, args[0]).ToString();
            char mode = LoadMode(context, args, false);
            MutableString content;
            try {
                content = MutableString.CreateBinary(File.ReadAllBytes(path), RubyEncoding.UTF8);
            } catch (FileNotFoundException) {
                throw RubyExceptions.CreateENOENT(path);
            } catch (DirectoryNotFoundException) {
                throw RubyExceptions.CreateENOENT(path);
            }
            var newArgs = (object[])args.Clone();
            newArgs[0] = content;
            ParseInfo pi;
            switch (mode) {
                case OjMode.Strict:
                case OjMode.Null:
                    pi = NewParseInfo(context, false);
                    break;
                case OjMode.Compat:
                case OjMode.Rails:
                    pi = NewParseInfo(context, true);
                    break;
                case OjMode.Custom:
                    throw OjState.NotImplemented("Oj :custom mode");
                case OjMode.Wab:
                    throw OjState.NotImplemented("Oj :wab mode");
                default:
                    throw OjState.NotImplemented("Oj :object mode");
            }
            pi.MaxDepth = 0;
            return OjParse.Parse(pi, newArgs, block, true);
        }

        [RubyMethod("safe_load", RubyMethodAttributes.PublicSingleton)]
        public static object SafeLoad(RubyContext/*!*/ context, RubyModule/*!*/ self, object doc) {
            var pi = NewParseInfo(context, false);
            pi.MaxDepth = 0;
            pi.Opts.AutoDefine = YesNo.No;
            pi.Opts.SymKey = YesNo.No;
            pi.Opts.Mode = OjMode.Strict;
            return OjParse.Parse(pi, new[] { doc }, null, true);
        }

        [RubyMethod("strict_load", RubyMethodAttributes.PublicSingleton)]
        public static object StrictLoad(RubyContext/*!*/ context, BlockParam block, RubyModule/*!*/ self, params object[]/*!*/ args) {
            if (1 > args.Length) {
                throw RubyExceptions.CreateArgumentError("Wrong number of arguments to parse.");
            }
            return StrictParse(context, block, args);
        }

        [RubyMethod("compat_load", RubyMethodAttributes.PublicSingleton)]
        public static object CompatLoad(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
            if (1 > args.Length) {
                throw RubyExceptions.CreateArgumentError("Wrong number of arguments to parse.");
            }
            return CompatParse(context, args, false);
        }

        [RubyMethod("object_load", RubyMethodAttributes.PublicSingleton)]
        public static object ObjectLoad(RubyModule/*!*/ self, params object[]/*!*/ args) {
            throw OjState.NotImplemented("Oj.object_load (:object mode)");
        }

        [RubyMethod("wab_load", RubyMethodAttributes.PublicSingleton)]
        public static object WabLoad(RubyModule/*!*/ self, params object[]/*!*/ args) {
            throw OjState.NotImplemented("Oj.wab_load (:wab mode)");
        }

        #endregion

        #region dump

        internal static MutableString/*!*/ DumpToString(RubyContext/*!*/ context, object obj, Options/*!*/ copts, object[]/*!*/ argv) {
            var output = new Out(context, copts);
            output.OmitNil = copts.OmitNil;
            output.OmitNullByte = copts.OmitNullByte;
            OjDump.DumpObjToJson(output, obj, copts, argv);
            return output.ToUtf8CString();
        }

        private static object[]/*!*/ Rest(object[]/*!*/ args, int from) {
            if (args.Length <= from) {
                return new object[0];
            }
            var result = new object[args.Length - from];
            Array.Copy(args, from, result, 0, result.Length);
            return result;
        }

        [RubyMethod("dump", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Dump(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
            if (1 > args.Length) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (0 for 1).");
            }
            Options copts = State(context).Defaults.Clone();
            if (OjMode.Compat == copts.Mode) {
                copts.NanDump = Nan.Word;
            }
            if (2 == args.Length) {
                copts.Parse(context, args[1], false);
            }
            if (OjMode.Compat == copts.Mode && copts.EscapeMode != Esc.ASCII) {
                copts.EscapeMode = Esc.JSON;
            }
            return DumpToString(context, args[0], copts, Rest(args, 1));
        }

        /// <summary>oj_parse_mimic_dump_options.</summary>
        internal static void ParseMimicDumpOptions(RubyContext/*!*/ context, object ropts, Options/*!*/ copts) {
            var hash = ropts as Hash;
            if (hash == null) {
                if (ropts != null && Sites.RespondTo(context, ropts, "to_hash")) {
                    hash = Sites.Call(context, ropts, "to_hash") as Hash;
                } else if (ropts != null && Sites.RespondTo(context, ropts, "to_h")) {
                    hash = Sites.Call(context, ropts, "to_h") as Hash;
                } else if (ropts == null) {
                    return;
                } else {
                    throw RubyExceptions.CreateArgumentError("options must be a hash.");
                }
                if (hash == null) {
                    throw RubyExceptions.CreateArgumentError("options must be a hash.");
                }
            }
            Func<string, object> get = name => {
                object value;
                return hash.TryGetValue(context.CreateAsciiSymbol(name), out value) ? value : null;
            };
            object v = get("max_nesting");
            if (v is bool && (bool)v) {
                copts.MaxDepth = 100;
            } else if (v == null || v is bool) {
                copts.MaxDepth = OjDump.MaxDepth;
            } else if (v is int) {
                copts.MaxDepth = (int)v;
                if (0 >= copts.MaxDepth) {
                    copts.MaxDepth = OjDump.MaxDepth;
                }
            }
            if (null != (v = get("allow_nan"))) {
                copts.NanDump = (v is bool && (bool)v) ? Nan.Word : Nan.Raise;
            }
            if (null != (v = get("indent"))) {
                copts.IndentStr = MimicBytes(context, v, "indent");
                copts.Use = true;
            }
            if (null != (v = get("space"))) {
                copts.AfterSep = MimicBytes(context, v, "space");
                copts.Use = true;
            }
            if (null != (v = get("space_before"))) {
                copts.BeforeSep = MimicBytes(context, v, "space_before");
                copts.Use = true;
            }
            if (null != (v = get("object_nl"))) {
                copts.HashNl = MimicBytes(context, v, "object_nl");
                copts.Use = true;
            }
            if (null != (v = get("array_nl"))) {
                copts.ArrayNl = MimicBytes(context, v, "array_nl");
                copts.Use = true;
            }
            if (null != (v = get("quirks_mode"))) {
                copts.QuirksMode = (v is bool && (bool)v) ? YesNo.Yes : YesNo.No;
            }
            if (null != (v = get("ascii_only"))) {
                // generate seems to take anything but nil and false as true
                copts.EscapeMode = (v is bool && !(bool)v) ? Esc.JX : Esc.ASCII;
            }
        }

        private static byte[]/*!*/ MimicBytes(RubyContext/*!*/ context, object v, string/*!*/ what) {
            var str = v as MutableString;
            if (str == null) {
                throw Sites.WrongType(context, v, "String");
            }
            byte[] bytes = str.ToByteArray();
            if (16 <= bytes.Length) {
                throw RubyExceptions.CreateArgumentError(String.Format("{0} string is limited to 16 characters.", what));
            }
            int nul = Array.IndexOf(bytes, (byte)0);
            if (nul >= 0) {
                Array.Resize(ref bytes, nul);
            }
            return bytes;
        }

        [RubyMethod("to_json", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ ToJson(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
            if (1 > args.Length) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (0 for 1).");
            }
            Options copts = State(context).Defaults.Clone();
            copts.EscapeMode = Esc.JX;
            copts.NanDump = Nan.Raise;
            if (2 == args.Length) {
                ParseMimicDumpOptions(context, args[1], copts);
            }
            copts.Mode = OjMode.Compat;
            copts.ToJson = YesNo.Yes;
            return DumpToString(context, args[0], copts, Rest(args, 1));
        }

        private static byte[]/*!*/ DumpBytes(RubyContext/*!*/ context, object obj, Options/*!*/ copts) {
            var output = new Out(context, copts);
            output.OmitNil = copts.OmitNil;
            OjDump.DumpObjToJson(output, obj, copts, new object[0]);
            var bytes = new byte[output.Cur];
            Buffer.BlockCopy(output.Buf, 0, bytes, 0, output.Cur);
            return bytes;
        }

        [RubyMethod("to_file", RubyMethodAttributes.PublicSingleton)]
        public static object ToFile(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
            Options copts = State(context).Defaults.Clone();
            if (3 == args.Length) {
                copts.Parse(context, args[2], false);
            }
            string path = Sites.StringValue(context, args[0]).ToString();
            byte[] bytes = DumpBytes(context, args[1], copts);
            try {
                File.WriteAllBytes(path, bytes);
            } catch (IOException e) {
                throw RubyExceptions.CreateIOError(e.Message);
            } catch (UnauthorizedAccessException e) {
                throw RubyExceptions.CreateIOError(e.Message);
            }
            return null;
        }

        [RubyMethod("to_stream", RubyMethodAttributes.PublicSingleton)]
        public static object ToStream(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
            Options copts = State(context).Defaults.Clone();
            if (3 == args.Length) {
                copts.Parse(context, args[2], false);
            }
            byte[] bytes = DumpBytes(context, args[1], copts);
            object stream = args[0];
            if (Sites.RespondTo(context, stream, "write")) {
                Sites.Call(context, stream, "write", MutableString.CreateBinary(bytes, RubyEncoding.Binary));
            } else {
                throw RubyExceptions.CreateArgumentError("to_stream() expected an IO Object.");
            }
            return null;
        }

        #endregion

        #region generate (JSON gem compatibility)

        internal static MutableString/*!*/ MimicGenerateCore(RubyContext/*!*/ context, object[]/*!*/ args, Options/*!*/ copts) {
            if (0 == args.Length) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (0))");
            }
            OjState state = State(context);
            copts.NanDump = Nan.Raise;
            copts.Mode = OjMode.Compat;
            copts.ToJson = YesNo.Yes;
            if (2 == args.Length && args[1] != null) {
                ParseMimicDumpOptions(context, args[1], copts);
            }
            object[] argv;
            if (1 < args.Length) {
                argv = Rest(args, 1);
            } else {
                if (state.StateClass == null) {
                    context.ReportWarning("Oj::Rails.mimic_JSON was called implicitly. " +
                        "Call it explicitly beforehand if you want to remove this warning.");
                    Sites.Call(context, OjState.Constant(context, context.ObjectClass, "Oj"), "mimic_JSON");
                }
                argv = new[] { Sites.Call(context, state.StateClass, "new") };
            }
            var output = new Out(context, copts);
            output.OmitNil = copts.OmitNil;
            OjDump.DumpObjToJson(output, args[0], copts, argv);
            return output.ToUtf8CString();
        }

        [RubyMethod("generate", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("fast_generate", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("__mimic_generate", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Generate(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
            Options copts = State(context).Defaults.Clone();
            copts.StrRx = new RxClass();
            return MimicGenerateCore(context, args, copts);
        }

        [RubyMethod("__mimic_pretty_generate", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ PrettyGenerate(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
            OjState state = State(context);
            if (0 == args.Length) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (0))");
            }
            Hash h;
            if (1 == args.Length || args[1] == null) {
                h = new Hash(context);
            } else {
                h = args[1] as Hash;
                if (h == null) {
                    throw RubyExceptions.CreateTypeError(String.Format("wrong argument type {0} (expected Hash)", Sites.TypeName(context, args[1])));
                }
            }
            Action<string, string> def = (name, value) => {
                RubySymbol key = context.CreateAsciiSymbol(name);
                if (!h.ContainsKey(key)) {
                    h[key] = MutableString.CreateAscii(value);
                }
            };
            def("indent", "  ");
            def("space_before", "");
            def("space", " ");
            def("object_nl", "\n");
            def("array_nl", "\n");
            if (state.StateClass == null) {
                Sites.Call(context, OjState.Constant(context, context.ObjectClass, "Oj"), "mimic_JSON");
            }
            object stateObj = Sites.Call(context, state.StateClass, "new", h);

            Options copts = state.Defaults.Clone();
            copts.StrRx = new RxClass();
            copts.IndentStr = Encoding.ASCII.GetBytes("  ");
            copts.BeforeSep = Options.Empty;
            copts.AfterSep = Encoding.ASCII.GetBytes(" ");
            copts.HashNl = Encoding.ASCII.GetBytes("\n");
            copts.ArrayNl = Encoding.ASCII.GetBytes("\n");
            copts.Use = true;
            return MimicGenerateCore(context, new[] { args[0], stateObj }, copts);
        }

        #endregion

        #region add_to_json

        [RubyMethod("add_to_json", RubyMethodAttributes.PublicSingleton)]
        public static object AddToJson(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
            SetToJson(context, args, true);
            return null;
        }

        [RubyMethod("remove_to_json", RubyMethodAttributes.PublicSingleton)]
        public static object RemoveToJson(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
            SetToJson(context, args, false);
            return null;
        }

        private static void SetToJson(RubyContext/*!*/ context, object[]/*!*/ args, bool on) {
            OjState state = State(context);
            if (0 == args.Length) {
                foreach (string name in OjState.CompatCodeNames) {
                    if (on) {
                        object cls;
                        if (!context.ObjectClass.TryGetConstant(null, name, out cls)) {
                            throw RubyExceptions.CreateNameError(String.Format("uninitialized constant {0}", name));
                        }
                        state.SetCodeClass(name, cls);
                        state.ActiveCodes.Add(name);
                    } else {
                        state.ActiveCodes.Remove(name);
                    }
                }
                state.UseStructAlt = on;
                state.UseExceptionAlt = on;
                state.UseBignumAlt = on;
                state.UseHashAlt = on;
                state.UseArrayAlt = on;
                return;
            }
            foreach (object arg in args) {
                if (ReferenceEquals(arg, context.GetClass(typeof(RubyStruct)))) {
                    state.UseStructAlt = on;
                } else if (ReferenceEquals(arg, context.ExceptionClass)) {
                    state.UseExceptionAlt = on;
                } else if (ReferenceEquals(arg, context.IntegerClass)) {
                    state.UseBignumAlt = on;
                } else if (ReferenceEquals(arg, context.GetClass(typeof(Hash)))) {
                    state.UseHashAlt = on;
                } else if (ReferenceEquals(arg, context.GetClass(typeof(RubyArray)))) {
                    state.UseArrayAlt = on;
                } else {
                    foreach (string name in OjState.CompatCodeNames) {
                        object cls;
                        if (on) {
                            if (!context.ObjectClass.TryGetConstant(null, name, out cls)) {
                                throw RubyExceptions.CreateNameError(String.Format("uninitialized constant {0}", name));
                            }
                            state.SetCodeClass(name, cls);
                        } else {
                            cls = state.ResolveCodeClass(context, name);
                        }
                        if (ReferenceEquals(arg, cls)) {
                            if (on) {
                                state.ActiveCodes.Add(name);
                            } else {
                                state.ActiveCodes.Remove(name);
                            }
                            break;
                        }
                    }
                }
            }
        }

        #endregion

        #region not implemented here

        [RubyMethod("register_odd", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("register_odd_raw", RubyMethodAttributes.PublicSingleton)]
        public static object RegisterOdd(RubyModule/*!*/ self, params object[]/*!*/ args) {
            throw OjState.NotImplemented("Oj.register_odd (:object and :custom mode)");
        }

        [RubyMethod("saj_parse", RubyMethodAttributes.PublicSingleton)]
        public static object SajParse(RubyModule/*!*/ self, params object[]/*!*/ args) {
            throw OjState.NotImplemented("Oj.saj_parse (the Saj callback parser)");
        }

        [RubyMethod("sc_parse", RubyMethodAttributes.PublicSingleton)]
        public static object ScParse(RubyModule/*!*/ self, params object[]/*!*/ args) {
            throw OjState.NotImplemented("Oj.sc_parse (the Scp callback parser)");
        }

        [RubyMethod("mem_report", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("debug_odd", RubyMethodAttributes.PublicSingleton)]
        public static object MemReport(RubyModule/*!*/ self, params object[]/*!*/ args) {
            return null;
        }

        #endregion

        #region mimic JSON primitives (the Ruby half is oj/oj.rb)

        private static int MimicLimitArg(object a) {
            if (a == null || !(a is int)) {
                return -1;
            }
            return (int)a;
        }

        [RubyMethod("__mimic_dump", RubyMethodAttributes.PublicSingleton)]
        public static object MimicDump(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
            OjState state = State(context);
            Options copts = state.Defaults.Clone();
            copts.StrRx = new RxClass();
            copts.EscapeMode = Esc.JX;
            copts.Mode = OjMode.Compat;
            copts.MaxDepth = OjDump.MaxDepth;
            if (2 <= args.Length) {
                int limit;
                if (0 <= (limit = MimicLimitArg(args[1]))) {
                    copts.MaxDepth = limit;
                }
                if (3 <= args.Length && 0 <= (limit = MimicLimitArg(args[2]))) {
                    copts.MaxDepth = limit;
                }
            }
            // ActiveSupport's to_json checks whether it was handed a ::JSON::State to decide
            // between the json gem's code and its own; handing it one takes the former.
            object activeHack = Sites.Call(context, state.StateClass, "new");
            var output = new Out(context, copts);
            output.OmitNil = copts.OmitNil;
            OjDump.DumpObjToJson(output, args[0], copts, new[] { activeHack });
            MutableString rstr = output.ToUtf8CString();
            if (2 <= args.Length && args[1] != null && Sites.RespondTo(context, args[1], "write")) {
                Sites.Call(context, args[1], "write", rstr);
                return args[1];
            }
            return rstr;
        }

        private static void MimicWalk(RubyContext/*!*/ context, object obj, object proc, BlockParam block) {
            var hash = obj as Hash;
            if (hash != null) {
                foreach (var pair in OjDump.Snapshot(hash)) {
                    MimicWalk(context, pair.Value, proc, block);
                }
            } else {
                var array = obj as RubyArray;
                if (array != null) {
                    for (int i = 0; i < array.Count; i++) {
                        MimicWalk(context, array[i], proc, block);
                    }
                }
            }
            if (proc == null) {
                if (block != null) {
                    object result;
                    block.Yield(obj, out result);
                }
            } else {
                ((Proc)proc).Call(null, obj);
            }
        }

        [RubyMethod("__mimic_load", RubyMethodAttributes.PublicSingleton)]
        public static object MimicLoad(RubyContext/*!*/ context, BlockParam block, RubyModule/*!*/ self, params object[]/*!*/ args) {
            if (1 > args.Length) {
                throw RubyExceptions.CreateArgumentError("Wrong number of arguments to parse.");
            }
            object obj = CompatParse(context, args, true);
            object p = null;
            if (2 <= args.Length) {
                if (args[1] is Proc && ReferenceEquals(context.GetClassOf(args[1]), context.GetClass(typeof(Proc)))) {
                    p = args[1];
                } else if (3 <= args.Length) {
                    if (args[2] is Proc && ReferenceEquals(context.GetClassOf(args[2]), context.GetClass(typeof(Proc)))) {
                        p = args[2];
                    }
                }
            }
            MimicWalk(context, obj, p, block);
            return obj;
        }

        [RubyMethod("__mimic_dump_load", RubyMethodAttributes.PublicSingleton)]
        public static object MimicDumpLoad(RubyContext/*!*/ context, BlockParam block, RubyModule/*!*/ self, params object[]/*!*/ args) {
            if (1 > args.Length) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (0 for 1)");
            }
            if (args[0] is MutableString) {
                return MimicLoad(context, block, self, args);
            }
            return MimicDump(context, self, args);
        }

        [RubyMethod("__mimic_parse", RubyMethodAttributes.PublicSingleton)]
        public static object MimicParse(RubyContext/*!*/ context, RubyModule/*!*/ self, object bang, params object[]/*!*/ args) {
            OjState state = State(context);
            if (args.Length < 1 || args.Length > 2) {
                throw RubyExceptions.CreateArgumentError(String.Format("wrong number of arguments (given {0}, expected 1..2)", args.Length));
            }
            object ropts = args.Length > 1 ? args[1] : null;
            var pi = new ParseInfo(context, state.Defaults.Clone());
            pi.Compat = true;
            pi.ErrClass = state.JsonParserErrorClass(context);
            pi.Opts.AutoDefine = YesNo.No;
            pi.Opts.QuirksMode = YesNo.Yes;
            pi.Opts.AllowInvalid = YesNo.Yes;
            pi.Opts.EmptyString = YesNo.No;
            pi.Opts.CreateOk = YesNo.No;
            pi.Opts.AllowNan = Protocols.IsTrue(bang) ? YesNo.Yes : YesNo.No;
            pi.Opts.Nilnil = YesNo.No;
            pi.Opts.BigdecLoad = BigLoad.RubyDec;
            pi.Opts.Mode = OjMode.Compat;
            pi.MaxDepth = 100;

            if (ropts != null) {
                var hash = ropts as Hash;
                if (hash == null) {
                    throw RubyExceptions.CreateArgumentError("options must be a hash.");
                }
                foreach (var pair in OjDump.Snapshot(hash)) {
                    var k = CustomStringDictionary.ObjToNull(pair.Key) as RubySymbol;
                    object v = pair.Value;
                    if (k == null) {
                        continue;
                    }
                    switch (k.ToString()) {
                        case "symbolize_names": pi.Opts.SymKey = (v is bool && (bool)v) ? YesNo.Yes : YesNo.No; break;
                        case "quirks_mode": pi.Opts.QuirksMode = (v is bool && (bool)v) ? YesNo.Yes : YesNo.No; break;
                        case "create_additions": pi.Opts.CreateOk = (v is bool && (bool)v) ? YesNo.Yes : YesNo.No; break;
                        case "allow_nan": pi.Opts.AllowNan = (v is bool && (bool)v) ? YesNo.Yes : YesNo.No; break;
                        case "hash_class":
                        case "object_class":
                            if (v == null) {
                                pi.Opts.HashClass = null;
                            } else {
                                if (!(v is RubyClass)) {
                                    throw Sites.WrongType(context, v, "Class");
                                }
                                pi.Opts.HashClass = v;
                            }
                            break;
                        case "array_class":
                            if (v == null) {
                                pi.Opts.ArrayClass = null;
                            } else {
                                if (!(v is RubyClass)) {
                                    throw Sites.WrongType(context, v, "Class");
                                }
                                pi.Opts.ArrayClass = v;
                            }
                            break;
                        case "decimal_class":
                            pi.Opts.CompatBigdec = ReferenceEquals(v, OjState.BigDecimalClass(context));
                            break;
                    }
                }
                object mn;
                hash.TryGetValue(context.CreateAsciiSymbol("max_nesting"), out mn);
                if (mn is bool && (bool)mn) {
                    pi.MaxDepth = 100;
                } else if (mn == null || mn is bool) {
                    pi.MaxDepth = 0;
                } else if (mn is int) {
                    pi.MaxDepth = (int)mn;
                }
                pi.Opts.ParseMatchString(context, hash);
                if (YesNo.Yes == pi.Opts.CreateOk && YesNo.Yes == pi.Opts.SymKey) {
                    throw RubyExceptions.CreateArgumentError(":symbolize_names and :create_additions can not both be true.");
                }
            }
            return OjParse.Parse(pi, new[] { args[0] }, null, false);
        }

        [RubyMethod("__mimic_recurse_proc", RubyMethodAttributes.PublicSingleton)]
        public static object MimicRecurseProc(RubyContext/*!*/ context, BlockParam block, RubyModule/*!*/ self, object obj) {
            if (block == null) {
                throw RubyExceptions.NoBlockGiven();
            }
            MimicWalk(context, obj, null, block);
            return null;
        }

        [RubyMethod("__mimic_set_create_id", RubyMethodAttributes.PublicSingleton)]
        public static object MimicSetCreateId(RubyContext/*!*/ context, RubyModule/*!*/ self, object id) {
            OjState state = State(context);
            if (id == null) {
                state.Defaults.CreateId = null;
            } else {
                byte[] bytes = Sites.StringValue(context, id).ToByteArray();
                if (Array.IndexOf(bytes, (byte)0) >= 0) {
                    throw RubyExceptions.CreateArgumentError("string contains null byte");
                }
                state.Defaults.CreateId = bytes;
            }
            return id;
        }

        [RubyMethod("__mimic_create_id", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ MimicCreateId(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            byte[] id = State(context).Defaults.CreateId;
            if (id != null) {
                return MutableString.CreateBinary(id, RubyEncoding.UTF8);
            }
            return MutableString.CreateAscii("json_class");
        }

        [RubyMethod("__mimic_object_to_json", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ MimicObjectToJson(RubyContext/*!*/ context, RubyModule/*!*/ self, object obj, params object[]/*!*/ args) {
            Options copts = State(context).Defaults.Clone();
            copts.StrRx = new RxClass();
            copts.Mode = OjMode.Compat;
            copts.ToJson = YesNo.No;
            if (1 <= args.Length && args[0] != null) {
                ParseMimicDumpOptions(context, args[0], copts);
            }
            var output = new Out(context, copts);
            output.OmitNil = copts.OmitNil;
            OjDump.DumpObjToJson(output, obj, copts, args);
            return output.ToUtf8CString();
        }

        [RubyMethod("__mimic_state", RubyMethodAttributes.PublicSingleton)]
        public static object MimicState(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            return State(context).StateClass;
        }

        [RubyMethod("__set_state_class", RubyMethodAttributes.PublicSingleton)]
        public static object SetStateClass(RubyContext/*!*/ context, RubyModule/*!*/ self, object cls) {
            State(context).StateClass = cls;
            return null;
        }

        [RubyMethod("__set_json_error_classes", RubyMethodAttributes.PublicSingleton)]
        public static object SetJsonErrorClasses(RubyContext/*!*/ context, RubyModule/*!*/ self, object parserError, object generatorError) {
            OjState state = State(context);
            if (parserError != null) {
                state.JsonParserErrorOverride = parserError;
            }
            if (generatorError != null) {
                state.JsonGeneratorErrorOverride = generatorError;
            }
            return null;
        }

        [RubyMethod("__set_mimic_defaults", RubyMethodAttributes.PublicSingleton)]
        public static object SetMimicDefaults(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            OjState state = State(context);
            state.Defaults = Options.MimicObjectToJson();
            state.Defaults.ToJson = YesNo.Yes;
            return null;
        }

        #endregion

        #region Oj::Rails

        [RubyModule("Rails")]
        public static class RailsOps {

            [RubyMethod("encode", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Encode(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
                if (1 > args.Length) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments (0 for 1).");
                }
                return OjRails.Encode(context, args[0], null, State(context).Defaults, Rest(args, 1));
            }

            [RubyMethod("optimize", RubyMethodAttributes.PublicSingleton)]
            public static object Optimize(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
                OjState state = State(context);
                OjRails.Optimize(context, args, state.GlobalRopts, true);
                state.StringWriterOptimized = true;
                return null;
            }

            [RubyMethod("deoptimize", RubyMethodAttributes.PublicSingleton)]
            public static object Deoptimize(RubyContext/*!*/ context, RubyModule/*!*/ self, params object[]/*!*/ args) {
                OjState state = State(context);
                OjRails.Optimize(context, args, state.GlobalRopts, false);
                state.StringWriterOptimized = false;
                return null;
            }

            [RubyMethod("optimized?", RubyMethodAttributes.PublicSingleton)]
            public static bool IsOptimized(RubyContext/*!*/ context, RubyModule/*!*/ self, object clas) {
                ROpt ro = State(context).GlobalRopts.Get(clas);
                return ro != null && ro.On;
            }

            [RubyMethod("__set_escape_html", RubyMethodAttributes.PublicSingleton)]
            public static object SetEscapeHtml(RubyContext/*!*/ context, RubyModule/*!*/ self, object state) {
                State(context).EscapeHtml = state is bool && (bool)state;
                return state;
            }

            [RubyMethod("__escape_html", RubyMethodAttributes.PublicSingleton)]
            public static bool GetEscapeHtml(RubyContext/*!*/ context, RubyModule/*!*/ self) {
                return State(context).EscapeHtml;
            }

            [RubyMethod("__set_xml_time", RubyMethodAttributes.PublicSingleton)]
            public static object SetXmlTime(RubyContext/*!*/ context, RubyModule/*!*/ self, object state) {
                State(context).XmlTime = state is bool && (bool)state;
                return state;
            }

            [RubyMethod("__xml_time", RubyMethodAttributes.PublicSingleton)]
            public static bool GetXmlTime(RubyContext/*!*/ context, RubyModule/*!*/ self) {
                return State(context).XmlTime;
            }

            [RubyMethod("__set_time_precision", RubyMethodAttributes.PublicSingleton)]
            public static object SetTimePrecision(RubyContext/*!*/ context, RubyModule/*!*/ self, object prec) {
                OjState state = State(context);
                state.Defaults.SecPrec = Sites.ToInt(context, prec);
                state.Defaults.SecPrecSet = true;
                return prec;
            }

            /// <summary>Oj::Rails::Encoder, ActiveSupport's json_encoder.</summary>
            [RubyClass("Encoder")]
            public sealed class Encoder : RubyObject {
                internal RailsOpts Ropts;       // null: follows the global table
                internal Options Opts;          // null: follows the defaults
                internal object Arg;

                public Encoder(RubyClass/*!*/ rubyClass) : base(rubyClass) {
                }

                protected override RubyObject/*!*/ CreateInstance() {
                    return new Encoder(ImmediateClass.NominalClass);
                }

                [RubyConstructor]
                public static Encoder/*!*/ Create(RubyClass/*!*/ self, params object[]/*!*/ args) {
                    RubyContext context = self.Context;
                    var e = new Encoder(self);
                    if (1 <= args.Length && args[0] != null) {
                        e.Arg = args[0];
                    } else {
                        e.Arg = new Hash(context);
                    }
                    // A Hash counts as options only when a key names an option Oj knows.
                    var hash = e.Arg as Hash;
                    if (hash != null && hash.Count > 0) {
                        var opts = State(context).Defaults.Clone();
                        opts.StrRx = new RxClass();
                        if (opts.Parse(context, hash, false)) {
                            e.Opts = opts;
                        }
                    }
                    return e;
                }

                [RubyMethod("encode")]
                public static MutableString/*!*/ EncodeObj(RubyContext/*!*/ context, Encoder/*!*/ self, object obj) {
                    Options opts = self.Opts ?? State(context).Defaults;
                    if (self.Arg != null) {
                        object arg = self.Arg is Hash ? (object)new Hash((Hash)self.Arg) : self.Arg;
                        return OjRails.Encode(context, obj, self.Ropts, opts, new[] { arg });
                    }
                    return OjRails.Encode(context, obj, self.Ropts, opts, new object[0]);
                }

                private RailsOpts/*!*/ OwnRopts(RubyContext/*!*/ context) {
                    if (Ropts == null) {
                        Ropts = State(context).GlobalRopts.Copy();
                    }
                    return Ropts;
                }

                [RubyMethod("optimize")]
                public static object Optimize(RubyContext/*!*/ context, Encoder/*!*/ self, params object[]/*!*/ args) {
                    OjRails.Optimize(context, args, self.OwnRopts(context), true);
                    return null;
                }

                [RubyMethod("deoptimize")]
                public static object Deoptimize(RubyContext/*!*/ context, Encoder/*!*/ self, params object[]/*!*/ args) {
                    OjRails.Optimize(context, args, self.OwnRopts(context), false);
                    return null;
                }

                [RubyMethod("optimized?")]
                public static bool IsOptimized(RubyContext/*!*/ context, Encoder/*!*/ self, object clas) {
                    ROpt ro = (self.Ropts ?? State(context).GlobalRopts).Get(clas);
                    return ro != null && ro.On;
                }
            }
        }

        #endregion
    }
}

/* ****************************************************************************
 *
 * IronRuby's implementation of the yajl-ruby gem's C extension.
 *
 * yajl-ruby 1.4.3 is Ruby over one C extension, yajl/yajl: Yajl::Parser,
 * Yajl::Encoder, Yajl::Projector#project, Yajl::ParseError / EncodeError and the
 * JSON-gem compatibility switch, on top of the yajl 1.x C library it carries.
 * YajlCore.cs is that library in C#; this file is ext/yajl/yajl_ext.c, keeping
 * yajl's semantics rather than the json gem's: numbers with '.', 'e' or 'E' are
 * Floats (strtod) and the rest Integers of any size, an unfinished document parses to
 * what was built so far, a number at the top level never fires the completion
 * callback, every error message is yajl's own with its context window, the encoder
 * writes String bytes as they are (no transcoding), Floats and Bignums through #to_s,
 * other objects through #to_json or #to_s, and flushes 8 KiB chunks to an IO or the
 * on_progress callback as it goes.
 *
 * The gem's own Ruby (lib/yajl.rb and lib/yajl/*) is not vendored: an installed
 * yajl-ruby gem, from rubygems.org or from git, loads on top of this.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Yajl {

    internal static class Rb {
        private sealed class Sites {
            internal readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>> Sites0 =
                new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>>();
            internal readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>> Sites1 =
                new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>>();
            internal readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object, object>>> Sites2 =
                new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object, object>>>();
        }

        private static readonly object _sitesKey = new object();

        private static Sites/*!*/ GetSites(RubyContext/*!*/ context) {
            return (Sites)context.GetOrCreateLibraryData(_sitesKey, () => new Sites());
        }

        /// <summary>rb_funcall: visibility is ignored, as rb_funcall ignores it.</summary>
        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name) {
            var site = GetSites(context).Sites0.GetOrAdd(name, n => CallSite<Func<CallSite, object, object>>.Create(
                RubyCallAction.Make(context, n, RubyCallSignature.WithImplicitSelf(0))));
            return site.Target(site, target);
        }

        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name, object arg) {
            var site = GetSites(context).Sites1.GetOrAdd(name, n => CallSite<Func<CallSite, object, object, object>>.Create(
                RubyCallAction.Make(context, n, RubyCallSignature.WithImplicitSelf(1))));
            return site.Target(site, target, arg);
        }

        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name, object arg1, object arg2) {
            var site = GetSites(context).Sites2.GetOrAdd(name, n => CallSite<Func<CallSite, object, object, object, object>>.Create(
                RubyCallAction.Make(context, n, RubyCallSignature.WithImplicitSelf(2))));
            return site.Target(site, target, arg1, arg2);
        }

        /// <summary>rb_respond_to</summary>
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

        /// <summary>A message as the C makes it: a binary String.</summary>
        internal static MutableString/*!*/ Message(string/*!*/ text) {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) {
                bytes[i] = unchecked((byte)text[i]);
            }
            return MutableString.CreateBinary(bytes, RubyEncoding.Binary);
        }

        internal static Exception/*!*/ WithMessage(Exception/*!*/ exception, MutableString/*!*/ message) {
            return RubyExceptionData.InitializeException(exception, message);
        }

        internal static Exception/*!*/ ParseError(string/*!*/ text) {
            return WithMessage(new YajlOps.ParseError(text), Message(text));
        }

        internal static Exception/*!*/ ParseError(byte[]/*!*/ message) {
            return WithMessage(new YajlOps.ParseError(System.Text.Encoding.UTF8.GetString(message)),
                MutableString.CreateBinary(message, RubyEncoding.Binary));
        }

        internal static Exception/*!*/ EncodeError(string/*!*/ text) {
            return WithMessage(new YajlOps.EncodeError(text), Message(text));
        }

        /// <summary>Check_Type</summary>
        internal static Exception/*!*/ WrongType(RubyContext/*!*/ context, object obj, string/*!*/ expected) {
            string text = String.Format("wrong argument type {0} (expected {1})", TypeName(context, obj), expected);
            return WithMessage(RubyExceptions.CreateTypeError(text), Message(text));
        }

        /// <summary>rb_str_export_to_enc(str, utf8): transcoded where it can be, else as it is.</summary>
        internal static MutableString/*!*/ ExportToUtf8(RubyContext/*!*/ context, MutableString/*!*/ str) {
            if (str.Encoding == RubyEncoding.UTF8) {
                return str;
            }
            if (str.IsAscii() && !str.Encoding.IsDummy && str.Encoding.IsAsciiIdentity) {
                var copy = str.Clone();
                copy.ForceEncoding(RubyEncoding.UTF8);
                return copy;
            }
            try {
                var result = Call(context, str, "encode", MutableString.CreateAscii("UTF-8")) as MutableString;
                return result ?? str;
            } catch (Exception) {
                return str;
            }
        }

        /// <summary>rb_cstr2inum(text, 10) or rb_float_new(strtod(text)), as yajl_found_number decides.</summary>
        internal static object Number(byte[]/*!*/ buf, int start, int len) {
            bool isFloat = false;
            for (int i = 0; i < len; i++) {
                byte b = buf[start + i];
                if (b == '.' || b == 'e' || b == 'E') {
                    isFloat = true;
                    break;
                }
            }
            string text = System.Text.Encoding.ASCII.GetString(buf, start, len);
            if (isFloat) {
                return Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            if (len <= 18) {
                return Protocols.Normalize(Int64.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
            }
            return Protocols.Normalize(BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        }

        internal static MutableString/*!*/ Utf8String(byte[]/*!*/ buf, int start, int len) {
            var bytes = new byte[len];
            Buffer.BlockCopy(buf, start, bytes, 0, len);
            return MutableString.CreateBinary(bytes, RubyEncoding.UTF8);
        }

        /// <summary>The value of opts[key], through Hash#[] as rb_hash_aref goes.</summary>
        internal static object Option(RubyContext/*!*/ context, Hash/*!*/ opts, string/*!*/ key) {
            return Call(context, opts, "[]", context.CreateAsciiSymbol(key));
        }

        internal static bool IsTrue(object value) {
            return value is bool && (bool)value;
        }

        internal static bool IsFalse(object value) {
            return value is bool && !(bool)value;
        }
    }

    [RubyModule("Yajl")]
    public static class YajlOps {
        [RubyConstant("MAX_DEPTH")]
        public const int MaxDepth = YajlGen.MaxDepth;

        [RubyException("ParseError")]
        public class ParseError : SystemException {
            public ParseError() : this(null, null) { }
            public ParseError(string message) : this(message, null) { }
            public ParseError(string message, Exception inner) : base(message ?? "Yajl::ParseError", inner) { }
        }

        [RubyException("EncodeError")]
        public class EncodeError : SystemException {
            public EncodeError() : this(null, null) { }
            public EncodeError(string message) : this(message, null) { }
            public EncodeError(string message, Exception inner) : base(message ?? "Yajl::EncodeError", inner) { }
        }

        private static void CheckArity(int given, int min, int max) {
            if (given < min || given > max) {
                string text = min == max
                    ? String.Format("wrong number of arguments (given {0}, expected {1})", given, min)
                    : String.Format("wrong number of arguments (given {0}, expected {1}..{2})", given, min, max);
                throw Rb.WithMessage(RubyExceptions.CreateArgumentError(text), Rb.Message(text));
            }
        }

        #region Parser

        [RubyClass("Parser", Inherits = typeof(object))]
        public sealed class Parser : RubyObject, IYajlCallbacks {
            private YajlHandle _handle;
            private readonly List<object>/*!*/ _builderStack = new List<object>();
            private object _parseCompleteCallback;
            private int _nestedArrayLevel;
            private int _nestedHashLevel;
            private int _objectsFound;
            private bool _symbolizeKeys;

            public Parser(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Parser(ImmediateClass.NominalClass);
            }

            private RubyContext/*!*/ Context {
                get { return ImmediateClass.Context; }
            }

            private YajlHandle/*!*/ Handle {
                get {
                    if (_handle == null) {
                        throw RubyExceptions.CreateTypeError("uninitialized Yajl::Parser");
                    }
                    return _handle;
                }
            }

            /// <summary>rb_undef_alloc_func</summary>
            [RubyMethod("allocate", RubyMethodAttributes.PublicSingleton)]
            public static object Allocate(RubyClass/*!*/ self) {
                throw Rb.WithMessage(RubyExceptions.CreateTypeError("allocator undefined for " + self.Name),
                    Rb.Message("allocator undefined for " + self.Name));
            }

            /// <summary>rb_yajl_parser_new</summary>
            [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
            public static object New(RubyContext/*!*/ context, RubyClass/*!*/ self, params object[]/*!*/ args) {
                CheckArity(args.Length, 0, 1);
                bool allowComments = true, checkUtf8 = true, symbolizeKeys = false;
                if (args.Length == 1) {
                    var opts = args[0] as Hash;
                    if (opts == null) {
                        throw Rb.WrongType(context, args[0], "Hash");
                    }
                    if (Rb.IsFalse(Rb.Option(context, opts, "allow_comments"))) {
                        allowComments = false;
                    }
                    if (Rb.IsFalse(Rb.Option(context, opts, "check_utf8"))) {
                        checkUtf8 = false;
                    }
                    if (Rb.IsTrue(Rb.Option(context, opts, "symbolize_keys")) || Rb.IsTrue(Rb.Option(context, opts, "symbolize_names"))) {
                        symbolizeKeys = true;
                    }
                }

                var obj = new Parser(self);
                obj._handle = new YajlHandle(obj, allowComments, checkUtf8);
                obj._symbolizeKeys = symbolizeKeys;
                obj._parseCompleteCallback = null;
                // rb_obj_call_init(obj, 0, 0)
                Rb.Call(context, obj, "initialize");
                return obj;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(object self, params object[]/*!*/ args) {
                return self;
            }

            #region callbacks (yajl_found_*)

            /// <summary>yajl_check_and_fire_callback</summary>
            private void CheckAndFireCallback() {
                if (_parseCompleteCallback != null) {
                    if (_builderStack.Count == 1 && _nestedArrayLevel == 0 && _nestedHashLevel == 0) {
                        Rb.Call(Context, _parseCompleteCallback, "call", PopStack());
                    }
                } else {
                    if (_builderStack.Count == 1 && _nestedArrayLevel == 0 && _nestedHashLevel == 0) {
                        _objectsFound++;
                        if (_objectsFound > 1) {
                            throw Rb.ParseError("Found multiple JSON objects in the stream but no block or the on_parse_complete callback was assigned to handle them.");
                        }
                    }
                }
            }

            private object PopStack() {
                if (_builderStack.Count == 0) {
                    return null;
                }
                object result = _builderStack[_builderStack.Count - 1];
                _builderStack.RemoveAt(_builderStack.Count - 1);
                return result;
            }

            /// <summary>rb_ary_entry(builderStack, index)</summary>
            private object Entry(int index) {
                int count = _builderStack.Count;
                if (index < 0) {
                    index += count;
                }
                return index >= 0 && index < count ? _builderStack[index] : null;
            }

            /// <summary>yajl_set_static_value</summary>
            private void SetStaticValue(object val) {
                int len = _builderStack.Count;
                if (len > 0) {
                    object lastEntry = _builderStack[len - 1];
                    if (lastEntry is RubyArray) {
                        ((RubyArray)lastEntry).Add(val);
                        if (val is Hash || val is RubyArray) {
                            _builderStack.Add(val);
                        }
                    } else if (lastEntry is Hash) {
                        RubyUtils.SetHashElement(Context, (Hash)lastEntry, val, null);
                        _builderStack.Add(val);
                    } else if (lastEntry is MutableString || lastEntry is RubySymbol) {
                        var hash = Entry(len - 2) as Hash;
                        if (hash != null) {
                            RubyUtils.SetHashElement(Context, hash, lastEntry, val);
                            PopStack();
                            if (val is Hash || val is RubyArray) {
                                _builderStack.Add(val);
                            }
                        }
                    }
                } else {
                    _builderStack.Add(val);
                }
            }

            void IYajlCallbacks.Null() {
                SetStaticValue(null);
                CheckAndFireCallback();
            }

            void IYajlCallbacks.Boolean(bool value) {
                SetStaticValue(ScriptingRuntimeHelpers.BooleanToObject(value));
                CheckAndFireCallback();
            }

            void IYajlCallbacks.Number(byte[]/*!*/ buf, int start, int len) {
                // sic: no check_and_fire_callback for a number
                SetStaticValue(Rb.Number(buf, start, len));
            }

            void IYajlCallbacks.String(byte[]/*!*/ buf, int start, int len) {
                SetStaticValue(Rb.Utf8String(buf, start, len));
                CheckAndFireCallback();
            }

            void IYajlCallbacks.MapKey(byte[]/*!*/ buf, int start, int len) {
                object key;
                var str = Rb.Utf8String(buf, start, len);
                if (_symbolizeKeys) {
                    key = Context.CreateSymbol(str, false);
                } else {
                    key = str;
                }
                SetStaticValue(key);
                CheckAndFireCallback();
            }

            void IYajlCallbacks.StartMap() {
                _nestedHashLevel++;
                SetStaticValue(new Hash(Context));
            }

            void IYajlCallbacks.EndMap() {
                _nestedHashLevel--;
                if (_builderStack.Count > 1) {
                    PopStack();
                }
                CheckAndFireCallback();
            }

            void IYajlCallbacks.StartArray() {
                _nestedArrayLevel++;
                SetStaticValue(new RubyArray());
            }

            void IYajlCallbacks.EndArray() {
                _nestedArrayLevel--;
                if (_builderStack.Count > 1) {
                    PopStack();
                }
                CheckAndFireCallback();
            }

            #endregion

            /// <summary>yajl_parse_chunk</summary>
            private void ParseChunk(byte[]/*!*/ chunk, int len) {
                YajlStatus stat = Handle.Parse(chunk, len);
                if (stat == YajlStatus.Ok || stat == YajlStatus.InsufficientData) {
                    // success
                } else if (stat == YajlStatus.Error) {
                    throw Rb.ParseError(Handle.RenderError(chunk, len));
                } else {
                    throw Rb.ParseError(YajlHandle.StatusToString(stat));
                }
            }

            /// <summary>parse(input, buffer_size = 8192) { |obj| ... }</summary>
            [RubyMethod("parse")]
            public static object Parse(RubyContext/*!*/ context, BlockParam block, Parser/*!*/ self, params object[]/*!*/ args) {
                var handle = self.Handle;
                CheckArity(args.Length, 1, 2);
                object input = args[0];
                object rbufsize = args.Length > 1 ? args[1] : null;
                int bufsize;
                if (rbufsize == null) {
                    bufsize = 8192;
                } else if (rbufsize is int) {
                    bufsize = (int)rbufsize;
                } else {
                    throw Rb.WrongType(context, rbufsize, "Integer");
                }
                if (block != null) {
                    self._parseCompleteCallback = block.Proc;
                }

                var str = input as MutableString;
                if (str != null) {
                    byte[] bytes = str.ToByteArray();
                    self.ParseChunk(bytes, bytes.Length);
                } else if (Rb.RespondTo(context, input, "read")) {
                    var parsed = MutableString.CreateBinary(new byte[Math.Max(bufsize, 0)], RubyEncoding.Binary);
                    object rsize = ScriptingRuntimeHelpers.Int32ToObject(bufsize);
                    while (Rb.Call(context, input, "read", rsize, parsed) != null) {
                        byte[] bytes = parsed.ToByteArray();
                        self.ParseChunk(bytes, bytes.Length);
                    }
                } else {
                    throw Rb.ParseError("input must be a string or IO");
                }

                // parse any remaining buffered data; the status is not looked at
                handle.ParseComplete();

                if (self._parseCompleteCallback != null) {
                    self.CheckAndFireCallback();
                    return null;
                }
                return self.PopStack();
            }

            /// <summary>parse_chunk(string) / &lt;&lt;</summary>
            [RubyMethod("parse_chunk")]
            [RubyMethod("<<")]
            public static object ParseChunk(RubyContext/*!*/ context, Parser/*!*/ self, object chunk) {
                GC.KeepAlive(self.Handle);
                if (chunk == null) {
                    throw Rb.ParseError("Can't parse a nil string.");
                }
                if (self._parseCompleteCallback != null) {
                    var str = chunk as MutableString;
                    if (str == null) {
                        throw Rb.WrongType(context, chunk, "String");
                    }
                    byte[] bytes = str.ToByteArray();
                    self.ParseChunk(bytes, bytes.Length);
                } else {
                    throw Rb.ParseError("The on_parse_complete callback isn't setup, parsing useless.");
                }
                return null;
            }

            [RubyMethod("on_parse_complete=")]
            public static object SetCompleteCallback(Parser/*!*/ self, object callback) {
                self._parseCompleteCallback = callback;
                return null;
            }
        }

        #endregion

        #region Projector

        /// <summary>
        /// Yajl::Projector: an ordinary object (lib/yajl.rb gives it #initialize, the loader a
        /// #new that runs it), of which only #project is native.
        /// </summary>
        [RubyClass("Projector", Inherits = typeof(object))]
        public sealed class Projector : RubyObject {
            public Projector(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Projector(ImmediateClass.NominalClass);
            }

            private sealed class EventStream {
                internal readonly RubyContext/*!*/ Context;
                internal readonly object Stream;
                internal readonly MutableString/*!*/ Buffer;
                internal byte[]/*!*/ Bytes;
                internal int Offset;
                internal readonly YajlLexer/*!*/ Lexer = new YajlLexer(false, true);

                internal EventStream(RubyContext/*!*/ context, object stream, MutableString/*!*/ buffer, int offset) {
                    Context = context;
                    Stream = stream;
                    Buffer = buffer;
                    Bytes = buffer.ToByteArray();
                    Offset = offset;
                }
            }

            private struct Event {
                internal YajlTok Token;
                internal byte[] Buf;
                internal int Start;
                internal int Len;
            }

            /// <summary>yajl_event_stream_next</summary>
            private static Event Next(EventStream/*!*/ parser, bool pop) {
                while (true) {
                    if (parser.Offset >= parser.Bytes.Length) {
                        Rb.Call(parser.Context, parser.Stream, "read",
                            ScriptingRuntimeHelpers.Int32ToObject(parser.Bytes.Length), parser.Buffer);
                        parser.Bytes = parser.Buffer.ToByteArray();
                        if (parser.Bytes.Length == 0) {
                            return new Event { Token = YajlTok.Eof };
                        }
                        parser.Offset = 0;
                    }

                    YajlTok token;
                    if (!pop) {
                        token = parser.Lexer.Peek(parser.Bytes, parser.Bytes.Length, parser.Offset);
                        if (token == YajlTok.Eof) {
                            parser.Offset = parser.Bytes.Length;
                            continue;
                        }
                        return new Event { Token = token };
                    }

                    var ev = new Event();
                    token = parser.Lexer.Lex(parser.Bytes, parser.Bytes.Length, ref parser.Offset, out ev.Buf, out ev.Start, out ev.Len);
                    if (token == YajlTok.Eof) {
                        continue;
                    }
                    ev.Token = token;
                    return ev;
                }
            }

            private static object Filter(EventStream/*!*/ parser, object schema, Event ev) {
                // One C# frame per nesting level: a document of tens of thousands of '[' would
                // overflow the stack, which the CLR cannot catch, so stop while there is room.
                if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) {
                    throw new SystemStackError("stack level too deep");
                }
                switch (ev.Token) {
                    case YajlTok.LeftBrace:
                        return FilterArraySubtree(parser, schema, ev);
                    case YajlTok.LeftBracket:
                        return FilterObjectSubtree(parser, schema, ev);
                    default:
                        return BuildSimpleValue(parser, ev);
                }
            }

            private static bool IsValueStart(YajlTok t) {
                return t == YajlTok.String || t == YajlTok.Integer || t == YajlTok.Double || t == YajlTok.Null ||
                    t == YajlTok.Bool || t == YajlTok.LeftBracket || t == YajlTok.LeftBrace;
            }

            private static object FilterArraySubtree(EventStream/*!*/ parser, object schema, Event ev) {
                var ary = new RubyArray();
                while (true) {
                    ev = Next(parser, true);
                    if (ev.Token == YajlTok.RightBrace) {
                        break;
                    }
                    ary.Add(Filter(parser, schema, ev));

                    ev = Next(parser, false);
                    if (ev.Token == YajlTok.Comma) {
                        Next(parser, true);
                        ev = Next(parser, false);
                        if (!IsValueStart(ev.Token)) {
                            throw Rb.ParseError("read a comma, expected a value to follow, actually read " + YajlLexer.TokName(ev.Token));
                        }
                    } else if (ev.Token != YajlTok.RightBrace) {
                        throw Rb.ParseError("didn't read a comma, expected closing array, actually read " + YajlLexer.TokName(ev.Token));
                    }
                }
                return ary;
            }

            private static object FilterObjectSubtree(EventStream/*!*/ parser, object schema, Event ev) {
                var context = parser.Context;
                var hsh = new Hash(context);
                while (true) {
                    ev = Next(parser, true);
                    if (ev.Token == YajlTok.RightBracket) {
                        break;
                    }
                    if (!(ev.Token == YajlTok.String || ev.Token == YajlTok.StringWithEscapes)) {
                        throw Rb.ParseError("Expected string, unexpected stream event " + YajlLexer.TokName(ev.Token));
                    }
                    var key = BuildString(parser, ev);

                    ev = Next(parser, true);
                    if (ev.Token != YajlTok.Colon) {
                        throw Rb.ParseError("Expected colon, unexpected stream event " + YajlLexer.TokName(ev.Token));
                    }

                    bool interesting = schema == null || Rb.IsTrue(Rb.Call(context, schema, "key?", key));
                    if (!interesting) {
                        IgnoreValue(parser);
                    } else {
                        Event valueEvent = Next(parser, true);
                        object keySchema = schema == null ? null : Rb.Call(context, schema, "[]", key);
                        object val = Filter(parser, keySchema, valueEvent);
                        key.Freeze();
                        RubyUtils.SetHashElement(context, hsh, key, val);
                    }

                    ev = Next(parser, false);
                    if (ev.Token == YajlTok.Comma) {
                        Next(parser, true);
                        ev = Next(parser, false);
                        if (!(ev.Token == YajlTok.String || ev.Token == YajlTok.StringWithEscapes)) {
                            throw Rb.ParseError("read a comma, expected a key to follow, actually read " + YajlLexer.TokName(ev.Token));
                        }
                    } else if (ev.Token != YajlTok.RightBracket) {
                        throw Rb.ParseError("read a value without tailing comma, expected closing bracket, actually read " + YajlLexer.TokName(ev.Token));
                    }
                }
                return hsh;
            }

            private static void IgnoreValue(EventStream/*!*/ parser) {
                Event valueEvent = Next(parser, true);
                switch (valueEvent.Token) {
                    case YajlTok.Null:
                    case YajlTok.Bool:
                    case YajlTok.Integer:
                    case YajlTok.Double:
                    case YajlTok.String:
                    case YajlTok.StringWithEscapes:
                        return;
                }
                if (valueEvent.Token == YajlTok.LeftBrace || valueEvent.Token == YajlTok.LeftBracket) {
                    IgnoreContainer(parser);
                    return;
                }
                throw Rb.ParseError("unknown value type to ignore " + YajlLexer.TokName(valueEvent.Token));
            }

            private static void IgnoreContainer(EventStream/*!*/ parser) {
                int depth = 1;
                while (depth > 0) {
                    Event ev = Next(parser, true);
                    if (ev.Token == YajlTok.Eof) {
                        return;
                    }
                    if (ev.Token == YajlTok.LeftBracket || ev.Token == YajlTok.LeftBrace) {
                        depth += 1;
                    } else if (ev.Token == YajlTok.RightBracket || ev.Token == YajlTok.RightBrace) {
                        depth -= 1;
                    }
                }
            }

            private static object BuildSimpleValue(EventStream/*!*/ parser, Event ev) {
                switch (ev.Token) {
                    case YajlTok.Null:
                        return null;
                    case YajlTok.Bool:
                        if (ev.Len >= 4 && ev.Buf[ev.Start] == 't') {
                            return ScriptingRuntimeHelpers.BooleanToObject(true);
                        }
                        return ScriptingRuntimeHelpers.BooleanToObject(false);
                    case YajlTok.Integer:
                    case YajlTok.Double:
                        return Rb.Number(ev.Buf, ev.Start, ev.Len);
                    case YajlTok.String:
                    case YajlTok.StringWithEscapes:
                        return BuildString(parser, ev);
                    case YajlTok.Eof:
                        throw Rb.ParseError("unexpected eof while constructing value");
                    case YajlTok.Comma:
                        throw Rb.ParseError("unexpected comma while constructing value");
                    case YajlTok.Colon:
                        throw Rb.ParseError("unexpected colon while constructing value");
                    default:
                        // the C calls rb_bug here
                        throw RubyExceptions.CreateRuntimeError("[BUG] we should never get here");
                }
            }

            private static MutableString/*!*/ BuildString(EventStream/*!*/ parser, Event ev) {
                if (ev.Token == YajlTok.String) {
                    return Rb.Utf8String(ev.Buf, ev.Start, ev.Len);
                }
                var strBuf = new YajlBuf();
                YajlEncode.Decode(strBuf, ev.Buf, ev.Start, ev.Len);
                return Rb.Utf8String(strBuf.Data, 0, strBuf.Length);
            }

            [RubyMethod("project")]
            public static object Project(RubyContext/*!*/ context, object self, object schema) {
                object stream = Rb.Call(context, self, "instance_variable_get", context.EncodeIdentifier("@stream"));
                object size = Rb.Call(context, self, "instance_variable_get", context.EncodeIdentifier("@buffer_size"));
                if (!(size is int)) {
                    throw Rb.WrongType(context, size, "Integer");
                }
                int bufferSize = (int)size;
                var buffer = MutableString.CreateBinary(new byte[Math.Max(bufferSize, 0)], RubyEncoding.Binary);
                var parser = new EventStream(context, stream, buffer, bufferSize);

                Event ev = Next(parser, true);
                if (ev.Token == YajlTok.LeftBrace || ev.Token == YajlTok.LeftBracket) {
                    return Filter(parser, schema, ev);
                }
                throw Rb.ParseError("expected left bracket or brace, actually read " + YajlLexer.TokName(ev.Token));
            }
        }

        #endregion

        #region Encoder

        [RubyClass("Encoder", Inherits = typeof(object))]
        public sealed class Encoder : RubyObject {
            private const int WriteBufSize = 8192;
            private static readonly object NoTerminator = new object();   // the C's terminator == 0

            private YajlGen _gen;
            private object _onProgressCallback;
            private object _terminator = NoTerminator;

            public Encoder(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Encoder(ImmediateClass.NominalClass);
            }

            private RubyContext/*!*/ Context {
                get { return ImmediateClass.Context; }
            }

            private YajlGen/*!*/ Gen {
                get {
                    if (_gen == null) {
                        throw RubyExceptions.CreateTypeError("uninitialized Yajl::Encoder");
                    }
                    return _gen;
                }
            }

            [RubyMethod("allocate", RubyMethodAttributes.PublicSingleton)]
            public static object Allocate(RubyClass/*!*/ self) {
                throw Rb.WithMessage(RubyExceptions.CreateTypeError("allocator undefined for " + self.Name),
                    Rb.Message("allocator undefined for " + self.Name));
            }

            /// <summary>rb_yajl_encoder_new</summary>
            [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
            public static object New(RubyContext/*!*/ context, RubyClass/*!*/ self, params object[]/*!*/ args) {
                CheckArity(args.Length, 0, 1);
                Hash opts = null;
                bool beautify = false;
                int htmlSafe = 0;
                byte[] indentString = null;

                if (args.Length == 1) {
                    opts = args[0] as Hash;
                    if (opts == null) {
                        throw Rb.WrongType(context, args[0], "Hash");
                    }
                    if (Rb.IsTrue(Rb.Option(context, opts, "pretty"))) {
                        beautify = true;
                        object indent = Rb.Option(context, opts, "indent");
                        if (indent != null) {
                            var str = indent as MutableString;
                            if (str == null) {
                                throw Rb.WrongType(context, indent, "String");
                            }
                            indentString = Rb.ExportToUtf8(context, str).ToByteArray();
                        }
                    }
                    if (Rb.IsTrue(Rb.Option(context, opts, "html_safe"))) {
                        htmlSafe = 1;
                    }
                    if (Rb.IsTrue(Rb.Option(context, opts, "entities"))) {
                        htmlSafe = 2;
                    }
                }

                var obj = new Encoder(self);
                obj._gen = new YajlGen(beautify, indentString, htmlSafe);
                obj._onProgressCallback = null;
                if (opts != null && Rb.IsTrue(Rb.Call(context, opts, "has_key?", context.CreateAsciiSymbol("terminator")))) {
                    object terminator = Rb.Option(context, opts, "terminator");
                    var str = terminator as MutableString;
                    if (str != null) {
                        terminator = Rb.ExportToUtf8(context, str);
                    }
                    obj._terminator = terminator;
                } else {
                    obj._terminator = NoTerminator;
                }
                // rb_obj_call_init(obj, 0, 0)
                Rb.Call(context, obj, "initialize");
                return obj;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(object self, params object[]/*!*/ args) {
                return self;
            }

            private void RaiseForStatus(YajlGenStatus status) {
                switch (status) {
                    case YajlGenStatus.KeysMustBeStrings:
                        throw Rb.EncodeError("YAJL internal error: attempted use of non-string object as key");
                    case YajlGenStatus.MaxDepthExceeded:
                        throw Rb.EncodeError("Max nesting depth of " + YajlGen.MaxDepth + " exceeded");
                    case YajlGenStatus.InErrorState:
                        throw Rb.EncodeError("YAJL internal error: a generator function (yajl_gen_XXX) was called while in an error state");
                    case YajlGenStatus.GenerationComplete:
                        throw Rb.EncodeError("YAJL internal error: attempted to encode to an already-complete document");
                    case YajlGenStatus.InvalidNumber:
                        throw Rb.EncodeError("Invalid number: cannot encode Infinity, -Infinity, or NaN");
                    case YajlGenStatus.NoBuf:
                        throw Rb.EncodeError("YAJL internal error: yajl_gen_get_buf was called, but a print callback was specified, so no internal buffer is available");
                    case YajlGenStatus.AllocError:
                        throw Rb.EncodeError("YAJL internal error: failed to allocate memory");
                    default:
                        throw Rb.EncodeError("Encountered unknown YAJL status " + (int)status + " during JSON generation");
                }
            }

            private static bool StartsWith(byte[]/*!*/ bytes, string/*!*/ prefix) {
                if (bytes.Length < prefix.Length) {
                    return false;
                }
                for (int i = 0; i < prefix.Length; i++) {
                    if (bytes[i] != prefix[i]) {
                        return false;
                    }
                }
                return true;
            }

            /// <summary>yajl_key_to_string</summary>
            private object KeyToString(object obj) {
                if (obj is MutableString) {
                    return obj;
                }
                var sym = obj as RubySymbol;
                if (sym != null) {
                    return sym.String;
                }
                return Rb.Call(Context, obj, "to_s");
            }

            /// <summary>yajl_encode_part</summary>
            private void EncodePart(object obj, object io) {
                var context = Context;
                var gen = Gen;
                YajlGenStatus status = YajlGenStatus.Ok;

                if (io != null || _onProgressCallback != null) {
                    if (gen.Buf.Length >= WriteBufSize) {
                        var outBuff = MutableString.CreateBinary(gen.Buf.ToArray(), RubyEncoding.Binary);
                        if (io != null) {
                            Rb.Call(context, io, "write", outBuff);
                        } else if (_onProgressCallback != null) {
                            Rb.Call(context, _onProgressCallback, "call", outBuff);
                        }
                        gen.Buf.Clear();
                    }
                }

                if (obj is Hash) {
                    var hash = (Hash)obj;
                    status = gen.MapOpen();
                    if (status == YajlGenStatus.Ok) {
                        var pairs = new List<KeyValuePair<object, object>>(hash);
                        foreach (var pair in pairs) {
                            object key = CustomStringDictionary.ObjToNull(pair.Key);
                            EncodePart(KeyToString(key), io);
                            EncodePart(pair.Value, io);
                        }
                        status = gen.MapClose();
                    }
                } else if (obj is RubyArray) {
                    var array = (RubyArray)obj;
                    status = gen.ArrayOpen();
                    if (status == YajlGenStatus.Ok) {
                        for (int idx = 0; idx < array.Count; idx++) {
                            EncodePart(array[idx], io);
                        }
                        status = gen.ArrayClose();
                    }
                } else if (obj == null) {
                    status = gen.Null();
                } else if (obj is bool) {
                    status = gen.Bool((bool)obj);
                } else if (obj is int) {
                    status = gen.Long((int)obj);
                } else if (obj is long) {
                    status = gen.Long((long)obj);
                } else if (obj is double || obj is BigInteger) {
                    var str = Rb.Call(context, obj, "to_s") as MutableString;
                    byte[] bytes = str != null ? str.ToByteArray() : new byte[0];
                    if (StartsWith(bytes, "NaN") || StartsWith(bytes, "Infinity") || StartsWith(bytes, "-Infinity")) {
                        int nul = Array.IndexOf(bytes, (byte)0);
                        throw Rb.EncodeError("'" + System.Text.Encoding.ASCII.GetString(bytes, 0, nul < 0 ? bytes.Length : nul) + "' is an invalid number");
                    }
                    status = gen.Number(bytes, 0, bytes.Length);
                } else if (obj is MutableString) {
                    byte[] bytes = ((MutableString)obj).ToByteArray();
                    status = gen.String(bytes, 0, bytes.Length);
                } else if (obj is RubySymbol) {
                    byte[] bytes = ((RubySymbol)obj).String.ToByteArray();
                    status = gen.String(bytes, 0, bytes.Length);
                } else {
                    if (Rb.RespondTo(context, obj, "to_json")) {
                        object json = Rb.Call(context, obj, "to_json");
                        var str = json as MutableString;
                        if (str == null) {
                            throw Rb.WrongType(context, json, "String");
                        }
                        byte[] bytes = str.ToByteArray();
                        status = gen.Number(bytes, 0, bytes.Length);
                    } else {
                        object s = Rb.Call(context, obj, "to_s");
                        var str = s as MutableString;
                        if (str == null) {
                            throw Rb.WrongType(context, s, "String");
                        }
                        byte[] bytes = str.ToByteArray();
                        status = gen.String(bytes, 0, bytes.Length);
                    }
                }

                if (status != YajlGenStatus.Ok) {
                    RaiseForStatus(status);
                }
            }

            /// <summary>encode(obj, io = nil) { |chunk| ... }</summary>
            [RubyMethod("encode")]
            public static object Encode(RubyContext/*!*/ context, BlockParam block, Encoder/*!*/ self, params object[]/*!*/ args) {
                var gen = self.Gen;
                CheckArity(args.Length, 1, 2);
                object obj = args[0];
                object io = args.Length > 1 ? args[1] : null;
                object blk = block != null ? block.Proc : null;

                if (blk != null) {
                    self._onProgressCallback = blk;
                }

                self.EncodePart(obj, io);

                var outBuff = MutableString.CreateBinary(gen.Buf.ToArray(), RubyEncoding.UTF8);
                gen.Buf.Clear();

                object terminator = self._terminator;
                if (io != null) {
                    Rb.Call(context, io, "write", outBuff);
                    if (terminator != NoTerminator && terminator != null) {
                        Rb.Call(context, io, "write", terminator);
                    }
                    return null;
                } else if (blk != null) {
                    Rb.Call(context, blk, "call", outBuff);
                    if (terminator != NoTerminator) {
                        Rb.Call(context, blk, "call", terminator);
                    }
                    return null;
                } else {
                    if (terminator != NoTerminator && terminator != null) {
                        Rb.Call(context, outBuff, "concat", terminator);
                    }
                    return outBuff;
                }
            }

            [RubyMethod("on_progress=")]
            public static object SetProgressCallback(Encoder/*!*/ self, object callback) {
                self._onProgressCallback = callback;
                return null;
            }
        }

        #endregion
    }
}

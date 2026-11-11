/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * RBS::Parser's primitives - ext/rbs_extension/main.c of the rbs gem (BSD-2-Clause / Ruby).
 *
 * ***************************************************************************/

using System;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Rbs {

    internal sealed class RbsNodeInfo {
        internal readonly string[]/*!*/ ClassPath;
        internal readonly string[]/*!*/ Keys;
        internal readonly bool ResolveTypeParams;

        internal RbsNodeInfo(string[]/*!*/ classPath, string[]/*!*/ keys, bool resolveTypeParams) {
            ClassPath = classPath;
            Keys = keys;
            ResolveTypeParams = resolveTypeParams;
        }
    }

    internal static partial class RbsNodes {
    }

    /// <summary>
    /// What one RubyContext needs to turn librbs's stream into objects: the call sites, the
    /// node classes and keyword Symbols (looked up once, as class_constants.c imports them
    /// once), and the C extension's two shared frozen objects.
    /// </summary>
    internal sealed class RbsSites {
        internal readonly RubyContext/*!*/ Context;
        internal readonly CallSite<Func<CallSite, object, object>>/*!*/ Content;
        internal readonly CallSite<Func<CallSite, object, object>>/*!*/ ToI;
        internal readonly CallSite<Func<CallSite, object, object>>/*!*/ Inspect;
        internal readonly CallSite<Func<CallSite, object, object, object>>/*!*/ New1;
        internal readonly CallSite<Func<CallSite, object, object, object, object, object>>/*!*/ New3;
        internal readonly CallSite<Func<CallSite, object, object, object>>/*!*/ ResolveVariables;
        internal readonly RubyArray/*!*/ EmptyArray;
        internal readonly Hash/*!*/ EmptyHash;

        private readonly RubyModule[]/*!*/ _classes = new RubyModule[RbsNodes.Table.Length];
        private readonly RubySymbol[][]/*!*/ _keys = new RubySymbol[RbsNodes.Table.Length][];
        private RubyModule _typeParam, _parsingError;

        internal RbsSites(RubyContext/*!*/ context) {
            Context = context;
            Content = Site<Func<CallSite, object, object>>("content", 0);
            ToI = Site<Func<CallSite, object, object>>("to_i", 0);
            Inspect = Site<Func<CallSite, object, object>>("inspect", 0);
            New1 = Site<Func<CallSite, object, object, object>>("new", 1);
            New3 = Site<Func<CallSite, object, object, object, object, object>>("new", 3);
            ResolveVariables = Site<Func<CallSite, object, object, object>>("resolve_variables", 1);
            EmptyArray = new RubyArray().Freeze();
            EmptyHash = new Hash(context).Freeze();
        }

        private CallSite<T>/*!*/ Site<T>(string/*!*/ name, int argumentCount) where T : class {
            return CallSite<T>.Create(RubyCallAction.Make(Context, name, RubyCallSignature.Simple(argumentCount)));
        }

        internal static RbsSites/*!*/ Get(RubyContext/*!*/ context) {
            return (RbsSites)context.GetOrCreateLibraryData(typeof(RbsSites), () => new RbsSites(context));
        }

        private RubyModule/*!*/ Resolve(string[]/*!*/ path) {
            RubyModule module = Context.ObjectClass;
            foreach (string name in path) {
                object value;
                if (!module.TryResolveConstant(null, name, out value) || !(value is RubyModule)) {
                    throw RubyExceptions.CreateNameError(String.Format("uninitialized constant {0}", String.Join("::", path)));
                }
                module = (RubyModule)value;
            }
            return module;
        }

        internal RubyModule/*!*/ NodeClass(int type) {
            return _classes[type] ?? (_classes[type] = Resolve(RbsNodes.Table[type].ClassPath));
        }

        internal RubySymbol[]/*!*/ NodeKeys(int type) {
            var keys = _keys[type];
            if (keys == null) {
                var names = RbsNodes.Table[type].Keys;
                keys = new RubySymbol[names.Length];
                for (int i = 0; i < names.Length; i++) {
                    keys[i] = Context.CreateAsciiSymbol(names[i]);
                }
                _keys[type] = keys;
            }
            return keys;
        }

        internal RubyModule/*!*/ TypeParam {
            get { return _typeParam ?? (_typeParam = Resolve(new[] { "RBS", "AST", "TypeParam" })); }
        }

        internal RubyModule/*!*/ ParsingError {
            get { return _parsingError ?? (_parsingError = Resolve(new[] { "RBS", "ParsingError" })); }
        }
    }

    public static partial class RbsOps {

        /// <summary>
        /// RBS::Parser's private primitives, which rbs/parser_aux.rb (from the installed gem)
        /// wraps in the public API.  Each one does what main.c does around the parser - the
        /// same argument checks, in the same order, with the same messages - and leaves the
        /// parsing itself to librbs.
        /// </summary>
        [RubyClass("Parser", Inherits = typeof(object))]
        public static class ParserOps {
            // rbs_ironruby.c's kinds and flags
            private const int KindType = 0, KindMethodType = 1, KindSignature = 2, KindTypeParams = 3,
                KindInlineLeadingAnnotation = 4, KindInlineTrailingAnnotation = 5;
            private const int RequireEof = 1, VoidAllowed = 2, SelfAllowed = 4, ModuleTypeParams = 8;

            [RubyMethod("_parse_type", RubyMethodAttributes.PublicSingleton)]
            public static object ParseTypeMethod(ConversionStorage<MutableString>/*!*/ toStr, RubyModule/*!*/ self,
                object buffer, int startPos, int endPos, object variables, object requireEof, object voidAllowed, object selfAllowed) {

                int flags = (RubyOps.IsTrue(requireEof) ? RequireEof : 0) | (RubyOps.IsTrue(voidAllowed) ? VoidAllowed : 0) |
                    (RubyOps.IsTrue(selfAllowed) ? SelfAllowed : 0);
                return Parse(toStr, self, KindType, buffer, startPos, endPos, variables, true, flags);
            }

            [RubyMethod("_parse_method_type", RubyMethodAttributes.PublicSingleton)]
            public static object ParseMethodTypeMethod(ConversionStorage<MutableString>/*!*/ toStr, RubyModule/*!*/ self,
                object buffer, int startPos, int endPos, object variables, object requireEof) {

                return Parse(toStr, self, KindMethodType, buffer, startPos, endPos, variables, true, RubyOps.IsTrue(requireEof) ? RequireEof : 0);
            }

            [RubyMethod("_parse_signature", RubyMethodAttributes.PublicSingleton)]
            public static object ParseSignatureMethod(ConversionStorage<MutableString>/*!*/ toStr, RubyModule/*!*/ self,
                object buffer, int startPos, int endPos) {

                return Parse(toStr, self, KindSignature, buffer, startPos, endPos, null, false, 0);
            }

            /// <summary>
            /// main.c hands module_type_params to the parser's bool parameter as the VALUE it
            /// is, so anything but false - nil included - means true.
            /// </summary>
            [RubyMethod("_parse_type_params", RubyMethodAttributes.PublicSingleton)]
            public static object ParseTypeParamsMethod(ConversionStorage<MutableString>/*!*/ toStr, RubyModule/*!*/ self,
                object buffer, int startPos, int endPos, object moduleTypeParams) {

                bool module = !(moduleTypeParams is bool && !(bool)moduleTypeParams);
                return Parse(toStr, self, KindTypeParams, buffer, startPos, endPos, null, false, module ? ModuleTypeParams : 0);
            }

            [RubyMethod("_parse_inline_leading_annotation", RubyMethodAttributes.PublicSingleton)]
            public static object ParseInlineLeadingAnnotation(ConversionStorage<MutableString>/*!*/ toStr, RubyModule/*!*/ self,
                object buffer, int startPos, int endPos, object variables) {

                return Parse(toStr, self, KindInlineLeadingAnnotation, buffer, startPos, endPos, variables, true, 0);
            }

            [RubyMethod("_parse_inline_trailing_annotation", RubyMethodAttributes.PublicSingleton)]
            public static object ParseInlineTrailingAnnotation(ConversionStorage<MutableString>/*!*/ toStr, RubyModule/*!*/ self,
                object buffer, int startPos, int endPos, object variables) {

                return Parse(toStr, self, KindInlineTrailingAnnotation, buffer, startPos, endPos, variables, true, 0);
            }

            /// <summary>
            /// The rbs release librbs was built from - IronRuby's, not the C extension's:
            /// rbs_extension.rb loads only under that RBS::VERSION.  Raises LoadError when
            /// librbs is missing.
            /// </summary>
            [RubyMethod("_ironruby_librbs_version", RubyMethodAttributes.PrivateSingleton)]
            public static MutableString/*!*/ LibrbsVersion(RubyModule/*!*/ self) {
                return MutableString.CreateAscii(RbsNative.RbsVersion);
            }

            /// <summary>
            /// [[type, location], ...] for every token up to and including pEOF.
            /// </summary>
            [RubyMethod("_lex", RubyMethodAttributes.PublicSingleton)]
            public static RubyArray/*!*/ Lex(ConversionStorage<MutableString>/*!*/ toStr, RubyModule/*!*/ self, object buffer, int endPos) {
                var sites = RbsSites.Get(self.Context);
                MutableString content = Content(toStr, sites, buffer);

                // alloc_lexer_from_buffer
                if (endPos < 0) {
                    throw RbsOps.Binary(RubyExceptions.CreateArgumentError(String.Format("negative position range: {0}...{1}", 0, endPos)));
                }
                byte[] source = CString(content);

                byte[] result;
                int status = RbsNative.Lex(source, EncodingName(content), endPos, out result);
                CheckEncoding(status, content);

                var reader = new RbsReader(sites, buffer, content.Encoding, result);
                return reader.ReadTokens();
            }

            private static object Parse(ConversionStorage<MutableString>/*!*/ toStr, RubyModule/*!*/ self, int kind,
                object buffer, int startPos, int endPos, object variables, bool declaresVariables, int flags) {

                var sites = RbsSites.Get(self.Context);

                // rbsparser_parse_*: buffer.content, StringValue
                MutableString content = Content(toStr, sites, buffer);

                // alloc_parser_from_buffer
                if (startPos < 0 || endPos < 0) {
                    throw RbsOps.Binary(RubyExceptions.CreateArgumentError(String.Format("negative position range: {0}...{1}", startPos, endPos)));
                }
                byte[] source = CString(content);

                // declare_type_variables
                byte[] names = null;
                int count = 0;
                if (declaresVariables && variables != null) {
                    names = VariableNames(sites, variables, out count);
                }

                byte[] result;
                int status = RbsNative.Parse(kind, source, EncodingName(content), startPos, endPos, names, count, flags, out result);
                CheckEncoding(status, content);

                var reader = new RbsReader(sites, buffer, content.Encoding, result);
                if (status == RbsNative.Error) {
                    throw reader.ReadError();
                }
                return reader.ReadValue();
            }

            private static MutableString/*!*/ Content(ConversionStorage<MutableString>/*!*/ toStr, RbsSites/*!*/ sites, object buffer) {
                object content = sites.Content.Target(sites.Content, buffer);
                return Protocols.CastToString(toStr, content);
            }

            /// <summary>StringValueCStr: the parser stops at the first NUL, so the C extension refuses one.</summary>
            private static byte[]/*!*/ CString(MutableString/*!*/ content) {
                byte[] bytes = content.ToByteArray();
                if (Array.IndexOf(bytes, (byte)0) >= 0) {
                    throw RbsOps.Binary(RubyExceptions.CreateArgumentError("string contains null byte"));
                }
                return bytes;
            }

            /// <summary>rb_enc_name of the content's encoding, which rbs looks up by name.</summary>
            private static string/*!*/ EncodingName(MutableString/*!*/ content) {
                RubyEncoding encoding = content.Encoding;
                return encoding == RubyEncoding.Binary ? "ASCII-8BIT" : encoding.Name;
            }

            private static void CheckEncoding(int status, MutableString/*!*/ content) {
                if (status == RbsNative.UnknownEncoding) {
                    // The C extension hands the lexer a NULL encoding here and crashes.
                    throw RbsOps.Binary(RubyExceptions.CreateArgumentError(String.Format("rbs cannot parse {0} text", EncodingName(content))));
                }
            }

            /// <summary>
            /// The names as librbs takes them: (u32 length, bytes) each.  Only an Array of
            /// Symbols, or nil, is accepted, with main.c's messages.
            /// </summary>
            private static byte[]/*!*/ VariableNames(RbsSites/*!*/ sites, object variables, out int count) {
                var context = sites.Context;
                var array = variables as RubyArray;
                if (array == null) {
                    throw RbsOps.Binary(RubyExceptions.CreateTypeError(String.Format("wrong argument type {0} (must be an Array of Symbols or nil)",
                        context.GetClassDisplayName(variables))));
                }

                var stream = new System.IO.MemoryStream();
                count = 0;
                for (int i = 0; i < array.Count; i++) {
                    var symbol = array[i] as RubySymbol;
                    if (symbol == null) {
                        object inspected = sites.Inspect.Target(sites.Inspect, array[i]);
                        throw RbsOps.Binary(RubyExceptions.CreateTypeError(String.Format(
                            "Type variables Array contains invalid value {0} of type {1} (must be an Array of Symbols or nil)",
                            inspected, context.GetClassDisplayName(array[i]))));
                    }
                    byte[] name = symbol.String.ToByteArray();
                    stream.WriteByte((byte)name.Length);
                    stream.WriteByte((byte)(name.Length >> 8));
                    stream.WriteByte((byte)(name.Length >> 16));
                    stream.WriteByte((byte)(name.Length >> 24));
                    stream.Write(name, 0, name.Length);
                    count++;
                }
                return stream.ToArray();
            }
        }
    }

    /// <summary>
    /// Reads rbs_ironruby.c's stream (see the format at the top of that file) and builds what
    /// ast_translation.c builds: every node with Class.new(**fields).
    /// </summary>
    internal sealed class RbsReader {
        private const byte Nil = 0, True = 1, False = 2, Node = 3, Array = 4, HashTag = 5, EmptyHash = 6, EmptyArray = 7,
            String = 8, StringUtf8 = 9, Integer = 10, Symbol = 11, Keyword = 12, LocationTag = 13;

        private readonly RbsSites/*!*/ _sites;
        private readonly RubyContext/*!*/ _context;
        private readonly object _buffer;
        private readonly RubyEncoding/*!*/ _encoding;
        private readonly RubyClass/*!*/ _locationClass;
        private readonly byte[]/*!*/ _data;
        private int _pos;

        internal RbsReader(RbsSites/*!*/ sites, object buffer, RubyEncoding/*!*/ encoding, byte[]/*!*/ data) {
            _sites = sites;
            _context = sites.Context;
            _buffer = buffer;
            _encoding = encoding;
            _locationClass = RbsOps.Location.LocationClass(_context);
            _data = data;
        }

        private byte U8() {
            return _data[_pos++];
        }

        private int U16() {
            int value = _data[_pos] | (_data[_pos + 1] << 8);
            _pos += 2;
            return value;
        }

        private int I32() {
            int value = _data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24);
            _pos += 4;
            return value;
        }

        private byte[]/*!*/ Bytes() {
            int length = I32();
            var bytes = new byte[length];
            Buffer.BlockCopy(_data, _pos, bytes, 0, length);
            _pos += length;
            return bytes;
        }

        private string/*!*/ Ascii() {
            int length = I32();
            string result = System.Text.Encoding.ASCII.GetString(_data, _pos, length);
            _pos += length;
            return result;
        }

        internal object ReadValue() {
            byte tag = U8();
            switch (tag) {
                case Nil: return null;
                case True: return ScriptingRuntimeHelpers.True;
                case False: return ScriptingRuntimeHelpers.False;
                case Node: return ReadNode();

                case Array: {
                        int count = I32();
                        var array = new RubyArray(count);
                        for (int i = 0; i < count; i++) {
                            array.Add(ReadValue());
                        }
                        return array;
                    }

                case HashTag: {
                        int count = I32();
                        var hash = new Hash(_context);
                        for (int i = 0; i < count; i++) {
                            object key = ReadValue();
                            object value = ReadValue();
                            RubyUtils.SetHashElement(_context, hash, key, value);
                        }
                        return hash;
                    }

                case EmptyHash: return _sites.EmptyHash;
                case EmptyArray: return _sites.EmptyArray;

                // rbs_string_to_ruby_string: in the buffer's encoding
                case String: return MutableString.CreateBinary(Bytes(), _encoding);
                case StringUtf8: return MutableString.CreateBinary(Bytes(), RubyEncoding.UTF8);

                case Integer: {
                        var text = MutableString.CreateBinary(Bytes(), RubyEncoding.UTF8);
                        return _sites.ToI.Target(_sites.ToI, text);
                    }

                // rb_intern3(..., ctx.encoding)
                case Symbol: return _context.CreateSymbol(Bytes(), _encoding);
                // rb_intern2
                case Keyword: return _context.CreateAsciiSymbol(Ascii());

                case LocationTag: return ReadLocation();

                default:
                    throw RubyExceptions.CreateRuntimeError(System.String.Format("Unknown node type: {0}", tag));
            }
        }

        private object ReadNode() {
            int type = U8();
            var info = type < RbsNodes.Table.Length ? RbsNodes.Table[type] : null;
            if (info == null) {
                throw RubyExceptions.CreateRuntimeError(System.String.Format("Unknown node type: {0}", type));
            }

            var keys = _sites.NodeKeys(type);
            var fields = new Hash(_context);
            object typeParams = null;
            for (int i = 0; i < keys.Length; i++) {
                object value = ReadValue();
                RubyUtils.SetHashElement(_context, fields, keys[i], value);
                if (info.ResolveTypeParams && info.Keys[i] == "type_params") {
                    typeParams = value;
                }
            }

            if (info.ResolveTypeParams) {
                _sites.ResolveVariables.Target(_sites.ResolveVariables, _sites.TypeParam, typeParams);
            }

            // CLASS_NEW_INSTANCE: rb_class_new_instance_kw(1, &h, klass, RB_PASS_KEYWORDS)
            fields.IsKeywordArguments = true;
            return _sites.New1.Target(_sites.New1, _sites.NodeClass(type), fields);
        }

        private object ReadLocation() {
            int start = I32();
            int end = I32();
            var location = new RbsOps.Location(_locationClass, _buffer, start, end);

            int capacity = U16();
            if (capacity > 0) {
                int count = U16();
                uint required = (uint)I32();
                var children = new RbsOps.Location.Child[capacity];
                for (int i = 0; i < count; i++) {
                    children[i].Name = _context.CreateAsciiSymbol(Ascii());
                    children[i].Start = I32();
                    children[i].End = I32();
                }
                location.SetChildren(children, count, required);
            }
            return location;
        }

        /// <summary>raise_error: RBS::ParsingError.new(location, message, token_type), or RuntimeError.</summary>
        internal Exception/*!*/ ReadError() {
            bool syntaxError = U8() != 0;
            int start = I32();
            int end = I32();
            byte[] tokenType = Bytes();
            byte[] message = Bytes();

            if (!syntaxError) {
                return RbsOps.Binary(RubyExceptions.CreateRuntimeError("Unexpected error"));
            }

            var location = new RbsOps.Location(_locationClass, _buffer, start, end);
            // rb_str_new_cstr: ASCII-8BIT
            object error = _sites.New3.Target(_sites.New3, _sites.ParsingError, location,
                MutableString.CreateBinary(message, RubyEncoding.Binary), MutableString.CreateBinary(tokenType, RubyEncoding.Binary));

            var exception = error as Exception;
            if (exception == null) {
                return RubyExceptions.CreateTypeError("exception class/object expected");
            }
            return exception;
        }

        internal RubyArray/*!*/ ReadTokens() {
            int count = I32();
            var tokens = new RubyArray(count);
            for (int i = 0; i < count; i++) {
                // ID2SYM(rb_intern(rbs_token_type_str(token.type)))
                var type = _context.CreateAsciiSymbol(Ascii());
                int start = I32();
                int end = I32();
                var pair = new RubyArray(2);
                pair.Add(type);
                pair.Add(new RbsOps.Location(_locationClass, _buffer, start, end));
                tokens.Add(pair);
            }
            return tokens;
        }
    }
}

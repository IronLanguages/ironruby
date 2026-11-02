/* ****************************************************************************
 *
 * JSON for IronRuby.
 *
 * MRI implements json as a C extension, so this is a C# reimplementation in
 * the same spirit as the Digest, Zlib and StringIO libraries here — the
 * approach JRuby also takes for the C-backed parts of the standard library.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Json {

    [RubyModule("JSON")]
    public static class JsonModule {

        #region errors

        [RubyClass("JSONError", Extends = typeof(JsonError), Inherits = typeof(SystemException))]
        public static class JsonErrorOps {
        }

        [RubyClass("ParserError", Extends = typeof(JsonParserError), Inherits = typeof(JsonError))]
        public static class JsonParserErrorOps {
        }

        [RubyClass("GeneratorError", Extends = typeof(JsonGeneratorError), Inherits = typeof(JsonError))]
        public static class JsonGeneratorErrorOps {
        }

        #endregion

        #region generate

        /// <summary>
        /// The formatting options of JSON::State, as far as the generator here reads them.
        /// The json gem takes them as an options Hash (or a State) in generate's second
        /// argument and ignores any key it does not know, which is what callers rely on:
        /// the graphql gem, for one, still passes the long-removed <c>quirks_mode</c>.
        /// </summary>
        private sealed class GeneratorOptions {
            internal string Indent = "";
            internal string Space = "";
            internal string SpaceBefore = "";
            internal string ObjectNl = "";
            internal string ArrayNl = "";
            internal int MaxNesting = 100;

            internal static readonly GeneratorOptions Compact = new GeneratorOptions();

            internal static GeneratorOptions/*!*/ Pretty() {
                return new GeneratorOptions { Indent = "  ", Space = " ", ObjectNl = "\n", ArrayNl = "\n" };
            }

            internal static GeneratorOptions/*!*/ Parse(object opts, GeneratorOptions/*!*/ defaults) {
                var hash = opts as Hash;
                if (hash == null || hash.Count == 0) {
                    return defaults;
                }

                var result = new GeneratorOptions {
                    Indent = defaults.Indent,
                    Space = defaults.Space,
                    SpaceBefore = defaults.SpaceBefore,
                    ObjectNl = defaults.ObjectNl,
                    ArrayNl = defaults.ArrayNl,
                    MaxNesting = defaults.MaxNesting,
                };

                foreach (var entry in hash) {
                    string name = KeyName(entry.Key);
                    switch (name) {
                        case "indent": result.Indent = OptionString(entry.Value); break;
                        case "space": result.Space = OptionString(entry.Value); break;
                        case "space_before": result.SpaceBefore = OptionString(entry.Value); break;
                        case "object_nl": result.ObjectNl = OptionString(entry.Value); break;
                        case "array_nl": result.ArrayNl = OptionString(entry.Value); break;
                        case "max_nesting":
                            if (entry.Value is int) {
                                result.MaxNesting = (int)entry.Value;
                            } else if (entry.Value == null || entry.Value is bool && !(bool)entry.Value) {
                                result.MaxNesting = 0;
                            }
                            break;
                    }
                }
                return result;
            }

            private static string KeyName(object key) {
                var symbol = key as RubySymbol;
                if (symbol != null) {
                    return symbol.ToString();
                }
                var str = key as MutableString;
                return str != null ? str.ConvertToString() : null;
            }

            private static string/*!*/ OptionString(object value) {
                var str = value as MutableString;
                return str != null ? str.ConvertToString() : "";
            }
        }

        [RubyMethod("generate", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("dump", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Generate(RubyContext/*!*/ context, object self, object obj,
            [Optional]object opts) {

            var builder = new StringBuilder();
            WriteValue(context, builder, obj, GeneratorOptions.Parse(opts, GeneratorOptions.Compact), 0);
            return MutableString.Create(builder.ToString(), RubyEncoding.UTF8);
        }

        [RubyMethod("pretty_generate", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ PrettyGenerate(RubyContext/*!*/ context, object self, object obj,
            [Optional]object opts) {

            var builder = new StringBuilder();
            WriteValue(context, builder, obj, GeneratorOptions.Parse(opts, GeneratorOptions.Pretty()), 0);
            return MutableString.Create(builder.ToString(), RubyEncoding.UTF8);
        }

        private static void WriteValue(RubyContext/*!*/ context, StringBuilder/*!*/ builder, object obj,
            GeneratorOptions/*!*/ options, int depth) {

            if (options.MaxNesting > 0 && depth > options.MaxNesting) {
                throw new JsonGeneratorError(String.Format("nesting of {0} is too deep", options.MaxNesting));
            }

            if (obj == null) {
                builder.Append("null");
                return;
            }

            if (obj is bool) {
                builder.Append((bool)obj ? "true" : "false");
                return;
            }

            if (obj is int || obj is BigInteger || obj is long) {
                builder.Append(obj.ToString());
                return;
            }

            if (obj is double) {
                double value = (double)obj;
                if (Double.IsNaN(value) || Double.IsInfinity(value)) {
                    throw new JsonGeneratorError(String.Format("{0} not allowed in JSON", value));
                }
                builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            var str = obj as MutableString;
            if (str != null) {
                WriteString(builder, str.ConvertToString());
                return;
            }

            var symbol = obj as RubySymbol;
            if (symbol != null) {
                WriteString(builder, symbol.ToString());
                return;
            }

            var hash = obj as Hash;
            if (hash != null) {
                WriteHash(context, builder, hash, options, depth);
                return;
            }

            var list = obj as IList<object>;
            if (list != null) {
                WriteArray(context, builder, list, options, depth);
                return;
            }

            // anything else is rendered through to_s, as the json gem does
            WriteString(builder, context.Inspect(obj).ConvertToString());
        }

        private static void WriteHash(RubyContext/*!*/ context, StringBuilder/*!*/ builder, Hash/*!*/ hash,
            GeneratorOptions/*!*/ options, int depth) {

            builder.Append('{');
            bool first = true;
            foreach (var entry in hash) {
                if (!first) {
                    builder.Append(',');
                }
                first = false;
                NewLine(builder, options.ObjectNl, options.Indent, depth + 1);

                // JSON object keys are always strings
                var key = entry.Key;
                var keyString = key as MutableString;
                var keySymbol = key as RubySymbol;
                if (keyString != null) {
                    WriteString(builder, keyString.ConvertToString());
                } else if (keySymbol != null) {
                    WriteString(builder, keySymbol.ToString());
                } else if (key == null) {
                    WriteString(builder, "");
                } else {
                    WriteString(builder, key.ToString());
                }

                builder.Append(options.SpaceBefore);
                builder.Append(':');
                builder.Append(options.Space);
                WriteValue(context, builder, entry.Value, options, depth + 1);
            }
            if (!first) {
                NewLine(builder, options.ObjectNl, options.Indent, depth);
            }
            builder.Append('}');
        }

        private static void WriteArray(RubyContext/*!*/ context, StringBuilder/*!*/ builder, IList<object>/*!*/ list,
            GeneratorOptions/*!*/ options, int depth) {

            builder.Append('[');
            for (int i = 0; i < list.Count; i++) {
                if (i > 0) {
                    builder.Append(',');
                }
                NewLine(builder, options.ArrayNl, options.Indent, depth + 1);
                WriteValue(context, builder, list[i], options, depth + 1);
            }
            if (list.Count > 0) {
                NewLine(builder, options.ArrayNl, options.Indent, depth);
            }
            builder.Append(']');
        }

        private static void NewLine(StringBuilder/*!*/ builder, string/*!*/ newLine, string/*!*/ indent, int depth) {
            if (newLine.Length == 0 && indent.Length == 0) {
                return;
            }
            builder.Append(newLine);
            for (int i = 0; i < depth; i++) {
                builder.Append(indent);
            }
        }

        private static void WriteString(StringBuilder/*!*/ builder, string/*!*/ value) {
            builder.Append('"');
            foreach (char c in value) {
                switch (c) {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20) {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        } else {
                            builder.Append(c);
                        }
                        break;
                }
            }
            builder.Append('"');
        }

        #endregion

        #region parse

        [RubyMethod("parse", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("load", RubyMethodAttributes.PublicSingleton)]
        public static object Parse(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]MutableString/*!*/ source,
            [DefaultParameterValue(null)]Hash options) {

            var parser = new JsonParser(source.ConvertToString(), context);
            parser.SymbolizeNames = TruthyOption(context, options, "symbolize_names");
            parser.AllowControlCharacters = TruthyOption(context, options, "allow_control_characters");
            object result = parser.ParseValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd) {
                throw new JsonParserError("unexpected token at '" + parser.Rest + "'");
            }
            return result;
        }

        /// <summary>
        /// One keyword of the options Hash JSON.parse takes.  Only the options this parser
        /// can honour are read: symbolize_names, allow_control_characters, and allow_comments, which needs no flag
        /// because this parser has always skipped comments.  An option it does not know is
        /// ignored rather than rejected, the way the json gem ignores unknown keys.
        /// </summary>
        private static bool TruthyOption(RubyContext/*!*/ context, Hash options, string/*!*/ name) {
            if (options == null) {
                return false;
            }
            object value;
            if (!options.TryGetValue(context.CreateSymbol(name, RubyEncoding.UTF8), out value)) {
                return false;
            }
            return RubyOps.IsTrue(value);
        }

        #endregion
    }

    #region exception types

    public class JsonError : SystemException {
        public JsonError(string message) : base(message) { }
        public JsonError() : this("JSON error") { }
    }

    public class JsonParserError : JsonError {
        public JsonParserError(string message) : base(message) { }
        public JsonParserError() : this("parser error") { }
    }

    public class JsonGeneratorError : JsonError {
        public JsonGeneratorError(string message) : base(message) { }
        public JsonGeneratorError() : this("generator error") { }
    }

    #endregion

    /// <summary>
    /// A straightforward recursive-descent JSON reader. Produces IronRuby values
    /// directly (Hash, RubyArray, MutableString, int/BigInteger, double, bool, null).
    /// </summary>
    internal sealed class JsonParser {
        private readonly string/*!*/ _source;
        private readonly RubyContext/*!*/ _context;
        private int _position;

        /// <summary>
        /// JSON.parse(source, symbolize_names: true) answers Symbol keys.
        /// </summary>
        internal bool SymbolizeNames { get; set; }

        /// <summary>
        /// JSON.parse(source, allow_control_characters: true) takes a raw newline, tab, ... inside
        /// a string as it is.
        /// </summary>
        internal bool AllowControlCharacters { get; set; }

        internal JsonParser(string/*!*/ source, RubyContext/*!*/ context) {
            _source = source;
            _context = context;
        }

        internal bool AtEnd {
            get { return _position >= _source.Length; }
        }

        internal string Rest {
            get { return _position < _source.Length ? _source.Substring(_position, Math.Min(10, _source.Length - _position)) : ""; }
        }

        internal void SkipWhitespace() {
            while (_position < _source.Length) {
                char c = _source[_position];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
                    _position++;
                } else if (c == '/' && _position + 1 < _source.Length && _source[_position + 1] == '/') {
                    while (_position < _source.Length && _source[_position] != '\n') _position++;
                } else if (c == '/' && _position + 1 < _source.Length && _source[_position + 1] == '*') {
                    _position += 2;
                    while (_position + 1 < _source.Length &&
                           !(_source[_position] == '*' && _source[_position + 1] == '/')) {
                        _position++;
                    }
                    _position = Math.Min(_position + 2, _source.Length);
                } else {
                    break;
                }
            }
        }

        internal object ParseValue(int depth) {
            if (depth > 100) {
                throw new JsonParserError("nesting of 100 is too deep");
            }

            SkipWhitespace();
            if (AtEnd) {
                throw new JsonParserError("unexpected end of input");
            }

            char c = _source[_position];
            switch (c) {
                case '{': return ParseObject(depth);
                case '[': return ParseArray(depth);
                case '"': return MutableString.Create(ParseString(), RubyEncoding.UTF8);
                case 't': Expect("true"); return ScriptingRuntimeHelpers.True;
                case 'f': Expect("false"); return ScriptingRuntimeHelpers.False;
                case 'n': Expect("null"); return null;
                default: return ParseNumber();
            }
        }

        private void Expect(string/*!*/ word) {
            if (_position + word.Length > _source.Length || String.CompareOrdinal(_source, _position, word, 0, word.Length) != 0) {
                throw new JsonParserError("unexpected token at '" + Rest + "'");
            }
            _position += word.Length;
        }

        private object ParseObject(int depth) {
            _position++; // '{'
            var hash = new Hash(_context);
            SkipWhitespace();
            if (!AtEnd && _source[_position] == '}') {
                _position++;
                return hash;
            }

            while (true) {
                SkipWhitespace();
                if (AtEnd || _source[_position] != '"') {
                    throw new JsonParserError("expected object key at '" + Rest + "'");
                }
                string name = ParseString();
                object key = SymbolizeNames
                    ? (object)_context.CreateSymbol(name, RubyEncoding.UTF8)
                    : MutableString.Create(name, RubyEncoding.UTF8);
                SkipWhitespace();
                if (AtEnd || _source[_position] != ':') {
                    throw new JsonParserError("expected ':' at '" + Rest + "'");
                }
                _position++;
                hash[key] = ParseValue(depth + 1);

                SkipWhitespace();
                if (AtEnd) {
                    throw new JsonParserError("unexpected end of input");
                }
                if (_source[_position] == ',') {
                    _position++;
                    continue;
                }
                if (_source[_position] == '}') {
                    _position++;
                    return hash;
                }
                throw new JsonParserError("expected ',' or '}' at '" + Rest + "'");
            }
        }

        private object ParseArray(int depth) {
            _position++; // '['
            var array = new RubyArray();
            SkipWhitespace();
            if (!AtEnd && _source[_position] == ']') {
                _position++;
                return array;
            }

            while (true) {
                array.Add(ParseValue(depth + 1));
                SkipWhitespace();
                if (AtEnd) {
                    throw new JsonParserError("unexpected end of input");
                }
                if (_source[_position] == ',') {
                    _position++;
                    continue;
                }
                if (_source[_position] == ']') {
                    _position++;
                    return array;
                }
                throw new JsonParserError("expected ',' or ']' at '" + Rest + "'");
            }
        }

        private string/*!*/ ParseString() {
            _position++; // opening quote
            var builder = new StringBuilder();
            while (true) {
                if (AtEnd) {
                    throw new JsonParserError("unterminated string");
                }
                char c = _source[_position++];
                if (c == '"') {
                    return builder.ToString();
                }
                if (c != '\\') {
                    // RFC 8259: U+0000 through U+001F must be escaped inside a string. The json gem
                    // rejects them unless asked not to (2.18.0's allow_control_characters).
                    if (c < ' ' && !AllowControlCharacters) {
                        _position--;
                        throw new JsonParserError("invalid ASCII control character in string: '" + Rest + "'");
                    }
                    builder.Append(c);
                    continue;
                }

                if (AtEnd) {
                    throw new JsonParserError("unterminated escape");
                }
                char escape = _source[_position++];
                switch (escape) {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        if (_position + 4 > _source.Length) {
                            throw new JsonParserError("invalid \\u escape");
                        }
                        builder.Append((char)Int32.Parse(_source.Substring(_position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _position += 4;
                        break;
                    default:
                        throw new JsonParserError("invalid escape '\\" + escape + "'");
                }
            }
        }

        private object ParseNumber() {
            int start = _position;
            if (!AtEnd && (_source[_position] == '-' || _source[_position] == '+')) {
                _position++;
            }
            bool isFloat = false;
            while (!AtEnd) {
                char c = _source[_position];
                if (c >= '0' && c <= '9') {
                    _position++;
                } else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') {
                    isFloat = true;
                    _position++;
                } else {
                    break;
                }
            }

            string text = _source.Substring(start, _position - start);
            if (text.Length == 0) {
                throw new JsonParserError("unexpected token at '" + Rest + "'");
            }

            if (isFloat) {
                double result;
                if (!Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) {
                    throw new JsonParserError("invalid number '" + text + "'");
                }
                return result;
            }

            int intResult;
            if (Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out intResult)) {
                return ScriptingRuntimeHelpers.Int32ToObject(intResult);
            }

            BigInteger bigResult;
            if (BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out bigResult)) {
                return bigResult;
            }

            throw new JsonParserError("invalid number '" + text + "'");
        }
    }
}

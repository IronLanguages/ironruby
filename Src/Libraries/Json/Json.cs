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

        // json/common.rb: what a max_nesting that is exceeded raises, from the generator as from the parser
        [RubyClass("NestingError", Extends = typeof(JsonNestingError), Inherits = typeof(JsonParserError))]
        public static class JsonNestingErrorOps {
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
            internal bool AllowNan;
            internal bool AsciiOnly;
            internal bool ScriptSafe;

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
                    AllowNan = defaults.AllowNan,
                    AsciiOnly = defaults.AsciiOnly,
                    ScriptSafe = defaults.ScriptSafe,
                };

                foreach (var entry in hash) {
                    string name = KeyName(entry.Key);
                    switch (name) {
                        case "indent": result.Indent = OptionString(entry.Value); break;
                        case "space": result.Space = OptionString(entry.Value); break;
                        case "space_before": result.SpaceBefore = OptionString(entry.Value); break;
                        case "object_nl": result.ObjectNl = OptionString(entry.Value); break;
                        case "array_nl": result.ArrayNl = OptionString(entry.Value); break;
                        case "allow_nan": result.AllowNan = RubyOps.IsTrue(entry.Value); break;
                        case "ascii_only": result.AsciiOnly = RubyOps.IsTrue(entry.Value); break;
                        case "script_safe":
                        case "escape_slash": result.ScriptSafe = RubyOps.IsTrue(entry.Value); break;
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
        public static MutableString/*!*/ Generate(ConversionStorage<MutableString>/*!*/ toS, object self, object obj,
            [Optional]object opts) {

            var writer = new Writer(toS, GeneratorOptions.Parse(opts, GeneratorOptions.Compact));
            writer.WriteValue(obj, 0);
            return MutableString.Create(writer.Builder.ToString(), RubyEncoding.UTF8);
        }

        [RubyMethod("pretty_generate", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ PrettyGenerate(ConversionStorage<MutableString>/*!*/ toS, object self, object obj,
            [Optional]object opts) {

            var writer = new Writer(toS, GeneratorOptions.Parse(opts, GeneratorOptions.Pretty()));
            writer.WriteValue(obj, 0);
            return MutableString.Create(writer.Builder.ToString(), RubyEncoding.UTF8);
        }

        private sealed class Writer {
            internal readonly StringBuilder/*!*/ Builder = new StringBuilder();
            private readonly ConversionStorage<MutableString>/*!*/ _toS;
            private readonly GeneratorOptions/*!*/ _options;

            internal Writer(ConversionStorage<MutableString>/*!*/ toS, GeneratorOptions/*!*/ options) {
                _toS = toS;
                _options = options;
            }

            // As the json gem counts it: each Array and Hash is a level, checked as it is entered.
            private void EnterLevel(int depth) {
                if (_options.MaxNesting > 0 && depth + 1 > _options.MaxNesting) {
                    throw new JsonNestingError(String.Format(
                        "nesting of {0} is too deep. Did you try to serialize objects with circular references?", depth));
                }
            }

            internal void WriteValue(object obj, int depth) {
                if (obj == null) {
                    Builder.Append("null");
                    return;
                }

                if (obj is bool) {
                    Builder.Append((bool)obj ? "true" : "false");
                    return;
                }

                if (obj is int || obj is BigInteger || obj is long) {
                    Builder.Append(obj.ToString());
                    return;
                }

                if (obj is double) {
                    double value = (double)obj;
                    if (Double.IsNaN(value) || Double.IsInfinity(value)) {
                        if (!_options.AllowNan) {
                            throw new JsonGeneratorError(String.Format("{0} not allowed in JSON", FloatToRubyString(value)));
                        }
                        Builder.Append(FloatToRubyString(value));
                        return;
                    }
                    AppendFloat(Builder, value);
                    return;
                }

                var str = obj as MutableString;
                if (str != null) {
                    WriteString(StringContent(str));
                    return;
                }

                var symbol = obj as RubySymbol;
                if (symbol != null) {
                    WriteString(symbol.ToString());
                    return;
                }

                var hash = obj as Hash;
                if (hash != null) {
                    WriteHash(hash, depth);
                    return;
                }

                var list = obj as IList<object>;
                if (list != null) {
                    WriteArray(list, depth);
                    return;
                }

                // anything else is its to_s as a JSON string, as Object#to_json is in the json gem
                WriteString(StringContent(Protocols.ConvertToString(_toS, obj)));
            }

            private void WriteHash(Hash/*!*/ hash, int depth) {
                EnterLevel(depth);
                Builder.Append('{');
                bool first = true;
                foreach (var entry in hash) {
                    if (!first) {
                        Builder.Append(',');
                    }
                    first = false;
                    NewLine(_options.ObjectNl, depth + 1);

                    // JSON object keys are strings: a String or Symbol as it is, anything else
                    // by its to_s - nil is "" (the Hash keeps it as a sentinel object, whose CLR
                    // ToString was "System.Object"), 1.5 is "1.5"
                    object key = CustomStringDictionary.ObjToNull(entry.Key);
                    var keyString = key as MutableString;
                    var keySymbol = key as RubySymbol;
                    if (keyString != null) {
                        WriteString(StringContent(keyString));
                    } else if (keySymbol != null) {
                        WriteString(keySymbol.ToString());
                    } else if (key == null) {
                        WriteString("");
                    } else {
                        WriteString(StringContent(Protocols.ConvertToString(_toS, key)));
                    }

                    Builder.Append(_options.SpaceBefore);
                    Builder.Append(':');
                    Builder.Append(_options.Space);
                    WriteValue(entry.Value, depth + 1);
                }
                if (!first) {
                    NewLine(_options.ObjectNl, depth);
                }
                Builder.Append('}');
            }

            private void WriteArray(IList<object>/*!*/ list, int depth) {
                EnterLevel(depth);
                Builder.Append('[');
                for (int i = 0; i < list.Count; i++) {
                    if (i > 0) {
                        Builder.Append(',');
                    }
                    NewLine(_options.ArrayNl, depth + 1);
                    WriteValue(list[i], depth + 1);
                }
                if (list.Count > 0) {
                    NewLine(_options.ArrayNl, depth);
                }
                Builder.Append(']');
            }

            private void NewLine(string/*!*/ newLine, int depth) {
                string indent = _options.Indent;
                if (newLine.Length == 0 && indent.Length == 0) {
                    return;
                }
                Builder.Append(newLine);
                for (int i = 0; i < depth; i++) {
                    Builder.Append(indent);
                }
            }

            private void WriteString(string/*!*/ value) {
                Builder.Append('"');
                foreach (char c in value) {
                    switch (c) {
                        case '"': Builder.Append("\\\""); break;
                        case '\\': Builder.Append("\\\\"); break;
                        case '\n': Builder.Append("\\n"); break;
                        case '\r': Builder.Append("\\r"); break;
                        case '\t': Builder.Append("\\t"); break;
                        case '\b': Builder.Append("\\b"); break;
                        case '\f': Builder.Append("\\f"); break;
                        case '/':
                            Builder.Append(_options.ScriptSafe ? "\\/" : "/");
                            break;
                        default:
                            // ascii_only escapes every non-ASCII character (a pair as its two halves),
                            // script_safe the two line terminators JavaScript does not allow in a string
                            if (c < 0x20 || (_options.AsciiOnly && c >= 0x80) ||
                                (_options.ScriptSafe && (c == '\u2028' || c == '\u2029'))) {
                                Builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            } else {
                                Builder.Append(c);
                            }
                            break;
                    }
                }
                Builder.Append('"');
            }
        }

        /// <summary>
        /// The characters of a String as JSON writes them, which is UTF-8. A string whose bytes are not
        /// valid UTF-8 is a GeneratorError, as in the json gem - a binary "\xFF" used to come out as "ÿ",
        /// its byte read as Latin-1. A string in another encoding is written as its characters.
        /// </summary>
        private static string/*!*/ StringContent(MutableString/*!*/ str) {
            var encoding = str.Encoding;
            if ((encoding == RubyEncoding.Binary || encoding == RubyEncoding.UTF8 || encoding == RubyEncoding.Ascii) && !str.IsAscii()) {
                byte[] bytes = str.ToByteArray();
                try {
                    return new UTF8Encoding(false, true).GetString(bytes);
                } catch (DecoderFallbackException) {
                    throw new JsonGeneratorError(String.Format("source sequence is illegal/malformed utf-8"));
                }
            }
            if (str.ContainsInvalidCharacters()) {
                throw new JsonGeneratorError(String.Format("source sequence is illegal/malformed utf-8"));
            }
            return str.ConvertToString();
        }

        private static string/*!*/ FloatToRubyString(double value) {
            return Double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity";
        }

        /// <summary>
        /// A Float the way the json gem (2.10 and later, its fpconv_dtoa) writes it: the shortest digits
        /// that read back as the same Float, as digits x 10**K. With E the exponent of the leading digit,
        /// a whole number is written out with ".0" while E is below 15 (100.0, 123456789012345.0) and in
        /// exponent form from there (1e+15); a number with a fraction is written out while K is above -7
        /// or E is within 10 of zero (0.0000001, 0.000123456789) and in exponent form otherwise (1e-10).
        /// The exponent form has no ".0" and no padding: 1e+20, 1.5e-16, 5e-324.
        /// </summary>
        internal static void AppendFloat(StringBuilder/*!*/ builder, double value) {
            if (value == 0) {
                builder.Append(Double.IsNegative(value) ? "-0.0" : "0.0");
                return;
            }
            if (value < 0) {
                builder.Append('-');
                value = -value;
            }

            // .NET's "R" is the shortest round-tripping representation; take its digits and exponent
            string r = value.ToString("R", CultureInfo.InvariantCulture);
            int exponent = 0;
            int e = r.IndexOfAny(new[] { 'E', 'e' });
            string mantissa = r;
            if (e >= 0) {
                exponent = Int32.Parse(r.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                mantissa = r.Substring(0, e);
            }
            int dot = mantissa.IndexOf('.');
            string whole = dot < 0 ? mantissa : mantissa.Substring(0, dot);
            string all = dot < 0 ? mantissa : whole + mantissa.Substring(dot + 1);
            string digits = all.TrimStart('0');
            int pointPosition = whole.Length + exponent - (all.Length - digits.Length);
            digits = digits.TrimEnd('0');
            int nd = digits.Length;
            int k = pointPosition - nd;                  // value = digits * 10**k
            int leading = k + nd - 1;                    // exponent of the leading digit

            if (k >= 0 && Math.Abs(leading) < 15) {
                builder.Append(digits).Append('0', k).Append(".0");
            } else if (k < 0 && (k > -7 || Math.Abs(leading) < 10)) {
                if (pointPosition > 0) {
                    builder.Append(digits, 0, pointPosition).Append('.').Append(digits, pointPosition, nd - pointPosition);
                } else {
                    builder.Append("0.").Append('0', -pointPosition).Append(digits);
                }
            } else {
                builder.Append(digits[0]);
                if (nd > 1) {
                    builder.Append('.').Append(digits, 1, nd - 1);
                }
                builder.Append('e').Append(leading < 0 ? '-' : '+').Append(Math.Abs(leading).ToString(CultureInfo.InvariantCulture));
            }
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

    public class JsonNestingError : JsonParserError {
        public JsonNestingError(string message) : base(message) { }
        public JsonNestingError() : this("nesting error") { }
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

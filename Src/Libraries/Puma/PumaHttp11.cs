/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * Puma's HTTP/1.1 request parser - ext/puma_http11 - for IronRuby.
 *
 * Copyright (c) 2005 Zed A. Shaw; the state machine is the one puma generates with Ragel from
 * ext/puma_http11/http11_parser_common.rl, taken from the table-driven output it ships for
 * JRuby (ext/puma_http11/org/jruby/puma/Http11Parser.java).  3-clause BSD, as puma is.
 *
 * ***************************************************************************/

// What this is, and where it differs from the C extension:
//
//   * Puma::HttpParser#execute, #finished?, #error?, #nread, #body, #reset, #finish and the env
//     Hash it fills are the C extension's (ext/puma_http11/puma_http11.c), message for message:
//     the same Puma::HttpParserError, the same "HTTP element X is longer than the N allowed
//     length (was M)" limits, the same keys (frozen UTF-8 "HTTP_*", with CONTENT_LENGTH and
//     CONTENT_TYPE bare) and the same ASCII-8BIT values, duplicate headers joined with ", ".
//   * Like the C extension, it upcases each header name inside the String it was handed (the
//     snake_upcase_field action writes through the pointer).  That is visible: puma's "Bad
//     headers" error quotes the buffer, and CRuby's message reads "HOST: localhost" there.
//     A frozen String is left alone.
//   * The limits the C extension fixes at compile time (PUMA_REQUEST_URI_MAX_LENGTH,
//     PUMA_REQUEST_PATH_MAX_LENGTH, PUMA_QUERY_STRING_MAX_LENGTH) are read from the environment
//     when the parser is first used, as puma's JRuby parser does - there is no compile step
//     here to hand them to.
//   * Puma::MiniSSL (TLS) is not part of this file; it is in MiniSsl.cs.

using System;
using System.Collections.Generic;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Puma {

    [RubyModule("Puma")]
    public static partial class PumaModule {

        [RubyException("HttpParserError")]
        public class HttpParserError : SystemException {
            public HttpParserError() : this(null, null) { }
            public HttpParserError(string message) : this(message, null) { }
            public HttpParserError(string message, Exception inner) : base(message ?? "Puma::HttpParserError", inner) { }
        }

        [RubyClass("HttpParser")]
        public sealed class HttpParser : RubyObject {
            private readonly Http11Parser/*!*/ _parser = new Http11Parser();

            public HttpParser(RubyClass/*!*/ rubyClass) : base(rubyClass) {
                _parser.Init();
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new HttpParser(ImmediateClass.NominalClass);
            }

            [RubyConstructor]
            public static HttpParser/*!*/ Create(RubyClass/*!*/ self) {
                return new HttpParser(self);
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static HttpParser/*!*/ Initialize(HttpParser/*!*/ self) {
                self._parser.Init();
                return self;
            }

            [RubyMethod("reset")]
            public static object Reset(HttpParser/*!*/ self) {
                self._parser.Init();
                return null;
            }

            [RubyMethod("finish")]
            public static bool Finish(HttpParser/*!*/ self) {
                return self._parser.IsFinished;
            }

            /// <summary>
            /// execute(req_hash, data, start) -> Integer: parses <paramref name="data"/> from
            /// <paramref name="start"/>, filling <paramref name="request"/>, and answers how much
            /// of it has been read in this parse so far.  Raises Puma::HttpParserError on
            /// malformed input or a field over its limit.
            /// </summary>
            [RubyMethod("execute")]
            public static int Execute(HttpParser/*!*/ self, [NotNull]Hash/*!*/ request, [NotNull]MutableString/*!*/ data, int start) {
                byte[] bytes = data.ToByteArray();
                if (start >= bytes.Length) {
                    throw new HttpParserError("Requested start is after data buffer end.");
                }

                var parser = self._parser;
                parser.Request = request;
                try {
                    parser.Execute(data, bytes, start);
                } finally {
                    parser.Request = null;
                }
                self._body = parser.Body;

                Limit.Header.Validate(parser.NRead);

                if (parser.HasError) {
                    throw new HttpParserError("Invalid HTTP format, parsing fails. Are you trying to open an SSL connection to a non-SSL Puma?");
                }
                return parser.NRead;
            }

            private MutableString _body;

            [RubyMethod("error?")]
            public static bool HasError(HttpParser/*!*/ self) {
                return self._parser.HasError;
            }

            [RubyMethod("finished?")]
            public static bool IsFinished(HttpParser/*!*/ self) {
                return self._parser.IsFinished;
            }

            [RubyMethod("nread")]
            public static int NRead(HttpParser/*!*/ self) {
                return self._parser.NRead;
            }

            [RubyMethod("body")]
            public static MutableString GetBody(HttpParser/*!*/ self) {
                return self._body;
            }
        }
    }

    /// <summary>
    /// The C extension's DEF_MAX_LENGTH table.  Each limit comes with the text its error message
    /// shows for it, which in the C extension is the macro's source text, stringified by the
    /// preprocessor: "longer than the (1024 * 10) allowed length", not "10240".  A limit set from
    /// the environment reads as the plain number, as it would compiled in with -D.
    /// </summary>
    internal sealed class Limit {
        internal readonly string/*!*/ Element;
        internal readonly int Max;
        internal readonly string/*!*/ Text;

        private Limit(string/*!*/ element, int max, string/*!*/ text) {
            Element = element;
            Max = max;
            Text = text;
        }

        internal static readonly Limit FieldName = new Limit("FIELD_NAME", 256, "256");
        internal static readonly Limit FieldValue = new Limit("FIELD_VALUE", 80 * 1024, "80 * 1024");
        internal static readonly Limit Fragment = new Limit("FRAGMENT", 1024, "1024");
        internal static readonly Limit Header = new Limit("HEADER", 1024 * (80 + 32), "(1024 * (80 + 32))");
        internal static readonly Limit RequestUri = FromEnvironment("REQUEST_URI", "PUMA_REQUEST_URI_MAX_LENGTH", 1024 * 12, "(1024 * 12)");
        internal static readonly Limit RequestPath = FromEnvironment("REQUEST_PATH", "PUMA_REQUEST_PATH_MAX_LENGTH", 8192, "(8192)");
        internal static readonly Limit QueryString = FromEnvironment("QUERY_STRING", "PUMA_QUERY_STRING_MAX_LENGTH", 1024 * 10, "(1024 * 10)");

        /// <summary>
        /// A positive integer from the environment, or the default - with the JRuby parser's
        /// warning when the value is there but is not one.
        /// </summary>
        private static Limit/*!*/ FromEnvironment(string/*!*/ element, string/*!*/ name, int defaultValue, string/*!*/ defaultText) {
            string value = Environment.GetEnvironmentVariable(name);
            if (!String.IsNullOrEmpty(value)) {
                uint parsed;
                if (UInt32.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parsed)
                    && parsed > 0 && parsed <= Int32.MaxValue) {
                    return new Limit(element, (int)parsed, parsed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                Console.Error.WriteLine("The value {0} for {1} is invalid. Using default value {2} instead.", value, name, defaultValue);
            }
            return new Limit(element, defaultValue, defaultText);
        }

        internal void Validate(int length) {
            if (length > Max) {
                throw new PumaModule.HttpParserError(String.Format(
                    "HTTP element {0} is longer than the {1} allowed length (was {2})", Element, Text, length));
            }
        }
    }

    /// <summary>
    /// The parser state machine: puma's Ragel machine (http11_parser_common.rl) in its
    /// table-driven form, with the actions of http11_parser.rl calling back into the env
    /// building below - the C extension's http_field, request_method and friends.
    /// </summary>
    internal sealed class Http11Parser {
        private const int Start = 1;
        private const int FirstFinal = 46;
        private const int Error = 0;

        private int _cs;
        private int _bodyStart;
        private int _nread;
        private int _mark;
        private int _fieldStart;
        private int _fieldLength;
        private int _queryStart;
        private byte[] _buffer;

        // Where this call to Execute started, and whether the header names before that point
        // are upcased in the buffer already - they are, unless the String was frozen and the
        // upcasing of an earlier call could not be written back to it.
        private int _executeOffset;
        private bool _earlierNamesUpcased;

        internal Hash Request;
        internal MutableString Body;

        internal void Init() {
            _cs = Start;
            _bodyStart = 0;
            _nread = 0;
            _mark = 0;
            _fieldStart = 0;
            _fieldLength = 0;
            _queryStart = 0;
            Body = null;
        }

        internal bool HasError {
            get { return _cs == Error; }
        }

        internal bool IsFinished {
            get { return _cs >= FirstFinal; }
        }

        internal int NRead {
            get { return _nread; }
        }

        #region env building (puma_http11.c)

        private static MutableString/*!*/ Key(string/*!*/ name) {
            return MutableString.Create(name, RubyEncoding.UTF8).Freeze();
        }

        private static readonly MutableString RequestMethodKey = Key("REQUEST_METHOD");
        private static readonly MutableString RequestUriKey = Key("REQUEST_URI");
        private static readonly MutableString FragmentKey = Key("FRAGMENT");
        private static readonly MutableString QueryStringKey = Key("QUERY_STRING");
        private static readonly MutableString ServerProtocolKey = Key("SERVER_PROTOCOL");
        private static readonly MutableString RequestPathKey = Key("REQUEST_PATH");

        // common_http_fields: the header names a request is expected to carry, with the key each
        // becomes.  All are HTTP_ plus the name except the two Rack keeps bare.
        private static readonly Dictionary<string, MutableString>/*!*/ CommonFields = CreateCommonFields();

        private static Dictionary<string, MutableString>/*!*/ CreateCommonFields() {
            string[] prefixed = {
                "ACCEPT", "ACCEPT_CHARSET", "ACCEPT_ENCODING", "ACCEPT_LANGUAGE", "ALLOW", "AUTHORIZATION",
                "CACHE_CONTROL", "CONNECTION", "CONTENT_ENCODING", "COOKIE", "DATE", "EXPECT", "FROM", "HOST",
                "IF_MATCH", "IF_MODIFIED_SINCE", "IF_NONE_MATCH", "IF_RANGE", "IF_UNMODIFIED_SINCE", "KEEP_ALIVE",
                "MAX_FORWARDS", "PRAGMA", "PROXY_AUTHORIZATION", "RANGE", "REFERER", "TE", "TRAILER",
                "TRANSFER_ENCODING", "UPGRADE", "USER_AGENT", "VIA", "X_FORWARDED_FOR", "X_REAL_IP", "WARNING",
            };
            var result = new Dictionary<string, MutableString>(StringComparer.Ordinal);
            foreach (var name in prefixed) {
                result[name] = Key("HTTP_" + name);
            }
            result["CONTENT_LENGTH"] = Key("CONTENT_LENGTH");
            result["CONTENT_TYPE"] = Key("CONTENT_TYPE");
            return result;
        }

        /// <summary>
        /// Capitalizes lower-case ASCII, turns dashes into underscores and underscores into
        /// commas - the C extension's snake_upcase_char, so that "X-Forwarded-For" and
        /// "X_Forwarded_For" do not end up as the same key.
        /// </summary>
        private static byte SnakeUpcase(byte c) {
            if (c >= (byte)'a' && c <= (byte)'z') {
                return (byte)(c & ~0x20);
            }
            if (c == (byte)'_') {
                return (byte)',';
            }
            if (c == (byte)'-') {
                return (byte)'_';
            }
            return c;
        }

        private static bool IsOws(byte c) {
            return c == (byte)' ' || c == (byte)'\t';
        }

        private MutableString/*!*/ Value(int start, int length) {
            var result = MutableString.CreateBinary(length);
            result.Append(_buffer, start, length);
            return result;
        }

        private void Set(MutableString/*!*/ key, MutableString/*!*/ value) {
            Request.RequireNotFrozen();
            Request[key] = value;
        }

        private void HttpField(int valueLength) {
            int fieldLength = _fieldLength;
            Limit.FieldName.Validate(fieldLength);
            Limit.FieldValue.Validate(valueLength);

            // Field names are tokens, so ASCII: a char per byte.  The machine has upcased them in
            // the buffer as it went (action snake_upcase_field).
            var name = new StringBuilder(fieldLength);
            for (int i = 0; i < fieldLength; i++) {
                int at = _fieldStart + i;
                byte c = _buffer[at];
                name.Append((char)((at < _executeOffset && !_earlierNamesUpcased) ? SnakeUpcase(c) : c));
            }
            string upcased = name.ToString();

            MutableString key;
            if (!CommonFields.TryGetValue(upcased, out key)) {
                key = MutableString.Create("HTTP_" + upcased, RubyEncoding.UTF8).Freeze();
            }

            int mark = _mark;
            int length = valueLength;
            while (length > 0 && IsOws(_buffer[mark + length - 1])) {
                length--;
            }
            while (length > 0 && IsOws(_buffer[mark])) {
                length--;
                mark++;
            }

            object existing;
            var current = Request.TryGetValue(key, out existing) ? existing as MutableString : null;
            if (current == null) {
                Set(key, Value(mark, length));
            } else {
                // A repeated header: its values, comma separated, as RFC 9110 allows.
                current.Append((byte)',').Append((byte)' ').Append(_buffer, mark, length);
            }
        }

        private void RequestMethod(int length) {
            Set(RequestMethodKey, Value(_mark, length));
        }

        private void RequestUri(int length) {
            Limit.RequestUri.Validate(length);
            Set(RequestUriKey, Value(_mark, length));
        }

        private void Fragment(int length) {
            Limit.Fragment.Validate(length);
            Set(FragmentKey, Value(_mark, length));
        }

        private void RequestPath(int length) {
            Limit.RequestPath.Validate(length);
            Set(RequestPathKey, Value(_mark, length));
        }

        private void QueryString(int length) {
            Limit.QueryString.Validate(length);
            Set(QueryStringKey, Value(_queryStart, length));
        }

        private void ServerProtocol(int length) {
            Set(ServerProtocolKey, Value(_mark, length));
        }

        private void HeaderDone(int at, int length) {
            Body = Value(at, length);
        }

        #endregion

        #region the machine

        /// <summary>
        /// Runs the machine over data[offset..] and answers nread.  The state, and the marks,
        /// carry over from call to call: the caller appends to its buffer and calls again with the
        /// whole of it, starting where the last call stopped.
        /// </summary>
        internal int Execute(MutableString/*!*/ source, byte[]/*!*/ data, int offset) {
            bool frozen = source.IsFrozen;
            _executeOffset = offset;
            _earlierNamesUpcased = !frozen;
            try {
                return Execute(data, offset);
            } finally {
                // What snake_upcase_field changed goes back into the caller's String, where the C
                // extension's writes land.
                if (!frozen) {
                    for (int i = offset; i < data.Length; i++) {
                        if (_upcased != null && _upcased[i - offset]) {
                            source.SetByte(i, data[i]);
                        }
                    }
                }
                _upcased = null;
            }
        }

        private bool[] _upcased;

        private void UpcaseInPlace(int p) {
            byte c = _buffer[p];
            byte up = SnakeUpcase(c);
            if (up != c) {
                _buffer[p] = up;
                if (_upcased == null) {
                    _upcased = new bool[_buffer.Length - _executeOffset];
                }
                _upcased[p - _executeOffset] = true;
            }
        }

        private int Execute(byte[]/*!*/ data, int offset) {
            int p = offset;
            int pe = data.Length;
            int cs = _cs;
            _buffer = data;

            if (p != pe && cs != Error) {
                while (true) {
                    int keys = KeyOffsets[cs];
                    int trans = IndexOffsets[cs];
                    int c = data[p];

                    bool matched = false;
                    int klen = SingleLengths[cs];
                    if (klen > 0) {
                        int lower = keys;
                        int upper = keys + klen - 1;
                        while (upper >= lower) {
                            int mid = lower + ((upper - lower) >> 1);
                            if (c < TransKeys[mid]) {
                                upper = mid - 1;
                            } else if (c > TransKeys[mid]) {
                                lower = mid + 1;
                            } else {
                                trans += mid - keys;
                                matched = true;
                                break;
                            }
                        }
                        if (!matched) {
                            keys += klen;
                            trans += klen;
                        }
                    }

                    if (!matched) {
                        klen = RangeLengths[cs];
                        if (klen > 0) {
                            int lower = keys;
                            int upper = keys + (klen << 1) - 2;
                            while (upper >= lower) {
                                int mid = lower + (((upper - lower) >> 1) & ~1);
                                if (c < TransKeys[mid]) {
                                    upper = mid - 2;
                                } else if (c > TransKeys[mid + 1]) {
                                    lower = mid + 2;
                                } else {
                                    trans += (mid - keys) >> 1;
                                    matched = true;
                                    break;
                                }
                            }
                            if (!matched) {
                                trans += klen;
                            }
                        }
                    }

                    trans = Indicies[trans];
                    cs = TransTargs[trans];

                    bool done = false;
                    if (TransActions[trans] != 0) {
                        int acts = TransActions[trans];
                        int nacts = Actions[acts++];
                        while (nacts-- > 0 && !done) {
                            switch (Actions[acts++]) {
                                case 0: _mark = p; break;
                                case 1: _fieldStart = p; break;
                                case 2: UpcaseInPlace(p); break;
                                case 3: _fieldLength = p - _fieldStart; break;
                                case 4: _mark = p; break;
                                case 5: HttpField(p - _mark); break;
                                case 6: RequestMethod(p - _mark); break;
                                case 7: RequestUri(p - _mark); break;
                                case 8: Fragment(p - _mark); break;
                                case 9: _queryStart = p; break;
                                case 10: QueryString(p - _queryStart); break;
                                case 11: ServerProtocol(p - _mark); break;
                                case 12: RequestPath(p - _mark); break;
                                case 13:
                                    _bodyStart = p + 1;
                                    HeaderDone(p + 1, pe - p - 1);
                                    // fbreak
                                    p += 1;
                                    done = true;
                                    break;
                            }
                        }
                    }

                    if (done || cs == Error) {
                        break;
                    }
                    if (++p == pe) {
                        break;
                    }
                }
            }

            _cs = cs;
            _nread += p - offset;
            return _nread;
        }

        private static readonly sbyte[] Actions = new sbyte[] {
            0, 1, 0, 1, 2, 1, 3, 1, 4, 1, 5, 1, 6, 1, 7, 1,
            8, 1, 9, 1, 11, 1, 12, 1, 13, 2, 0, 8, 2, 1, 2, 2,
            4, 5, 2, 10, 7, 2, 12, 7, 3, 9, 10, 7,
        };

        private static readonly short[] KeyOffsets = new short[] {
            0, 0, 8, 17, 27, 29, 30, 31, 32, 33, 34, 36, 39, 41, 44, 45,
            61, 62, 78, 85, 91, 99, 107, 117, 125, 134, 142, 150, 159, 168, 177, 186,
            195, 204, 213, 222, 231, 240, 249, 258, 267, 276, 285, 294, 303, 312, 313,
        };

        private static readonly ushort[] TransKeys = new ushort[] {
            36, 95, 45, 46, 48, 57, 65, 90, 32, 36, 95, 45, 46, 48, 57, 65,
            90, 42, 43, 47, 58, 45, 57, 65, 90, 97, 122, 32, 35, 72, 84, 84,
            80, 47, 48, 57, 46, 48, 57, 48, 57, 13, 48, 57, 10, 13, 33, 124,
            126, 35, 39, 42, 43, 45, 46, 48, 57, 65, 90, 94, 122, 10, 33, 58,
            124, 126, 35, 39, 42, 43, 45, 46, 48, 57, 65, 90, 94, 122, 13, 32,
            127, 0, 8, 10, 31, 13, 127, 0, 8, 10, 31, 32, 60, 62, 127, 0,
            31, 34, 35, 32, 60, 62, 127, 0, 31, 34, 35, 43, 58, 45, 46, 48,
            57, 65, 90, 97, 122, 32, 34, 35, 60, 62, 127, 0, 31, 32, 34, 35,
            60, 62, 63, 127, 0, 31, 32, 34, 35, 60, 62, 127, 0, 31, 32, 34,
            35, 60, 62, 127, 0, 31, 32, 36, 95, 45, 46, 48, 57, 65, 90, 32,
            36, 95, 45, 46, 48, 57, 65, 90, 32, 36, 95, 45, 46, 48, 57, 65,
            90, 32, 36, 95, 45, 46, 48, 57, 65, 90, 32, 36, 95, 45, 46, 48,
            57, 65, 90, 32, 36, 95, 45, 46, 48, 57, 65, 90, 32, 36, 95, 45,
            46, 48, 57, 65, 90, 32, 36, 95, 45, 46, 48, 57, 65, 90, 32, 36,
            95, 45, 46, 48, 57, 65, 90, 32, 36, 95, 45, 46, 48, 57, 65, 90,
            32, 36, 95, 45, 46, 48, 57, 65, 90, 32, 36, 95, 45, 46, 48, 57,
            65, 90, 32, 36, 95, 45, 46, 48, 57, 65, 90, 32, 36, 95, 45, 46,
            48, 57, 65, 90, 32, 36, 95, 45, 46, 48, 57, 65, 90, 32, 36, 95,
            45, 46, 48, 57, 65, 90, 32, 36, 95, 45, 46, 48, 57, 65, 90, 32,
            36, 95, 45, 46, 48, 57, 65, 90, 32, 0,
        };

        private static readonly sbyte[] SingleLengths = new sbyte[] {
            0, 2, 3, 4, 2, 1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 4,
            1, 4, 3, 2, 4, 4, 2, 6, 7, 6, 6, 3, 3, 3, 3, 3,
            3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 0,
        };

        private static readonly sbyte[] RangeLengths = new sbyte[] {
            0, 3, 3, 3, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0, 6,
            0, 6, 2, 2, 2, 2, 4, 1, 1, 1, 1, 3, 3, 3, 3, 3,
            3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 0,
        };

        private static readonly short[] IndexOffsets = new short[] {
            0, 0, 6, 13, 21, 24, 26, 28, 30, 32, 34, 36, 39, 41, 44, 46,
            57, 59, 70, 76, 81, 88, 95, 102, 110, 119, 127, 135, 142, 149, 156, 163,
            170, 177, 184, 191, 198, 205, 212, 219, 226, 233, 240, 247, 254, 261, 263,
        };

        private static readonly sbyte[] Indicies = new sbyte[] {
            0, 0, 0, 0, 0, 1, 2, 3, 3, 3, 3, 3, 1, 4, 5, 6,
            7, 5, 5, 5, 1, 8, 9, 1, 10, 1, 11, 1, 12, 1, 13, 1,
            14, 1, 15, 1, 16, 15, 1, 17, 1, 18, 17, 1, 19, 1, 20, 21,
            21, 21, 21, 21, 21, 21, 21, 21, 1, 22, 1, 23, 24, 23, 23, 23,
            23, 23, 23, 23, 23, 1, 26, 27, 1, 1, 1, 25, 29, 1, 1, 1,
            28, 30, 1, 1, 1, 1, 1, 31, 32, 1, 1, 1, 1, 1, 33, 34,
            35, 34, 34, 34, 34, 1, 8, 1, 9, 1, 1, 1, 1, 35, 36, 1,
            38, 1, 1, 39, 1, 1, 37, 40, 1, 42, 1, 1, 1, 1, 41, 43,
            1, 45, 1, 1, 1, 1, 44, 2, 46, 46, 46, 46, 46, 1, 2, 47,
            47, 47, 47, 47, 1, 2, 48, 48, 48, 48, 48, 1, 2, 49, 49, 49,
            49, 49, 1, 2, 50, 50, 50, 50, 50, 1, 2, 51, 51, 51, 51, 51,
            1, 2, 52, 52, 52, 52, 52, 1, 2, 53, 53, 53, 53, 53, 1, 2,
            54, 54, 54, 54, 54, 1, 2, 55, 55, 55, 55, 55, 1, 2, 56, 56,
            56, 56, 56, 1, 2, 57, 57, 57, 57, 57, 1, 2, 58, 58, 58, 58,
            58, 1, 2, 59, 59, 59, 59, 59, 1, 2, 60, 60, 60, 60, 60, 1,
            2, 61, 61, 61, 61, 61, 1, 2, 62, 62, 62, 62, 62, 1, 2, 63,
            63, 63, 63, 63, 1, 2, 1, 1, 0,
        };

        private static readonly sbyte[] TransTargs = new sbyte[] {
            2, 0, 3, 27, 4, 22, 24, 23, 5, 20, 6, 7, 8, 9, 10, 11,
            12, 13, 14, 15, 16, 17, 46, 17, 18, 19, 14, 18, 19, 14, 5, 21,
            5, 21, 22, 23, 5, 24, 20, 25, 5, 26, 20, 5, 26, 20, 28, 29,
            30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45,
        };

        private static readonly sbyte[] TransActions = new sbyte[] {
            1, 0, 11, 0, 1, 1, 1, 1, 13, 13, 1, 0, 0, 0, 0, 0,
            0, 0, 19, 0, 0, 28, 23, 3, 5, 7, 31, 7, 0, 9, 25, 1,
            15, 0, 0, 0, 37, 0, 37, 21, 40, 17, 40, 34, 0, 34, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        };

        #endregion
    }
}

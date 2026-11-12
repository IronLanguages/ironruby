/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Prism {

    /// <summary>
    /// The prism gem's native half, for IronRuby.
    ///
    /// ruby/prism's Ruby library is a decoder: every entry point calls one of libprism's
    /// pm_serialize_* functions and hands the bytes to Prism::Serialize, which is pure
    /// Ruby.  Upstream supplies those bytes from a CRuby C extension, or - on other
    /// implementations - through the ffi gem, which IronRuby cannot load.
    ///
    /// IronRuby links libprism already: it is the parser that compiles every file this
    /// interpreter runs.  So the third backend is this module, and
    /// Src/StdLib/ironruby/prism/ironruby.rb is the same thin layer upstream's
    /// prism/ffi.rb is, minus FFI.
    /// </summary>
    [RubyModule("Prism")]
    public static class PrismOps {

        [RubyModule("LibRubyParser")]
        public static class LibRubyParserOps {

            /// <summary>
            /// libprism's own version string - Prism::VERSION.
            /// </summary>
            [RubyMethod("version", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Version(RubyModule/*!*/ self) {
                return MutableString.CreateAscii(IronRuby.Prism.PrismSerialize.Version());
            }

            [RubyMethod("serialize_parse", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ SerializeParse(RubyModule/*!*/ self,
                [DefaultProtocol, NotNull]MutableString/*!*/ source, [DefaultProtocol]MutableString options) {
                return MutableString.CreateBinary(
                    IronRuby.Prism.PrismSerialize.Parse(source.ToByteArray(), Bytes(options)));
            }

            [RubyMethod("serialize_lex", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ SerializeLex(RubyModule/*!*/ self,
                [DefaultProtocol, NotNull]MutableString/*!*/ source, [DefaultProtocol]MutableString options) {
                return MutableString.CreateBinary(
                    IronRuby.Prism.PrismSerialize.Lex(source.ToByteArray(), Bytes(options)));
            }

            [RubyMethod("serialize_parse_comments", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ SerializeParseComments(RubyModule/*!*/ self,
                [DefaultProtocol, NotNull]MutableString/*!*/ source, [DefaultProtocol]MutableString options) {
                return MutableString.CreateBinary(
                    IronRuby.Prism.PrismSerialize.ParseComments(source.ToByteArray(), Bytes(options)));
            }

            [RubyMethod("serialize_parse_lex", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ SerializeParseLex(RubyModule/*!*/ self,
                [DefaultProtocol, NotNull]MutableString/*!*/ source, [DefaultProtocol]MutableString options) {
                return MutableString.CreateBinary(
                    IronRuby.Prism.PrismSerialize.ParseLex(source.ToByteArray(), Bytes(options)));
            }

            [RubyMethod("parse_success?", RubyMethodAttributes.PublicSingleton)]
            public static bool ParseSuccess(RubyModule/*!*/ self,
                [DefaultProtocol, NotNull]MutableString/*!*/ source, [DefaultProtocol]MutableString options) {
                return IronRuby.Prism.PrismSerialize.ParseSuccess(source.ToByteArray(), Bytes(options));
            }

            /// <summary>
            /// [level, formatted] - the level is -1 for "no errors", and otherwise 0 syntax,
            /// 1 argument, 2 load, which is what Prism.parse(raise_error: ...) raises.  The
            /// formatted bytes are the encoding name, a NUL, then the message.
            /// </summary>
            [RubyMethod("errors_format", RubyMethodAttributes.PublicSingleton)]
            public static RubyArray/*!*/ ErrorsFormat(RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ source,
                [DefaultProtocol]MutableString options, [DefaultProtocol]int formatType) {
                byte[] formatted;
                int level = IronRuby.Prism.PrismSerialize.ErrorsFormat(source.ToByteArray(), Bytes(options), formatType, out formatted);

                var result = new RubyArray(2);
                result.Add(level);
                result.Add(MutableString.CreateBinary(formatted));
                return result;
            }

            /// <summary>
            /// 1 true, 0 false, -1 "not an ascii-compatible encoding" - prism's
            /// pm_string_query_t, which Prism::StringQuery turns into booleans.
            /// </summary>
            [RubyMethod("string_query", RubyMethodAttributes.PublicSingleton)]
            public static int StringQuery(RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ kind,
                [DefaultProtocol, NotNull]MutableString/*!*/ source, [DefaultProtocol, NotNull]MutableString/*!*/ encodingName) {
                return IronRuby.Prism.PrismSerialize.StringQuery(kind.ToString(), source.ToByteArray(), encodingName.ToString());
            }

            private static byte[] Bytes(MutableString str) {
                return str == null ? null : str.ToByteArray();
            }
        }
    }
}

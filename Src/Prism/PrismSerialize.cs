using System;
using System.Runtime.InteropServices;

namespace IronRuby.Prism {
    /// <summary>
    /// prism's serialization API, handed over as raw bytes.
    ///
    /// This is the half of libprism the *Ruby* library needs rather than the compiler:
    /// ruby/prism's own lib/prism ships a CRuby C extension and an FFI backend, and both
    /// do nothing more than call one of the pm_serialize_* functions and let
    /// Prism::Serialize decode the result in Ruby.  IronRuby already links libprism for
    /// its own front end, so the third backend (Src/StdLib/ironruby/prism/ironruby.rb)
    /// is this class - no ffi gem, no second copy of the parser.
    ///
    /// The options blob is built in Ruby, by the same code the FFI backend uses; see
    /// prism's docs/serialization.md for its layout.
    /// </summary>
    public static class PrismSerialize {
        private delegate void SerializeCall(IntPtr buffer, IntPtr source, nuint size, IntPtr data);

        // Every call into prism's parser goes through PrismNative.WithParserStack: the parser is recursive C,
        // and a deeply nested source needs more native stack than the calling thread may have.
        private static byte[]/*!*/ Serialize(SerializeCall/*!*/ call, byte[]/*!*/ source, byte[] options) {
            return PrismNative.WithParserStack(source.Length, () => SerializeHere(call, source, options));
        }

        private static byte[]/*!*/ SerializeHere(SerializeCall/*!*/ call, byte[]/*!*/ source, byte[] options) {
            PrismNative.EnsureResolver();

            IntPtr buffer = IntPtr.Zero;
            // A zero-length array pins to a null-ish address on some runtimes; prism only
            // reads `size` bytes, so a one-byte scratch keeps the pointer valid.
            GCHandle pinnedSource = GCHandle.Alloc(source.Length == 0 ? new byte[1] : source, GCHandleType.Pinned);
            GCHandle pinnedOptions = options != null ? GCHandle.Alloc(options, GCHandleType.Pinned) : default;
            try {
                buffer = PrismNative.pm_buffer_new();
                call(buffer, pinnedSource.AddrOfPinnedObject(), (nuint)source.Length,
                    options != null ? pinnedOptions.AddrOfPinnedObject() : IntPtr.Zero);
                int length = checked((int)PrismNative.pm_buffer_length(buffer));
                byte[] result = new byte[length];
                Marshal.Copy(PrismNative.pm_buffer_value(buffer), result, 0, length);
                return result;
            } finally {
                if (buffer != IntPtr.Zero) PrismNative.pm_buffer_free(buffer);
                pinnedSource.Free();
                if (options != null) pinnedOptions.Free();
            }
        }

        public static byte[]/*!*/ Parse(byte[]/*!*/ source, byte[] options) {
            return Serialize(PrismNative.pm_serialize_parse, source, options);
        }

        public static byte[]/*!*/ Lex(byte[]/*!*/ source, byte[] options) {
            return Serialize(PrismNative.pm_serialize_lex, source, options);
        }

        public static byte[]/*!*/ ParseComments(byte[]/*!*/ source, byte[] options) {
            return Serialize(PrismNative.pm_serialize_parse_comments, source, options);
        }

        public static byte[]/*!*/ ParseLex(byte[]/*!*/ source, byte[] options) {
            return Serialize(PrismNative.pm_serialize_parse_lex, source, options);
        }

        public static bool ParseSuccess(byte[]/*!*/ source, byte[] options) {
            return PrismNative.WithParserStack(source.Length, () => ParseSuccessHere(source, options));
        }

        private static bool ParseSuccessHere(byte[]/*!*/ source, byte[] options) {
            PrismNative.EnsureResolver();

            GCHandle pinnedSource = GCHandle.Alloc(source.Length == 0 ? new byte[1] : source, GCHandleType.Pinned);
            GCHandle pinnedOptions = options != null ? GCHandle.Alloc(options, GCHandleType.Pinned) : default;
            try {
                return PrismNative.pm_serialize_parse_success_p(pinnedSource.AddrOfPinnedObject(), (nuint)source.Length,
                    options != null ? pinnedOptions.AddrOfPinnedObject() : IntPtr.Zero);
            } finally {
                pinnedSource.Free();
                if (options != null) pinnedOptions.Free();
            }
        }

        /// <summary>
        /// Formats the parse errors into <paramref name="formatted"/> and answers their
        /// level: -1 no errors, 0 syntax, 1 argument, 2 load.  This is what
        /// Prism.parse(raise_error: ...) turns into an exception.
        /// </summary>
        public static int ErrorsFormat(byte[]/*!*/ source, byte[] options, int formatType, out byte[] formatted) {
            var result = PrismNative.WithParserStack(source.Length, () => {
                int level = ErrorsFormatHere(source, options, formatType, out byte[] text);
                return (level, text);
            });
            formatted = result.text;
            return result.level;
        }

        private static int ErrorsFormatHere(byte[]/*!*/ source, byte[] options, int formatType, out byte[] formatted) {
            PrismNative.EnsureResolver();

            IntPtr buffer = IntPtr.Zero;
            GCHandle pinnedSource = GCHandle.Alloc(source.Length == 0 ? new byte[1] : source, GCHandleType.Pinned);
            GCHandle pinnedOptions = options != null ? GCHandle.Alloc(options, GCHandleType.Pinned) : default;
            try {
                buffer = PrismNative.pm_buffer_new();
                int level = PrismNative.pm_serialize_parse_errors_format(buffer, pinnedSource.AddrOfPinnedObject(),
                    (nuint)source.Length, options != null ? pinnedOptions.AddrOfPinnedObject() : IntPtr.Zero, formatType);

                int length = checked((int)PrismNative.pm_buffer_length(buffer));
                formatted = new byte[length];
                Marshal.Copy(PrismNative.pm_buffer_value(buffer), formatted, 0, length);
                return level;
            } finally {
                if (buffer != IntPtr.Zero) PrismNative.pm_buffer_free(buffer);
                pinnedSource.Free();
                if (options != null) pinnedOptions.Free();
            }
        }

        /// <summary>
        /// libprism's own version string, which is what Prism::VERSION reports.  Reading it
        /// from the library rather than hard-coding it is what keeps the Ruby side honest
        /// when the bundled libprism is upgraded.
        /// </summary>
        public static string/*!*/ Version() {
            PrismNative.EnsureResolver();
            return Marshal.PtrToStringAnsi(PrismNative.pm_version()) ?? "";
        }

        /// <summary>
        /// 1 / 0 / -1 for true / false / "that encoding is not ascii-compatible".
        /// </summary>
        public static int StringQuery(string/*!*/ kind, byte[]/*!*/ source, string/*!*/ encodingName) {
            PrismNative.EnsureResolver();

            GCHandle pinned = GCHandle.Alloc(source.Length == 0 ? new byte[1] : source, GCHandleType.Pinned);
            try {
                IntPtr p = pinned.AddrOfPinnedObject();
                nuint n = (nuint)source.Length;
                switch (kind) {
                    case "local": return PrismNative.pm_string_query_local(p, n, encodingName);
                    case "constant": return PrismNative.pm_string_query_constant(p, n, encodingName);
                    case "method_name": return PrismNative.pm_string_query_method_name(p, n, encodingName);
                    case "keyword": return PrismNative.pm_string_query_keyword(p, n, encodingName);
                    default: throw new ArgumentException("unknown query: " + kind, nameof(kind));
                }
            } finally {
                pinned.Free();
            }
        }
    }
}

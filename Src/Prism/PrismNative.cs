using System;
using System.Runtime.InteropServices;

namespace IronRuby.Prism {
    /// <summary>
    /// P/Invoke bindings for libprism (the CRuby parser, github.com/ruby/prism).
    /// All prism handles are opaque pointers; memory is owned by prism and released
    /// via the matching *_free functions.
    /// </summary>
    internal static class PrismNative {
        private const string Lib = "prism";

        /// <summary>
        /// The file name the prism build produces on this platform. `make shared` names it
        /// after RbConfig's SOEXT, so it is libprism.so on Linux, libprism.dll under the
        /// Windows DevKit (MinGW keeps the lib prefix) and libprism.dylib on macOS. The
        /// plain Windows spelling prism.dll is accepted too, for a hand-built or vendored
        /// copy.
        /// </summary>
        private static readonly string[] FileNames =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { "libprism.dll", "prism.dll" } :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new[] { "libprism.dylib", "prism.dylib" } :
            new[] { "libprism.so", "prism.so" };

        static PrismNative() {
            NativeLibrary.SetDllImportResolver(typeof(PrismNative).Assembly, (name, assembly, path) => {
                if (name == Lib) {
                    // Beside the host first, then beside this assembly: a plugin's IronRuby (an
                    // irubyc --library, say) is not in the host's directory, and on Windows the
                    // runtime's own probing would not find it by the lib-prefixed name either.
                    foreach (string dir in SearchDirectories()) {
                        foreach (string fileName in FileNames) {
                            string local = System.IO.Path.Combine(dir, fileName);
                            if (System.IO.File.Exists(local)) {
                                return NativeLibrary.Load(local);
                            }
                        }
                    }
                    // Not beside the host: let the OS loader look on its own search path
                    // (LD_LIBRARY_PATH, PATH, the app directory) before giving up.
                    foreach (string fileName in FileNames) {
                        if (NativeLibrary.TryLoad(fileName, out IntPtr handle)) {
                            return handle;
                        }
                    }
                }
                return IntPtr.Zero;
            });
        }

        private static System.Collections.Generic.IEnumerable<string> SearchDirectories() {
            yield return AppContext.BaseDirectory;
            string location = typeof(PrismNative).Assembly.Location;
            if (!string.IsNullOrEmpty(location)) {
                string own = System.IO.Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(own)) yield return own;
            }
        }

        /// <summary>
        /// Forces the DllImport resolver above to be installed. Other types in this assembly
        /// P/Invoke "prism" as well (PrismLex), and a static constructor only runs when its own
        /// type is first touched - so the resolver has to be armed before any of them is called.
        /// </summary>
        internal static void EnsureResolver() {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PrismNative).TypeHandle);
        }

        // A source this short cannot nest deeply enough to need more native stack than the margin
        // StackGuard.IsSufficient leaves.
        private const int InlineSourceLimit = 128;

        /// <summary>
        /// Runs <paramref name="call"/>, a call into prism's parser for a source of <paramref name="sourceLength"/>
        /// bytes, on a stack big enough for it. The parser is recursive C: it takes about half a KB of native
        /// stack per level of nesting and accepts 10000 levels - some 5 MB, more than a .NET thread has (1.5 MB
        /// by default, 1 MB for the main thread on Windows), and running out of it there kills the process. A
        /// source too short to nest that deep is parsed right here; any other on StackGuard's large-stack thread.
        /// </summary>
        internal static T WithParserStack<T>(int sourceLength, Func<T>/*!*/ call) {
            if (sourceLength <= InlineSourceLimit && IronRuby.Runtime.StackGuard.IsSufficient) {
                return call();
            }
            return IronRuby.Runtime.StackGuard.RunOnLargeStack(call);
        }

        // arena + parser + node lifecycle
        [DllImport(Lib)] internal static extern IntPtr pm_arena_new();
        [DllImport(Lib)] internal static extern void pm_arena_free(IntPtr arena);
        [DllImport(Lib)] internal static extern IntPtr pm_parser_new(IntPtr arena, IntPtr source, nuint size, IntPtr options);
        [DllImport(Lib)] internal static extern void pm_parser_free(IntPtr parser);
        [DllImport(Lib)] internal static extern IntPtr pm_parse(IntPtr parser);

        // buffers (opaque)
        [DllImport(Lib)] internal static extern IntPtr pm_buffer_new();
        [DllImport(Lib)] internal static extern void pm_buffer_free(IntPtr buffer);
        [DllImport(Lib)] internal static extern IntPtr pm_buffer_value(IntPtr buffer);
        [DllImport(Lib)] internal static extern nuint pm_buffer_length(IntPtr buffer);

        // AST dumps
        [DllImport(Lib)] internal static extern void pm_dump_json(IntPtr buffer, IntPtr parser, IntPtr node);
        [DllImport(Lib)] internal static extern void pm_serialize_parse(IntPtr buffer, IntPtr source, nuint size, IntPtr data);
        [DllImport(Lib)] internal static extern void pm_serialize_lex(IntPtr buffer, IntPtr source, nuint size, IntPtr data);
        [DllImport(Lib)] internal static extern void pm_serialize_parse_comments(IntPtr buffer, IntPtr source, nuint size, IntPtr data);
        [DllImport(Lib)] internal static extern void pm_serialize_parse_lex(IntPtr buffer, IntPtr source, nuint size, IntPtr data);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool pm_serialize_parse_success_p(IntPtr source, nuint size, IntPtr data);

        // Formats the parse errors into the buffer and answers their level: -1 none,
        // 0 syntax, 1 argument, 2 load.  format_type is pm_errors_format_type_t:
        // 1 plain, 2 bold, 3 color.
        [DllImport(Lib)] internal static extern sbyte pm_serialize_parse_errors_format(IntPtr buffer, IntPtr source, nuint size, IntPtr data, int formatType);

        // The prism library's own version string ("2.0.0"), which is what the Ruby
        // library reports as Prism::VERSION.
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern IntPtr pm_version();

        // Whether a string would lex as a local variable name / a constant name / a
        // method name / a keyword.  Returns pm_string_query_t: -1 error, 0 false, 1 true.
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern int pm_string_query_local(IntPtr source, nuint length, string encodingName);
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern int pm_string_query_constant(IntPtr source, nuint length, string encodingName);
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern int pm_string_query_method_name(IntPtr source, nuint length, string encodingName);
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern int pm_string_query_keyword(IntPtr source, nuint length, string encodingName);

        // diagnostics
        [DllImport(Lib)] internal static extern int pm_parser_start_line(IntPtr parser);
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern IntPtr pm_parser_encoding_name(IntPtr parser);
    }
}

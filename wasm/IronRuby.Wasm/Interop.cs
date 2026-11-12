using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using IronRuby;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;

namespace IronRuby.Wasm {
    /// <summary>
    /// What the page calls. All results are JSON strings, so the JavaScript side needs no
    /// marshalling knowledge beyond string and Uint8Array.
    /// </summary>
    public static partial class Interop {
        public const string Root = "/ironruby";
        public const string AssemblyDir = Root + "/assemblies";

        private static ScriptRuntime _runtime;
        private static ScriptEngine _engine;
        private static object _kernel;
        private static OutputStream _stdout, _stderr;
        private static bool _resolverInstalled;
        private static readonly Dictionary<string, Assembly> _loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Unpacks the standard library (a zip whose top level holds ironruby/, ruby/4.0/, ...)
        /// into the in-memory file system and starts the Ruby runtime with the core prelude.
        /// options: JSON, e.g. {"jit":false,"osr":false,"streaming":true}.
        /// </summary>
        [JSExport]
        public static string Boot(byte[] stdlibZip, string options) {
            var total = Stopwatch.StartNew();
            var timings = new List<KeyValuePair<string, double>>();
            // The session is published (the fields set) only once it has booted: after a boot that failed in the
            // prelude or in boot.rb, Run found an engine with no kernel, and a second boot that failed had already
            // replaced the streams the first one's runtime writes to.
            OutputStream stdout = null, stderr = null;
            try {
                bool jit = true, osr = true, prelude = true, legacyParser = false;
                var languageOptions = new Dictionary<string, object>();
                string[] extraPaths = Array.Empty<string>();
                if (!string.IsNullOrEmpty(options)) {
                    using var doc = JsonDocument.Parse(options);
                    foreach (var p in doc.RootElement.EnumerateObject()) {
                        switch (p.Name) {
                            case "jit": jit = p.Value.GetBoolean(); break;
                            case "osr": osr = p.Value.GetBoolean(); break;
                            case "prelude": prelude = p.Value.GetBoolean(); break;
                            case "streaming": OutputStream.Streaming = p.Value.GetBoolean(); break;
                            case "legacyParser": legacyParser = p.Value.GetBoolean(); break;
                            case "firstChance": TraceFirstChance(p.Value.GetInt32()); break;
                            case "languageOptions":
                                // raw DLR / IronRuby language options, e.g. {"CompilationThreshold": 2}
                                foreach (var o in p.Value.EnumerateObject()) {
                                    languageOptions[o.Name] = o.Value.ValueKind switch {
                                        JsonValueKind.True => true,
                                        JsonValueKind.False => false,
                                        JsonValueKind.Number => o.Value.GetInt32(),
                                        _ => o.Value.GetString(),
                                    };
                                }
                                break;
                            case "loadPath":
                                var list = new List<string>();
                                foreach (var e in p.Value.EnumerateArray()) list.Add(e.GetString());
                                extraPaths = list.ToArray();
                                break;
                        }
                    }
                }

                var sw = Stopwatch.StartNew();
                if (stdlibZip != null && stdlibZip.Length > 0) {
                    using var zip = new ZipArchive(new MemoryStream(stdlibZip), ZipArchiveMode.Read);
                    zip.ExtractToDirectory(Root, true);
                }
                Directory.CreateDirectory(AssemblyDir);
                timings.Add(new("unzip", sw.Elapsed.TotalMilliseconds));

                sw.Restart();
                if (!_resolverInstalled) {
                    // assemblies passed to LoadAssembly satisfy the references of later ones
                    AssemblyLoadContext.Default.Resolving += (ctx, name) =>
                        name.Name != null && _loaded.TryGetValue(name.Name, out var a) ? a : null;
                    _resolverInstalled = true;
                }

                // prism is the only parser since the legacy one was removed; the option is accepted and ignored
                RubyContext.AlternativeParser = IronRuby.Prism.PrismAstBridge.Parse;

                var setup = new ScriptRuntimeSetup();
                var ruby = Ruby.CreateRubySetup();
                var searchPaths = new List<string>(extraPaths) {
                    AssemblyDir,
                    Root + "/lib",
                    Root + "/stdlib/bundled-gems",
                };
                ruby.Options["SearchPaths"] = searchPaths;
                // adds stdlib/ruby/site_ruby/1.9.1, stdlib/ironruby, stdlib/ruby/4.0, stdlib/ruby/1.9.1
                ruby.Options["StandardLibrary"] = Root + "/stdlib";
                if (prelude) {
                    ruby.Options["RequiredPaths"] = new List<string> { "ruby4.rb" };
                }
                ruby.Options["JIT"] = jit;
                ruby.Options["OSR"] = osr;
                foreach (var o in languageOptions) ruby.Options[o.Key] = o.Value;
                setup.LanguageSetups.Add(ruby);
                setup.Options["InterpretedMode"] = false;

                stdout = new OutputStream(1);
                stderr = new OutputStream(2);
                var runtime = Ruby.CreateRuntime(setup);
                runtime.IO.SetOutput(stdout, new UTF8Encoding(false));
                runtime.IO.SetErrorOutput(stderr, new UTF8Encoding(false));
                runtime.IO.SetInput(new MemoryStream(), new UTF8Encoding(false));
                var engine = Ruby.GetEngine(runtime);
                timings.Add(new("runtime", sw.Elapsed.TotalMilliseconds));

                sw.Restart();
                string bootRb;
                using (var s = typeof(Interop).Assembly.GetManifestResourceStream("boot.rb"))
                using (var r = new StreamReader(s)) {
                    bootRb = r.ReadToEnd();
                }
                string bootPath = Root + "/boot.rb";
                File.WriteAllText(bootPath, bootRb);
                // what boot.rb's reset loads for a fresh top-level binding (IronRubyWasm::SESSION_FILE)
                File.WriteAllText(Root + "/session.rb", "IronRubyWasm.session = binding\n");
                engine.CreateScriptSourceFromFile(bootPath, Encoding.UTF8, SourceCodeKind.File).ExecuteProgram();
                object kernel = engine.Execute("IronRubyWasm");
                string version = engine.Execute("RUBY_DESCRIPTION").ToString();
                timings.Add(new("prelude", sw.Elapsed.TotalMilliseconds));

                _runtime = runtime;
                _engine = engine;
                _kernel = kernel;
                _stdout = stdout;
                _stderr = stderr;

                return Json(w => {
                    w.WriteBoolean("ok", true);
                    w.WriteString("version", version);
                    w.WriteNumber("ms", total.Elapsed.TotalMilliseconds);
                    w.WriteStartObject("timings");
                    foreach (var t in timings) w.WriteNumber(t.Key, Math.Round(t.Value, 1));
                    w.WriteEndObject();
                    w.WriteString("stdout", _stdout.Take());
                    w.WriteString("stderr", _stderr.Take());
                });
            } catch (Exception e) {
                return Json(w => {
                    w.WriteBoolean("ok", false);
                    w.WriteString("error", e.ToString());
                    w.WriteString("stdout", stdout?.Take() ?? "");
                    w.WriteString("stderr", stderr?.Take() ?? "");
                });
            }
        }

        /// <summary>
        /// Runs Ruby code in the persistent top-level binding. Returns
        /// {ok, result, stdout, stderr, ms} or {ok:false, error:{class, message, backtrace}, ...}.
        /// </summary>
        [JSExport]
        public static string Run(string code, string fileName) {
            if (_engine == null) {
                return Json(w => { w.WriteBoolean("ok", false); WriteError(w, "RuntimeError", "IronRuby is not booted", null); });
            }
            var sw = Stopwatch.StartNew();
            object result;
            try {
                result = _engine.Operations.InvokeMember(_kernel, "run",
                    MutableString.Create(code ?? "", RubyEncoding.UTF8),
                    string.IsNullOrEmpty(fileName) ? null : MutableString.Create(fileName, RubyEncoding.UTF8));
            } catch (Exception e) {
                // only when the kernel itself fails (it rescues Exception)
                return Json(w => {
                    w.WriteBoolean("ok", false);
                    WriteError(w, e.GetType().FullName, e.Message, new[] { e.StackTrace ?? "" });
                    WriteOutput(w, sw);
                });
            }
            var array = (RubyArray)result;
            string kind = array[0].ToString();
            return Json(w => {
                switch (kind) {
                    case "ok":
                        w.WriteBoolean("ok", true);
                        w.WriteString("result", array[1].ToString());
                        break;
                    case "exit":
                        w.WriteBoolean("ok", true);
                        w.WriteNull("result");
                        w.WriteNumber("exit", Convert.ToInt32(array[1]));
                        break;
                    default:
                        w.WriteBoolean("ok", false);
                        var bt = new List<string>();
                        if (array[3] is RubyArray lines) foreach (var l in lines) bt.Add(l?.ToString());
                        WriteError(w, array[1].ToString(), array[2].ToString(), bt);
                        break;
                }
                WriteOutput(w, sw);
            });
        }

        /// <summary>Forgets the local variables of earlier runs (methods, classes and constants stay).</summary>
        [JSExport]
        public static void Reset() {
            if (_engine != null) _engine.Operations.InvokeMember(_kernel, "reset");
        }

        /// <summary>
        /// Loads a .NET assembly (e.g. a NuGet package's lib/net8.0/*.dll) at run time. It goes into
        /// the default load context, so `load_assembly 'Name'` / `require 'Name'` find it by name and
        /// assemblies that reference it resolve; its namespaces become Ruby constants
        /// (Markdig::Markdown, Humanizer, ...). The file is also written to /ironruby/assemblies,
        /// which is on $LOAD_PATH, so `require 'Name.dll'` works too.
        /// </summary>
        [JSExport]
        public static string LoadAssembly(byte[] bytes, string fileName) {
            try {
                if (string.IsNullOrEmpty(fileName)) fileName = "assembly" + _loaded.Count + ".dll";
                fileName = Path.GetFileName(fileName);
                if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) fileName += ".dll";
                Directory.CreateDirectory(AssemblyDir);
                File.WriteAllBytes(Path.Combine(AssemblyDir, fileName), bytes);

                Assembly asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(bytes));
                _loaded[asm.GetName().Name] = asm;
                _runtime?.LoadAssembly(asm);
                var namespaces = new SortedSet<string>(StringComparer.Ordinal);
                int types = 0;
                try {
                    foreach (var t in asm.GetExportedTypes()) {
                        types++;
                        if (t.Namespace != null) namespaces.Add(t.Namespace);
                    }
                } catch (Exception) { }
                return Json(w => {
                    w.WriteBoolean("ok", true);
                    w.WriteString("name", asm.GetName().Name);
                    w.WriteString("fullName", asm.FullName);
                    w.WriteNumber("types", types);
                    w.WriteStartArray("namespaces");
                    foreach (var n in namespaces) w.WriteStringValue(n);
                    w.WriteEndArray();
                });
            } catch (Exception e) {
                return Json(w => { w.WriteBoolean("ok", false); w.WriteString("error", e.ToString()); });
            }
        }

        /// <summary>Writes a file into the in-memory file system (e.g. a Ruby library under /ironruby/lib).</summary>
        [JSExport]
        public static void WriteFile(string path, byte[] bytes) {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
        }

        // Diagnostic: prints the first n distinct exceptions thrown anywhere, caught or not.
        private static void TraceFirstChance(int n) {
            var seen = new HashSet<string>();
            AppDomain.CurrentDomain.FirstChanceException += (sender, e) => {
                string key = e.Exception.GetType().FullName + ": " + e.Exception.Message;
                if (seen.Count < n && seen.Add(key)) {
                    Console.Error.WriteLine("[first chance] " + key + "\n" + new StackTrace(1, false));
                }
            };
        }

        private static void WriteError(Utf8JsonWriter w, string cls, string message, IEnumerable<string> backtrace) {
            w.WriteStartObject("error");
            w.WriteString("class", cls);
            w.WriteString("message", message);
            w.WriteStartArray("backtrace");
            if (backtrace != null) foreach (var l in backtrace) w.WriteStringValue(l);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        private static void WriteOutput(Utf8JsonWriter w, Stopwatch sw) {
            w.WriteString("stdout", _stdout.Take());
            w.WriteString("stderr", _stderr.Take());
            w.WriteNumber("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1));
        }

        private static string Json(Action<Utf8JsonWriter> body) {
            var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}

// What an ahead-of-time compiled Ruby assembly calls at load time and startup. Everything the
// saved IL references is public here or public in IronRuby: a saved assembly has no
// restrictedSkipVisibility the way a DynamicMethod does.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;
using MSA = System.Linq.Expressions;

namespace IronRuby.Aot.Runtime {
    /// <summary>Marks the static class an ahead-of-time compiled program lives in.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class AotProgramAttribute : Attribute {
        public AotProgramAttribute(string mainFile, string[] files, string[] entryPoints, int initChunks, string[] searchPaths) {
            MainFile = mainFile;
            Files = files;
            EntryPoints = entryPoints;
            InitChunks = initChunks;
            SearchPaths = searchPaths;
        }
        /// <summary>The load path the compiled files were resolved against (the standard library's directories).</summary>
        public string[] SearchPaths { get; }
        public string MainFile { get; }
        /// <summary>Full paths of the files compiled in.</summary>
        public string[] Files { get; }
        /// <summary>The static method each file's top level was compiled to.</summary>
        public string[] EntryPoints { get; }
        public int InitChunks { get; }
    }

    public static class AotRuntime {
        private static RubyContext _context;

        /// <summary>The runtime the compiled code runs in (one per process, as the program has one).</summary>
        public static RubyContext Context => _context;

        // The constants the saved code re-creates on first use come from IronRuby.Runtime.Precompiled.PrecompiledCode,
        // which is bound to this runtime below.

        /// <summary>Number of times any Ruby parser ran in this process (IR_AOT_TRACE / IR_AOT_FORBID_PARSE).</summary>
        public static int ParseCount;

        #region Startup

        /// <summary>
        /// The entry point of a compiled program: sets up a runtime the way `ir` does (without the
        /// parser), registers the compiled files with the loader, and runs the main one.
        /// </summary>
        [RubyStackTraceHidden]
        public static int Main(Type program, string[] args) {
            var startTicks = Stopwatch.GetTimestamp();
            var info = program.GetCustomAttribute<AotProgramAttribute>();
            string trace = Environment.GetEnvironmentVariable("IR_AOT_TRACE");

            // Tripwire: every parse of Ruby source goes through RubyContext.ParseSourceCode, which
            // uses AlternativeParser when it is set. A compiled program does not ship a parser
            // front end at all, so any parse is counted, and refused under IR_AOT_FORBID_PARSE.
            bool forbid = Environment.GetEnvironmentVariable("IR_AOT_FORBID_PARSE") == "1";
            RubyContext.AlternativeParser = (unit, options, sink) => {
                Interlocked.Increment(ref ParseCount);
                if (trace != null) Console.Error.WriteLine("[aot] PARSE " + (unit.Path ?? "<string>"));
                if (forbid) throw new InvalidOperationException("IR_AOT_FORBID_PARSE: the compiled program tried to parse Ruby source: " + (unit.Path ?? "<string>"));
                var parser = Type.GetType("IronRuby.Prism.PrismAstBridge, IronRuby.Prism");
                if (parser != null) {
                    return (SourceUnitTree)parser.GetMethod("Parse").Invoke(null, new object[] { unit, options, sink });
                }
                // the prism front end is the only parser there is
                throw new InvalidOperationException("IronRuby.Prism is not loaded: cannot parse " + (unit.Path ?? "<string>"));
            };

            var setup = new ScriptRuntimeSetup();
            var ruby = IronRuby.Ruby.CreateRubySetup();
            string baseDir = Path.GetDirectoryName(program.Assembly.Location);
            string stdlib = Environment.GetEnvironmentVariable("IR_AOT_STDLIB");
            var searchPaths = new List<string>();
            if (stdlib != null) searchPaths.AddRange(stdlib.Split(Path.PathSeparator));
            ruby.Options["SearchPaths"] = searchPaths;
            ruby.Options["MainFile"] = info.MainFile;
            ruby.Options["Arguments"] = args;
            ruby.Options["JIT"] = false;
            ruby.Options["OSR"] = false;
            // the core prelude is required only when it was compiled in (or a stdlib is given to parse it from)
            bool hasPrelude = info.Files.Any(f => Path.GetFileName(f) == "ruby4.rb");
            if (stdlib == null && info.SearchPaths != null) {
                // `require` still resolves a file on the load path before it looks for compiled code
                searchPaths.AddRange(info.SearchPaths);
            }
            if (hasPrelude || stdlib != null) {
                ruby.Options["RequiredPaths"] = new[] { "ruby4.rb" };
            }
            setup.LanguageSetups.Add(ruby);
            var runtime = IronRuby.Ruby.CreateRuntime(setup);
            var engine = IronRuby.Ruby.GetEngine(runtime);
            _context = (RubyContext)HostingHelpers.GetLanguageContext(engine);
            IronRuby.Runtime.Precompiled.PrecompiledCode.Context = _context;

            for (int i = 0; i < info.InitChunks; i++) {
                program.GetMethod("__init" + i, BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            }

            RubyScriptCode main = null;
            for (int i = 0; i < info.Files.Length; i++) {
                var target = (Func<RubyScope, object, object>)program.GetMethod(info.EntryPoints[i], BindingFlags.Public | BindingFlags.Static)
                    .CreateDelegate(typeof(Func<RubyScope, object, object>));
                string path = info.Files[i];
                if (trace != null) {
                    var inner = target;
                    target = (scope, self) => {
                        long t0 = Stopwatch.GetTimestamp();
                        long jit0 = System.Runtime.JitInfo.GetCompiledMethodCount();
                        try {
                            return inner(scope, self);
                        } finally {
                            Console.Error.WriteLine($"[aot] {Path.GetFileName(path)} top level ran in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms " +
                                $"(incl. nested requires; {System.Runtime.JitInfo.GetCompiledMethodCount() - jit0} methods JIT-compiled meanwhile)");
                        }
                    };
                }
                if (path == info.MainFile) {
                    main = new RubyScriptCode(target, _context.CreateFileUnit(path), TopScopeFactoryKind.Main);
                } else {
                    // a precompiled library file: `require` still resolves it on the load path,
                    // then runs this code instead of compiling what it found there
                    _context.Loader.RegisterPrecompiledFile(path, new RubyScriptCode(target, _context.CreateFileUnit(path), TopScopeFactoryKind.File), null);
                }
            }
            if (trace != null) {
                Console.Error.WriteLine($"[aot] runtime ready in {Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds:F0} ms");
            }

            int status = 0;
            try {
                _context.RegisterSourceFileLocation(info.MainFile);
                main.Run();
            } catch (SystemExit e) {
                status = e.Status;
            } catch (Exception e) {
                _context.CurrentException = e;
                try { _context.RunShutdownHandlers(); } catch (Exception) { }
                Console.Error.Write(engine.GetService<ExceptionOperations>().FormatException(e));
                status = 1;
            }
            try {
                runtime.Shutdown();
            } catch (SystemExit e) {
                status = e.Status;
            }
            if (trace != null) {
                Console.Error.WriteLine($"[aot] exit {status}; parser calls: {ParseCount}; total {Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds:F0} ms; " +
                    $"methods JIT-compiled: {System.Runtime.JitInfo.GetCompiledMethodCount()} ({System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds:F0} ms)");
            }
            return status;
        }

        #endregion
    }
}

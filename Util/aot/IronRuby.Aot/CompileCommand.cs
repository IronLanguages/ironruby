using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IronRuby.Aot {
    /// <summary>
    ///   IronRuby.Aot compile [-o outdir] [--prelude] [--lib file.rb ...] main.rb
    ///
    /// Writes outdir/&lt;main&gt;.dll (+ runtimeconfig.json and the IronRuby runtime assemblies),
    /// runnable with a stock host: `dotnet outdir/&lt;main&gt;.dll args...`.
    /// --prelude compiles IronRuby's core prelude (Src/StdLib/ironruby/ruby4.rb) in as well, so
    /// that it is not parsed at startup either; --lib adds other files `require` will find.
    /// </summary>
    internal static class CompileCommand {
        public static int Run(string[] args) {
            string outDir = "aot-out";
            var libs = new List<string>();
            string main = null;
            bool prelude = false, eager = false, r2r = false;
            for (int i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "-o": outDir = args[++i]; break;
                    case "--prelude": prelude = true; break;
                    case "--eager-init": eager = true; break;
                    case "--r2r": r2r = true; break;
                    case "--lib": libs.Add(args[++i]); break;
                    default: main = args[i]; break;
                }
            }
            if (main == null) {
                Console.Error.WriteLine("usage: IronRuby.Aot compile [-o outdir] [--prelude] [--lib file.rb]... main.rb");
                return 2;
            }

            string toolDir = AppContext.BaseDirectory;
            string root = FindRepoRoot(toolDir);
            string stdlib = Path.Combine(root, "Src", "StdLib");
            // the load path ir.sh gives (-X:StdLib=ironruby:ruby/4.0)
            string[] searchPaths = { Path.Combine(stdlib, "ironruby"), Path.Combine(stdlib, "ruby", "4.0") };
            if (prelude) {
                // ruby4.rb and what it requires at startup (IR_AOT_TRACE=1 lists any file still parsed)
                int at = 0;
                foreach (var lib in PreludeFiles) {
                    string found = searchPaths.Select(d => Path.Combine(d, lib)).FirstOrDefault(File.Exists);
                    if (found == null) throw new FileNotFoundException("prelude file " + lib);
                    libs.Insert(at++, found);
                }
            }

            Directory.CreateDirectory(outDir);
            string name = Path.GetFileNameWithoutExtension(main);
            string dll = Path.Combine(outDir, name + ".dll");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var context = AotHost.CreateContext(out _);
            var compiler = new AotCompiler(context, name) { OutputDirectory = Path.GetFullPath(outDir), SearchPaths = searchPaths, LazyInit = !eager };
            var files = new List<string>(libs) { main };
            compiler.Compile(main, files, dll);
            Console.Error.WriteLine($"wrote {dll} ({new FileInfo(dll).Length:N0} bytes) in {sw.ElapsedMilliseconds} ms");
            foreach (var kv in compiler.Stats.OrderBy(kv => kv.Key)) {
                Console.Error.WriteLine($"  {kv.Value,7}  {kv.Key}");
            }

            // A stock `dotnet` host needs a runtimeconfig; the assemblies it probes for sit next to it.
            // The parser front end (IronRuby.Prism, libprism) is deliberately not copied.
            File.WriteAllText(Path.Combine(outDir, name + ".runtimeconfig.json"),
                "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"10.0.0\" },\n" +
                "    \"configProperties\": {\n      \"System.Text.Encoding.EnableUnsafeUTF7Encoding\": true,\n      \"System.IO.DisableFileLocking\": true\n    }\n  }\n}\n");
            foreach (var file in Directory.GetFiles(toolDir, "*.dll")) {
                string fn = Path.GetFileName(file);
                if (fn.StartsWith("IronRuby.Prism", StringComparison.Ordinal) || fn.StartsWith("libprism", StringComparison.Ordinal)) continue;
                File.Copy(file, Path.Combine(outDir, fn), true);
            }
            if (Directory.Exists(Path.Combine(toolDir, "runtimes"))) {
                CopyDir(Path.Combine(toolDir, "runtimes"), Path.Combine(outDir, "runtimes"));
            }
            if (r2r) {
                // ReadyToRun: crossgen2 compiles the program's IL (the Ruby) and IronRuby's own
                // assemblies to native code ahead of time, so startup does not wait for the JIT.
                var assemblies = new List<string> { dll };
                assemblies.AddRange(compiler.ExtraAssemblies);
                assemblies.AddRange(new[] { "IronRuby.dll", "IronRuby.Libraries.dll", "Microsoft.Scripting.dll", "Microsoft.Dynamic.dll", "IronRuby.Aot.dll" }
                    .Select(f => Path.Combine(outDir, f)));
                foreach (var asm in assemblies) {
                    if (!Crossgen(asm, outDir)) return 1;
                }
            }
            return 0;
        }

        private static readonly string[] PreludeFiles = {
            "ruby4.rb", "ironruby/rational.rb", "ironruby/complex.rb", "thread.rb", "set.rb", "argf.rb", "ironruby/gem_compat.rb",
        };

        private static bool Crossgen(string assembly, string outDir) {
            string home = Environment.GetEnvironmentVariable("HOME");
            string crossgen = Directory.GetFiles(Path.Combine(home, ".nuget", "packages", "microsoft.netcore.app.crossgen2.linux-x64"), "crossgen2", SearchOption.AllDirectories)
                .Where(p => p.Contains("/10.")).OrderBy(p => p).LastOrDefault();
            if (crossgen == null) {
                Console.Error.WriteLine("crossgen2 (microsoft.netcore.app.crossgen2.linux-x64 10.x) is not in the NuGet cache");
                return false;
            }
            string framework = Path.GetDirectoryName(typeof(object).Assembly.Location);
            string tmp = assembly + ".r2r";
            var psi = new System.Diagnostics.ProcessStartInfo(crossgen) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { assembly, "-o", tmp, "-r", framework + "/*.dll", "-r", Path.GetFullPath(outDir) + "/*.dll",
                "--targetos", "linux", "--targetarch", "x64", "-O" }) psi.ArgumentList.Add(a);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var p = System.Diagnostics.Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) {
                Console.Error.WriteLine("crossgen2 failed on " + assembly + ":\n" + output);
                return false;
            }
            long before = new FileInfo(assembly).Length;
            File.Move(tmp, assembly, true);
            Console.Error.WriteLine($"r2r {Path.GetFileName(assembly)}: {before:N0} -> {new FileInfo(assembly).Length:N0} bytes in {sw.ElapsedMilliseconds} ms");
            return true;
        }

        private static string FindRepoRoot(string dir) {
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent) {
                if (File.Exists(Path.Combine(d.FullName, "Src", "StdLib", "ironruby", "ruby4.rb"))) return d.FullName;
            }
            throw new InvalidOperationException("cannot find the IronRuby tree above " + dir);
        }

        private static void CopyDir(string from, string to) {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}

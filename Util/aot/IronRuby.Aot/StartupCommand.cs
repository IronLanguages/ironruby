using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IronRuby.Aot {
    /// <summary>
    ///   IronRuby.Aot startup -o dir [--tfm net8.0|net10.0] [--r2r] [--host dir] (file.rb | @list)...
    ///
    /// Compiles the Ruby code `ir` runs at startup - the core prelude (ruby4.rb), rbconfig, rubygems
    /// and the rest - into dir/IronRuby.Startup.dll, a library IronRuby's loader picks up when it
    /// sits next to IronRuby.dll (or is named by IR_STARTUP_DLL): requiring one of its files then
    /// runs the compiled top level instead of parsing, transforming and LightCompiling the source.
    ///
    /// --tfm net8.0 retargets the saved assembly's framework references (PersistedAssemblyBuilder
    /// writes the running .NET 10's) to .NET 8, so the net8.0 host can load it. --r2r runs crossgen2
    /// over it against the host's assemblies (--host, default Src/Console/bin/R2R/&lt;tfm&gt;), which is
    /// where it is meant to go: without native code its ~16k methods are JIT-compiled at startup.
    /// </summary>
    internal static class StartupCommand {
        public static int Run(string[] args) {
            string outDir = null, tfm = "net10.0", host = null;
            bool r2r = false;
            var files = new List<string>();
            for (int i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "-o": outDir = args[++i]; break;
                    case "--tfm": tfm = args[++i]; break;
                    case "--r2r": r2r = true; break;
                    case "--host": host = args[++i]; break;
                    default:
                        if (args[i].StartsWith("@", StringComparison.Ordinal)) {
                            string list = args[i].Substring(1), baseDir = Path.GetDirectoryName(Path.GetFullPath(list));
                            foreach (var line in File.ReadAllLines(list)) {
                                string f = line.Trim();
                                if (f.Length == 0 || f.StartsWith("#", StringComparison.Ordinal)) continue;
                                files.Add(Path.GetFullPath(Path.Combine(baseDir, f)));
                            }
                        } else {
                            files.Add(Path.GetFullPath(args[i]));
                        }
                        break;
                }
            }
            if (outDir == null || files.Count == 0) {
                Console.Error.WriteLine("usage: IronRuby.Aot startup -o dir [--tfm net8.0|net10.0] [--r2r] [--host dir] (file.rb | @list)...");
                return 2;
            }

            Directory.CreateDirectory(outDir);
            string dll = Path.Combine(outDir, "IronRuby.Startup.dll");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var context = AotHost.CreateContext(out _);
            var compiler = new AotCompiler(context, "IronRuby.Startup", library: true) { OutputDirectory = Path.GetFullPath(outDir) };
            compiler.Compile(null, files, dll);
            Console.Error.WriteLine($"wrote {dll} ({new FileInfo(dll).Length:N0} bytes, {files.Count} files) in {sw.ElapsedMilliseconds} ms");
            foreach (var kv in compiler.Stats.OrderBy(kv => kv.Key)) {
                Console.Error.WriteLine($"  {kv.Value,7}  {kv.Key}");
            }

            var outputs = new List<string> { dll };
            outputs.AddRange(compiler.ExtraAssemblies);
            int major = tfm == "net8.0" ? 8 : tfm == "net10.0" ? 10 : throw new ArgumentException("--tfm net8.0 or net10.0");
            if (major != Environment.Version.Major) {
                foreach (var asm in outputs) {
                    Console.Error.WriteLine($"retargeted {Path.GetFileName(asm)}: {Retarget(asm, Environment.Version.Major, major)} framework references -> {major}.0");
                }
            }
            if (r2r) {
                string root = FindRepoRoot(AppContext.BaseDirectory);
                host ??= Path.Combine(root, "Src", "Console", "bin", "R2R", tfm);
                string framework = Directory.GetDirectories("/usr/local/dotnet/shared/Microsoft.NETCore.App")
                    .Where(d => Path.GetFileName(d).StartsWith(major + ".", StringComparison.Ordinal)).OrderBy(d => d).Last();
                foreach (var asm in outputs) {
                    if (!Crossgen(asm, major, framework, host, outDir)) return 1;
                }
            }
            return 0;
        }

        private static readonly HashSet<string> FrameworkPrefixes = new() { "System", "Microsoft.Win32", "Microsoft.CSharp", "Microsoft.VisualBasic", "netstandard", "mscorlib" };

        /// <summary>
        /// Rewrites the version of every framework assembly reference from <paramref name="from"/>.x to
        /// <paramref name="to"/>.0.0.0 in place: the four version fields lead each AssemblyRef row.
        /// </summary>
        private static int Retarget(string path, int from, int to) {
            byte[] image = File.ReadAllBytes(path);
            int patched = 0;
            using (var pe = new PEReader(new MemoryStream(image))) {
                var md = pe.GetMetadataReader();
                int tableOffset = pe.PEHeaders.MetadataStartOffset + md.GetTableMetadataOffset(TableIndex.AssemblyRef);
                int rowSize = md.GetTableRowSize(TableIndex.AssemblyRef);
                int row = 0;
                foreach (var handle in md.AssemblyReferences) {
                    var r = md.GetAssemblyReference(handle);
                    string name = md.GetString(r.Name);
                    int at = tableOffset + row++ * rowSize;
                    if (r.Version.Major != from || !FrameworkPrefixes.Any(p => name == p || name.StartsWith(p + ".", StringComparison.Ordinal))) continue;
                    BitConverter.TryWriteBytes(new Span<byte>(image, at, 2), (ushort)to);
                    BitConverter.TryWriteBytes(new Span<byte>(image, at + 2, 2), (ushort)0);
                    BitConverter.TryWriteBytes(new Span<byte>(image, at + 4, 2), (ushort)0);
                    BitConverter.TryWriteBytes(new Span<byte>(image, at + 6, 2), (ushort)0);
                    patched++;
                }
            }
            File.WriteAllBytes(path, image);
            return patched;
        }

        private static bool Crossgen(string assembly, int major, string framework, string host, string outDir) {
            string home = Environment.GetEnvironmentVariable("HOME");
            string crossgen = Directory.GetFiles(Path.Combine(home, ".nuget", "packages", "microsoft.netcore.app.crossgen2.linux-x64"), "crossgen2", SearchOption.AllDirectories)
                .Where(p => p.Contains("/" + major + ".")).OrderBy(p => p).LastOrDefault();
            if (crossgen == null) {
                Console.Error.WriteLine($"crossgen2 (microsoft.netcore.app.crossgen2.linux-x64 {major}.x) is not in the NuGet cache");
                return false;
            }
            string tmp = assembly + ".r2r";
            var psi = new System.Diagnostics.ProcessStartInfo(crossgen) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { assembly, "-o", tmp, "-r", framework + "/*.dll", "-r", Path.GetFullPath(host) + "/*.dll",
                "-r", Path.GetFullPath(outDir) + "/*.dll", "--targetos", "linux", "--targetarch", "x64", "-O" }) psi.ArgumentList.Add(a);
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
            Console.Error.WriteLine($"r2r {Path.GetFileName(assembly)}: {before:N0} -> {new FileInfo(assembly).Length:N0} bytes in {sw.ElapsedMilliseconds} ms ({Path.GetFileName(Path.GetDirectoryName(crossgen + "/.."))})");
            return true;
        }

        internal static string FindRepoRoot(string dir) {
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent) {
                if (File.Exists(Path.Combine(d.FullName, "Src", "StdLib", "ironruby", "ruby4.rb"))) return d.FullName;
            }
            throw new InvalidOperationException("cannot find the IronRuby tree above " + dir);
        }
    }
}

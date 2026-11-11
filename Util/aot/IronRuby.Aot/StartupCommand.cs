using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using IronRuby.Runtime.Precompiled;

namespace IronRuby.Aot {
    /// <summary>
    ///   IronRuby.Aot startup -o dir [--tfm net10.0|net11.0] [--r2r] [--host dir] [--rid rid] [--root dir]
    ///                        [--crossgen2 path] [--cache dir] (file.rb | @list)...
    ///
    /// Compiles the Ruby code `ir` runs at startup - the core prelude (ruby4.rb), rbconfig, rubygems
    /// and the rest - into dir/IronRuby.Startup.dll, a library IronRuby's loader picks up when it
    /// sits next to IronRuby.dll (or is named by IR_STARTUP_DLL): requiring one of its files then
    /// runs the compiled top level instead of parsing, transforming and LightCompiling the source.
    ///
    /// --host names the directory of the ir that is to load the library (default Src/Console/bin/R2R/&lt;tfm&gt;):
    /// the library records the identity of its IronRuby and DLR assemblies and is only used with exactly those.
    /// --tfm is the host's framework; the compiler targets the same one, and when it runs on a later runtime (RollForward)
    /// the saved assembly's framework references (PersistedAssemblyBuilder writes the running runtime's) are retargeted to
    /// the host's major version, so the host can load it. --r2r runs crossgen2 over it against the host's
    /// assemblies (and, for a framework-dependent host, the installed shared framework of that major version): without
    /// native code its ~16k methods are JIT-compiled at startup. --rid is the host's runtime identifier (default this
    /// machine's); crossgen2 comes from the NuGet cache (microsoft.netcore.app.crossgen2.&lt;rid&gt;) unless --crossgen2
    /// names it.
    ///
    /// --root is the IronRuby tree the host belongs to (default the one this compiler was built in): each listed file
    /// is taken from the same place under it. The library records where that tree is relative to the host, so that a
    /// copy of the whole tree elsewhere (an unpacked release) uses it too: paths compiled into the code are moved
    /// to wherever the tree is at run time.
    ///
    /// --cache keeps the last library made in a directory, with a key of everything it was made from - the compiler,
    /// the host's assemblies, the files' contents, the options - and copies it out instead of compiling when the key
    /// is the same: an unchanged rebuild costs a second, not twenty.
    /// </summary>
    internal static class StartupCommand {
        public static int Run(string[] args) {
            string outDir = null, tfm = "net10.0", host = null, rid = null, root = null, crossgen = null, cache = null;
            bool r2r = false;
            var listed = new List<string>();
            for (int i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "-o": outDir = args[++i]; break;
                    case "--tfm": tfm = args[++i]; break;
                    case "--r2r": r2r = true; break;
                    case "--host": host = args[++i]; break;
                    case "--rid": rid = args[++i]; break;
                    case "--root": root = args[++i]; break;
                    case "--crossgen2": crossgen = args[++i]; break;
                    case "--cache": cache = args[++i]; break;
                    default:
                        if (args[i].StartsWith("@", StringComparison.Ordinal)) {
                            string list = args[i].Substring(1), baseDir = Path.GetDirectoryName(Path.GetFullPath(list));
                            foreach (var line in File.ReadAllLines(list)) {
                                string f = line.Trim();
                                if (f.Length == 0 || f.StartsWith("#", StringComparison.Ordinal)) continue;
                                listed.Add(Path.GetFullPath(Path.Combine(baseDir, f)));
                            }
                        } else {
                            listed.Add(Path.GetFullPath(args[i]));
                        }
                        break;
                }
            }
            if (outDir == null || listed.Count == 0) {
                Console.Error.WriteLine("usage: IronRuby.Aot startup -o dir [--tfm net10.0|net11.0] [--r2r] [--host dir] [--rid rid] [--root dir] " +
                    "[--crossgen2 path] [--cache dir] (file.rb | @list)...");
                return 2;
            }
            var tfmMatch = System.Text.RegularExpressions.Regex.Match(tfm, @"\Anet(\d+)\.0\z");
            int major = tfmMatch.Success ? int.Parse(tfmMatch.Groups[1].Value) : 0;
            if (major < 10 || major > Environment.Version.Major) {
                throw new ArgumentException($"--tfm net10.0 or later, up to this runtime's (net{Environment.Version.Major}.0): not {tfm}");
            }
            // the portable RID the host is published for (Ruby.Console.csproj: -r linux-x64), not RuntimeIdentifier,
            // which a distribution's own .NET build reports as its distro RID (ubuntu.24.04-x64) - whose crossgen2
            // package nothing restores
            rid ??= (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
                RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

            // the files of the host's tree: the same relative paths under --root as under the tree they were listed in
            string sourceRoot = FindRepoRoot(listed[0]);
            root = Path.GetFullPath(root ?? sourceRoot).TrimEnd('/', '\\');
            var files = listed.Select(f => Path.GetFullPath(Path.Combine(root, Path.GetRelativePath(sourceRoot, f)))).ToList();
            foreach (var f in files) {
                if (!File.Exists(f)) {
                    Console.Error.WriteLine($"{f} not found");
                    return 2;
                }
            }

            Directory.CreateDirectory(outDir);
            string dll = Path.Combine(outDir, "IronRuby.Startup.dll");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            host = Path.GetFullPath(host ?? Path.Combine(FindRepoRoot(AppContext.BaseDirectory), "Src", "Console", "bin", "R2R", tfm));
            // the host's own builds of the assemblies the saved IL calls: the library is only used with exactly these
            var ids = new List<string>();
            foreach (var name in DependencyNames) {
                string file = Path.Combine(host, name + ".dll");
                if (!File.Exists(file)) {
                    Console.Error.WriteLine($"{file} not found: --host names the directory of the ir that is to load the library");
                    return 2;
                }
                ids.Add(name + "=" + Mvid(file));
            }
            // a self-contained host carries its framework; a framework-dependent one runs on the latest installed patch of its major version
            bool selfContained = File.Exists(Path.Combine(host, "System.Private.CoreLib.dll"));
            string framework = null;
            if (r2r && !selfContained) {
                string shared = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('/', '\\'))), "Microsoft.NETCore.App");
                framework = Directory.Exists(shared) ? Directory.GetDirectories(shared)
                    .Where(d => Path.GetFileName(d).StartsWith(major + ".", StringComparison.Ordinal))
                    .OrderBy(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version(0, 0)).LastOrDefault() : null;
                if (framework == null) {
                    Console.Error.WriteLine($"no .NET {major} runtime in {shared} to compile the library's native code against");
                    return 1;
                }
            }
            if (r2r) {
                crossgen ??= FindCrossgen(rid, major);
                if (crossgen == null) {
                    Console.Error.WriteLine($"crossgen2 (microsoft.netcore.app.crossgen2.{rid} {major}.x) is not in the NuGet cache; --crossgen2 names it");
                    return 1;
                }
            }

            // everything the output depends on; what was compiled in is identified by content, so a tree that moves or is rebuilt
            // without changes hits the cache
            string key = null;
            if (cache != null) {
                var k = new StringBuilder();
                k.Append("v1\n").Append(tfm).Append(' ').Append(rid).Append(' ').Append(r2r).Append(' ').Append(selfContained).Append('\n');
                k.Append(Path.GetRelativePath(host, root)).Append('\n').Append(root).Append('\n');
                foreach (var id in ids) k.Append(id).Append('\n');
                foreach (var a in new[] { typeof(StartupCommand).Assembly, typeof(PrecompiledCode).Assembly, typeof(IronRuby.Builtins.KernelOps).Assembly,
                    typeof(IronRuby.Prism.PrismAstBridge).Assembly, typeof(Microsoft.Scripting.SourceUnit).Assembly, typeof(Microsoft.Scripting.Ast.Utils).Assembly }) {
                    k.Append(a.GetName().Name).Append('=').Append(a.ManifestModule.ModuleVersionId).Append('\n');
                }
                foreach (var native in Directory.GetFiles(AppContext.BaseDirectory, "*prism*").Where(p => !p.EndsWith(".dll") && !p.EndsWith(".pdb") && !p.EndsWith(".json"))) {
                    k.Append(Path.GetFileName(native)).Append('=').Append(PrecompiledCode.HashFile(native)).Append('\n');
                }
                if (r2r) k.Append(crossgen).Append('=').Append(new FileInfo(crossgen).Length).Append('\n').Append(framework ?? "self-contained").Append('\n');
                foreach (var f in files) k.Append(f).Append('=').Append(PrecompiledCode.HashFile(f)).Append('\n');
                key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(k.ToString())));
                string keyFile = Path.Combine(cache, "key");
                if (File.Exists(keyFile) && File.ReadAllText(keyFile).Trim() == key) {
                    foreach (var cached in Directory.GetFiles(cache, "IronRuby.Startup*.dll")) {
                        File.Copy(cached, Path.Combine(outDir, Path.GetFileName(cached)), true);
                    }
                    Console.Error.WriteLine($"cached {dll} ({new FileInfo(dll).Length:N0} bytes, {files.Count} files): nothing it is made from has changed");
                    return 0;
                }
            }

            var context = AotHost.CreateContext(out _);
            var compiler = new AotCompiler(context, "IronRuby.Startup", library: true) {
                OutputDirectory = Path.GetFullPath(outDir), DependencyIds = ids.ToArray(),
                Root = root, RootFromLibrary = Path.GetRelativePath(host, root),
                // arm64 ReadyToRun code of a method over 1 MB is wrong (AotCompiler.LargeMethodsJittedOver); arm64 code is
                // at most ~4.4x its IL, so a top level of 128 KB of IL or more is left to the JIT
                // (IR_AOT_LARGE_JIT=0 compiles them all, to see the check in Crossgen catch it)
                LargeMethodsJittedOver = r2r && rid.EndsWith("-arm64", StringComparison.Ordinal) &&
                    Environment.GetEnvironmentVariable("IR_AOT_LARGE_JIT") != "0" ? LargeMethodILBytes : 0
            };
            compiler.Compile(null, files, dll);
            Console.Error.WriteLine($"wrote {dll} ({new FileInfo(dll).Length:N0} bytes, {files.Count} files) in {sw.ElapsedMilliseconds} ms");
            foreach (var kv in compiler.Stats.OrderBy(kv => kv.Key)) {
                Console.Error.WriteLine($"  {kv.Value,7}  {kv.Key}");
            }

            var outputs = new List<string> { dll };
            outputs.AddRange(compiler.ExtraAssemblies);
            if (major != Environment.Version.Major) {
                foreach (var asm in outputs) {
                    Console.Error.WriteLine($"retargeted {Path.GetFileName(asm)}: {Retarget(asm, Environment.Version.Major, major)} framework references -> {major}.0");
                }
            }
            if (r2r) {
                foreach (var asm in outputs) {
                    if (!Crossgen(crossgen, asm, rid, framework, host, outDir)) return 1;
                }
            }

            if (cache != null) {
                Directory.CreateDirectory(cache);
                foreach (var old in Directory.GetFiles(cache)) File.Delete(old);
                foreach (var asm in outputs) File.Copy(asm, Path.Combine(cache, Path.GetFileName(asm)));
                File.WriteAllText(Path.Combine(cache, "key"), key + "\n");
            }
            return 0;
        }

        internal const int LargeMethodILBytes = 128 * 1024;
        // the reach of arm64's adr: a method whose code is longer addresses its read-only data with adrp+add
        private const long ArmMethodLimit = 1024 * 1024;

        // what the saved IL references: IronRuby.Libraries is not among them (library methods are reached through binders)
        private static readonly string[] DependencyNames = { "IronRuby", "Microsoft.Dynamic", "Microsoft.Scripting" };

        private static string Mvid(string file) {
            using var pe = new PEReader(File.OpenRead(file));
            var md = pe.GetMetadataReader();
            return md.GetGuid(md.GetModuleDefinition().Mvid).ToString();
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

        /// <summary>The newest crossgen2 of the major version in the NuGet cache - the one the SDK's ReadyToRun publish restores.</summary>
        private static string FindCrossgen(string rid, int major) {
            string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (string.IsNullOrEmpty(packages)) {
                packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            }
            string pack = Path.Combine(packages, "microsoft.netcore.app.crossgen2." + rid.ToLowerInvariant());
            if (!Directory.Exists(pack)) return null;
            return Directory.GetDirectories(pack)
                .Where(d => Path.GetFileName(d).StartsWith(major + ".", StringComparison.Ordinal))
                .OrderBy(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version(0, 0))
                .Select(d => new[] { "crossgen2", "crossgen2.exe" }.Select(n => Path.Combine(d, "tools", n)).FirstOrDefault(File.Exists))
                .LastOrDefault(p => p != null);
        }

        private static bool Crossgen(string crossgen, string assembly, string rid, string framework, string host, string outDir) {
            string os = rid.StartsWith("win", StringComparison.Ordinal) ? "windows" : rid.StartsWith("osx", StringComparison.Ordinal) ? "osx" : "linux";
            string arch = rid.Substring(rid.LastIndexOf('-') + 1);
            string tmp = assembly + ".r2r";
            var psi = new System.Diagnostics.ProcessStartInfo(crossgen) { RedirectStandardOutput = true, RedirectStandardError = true };
            var arguments = new List<string> { assembly, "-o", tmp };
            if (framework != null) arguments.AddRange(new[] { "-r", framework + "/*.dll" });
            arguments.AddRange(new[] { "-r", host + "/*.dll", "-r", Path.GetFullPath(outDir) + "/*.dll", "--targetos", os, "--targetarch", arch, "-O" });
            // arm64: the map says how large each method's code is, which must stay under 1 MB (AotCompiler.LargeMethodsJittedOver)
            bool arm64 = arch == "arm64";
            if (arm64) arguments.Add("--map");
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var p = System.Diagnostics.Process.Start(psi);
            var stderr = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd() + stderr.Result;
            p.WaitForExit();
            if (p.ExitCode != 0) {
                Console.Error.WriteLine("crossgen2 failed on " + assembly + ":\n" + output);
                return false;
            }
            if (arm64) {
                string map = Path.ChangeExtension(tmp, ".map");
                var tooLarge = File.ReadLines(map)
                    .Select(line => System.Text.RegularExpressions.Regex.Match(line, @"^0x[0-9A-F]+ \| 0x([0-9A-F]+) .*\| (\S+)\s+\(MethodWithGCInfo\)"))
                    .Where(m => m.Success && Convert.ToInt64(m.Groups[1].Value, 16) >= ArmMethodLimit)
                    .Select(m => m.Groups[2].Value + " (" + Convert.ToInt64(m.Groups[1].Value, 16).ToString("N0") + " bytes)").ToList();
                File.Delete(map);
                if (tooLarge.Count > 0) {
                    File.Delete(tmp);
                    Console.Error.WriteLine("crossgen2's arm64 code for " + string.Join(", ", tooLarge) + " is over 1 MB, which RyuJIT " +
                        "gets wrong in a ReadyToRun image (AotCompiler.LargeMethodsJittedOver): no library rather than a broken one");
                    return false;
                }
            }
            long before = new FileInfo(assembly).Length;
            File.Move(tmp, assembly, true);
            Console.Error.WriteLine($"r2r {Path.GetFileName(assembly)}: {before:N0} -> {new FileInfo(assembly).Length:N0} bytes in {sw.ElapsedMilliseconds} ms ({crossgen})");
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

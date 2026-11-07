/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Linq;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Runtime {
    [Flags]
    public enum LoadFlags {
        None = 0,
        LoadOnce = 1,
        LoadIsolated = 2,
        AppendExtensions = 4,

        /// <summary>
        /// Returns a scope or assembly of already loaded targets.
        /// </summary>
        ResolveLoaded = 8,
        AnyLanguage = 16,

        /// <summary>
        /// A #require the program wrote, as opposed to one the runtime makes on its own (autoload,
        /// the bootstrap of Complex and Rational): only that one reports a circular require.
        /// </summary>
        WarnCircular = 32,

        Require = LoadOnce | AppendExtensions,
    }

    // TODO: thread safety
    public sealed class Loader {

        private RubyContext/*!*/ _context;

        // $:
        private readonly RubyArray/*!*/ _loadPaths;

        // $"
        private readonly RubyArray/*!*/ _loadedFiles;

        // Files that a require has started and not finished, each mapped to the thread doing it.
        // Doubles as the monitor threads wait on when they want a file somebody else is loading.
        private readonly Dictionary<string, Thread>/*!*/ _unfinishedFiles;

        // lazy init
        private ConcurrentDictionary<string, Scope> _loadedScripts;

        // The assemblies this runtime loaded from files, each mapped to its file: a reference of one's that nothing else
        // resolves is looked for next to it (LoadAssemblyNextTo).
        private readonly ConcurrentDictionary<Assembly, string>/*!*/ _assemblyFiles = new ConcurrentDictionary<Assembly, string>();

        // Those of every runtime in the process: a reference made by another runtime's is left to that runtime
        // (ResolveAssembly).
        private static readonly ConditionalWeakTable<Assembly, string>/*!*/ _AssemblyFilesOfAllRuntimes = new ConditionalWeakTable<Assembly, string>();

        // Files compiled ahead of time (RegisterPrecompiledFile), by full path: the code, and whether the
        // source is still what it was compiled from (null: not checked). Null until one is registered.
        private Dictionary<string, (ScriptCode Code, Func<bool> IsCurrent)> _precompiledFiles;
        private readonly object/*!*/ _precompiledFilesLock = new object();

        /// <summary>
        /// TODO: Thread safety: the user of this object is responsible for locking it.
        /// </summary>
        public RubyArray/*!*/ LoadPaths {
            get { return _loadPaths; }
        }

        /// <summary>
        /// TODO: Thread safety: the user of this object is responsible for locking it.
        /// </summary>
        public RubyArray/*!*/ LoadedFiles {
            get { return _loadedFiles; }
        }

        /// <summary>
        /// Contains all loaded foreign language scripts. Maps path to scope created for each loaded script.
        /// A script is published here as soon as its scope is created just before it is executed.
        /// </summary>
        public IDictionary<string, Scope>/*!*/ LoadedScripts {
            get {
                if (_loadedScripts == null) {
                    Interlocked.CompareExchange(ref _loadedScripts, 
                        new ConcurrentDictionary<string, Scope>(DomainManager.Platform.PathComparer), null
                    );
                }
                return _loadedScripts;
            }
        }

        private PlatformAdaptationLayer/*!*/ Platform {
            get { return DomainManager.Platform; }
        }

        private ScriptDomainManager/*!*/ DomainManager {
            get { return _context.DomainManager; }
        }

        internal Loader(RubyContext/*!*/ context) {
            Assert.NotNull(context);
            _context = context;

            _toStrStorage = new ConversionStorage<MutableString>(context);
            _loadPaths = MakeLoadPaths(context.RubyOptions);
            _loadedFiles = new RubyArray();
            _unfinishedFiles = new Dictionary<string, Thread>();

            if (!context.RubyOptions.NoAssemblyResolveHook) {
                new AssemblyResolveHolder(this).HookAssemblyResolve();
            }
        }

        private RubyArray/*!*/ MakeLoadPaths(RubyOptions/*!*/ options) {
            var loadPaths = new RubyArray();
            
            if (options.HasSearchPaths) {
                foreach (string path in options.SearchPaths) {
                    loadPaths.Add(_context.EncodePath(path));
                    // -X:StdLib, which every launcher passes, names the library directories here.
                    if (_ironRubyLibraryPath == null &&
                        String.Equals(Path.GetFileName(path.TrimEnd('/', '\\')), "ironruby", StringComparison.OrdinalIgnoreCase)) {
                        _ironRubyLibraryPath = CanonicalDirectory(path);
                    }
                }
            }

            // No ".": MRI dropped the current directory from $LOAD_PATH in 1.9.2.
            AddStandardLibraryPath(loadPaths, options.StandardLibraryPath, options.ApplicationBase);
            return loadPaths;
        }

        /// <summary>
        /// IronRuby's own library directory, Src/StdLib/ironruby: where its implementations of
        /// CRuby's C extensions are, as .rb files (etc.rb loads the C# half). From -X:StdLib when
        /// it names one, else the default standard library's; spelled as FindFile spells a
        /// $LOAD_PATH directory, so the two compare.
        /// </summary>
        private string _ironRubyLibraryPath;

        private string/*!*/ CanonicalDirectory(string/*!*/ dir) {
            try {
                if (dir.Length > 0 && !Platform.IsAbsolutePath(dir)) {
                    dir = Platform.GetFullPath(dir);
                }
            } catch (Exception) {
                // as FindFile: the path as given
            }
            return RubyUtils.CanonicalizePath(ResolveDirectoryLinks(dir)).TrimEnd('/');
        }

        private void AddStandardLibraryPath(RubyArray/*!*/ loadPaths, string path, string applicationBaseDir) {
            bool isFullPath;
            if (path != null) {
                try {
                    isFullPath = Platform.IsAbsolutePath(path);
                } catch {
                    loadPaths.Add(_context.EncodePath(path));
                    return;
                }
            } else {
                // For developer use, add the Src/StdLib of the source tree the binaries were built in.
                // Not #if DEBUG: a Release build of the same tree has to find the same library, or its
                // default $LOAD_PATH points at a bin/Release/Lib that was never produced.
                path = FindSourceTreeStandardLibrary();
                if (path == null) {
                    path = "../Lib";
                }
                isFullPath = Platform.IsAbsolutePath(path);
            }

            if (!isFullPath) {
                if (String.IsNullOrEmpty(applicationBaseDir)) {
                    applicationBaseDir = _context.Platform.GetEnvironmentVariable(RubyContext.BinDirEnvironmentVariable);
                    if (!Directory.Exists(applicationBaseDir)) {
                        applicationBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                    }
                }

                try {
                    path = Platform.GetFullPath(RubyUtils.CombinePaths(applicationBaseDir, path));
                } catch {
                    loadPaths.Add(_context.EncodePath(path));
                    return;
                }
            }

            path = path.Replace('\\', '/');

            // As in MRI the site directory (RbConfig's sitelibdir) comes first, and every default
            // entry carries @gem_prelude_index (itself), which is how RubyGems tells them from the
            // -I ones before them.
            int firstDefault = loadPaths.Count;
            loadPaths.Add(_context.EncodePath(RubyUtils.CombinePaths(path, "ruby/site_ruby/" + _context.StandardLibraryVersion)));
            string ironRubyLibrary = RubyUtils.CombinePaths(path, "ironruby");
            if (_ironRubyLibraryPath == null) {
                _ironRubyLibraryPath = CanonicalDirectory(ironRubyLibrary);
            }
            loadPaths.Add(_context.EncodePath(ironRubyLibrary));
            string rubyLib4 = RubyUtils.CombinePaths(path, "ruby/4.0");
            if (Directory.Exists(rubyLib4)) {
                loadPaths.Add(_context.EncodePath(rubyLib4));
            }

            for (int i = firstDefault; i < loadPaths.Count; i++) {
                var entry = (MutableString)loadPaths[i];
                _context.SetInstanceVariable(entry, "@gem_prelude_index", entry);
                entry.Freeze();
            }
            }

        // bin/<config>/net8.0 of a project under Src/: the nearest ancestor with a StdLib/ironruby directory.
        // Returns null outside a source tree (a deployed layout), where the "../Lib" fallback is right.
        private static string FindSourceTreeStandardLibrary() {
            try {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent) {
                    string candidate = Path.Combine(dir.FullName, "StdLib");
                    if (Directory.Exists(Path.Combine(candidate, "ironruby"))) {
                        return candidate;
                    }
                }
            } catch (Exception) {
                // no access
            }
            return null;
        }

        /// <summary>
        /// Makes require/load of <paramref name="fullPath"/> run <paramref name="code"/> instead of
        /// compiling the file's source. The file is still resolved on the load path as usual.
        /// <paramref name="isCurrent"/> is asked each time the file is loaded whether the code still matches
        /// the source; once it does not, the file is compiled from source as any other. While Coverage
        /// measures (Coverage.start, which a program calls at any time), the file is compiled from source
        /// too: the precompiled code has no coverage counters.
        /// </summary>
        internal void RegisterPrecompiledFile(string/*!*/ fullPath, ScriptCode/*!*/ code, Func<bool> isCurrent) {
            Assert.NotNull(code);
            lock (_precompiledFilesLock) {
                (_precompiledFiles ??= new Dictionary<string, (ScriptCode, Func<bool>)>())[Platform.GetFullPath(fullPath)] = (code, isCurrent);
            }
        }

        private ScriptCode TryGetPrecompiledFile(string/*!*/ fullPath) {
            if (_precompiledFiles == null) {
                return null;
            }

            lock (_precompiledFilesLock) {
                if (!_precompiledFiles.TryGetValue(fullPath, out var file)) {
                    return null;
                }
                if (file.IsCurrent != null) {
                    if (_context.Coverage != null) {
                        return null;
                    }
                    // checked at every load, not only the first: `load' of a file edited since runs the edit
                    if (!file.IsCurrent()) {
                        _precompiledFiles.Remove(fullPath);
                        return null;
                    }
                }
                return file.Code;
            }
        }

        public bool LoadFile(Scope globalScope, object self, MutableString/*!*/ path, LoadFlags flags) {
            object loaded;
            return LoadFile(globalScope, self, path, flags, out loaded);
        }

        /// <summary>
        /// Returns <b>true</b> if a Ruby file is successfully loaded, <b>false</b> if it is already loaded.
        /// </summary>
        /// <param name="globalScope">
        /// A scope against which the file should be executed or null to create a new scope.
        /// </param>
        /// <param name="self"></param>
        /// <param name="path"></param>
        /// <param name="flags"></param>
        /// <param name="loaded"></param>
        /// <returns>True if the file was loaded/executed by this call.</returns>
        public bool LoadFile(Scope globalScope, object self, MutableString/*!*/ path, LoadFlags flags, out object loaded) {
            Assert.NotNull(path);

            string assemblyName, typeName;

            string strPath = _context.DecodePath(path);
            if (TryParseAssemblyName(strPath, out typeName, out assemblyName)) {

                if (AlreadyLoaded(strPath, flags, ArrayUtils.EmptyStrings)) {
                    loaded = ((flags & LoadFlags.ResolveLoaded) != 0) ? GetAssembly(assemblyName, true) : null;
                    return false;
                }

                Assembly assembly = LoadAssembly(assemblyName, typeName, false);
                if (assembly != null) {
                    FileLoaded(path.Clone(), flags);
                    loaded = assembly;
                    return true;
                }
            }

            return LoadFromPath(globalScope, self, strPath, path.Encoding, flags, out loaded);
        }

        #region Assemblies

        public Assembly LoadAssembly(string/*!*/ assemblyName, string typeName, bool throwOnError) {
            Assembly assembly = GetAssembly(assemblyName, throwOnError);
            return (assembly != null && LoadAssembly(assembly, typeName, throwOnError)) ? assembly : null;
        }
        
        private Assembly GetAssembly(string/*!*/ assemblyName, bool throwOnError) {
            try {
                return Platform.LoadAssembly(assemblyName);
            } catch (Exception) when (!throwOnError) {
                return null;
            } catch (Exception e) {
                throw RubyExceptions.CreateLoadError(e);
            }
        }
        
        private bool LoadAssembly(Assembly/*!*/ assembly, string typeName, bool throwOnError) {
            Utils.Log(String.Format("Loading assembly '{0}' and type '{1}'", assembly, typeName), "LOADER");
            Type initializerType;
            if (typeName != null) {
                // load Ruby library:
                initializerType = assembly.GetType(typeName);
                if (initializerType == null) {
                    if (throwOnError) {
                        throw new TypeLoadException(String.Format("Could not load type '{0}' from assembly '{1}'.", typeName, assembly.FullName));
                    }
                    return false;
                }

                LoadLibrary(initializerType, false);
            } else {
                // load namespaces:
                try {
                    DomainManager.LoadAssembly(assembly);
                } catch (Exception e) {
                    if (throwOnError) {
                        throw RubyExceptions.CreateLoadError(e);
                    }
                    return false;
                }
            }

            return true;
        }

        // Built on first use: every require asks TryParseAssemblyName, and almost no path has the '=' an
        // assembly name needs, so most processes never need the Regex (4 ms of the Loader's cctor).
        private static Regex _assemblyNameRegex;

        private static Regex/*!*/ AssemblyNameRegex => _assemblyNameRegex ??= new Regex(@"
            \s*((?<type>[\w.+]+)\s*,)?\s* # type name
            (?<assembly>
              [^,=]+\s*                   # assembly name
              (,\s*[\w]+\s*=\s*[^,]+\s*)+ # properties
            )", 
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline
        );

        internal static bool TryParseAssemblyName(string/*!*/ path, out string typeName, out string assemblyName) {
            // The regex needs a property - a "," and a later "=" - to match. Every require asks,
            // and an ordinary path has neither, so a quick look first: the unanchored regex
            // otherwise tries each position of the path, which made it a tenth of irb's startup.
            int comma = path.IndexOf(',');
            Match match = (comma >= 0 && path.IndexOf('=', comma) >= 0) ? AssemblyNameRegex.Match(path) : null;
            if (match != null && match.Success) {
                Group typeGroup = match.Groups["type"];
                Group assemblyGroup = match.Groups["assembly"];
                Debug.Assert(assemblyGroup.Success);

                typeName = typeGroup.Success ? typeGroup.Value : null;
                assemblyName = assemblyGroup.Value;
                return true;
            }

            if (path.Trim() == "mscorlib") {
                typeName = null;
                assemblyName = path;
                return true;
            }

            typeName = null;
            assemblyName = null;
            return false;
        }

        private sealed class AssemblyResolveHolder {
            private readonly WeakReference<Loader> _loader;

            [ThreadStatic]
            private static HashSet<string> _assembliesBeingResolved;

            public AssemblyResolveHolder(Loader/*!*/ loader) {
                _loader = new WeakReference<Loader>(loader);
            }

            internal void HookAssemblyResolve() {
                AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolveEvent;
            }

            private Assembly AssemblyResolveEvent(object sender, ResolveEventArgs args) {
                if (_loader.TryGetTarget(out Loader loader)) {
                    string assemblyName = args.Name;
                    Utils.Log(String.Format("assembly resolve event: {0}", assemblyName), "RESOLVE_ASSEMBLY");

                    if (_assembliesBeingResolved == null) {
                        _assembliesBeingResolved = new HashSet<string>();
                    } else if (_assembliesBeingResolved.Contains(assemblyName)) {
                        Utils.Log(String.Format("recursive assembly resolution: {0}", assemblyName), "RESOLVE_ASSEMBLY");
                        return null;
                    }

                    _assembliesBeingResolved.Add(assemblyName);
                    try {
                        return loader.ResolveAssembly(assemblyName, args.RequestingAssembly);
                    } catch (Exception e) {
                        // the exception might not be reported by the type loader, so at least report a warning:
                        loader._context.ReportWarning(
                            String.Format("An exception was risen while resolving an assembly '{0}': {1}", assemblyName, e.Message)
                        );
                        throw;
                    } finally {
                        _assembliesBeingResolved.Remove(assemblyName);
                    }
                } else {
                    AppDomain.CurrentDomain.AssemblyResolve -= AssemblyResolveEvent;
                    return null;
                }
            }
        }

        /// <summary>
        /// The assembly for a reference by name that .NET's own probing did not find: one this runtime has loaded already,
        /// else one on $LOAD_PATH, else one next to the assembly that asked, if that was loaded from a file.
        /// </summary>
        internal Assembly ResolveAssembly(string/*!*/ fullName, Assembly requestingAssembly) {
            Utils.Log(String.Format("Resolving assembly: '{0}'", fullName), "RESOLVE_ASSEMBLY");

            // The event is the process's, and every runtime in it is asked, in the order they hooked it. A reference
            // made by a DLL that another runtime loaded from a file is left to that one: this runtime would answer it
            // with its own copy of what the DLL references, loaded already or found on its $LOAD_PATH.
            if (requestingAssembly != null && !_assemblyFiles.ContainsKey(requestingAssembly) &&
                _AssemblyFilesOfAllRuntimes.TryGetValue(requestingAssembly, out _)) {
                return null;
            }

            AssemblyName assemblyName = new AssemblyName(fullName);
            return FindLoadedAssembly(assemblyName)
                ?? LoadAssemblyFromLoadPath(assemblyName, fullName)
                ?? LoadAssemblyNextTo(requestingAssembly, assemblyName);
        }

        /// <summary>
        /// One of the assemblies this runtime has loaded, if one will do. Most references that get here are made by
        /// a DLL required by its path: Assembly.LoadFile gives each file a load context of its own, where a reference
        /// by name finds the application's assemblies and no other. If the DLL referenced is loaded too, the reference
        /// is to that one. Loading it again, from $LOAD_PATH or the directory, would make a second copy with types
        /// of its own, and the DLL making the reference would take the copy's types, not those of the objects Ruby
        /// has made.
        /// </summary>
        private Assembly FindLoadedAssembly(AssemblyName/*!*/ reference) {
            foreach (Assembly assembly in DomainManager.GetLoadedAssemblyList()) {
                if (!assembly.IsDynamic && Satisfies(assembly.GetName(), reference)) {
                    Utils.Log(String.Format("Assembly '{0}' already loaded for '{1}'", assembly.FullName, reference.FullName), "RESOLVE_ASSEMBLY");
                    return assembly;
                }
            }
            return null;
        }

        private Assembly LoadAssemblyFromLoadPath(AssemblyName/*!*/ assemblyName, string/*!*/ fullName) {
            ResolvedFile file = FindFile(assemblyName.Name, true, ArrayUtils.EmptyStrings, false).FirstOrDefault();
            if (file == null || file.SourceUnit != null) {
                return null;
            }

            Utils.Log(String.Format("Assembly '{0}' resolved: found in '{1}'", fullName, file.Path), "RESOLVE_ASSEMBLY");
            try {
                // Checked before loading, as next to the requesting assembly (LoadAssemblyNextTo): an older copy here
                // must not answer a reference to a newer one, and a file that won't do isn't loaded at all.
                if (!Satisfies(AssemblyName.GetAssemblyName(file.Path), assemblyName)) {
                    return null;
                }

                Assembly assembly = LoadAssemblyFromFile(file.Path);
                Utils.Log(String.Format("Assembly '{0}' loaded for '{1}'", assembly.FullName, fullName), "RESOLVE_ASSEMBLY");
                DomainManager.LoadAssembly(assembly);
                return assembly;
            } catch (Exception e) {
                throw RubyExceptions.CreateLoadError(e);
            }
        }

        /// <summary>
        /// .NET Framework's LoadFrom context looked for an assembly's references next to it. Assembly.LoadFile,
        /// which require uses, does not, so a DLL required by its path found what it references only on
        /// $LOAD_PATH. This looks next to the assemblies loaded from files, after $LOAD_PATH, and loads what it
        /// finds there as what is found on $LOAD_PATH is loaded: its namespaces become Ruby's.
        /// </summary>
        private Assembly LoadAssemblyNextTo(Assembly requestingAssembly, AssemblyName/*!*/ reference) {
            string requestingFile;
            if (requestingAssembly == null || !_assemblyFiles.TryGetValue(requestingAssembly, out requestingFile)) {
                return null;
            }

            string path = Path.Combine(Path.GetDirectoryName(requestingFile), reference.Name + ".dll");

            // A file that won't do isn't loaded at all: nothing would use it, and it would stay loaded. One that isn't
            // an assembly (a native DLL of the same name) or can't be read isn't an answer either: the reference is
            // left unresolved, as if nothing were there.
            try {
                if (!Platform.FileExists(path) || !Satisfies(AssemblyName.GetAssemblyName(path), reference)) {
                    return null;
                }
            } catch (Exception e) when (e is IOException || e is BadImageFormatException) {
                return null;
            }

            Assembly assembly;
            try {
                assembly = LoadAssemblyFromFile(path);
            } catch (Exception e) {
                throw RubyExceptions.CreateLoadError(e);
            }
            Utils.Log(String.Format("Assembly '{0}' loaded for '{1}' from '{2}'", assembly.FullName, reference.FullName, path), "RESOLVE_ASSEMBLY");

            // The assembly is what the reference asks for even if its extension methods can't be read (they name a type
            // of an assembly that isn't there, say): that is reported, and the reference still gets it.
            try {
                DomainManager.LoadAssembly(assembly);
            } catch (Exception e) when (e is IOException || e is BadImageFormatException || e is TypeLoadException) {
                _context.ReportWarning(String.Format("extension methods of {0} not read: {1}", assembly.GetName().Name, e.Message.TrimEnd()));
            }
            return assembly;
        }

        /// <summary>
        /// Whether an assembly named <paramref name="definition"/> will do for a reference to <paramref name="reference"/>:
        /// the same simple name and culture and at least the version asked for, as .NET's binder requires of an
        /// application's assemblies, and the same public key token if the reference names one.
        /// AssemblyName.ReferenceMatchesDefinition compares the simple names only.
        /// </summary>
        private static bool Satisfies(AssemblyName/*!*/ definition, AssemblyName/*!*/ reference) {
            byte[] token = reference.GetPublicKeyToken();
            return String.Equals(definition.Name, reference.Name, StringComparison.OrdinalIgnoreCase)
                && String.Equals(definition.CultureName ?? "", reference.CultureName ?? "", StringComparison.OrdinalIgnoreCase)
                && (reference.Version == null || (definition.Version != null && definition.Version >= reference.Version))
                && (token == null || token.Length == 0 || token.AsSpan().SequenceEqual(definition.GetPublicKeyToken()));
        }

        /// <summary>
        /// Loads the assembly in the file at <paramref name="path"/>, remembering the file and that this runtime
        /// loaded it (ResolveAssembly).
        /// </summary>
        private Assembly/*!*/ LoadAssemblyFromFile(string/*!*/ path) {
            Assembly assembly = Platform.LoadAssemblyFromPath(path);
            _assemblyFiles.TryAdd(assembly, path);
            _AssemblyFilesOfAllRuntimes.AddOrUpdate(assembly, path);
            return assembly;
        }

        #endregion

        private class ResolvedFile {
            public readonly SourceUnit SourceUnit;
            public readonly string/*!*/ Path;
            public readonly string AppendedExtension;

            public ResolvedFile(SourceUnit/*!*/ sourceUnit, string/*!*/ fullPath, string appendedExtension) {
                Assert.NotNull(sourceUnit, fullPath);
                SourceUnit = sourceUnit;
                Path = fullPath; 
                AppendedExtension = appendedExtension;
            }

            public ResolvedFile(string/*!*/ fullLibraryPath, string appendedExtension) {
                Assert.NotNull(fullLibraryPath);
                Path = fullLibraryPath;
                AppendedExtension = appendedExtension;
            }
        }

        /// <summary>
        /// MRI 1.9 changed the wording to "cannot load such file", and 2.0 started recording the
        /// path on the exception -- LoadError#path (see Src/StdLib/ironruby/ruby4.rb) reads @path,
        /// and answered nil for every load error because nothing ever set it.
        /// </summary>
        private Exception/*!*/ MissingFileError(string/*!*/ path) {
            Exception error = RubyExceptions.CreateLoadError(String.Format("cannot load such file -- {0}", path));
            _context.SetInstanceVariable(error, "@path", _context.EncodePath(path));
            return error;
        }

        private bool LoadFromPath(Scope globalScope, object self, string/*!*/ path, RubyEncoding/*!*/ pathEncoding, LoadFlags flags, out object loaded) {
            Assert.NotNull(pathEncoding, path);

            string[] sourceFileExtensions;
            if ((flags & LoadFlags.AnyLanguage) != 0) {
                sourceFileExtensions = DomainManager.Configuration.GetFileExtensions();
            } else {
                sourceFileExtensions = DomainManager.Configuration.GetFileExtensions(_context);
            }

            // #require never looks in the current directory; #load does.
            IList<ResolvedFile> files = FindFile(path, (flags & LoadFlags.AppendExtensions) != 0, sourceFileExtensions,
                (flags & LoadFlags.LoadOnce) == 0);

            if (files.Count == 0) {
                // MRI: doesn't throw an exception if the path is in $" (performs resolution first though):
                if (AlreadyLoaded(path, flags, sourceFileExtensions)) {
                    loaded = null;
                    return false;
                }
                throw MissingFileError(path);
            }

            ResolvedFile file = files.First();

            string pathWithExtension = path;
            if (file.AppendedExtension != null) {
                pathWithExtension += file.AppendedExtension;
            }

            // Claim the file, or find out why we cannot have it.  Three ways that can go: it is
            // already loaded; this very thread is already loading it, which is a circular require and
            // answers false at once; or another thread is loading it, and then MRI has us wait for
            // that thread rather than answer false while the file's definitions do not exist yet.
            // The wait is also what makes the waiting thread observable - it is sleeping inside
            // require, which is what a spec watching it expects to see.
            // Only #require has anything to claim. #load runs the file every time it is called,
            // including from inside itself, and taking the claim here made a file that loads itself
            // execute once instead of twice.
            bool tracked = (flags & LoadFlags.LoadOnce) != 0;

            bool claimed;
            if (!tracked) {
                claimed = true;
            } else {
                lock (_unfinishedFiles) {
                    Thread owner;
                    while (_unfinishedFiles.TryGetValue(file.Path, out owner) && owner != Thread.CurrentThread) {
                        try {
                            Monitor.Wait(_unfinishedFiles);
                        } catch (ThreadInterruptedException) {
                            // Thread#kill and Thread#raise deliver by parking an exception and interrupting the
                            // target's wait, so an interrupt here is either that exception - in which case this
                            // throws it, holding no claim on the file - or somebody else's, in which case the
                            // wait simply resumes.
                            RubyUtils.TranslateThreadInterrupt();
                        }
                    }

                    bool circular = _unfinishedFiles.ContainsKey(file.Path);
                    claimed = !circular && !AlreadyLoaded(path, files, flags);
                    if (claimed) {
                        // save path as is, no canonicalization nor combination with an extension or directory:
                        _unfinishedFiles.Add(file.Path, Thread.CurrentThread);
                    }

                    // MRI warns under -w. Only for a require the program made: an autoloaded
                    // constant whose file reopens the very class being autoloaded is a circularity
                    // the user did not write.
                    if (circular && (flags & LoadFlags.WarnCircular) != 0) {
                        _context.ReportWarning("loading in progress, circular require considered harmful - " + file.Path, true);
                    }
                }
            }

            if (!claimed) {
                if ((flags & LoadFlags.ResolveLoaded) != 0) {
                    if (file.SourceUnit != null) {
                        Scope loadedScope;
                        if (!LoadedScripts.TryGetValue(file.Path, out loadedScope)) {
                            throw MissingFileError(file.Path);
                        }
                        loaded = loadedScope;
                    } else {
                        loaded = Platform.LoadAssemblyFromPath(file.Path);
                    }
                } else {
                    loaded = null;
                }
                return false;
            }

            var autoloads = tracked ? BeginAutoloadsOf(file.Path) : null;
            bool succeeded = false;
            try {
                if (file.SourceUnit != null) {
                    AddScriptLines(file.SourceUnit);

                    long traceStart = StartupTrace.Now();
                    StartupTrace.BeginFile();
                    try {
                        ScriptCode compiledCode;
                        if (file.SourceUnit.LanguageContext == _context) {
                            compiledCode = CompileRubySource(file.SourceUnit, flags);
                        } else {
                            compiledCode = file.SourceUnit.Compile();
                        }
                        // A file the prelude requires from outside its own directory - an argf.rb
                        // earlier on the load path, say - is a program's, and its constants keep
                        // their locations: it runs with the marker lifted.
                        Thread thread = Thread.CurrentThread;
                        bool corePrelude = _corePreludeLoaderThread == thread && _corePreludeDirectory == null
                            && Path.GetFileName(file.Path) == "ruby4.rb";
                        bool foreign = !corePrelude && _corePreludeThread == thread && !IsInCorePreludeDirectory(file.Path);
                        if (corePrelude) {
                            _corePreludeDirectory = Path.GetDirectoryName(Path.GetFullPath(file.Path)) + Path.DirectorySeparatorChar;
                            _corePreludeThread = thread;
                        } else if (foreign) {
                            _corePreludeThread = null;
                        }
                        try {
                            loaded = Execute(globalScope, compiledCode);
                        } finally {
                            if (corePrelude) {
                                _corePreludeThread = null;
                            } else if (foreign) {
                                _corePreludeThread = thread;
                            }
                        }
                    } finally {
                        StartupTrace.EndFile(file.Path, traceStart);
                    }
                } else {
                    Debug.Assert(file.Path != null);
                    try {
                        Assembly assembly = LoadAssemblyFromFile(file.Path);
                        if (!DomainManager.LoadAssembly(assembly)) {
                            // The runtime has seen the assembly, which it reports once: maybe in a require that failed
                            // reading its extension methods. This reads them if they haven't been read.
                            _context.AddExtensionAssembly(assembly);
                        }
                        loaded = assembly;
                    } catch (BadImageFormatException) when (IsNativeExtensionPath(file.Path)) {
                        // A gem's compiled CRuby extension. It is not a CLR assembly and never will
                        // be, so say that rather than report a malformed one: "Bad IL format" sends
                        // people looking for a corrupt file.
                        throw RubyExceptions.CreateLoadError(
                            "cannot load such file -- " + file.Path +
                            " (a C extension; IronRuby has no C extension host)"
                        );
                    } catch (Exception e) {
                        throw RubyExceptions.CreateLoadError(e);
                    }
                }

                // a non-ASCII path given in BINARY (or US-ASCII) - what the C locale's path layer hands out - was the file
                // system's UTF-8 bytes (see RubyContext.DecodePath), and is recorded in the filesystem encoding, as MRI
                // does, not one byte per character
                FileLoaded((pathEncoding == RubyEncoding.Binary || pathEncoding == RubyEncoding.Ascii) && !file.Path.IsAscii() ?
                    _context.EncodePath(file.Path) :
                    MutableString.Create(file.Path, pathEncoding), flags);
                succeeded = true;
            } finally {
                EndAutoloads(autoloads, file.Path, succeeded);
                // Only release what was claimed: an untracked #load must not drop the claim a
                // concurrent #require of the same file is holding.
                if (tracked) {
                    lock (_unfinishedFiles) {
                        _unfinishedFiles.Remove(file.Path);
                        Monitor.PulseAll(_unfinishedFiles);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// True for the file name of a compiled CRuby extension (what a gem's extconf.rb builds).
        /// </summary>
        private static bool IsNativeExtensionPath(string path) {
            if (path == null) {
                return false;
            }
            return path.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase);
        }

        private ScriptCode/*!*/ CompileRubySource(SourceUnit/*!*/ sourceUnit, LoadFlags flags) {
            Assert.NotNull(sourceUnit);
            
            _context.RegisterSourceFileLocation(sourceUnit.Path);

            // a precompiled top level is a file's, not the anonymous module `load(file, true)' wraps it in
            if ((flags & LoadFlags.LoadIsolated) == 0) {
                ScriptCode precompiled = TryGetPrecompiledFile(Platform.GetFullPath(sourceUnit.Path));
                if (precompiled != null) {
                    return precompiled;
                }
            }

            if (IronRuby.Runtime.Precompiled.PrecompiledCode.Trace) {
                Console.Error.WriteLine("IronRuby.Startup: compiling " + Platform.GetFullPath(sourceUnit.Path));
            }

            RubyCompilerOptions options = new RubyCompilerOptions() {
                FactoryKind = (flags & LoadFlags.LoadIsolated) != 0 ? TopScopeFactoryKind.WrappedFile : TopScopeFactoryKind.File
            };

            return sourceUnit.Compile(options, _context.RuntimeErrorSink);
        }

        internal Scope Execute(Scope globalScope, ScriptCode/*!*/ code) {
            if (globalScope == null || code.LanguageContext != _context) {
                if (globalScope == null) {
                    globalScope = code.CreateScope();
                }

                if (code.SourceUnit.Path != null) {
                    LoadedScripts[Platform.GetFullPath(code.SourceUnit.Path)] = globalScope;
                }
                code.Run(globalScope);
                return globalScope;
            } else {
                code.Run(globalScope);
                return null;
            }
        }

        /// <summary>
        /// Searches file in load directories and then appends extensions.
        /// </summary>
        private IList<ResolvedFile> FindFile(string/*!*/ path, bool appendExtensions, string[] sourceFileExtensions, bool searchCurrentDirectory) {
            Assert.NotNull(path);
            bool isAbsolutePath;

            if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)) {
                path = RubyUtils.ExpandPath(_context.Platform, path);
                isAbsolutePath = true;
            } else {
                try {
                    isAbsolutePath = Platform.IsAbsolutePath(path);
                } catch (ArgumentException e) {
                    throw RubyExceptions.CreateLoadError(e);
                }
            }

            string extension = RubyUtils.GetExtension(path);

            // Absolute path -> load paths not consulted.
            if (isAbsolutePath) {
                var file = ResolveFile(path, extension, appendExtensions, sourceFileExtensions);
                return file != null ? new[] { file } : new ResolvedFile[0];
            }

            string[] loadPaths = GetLoadPathStrings();

            if (loadPaths.Length == 0) {
                return CurrentDirectoryFallback(path, extension, appendExtensions, sourceFileExtensions, searchCurrentDirectory);
            }

            // If load paths are non-empty and the path starts with .\ or ..\ then MRI also ignores the load paths.
            if (path.StartsWith("./", StringComparison.Ordinal) ||
                path.StartsWith("../", StringComparison.Ordinal) ||
                path.StartsWith(".\\", StringComparison.Ordinal) ||
                path.StartsWith("..\\", StringComparison.Ordinal)) {

                var file = ResolveFile(path, extension, appendExtensions, sourceFileExtensions);
                return file != null ? new[] { file } : new ResolvedFile[0];
            }

            var result = new List<ResolvedFile>();
            foreach (var loadPath in loadPaths) {
                // MRI expands a relative $LOAD_PATH entry (such as ".") against the current
                // directory, so the feature - and __FILE__ in it - gets an absolute path
                string dir = loadPath;
                try {
                    if (dir.Length > 0 && !Platform.IsAbsolutePath(dir)) {
                        dir = Platform.GetFullPath(dir);
                    }
                } catch (Exception) {
                    dir = loadPath;
                }
                dir = ResolveDirectoryLinks(dir);
                // GetFullPath hands back a path in the platform's own spelling, which on
                // Windows is backslashed; Ruby paths - and so __FILE__ and $LOADED_FEATURES
                // - use forward slashes everywhere.
                dir = RubyUtils.CanonicalizePath(dir);
                ResolvedFile file = ResolveFile(RubyUtils.CombinePaths(dir, path), extension, appendExtensions, sourceFileExtensions);
                // A C extension by its library name ("etc.so", as tmpdir.rb requires it) is IronRuby's
                // implementation of that extension, as JRuby loads its own for one: the .rb of the
                // same name in IronRuby's library directory. Not finding it cost a LoadError and
                // RubyGems' search of every installed gem - a quarter of a second, the first time.
                if (file == null && _ironRubyLibraryPath != null &&
                    extension.Equals(".so", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(dir.TrimEnd('/'), _ironRubyLibraryPath, StringComparison.OrdinalIgnoreCase)) {
                    string source = path.Substring(0, path.Length - extension.Length) + ".rb";
                    file = ResolveFile(RubyUtils.CombinePaths(dir, source), ".rb", false, sourceFileExtensions);
                }
                if (file != null) {
                    result.Add(file);
                }
            }

            if (result.Count == 0) {
                return CurrentDirectoryFallback(path, extension, appendExtensions, sourceFileExtensions, searchCurrentDirectory);
            }

            // MRI tries each extension over the whole $LOAD_PATH before the next one, so for a
            // bare name a .rb file anywhere wins over a library found in an earlier directory.
            // A name whose last dot starts no extension Ruby knows - "cool.io_ext" - is a bare
            // name too: MRI appends .rb and .so to it just the same.
            bool bareName = extension.Length == 0 ||
                (!IsKnownExtension(extension, sourceFileExtensions ?? ArrayUtils.EmptyStrings) &&
                 !_LibraryExtensions.Contains(extension, DlrConfiguration.FileExtensionComparer));
            if (appendExtensions && bareName && result.Count > 1 && result[0].SourceUnit == null) {
                result = result.Where((f) => f.SourceUnit != null).Concat(result.Where((f) => f.SourceUnit == null)).ToList();
            }

            return result;
        }

        /// <summary>
        /// MRI takes the real path of a $LOAD_PATH entry (though not of the file found in it), so
        /// a feature required through a symlinked directory is recorded under the directory it
        /// links to. Anything that cannot be resolved is used as it is.
        /// </summary>
        private static string/*!*/ ResolveDirectoryLinks(string/*!*/ dir) {
            if (dir.Length == 0 || dir[0] != '/' || Path.DirectorySeparatorChar != '/') {
                return dir;
            }
            try {
                string resolved = "";
                foreach (string part in dir.Split('/')) {
                    if (part.Length == 0 || part == ".") {
                        continue;
                    }
                    if (part == "..") {
                        return dir;
                    }
                    resolved += "/" + part;
                    var info = new DirectoryInfo(resolved);
                    if (!info.Exists) {
                        return dir;
                    }
                    if (info.LinkTarget != null) {
                        var target = info.ResolveLinkTarget(true);
                        if (target == null) {
                            return dir;
                        }
                        resolved = target.FullName.TrimEnd('/');
                    }
                }
                return resolved.Length == 0 ? "/" : resolved;
            } catch (Exception) {
                return dir;
            }
        }

        /// <summary>
        /// #load, unlike #require, falls back to the current working directory when $LOAD_PATH does
        /// not hold the file -- load("load_fixture.rb") from inside the directory works in MRI and
        /// was a LoadError here.
        /// </summary>
        private ResolvedFile[]/*!*/ CurrentDirectoryFallback(string/*!*/ path, string/*!*/ extension, bool appendExtensions,
            string[] sourceFileExtensions, bool searchCurrentDirectory) {

            if (!searchCurrentDirectory) {
                return new ResolvedFile[0];
            }
            ResolvedFile file = ResolveFile(path, extension, appendExtensions, sourceFileExtensions);
            return file != null ? new[] { file } : new ResolvedFile[0];
        }

        internal string[]/*!*/ GetLoadPathStrings() {
            var loadPaths = GetLoadPaths();
            var result = new string[loadPaths.Length];
            var toPath = _toStrStorage.GetSite(CompositeConversionAction.Make(_context, CompositeConversion.ToPathToStr));

            for (int i = 0; i < loadPaths.Length; i++) {
                if (loadPaths[i] == null) {
                    throw RubyExceptions.CreateImplicitConversionError("nil", "String");
                }

                result[i] = _context.DecodePath(Protocols.CastToPath(toPath, loadPaths[i]));
            }

            return result;
        }

        /// <summary>
        /// Appends extensions if applicable.
        /// </summary>
        private ResolvedFile ResolveFile(string/*!*/ path, string/*!*/ extension, bool appendExtensions, string[]/*!*/ knownExtensions) {
            Debug.Assert(RubyUtils.GetExtension(path) == extension);

            string expandedPath = RubyUtils.ExpandPath(_context.Platform, path);

            // MRI doesn't load file w/o .rb extension:
            if (IsKnownExtension(extension, knownExtensions)) {
                return GetSourceUnit(path, expandedPath, extension, false);
            } 
            
            if (_LibraryExtensions.Contains(extension, DlrConfiguration.FileExtensionComparer)) {
                if (Platform.FileExists(expandedPath)) {
                    return new ResolvedFile(expandedPath, null);
                }
            } else if (!appendExtensions) {
                return GetSourceUnit(path, expandedPath, extension, false);
            }

            if (appendExtensions) {
                List<string> matchingExtensions = GetExtensionsOfExistingFiles(expandedPath, knownExtensions);

                if (matchingExtensions.Count == 1) {
                    return GetSourceUnit(path + matchingExtensions[0], expandedPath + matchingExtensions[0], matchingExtensions[0], true);
                } else if (matchingExtensions.Count > 1) {
                    Exception e = new AmbiguousFileNameException(expandedPath + matchingExtensions[0], expandedPath + matchingExtensions[1]);
                    throw RubyExceptions.CreateLoadError(e);
                }

                foreach (string libExtension in _LibraryExtensions) {
                    if (Platform.FileExists(expandedPath + libExtension)) {
                        return new ResolvedFile(expandedPath + libExtension, libExtension);
                    }
                }
            }

            return null;
        }

        private static readonly string[] _LibraryExtensions = new string[] { ".dll", ".so", ".exe" };

        private static bool IsKnownExtension(string/*!*/ extension, string[]/*!*/ knownExtensions) {
            return extension.Length > 0 && knownExtensions.Contains(extension, DlrConfiguration.FileExtensionComparer);
        }

        private ResolvedFile GetSourceUnit(string/*!*/ path, string/*!*/ fullPath, string/*!*/ extension, bool extensionAppended) {
            Assert.NotNull(path, fullPath, extension);

            LanguageContext language;
            if (extension.Length == 0 || !DomainManager.TryGetLanguageByFileExtension(extension, out language)) {
                // Ruby by default:
                language = _context;
            }

            if (!DomainManager.Platform.FileExists(fullPath)) {
                return null;
            }

            var sourceUnit = language.CreateFileUnit(path, RubyEncoding.Ascii.Encoding, SourceCodeKind.File);
            List<string> log = SourceFileLog;
            if (log != null) {
                lock (log) {
                    log.Add(fullPath);
                }
            }
            return new ResolvedFile(sourceUnit, fullPath, extensionAppended ? extension : null);
        }

        /// <summary>
        /// When set, every source file the loader resolves is added to it - the prelude's own files too,
        /// which never show in $". A warm-start worker (Hosting/WarmStart.cs) watches them for changes.
        /// </summary>
        internal List<string> SourceFileLog { get; set; }

        private List<string>/*!*/ GetExtensionsOfExistingFiles(string/*!*/ path, IEnumerable<string>/*!*/ extensions) {
            // all extensions that could be appended to the path to get an existing file:
            List<string> result = new List<string>();
            foreach (string extension in extensions) {
                Debug.Assert(extension != null && extension.StartsWith("."));
                string fullPath = path + extension;
                if (Platform.FileExists(fullPath)) {
                    result.Add(extension);
                }
            }
            return result;
        }

        #region Global Variables

        private readonly ConversionStorage<MutableString>/*!*/ _toStrStorage;

        internal object[]/*!*/ GetLoadPaths() {
            lock (_loadPaths) {
                return _loadPaths.ToArray();
            }
        }

        public void SetLoadPaths(IEnumerable<string/*!*/>/*!*/ paths) {
            ContractUtils.RequiresNotNullItems(paths, "paths");

            lock (_loadPaths) {
                _loadPaths.Clear();
                foreach (string path in paths) {
                    _loadPaths.Add(_context.EncodePath(path));
                }
            }
        }

        #region Autoloads

        // Autoloads that have not run yet. Requiring the very file an autoload names is that autoload
        // running, as far as MRI is concerned: while the file loads, the requiring thread does not see
        // the pending autoload, and once it is loaded the autoload has nothing left to do.
        private readonly List<WeakReference<RubyModule.AutoloadedConstant>>/*!*/ _pendingAutoloads =
            new List<WeakReference<RubyModule.AutoloadedConstant>>();

        internal static string/*!*/ GetFeatureName(string/*!*/ path) {
            string name = path.Substring(path.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            return name.EndsWith(".rb", StringComparison.Ordinal) ? name.Substring(0, name.Length - 3) : name;
        }

        internal void RegisterAutoload(RubyModule.AutoloadedConstant/*!*/ autoload) {
            lock (_pendingAutoloads) {
                _pendingAutoloads.Add(new WeakReference<RubyModule.AutoloadedConstant>(autoload));
            }
        }

        // The full path #require would load for the given feature, or null.
        private string ResolveFeature(string/*!*/ path) {
            try {
                var files = FindFile(path, true, DomainManager.Configuration.GetFileExtensions(_context), false);
                return files.Count > 0 ? files[0].Path : null;
            } catch (Exception) {
                return null;
            }
        }

        /// <summary>
        /// An autoload of a file that has already been required - or that this thread is requiring right
        /// now, which is an autoload of __FILE__ - has nothing to load. Returns that file's full path.
        /// </summary>
        internal string GetRequiredFeature(string/*!*/ path) {
            // Only a file whose name matches is worth the file-system probes of resolving the path.
            string name = GetFeatureName(path);
            bool candidate = false;
            lock (_unfinishedFiles) {
                foreach (var entry in _unfinishedFiles) {
                    if (entry.Value == Thread.CurrentThread && GetFeatureName(entry.Key) == name) {
                        candidate = true;
                        break;
                    }
                }
            }
            if (!candidate) {
                foreach (object file in GetLoadedFiles()) {
                    var loadedPath = file as MutableString;
                    if (loadedPath != null && GetFeatureName(loadedPath.ToString()) == name) {
                        candidate = true;
                        break;
                    }
                }
            }
            if (!candidate) {
                return null;
            }

            string fullPath = ResolveFeature(path);
            return fullPath != null && IsFeatureRequiredOrRequiring(fullPath) ? fullPath : null;
        }

        internal bool IsFeatureRequiredOrRequiring(string/*!*/ fullPath) {
            lock (_unfinishedFiles) {
                Thread owner;
                if (_unfinishedFiles.TryGetValue(fullPath, out owner) && owner == Thread.CurrentThread) {
                    return true;
                }
            }
            return AnyFileLoaded(new[] { _context.EncodePath(fullPath) });
        }

        // Takes over, for the current thread, every pending autoload that names the file being required.
        private List<RubyModule.AutoloadedConstant> BeginAutoloadsOf(string/*!*/ fullPath) {
            string name = GetFeatureName(fullPath);
            List<RubyModule.AutoloadedConstant> candidates = null;
            lock (_pendingAutoloads) {
                for (int i = _pendingAutoloads.Count - 1; i >= 0; i--) {
                    RubyModule.AutoloadedConstant autoload;
                    if (!_pendingAutoloads[i].TryGetTarget(out autoload) || autoload.Loaded) {
                        _pendingAutoloads.RemoveAt(i);
                    } else if (autoload.FeatureName == name) {
                        if (candidates == null) {
                            candidates = new List<RubyModule.AutoloadedConstant>();
                        }
                        candidates.Add(autoload);
                    }
                }
            }
            if (candidates == null) {
                return null;
            }

            List<RubyModule.AutoloadedConstant> result = null;
            foreach (var autoload in candidates) {
                if (ResolveFeature(_context.DecodePath(autoload.Path)) == fullPath) {
                    if (result == null) {
                        result = new List<RubyModule.AutoloadedConstant>();
                        _context.EnterAutoload();
                    }
                    autoload.BeginLoad();
                    result.Add(autoload);
                }
            }
            return result;
        }

        private void EndAutoloads(List<RubyModule.AutoloadedConstant> autoloads, string/*!*/ fullPath, bool loaded) {
            if (autoloads == null) {
                return;
            }
            foreach (var autoload in autoloads) {
                if (loaded) {
                    autoload.RequiredAs(fullPath);
                }
                autoload.EndLoad();
            }
            _context.LeaveAutoload();
        }

        #endregion

        // The thread running ruby4.rb as the host's core prelude, while it does - directly under
        // --disable-gems, else from gem_prelude.rb's first line. The only Ruby code on its stack then
        // is the prelude's own, which backtraces leave out.
        private Thread _corePreludeThread;

        // The thread in LoadCorePrelude, while it is: the ruby4.rb it loads is the core prelude, a
        // later `load "ruby4.rb"` by a program is not.
        private Thread _corePreludeLoaderThread;

        // The directory of the core prelude's ruby4.rb, with a trailing separator, once it has started.
        private string _corePreludeDirectory;

        private bool IsInCorePreludeDirectory(string/*!*/ path) {
            return Path.GetFullPath(path).StartsWith(_corePreludeDirectory, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True while this thread runs ruby4.rb as the core prelude, loaded by the host itself: a
        /// backtrace taken now has no frame in it (they are all the prelude's, which backtraces
        /// skip), so a caller that only wants the innermost location need not walk the CLR stack.
        /// </summary>
        internal bool IsLoadingCorePrelude {
            get { return _corePreludeThread == Thread.CurrentThread; }
        }

        /// <summary>
        /// Runs the load of a file a core autoload names - set.rb, for the Set MRI has from the start -
        /// as part of the core prelude, whenever it happens: its helpers are unlisted, its constants have no
        /// location and its autoloads are core too, as they would be had the prelude loaded it up front.
        /// A thread that finds another one in the prelude just loads the file.
        /// </summary>
        internal bool LoadAsCorePrelude(Func<bool>/*!*/ load) {
            var thread = Thread.CurrentThread;
            if (_corePreludeDirectory == null || Interlocked.CompareExchange(ref _corePreludeThread, thread, null) != null) {
                return load();
            }
            try {
                return load();
            } finally {
                _corePreludeThread = null;
            }
        }

        // Set while the core prelude (gem_prelude.rb and everything it requires, or ruby4.rb) loads,
        // on whichever thread: RubyContext.CompilationThreshold reads it.
        private volatile bool _loadingCorePrelude;

        internal bool LoadingCorePrelude {
            get { return _loadingCorePrelude; }
        }

        /// <summary>
        /// Requires the core prelude - gem_prelude.rb, or ruby4.rb under --disable-gems - and then
        /// lists the features MRI has in $" from the start.
        /// </summary>
        internal void LoadCorePrelude(Scope/*!*/ globalScope, object self, string/*!*/ path) {
            // the prelude, rubygems and the rest of startup compiled ahead of time, if there is such a library
            IronRuby.Runtime.Precompiled.PrecompiledCode.TryRegisterStartupLibrary(_context);
            _corePreludeLoaderThread = Thread.CurrentThread;
            _loadingCorePrelude = true;
            PhaseTimer.MarkPrelude(true);
            // The prelude's hot code is mostly not the program's: compiling it on another thread while the prelude
            // loads only competes with the loading (the compilations of `ir -e 1' took ~180 ms of CPU). What is
            // still hot once the program runs is compiled then (see BackgroundCompilation).
            var holdCompilation = Microsoft.Scripting.Interpreter.BackgroundCompilation.HoldBack();
            try {
                LoadFile(globalScope, self, MutableString.Create(path, RubyEncoding.UTF8), LoadFlags.Require);
            } finally {
                _loadingCorePrelude = false;
                PhaseTimer.MarkPrelude(false);
                _corePreludeLoaderThread = null;
                holdCompilation.Dispose();
            }
            ProvideCoreFeatures();
        }

        // Features the core prelude loaded: kept out of $" (MRI's own Ruby-defined core is not in
        // it either) but still loaded, so that requiring one again does nothing.
        private readonly HashSet<string>/*!*/ _hiddenLoadedFiles = new HashSet<string>(StringComparer.Ordinal);

        private static readonly string[]/*!*/ _CorePreludeFiles = {
            "gem_prelude.rb", "ruby4.rb", "argf.rb", "thread.rb", "rational.rb", "complex.rb",
            "gem_compat.rb"
        };

        // What MRI 4.0 has in $" before the program starts: features that are part of the core
        // and so already required.
        private static readonly string[]/*!*/ _ProvidedFeatures = {
            "enumerator.so", "thread.rb", "fiber.so", "rational.so", "complex.so", "pathname.so", "ruby2_keywords.rb", "set.rb"
        };

        /// <summary>
        /// Called once the core prelude has been required: hides the prelude's own files from $"
        /// and lists the provided features instead, as MRI does.
        /// </summary>
        internal void ProvideCoreFeatures() {
            lock (_loadedFiles) {
                for (int i = _loadedFiles.Count - 1; i >= 0; i--) {
                    var path = _loadedFiles[i] as MutableString;
                    if (path != null && Array.IndexOf(_CorePreludeFiles, Path.GetFileName(path.ToString())) >= 0) {
                        _hiddenLoadedFiles.Add(path.ToString());
                        _loadedFiles.RemoveAt(i);
                    }
                }
                for (int i = 0; i < _ProvidedFeatures.Length; i++) {
                    _loadedFiles.Insert(i, MutableString.CreateAscii(_ProvidedFeatures[i]));
                }
            }
        }

        private void AddLoadedFile(MutableString/*!*/ path) {
            lock (_loadedFiles) {
                _loadedFiles.Add(path);
            }
        }

        private void FileLoaded(MutableString/*!*/ path, LoadFlags flags) {
            if ((flags & LoadFlags.LoadOnce) != 0) {
                AddLoadedFile(path);
            }
        }

        /// <summary>
        /// If the SCRIPT_LINES__ constant is set, we need to publish the file being loaded,
        /// along with the contents of the file
        /// </summary>
        private void AddScriptLines(SourceUnit file) {            
            ConstantStorage storage;
            if (!_context.ObjectClass.TryResolveConstant(null, "SCRIPT_LINES__", out storage)) {
                return;
            }

            IDictionary scriptLines = storage.Value as IDictionary;
            if (scriptLines == null) {
                return;
            }

            lock (scriptLines) {
                // Read in the contents of the file

                RubyArray lines = new RubyArray();
                SourceCodeReader reader = file.GetReader();
                RubyEncoding encoding = RubyEncoding.GetRubyEncoding(reader.Encoding);
                using (reader) {
                    reader.SeekLine(1);
                    while (true) {
                        string lineStr = reader.ReadLine();
                        if (lineStr == null) {
                            break;
                        }
                        MutableString line = MutableString.CreateMutable(lineStr.Length + 1, encoding);
                        line.Append(lineStr).Append('\n');
                        lines.Add(line);
                    }
                }

                // Publish the contents of the file, keyed by the file name
                MutableString path = MutableString.Create(file.Document.FileName, _context.GetPathEncoding());
                scriptLines[path] = lines;
            }
        }

        internal object[]/*!*/ GetLoadedFiles() {
            lock (_loadedFiles) {
                return _loadedFiles.ToArray();
            }
        }

        private bool AlreadyLoaded(string/*!*/ path, LoadFlags flags, string[]/*!*/ sourceFileExtensions) {
            return (flags & LoadFlags.LoadOnce) != 0 && AnyFileLoaded(GetPathsToTestLoaded(path, flags, sourceFileExtensions));
        }

        private IEnumerable<MutableString>/*!*/ GetPathsToTestLoaded(string/*!*/ path, LoadFlags flags, string[]/*!*/ sourceFileExtensions) {
            List<MutableString> paths = new List<MutableString>();
            paths.Add(_context.EncodePath(path));

            if ((flags & LoadFlags.AppendExtensions) != 0 && RubyUtils.GetExtension(path).Length == 0) {
                foreach (var extension in sourceFileExtensions) {
                    paths.Add(_context.EncodePath(path + extension));
                }
                foreach (var extension in _LibraryExtensions) {
                    paths.Add(_context.EncodePath(path + extension));
                }
            }

            return paths;
        }

        /// <summary>
        /// Return true if any of the files has alraedy been loaded.
        /// </summary>
        private bool AlreadyLoaded(string/*!*/ path, IEnumerable<ResolvedFile>/*!*/ files, LoadFlags flags) {
            // An extensionless entry in $" says nothing about a file that was found: MRI only
            // counts "foo" as loaded when no foo.rb (or library) could be found at all.
            // A feature listed by name ("set.rb", "complex.so" - what MRI provides) is loaded for
            // a require of that name, with or without the extension. Except that a library
            // ("foo.so") does not stand in for a Ruby file found on the path: MRI's
            // search_required goes on to look for foo.rb when only foo.so is in $", which is how
            // `require "pathname"` loads lib/pathname.rb over the core's "pathname.so".
            IEnumerable<MutableString> requested;
            if (RubyUtils.GetExtension(path).Length != 0) {
                requested = new[] { _context.EncodePath(path) };
            } else if (!Platform.IsAbsolutePath(path) && !path.StartsWith(".", StringComparison.Ordinal)) {
                var found = files.FirstOrDefault();
                bool foundSource = found != null && found.SourceUnit != null;
                requested = foundSource
                    ? new[] { _context.EncodePath(path + ".rb") }
                    : new[] { _context.EncodePath(path + ".rb"), _context.EncodePath(path + ".so") };
            } else {
                requested = Enumerable.Empty<MutableString>();
            }
            return (flags & LoadFlags.LoadOnce) != 0 && AnyFileLoaded(
                requested.Concat(files.Select((file) => _context.EncodePath(file.Path)))
            );
        }

        private bool AnyFileLoaded(IEnumerable<MutableString>/*!*/ paths) {
            var toPath = _toStrStorage.GetSite(CompositeConversionAction.Make(_context, CompositeConversion.ToPathToStr));

            foreach (object file in GetLoadedFiles()) {
                if (file == null) {
                    throw RubyExceptions.CreateImplicitConversionError("nil", "String");
                }

                // use case sensitive comparison
                MutableString loadedPath = Protocols.CastToPath(toPath, file);
                if (paths.Any((path) => loadedPath.Equals(path))) {
                    return true;
                }
            }

            lock (_loadedFiles) {
                if (_hiddenLoadedFiles.Count > 0 && paths.Any((path) => _hiddenLoadedFiles.Contains(path.ToString()))) {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region IronRuby Libraries

        /// <exception cref="LoadError"></exception>
        internal void LoadBuiltins() {
            Type initializerType;
            try {
                Assembly assembly = _context.DomainManager.Platform.LoadAssembly(GetIronRubyAssemblyLongName("IronRuby.Libraries"));
                initializerType = assembly.GetType(LibraryInitializer.GetBuiltinsFullTypeName());
            } catch (Exception e) {
                throw RubyExceptions.CreateLoadError(e);
            }

            LoadLibrary(initializerType, true);
        }

        public static string/*!*/ GetIronRubyAssemblyLongName(string/*!*/ baseName) {
            ContractUtils.RequiresNotNull(baseName, "baseName");
            string fullName = typeof(RubyContext).GetTypeInfo().Assembly.FullName;
            int firstComma = fullName.IndexOf(',');
            return firstComma > 0 ? baseName + fullName.Substring(firstComma) : baseName;
        }

        private readonly HashSet<Type> _loadedTypes = new HashSet<Type>();

        /// <exception cref="LoadError"></exception>
        private void LoadLibrary(Type/*!*/ initializerType, bool builtin) {
            lock (_loadedTypes) {
                if (_loadedTypes.Contains(initializerType)) {
                    return;
                }
            }
            
            LibraryInitializer initializer;
            try {
                initializer = Activator.CreateInstance(initializerType) as LibraryInitializer;
            } catch (TargetInvocationException e) {
                throw RubyExceptions.CreateLoadError(e.InnerException);
            } catch (Exception e) {
                throw RubyExceptions.CreateLoadError(e);
            }

            if (initializer == null) {
                throw RubyExceptions.CreateLoadError(String.Format("Specified type {0} is not a subclass of {1}", 
                    initializerType.FullName,
                    typeof(LibraryInitializer).FullName)
                );
            }

            // Propagate exceptions from initializers (do not wrap them to LoadError).
            // E.g. TypeError (can't modify frozen module) can be thrown.
            initializer.LoadModules(_context, builtin);

            lock (_loadedTypes) {
                _loadedTypes.Add(initializerType);
            }
        }

        #endregion
    }
}

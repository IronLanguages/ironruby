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
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Runtime.Calls;
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

        internal enum FileKind {
            RubySourceFile,
            NonRubySourceFile,
            Assembly,
            Type,
            Unknown,
        }

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

#if FEATURE_FILESYSTEM
        // TODO: static
        // maps full normalized path to compiled code:
        private Dictionary<string, CompiledFile> _compiledFiles;
        private readonly object/*!*/ _compiledFileMutex = new object();

        private struct CompiledFile {
            public readonly ScriptCode/*!*/ CompiledCode;

            public CompiledFile(ScriptCode/*!*/ compiledCode) {
                Assert.NotNull(compiledCode);

                CompiledCode = compiledCode;
            }
        }

        // counters:
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        private int _cacheHitCount;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        private int _compiledFileCount;
#endif

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

#if FEATURE_ASSEMBLY_RESOLVE
            if (!context.RubyOptions.NoAssemblyResolveHook) {
                new AssemblyResolveHolder(this).HookAssemblyResolve();
            }
#endif
        }

        private RubyArray/*!*/ MakeLoadPaths(RubyOptions/*!*/ options) {
            var loadPaths = new RubyArray();
            
            if (options.HasSearchPaths) {
                foreach (string path in options.SearchPaths) {
                    loadPaths.Add(_context.EncodePath(path));
                }
            }

            AddStandardLibraryPath(loadPaths, options.StandardLibraryPath, options.ApplicationBase);
            // TODO: remove?
            loadPaths.Add(MutableString.CreateAscii("."));
            return loadPaths;
        }

        private void AddStandardLibraryPath(RubyArray/*!*/ loadPaths, string path, string applicationBaseDir) {
#if FEATURE_FILESYSTEM
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
                try {
                    if (String.IsNullOrEmpty(applicationBaseDir)) {
                        applicationBaseDir = _context.Platform.GetEnvironmentVariable(RubyContext.BinDirEnvironmentVariable);
                        if (!Directory.Exists(applicationBaseDir)) {
                            applicationBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                        }
                    }
                } catch (SecurityException) {
                    applicationBaseDir = null;
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
            loadPaths.Add(_context.EncodePath(RubyUtils.CombinePaths(path, "ironruby")));
            string rubyLib4 = RubyUtils.CombinePaths(path, "ruby/4.0");
            if (Directory.Exists(rubyLib4)) {
                loadPaths.Add(_context.EncodePath(rubyLib4));
            }
            loadPaths.Add(_context.EncodePath(RubyUtils.CombinePaths(path, "ruby/" + _context.StandardLibraryVersion)));

            for (int i = firstDefault; i < loadPaths.Count; i++) {
                var entry = (MutableString)loadPaths[i];
                _context.SetInstanceVariable(entry, "@gem_prelude_index", entry);
                entry.Freeze();
            }
#endif
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

        private void AddAbsoluteLibraryPaths(RubyArray/*!*/ result, string applicationBaseDir, ICollection<string>/*!*/ paths) {
            foreach (var path in paths) {
                string fullPath;
                if (applicationBaseDir != null) {
                    try {
                        fullPath = Platform.IsAbsolutePath(path) ? path : Platform.GetFullPath(Path.Combine(applicationBaseDir, path));
                    } catch (Exception) {
                        // error will be reported on first require:
                        fullPath = path;
                    }
                } else {
                    fullPath = path;
                }
                result.Add(_context.EncodePath(fullPath.Replace('\\', '/')));
            }
        }

#if FEATURE_FILESYSTEM
        private Dictionary<string, CompiledFile>/*!*/ LoadCompiledCode() {
            Debug.Assert(_context.RubyOptions.LoadFromDisk);

            Dictionary<string, CompiledFile> result = new Dictionary<string, CompiledFile>();
            Utils.Log("LOADING", "LOADER");

            ScriptCode[] codes = SavableScriptCode.LoadFromAssembly(_context.DomainManager,
                Assembly.Load(Path.GetFileName(_context.RubyOptions.MainFile))
            );

            for (int i = 0; i < codes.Length; i++) {
                string path = codes[i].SourceUnit.Path;
                string fullPath = Platform.GetFullPath(path);
                result[fullPath] = new CompiledFile(codes[i]);
            }

            return result;
        }

        internal void SaveCompiledCode() {
            string savePath = _context.RubyOptions.SavePath;
            if (savePath != null) {
                lock (_compiledFileMutex) {
                    var assemblyPath = Path.Combine(savePath, (Path.GetFileName(_context.RubyOptions.MainFile) ?? "snippets") + ".dll");

                    Utils.Log(String.Format("SAVING to {0}", Path.GetFullPath(assemblyPath)), "LOADER");

                    // TODO: allocate eagerly (as soon as config gets fixed)
                    if (_compiledFiles == null) {
                        _compiledFiles = new Dictionary<string, CompiledFile>();
                    }

                    SavableScriptCode[] codes = new SavableScriptCode[_compiledFiles.Count];
                    int i = 0;
                    foreach (CompiledFile file in _compiledFiles.Values) {
                        codes[i++] = (SavableScriptCode)file.CompiledCode;
                    }

                    SavableScriptCode.SaveToAssembly(assemblyPath, codes);
                }
            }
        }

        // Files compiled ahead of time into an assembly (Util/aot) that the host registered.
        private bool _hasPrecompiledFiles;

        /// <summary>
        /// Makes require/load of <paramref name="fullPath"/> run <paramref name="code"/> instead of
        /// compiling the file's source. The file is still resolved on the load path as usual.
        /// </summary>
        internal void RegisterPrecompiledFile(string/*!*/ fullPath, ScriptCode/*!*/ code) {
            lock (_compiledFileMutex) {
                if (_compiledFiles == null) {
                    _compiledFiles = new Dictionary<string, CompiledFile>();
                }
                _compiledFiles[Platform.GetFullPath(fullPath)] = new CompiledFile(code);
                _hasPrecompiledFiles = true;
            }
        }

        private bool TryGetCompiledFile(string/*!*/ fullPath, out CompiledFile compiledFile) {
            if (!_context.RubyOptions.LoadFromDisk && !_hasPrecompiledFiles) {
                compiledFile = default(CompiledFile);
                return false;
            }

            lock (_compiledFileMutex) {
                if (_compiledFiles == null) {
                    _compiledFiles = LoadCompiledCode();
                }

                return _compiledFiles.TryGetValue(fullPath, out compiledFile);
            }
        }

        private void AddCompiledFile(string/*!*/ fullPath, ScriptCode/*!*/ compiledCode) {
            if (_context.RubyOptions.SavePath != null) {
                lock (_compiledFileMutex) {
                    // TODO: allocate eagerly (as soon as config gets fixed)
                    if (_compiledFiles == null) {
                        _compiledFiles = new Dictionary<string, CompiledFile>();
                    }
                    _compiledFiles[fullPath] = new CompiledFile(compiledCode);
                }
            }
        }
#endif
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

            string strPath = path.ConvertToString();
            if (TryParseAssemblyName(strPath, out typeName, out assemblyName)) {

                if (AlreadyLoaded(strPath, (string)null, flags)) {
                    loaded = ((flags & LoadFlags.ResolveLoaded) != 0) ? GetAssembly(assemblyName, true, false) : null;
                    return false;
                }

                Assembly assembly = LoadAssembly(assemblyName, typeName, false, false);
                if (assembly != null) {
                    FileLoaded(path.Clone(), flags);
                    loaded = assembly;
                    return true;
                }
            }

            return LoadFromPath(globalScope, self, strPath, path.Encoding, flags, out loaded);
        }

        #region Assemblies

        public Assembly LoadAssembly(string/*!*/ assemblyName, string typeName, bool throwOnError, bool tryPartialName) {
            Assembly assembly = GetAssembly(assemblyName, throwOnError, tryPartialName);
            return (assembly != null && LoadAssembly(assembly, typeName, throwOnError)) ? assembly : null;
        }
        
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods")]
        private Assembly GetAssembly(string/*!*/ assemblyName, bool throwOnError, bool tryPartialName) {
            try {
                return Platform.LoadAssembly(assemblyName);
            } catch (Exception e) {
                if (!tryPartialName || !(e is FileNotFoundException)) {
                    if (throwOnError) {
                        throw RubyExceptions.CreateLoadError(e);
                    } else {
                        return null;
                    }
                }
            }

#pragma warning disable 618,612 // csc, gmcs
            Assembly assembly;
            try { 
                assembly = Assembly.LoadWithPartialName(assemblyName);
            } catch (Exception e) {
                if (throwOnError) {
                    throw RubyExceptions.CreateLoadError(e);
                } else {
                    return null;
                }
            }
            if (assembly == null && throwOnError) {
                throw RubyExceptions.CreateLoadError(String.Format("Assembly '{0}' not found", assemblyName));
            }
#pragma warning restore 618,612
            return assembly;
        }
        
        private bool LoadAssembly(Assembly/*!*/ assembly, string typeName, bool throwOnError) {
            Utils.Log(String.Format("Loading assembly '{0}' and type '{1}'", assembly, typeName), "LOADER");
            Type initializerType;
            if (typeName != null) {
                // load Ruby library:
                initializerType = assembly.GetType(typeName);
                if (initializerType == null) {
                    if (throwOnError) {
                        throw new TypeLoadException(
                            String.Format("Unhandled Exception: System.TypeLoadException: Could not load type '{0}' from assembly '{1}'.", 
                                typeName, assembly.FullName)
                        );
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
            Match match = (path.IndexOf('=') >= 0) ? AssemblyNameRegex.Match(path) : null;
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

#if FEATURE_ASSEMBLY_RESOLVE
        private sealed class AssemblyResolveHolder {
            private readonly WeakReference _loader;

            [ThreadStatic]
            private static HashSet<string> _assembliesBeingResolved;

            public AssemblyResolveHolder(Loader/*!*/ loader) {
                _loader = new WeakReference(loader);
            }

            internal void HookAssemblyResolve() {
                try {
                    HookAssemblyResolveInternal();
                } catch (System.Security.SecurityException) {
                    // We may not have SecurityPermissionFlag.ControlAppDomain. 
                }
            }

            private void HookAssemblyResolveInternal() {
                AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolveEvent;
            }

            private Assembly AssemblyResolveEvent(object sender, ResolveEventArgs args) {
                Loader loader = (Loader)_loader.Target;
                if (loader != null) {
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
                        return loader.ResolveAssembly(assemblyName);
                    } catch (Exception e) {
                        // the exception might not be reported by the type loader, so at least report a warning:
                        loader._context.ReportWarning(
                            String.Format("An exception was risen while resolving an assembly `{0}': {1}", assemblyName, e.Message)
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

        internal Assembly ResolveAssembly(string/*!*/ fullName) {
            Utils.Log(String.Format("Resolving assembly: '{0}'", fullName), "RESOLVE_ASSEMBLY");
            
            AssemblyName assemblyName = new AssemblyName(fullName);
            ResolvedFile file = FindFile(assemblyName.Name, true, ArrayUtils.EmptyStrings, false).FirstOrDefault();
            if (file == null || file.SourceUnit != null) {
                return null;
            }

            Utils.Log(String.Format("Assembly '{0}' resolved: found in '{1}'", fullName, file.Path), "RESOLVE_ASSEMBLY");
            try {
                Assembly assembly = Platform.LoadAssemblyFromPath(file.Path);

                // TODO: this API is broken
                if (AssemblyName.ReferenceMatchesDefinition(assemblyName, assembly.GetName())) 
                {
                    Utils.Log(String.Format("Assembly '{0}' loaded for '{1}'", assembly.FullName, fullName), "RESOLVE_ASSEMBLY");
                    DomainManager.LoadAssembly(assembly);
                    return assembly;
                }
            } catch (Exception e) {
                throw RubyExceptions.CreateLoadError(e);
            }

            return null;
        }

#endif
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
                if (AlreadyLoaded(path, null, flags, sourceFileExtensions)) {
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

                    // MRI warns under -w. Only for a require the program made: Kernel#Complex and
                    // #Rational are autoloaded constants pointing at complex18.rb and rational18.rb,
                    // and those files reopen the very class whose autoload is running - a circularity
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

                    ScriptCode compiledCode;
                    if (file.SourceUnit.LanguageContext == _context) {
                        compiledCode = CompileRubySource(file.SourceUnit, flags);
                    } else {
                        compiledCode = file.SourceUnit.Compile();
                    }
                    loaded = Execute(globalScope, compiledCode);
                } else {
                    Debug.Assert(file.Path != null);
                    try {
                        Assembly assembly = Platform.LoadAssemblyFromPath(file.Path);
                        DomainManager.LoadAssembly(assembly);
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

                FileLoaded(MutableString.Create(file.Path, pathEncoding), flags);
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
            
            // TODO: check file timestamp
            string fullPath = Platform.GetFullPath(sourceUnit.Path);
            _context.RegisterSourceFileLocation(sourceUnit.Path);

#if FEATURE_FILESYSTEM
            CompiledFile compiledFile;
            if (TryGetCompiledFile(fullPath, out compiledFile)) {
                Utils.Log(String.Format("{0}: {1}", ++_cacheHitCount, sourceUnit.Path), "LOAD_CACHED");

                return compiledFile.CompiledCode;
            } 

            Utils.Log(String.Format("{0}: {1}", ++_compiledFileCount, sourceUnit.Path), "LOAD_COMPILED");
#endif

            RubyCompilerOptions options = new RubyCompilerOptions(_context.RubyOptions) {
                FactoryKind = (flags & LoadFlags.LoadIsolated) != 0 ? TopScopeFactoryKind.WrappedFile : TopScopeFactoryKind.File
            };

            ScriptCode compiledCode = sourceUnit.Compile(options, _context.RuntimeErrorSink);

#if FEATURE_FILESYSTEM
            AddCompiledFile(fullPath, compiledCode);
#endif
            return compiledCode;
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
                if (file != null) {
                    result.Add(file);
                }
            }

            if (result.Count == 0) {
                return CurrentDirectoryFallback(path, extension, appendExtensions, sourceFileExtensions, searchCurrentDirectory);
            }

            // MRI tries each extension over the whole $LOAD_PATH before the next one, so for a
            // bare name a .rb file anywhere wins over a library found in an earlier directory.
            if (appendExtensions && extension.Length == 0 && result.Count > 1 && result[0].SourceUnit == null) {
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

                result[i] = Protocols.CastToPath(toPath, loadPaths[i]).ConvertToString();
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
            
            if (_LibraryExtensions.IndexOf(extension, DlrConfiguration.FileExtensionComparer) != -1) {
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
            return extension.Length > 0 && knownExtensions.IndexOf(extension, DlrConfiguration.FileExtensionComparer) >= 0;
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
            return new ResolvedFile(sourceUnit, fullPath, extensionAppended ? extension : null);
        }

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
            lock (_loadedFiles) {
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

        internal void AddLoadPaths(IEnumerable<string/*!*/>/*!*/ paths) {
            Assert.NotNullItems(paths);

            lock (_loadPaths) {
                foreach (string path in paths) {
                    _loadPaths.Add(_context.EncodePath(path));
                }
            }
        }

        internal void InsertLoadPaths(IEnumerable<string/*!*/>/*!*/ paths, int index) {
            Assert.NotNullItems(paths);

            lock (_loadPaths) {
                foreach (string path in paths) {
                    _loadPaths.Insert(0, _context.EncodePath(path));
                }
            }
        }

        internal void InsertLoadPaths(IEnumerable<string/*!*/>/*!*/ paths) {
            InsertLoadPaths(paths, 0);
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
                if (ResolveFeature(autoload.Path.ConvertToString()) == fullPath) {
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

        // The thread running ruby4.rb as the host's core prelude (--disable-gems), while it does. The
        // only Ruby code on its stack then is the prelude's own, which backtraces leave out.
        private Thread _corePreludeThread;

        /// <summary>
        /// True while this thread runs ruby4.rb as the core prelude, loaded by the host itself: a
        /// backtrace taken now has no frame in it (they are all the prelude's, which backtraces
        /// skip), so a caller that only wants the innermost location need not walk the CLR stack.
        /// </summary>
        internal bool IsLoadingCorePrelude {
            get { return _corePreludeThread == Thread.CurrentThread; }
        }

        /// <summary>
        /// Requires the core prelude - gem_prelude.rb, or ruby4.rb under --disable-gems - and then
        /// lists the features MRI has in $" from the start.
        /// </summary>
        internal void LoadCorePrelude(Scope/*!*/ globalScope, object self, string/*!*/ path) {
            bool direct = path == "ruby4.rb";
            if (direct) {
                _corePreludeThread = Thread.CurrentThread;
            }
            try {
                LoadFile(globalScope, self, MutableString.Create(path, RubyEncoding.UTF8), LoadFlags.Require);
            } finally {
                if (direct) {
                    _corePreludeThread = null;
                }
            }
            ProvideCoreFeatures();
        }

        // Features the core prelude loaded: kept out of $" (MRI's own Ruby-defined core is not in
        // it either) but still loaded, so that requiring one again does nothing.
        private readonly HashSet<string>/*!*/ _hiddenLoadedFiles = new HashSet<string>(StringComparer.Ordinal);

        private static readonly string[]/*!*/ _CorePreludeFiles = {
            "gem_prelude.rb", "ruby4.rb", "argf.rb", "thread.rb", "complex18.rb", "rational18.rb",
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

        private bool AlreadyLoaded(string/*!*/ path, string fullPath, LoadFlags flags) {
            return AlreadyLoaded(path, fullPath, flags, ArrayUtils.EmptyStrings);
        }
        
        private bool AlreadyLoaded(string/*!*/ path, string fullPath, LoadFlags flags, string[]/*!*/ sourceFileExtensions) {
            Debug.Assert(fullPath == null || RubyUtils.GetExtension(path) == RubyUtils.GetExtension(fullPath));
            return (flags & LoadFlags.LoadOnce) != 0 && AnyFileLoaded(GetPathsToTestLoaded(path, fullPath, flags, sourceFileExtensions));
        }

        private IEnumerable<MutableString>/*!*/ GetPathsToTestLoaded(string/*!*/ path, string fullPath, LoadFlags flags, string[]/*!*/ sourceFileExtensions) {
            List<MutableString> paths = new List<MutableString>();
            paths.Add(_context.EncodePath(path));

            if (fullPath != null) {
                paths.Add(_context.EncodePath(path));
            }

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
            // a require of that name, with or without the extension.
            IEnumerable<MutableString> requested;
            if (RubyUtils.GetExtension(path).Length != 0) {
                requested = new[] { _context.EncodePath(path) };
            } else if (!Platform.IsAbsolutePath(path) && !path.StartsWith(".", StringComparison.Ordinal)) {
                requested = new[] { _context.EncodePath(path + ".rb"), _context.EncodePath(path + ".so") };
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

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

using MSA = System.Linq.Expressions;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;

namespace IronRuby.Runtime.Precompiled {
    /// <summary>
    /// Marks the class of a precompiled startup library (Util/aot: IronRuby.Aot startup): the top
    /// level of each source file compiled to a static method, with the hash of the source it was
    /// compiled from.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class), CLSCompliant(false)]
    public sealed class PrecompiledFilesAttribute : Attribute {
        public PrecompiledFilesAttribute(string[] files, string[] entryPoints, string[] hashes, string[] assemblies,
            string root, string rootFromLibrary, bool relocatable) {
            Files = files;
            EntryPoints = entryPoints;
            Hashes = hashes;
            Assemblies = assemblies;
            Root = root;
            RootFromLibrary = rootFromLibrary;
            Relocatable = relocatable;
        }

        /// <summary>
        /// The IronRuby tree the files were compiled from, and where it is from the library's directory: a copy of the
        /// tree elsewhere (an unpacked release) finds its own root from where its library is.
        /// </summary>
        public string Root { get; }
        public string RootFromLibrary { get; }

        /// <summary>
        /// Every path under <see cref="Root"/> the code holds goes through <see cref="PrecompiledCode.Rooted"/>, so the
        /// library may run from a copy of the tree elsewhere. If not, it is only used where it was compiled.
        /// </summary>
        public bool Relocatable { get; }

        /// <summary>
        /// "name=mvid" of each assembly the library was compiled against (IronRuby and the DLR): the saved
        /// IL calls into them and binds their binders by name, so a rebuild of any of them makes the library stale.
        /// </summary>
        public string[] Assemblies { get; }

        /// <summary>The files compiled in, relative to <see cref="Root"/>.</summary>
        public string[] Files { get; }
        /// <summary>The static method each file's top level was compiled to.</summary>
        public string[] EntryPoints { get; }
        /// <summary>The source each was compiled from (<see cref="PrecompiledCode.HashFile"/>).</summary>
        public string[] Hashes { get; }
    }

    /// <summary>
    /// The closure object nested lambdas of saved code are bound to: .NET Core does not expose
    /// System.Runtime.CompilerServices.Closure, and a saved assembly must reference a public type.
    /// </summary>
    public sealed class Closure {
        public readonly object[] Constants;
        public readonly object[] Locals;

        public Closure(object[] constants, object[] locals) {
            Constants = constants;
            Locals = locals;
        }
    }

    public static class RuntimeOps {
        public static System.Runtime.CompilerServices.IRuntimeVariables CreateRuntimeVariables() {
            throw new NotSupportedException("RuntimeVariablesExpression is not supported in precompiled code");
        }

        public static System.Runtime.CompilerServices.IRuntimeVariables CreateRuntimeVariables(object[] data, long[] indexes) {
            throw new NotSupportedException("RuntimeVariablesExpression is not supported in precompiled code");
        }

        public static MSA.Expression Quote(MSA.Expression expression, object hoistedLocals, object[] locals) {
            throw new NotSupportedException("Quote is not supported in precompiled code");
        }
    }

    /// <summary>
    /// What Ruby code compiled ahead of time into a saved assembly (Util/aot) calls to re-create the
    /// live objects the compiler embeds in a tree (symbols, method bodies, block dispatchers, call
    /// site binders). The saved code keeps them in static fields, so it is bound to one runtime per
    /// process: <see cref="Context"/>.
    /// </summary>
    public static class PrecompiledCode {
        private static RubyContext _context;

        /// <summary>The runtime the static fields of precompiled code belong to.</summary>
        public static RubyContext Context {
            get { return _context; }
            set { _context = value; }
        }

        #region Constants the saved code re-creates on first use

        public static RubySymbol/*!*/ Symbol(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            return _context.CreateSymbol(bytes, encoding);
        }

        public static MutableString/*!*/ FrozenString(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            return MutableString.CreateBinary(bytes, encoding).Freeze();
        }

        /// <summary>
        /// A frozen string the compiler made from text (the literal parts of an interpolated string): with the same
        /// content as the compiler's, characters. Made from bytes, every interpolation appending it to its characters
        /// would convert one of the two (Fiber.new: 3x the time of its "#{file}:#{line}").
        /// </summary>
        public static MutableString/*!*/ FrozenString(string/*!*/ text, RubyEncoding/*!*/ encoding) {
            return MutableString.Create(text, encoding).Freeze();
        }

        public static BlockDispatcher/*!*/ CreateBlockDispatcher(int parameterCount, int postCount, int attributesAndArity,
            string sourcePath, int sourceLine, RubyParameterSignature signature) {
            var result = BlockDispatcher.Create(parameterCount, postCount, (BlockSignatureAttributes)attributesAndArity, sourcePath, sourceLine);
            result.ParameterSignature = signature;
            return result;
        }

        /// <summary>
        /// A block that uses locals of the scopes around it: Ractor.new needs to know which
        /// (BlockDispatcher.OuterVariableNames).
        /// </summary>
        public static BlockDispatcher/*!*/ CreateBlockDispatcher(int parameterCount, int postCount, int attributesAndArity,
            string sourcePath, int sourceLine, RubyParameterSignature signature, string[] outerVariables, string[] assignedOuterVariables) {
            var result = CreateBlockDispatcher(parameterCount, postCount, attributesAndArity, sourcePath, sourceLine, signature);
            result.OuterVariableNames = outerVariables;
            result.AssignedOuterVariableNames = assignedOuterVariables;
            return result;
        }

        /// <summary>
        /// As above, with the outer variables the scope assigns more than once
        /// (BlockDispatcher.ReassignedOuterVariableNames), which a shareable proc may not read.
        /// </summary>
        public static BlockDispatcher/*!*/ CreateBlockDispatcher(int parameterCount, int postCount, int attributesAndArity,
            string sourcePath, int sourceLine, RubyParameterSignature signature, string[] outerVariables, string[] assignedOuterVariables,
            string[] reassignedOuterVariables) {
            var result = CreateBlockDispatcher(parameterCount, postCount, attributesAndArity, sourcePath, sourceLine, signature,
                outerVariables, assignedOuterVariables);
            result.ReassignedOuterVariableNames = reassignedOuterVariables;
            return result;
        }

        public static RubyParameterSignature/*!*/ Signature(string[]/*!*/ kinds, string[]/*!*/ names, int leadingCount, int optionalCount,
            int postCount, bool hasRest, int requiredKeywordCount, bool hasKeywords, bool hasKeywordRest, string[] implicitParameterNames) {
            var ps = new RubyParameterSignature.Parameter[kinds.Length];
            for (int i = 0; i < ps.Length; i++) {
                ps[i] = new RubyParameterSignature.Parameter(kinds[i], names[i]);
            }
            return new RubyParameterSignature(ps, leadingCount, optionalCount, postCount, hasRest, requiredKeywordCount, hasKeywords,
                hasKeywordRest) {
                ImplicitParameterNames = implicitParameterNames
            };
        }

        private static SourceSpan Span(int[]/*!*/ s) {
            return new SourceSpan(new SourceLocation(s[0], s[1], s[2]), new SourceLocation(s[3], s[4], s[5]));
        }

        /// <summary>
        /// The header of a `def': the AST the runtime consults for the name, parameters, arity and
        /// location, without a body - that is compiled IL, reached through the body's factory.
        /// </summary>
        public static MethodDefinition/*!*/ MethodHeader(string/*!*/ name, bool hasTarget, bool usesBlock, int[]/*!*/ location,
            string[]/*!*/ mandatory, int leadingMandatoryCount, string[]/*!*/ optional, string unsplat, string block,
            RubyParameterSignature signature) {
            var span = Span(location);
            LeftValue[] m = mandatory.Select(n => (LeftValue)new LocalVariable(n, span, 0)).ToArray();
            SimpleAssignmentExpression[] o = optional.Select(n =>
                new SimpleAssignmentExpression(new LocalVariable(n, span, 0), Literal.Nil(span), null, span)).ToArray();
            var parameters = new Parameters(m, leadingMandatoryCount, o,
                unsplat != null ? new LocalVariable(unsplat, span, 0) : null,
                block != null ? new LocalVariable(block, span, 0) : null, span) {
                Signature = signature
            };
            var body = new Body(new Statements(), null, null, null, span);
            return new MethodDefinition(new MethodLexicalScope(new TopStaticLexicalScope(null)), hasTarget ? new SelfReference(span) : null,
                name, parameters, body, span) {
                UsesBlock = usesBlock
            };
        }

        public static RubyMethodBody/*!*/ CreateMethodBody(MethodDefinition/*!*/ header, string documentPath, RubyEncoding/*!*/ encoding,
            Func<RubyScope, RubyModule, Delegate>/*!*/ factory) {
            return CreateMethodBody(header, documentPath, encoding, factory, null);
        }

        /// <summary>
        /// <paramref name="jitSource"/>: the def's source, kept for a method the JIT may specialize (JitStub.IsCandidate):
        /// the JIT works from the method's AST, and the header has no body. Parsed when the method is first called.
        /// </summary>
        public static RubyMethodBody/*!*/ CreateMethodBody(MethodDefinition/*!*/ header, string documentPath, RubyEncoding/*!*/ encoding,
            Func<RubyScope, RubyModule, Delegate>/*!*/ factory, string jitSource) {
            return CreateMethodBody(header, documentPath, encoding, factory, jitSource, null);
        }

        /// <summary>
        /// <paramref name="label"/>: the label the compiled body's frames carry, which the compiler worked out from the
        /// lexical nesting. If the runtime gives the method another (RubyMethodBody.Compile), the def is compiled from its
        /// source instead (<see cref="ParseDefinition"/>).
        /// </summary>
        public static RubyMethodBody/*!*/ CreateMethodBody(MethodDefinition/*!*/ header, string documentPath, RubyEncoding/*!*/ encoding,
            Func<RubyScope, RubyModule, Delegate>/*!*/ factory, string jitSource, string label) {
            var document = documentPath != null ? MSA.Expression.SymbolDocument(documentPath) : null;
            return new RubyMethodBody(header, document, encoding, factory, jitSource, label);
        }

        /// <summary>
        /// The `def' a precompiled method was compiled from, parsed from the source the library keeps for it
        /// (CreateMethodBody), for the JIT: the same parser on the same text, starting at the def's line, gives the AST
        /// the compiler screened. Null if it does not (the method then runs as compiled, unspecialized).
        /// </summary>
        internal static MethodDefinition ParseJitSource(RubyContext/*!*/ context, string/*!*/ source, MethodDefinition/*!*/ header, string path) {
            if (Trace) {
                Console.Error.WriteLine("IronRuby.Startup: the JIT parses `def " + header.Name + "' (" + path + ":" + header.Location.Start.Line + ")");
            }
            return ParseMethod(context, null, source, header, path);
        }

        // magicComments: lines put before the def, which the def's first line is moved up by
        private static MethodDefinition ParseMethod(RubyContext/*!*/ context, string magicComments, string/*!*/ source,
            MethodDefinition/*!*/ header, string path) {
            int line = header.Location.Start.Line;
            if (magicComments != null) {
                foreach (char c in magicComments) {
                    if (c == '\n') {
                        line--;
                    }
                }
                source = magicComments + source;
            }
            try {
                var unit = context.CreateSnippet(source, path, SourceCodeKind.File);
                var options = new RubyCompilerOptions() {
                    FactoryKind = TopScopeFactoryKind.File,
                    InitialLocation = new SourceLocation(0, line, 1)
                };
                var tree = RubyContext.EffectiveParser(unit, options, ErrorSink.Null);
                var def = tree != null ? tree.Statements.FirstOrDefault() as MethodDefinition : null;
                if (def == null || def.Name != header.Name || def.Parameters.Mandatory.Length != header.Parameters.Mandatory.Length ||
                    def.Parameters.Optional.Length != header.Parameters.Optional.Length) {
                    return null;
                }
                return def;
            } catch (Exception) {
                return null;
            }
        }

        // the file whose source ParseDefinition read last, as prism was given it, and its magic comments
        private static Tuple<string, byte[], string> _lastSource;

        /// <summary>
        /// The lines of the file's leading comments that set frozen_string_literal (or a lone def parsed from the file would
        /// get mutable string literals where the file has frozen ones), each with its "\n"; null if there are none. The
        /// encoding comment is left out: the def is UTF-8, as a file without one is.
        /// </summary>
        internal static string MagicComments(byte[]/*!*/ code) {
            System.Text.StringBuilder result = null;
            int pos = 0;
            while (pos < code.Length) {
                int end = Array.IndexOf(code, (byte)'\n', pos);
                if (end < 0) {
                    end = code.Length;
                }
                int first = pos;
                while (first < end && (code[first] == ' ' || code[first] == '\t' || code[first] == '\r')) {
                    first++;
                }
                if (first < end && code[first] != '#') {
                    break;
                }
                if (first < end) {
                    string text = System.Text.Encoding.UTF8.GetString(code, first, end - first).TrimEnd('\r');
                    if (text.IndexOf("frozen", StringComparison.OrdinalIgnoreCase) >= 0) {
                        (result ?? (result = new System.Text.StringBuilder())).Append(text).Append('\n');
                    }
                }
                pos = end + 1;
            }
            return result != null ? result.ToString() : null;
        }

        /// <summary>
        /// A precompiled `def' parsed from its file, for a method the library's compiled body does not fit (see
        /// CreateMethodBody's label): only while the file is still the source the library was compiled from, and only for
        /// UTF-8 source, whose bytes the def's location indexes. Null if not.
        /// </summary>
        internal static MethodDefinition ParseDefinition(RubyContext/*!*/ context, MethodDefinition/*!*/ header, RubyEncoding/*!*/ encoding,
            string path) {
            string hash;
            if (path == null || encoding != RubyEncoding.UTF8 || _fileHashes == null || !_fileHashes.TryGetValue(path, out hash)) {
                return null;
            }
            byte[] code;
            string magicComments;
            var last = _lastSource;
            if (last != null && last.Item1 == path) {
                code = last.Item2;
                magicComments = last.Item3;
            } else {
                try {
                    code = File.ReadAllBytes(path);
                } catch (Exception) {
                    return null;
                }
                if (HashBytes(code) != hash) {
                    return null;
                }
                if (code.Length >= 3 && code[0] == 0xEF && code[1] == 0xBB && code[2] == 0xBF) {
                    code = code.AsSpan(3).ToArray();
                }
                magicComments = MagicComments(code);
                _lastSource = Tuple.Create(path, code, magicComments);
            }
            int start = header.Location.Start.Index, end = header.Location.End.Index;
            if (start < 0 || end <= start || end > code.Length) {
                return null;
            }
            string text = System.Text.Encoding.UTF8.GetString(code, start, end - start);
            if (!text.StartsWith("def", StringComparison.Ordinal)) {
                return null;
            }
            if (Trace) {
                Console.Error.WriteLine("IronRuby.Startup: compiles `def " + header.Name + "' (" + path + ":" + header.Location.Start.Line +
                    ") from source: it runs for another module than its compiled frame label says");
            }
            return ParseMethod(context, magicComments, text, header, path);
        }

        #endregion

        #region Startup library

        /// <summary>Stands for the run-time tree's root, and the separator after it, in the paths saved code holds.</summary>
        public const char RootMarker = '\uFDD0';

        // the tree's root as compiled and as run, each with a trailing separator; equal unless the tree was moved
        private static string _compiledRoot, _runtimeRoot;

        /// <summary>A path the saved code holds, with <see cref="RootMarker"/> for the tree's root, in the tree it runs from.</summary>
        public static string/*!*/ Rooted(string/*!*/ path) {
            return path.Replace(RootMarker.ToString(), _runtimeRoot ?? _compiledRoot ?? "");
        }

        /// <summary>
        /// The file of a backtrace frame of the startup library, which its method name encodes as it was compiled,
        /// in the tree it runs from.
        /// </summary>
        internal static void RebaseFrameFile(MethodBase/*!*/ method, ref string file) {
            if (file != null && _startupAssembly != null && !ReferenceEquals(_runtimeRoot, _compiledRoot) &&
                method.DeclaringType != null && method.DeclaringType.Assembly == _startupAssembly) {
                file = file.Replace(_compiledRoot, _runtimeRoot);
            }
        }

        /// <summary>The file of a <see cref="PrecompiledFrames"/> frame, which only the startup library makes.</summary>
        internal static void RebaseFrameFile(ref string file) {
            if (file != null && _startupAssembly != null && !ReferenceEquals(_runtimeRoot, _compiledRoot)) {
                file = file.Replace(_compiledRoot, _runtimeRoot);
            }
        }

        /// <summary>
        /// A hash of the file's bytes, as hex: what the startup library records for each file, so that
        /// a file edited since is parsed again rather than run from stale code.
        /// </summary>
        public static string/*!*/ HashFile(string/*!*/ path) {
            return HashBytes(File.ReadAllBytes(path));
        }

        internal static string/*!*/ HashBytes(byte[]/*!*/ bytes) {
            // FNV-1a over 8-byte words, then the tail bytes: a change detector, not a cryptographic hash
            ulong h = 14695981039346656037UL;
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(new ReadOnlySpan<byte>(bytes));
            for (int i = 0; i < words.Length; i++) {
                h = (h ^ words[i]) * 1099511628211UL;
                h ^= h >> 29;
            }
            for (int i = words.Length * 8; i < bytes.Length; i++) {
                h = (h ^ bytes[i]) * 1099511628211UL;
            }
            return bytes.Length.ToString("x") + "-" + h.ToString("x16");
        }

        private static int _startupClaimed;

        // the library's files, as the run-time tree has them, and the hashes of the sources they were compiled from
        private static System.Collections.Generic.Dictionary<string, string> _fileHashes;

        [ThreadStatic]
        private static int _runDepth;

        /// <summary>
        /// True while this thread runs the top level of a precompiled file. Its frames are compiled IL,
        /// not interpreted frames, so a caller location (const_set, autoload) that precompiled code did not
        /// note (NoteCallLocation) could only come from a CLR stack walk - about 0.2 ms - and is not looked
        /// for: such a call gets no location, as constants set from C do in MRI.
        /// </summary>
        internal static bool IsRunningOnThread {
            get { return _runDepth > 0; }
        }

        internal static object Run(Func<RubyScope, object, object>/*!*/ target, RubyScope/*!*/ scope) {
            _runDepth++;
            try {
                return target(scope, scope.SelfObject);
            } finally {
                _runDepth--;
            }
        }

        // What precompiled code says right before it calls const_set or autoload (see NoteCallLocation).
        [ThreadStatic]
        private static object _callScope;
        [ThreadStatic]
        private static string _callPath;
        [ThreadStatic]
        private static int _callLine;

        /// <summary>
        /// Called by precompiled code right before a call of const_set or autoload, with the scope the call is made
        /// from and where it is: Module#const_set and #autoload record their caller's location, which an interpreted
        /// frame gives for free but compiled code only through a CLR stack walk (~0.2 ms, and rubygems alone makes
        /// some 30 such calls at startup). The method asks with the scope it was called from, which only the noted
        /// call can match (see TryTakeCallLocation). Not emitted in the Ruby half of the core library, whose frames
        /// report their caller's location.
        /// </summary>
        public static void NoteCallLocation(object scope, string path, int line) {
            _callScope = scope;
            _callPath = path;
            _callLine = line;
        }

        /// <summary>
        /// The location precompiled code noted for a call it made from <paramref name="scope"/> (NoteCallLocation),
        /// once: a later call from the same scope that noted nothing (a send) must not take it again.
        /// </summary>
        internal static bool TryTakeCallLocation(RubyScope scope, out string path, out int line) {
            object noted = _callScope;
            if (noted != null) {
                // taken, or left by a noted call that went elsewhere (a receiver's own const_set) and raised before
                // ClearCallLocation: either way no later call may take it
                _callScope = null;
                path = _callPath;
                line = _callLine;
                _callPath = null;
                if (scope != null && ReferenceEquals(noted, scope)) {
                    return path != null;
                }
            }
            path = null;
            line = 0;
            return false;
        }

        /// <summary>
        /// Called by precompiled code right after the call it noted the location of (NoteCallLocation) returns: a call that
        /// did not reach Module#const_set or #autoload leaves nothing behind for a later one, nor keeps the scope alive.
        /// </summary>
        public static void ClearCallLocation() {
            _callScope = null;
            _callPath = null;
        }

        /// <summary>IR_STARTUP_DLL_TRACE=1: say what the startup library registered, and which files are compiled from source.</summary>
        public static readonly bool Trace = Environment.GetEnvironmentVariable("IR_STARTUP_DLL_TRACE") == "1";

        /// <summary>
        /// Registers the files of the precompiled startup library with the loader - IronRuby.Startup.dll next to
        /// IronRuby.dll, or IR_STARTUP_DLL - so that requiring one of them runs its compiled top level instead of
        /// parsing and compiling it. Only for the first runtime of the process (the library's static fields hold
        /// that runtime's objects), only if the runtime compiles code the way the library was compiled, and only
        /// for files whose source is still what was compiled. IR_STARTUP_DLL=none turns it off.
        /// </summary>
        internal static void TryRegisterStartupLibrary(RubyContext/*!*/ context) {
            string path = Environment.GetEnvironmentVariable("IR_STARTUP_DLL");
            if (path == "none" || path == "") {
                return;
            }
            if (path == null) {
                // nothing to load next to a host without assembly files (NativeAOT) or that cannot load code
                string location = typeof(PrecompiledCode).Assembly.Location;
                if (String.IsNullOrEmpty(location) || !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) {
                    return;
                }
                path = Path.Combine(Path.GetDirectoryName(location), "IronRuby.Startup.dll");
                if (!File.Exists(path)) {
                    return;
                }
            }

            var options = context.RubyOptions;
            if (options.EnableTracing || options.Profile || options.DebugFrozenStringLiteral || options.FrozenStringLiteral != 0 ||
                context.Coverage != null || context.DomainManager.Configuration.DebugMode || options.NoAdaptiveCompilation ||
                options.SavePath != null) {
                return;
            }
            if (Interlocked.CompareExchange(ref _startupClaimed, 1, 0) != 0) {
                return;
            }

            bool timed = PhaseTimer.Enter(PhaseTimer.Precompiled);
            var watch = Trace ? Stopwatch.StartNew() : null;
            try {
                Assembly assembly = Assembly.LoadFrom(path);
                Type index = assembly.GetType("RubyStartupIndex", false);
                var info = index != null ? index.GetCustomAttribute<PrecompiledFilesAttribute>() : null;
                if (info == null || !AssembliesMatch(info.Assemblies)) {
                    if (Trace) {
                        Console.Error.WriteLine("IronRuby.Startup: compiled against other assemblies, not used: " + path);
                    }
                    return;
                }
                // the tree it belongs to: where it was compiled, or a copy of that tree it was moved with
                string compiledRoot = info.Root + "/";
                string runtimeRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), info.RootFromLibrary)).TrimEnd('/', '\\') + "/";
                if (runtimeRoot == compiledRoot) {
                    runtimeRoot = compiledRoot;
                } else if (!info.Relocatable) {
                    if (Trace) {
                        Console.Error.WriteLine("IronRuby.Startup: compiled for " + compiledRoot + ", not used in " + runtimeRoot);
                    }
                    return;
                }
                _compiledRoot = compiledRoot;
                _runtimeRoot = runtimeRoot;
                // each file's code is a class of its own (RubyStartup<i>), loaded when the file first runs
                var entry = (Func<int, Func<RubyScope, object, object>>)index.GetMethod("Entry").CreateDelegate(
                    typeof(Func<int, Func<RubyScope, object, object>>));
                _lineTableSource = (Func<string>)index.GetMethod("Lines").CreateDelegate(typeof(Func<string>));
                _startupAssembly = assembly;
                _context = context;
                if (Trace) {
                    Console.Error.WriteLine("IronRuby.Startup: loaded " + path + " in " + watch.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
                }
                var fileHashes = new System.Collections.Generic.Dictionary<string, string>();
                for (int i = 0; i < info.Files.Length; i++) {
                    string file = Path.IsPathRooted(info.Files[i]) ? info.Files[i] : runtimeRoot + info.Files[i], hash = info.Hashes[i];
                    fileHashes[file] = hash;
                    // checked when the file is loaded, not now: most of a library's files may never be
                    Func<bool> isCurrent = () => {
                        bool current;
                        try {
                            current = File.Exists(file) && HashFile(file) == hash;
                        } catch (Exception) {
                            current = false;
                        }
                        if (!current && Trace) {
                            Console.Error.WriteLine("IronRuby.Startup: stale, not used: " + file);
                        }
                        return current;
                    };
                    int fileIndex = i;
                    Func<RubyScope, object, object> target = null;
                    Func<RubyScope, object, object> lazyTarget = (scope, self) => (target ?? (target = entry(fileIndex)))(scope, self);
                    context.Loader.RegisterPrecompiledFile(file, new RubyScriptCode(lazyTarget, context.CreateFileUnit(file), TopScopeFactoryKind.File), isCurrent);
                }
                _fileHashes = fileHashes;
            } catch (Exception e) {
                // a broken or mismatched library only costs the speedup
                if (Trace) {
                    Console.Error.WriteLine("IronRuby.Startup: " + e);
                }
            } finally {
                PhaseTimer.Exit(timed);
                if (Trace) {
                    Console.Error.WriteLine("IronRuby.Startup: registered in " + watch.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
                }
            }
        }

        private static bool AssembliesMatch(string[] assemblies) {
            if (assemblies == null) {
                return false;
            }
            // all of them are loaded by now: the runtime is up and running the prelude
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (string entry in assemblies) {
                int eq = entry.IndexOf('=');
                string name = entry.Substring(0, eq), mvid = entry.Substring(eq + 1);
                bool found = false;
                foreach (var a in loaded) {
                    if (!a.IsDynamic && a.GetName().Name == name) {
                        if (a.ManifestModule.ModuleVersionId.ToString() != mvid) {
                            return false;
                        }
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    return false;
                }
            }
            return true;
        }

        #endregion

        #region Line table

        private static Assembly _startupAssembly;
        private static Func<string> _lineTableSource;
        private static System.Collections.Generic.Dictionary<string, RubyMethodDebugInfo> _lineTable;

        /// <summary>
        /// The IL offset to line map of a method of the startup library (it has no PDB): what the backtrace builder
        /// reads for a compiled frame. Decoded from RubyStartupIndex.Lines() when the first such frame is formatted.
        /// </summary>
        internal static bool TryGetDebugInfo(MethodBase/*!*/ method, out RubyMethodDebugInfo info) {
            info = null;
            if (_startupAssembly == null || method.DeclaringType == null || method.DeclaringType.Assembly != _startupAssembly) {
                return false;
            }
            var table = _lineTable;
            if (table == null) {
                table = new System.Collections.Generic.Dictionary<string, RubyMethodDebugInfo>();
                string text = _lineTableSource();
                int pos = 0;
                while (pos < text.Length) {
                    int nameEnd = text.IndexOf('\u0001', pos);
                    int lineEnd = text.IndexOf('\n', nameEnd);
                    var methodInfo = new RubyMethodDebugInfo();
                    int p = nameEnd + 1;
                    while (p < lineEnd) {
                        int comma = text.IndexOf(',', p);
                        int next = text.IndexOf(',', comma + 1);
                        if (next < 0 || next > lineEnd) {
                            next = lineEnd;
                        }
                        methodInfo.AddMapping(ParseInt(text, p, comma), ParseInt(text, comma + 1, next));
                        p = next + 1;
                    }
                    table[text.Substring(pos, nameEnd - pos)] = methodInfo;
                    pos = lineEnd + 1;
                }
                Interlocked.CompareExchange(ref _lineTable, table, null);
                table = _lineTable;
            }
            return table.TryGetValue(method.Name, out info);
        }

        private static int ParseInt(string/*!*/ text, int start, int end) {
            int result = 0;
            for (int i = start; i < end; i++) {
                result = result * 10 + (text[i] - '0');
            }
            return result;
        }

        #endregion
    }

    /// <summary>
    /// The frames of the startup library's methods and blocks that run a block (a yield, a block parameter) or a loop, for
    /// another thread's backtrace (Thread#backtrace, #backtrace_locations). That is built from the interpreter's frame
    /// chain, as .NET cannot walk another thread's stack, and precompiled code is compiled code from its first call:
    /// without these, Kernel#loop, Set#each or Monitor#synchronize were missing from every thread dump, where they are
    /// much of what a parked worker shows. Other methods are left out: they would pay for a frame at every call, and they
    /// are rarely what a thread is parked or busy in below its own code.
    ///
    /// The methods, blocks and files the runtime compiles once they are hot keep a frame here too, every one of them
    /// (IronRuby.Compiler.Ast.CompiledFrameExpression): a DynamicMethod has no line information at all, so this is where
    /// the line of such a frame comes from in the thread's own backtraces as well.
    ///
    /// The saved code of such a method or block does (AotCompiler.ThreadFrameAdder):
    ///   var frames = PrecompiledFrames.Current; int depth = frames.Enter(name, line);
    ///   try { ... frames.SetLine(depth, line) at each statement ... } finally { frames.Leave(depth); }
    /// where name is the frame's encoded name, the CLR method name the same thread's backtrace decodes. Each frame keeps
    /// the interpreted frame it was entered from, which places it among the interpreted frames.
    /// </summary>
    public sealed class PrecompiledFrames {
        [ThreadStatic]
        private static PrecompiledFrames _current;

        // the frames of each thread that has run any, for the other threads' backtraces
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Thread, PrecompiledFrames> _byThread = new();

        private struct Entry {
            public string Name;
            public int Line;
            public Microsoft.Scripting.Interpreter.InterpretedFrame Below;
        }

        private Entry[] _entries = new Entry[16];
        private int _depth;
        // one past the frame entered last: the frames from _depth up to here have been left since, and keep the
        // line they were left at - an exception's backtrace, built after it left them, reads them (see CaptureLines)
        private int _top;

        // this thread's slot of InterpretedFrame.CurrentFrame, which holds its current interpreted frame: a slot is its
        // thread's for good, and reading it is a field load where the thread-local lookup takes two thread-static reads
        private readonly Microsoft.Scripting.Utils.ThreadLocal<Microsoft.Scripting.Interpreter.InterpretedFrame>.StorageInfo/*!*/ _interpreted;

        private PrecompiledFrames() {
            _interpreted = Microsoft.Scripting.Interpreter.InterpretedFrame.CurrentFrame.GetStorageInfo();
        }

        // The main thread's frames, once it has asked for them while StackLimit knew its stack. Every compiled method and
        // block reads Current at entry (CompiledFrameExpression), and that code is a DynamicMethod, where a thread-static
        // read is not the inline TLS access it is in an ordinary method but a call into the runtime: it made
        // blk_call_in_method 36% and yield_0 25% slower than v4.0.3 (#168). An address in the main thread's stack, which
        // no other thread's stack can share while the main thread lives, needs no lookup - StackLimit's trick.
        private static PrecompiledFrames _main;

        public static unsafe PrecompiledFrames/*!*/ Current {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get {
                byte probe;
                ulong sp = (ulong)&probe;
                var main = _main;
                if (main != null && sp - StackLimit._mainLow < StackLimit._mainSize) {
                    return main;
                }
                return CurrentOffMain(sp);
            }
        }

        // Not inlined: the thread-static read stays in this method, compiled ahead of time or by the tiered JIT, where it
        // is cheap, and out of the DynamicMethods Current is inlined into.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static PrecompiledFrames/*!*/ CurrentOffMain(ulong sp) {
            var result = _current ?? CreateCurrent();
            if (_main == null && sp - StackLimit._mainLow < StackLimit._mainSize) {
                _main = result;
            }
            return result;
        }

        private static PrecompiledFrames/*!*/ CreateCurrent() {
            var result = new PrecompiledFrames();
            _byThread.AddOrUpdate(Thread.CurrentThread, result);
            return _current = result;
        }

        /// <summary>Enters a frame; returns what <see cref="SetLine"/> and <see cref="Leave"/> take.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public int Enter(string/*!*/ name, int line) {
            int depth = _depth;
            var entries = _entries;
            if ((uint)depth >= (uint)entries.Length) {
                entries = Grow();
            }
            ref Entry entry = ref entries[depth];
            // A hot loop calls the same method at the same depth over and over: the slot already has its name, and
            // skipping a store that is a no-op skips its GC write barrier.
            if ((object)entry.Name != name) {
                entry.Name = name;
            }
            entry.Line = line;
            entry.Below = _interpreted.Value;
            _top = depth + 1;
            // written last: another thread reading the frames reads the depth first
            _depth = depth + 1;
            return depth;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void SetLine(int depth, int line) {
            _entries[depth].Line = line;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Leave(int depth) {
            // the interpreted frame is not kept alive by a frame that is gone
            _entries[depth].Below = null;
            _depth = depth;
        }

        /// <summary>
        /// Leaves a frame that returned (<see cref="Leave"/> is for one an exception left): the frames above it are
        /// gone for good, and an exception raised from here on is no longer one that unwound them.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Return(int depth) {
            _entries[depth].Below = null;
            _top = depth;
            _depth = depth;
        }

        /// <summary>
        /// A rescue clause has taken the exception that left the frames above the current one: their lines were for its
        /// backtrace, which has been captured, and the frames are gone for good. An exception raised from here on did
        /// not unwind them, and must not find a stale line under the name of a live frame (a rescued recursive call).
        /// </summary>
        internal static void ForgetLeft() {
            var frames = _current;
            if (frames != null) {
                frames._top = frames._depth;
            }
        }

        /// <summary>
        /// The current thread's innermost frame: where a compiled frame that has just caught an exception finds its own record,
        /// the frames the exception came out of having been left (RubyExceptionData.TryDeferCatchSite).
        /// </summary>
        internal static bool TryGetInnermost(out PrecompiledFrames frames, out int index, out string name) {
            frames = _current;
            index = (frames != null) ? frames._depth - 1 : -1;
            name = (index >= 0) ? frames._entries[index].Name : null;
            return name != null;
        }

        /// <summary>
        /// Whether the frame at <paramref name="index"/> is still the one of that name, and how many frames of the same name have
        /// been entered above it since and are still there.
        /// </summary>
        internal bool TryCountLiveAbove(int index, string/*!*/ name, out int count) {
            count = 0;
            int depth = _depth;
            var entries = _entries;
            if (index >= depth || !String.Equals(entries[index].Name, name, StringComparison.Ordinal)) {
                return false;
            }
            for (int i = index + 1; i < depth; i++) {
                if (String.Equals(entries[i].Name, name, StringComparison.Ordinal)) {
                    count++;
                }
            }
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private Entry[]/*!*/ Grow() {
            // filled before it is published: a reader sees the old array or the complete new one
            var entries = new Entry[_entries.Length * 2];
            Array.Copy(_entries, entries, _entries.Length);
            return _entries = entries;
        }

        internal struct Frame {
            public string Name;
            public int Line;
            public Microsoft.Scripting.Interpreter.InterpretedFrame Below;
        }

        /// <summary>
        /// The current thread's frames, outermost first, for its own backtrace; null if it has none. With
        /// <paramref name="includeLeft"/> also the frames left since the innermost one was entered, which an exception
        /// raised there has just unwound: the frames of the exception's backtrace that it caught nowhere. A frame that
        /// ran Ruby code on its way out (an ensure clause calling a method) may have reused the slot of one of them,
        /// which the backtrace then does not find under its name.
        /// </summary>
        internal static Frame[] CaptureLines(bool includeLeft) {
            var frames = _current;
            if (frames == null) {
                return null;
            }
            int count = includeLeft ? Math.Max(frames._top, frames._depth) : frames._depth;
            if (count == 0) {
                return null;
            }
            var entries = frames._entries;
            var result = new Frame[count];
            for (int i = 0; i < count; i++) {
                result[i].Name = entries[i].Name;
                result[i].Line = entries[i].Line;
            }
            return result;
        }

        /// <summary>
        /// The frames <paramref name="thread"/> is in, innermost first; empty if none. The thread runs on while it is read,
        /// so this is a snapshot that may be slightly off, as the interpreted frames read alongside are.
        /// </summary>
        internal static Frame[]/*!*/ Snapshot(Thread/*!*/ thread) {
            PrecompiledFrames frames;
            if (!_byThread.TryGetValue(thread, out frames)) {
                return Array.Empty<Frame>();
            }
            int depth = frames._depth;
            var entries = frames._entries;
            depth = Math.Min(depth, entries.Length);
            var result = new System.Collections.Generic.List<Frame>(depth);
            for (int i = depth - 1; i >= 0; i--) {
                var entry = entries[i];
                if (entry.Name != null) {
                    result.Add(new Frame { Name = entry.Name, Line = entry.Line, Below = entry.Below });
                }
            }
            return result.ToArray();
        }
    }
}

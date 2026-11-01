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

#if FEATURE_CORE_DLR
using MSA = System.Linq.Expressions;
#else
using MSA = Microsoft.Scripting.Ast;
#endif

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
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class PrecompiledFilesAttribute : Attribute {
        public PrecompiledFilesAttribute(string[] files, string[] entryPoints, string[] hashes) {
            Files = files;
            EntryPoints = entryPoints;
            Hashes = hashes;
        }

        /// <summary>Full paths of the files compiled in.</summary>
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

        public static BlockDispatcher/*!*/ CreateBlockDispatcher(int parameterCount, int postCount, int attributesAndArity,
            string sourcePath, int sourceLine, RubyParameterSignature signature) {
            var result = BlockDispatcher.Create(parameterCount, postCount, (BlockSignatureAttributes)attributesAndArity, sourcePath, sourceLine);
            result.ParameterSignature = signature;
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
            var document = documentPath != null ? MSA.Expression.SymbolDocument(documentPath) : null;
            return new RubyMethodBody(header, document, encoding, factory);
        }

        #endregion

        #region Startup library

        /// <summary>
        /// FNV-1a over the file's bytes, as hex: what the startup library records for each file, so that
        /// a file edited since is parsed again rather than run from stale code.
        /// </summary>
        public static string/*!*/ HashFile(string/*!*/ path) {
            byte[] bytes = File.ReadAllBytes(path);
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < bytes.Length; i++) {
                h = (h ^ bytes[i]) * 1099511628211UL;
            }
            return bytes.Length.ToString("x") + "-" + h.ToString("x16");
        }

        private static int _startupClaimed;

        [ThreadStatic]
        private static int _runDepth;

        /// <summary>
        /// True while this thread runs the top level of a precompiled file. Its frames are compiled IL,
        /// not interpreted frames, so a caller location (const_set, autoload) could only come from a
        /// CLR stack walk - about 0.3 ms, for an answer without a line - and the prelude makes hundreds
        /// of such calls. They get no location instead, as constants set from C do in MRI.
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
                path = Path.Combine(Path.GetDirectoryName(typeof(PrecompiledCode).Assembly.Location) ?? "", "IronRuby.Startup.dll");
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
            try {
                Assembly assembly = Assembly.LoadFrom(path);
                Type program = assembly.GetType("RubyStartup", false);
                var info = program != null ? program.GetCustomAttribute<PrecompiledFilesAttribute>() : null;
                if (info == null) {
                    return;
                }
                _context = context;
                for (int i = 0; i < info.Files.Length; i++) {
                    string file = info.Files[i];
                    try {
                        if (!File.Exists(file) || HashFile(file) != info.Hashes[i]) {
                            if (Trace) {
                                Console.Error.WriteLine("IronRuby.Startup: stale, not used: " + file);
                            }
                            continue;
                        }
                    } catch (Exception) {
                        continue;
                    }
                    var target = (Func<RubyScope, object, object>)program.GetMethod(info.EntryPoints[i], BindingFlags.Public | BindingFlags.Static)
                        .CreateDelegate(typeof(Func<RubyScope, object, object>));
                    context.Loader.RegisterPrecompiledFile(file, new RubyScriptCode(target, context.CreateFileUnit(file), TopScopeFactoryKind.File));
                }
            } catch (Exception e) {
                // a broken or mismatched library only costs the speedup
                if (Trace) {
                    Console.Error.WriteLine("IronRuby.Startup: " + e);
                }
            } finally {
                PhaseTimer.Exit(timed);
            }
        }

        #endregion
    }
}

// Ruby -> a saved .NET assembly whose IL is the compiled Ruby.
//
//   IronRuby.Aot compile -o out/app.dll main.rb [lib.rb ...] [--prelude]
//
// 1. Each file is parsed and transformed to IronRuby's DLR expression tree, exactly as `ir`
//    would; every `def' body (which IronRuby otherwise compiles lazily from the AST at the first
//    call) is transformed too, with its declaring scope and module as lambda parameters.
// 2. A rewriter removes what a saved assembly cannot hold: every live object the tree embeds
//    becomes a static field initialized at load time from an expression that re-creates it, and
//    every dynamic call site becomes a static CallSite<T> field whose binder is re-created.
// 3. The ported System.Core LambdaCompiler (ExpressionCompiler/) compiles the rewritten trees
//    into static methods of a PersistedAssemblyBuilder type, which is saved with an entry point.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using IronRuby.Aot.Runtime;
using IronRuby.Runtime.Precompiled;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using Expression = System.Linq.Expressions.Expression;
using MethodDefinition = IronRuby.Compiler.Ast.MethodDefinition;
using ModuleDefinition = IronRuby.Compiler.Ast.ModuleDefinition;
using LocalVariable = IronRuby.Compiler.Ast.LocalVariable;
using ExpressionCompiler = IronRuby.Aot.Compiler.ExpressionCompiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Aot {
    internal sealed class AotCompiler {
        private readonly RubyContext _context;
        private readonly PersistedAssemblyBuilder _assembly;
        // the type the current file's methods and constant fields go to: one per file in a library, so that
        // loading a file's code loads only that file's fields (RubyStartup<i>)
        private TypeBuilder _type;
        private readonly ModuleBuilder _module;
        private readonly List<KeyValuePair<FieldBuilder, Expression>> _constants = new();
        private readonly Dictionary<object, Expression> _constantFields = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<FieldInfo, bool> _siteFieldsUnused = new();
        private readonly Dictionary<string, string> _frameOwners = new();
        private readonly List<string> _entryPoints = new();
        private readonly List<MethodBuilder> _entryMethods = new();
        // method name -> (IL offset, line) pairs, in emit order: the startup library's line table
        private readonly Dictionary<string, List<int>> _lines = new();
        private Dictionary<object, FieldInfo> _wellKnownStatics;
        private int _fieldCounter;
        public readonly Dictionary<string, int> Stats = new();

        /// <summary>
        /// A library rather than a program: no main file and no entry point; the files are listed in a
        /// PrecompiledFilesAttribute with the hashes of their sources, for IronRuby's loader to pick up
        /// (IronRuby.Startup.dll, see PrecompiledCode.TryRegisterStartupLibrary).
        /// </summary>
        public readonly bool Library;

        /// <summary>A library's "name=mvid" of the host assemblies it is compiled for (see PrecompiledFilesAttribute.Assemblies).</summary>
        public string[] DependencyIds;

        /// <summary>
        /// A library's IronRuby tree, which all its files are in, and where that is from the directory the library goes to.
        /// Paths under it that the code holds (__FILE__, a block's or a method's file) are compiled as calls of
        /// PrecompiledCode.Rooted, so that a copy of the tree elsewhere runs them with its own paths.
        /// </summary>
        public string Root, RootFromLibrary;

        // a path under Root in something other than a string constant (a symbol, a frozen string's bytes): the library
        // then only runs from where it was compiled
        private bool _unrelocatable;
        private readonly Dictionary<string, Expression> _rootedStrings = new();

        private string RootPrefix => Root == null ? null : Root + "/";

        private bool IsRooted(string s) => RootPrefix != null && s != null && s.Contains(RootPrefix, StringComparison.Ordinal);

        /// <summary>A string that may hold a path under Root: as is, or rebased onto the run-time tree.</summary>
        private Expression StringExpression(string s) {
            if (!IsRooted(s)) {
                return Expression.Constant(s, typeof(string));
            }
            Count("paths moved with the tree");
            return Expression.Call(typeof(PrecompiledCode).GetMethod("Rooted"),
                Expression.Constant(s.Replace(RootPrefix, PrecompiledCode.RootMarker.ToString()), typeof(string)));
        }

        // in the code itself, once per file and string: a lazily set static field
        private Expression RootedStringField(string s) {
            if (!_rootedStrings.TryGetValue(s, out var field)) {
                _rootedStrings[s] = field = NewField(typeof(string), StringExpression(s), "path");
            }
            return field;
        }

        private void CheckUnrelocatable(string what, string text) {
            if (IsRooted(text)) {
                _unrelocatable = true;
                Count("UNRELOCATABLE: a path under the tree in a " + what);
            }
        }

        public AotCompiler(RubyContext context, string assemblyName, bool library = false) {
            _context = context;
            Library = library;
            _assembly = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
            var module = _module = _assembly.DefineDynamicModule(assemblyName);
            _type = module.DefineType(library ? "RubyStartup0" : "RubyProgram", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
        }

        private void Count(string what, int n = 1) {
            Stats.TryGetValue(what, out int c);
            Stats[what] = c + n;
        }

        #region Driver

        public void Compile(string mainFile, IList<string> files, string outputPath) {
            var paths = files.Select(Path.GetFullPath).ToArray();
            if (Library) {
                IronRuby.Aot.Compiler.LambdaCompiler.LineRecorder = (method, offset, line) => {
                    if (!_lines.TryGetValue(method, out var list)) _lines[method] = list = new List<int>();
                    // one entry per offset: the last sequence point emitted there wins
                    if (list.Count >= 2 && list[list.Count - 2] == offset) { list[list.Count - 1] = line; return; }
                    list.Add(offset); list.Add(line);
                };
            }
            if (Library && LoopMethodsJitted) {
                IronRuby.Aot.Compiler.LambdaCompiler.MethodImplFlags = lambda => {
                    if (!IsMethodOrBlock(lambda) || !LoopFinder.HasLoop(lambda)) return MethodImplAttributes.IL;
                    Count("methods and blocks with a loop left to the JIT (AggressiveOptimization)");
                    return MethodImplAttributes.AggressiveOptimization;
                };
            }
            // each file parsed first: the frame labels of one file's methods depend on what the others do with its modules
            var asts = new SourceUnitTree[paths.Length];
            var units = new SourceUnit[paths.Length];
            var unitOptions = new RubyCompilerOptions[paths.Length];
            var extended = new HashSet<string>();
            for (int i = 0; i < paths.Length; i++) {
                var unit = units[i] = _context.CreateFileUnit(paths[i]);
                bool isMain = mainFile != null && paths[i] == Path.GetFullPath(mainFile);
                var options = unitOptions[i] = new RubyCompilerOptions() { FactoryKind = isMain ? TopScopeFactoryKind.Main : TopScopeFactoryKind.File };
                asts[i] = IronRuby.Prism.PrismAstBridge.Parse(unit, options, ErrorSink.Default);
                if (asts[i] == null) throw new InvalidOperationException("syntax error in " + paths[i]);
                new ExtendWalker(extended).Walk(asts[i]);
            }
            for (int i = 0; i < paths.Length; i++) {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (Library && i > 0) {
                    _type = _module.DefineType("RubyStartup" + i, TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
                    // a field of another file's type would load that type too
                    _constantFields.Clear();
                    _rootedStrings.Clear();
                    _boxedLiterals.Clear();
                }
                var unit = units[i];
                var options = unitOptions[i];
                var ast = asts[i];
                asts[i] = null;
                new FrameLabelWalker(_frameOwners, _context, extended).Walk(ast);
                var lambda = _context.TransformTree<Func<RubyScope, object, object>>(ast, unit, options);

                var rewritten = (LambdaExpression)Rewrite(lambda);
                // the name is what the backtrace builder decodes into "file:line:in '<main>'"
                var method = _type.DefineMethod(lambda.Name, MethodAttributes.Public | MethodAttributes.Static);
                _entryPoints.Add(lambda.Name);
                _entryMethods.Add(method);
                ExpressionCompiler.CompileToMethod(rewritten, method);
                if (Library) _type.CreateType();
                Console.Error.WriteLine($"compiled {paths[i]} in {sw.ElapsedMilliseconds} ms");
                if (Environment.GetEnvironmentVariable("IR_AOT_CHECK_BRANCHES") == "1") CheckBranches();
            }

            // load-time initialization of the constants, in chunks to keep methods a sane size
            const int ChunkSize = 200;
            int chunks = 0;
            for (int start = 0; start < _constants.Count; start += ChunkSize, chunks++) {
                var assigns = _constants.Skip(start).Take(ChunkSize)
                    .Select(kv => (Expression)Expression.Assign(Expression.Field(null, kv.Key), kv.Value)).ToList();
                assigns.Add(Expression.Empty());
                var init = Expression.Lambda<Action>(Expression.Block(assigns), "__init" + chunks, null);
                ExpressionCompiler.CompileToMethod(init, _type.DefineMethod("__init" + chunks, MethodAttributes.Public | MethodAttributes.Static));
            }

            if (Library) {
                if (chunks != 0) throw new NotSupportedException("a startup library initializes its constants lazily");
                IronRuby.Aot.Compiler.LambdaCompiler.LineRecorder = null;
                IronRuby.Aot.Compiler.LambdaCompiler.MethodImplFlags = null;
                // the files relative to the tree, if they are in it
                var recorded = paths.Select(p => RootPrefix != null && p.StartsWith(RootPrefix, StringComparison.Ordinal) ? p.Substring(RootPrefix.Length) : p).ToArray();
                DefineStartupIndex(new CustomAttributeBuilder(
                    typeof(PrecompiledFilesAttribute).GetConstructors()[0],
                    new object[] { recorded, _entryPoints.ToArray(), paths.Select(PrecompiledCode.HashFile).ToArray(), DependencyIds ?? new string[0],
                        Root, RootFromLibrary, !_unrelocatable }));
                Count("line table: methods", _lines.Count);
                Count("line table: entries", _lines.Values.Sum(l => l.Count / 2));
                Save(outputPath, PEHeaderBuilder.CreateLibraryHeader(), default);
                return;
            }

            // Main(string[] args) => AotRuntime.Main(typeof(RubyProgram), args)
            var main = _type.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(int), new[] { typeof(string[]) });
            var il = main.GetILGenerator();
            il.Emit(OpCodes.Ldtoken, _type);
            il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(AotRuntime).GetMethod("Main"));
            il.Emit(OpCodes.Ret);
            main.SetCustomAttribute(new CustomAttributeBuilder(typeof(RubyStackTraceHiddenAttribute).GetConstructor(Type.EmptyTypes), new object[0]));

            _type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AotProgramAttribute).GetConstructors()[0],
                new object[] { Path.GetFullPath(mainFile), paths, _entryPoints.ToArray(), chunks, SearchPaths }));
            _type.CreateType();

            Count("constant fields initialized at load time", _constants.Count);
            Count("init chunks", chunks);

            Save(outputPath, PEHeaderBuilder.CreateExecutableHeader(), MetadataTokens.MethodDefinitionHandle(main.MetadataToken));
        }

        /// <summary>
        /// A method or block whose own code has a loop is left to the JIT (AggressiveOptimization: crossgen2 does not compile it,
        /// and the JIT compiles it optimized at its first call). ReadyToRun code is only replaced by optimized code after it has
        /// been called often enough, and never while it runs: a loop that runs long in a method called once - Kernel#loop,
        /// a fiber thread's loop - would run as ReadyToRun code to the end, calling every helper the JIT would have inlined.
        /// File and class bodies, which run once, keep their ReadyToRun code. IR_AOT_LOOP_JIT=0 turns this off.
        /// </summary>
        public bool LoopMethodsJitted = Environment.GetEnvironmentVariable("IR_AOT_LOOP_JIT") != "0";

        private static bool IsMethodOrBlock(LambdaExpression lambda) {
            // the encoded Ruby frame name: "\u2111\u211c:" + label; file and class bodies are labelled <top (required)>, <main>, <class:X>...
            const string prefix = "\u2111\u211c:";
            return lambda.Name != null && lambda.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                lambda.Name.Length > prefix.Length && lambda.Name[prefix.Length] != '<';
        }

        // a loop in the lambda's own code, not in the lambdas nested in it
        private sealed class LoopFinder : ExpressionVisitor {
            private bool _found;

            public static bool HasLoop(LambdaExpression lambda) {
                var finder = new LoopFinder();
                finder.Visit(lambda.Body);
                return finder._found;
            }

            public override Expression Visit(Expression node) => _found ? node : base.Visit(node);

            protected override Expression VisitLoop(LoopExpression node) {
                _found = true;
                return node;
            }

            protected override Expression VisitLambda<T>(Expression<T> node) => node;

            protected override Expression VisitExtension(Expression node) => node.CanReduce ? Visit(node.Reduce()) : node;
        }

        /// <summary>
        /// RubyStartupIndex, the library's small entry class (the files' code is in RubyStartup&lt;i&gt;, one class per
        /// file, which is loaded when the file is), carrying the PrecompiledFilesAttribute:
        ///   Entry(i): the delegate of file i's top level, in PrecompiledFilesAttribute order;
        ///   Lines(): the line table, "method\u0001offset,line,offset,line...\n" per method (there is no PDB).
        /// </summary>
        private void DefineStartupIndex(CustomAttributeBuilder files) {
            var index = _module.DefineType("RubyStartupIndex",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
            index.SetCustomAttribute(files);
            var funcType = typeof(Func<RubyScope, object, object>);
            var entry = index.DefineMethod("Entry", MethodAttributes.Public | MethodAttributes.Static, funcType, new[] { typeof(int) });
            var il = entry.GetILGenerator();
            var labels = _entryMethods.Select(_ => il.DefineLabel()).ToArray();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Switch, labels);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            for (int i = 0; i < _entryMethods.Count; i++) {
                il.MarkLabel(labels[i]);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ldftn, _entryMethods[i]);
                il.Emit(OpCodes.Newobj, funcType.GetConstructor(new[] { typeof(object), typeof(IntPtr) }));
                il.Emit(OpCodes.Ret);
            }

            var sb = new System.Text.StringBuilder();
            foreach (var kv in _lines) {
                sb.Append(kv.Key).Append('\u0001').Append(string.Join(",", kv.Value)).Append('\n');
            }
            var lines = index.DefineMethod("Lines", MethodAttributes.Public | MethodAttributes.Static, typeof(string), Type.EmptyTypes);
            il = lines.GetILGenerator();
            il.Emit(OpCodes.Ldstr, sb.ToString());
            il.Emit(OpCodes.Ret);
            index.CreateType();
        }

        private void Save(string outputPath, PEHeaderBuilder header, MethodDefinitionHandle entryPoint) {
            var metadata = _assembly.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
            var peBuilder = new ManagedPEBuilder(
                header: header,
                metadataRootBuilder: new MetadataRootBuilder(metadata),
                ilStream: ilStream,
                mappedFieldData: fieldData,
                entryPoint: entryPoint);
            var blob = new BlobBuilder();
            peBuilder.Serialize(blob);
            using (var fs = File.Create(outputPath)) {
                blob.WriteContentTo(fs);
            }
        }

        // Debugging aid: the branch fixups PersistedAssemblyBuilder does at save time need every
        // method's branches in increasing operand order.
        private static void CheckBranches() {
            foreach (var mb in IronRuby.Aot.Compiler.LambdaCompiler.CreatedMethods) {
                var il = mb.GetILGenerator();
                var cf = il.GetType().GetField("_cfBuilder", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(il);
                var branches = (System.Collections.IList)cf.GetType().GetField("_branches", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(cf);
                int last = -1;
                foreach (var b in branches) {
                    int off = (int)b.GetType().GetField("OperandOffset", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(b);
                    object op = b.GetType().GetField("OpCode", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(b);
                    if (off <= last) {
                        Console.Error.WriteLine($"BAD BRANCH ORDER in {mb.Name}: operand at {off} after {last} ({op})");
                        break;
                    }
                    last = off;
                }
            }
            IronRuby.Aot.Compiler.LambdaCompiler.CreatedMethods.Clear();
        }

        #endregion

        #region Rewriting

        private Expression Rewrite(Expression node) => new Rewriter(this).Visit(node);

        private sealed class Rewriter : DynamicExpressionVisitor {
            private readonly AotCompiler _c;
            public Rewriter(AotCompiler c) { _c = c; }

            // the statement being rewritten: the last sequence point visited (see NoteCallLocation)
            private string _file;
            private int _line;

            protected override Expression VisitDebugInfo(DebugInfoExpression node) {
                if (!node.IsClear) {
                    _file = node.Document.FileName;
                    _line = node.StartLine;
                }
                return base.VisitDebugInfo(node);
            }

            protected override Expression VisitExtension(Expression node) {
                if (!node.CanReduce) {
                    throw new NotSupportedException("irreducible extension node " + node.GetType());
                }
                string file = _file;
                int line = _line;
                // A call site the interpreter would run gets the interpreter's site shape: every argument and the result
                // an object (DynamicInstructionN.CreateUntypedInstruction). Reduced, it would be typed after its
                // arguments ((CallSite, RubyScope, object, int) and so on): a delegate type, CallSite<T> and rule
                // instantiations of its own that are JIT-compiled at startup (~110 methods for `ir -e 1'), rules its
                // binder caches apart from the interpreted code's, and shared-rule records ahead of the program's own
                // compiled code (see RuleVersionSet.MakeTest).
                if (node is Microsoft.Scripting.Ast.LightDynamicExpression && node.Type == typeof(object) &&
                    node.Reduce() is DynamicExpression reduced && reduced.Type == typeof(object) && reduced.Arguments.Count <= MaxUntypedSiteArguments) {
                    var args = Visit(reduced.Arguments);
                    return _c.NoteCallLocation(reduced.Binder, args, file, line, _c.UntypedDynamic(reduced.Binder, args));
                }
                return Visit(node.Reduce());
            }

            protected override Expression VisitConstant(ConstantExpression node) {
                if (node.Value is string s && _c.IsRooted(s)) {
                    var path = _c.RootedStringField(s);
                    return path.Type == node.Type ? path : Expression.Convert(path, node.Type);
                }
                if (Census.IsLiteral(node.Value, node.Type)) {
                    return node;
                }
                var field = _c.ConstantField(node.Value, node.Type);
                return field.Type == node.Type ? field : Expression.Convert(field, node.Type);
            }

            protected override Expression VisitDynamic(DynamicExpression node) {
                string file = _file;
                int line = _line;
                var args = Visit(node.Arguments);
                var siteExpr = _c.SiteField(node.DelegateType, node.Binder);
                var site = Expression.Variable(siteExpr.Type, "#site");
                var invokeArgs = new List<Expression> { site };
                invokeArgs.AddRange(args);
                return _c.NoteCallLocation(node.Binder, args, file, line, Expression.Block(node.Type, new[] { site },
                    Expression.Assign(site, siteExpr),
                    Expression.Invoke(Expression.Field(site, "Target"), invokeArgs)));
            }
        }

        // the builtins that record where they were called from (RubyUtils.TryGetCallerSourceLocation)
        private static readonly HashSet<string> _locationRecordingMethods = new() { "const_set", "autoload" };

        /// <summary>
        /// A call of Module#const_set or #autoload records its caller's location, which compiled code only has on the
        /// CLR stack: the call says where it is first (PrecompiledCode.NoteCallLocation), with the scope the method is
        /// then called from. Not in the Ruby half of the core library, whose frames report their caller's location.
        /// </summary>
        private Expression NoteCallLocation(CallSiteBinder binder, IList<Expression> args, string file, int line, Expression call) {
            if (!(binder is RubyCallAction action) || !_locationRecordingMethods.Contains(action.MethodName) || !action.Signature.HasScope ||
                args.Count == 0 || file == null || line <= 0 || RubyStackTraceBuilder.IsInternalFile(file)) {
                return call;
            }
            var scope = args[0];
            while (scope is UnaryExpression u && u.NodeType == ExpressionType.Convert) {
                scope = u.Operand;
            }
            if (!(scope is ParameterExpression)) {
                return call;
            }
            Count("const_set/autoload calls noting their location");
            var note = Expression.Call(typeof(PrecompiledCode).GetMethod(nameof(PrecompiledCode.NoteCallLocation)),
                Expression.Convert(scope, typeof(object)), IsRooted(file) ? RootedStringField(file) : Expression.Constant(file, typeof(string)),
                Expression.Constant(line));
            // a call that went elsewhere (a receiver's own const_set) leaves no note for a later one
            var clear = Expression.Call(typeof(PrecompiledCode).GetMethod(nameof(PrecompiledCode.ClearCallLocation)));
            if (call.Type == typeof(void)) {
                return Expression.Block(note, call, clear);
            }
            var result = Expression.Variable(call.Type, "#noted");
            return Expression.Block(call.Type, new[] { result }, note, Expression.Assign(result, call), clear, result);
        }

        // DynamicInstructionN.CreateUntypedInstruction's limit
        private const int MaxUntypedSiteArguments = 15;

        private static readonly Dictionary<int, Type> _untypedSiteTypes = new();
        private readonly Dictionary<string, Expression> _boxedLiterals = new();

        private Expression UntypedDynamic(CallSiteBinder binder, IList<Expression> args) {
            if (!_untypedSiteTypes.TryGetValue(args.Count, out var delegateType)) {
                var types = new Type[args.Count + 2];
                types[0] = typeof(CallSite);
                for (int i = 1; i < types.Length; i++) types[i] = typeof(object);
                _untypedSiteTypes[args.Count] = delegateType = Expression.GetDelegateType(types);
            }
            Count("call sites in the interpreter's shape");
            var siteExpr = SiteField(delegateType, binder);
            var site = Expression.Variable(siteExpr.Type, "#site");
            var invokeArgs = new List<Expression> { site };
            foreach (var arg in args) {
                invokeArgs.Add(arg.Type == typeof(object) ? arg : BoxArgument(arg));
            }
            return Expression.Block(typeof(object), new[] { site },
                Expression.Assign(site, siteExpr),
                Expression.Invoke(Expression.Field(site, "Target"), invokeArgs));
        }

        // a value-type literal is boxed once, into a static field; anything else as it comes
        private Expression BoxArgument(Expression arg) {
            if (arg is ConstantExpression c && c.Value != null && c.Type.IsValueType) {
                var key = c.Type.FullName + ":" + c.Value;
                if (!_boxedLiterals.TryGetValue(key, out var field)) {
                    Count("boxed literal arguments");
                    _boxedLiterals[key] = field = NewField(typeof(object), Expression.Convert(c, typeof(object)), "box");
                }
                return field;
            }
            return Expression.Convert(arg, typeof(object));
        }

        /// <summary>
        /// Initialize constants when first used rather than all at load time: a program (and the
        /// prelude above all) has thousands of call sites and `def's that never run. The price is
        /// a null check per use; two threads racing through a first use may both create the value.
        /// </summary>
        public bool LazyInit = true;

        private Expression NewField(Type type, Expression init, string hint) {
            var field = _type.DefineField("k" + (_fieldCounter++) + "_" + hint, type, FieldAttributes.Public | FieldAttributes.Static);
            var access = Expression.Field(null, field);
            if (LazyInit && !type.IsValueType) {
                Count("constant fields (lazily initialized)");
                return Expression.Coalesce(access, Expression.Assign(access, init));
            }
            _constants.Add(new KeyValuePair<FieldBuilder, Expression>(field, init));
            return access;
        }

        private readonly Dictionary<Type, Type> _persistedDelegates = new();
        public readonly List<string> ExtraAssemblies = new();
        public string OutputDirectory;
        public string[] SearchPaths;

        /// <summary>
        /// The DLR makes delegate types for call sites with more arguments than Func has in an
        /// in-memory assembly, which a saved assembly cannot reference. Each is re-made in a small
        /// saved assembly of its own, loaded back right away so the rest of the compilation can use
        /// it as a real type.
        /// </summary>
        private Type PersistDelegateType(Type inMemory) {
            if (_persistedDelegates.TryGetValue(inMemory, out var result)) return result;
            var invoke = inMemory.GetMethod("Invoke");
            string name = _type.Assembly.GetName().Name + ".Delegate" + _persistedDelegates.Count;
            var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
            var tb = ab.DefineDynamicModule(name).DefineType("D", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AutoClass, typeof(MulticastDelegate));
            tb.DefineConstructor(MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public,
                CallingConventions.Standard, new[] { typeof(object), typeof(IntPtr) }).SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
            tb.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                invoke.ReturnType, invoke.GetParameters().Select(p => p.ParameterType).ToArray()).SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
            tb.CreateType();
            string path = Path.Combine(OutputDirectory, name + ".dll");
            ab.Save(path);
            result = Assembly.LoadFrom(path).GetType("D");
            ExtraAssemblies.Add(path);
            return _persistedDelegates[inMemory] = result;
        }

        private Expression ContextExpression => Expression.Property(null, typeof(PrecompiledCode).GetProperty("Context"));

        /// <summary>A static CallSite field for a dynamic site: `CallSite<T>.Create(binder)` at load time.</summary>
        private Expression SiteField(Type delegateType, CallSiteBinder binder) {
            if (delegateType.Assembly.IsDynamic) {
                delegateType = PersistDelegateType(delegateType);
                Count("call sites needing a delegate type of their own (> 16 arguments)");
            }
            Count("call sites");
            var siteType = typeof(CallSite<>).MakeGenericType(delegateType);
            var init = Expression.Call(siteType.GetMethod("Create"), BinderExpression(binder));
            return NewField(siteType, init, "site");
        }

        /// <summary>
        /// The binder, bound to the runtime the program runs in. IronRuby's binders describe
        /// themselves as `X.MakeShared(args)` - a binder usable from any runtime, which pays for
        /// cross-runtime checks in every rule - so that is turned into `X.Make(context, args)`.
        /// </summary>
        private Expression BinderExpression(CallSiteBinder binder) {
            if (binder is not IExpressionSerializable ser) {
                throw new NotSupportedException("binder " + binder.GetType() + " cannot describe itself as an expression");
            }
            var expr = ser.CreateExpression();
            if (expr is MethodCallExpression call && call.Method.Name == "MakeShared") {
                var types = new[] { typeof(RubyContext) }.Concat(call.Method.GetParameters().Select(p => p.ParameterType)).ToArray();
                var make = call.Method.DeclaringType.GetMethod("Make", BindingFlags.Public | BindingFlags.Static, null, types, null);
                if (make != null) {
                    Count("binders bound to the runtime");
                    expr = Expression.Call(make, new[] { ContextExpression }.Concat(call.Arguments));
                } else {
                    Count("binders left shared (no Make(RubyContext, ...))");
                }
            }
            return Rewrite(expr);
        }

        /// <summary>The static field that holds a live object of the tree, created on first sight.</summary>
        private Expression ConstantField(object value, Type type) {
            if (_constantFields.TryGetValue(value, out var existing)) {
                return existing;
            }
            if (value is RubyContext) {
                Count("constants: RubyContext");
                return ContextExpression;
            }
            if (WellKnownStatics.TryGetValue(value, out var staticField)) {
                Count("constants: well-known static " + staticField.DeclaringType.Name + "." + staticField.Name);
                return _constantFields[value] = Expression.Field(null, staticField);
            }
            Expression init = Serialize(value, out string hint);
            Count("constants: " + (hint.StartsWith("def_") ? "def (method body)" : hint));
            var fieldType = type.IsVisible ? type : value.GetType();
            if (!fieldType.IsVisible) fieldType = typeof(object);
            var field = NewField(fieldType, init.Type == fieldType ? init : Expression.Convert(init, fieldType), hint);
            _constantFields[value] = field;
            return field;
        }

        private Dictionary<object, FieldInfo> WellKnownStatics {
            get {
                if (_wellKnownStatics == null) {
                    _wellKnownStatics = new Dictionary<object, FieldInfo>(ReferenceEqualityComparer.Instance);
                    foreach (var t in typeof(RubyContext).Assembly.GetExportedTypes()) {
                        if (t.ContainsGenericParameters) continue;
                        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static)) {
                            if (!f.IsInitOnly || f.FieldType.IsValueType) continue;
                            try {
                                object v = f.GetValue(null);
                                if (v != null && !(v is string) && !_wellKnownStatics.ContainsKey(v)) _wellKnownStatics[v] = f;
                            } catch (Exception) { }
                        }
                    }
                }
                return _wellKnownStatics;
            }
        }

        private static readonly FieldInfo _dispatcherAttributes = typeof(BlockDispatcher).GetField("_attributesAndArity", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>An expression that re-creates <paramref name="value"/> at load time.</summary>
        private Expression Serialize(object value, out string hint) {
            switch (value) {
                case CallSite site:
                    hint = "site";
                    return Expression.Call(site.GetType().GetMethod("Create"), BinderExpression(site.Binder));

                case RubyMethodBody body:
                    hint = "def_" + Sanitize(body.Name);
                    return SerializeMethodBody(body);

                case BlockDispatcher dispatcher: {
                    hint = "block";
                    int postCount = 0;
                    var postField = dispatcher.GetType().GetField("_postCount", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (postField != null) postCount = (int)postField.GetValue(dispatcher);
                    int attributes = (int)(BlockSignatureAttributes)_dispatcherAttributes.GetValue(dispatcher);
                    var args = new List<Expression> {
                        Expression.Constant(dispatcher.ParameterCount), Expression.Constant(postCount), Expression.Constant(attributes),
                        StringExpression(dispatcher.SourcePath), Expression.Constant(dispatcher.SourceLine),
                        SerializeSignature(dispatcher.ParameterSignature)
                    };
                    // the locals of enclosing scopes the block uses, which Ractor.new checks
                    if (dispatcher.OuterVariableNames != null) {
                        args.Add(Strings(dispatcher.OuterVariableNames));
                        args.Add(Strings(dispatcher.AssignedOuterVariableNames));
                        args.Add(Strings(dispatcher.ReassignedOuterVariableNames));
                    }
                    return Expression.Call(typeof(PrecompiledCode).GetMethods()
                        .Single(m => m.Name == "CreateBlockDispatcher" && m.GetParameters().Length == args.Count), args);
                }

                case RubySymbol symbol:
                    hint = "sym";
                    CheckUnrelocatable("symbol", symbol.ToString());
                    return Expression.Call(typeof(PrecompiledCode).GetMethod("Symbol"),
                        ByteArray(symbol.String.ToByteArray()), ConstantField(symbol.Encoding, typeof(RubyEncoding)));

                case string[] strings:
                    hint = "strings";
                    return Expression.NewArrayInit(typeof(string), strings.Select(StringExpression));

                case byte[] bytes:
                    hint = "bytes";
                    return ByteArray(bytes);

                case Array array when array.Rank == 1 && !value.GetType().GetElementType().IsValueType: {
                    var elementType = value.GetType().GetElementType();
                    hint = elementType.Name + "s";
                    var items = new List<Expression>();
                    foreach (var item in array) {
                        items.Add(item == null ? Expression.Constant(null, elementType)
                            : Census.IsLiteral(item, elementType) ? Expression.Constant(item, elementType)
                            : ConvertTo(ConstantField(item, elementType), elementType));
                    }
                    return Expression.NewArrayInit(elementType, items);
                }

                case MutableString str when str.IsFrozen:
                    // literal parts of an interpolated string: frozen, only ever copied from; made of what the compiler
                    // made them of, characters or bytes (appending one kind to the other converts)
                    hint = "str";
                    CheckUnrelocatable("string literal", str.ToString());
                    if (!str.IsBinary) {
                        Count("frozen strings (characters)");
                        return Expression.Call(typeof(PrecompiledCode).GetMethod("FrozenString", new[] { typeof(string), typeof(RubyEncoding) }),
                            Expression.Constant(str.ToString(), typeof(string)), ConstantField(str.Encoding, typeof(RubyEncoding)));
                    }
                    Count("frozen strings (bytes)");
                    return Expression.Call(typeof(PrecompiledCode).GetMethod("FrozenString", new[] { typeof(byte[]), typeof(RubyEncoding) }),
                        ByteArray(str.ToByteArray()), ConstantField(str.Encoding, typeof(RubyEncoding)));

                case InstanceVariableSite ivar:
                    hint = "ivar";
                    return Expression.New(typeof(InstanceVariableSite).GetConstructor(new[] { typeof(string), typeof(bool) }),
                        Expression.Constant(ivar.Name), Expression.Constant(ivar.IsCoreLibrary));

                case GlobalVariableSite gvar:
                    hint = "gvar";
                    return Expression.New(typeof(GlobalVariableSite).GetConstructor(new[] { typeof(string), typeof(bool) }),
                        Expression.Constant(gvar.Name), Expression.Constant(gvar.IsCoreLibrary));

                case ConstantSiteCache cache when cache.IsCoreLibrary:
                    hint = "constcache";
                    return Expression.New(typeof(ConstantSiteCache).GetConstructor(new[] { typeof(bool) }), Expression.Constant(true));

                case ExpressionQualifiedConstantSiteCache cache when cache.IsCoreLibrary:
                    hint = "constcache";
                    return Expression.New(typeof(ExpressionQualifiedConstantSiteCache).GetConstructor(new[] { typeof(bool) }), Expression.Constant(true));

                case ExpressionQualifiedConstantSiteCache:
                case ExpressionQualifiedIsDefinedConstantSiteCache:
                case ConstantSiteCache:
                case IsDefinedConstantSiteCache:
                    hint = "constcache";
                    return Expression.New(value.GetType());

                case ArrayExtremeSite extreme:
                    hint = "extreme";
                    return Expression.New(typeof(ArrayExtremeSite).GetConstructor(new[] { typeof(bool) }), Expression.Constant(extreme.IsMax));

                case BinaryOpStorage:
                    hint = "opstorage";
                    return Expression.New(typeof(BinaryOpStorage).GetConstructor(new[] { typeof(RubyContext) }), ContextExpression);

                case IronRuby.Builtins.Range range when range.Begin is int && range.End is int:
                    hint = "range";
                    return Expression.New(typeof(IronRuby.Builtins.Range).GetConstructor(new[] { typeof(int), typeof(int), typeof(bool) }),
                        Expression.Constant(range.Begin), Expression.Constant(range.End), Expression.Constant(range.ExcludeEnd));

                case IStrongBox box when value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(StrongBox<>):
                    if (box.Value != null) throw new NotSupportedException("StrongBox holding " + box.Value.GetType());
                    hint = "cache";
                    return Expression.New(value.GetType());

                case IExpressionSerializable ser:
                    hint = value.GetType().Name;
                    return Rewrite(ser.CreateExpression());
            }
            Count("UNSUPPORTED constant " + value.GetType().FullName);
            throw new NotSupportedException("cannot re-create a " + value.GetType().FullName + " at load time");
        }

        private static Expression ConvertTo(Expression e, Type type) => e.Type == type ? e : Expression.Convert(e, type);

        private static Expression ByteArray(byte[] bytes) =>
            Expression.NewArrayInit(typeof(byte), bytes.Select(b => Expression.Constant(b)));

        private static string Sanitize(string s) => new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

        private static Expression Strings(IEnumerable<string> strings) =>
            strings == null ? (Expression)Expression.Constant(null, typeof(string[]))
                : Expression.NewArrayInit(typeof(string), strings.Select(s => Expression.Constant(s, typeof(string))));

        private static readonly Func<RubyParameterSignature, object>[] _sigFields = null;

        private static T SigField<T>(RubyParameterSignature sig, string name) =>
            (T)typeof(RubyParameterSignature).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sig);

        private Expression SerializeSignature(RubyParameterSignature sig) {
            if (sig == null) return Expression.Constant(null, typeof(RubyParameterSignature));
            var ps = SigField<RubyParameterSignature.Parameter[]>(sig, "_parameters");
            return Expression.Call(typeof(PrecompiledCode).GetMethod("Signature"),
                Strings(ps.Select(p => p.Kind)), Strings(ps.Select(p => p.Name)),
                Expression.Constant(SigField<int>(sig, "_leadingCount")), Expression.Constant(SigField<int>(sig, "_optionalCount")),
                Expression.Constant(SigField<int>(sig, "_postCount")), Expression.Constant(SigField<bool>(sig, "_hasRest")),
                Expression.Constant(SigField<int>(sig, "_requiredKeywordCount")), Expression.Constant(SigField<bool>(sig, "_hasKeywords")),
                Expression.Constant(SigField<bool>(sig, "_hasKeywordRest")), Strings(sig.ImplicitParameterNames));
        }

        /// <summary>
        /// A `def': the header (what the runtime asks of the method's AST) plus a factory lambda
        /// (declaringScope, declaringModule) => body delegate, compiled here instead of at the first call.
        /// </summary>
        private Expression SerializeMethodBody(RubyMethodBody body) {
            var ast = body.Ast;
            var ps = ast.Parameters;
            string label;
            if (!_frameOwners.TryGetValue(LocationKey(ast), out label)) label = ast.Name;

            var gen = new AstGenerator(_context, new RubyCompilerOptions(), body.Document, AotHost.GetEncoding(body), false);
            var scopeParam = Expression.Parameter(typeof(RubyScope), "#declaringScope");
            var moduleParam = Expression.Parameter(typeof(RubyModule), "#declaringModule");
            var bodyLambda = ast.TransformBody(gen, scopeParam, moduleParam, label, null);
            var rewritten = Rewrite(bodyLambda);
            if (ThreadFrames) {
                rewritten = new ThreadFrameAdder(this, ps.Block != null).Visit(rewritten);
            }
            var factory = Expression.Lambda<Func<RubyScope, RubyModule, Delegate>>(
                Expression.Convert(rewritten, typeof(Delegate)), "def " + label, new[] { scopeParam, moduleParam });
            Count("method bodies compiled ahead of time");

            var loc = ast.Location;
            int[] location = { loc.Start.Index, loc.Start.Line, loc.Start.Column, loc.End.Index, loc.End.Line, loc.End.Column };
            var header = Expression.Call(typeof(PrecompiledCode).GetMethod("MethodHeader"),
                Expression.Constant(ast.Name), Expression.Constant(ast.Target != null), Expression.Constant(ast.UsesBlock),
                Expression.NewArrayInit(typeof(int), location.Select(i => Expression.Constant(i))),
                Strings(ps.Mandatory.Select(m => ((LocalVariable)m).Name)), Expression.Constant(ps.LeadingMandatoryCount),
                Strings(ps.Optional.Select(o => ((LocalVariable)o.Left).Name)),
                Expression.Constant(ps.Unsplat != null ? ((LocalVariable)ps.Unsplat).Name : null, typeof(string)),
                Expression.Constant(ps.Block?.Name, typeof(string)),
                SerializeSignature(ps.Signature));
            return Expression.Call(typeof(PrecompiledCode).GetMethod("CreateMethodBody",
                    new[] { typeof(MethodDefinition), typeof(string), typeof(RubyEncoding), typeof(Func<RubyScope, RubyModule, Delegate>), typeof(string), typeof(string) }),
                header, StringExpression(body.Document?.FileName),
                ConstantField(AotHost.GetEncoding(body), typeof(RubyEncoding)), factory,
                Expression.Constant(JitSource(body, ast), typeof(string)), Expression.Constant(label, typeof(string)));
        }

        /// <summary>
        /// Another thread's backtrace is built from the interpreter's frame chain, which compiled code is not in: a method
        /// or block that runs a block (a yield, or a block parameter of a method) or a loop keeps a frame of its own for it,
        /// PrecompiledFrames, so that Kernel#loop, Set#each or Monitor#synchronize show in a thread dump as they do without
        /// the library. These are the frames a parked or busy thread is likely to be in, below its own code (in the startup
        /// files 363 of the 2,379 methods and 68 blocks); the other methods do not pay for a frame at every call.
        /// IR_AOT_THREAD_FRAMES=0 turns this off.
        /// </summary>
        public bool ThreadFrames = Environment.GetEnvironmentVariable("IR_AOT_THREAD_FRAMES") != "0";

        // wraps a def's body lambda (the first visited) and the blocks in it, those that run a block or a loop:
        //   var #frames = PrecompiledFrames.Current; var #frameDepth = #frames.Enter(name, line);
        //   try { body, with #frames.SetLine(#frameDepth, line) after each sequence point } finally { #frames.Leave(#frameDepth); }
        private sealed class ThreadFrameAdder : ExpressionVisitor {
            private readonly AotCompiler _c;
            private bool _isMethod = true;
            private readonly bool _hasBlockParameter;

            public ThreadFrameAdder(AotCompiler c, bool hasBlockParameter) {
                _c = c;
                _hasBlockParameter = hasBlockParameter;
            }

            protected override Expression VisitLambda<T>(Expression<T> node) {
                bool isMethod = _isMethod;
                _isMethod = false;
                var body = Visit(node.Body);
                if (!IsMethodOrBlock(node) || !(isMethod && _hasBlockParameter || YieldFinder.Yields(node.Body) || LoopFinder.HasLoop(node))) {
                    return body == node.Body ? node : Expression.Lambda<T>(body, node.Name, node.TailCall, node.Parameters);
                }
                // the encoded name: "\u2111\u211c:" + label + U+2236 + line + U+2236 + id + U+2236 + file
                var parts = node.Name.Split('\u2236');
                int line = parts.Length > 1 && int.TryParse(parts[1], out int l) ? l : 0;
                var frames = Expression.Variable(typeof(PrecompiledFrames), "#frames");
                var depth = Expression.Variable(typeof(int), "#frameDepth");
                body = new LineSetter(frames, depth).Visit(body);
                _c.Count(isMethod ? "methods that run a block or a loop, with a frame for other threads' backtraces"
                    : "blocks that run a block or a loop, with a frame for other threads' backtraces");
                return Expression.Lambda<T>(
                    Expression.Block(node.ReturnType, new[] { frames, depth },
                        Expression.Assign(frames, Expression.Property(null, typeof(PrecompiledFrames).GetProperty(nameof(PrecompiledFrames.Current)))),
                        Expression.Assign(depth, Expression.Call(frames, typeof(PrecompiledFrames).GetMethod(nameof(PrecompiledFrames.Enter)),
                            Expression.Constant(node.Name, typeof(string)), Expression.Constant(line))),
                        Expression.TryFinally(ConvertTo(body, node.ReturnType),
                            Expression.Call(frames, typeof(PrecompiledFrames).GetMethod(nameof(PrecompiledFrames.Leave)), depth))),
                    node.Name, node.TailCall, node.Parameters);
            }

            protected override Expression VisitExtension(Expression node) => node.CanReduce ? Visit(node.Reduce()) : node;
        }

        // a yield anywhere in the lambda, its blocks included (a method whose block yields runs its block too)
        private sealed class YieldFinder : ExpressionVisitor {
            private bool _found;

            public static bool Yields(Expression body) {
                var finder = new YieldFinder();
                finder.Visit(body);
                return finder._found;
            }

            public override Expression Visit(Expression node) => _found ? node : base.Visit(node);

            protected override Expression VisitMethodCall(MethodCallExpression node) {
                if (node.Method.DeclaringType == typeof(RubyOps) &&
                    (node.Method.Name == nameof(RubyOps.MethodYield) || node.Method.Name == nameof(RubyOps.BlockYield))) {
                    _found = true;
                    return node;
                }
                return base.VisitMethodCall(node);
            }

            protected override Expression VisitExtension(Expression node) => node.CanReduce ? Visit(node.Reduce()) : node;
        }

        // the frame's line kept at each statement of the lambda's own code (not of the lambdas nested in it)
        private sealed class LineSetter : ExpressionVisitor {
            private readonly ParameterExpression _frames, _depth;

            public LineSetter(ParameterExpression frames, ParameterExpression depth) {
                _frames = frames;
                _depth = depth;
            }

            protected override Expression VisitDebugInfo(DebugInfoExpression node) {
                if (node.IsClear || node.StartLine <= 0) {
                    return node;
                }
                return Expression.Block(node,
                    Expression.Call(_frames, typeof(PrecompiledFrames).GetMethod(nameof(PrecompiledFrames.SetLine)), _depth, Expression.Constant(node.StartLine)));
            }

            protected override Expression VisitLambda<T>(Expression<T> node) => node;

            protected override Expression VisitExtension(Expression node) => node.CanReduce ? Visit(node.Reduce()) : node;
        }

        private string _sourcePath;
        private byte[] _sourceBytes;

        /// <summary>
        /// The source of a `def' the method JIT may specialize (JitStub.IsCandidate), which the library keeps for it: the JIT
        /// works from the method's AST, and the compiled method has none (PrecompiledCode.ParseJitSource parses this at the
        /// method's first call). Kept only if it parses back, the way the runtime will parse it, to a method the JIT takes too.
        /// </summary>
        private string JitSource(RubyMethodBody body, MethodDefinition ast) {
            string path = body.Document?.FileName;
            if (path == null || AotHost.GetEncoding(body) != RubyEncoding.UTF8 || !IronRuby.Runtime.Jit.JitStub.IsCandidate(ast, _context)) {
                return null;
            }
            if (_sourcePath != path) {
                // what prism was given: the UTF-8 bytes of the file's code, whose offsets the AST's locations are
                _sourceBytes = System.Text.Encoding.UTF8.GetBytes(_context.CreateFileUnit(path).GetCode());
                _sourcePath = path;
            }
            int start = ast.Location.Start.Index, end = ast.Location.End.Index;
            string text = start >= 0 && end > start && end <= _sourceBytes.Length ? System.Text.Encoding.UTF8.GetString(_sourceBytes, start, end - start) : null;
            var parsed = text != null && text.StartsWith("def", StringComparison.Ordinal) ? PrecompiledCode.ParseJitSource(_context, text, ast, path) : null;
            if (parsed == null || !IronRuby.Runtime.Jit.JitStub.IsCandidate(parsed, _context)) {
                Count("JIT candidates whose source does not parse back (not kept)");
                return null;
            }
            Count("method bodies whose source is kept for the JIT");
            Count("source kept for the JIT (chars)", text.Length);
            return text;
        }

        internal static string LocationKey(MethodDefinition def) => def.Location.Start.Index + ":" + def.Location.End.Index + ":" + def.Name;

        #endregion

        // the modules `extend M' names, by their last segment
        private sealed class ExtendWalker : Walker {
            private readonly HashSet<string> _names;
            public ExtendWalker(HashSet<string> names) { _names = names; }

            public override bool Enter(MethodCall node) {
                if (node.MethodName == "extend" && node.Arguments != null) {
                    foreach (var arg in node.Arguments.Expressions) {
                        if (arg is ConstantVariable c) _names.Add(c.Name);
                    }
                }
                if (node.Block is BlockDefinition block) block.Walk(this);
                return true;
            }
        }

        /// <summary>
        /// Frame labels ("Gem::Requirement#parse", "Gem::Requirement.create") that the runtime works out from
        /// the declaring module when the `def' runs; here they come from the lexical nesting. A qualified
        /// class name (class Gem::Requirement) is resolved the way the constant lookup most likely does it:
        /// its first segment against the enclosing modules, then against the modules compiled so far, and
        /// otherwise at the top level.
        ///
        /// A `def' run for another module than the one guessed here - the copy module_function makes of it, a def
        /// in class_eval or in a block - is compiled from its source at its first call (RubyMethodBody), so a wrong
        /// guess costs a parse, not a wrong label. So the guess is the copy most calls go to: for a module function
        /// (module_function or FileUtils' private_module_function) the module's singleton copy ("FileUtils.mkdir_p"),
        /// except in Kernel, whose functions are called without a receiver ("Kernel#sleep"), and in a module the files
        /// extend, whose functions the extended classes call (Gem::Deprecate#rubygems_deprecate, at every start).
        /// </summary>
        private sealed class FrameLabelWalker : Walker {
            private readonly Dictionary<string, string> _owners;
            private readonly RubyContext _context;
            // the names (last segment) of the modules the compiled files extend
            private readonly HashSet<string> _extended;
            // the module bodies (and blocks) the walk is in, innermost last
            private readonly List<Frame> _frames = new();
            private static readonly HashSet<string> _known = new();
            // the block of `X = Class.new do ... end' (Module.new, Struct.new, Data.define) and the module it makes
            private readonly Dictionary<MethodCall, string> _blockModules = new(ReferenceEqualityComparer.Instance);

            public FrameLabelWalker(Dictionary<string, string> owners, RubyContext context, HashSet<string> extended) {
                _owners = owners;
                _context = context;
                _extended = extended;
            }

            private sealed class Frame {
                // the full module path; null for a module whose name is not a constant path
                public string Path;
                // class << X: methods in it are singleton methods
                public bool Singleton;
                // module M (module_function applies to its methods)
                public bool IsModule;
                // after a bare `module_function' in the body: its methods are module functions
                public bool ModuleFunctions;
                // how deep in `def's the walk is in this body (a call there is not the body's)
                public int DefDepth;
                // the body's defs by name, the last one of each name
                public readonly Dictionary<string, MethodDefinition> Defs = new();
            }

            private Frame Top => _frames.Count == 0 ? null : _frames[_frames.Count - 1];
            private string Enclosing => Top?.Path;

            private static List<string> Segments(ConstantVariable c, out bool global) {
                var result = new List<string>();
                global = false;
                for (IronRuby.Compiler.Ast.Expression q = null; c != null; c = q as ConstantVariable) {
                    result.Insert(0, c.Name);
                    q = null;
                    if (c.IsGlobal) { global = true; break; }
                    if (c.Qualifier == null) break;
                    q = c.Qualifier as ConstantVariable;
                    if (q == null) return null; // a computed qualifier
                }
                return result;
            }

            // the module a `class'/`module' definition names
            private string Resolve(ConstantVariable name) {
                var segments = Segments(name, out bool global);
                if (segments == null) return null;
                string rest = string.Join("::", segments);
                if (global) return rest;
                if (segments.Count == 1) {
                    return Enclosing == null ? rest : Enclosing + "::" + rest;
                }
                string first = segments[0];
                for (int i = _frames.Count - 1; i >= 0; i--) {
                    string m = _frames[i].Path;
                    if (m == null) continue;
                    if (m == first || m.EndsWith("::" + first, StringComparison.Ordinal)) {
                        return m + rest.Substring(first.Length);
                    }
                    if (_known.Contains(m + "::" + first)) {
                        return m + "::" + rest;
                    }
                }
                return rest;
            }

            // the module a constant refers to (class << Time, def Time.now): a module the enclosing ones or the top level
            // have under that name, if the files compiled so far define it or the runtime has it; null for anything else
            // (ENV, a class made by Class.new), which the runtime labels by its own name or not at all
            private string ResolveReference(ConstantVariable name) {
                var segments = Segments(name, out bool global);
                if (segments == null) return null;
                string rest = string.Join("::", segments);
                if (!global) {
                    for (int i = _frames.Count - 1; i >= 0; i--) {
                        string m = _frames[i].Path;
                        if (m != null && _known.Contains(m + "::" + segments[0])) {
                            rest = m + "::" + rest;
                            break;
                        }
                    }
                }
                return _known.Contains(rest) || IsRuntimeModule(rest) ? rest : null;
            }

            private bool IsRuntimeModule(string path) {
                object value = _context.ObjectClass;
                foreach (string segment in path.Split("::")) {
                    if (!(value is RubyModule module) || !module.TryGetConstant(null, segment, out value)) return false;
                }
                return value is RubyModule;
            }

            private void Push(string path, bool singleton, bool isModule) {
                _frames.Add(new Frame { Path = path, Singleton = singleton, IsModule = isModule });
                if (path != null) _known.Add(path);
            }

            private void Pop() {
                _frames.RemoveAt(_frames.Count - 1);
            }

            public override bool Enter(ClassDefinition node) { Push(Resolve(node.QualifiedName), false, false); return true; }
            public override void Exit(ClassDefinition node) { Pop(); }
            public override bool Enter(ModuleDefinition node) { Push(Resolve(node.QualifiedName), false, true); return true; }
            public override void Exit(ModuleDefinition node) { Pop(); }
            public override bool Enter(SingletonDefinition node) {
                // class << self: the module itself, its methods singleton ones; class << Time: Time's; class << obj: unknown
                string path = node.Singleton is SelfReference ? Enclosing
                    : node.Singleton is ConstantVariable c ? ResolveReference(c)
                    : null;
                Push(path, path != null, false);
                return true;
            }
            public override void Exit(SingletonDefinition node) { Pop(); }

            private static bool IsModuleFunctionCall(string name) => name == "module_function" || name == "private_module_function";

            // a call in a module body (not in a def of it)
            private Frame BodyOf(MethodCall node) {
                var top = Top;
                return node.Target == null && top != null && top.DefDepth == 0 ? top : null;
            }

            public override bool Enter(SimpleAssignmentExpression node) {
                if (node.Left is ConstantVariable name && node.Right is MethodCall call && call.Block is BlockDefinition &&
                    call.Target is ConstantVariable maker && maker.Qualifier == null && !maker.IsGlobal &&
                    (call.MethodName == "new" && (maker.Name == "Class" || maker.Name == "Module" || maker.Name == "Struct") ||
                     call.MethodName == "define" && maker.Name == "Data")) {
                    // its methods are first called once the module has the constant's name, which is when the runtime labels them
                    _blockModules[call] = Resolve(name);
                }
                return true;
            }

            public override bool Enter(MethodCall node) {
                // the Walker leaves out a call's block: its defs are those of a module made there (X = Class.new do ... end),
                // or of a module only the run knows (class_eval, refine), labelled by their bare name here
                if (node.Block is BlockDefinition block) {
                    _blockModules.TryGetValue(node, out string module);
                    Push(module, false, module != null && ((ConstantVariable)node.Target).Name == "Module");
                    block.Walk(this);
                    Pop();
                }
                var body = BodyOf(node);
                if (body != null && (node.Arguments == null || node.Arguments.IsEmpty)) {
                    if (node.MethodName == "module_function") {
                        body.ModuleFunctions = body.IsModule;
                    } else if (node.MethodName == "public" || node.MethodName == "private" || node.MethodName == "protected") {
                        body.ModuleFunctions = false;
                    }
                }
                return true;
            }

            // module_function :a, :b, or module_function def a ... end: once the arguments are walked
            public override void Exit(MethodCall node) {
                var body = BodyOf(node);
                if (body == null || !body.IsModule || !IsModuleFunctionCall(node.MethodName) || node.Arguments == null) {
                    return;
                }
                foreach (var arg in node.Arguments.Expressions) {
                    MethodDefinition def = arg as MethodDefinition;
                    if (def == null && arg is StringLiteral literal && literal.Value is string name) {
                        body.Defs.TryGetValue(name, out def);
                    }
                    if (def != null && def.Target == null) {
                        _owners[LocationKey(def)] = ModuleFunctionLabel(body, def.Name);
                    }
                }
            }

            private string ModuleFunctionLabel(Frame body, string name) {
                if (body.Path == null) return name;
                int colons = body.Path.LastIndexOf("::", StringComparison.Ordinal);
                string last = colons < 0 ? body.Path : body.Path.Substring(colons + 2);
                return body.Path == "Kernel" || _extended.Contains(last) ? body.Path + "#" + name : body.Path + "." + name;
            }

            public override bool Enter(MethodDefinition node) {
                var top = Top;
                string owner = top == null ? "Object" : top.Path;
                string label;
                if (node.Target != null) {
                    string target = node.Target is SelfReference ? (owner != "Object" ? owner : null)
                        : node.Target is ConstantVariable c ? ResolveReference(c)
                        : null;
                    label = target != null ? target + "." + node.Name : node.Name;
                } else if (top != null && top.Singleton) {
                    label = owner != null ? owner + "." + node.Name : node.Name;
                } else if (top != null && top.ModuleFunctions && top.DefDepth == 0) {
                    label = ModuleFunctionLabel(top, node.Name);
                } else {
                    label = owner != null ? owner + "#" + node.Name : node.Name;
                }
                _owners[LocationKey(node)] = label;
                if (top != null) {
                    if (top.DefDepth == 0 && node.Target == null) top.Defs[node.Name] = node;
                    top.DefDepth++;
                }
                return true;
            }

            public override void Exit(MethodDefinition node) {
                var top = Top;
                if (top != null) top.DefDepth--;
            }
        }
    }
}

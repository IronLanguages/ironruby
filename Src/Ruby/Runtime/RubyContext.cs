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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Compiler.Generation;
using IronRuby.Hosting;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Collections.ObjectModel;
using System.Globalization;

namespace IronRuby.Runtime {
    [ReflectionCached]
    /// <summary>
    /// Something ObjectSpace.define_finalizer registered that has to run at exit if the object is still alive.
    /// </summary>
    public interface IExitFinalizer {
        void RunAtExit();
    }

    /// <summary>
    /// Hidden state kept among an object's instance variables that a copy of the object needs its
    /// own instance of rather than a shared one: MRI's dup and clone give the copy the original's
    /// finalizers, called with the copy's id.
    /// </summary>
    public interface IPerObjectState {
        object CopyFor(object copy);
    }

    [ReflectionCached]
    public sealed class RubyContext : LanguageContext {
        #region Constants

        internal static readonly Guid RubyLanguageGuid = new Guid("F03C4640-DABA-473f-96F1-391400714DAB");
        private static readonly Guid LanguageVendor_Microsoft = new Guid(-1723120188, -6423, 0x11d2, 0x90, 0x3f, 0, 0xc0, 0x4f, 0xa3, 2, 0xa1);
        private static int _RuntimeIdGenerator = 0;

        // MRI compliance: language level targeted by the prism front end.
        public string/*!*/ MriVersion {
            get { return MriVersionString; }
        }

        private const string MriVersionString = "4.0.0";

        // Names the site directory (ruby/site_ruby/1.9.1, where rb-readline lives), as
        // RbConfig's ruby_version does. The standard library itself is ruby/4.0.
        public string/*!*/ StandardLibraryVersion {
            get { return "1.9.1"; }
        }

        public string/*!*/ MriReleaseDate {
            get { return "2025-12-25"; }
        }

        public int MriPatchLevel {
            get { return 0; }
        }

        public const string BinDirEnvironmentVariable = "IRONRUBY_11";

        // IronRuby:
        public const string IronRubyInformationalVersion = "1.1.3";
        internal const string/*!*/ IronRubyDisplayName = "IronRuby";
        internal const string/*!*/ IronRubyNames = "IronRuby;Ruby;rb";
        internal const string/*!*/ IronRubyFileExtensions = ".rb";

        #endregion

        // TODO: remove
        internal static RubyContext _Default;

        private readonly int _runtimeId;
        private readonly RubyScope/*!*/ _emptyScope;

        private RubyOptions/*!*/ _options;
        private readonly TopNamespaceTracker _namespaces;
        private readonly Loader/*!*/ _loader;
        private readonly Scope/*!*/ _globalScope;
        private RubyGlobalScope _mainGlobalScope;
        private readonly RubyMetaBinderFactory/*!*/ _metaBinderFactory;
        private readonly RubyBinder _binder;
        private DynamicDelegateCreator _delegateCreator;
        private RubyService _rubyService;

        #region Global Variables (thread-safe access)

        /// <summary>
        /// $0
        /// </summary>
        public MutableString CommandLineProgramPath { get; set; }

        /// <summary>
        /// Changes the operating-system process title without changing Ruby's $0. Process.setproctitle
        /// has that contract, while assigning $0 changes both values.
        /// </summary>
        public void SetNativeProcessTitle(MutableString/*!*/ title) {
            SpecialGlobalVariableInfo.SetProcessTitle(title.ToByteArray());
        }

        /// <summary>
        /// $? of type Process::Status
        /// </summary>
        [ThreadStatic]
        private static object _childProcessExitStatus;

        /// <summary>
        /// $/, $-O
        /// </summary>
        private MutableString _inputSeparator;

        /// <summary>
        /// $\
        /// </summary>
        private MutableString _outputSeparator;

        /// <summary>
        /// $;, $-F
        /// </summary>
        private object _stringSeparator;

        /// <summary>
        /// $,
        /// </summary>
        private MutableString _itemSeparator;

        private readonly Dictionary<string/*!*/, GlobalVariable>/*!*/ _globalVariables;
        public object/*!*/ GlobalVariablesLock { get { return _globalVariables; } }

        /// <summary>
        /// Changes whenever a name starts or stops denoting a GlobalVariable object - a define,
        /// an alias, a delete, the first assignment of a new global - but not when a value is
        /// assigned. A GlobalVariableSite keeps the object it found until this moves.
        /// Written under GlobalVariablesLock (or before any Ruby code runs).
        /// </summary>
        private volatile int _globalVariablesVersion = GlobalVariableSite.NextVersion();
        internal int GlobalVariablesVersion { get { return _globalVariablesVersion; } }

        private void GlobalVariablesChanged() {
            _globalVariablesVersion = GlobalVariableSite.NextVersion();
        }

        // not thread safe: use GlobalVariablesLock to synchronize access to the variables:
        public IEnumerable<KeyValuePair<string, GlobalVariable>>/*!*/ GlobalVariables {
            get { return _globalVariables; }
        }
        
        #endregion

        #region Random Number Generator

        private readonly object _randomNumberGeneratorLock = new object();
        private Random _randomNumberGenerator; // lazy
        private object _randomNumberGeneratorSeed = ScriptingRuntimeHelpers.Int32ToObject(0);

        public object RandomNumberGeneratorSeed {
            get { return _randomNumberGeneratorSeed; }
        }

        public void SeedRandomNumberGenerator(IntegerValue value) {
            lock (_randomNumberGeneratorLock) {
                _randomNumberGenerator = new Random(value.IsFixnum ? value.Fixnum : value.Bignum.GetHashCode());
                _randomNumberGeneratorSeed = value.ToObject();
            }
        }

        public Random/*!*/ RandomNumberGenerator {
            get {
                if (_randomNumberGenerator == null) {
                    lock (_randomNumberGeneratorLock) {
                        if (_randomNumberGenerator == null) {
                            _randomNumberGenerator = new Random();
                        }
                    }
                }
                return _randomNumberGenerator;
            }
        }

        #endregion

        #region Threading

        // Thread#main
        private readonly Thread _mainThread;

        // Thread#critical=
        // We just need a bool. But we store the Thread object for easier debugging if there is a hang
        private Thread _criticalThread;
        private readonly object _criticalMonitor = new object();

        #endregion

        #region Tracing

        private readonly RubyInputProvider/*!*/ _inputProvider;
        private Proc _traceListener;

        // Set while the trace proc runs on this thread, so that what the proc does is not traced itself.
        [ThreadStatic]
        private static bool _traceListenerSuspended;
        
        private readonly Stopwatch _upTime;

        // TODO: thread-safety
        internal Action<Expression, MSA.DynamicExpression> CallSiteCreated { get; set; }

        #endregion

        #region Look-aside tables

        /// <summary>
        /// Maps CLR types to Ruby classes/modules.
        /// Doesn't contain classes defined in Ruby.
        /// </summary>
        private readonly Dictionary<Type, RubyModule>/*!*/ _moduleCache;
        private object ModuleCacheLock { get { return _moduleCache; } }

        /// <summary>
        /// Maps CLR namespace trackers to Ruby modules.
        /// </summary>
        private readonly Dictionary<NamespaceTracker, RubyModule>/*!*/ _namespaceCache;
        private object NamespaceCacheLock { get { return _namespaceCache; } }

        // Maps objects to InstanceData. The keys store weak references to the objects.
        // Objects are compared by reference (identity). 
        // An entry can be removed as soon as the key object becomes unreachable.
        // A ConditionalWeakTable: reads are lock-free, which matters because every class lookup
        // of a CLR-backed object (String, Array, Hash...) that may have a singleton asks it.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, RubyInstanceData>/*!*/ _referenceTypeInstanceData;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, RubyInstanceData>.CreateValueCallback/*!*/ _CreateInstanceData =
            _ => new RubyInstanceData();

        // Maps values to InstanceData. The keys store value representatives. 
        // All objects that has the same value (value-equality) map to the same InstanceData.
        // Entries cannot be ever freed since anytime in future one may create a new object whose value has already been mapped to InstanceData.
        private readonly Dictionary<object, RubyInstanceData>/*!*/ _valueTypeInstanceData;
        private object/*!*/ ValueTypeInstanceDataLock { get { return _valueTypeInstanceData; } }

        // The number of entries in _valueTypeInstanceData, published after each add. Entries are
        // never removed, so zero means there is nothing to find: then a lookup - every
        // `1.is_a?(Hash)` asks for the immediate class of an Integer - takes no lock.
        private int _valueTypeInstanceDataCount;

        // not thread-safe: 
        private readonly RubyInstanceData/*!*/ _nilInstanceData = new RubyInstanceData(RubyUtils.NilObjectId);

        #endregion

        #region Class Hierarchy

        public IDisposable/*!*/ ClassHierarchyLocker() {
            return _classHierarchyLock.CreateLocker();
        }

        public IDisposable/*!*/ ClassHierarchyUnlocker() {
            return _classHierarchyLock.CreateUnlocker();
        }

        private readonly CheckedMonitor/*!*/ _classHierarchyLock = new CheckedMonitor();

        [Emitted]
        public int ConstantAccessVersion = 1;

        // Number of autoloads running anywhere in this runtime. While one is in flight the
        // answer a constant site gets depends on which thread is asking -- the thread running
        // the file sees the constant as undefined and every other thread does not -- so the
        // per-site caches, which are keyed only on ConstantAccessVersion, must not be filled.
        private int _autoloadsInProgress;

        public bool IsAutoloadInProgress {
            get { return System.Threading.Volatile.Read(ref _autoloadsInProgress) != 0; }
        }

        public void EnterAutoload() {
            System.Threading.Interlocked.Increment(ref _autoloadsInProgress);
            ConstantAccessVersion++;
        }

        public void LeaveAutoload() {
            System.Threading.Interlocked.Decrement(ref _autoloadsInProgress);
            ConstantAccessVersion++;
        }


        #region Refinements

        // Bumped by every successful `using'.  RubyScope memoizes its effective RefinementActivation and
        // recomputes it when this changes.  The recomputed table is the same instance whenever its contents
        // are (RefinementActivation interns them), so a `using' only fails the call-site rules guarded on
        // a table it actually changed.
        internal int RefinementVersion = 1;

        // Every module that Module#refine has ever been called on.  Empty in a program that never calls
        // refine, which is what keeps the whole feature off the fast path.
        private readonly Dictionary<RubyModule, bool> _refinedModules = new Dictionary<RubyModule, bool>();

        // Set by the first `using' of a CLR namespace or static class (see ActivateScopedExtensions): from
        // then on lexical activations matter even if nothing is refined.
        private bool _hasScopedExtensions;

        // { module -> the names a refinement of it, or an extension method of it some `using' of a CLR
        // namespace has brought in, has (or had) }.  This is what decides whether a call can depend on
        // where it is made at all - CRuby's REFINED method entry: a refinement of String that defines
        // #shout makes `x.shout' a refined call where x is a String, and leaves `x.upcase' and every call
        // on an Integer alone.  Only ever grows.
        private readonly Dictionary<RubyModule, HashSet<string>> _refinedMethodNames = new Dictionary<RubyModule, HashSet<string>>();

        // Every name in _refinedMethodNames, read without the class-hierarchy lock; copy-on-write.
        private volatile HashSet<string> _allRefinedMethodNames = new HashSet<string>();

        // RefinementActivation's intern table (see RefinementActivation.Intern).
        internal readonly Dictionary<RefinementActivation, RefinementActivation> RefinementActivations =
            new Dictionary<RefinementActivation, RefinementActivation>(RefinementActivation.ContentComparer);

        /// <summary>
        /// True once anything has been refined or a CLR namespace `using'd: from then on what a `using'
        /// activates is recorded where methods are defined and asked by the calls that can depend on it.
        /// </summary>
        internal bool HasRefinements {
            get { return _refinedModules.Count > 0 || _hasScopedExtensions; }
        }

        private RubyClass _refinementClass;

        /// <summary>
        /// The Refinement class, i.e. the class of the module Module#refine returns.  Looked up lazily by
        /// constant because it is an ordinary library class with no CLR type of its own.
        /// </summary>
        internal RubyClass/*!*/ RefinementClass {
            get {
                if (_refinementClass == null) {
                    InitializeRefinementClass();
                }
                return _refinementClass ?? ModuleClass;
            }
        }

        internal void RegisterRefinedModule(RubyModule/*!*/ refinedModule) {
            RequiresClassHierarchyLock();
            if (_refinedModules.ContainsKey(refinedModule)) {
                return;
            }
            // A refinement that defines nothing changes no call: the call sites it can affect are
            // invalidated name by name, as methods arrive in it (RefinedMethodChanged).
            _refinedModules.Add(refinedModule, true);
        }

        /// <summary>
        /// A refinement of <paramref name="refinedModule"/> defined, redefined, undefined or removed its
        /// method <paramref name="name"/>.  The call sites that resolved the name on the module or below it
        /// were bound without the refinement guard (the name was not refined yet) or against the method it
        /// had before, so they re-bind - CRuby clears the method cache of the refined class for the name.
        /// </summary>
        internal void RefinedMethodChanged(RubyModule/*!*/ refinedModule, string/*!*/ name) {
            RequiresClassHierarchyLock();
            AddLexicalMethodName(refinedModule, name);
            refinedModule.AllDependentMethodsUpdated("RefinedMethod: " + name);
        }

        /// <summary>
        /// Extension methods of <paramref name="module"/> named <paramref name="names"/> can now be reached by a
        /// call made where their namespace is `using'd: the calls of those names on the module and below it
        /// re-bind, now with the lexical guard.
        /// </summary>
        internal void LexicalMethodsAdded(RubyModule/*!*/ module, ICollection<string>/*!*/ names, string/*!*/ reason) {
            RequiresClassHierarchyLock();
            if (names.Count == 0) {
                return;
            }
            foreach (string name in names) {
                AddLexicalMethodName(module, name);
            }
            module.AllDependentMethodsUpdated(reason);
        }

        private void AddLexicalMethodName(RubyModule/*!*/ module, string/*!*/ name) {
            HashSet<string> names;
            if (!_refinedMethodNames.TryGetValue(module, out names)) {
                _refinedMethodNames.Add(module, names = new HashSet<string>());
            }
            if (names.Add(name) && !_allRefinedMethodNames.Contains(name)) {
                var all = new HashSet<string>(_allRefinedMethodNames);
                all.Add(name);
                _allRefinedMethodNames = all;
            }
        }

        /// <summary>
        /// True if a call of <paramref name="name"/> on an instance of <paramref name="cls"/> could be
        /// affected by a refinement: some refinement of the class or of one of its ancestors has a method
        /// of that name (or a `using'd CLR namespace has an extension method of that name for one of them).  Whether one is active where the call is made is the call site's question, which
        /// only call sites that answer true here ask; all the others are built - and dispatched, and
        /// JIT-compiled - exactly as if nothing were refined.
        /// </summary>
        internal bool IsRefinementSensitive(RubyModule/*!*/ cls, string/*!*/ name) {
            RequiresClassHierarchyLock();
            if (!IsRefinedMethodName(name)) {
                return false;
            }
            return cls.ForEachAncestor(true, (m) => {
                HashSet<string> names;
                return _refinedMethodNames.TryGetValue(m, out names) && names.Contains(name);
            });
        }

        /// <summary>
        /// False if no refinement anywhere has a method <paramref name="name"/>: no call of the name can be a
        /// refined call.  Lock-free, for the checks made on every execution of an operation rather than once
        /// per call-site rule.
        /// </summary>
        public bool IsRefinedMethodName(string/*!*/ name) {
            var names = _allRefinedMethodNames;
            return names.Count != 0 && names.Contains(name);
        }

        #endregion

        [Conditional("DEBUG")]
        internal void RequiresClassHierarchyLock() {
            Debug.Assert(_classHierarchyLock.IsLocked, "Code can only be executed while holding class hierarchy lock.");
        }

        // classes used by runtime (we need to update initialization generator if any of these are added):
        private RubyClass/*!*/ _basicObjectClass;
        private RubyModule/*!*/ _kernelModule;
        private RubyClass/*!*/ _objectClass;
        private RubyClass/*!*/ _classClass;
        private RubyClass/*!*/ _moduleClass;
        private RubyClass/*!*/ _nilClass;
        private RubyClass/*!*/ _trueClass;
        private RubyClass/*!*/ _falseClass;
        private RubyClass/*!*/ _exceptionClass;
        private RubyClass _standardErrorClass;
        private RubyClass _comObjectClass;
        private RubyClass _integerClass;

        private Action<RubyModule>/*!*/ _mainSingletonTrait;

        // internally set by Initializer:
        public RubyClass/*!*/ BasicObjectClass { get { return _basicObjectClass; } }
        public RubyModule/*!*/ KernelModule { get { return _kernelModule; } }
        public RubyClass/*!*/ ObjectClass { get { return _objectClass; } }
        public RubyClass/*!*/ ClassClass { get { return _classClass; } set { _classClass = value; } }
        public RubyClass/*!*/ ModuleClass { get { return _moduleClass; } set { _moduleClass = value; } }
        public RubyClass/*!*/ NilClass { get { return _nilClass; } set { _nilClass = value; } }
        public RubyClass/*!*/ TrueClass { get { return _trueClass; } set { _trueClass = value; } }
        public RubyClass/*!*/ FalseClass { get { return _falseClass; } set { _falseClass = value; } }
        public RubyClass ExceptionClass { get { return _exceptionClass; } set { _exceptionClass = value; } }
        public RubyClass StandardErrorClass { get { return _standardErrorClass; } set { _standardErrorClass = value; } }
        public RubyClass IntegerClass { get { return _integerClass; } }
        
        internal RubyClass ComObjectClass {
            get {
                if (_comObjectClass == null) {
                    GetOrCreateClass(TypeUtils.ComObjectType);
                }
                return _comObjectClass;
            }
        }

        // Set of names that method_missing defined on any module was resolved for and that are cached. Lazy init.
        // 
        // Note: We used to have this set per module but that doesn't work - see unit test MethodCallCaching_MethodMissing4.
        // Whenever a method is added to a class C we would need to traverse all its subclasses to see if any of them 
        // has the added method in its MissingMethodsCachedInSites table. 
        // TODO: Could we optimize this search? If so we could also free the per-module set if mm is removed.
        internal HashSet<string> MissingMethodsCachedInSites { get; set; }

        // How many library method tables are being populated right now (guarded by ClassHierarchyLock):
        // RubyModule's method-lookup cache is not filled meanwhile.
        internal int MethodTableInitializationDepth;

        #endregion

        #region Properties

        public PlatformAdaptationLayer/*!*/ Platform {
            get { return DomainManager.Platform; }
        }

        public override LanguageOptions Options {
            get { return _options; }
        }

        public RubyOptions RubyOptions {
            get { return _options; }
        }

        internal RubyScope/*!*/ EmptyScope {
            get { return _emptyScope; }
        }

        public Thread MainThread {
            get { return _mainThread; }
        }

        public MutableString InputSeparator {
            get { return _inputSeparator; }
            set { _inputSeparator = value; }
        }

        public MutableString OutputSeparator {
            get { return _outputSeparator; }
            set { _outputSeparator = value; }
        }

        public object StringSeparator {
            get { return _stringSeparator; }
            set { _stringSeparator = value; }
        }

        public MutableString ItemSeparator {
            get { return _itemSeparator; }
            set { _itemSeparator = value; }
        }

        public object CriticalMonitor {
            get { return _criticalMonitor; }
        }

        public Thread CriticalThread {
            get { return _criticalThread; }
            set { _criticalThread = value; }
        }

        public Proc TraceListener {
            get { return _traceListener; }
            set { _traceListener = value; }
        }

        public RubyInputProvider/*!*/ InputProvider {
            get { return _inputProvider; }
        }

        public object ChildProcessExitStatus {
            get { return _childProcessExitStatus; }
            set { _childProcessExitStatus = value; }
        }

        public Scope/*!*/ TopGlobalScope {
            get { return _globalScope; }
        }

        /// <summary>
        /// The global scope of the program that is running - the main script's, or the first one
        /// created if the runtime is hosted. It is the fallback for call sites that carry no Ruby
        /// frame and therefore run against <see cref="EmptyScope"/>. Null until one exists.
        /// </summary>
        internal RubyGlobalScope MainGlobalScope {
            get { return _mainGlobalScope; }
            set { _mainGlobalScope = value; }
        }

        internal RubyMetaBinderFactory/*!*/ MetaBinderFactory {
            get { return _metaBinderFactory; }
        }

        public Loader/*!*/ Loader {
            get { return _loader; }
        }

        public bool ShowCls {
            get { return false; }
        }

        public EqualityComparer/*!*/ EqualityComparer {
            get {
                if (_equalityComparer == null) {
                    _equalityComparer = new EqualityComparer(this);
                }
                return _equalityComparer;
            }
        }
        private EqualityComparer _equalityComparer;

        public override Version LanguageVersion {
            get { return new Version(IronRuby.CurrentVersion.Major, IronRuby.CurrentVersion.Minor, IronRuby.CurrentVersion.Micro); }
        }

        public override Guid LanguageGuid {
            get { return RubyLanguageGuid; }
        }

        public override Guid VendorGuid {
            get { return LanguageVendor_Microsoft; }
        }

        public int RuntimeId {
            get { return _runtimeId; }
        }

        internal TopNamespaceTracker/*!*/ Namespaces {
            get { return _namespaces; }
        }

        public object Verbose { get; set; }

        private RubyEncoding/*!*/ _defaultExternalEncoding;

        public RubyEncoding/*!*/ DefaultExternalEncoding {
            get { return _defaultExternalEncoding; }
            set {
                ContractUtils.RequiresNotNull(value, "value");
                _defaultExternalEncoding = value;
            }
        }

        public RubyEncoding DefaultInternalEncoding { get; set; }
        
        #endregion

        #region Initialization

        public RubyContext(ScriptDomainManager/*!*/ manager, IDictionary<string, object> options)
            : base(manager) {
            ContractUtils.RequiresNotNull(manager, "manager");
            _options = new RubyOptions(options);
            if (_options.ObjectSpace) {
                ObjectSpaceObjects = new ObjectSpaceRegistry();
            }

            _runtimeId = Interlocked.Increment(ref _RuntimeIdGenerator);
            _upTime = new Stopwatch();
            _upTime.Start();
            
            _binder = new RubyBinder(this);

            _symbols = new Dictionary<MutableString, RubySymbol>();
            _metaBinderFactory = new RubyMetaBinderFactory(this);
            _runtimeErrorSink = new RuntimeErrorSink(this);
            _equalityComparer = new EqualityComparer(this);
            _globalVariables = new Dictionary<string, GlobalVariable>();
            _moduleCache = new Dictionary<Type, RubyModule>();
            _namespaceCache = new Dictionary<NamespaceTracker, RubyModule>();
            _referenceTypeInstanceData = new System.Runtime.CompilerServices.ConditionalWeakTable<object, RubyInstanceData>();
            _valueTypeInstanceData = new Dictionary<object, RubyInstanceData>();
            // MRI 3.0+ on Windows: the default external encoding is UTF-8 whatever the console's
            // code page, which stays the locale encoding - and ARGV is in it too; a .NET argument
            // is Unicode, so nothing is lost.
            bool windows = Path.DirectorySeparatorChar == '\\';
            _inputProvider = new RubyInputProvider(this, _options.Arguments, windows ? RubyEncoding.UTF8 : _options.LocaleEncoding);
            _defaultExternalEncoding = _options.DefaultEncoding ?? (windows ? RubyEncoding.UTF8 : _options.LocaleEncoding);
            // -E / --encoding, resolved here rather than in the options parser because looking an
            // encoding up by its Ruby name needs a context. It overrides both -K and the locale:
            // that is the whole point of the option.
            if (_options.ExternalEncodingName != null) {
                _defaultExternalEncoding = GetCommandLineEncoding(_options.ExternalEncodingName);
            }
            if (_options.InternalEncodingName != null) {
                DefaultInternalEncoding = GetCommandLineEncoding(_options.InternalEncodingName);
            }
            _globalScope = DomainManager.Globals;
            _loader = new Loader(this);
            _emptyScope = new RubyTopLevelScope(this);            
            _currentException = null;
            _currentSafeLevel = 0;
            _childProcessExitStatus = null;
            // -0<octal> names the record separator; -l then copies it to $\ so that puts and
            // print put back what the loop chomped off.
            _inputSeparator = _options.NoInputRecordSeparator ? null
                : CreateInputSeparator(_options.InputRecordSeparator ?? "\n").Freeze();
            _outputSeparator = _options.ChopLines ? _inputSeparator : null;
            // -F names $; - as a Regexp, the way MRI compiles it.
            _stringSeparator = _options.FieldSeparator != null
                ? new RubyRegex(MutableString.Create(_options.FieldSeparator, RubyEncoding.UTF8), RubyRegexOptions.NONE)
                : null;
            _itemSeparator = null;
            _mainThread = Thread.CurrentThread;
            
            if (_options.MainFile != null) {
                CommandLineProgramPath = EncodePath(_options.MainFile);
            }

            if (_options.Verbosity <= 0) {
                Verbose = null;
            } else if (_options.Verbosity == 1) {
                Verbose = ScriptingRuntimeHelpers.False; 
            } else {
                Verbose = ScriptingRuntimeHelpers.True;
            }

            _namespaces = new TopNamespaceTracker(manager);
            manager.AssemblyLoaded += new EventHandler<AssemblyLoadedEventArgs>((_, e) => AssemblyLoaded(e.Assembly));
            foreach (Assembly asm in manager.GetLoadedAssemblyList()) {
                AssemblyLoaded(asm);
            }

            // TODO:
            Interlocked.CompareExchange(ref _Default, this, null);

            _loader.LoadBuiltins();
            Debug.Assert(_exceptionClass != null && _standardErrorClass != null && _nilClass != null);

            // Ruby 2.4 unified Fixnum and Bignum into Integer, so both CLR representations of an
            // integer have to report the same Ruby class.  Integer is declared as extending
            // System.Int32; alias System.Numerics.BigInteger onto that very same RubyClass so
            // GetClassOf() answers Integer for a bignum too.  Object is aliased the same way for
            // RubyObject in InitializeCoreClasses.
            using (ClassHierarchyLocker()) {
                lock (ModuleCacheLock) {
                    RubyClass integerClass;
                    if (TryGetClassNoLock(typeof(int), out integerClass)) {
                        AddModuleToCacheNoLock(typeof(BigInteger), integerClass);
                        // System.Int64 is the third representation: it carries every value that
                        // does not fit in an Int32 but still fits in CRuby's 63-bit Fixnum range
                        // (and a little beyond).  It is aliased the same way, so a long answers
                        // Integer too.  This overwrites the System::Int64 class the Int64Ops
                        // library registered; that class object is still reachable through the
                        // System::Int64 constant for CLR interop, it just no longer claims
                        // instances.
                        ReplaceModuleInCacheNoLock(typeof(long), integerClass);
                        _integerClass = integerClass;
                    }
                }
            }

            Debug.Assert(_classClass != null && _moduleClass != null);
            
            // needs to run before globals and constants are initialized:
            InitializeFileDescriptors(DomainManager.SharedIO);

            InitializeGlobalConstants();
            InitializeGlobalVariables();
            InitializeWarningCategories();

            // MutableString's mutation guard has no way to find a runtime, so it calls back here.
            MutableString.ChilledMutationReporter = ReportChilledStringMutation;

            // Refinement inherits Module's ancestor-splicing hooks and must not keep them; they have to go
            // before any user code can ask Refinement.private_instance_methods.
            InitializeRefinementClass();
            StartupTrace.Mark("RubyContext created (core classes set up)");
        }

        private void InitializeRefinementClass() {
            object value;
            RubyClass cls = ObjectClass.TryGetConstant(null, "Refinement", out value) ? value as RubyClass : null;
            if (cls == null) {
                return;
            }

            // A refinement is never spliced into anyone's ancestors, so the Module hooks that do the
            // splicing must not be callable on one.  CRuby removes them outright.
            cls.UndefineMethodNoEvent("append_features");
            cls.UndefineMethodNoEvent("prepend_features");
            cls.UndefineMethodNoEvent("extend_object");
            _refinementClass = cls;
        }

        internal RubyBinder/*!*/ Binder {
            get { return _binder; }
        }

        /// <summary>
        /// Clears thread static variables.
        /// </summary>
        internal static void ClearThreadStatics() {
            _currentException = null;
        }

        private void InitializeGlobalVariables() {
            // special variables:
            Runtime.GlobalVariables.DefineVariablesNoLock(this);

            // TODO:
            // $-a
            // $F
            // $-i

            // $?


            // $0
            DefineGlobalVariableNoLock("PROGRAM_NAME", Runtime.GlobalVariables.CommandLineProgramPath);

            DefineGlobalVariableNoLock("stdin", Runtime.GlobalVariables.InputStream);
            DefineGlobalVariableNoLock("stdout", Runtime.GlobalVariables.OutputStream);
            DefineGlobalVariableNoLock("defout", Runtime.GlobalVariables.OutputStream);
            DefineGlobalVariableNoLock("stderr", Runtime.GlobalVariables.ErrorOutputStream);

            DefineGlobalVariableNoLock("LOADED_FEATURES", Runtime.GlobalVariables.LoadedFiles);
            DefineGlobalVariableNoLock("LOAD_PATH", Runtime.GlobalVariables.LoadPath);
            DefineGlobalVariableNoLock("-I", Runtime.GlobalVariables.LoadPath);
            DefineGlobalVariableNoLock("-0", Runtime.GlobalVariables.InputSeparator);
            DefineGlobalVariableNoLock("-O", Runtime.GlobalVariables.InputSeparator);
            DefineGlobalVariableNoLock("-a", new ReadOnlyGlobalVariableInfo(RubyOptions.AutoSplit));
            DefineGlobalVariableNoLock("-l", new ReadOnlyGlobalVariableInfo(RubyOptions.ChopLines));
            DefineGlobalVariableNoLock("-p", new ReadOnlyGlobalVariableInfo(RubyOptions.PrintEachLine));
            DefineGlobalVariableNoLock("-F", Runtime.GlobalVariables.StringSeparator);
            DefineGlobalVariableNoLock("FILENAME", Runtime.GlobalVariables.InputFileName);

            GlobalVariableInfo debug = new GlobalVariableInfo(DomainManager.Configuration.DebugMode || RubyOptions.DebugVariable);

            DefineGlobalVariableNoLock("VERBOSE", Runtime.GlobalVariables.Verbose);
            DefineGlobalVariableNoLock("-v", Runtime.GlobalVariables.Verbose);
            DefineGlobalVariableNoLock("-w", Runtime.GlobalVariables.Verbose);
            DefineGlobalVariableNoLock("-W", Runtime.GlobalVariables.WarningLevel);
            DefineGlobalVariableNoLock("DEBUG", debug);
            DefineGlobalVariableNoLock("-d", debug);

            DefineGlobalVariableNoLock("KCODE", Runtime.GlobalVariables.KCode);
            DefineGlobalVariableNoLock("-K", Runtime.GlobalVariables.KCode);

            // $=: removed from Ruby long ago, but still defined so that reading or writing it
            // produces the deprecation warning rather than an unknown-global nil.
            DefineGlobalVariableNoLock("=", Runtime.GlobalVariables.IgnoreCase);

            // $SAFE is an ordinary global since Ruby 3.0, so it is not defined here.

            try {
                TrySetCurrentProcessVariables();
            } catch (SecurityException) {
                // nop
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
        private void TrySetCurrentProcessVariables() {
            Process process = Process.GetCurrentProcess();
            DefineGlobalVariableNoLock(Symbols.CurrentProcessId, new ReadOnlyGlobalVariableInfo(process.Id));
        }

        private void InitializeGlobalConstants() {
            Debug.Assert(_objectClass != null);

            // MRI freezes every RUBY_* string constant; ruby/spec asserts it directly
            // (spec/core/builtin_constants), and user code relies on being able to hand
            // them out without defensive dups.
            MutableString version = MutableString.CreateAscii(MriVersion).Freeze();
            MutableString platform = MakePlatformString().Freeze();

            MutableString releaseDate = MutableString.CreateAscii(MriReleaseDate).Freeze();
            MutableString rubyEngine = MutableString.CreateAscii("ironruby").Freeze();
            MutableString revision = MutableString.CreateAscii(MakeRevisionString()).Freeze();

            using (ClassHierarchyLocker()) {
                RubyClass obj = _objectClass;

                obj.SetConstantNoMutateNoLock("RUBY_ENGINE", rubyEngine);
                obj.SetConstantNoMutateNoLock("RUBY_VERSION", version);
                obj.SetConstantNoMutateNoLock("RUBY_PATCHLEVEL", MriPatchLevel);
                obj.SetConstantNoMutateNoLock("RUBY_PLATFORM", platform);
                obj.SetConstantNoMutateNoLock("RUBY_RELEASE_DATE", releaseDate);
                obj.SetConstantNoMutateNoLock("RUBY_DESCRIPTION", MutableString.CreateAscii(MakeDescriptionString()).Freeze());
                obj.SetConstantNoMutateNoLock("RUBY_REVISION", revision);

                obj.SetConstantNoMutateNoLock("VERSION", version);
                obj.SetConstantNoMutateNoLock("PLATFORM", platform);
                obj.SetConstantNoMutateNoLock("RELEASE_DATE", releaseDate);

                obj.SetConstantNoMutateNoLock("IRONRUBY_VERSION", MutableString.CreateAscii(IronRuby.CurrentVersion.DisplayVersion));

                obj.SetConstantNoMutateNoLock("STDIN", StandardInput);
                obj.SetConstantNoMutateNoLock("STDOUT", StandardOutput);
                obj.SetConstantNoMutateNoLock("STDERR", StandardErrorOutput);

                ConstantStorage argf;
                if (obj.TryGetConstantNoAutoloadCheck("ARGF", out argf)) {
                    _inputProvider.Singleton = argf.Value;
                }

                obj.SetConstantNoMutateNoLock("ARGV", _inputProvider.CommandLineArguments);

                if (_options.InplaceMode != null) {
                    // CRuby reports -i's extension as $-i, which is where ARGF looks for it.
                    _globalVariables["-i"] = new GlobalVariableInfo(
                        MutableString.Create(_options.InplaceMode, GetPathEncoding()).Freeze()
                    );
                } else {
                    _globalVariables["-i"] = new GlobalVariableInfo(null);
                }
                GlobalVariablesChanged();

                // Hash
                // SCRIPT_LINES__
            }
        }

        /// <summary>
        /// MRI's RUBY_REVISION is the git commit the interpreter was built from. The
        /// closest equivalent here is the source-revision suffix SourceLink writes into
        /// AssemblyInformationalVersion ("1.2.0-dev+&lt;sha&gt;"); when the assembly was
        /// built without it we report the empty string rather than omitting the constant,
        /// so that `defined?(RUBY_REVISION)` and RUBY_REVISION.is_a?(String) both hold.
        /// </summary>
        public static string/*!*/ MakeRevisionString() {
            try {
                var attribute = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    typeof(RubyContext).Assembly, typeof(AssemblyInformationalVersionAttribute)
                );

                if (attribute != null) {
                    string informationalVersion = attribute.InformationalVersion;
                    int plus = informationalVersion.IndexOf('+');
                    if (plus >= 0 && plus + 1 < informationalVersion.Length) {
                        return informationalVersion.Substring(plus + 1);
                    }
                }
            } catch (Exception) {
                // reflection over our own assembly metadata must never stop the runtime booting
            }

            return String.Empty;
        }

        // Like MRI's ("ruby 4.0.6 (...) [x86_64-linux]") and JRuby's, the description
        // names the language version and RUBY_PLATFORM.
        public static string/*!*/ MakeDescriptionString() {
            return String.Format(CultureInfo.InvariantCulture, "IronRuby {0} ({1}) on {2} [{3}]",
                IronRuby.CurrentVersion.DisplayVersion, MriVersionString, MakeRuntimeDesriptionString(), MakePlatformName()
            );
        }

        internal static string MakeRuntimeDesriptionString() {
            Type mono = typeof(object).Assembly.GetType("Mono.Runtime");
            return mono != null ?
                (string)mono.GetMethod("GetDisplayName", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null)
                : String.Format(CultureInfo.InvariantCulture, ".NET {0}", Environment.Version);
        }

        private static MutableString/*!*/ MakePlatformString() {
            return MutableString.CreateAscii(MakePlatformName());
        }

        private static string/*!*/ MakePlatformName() {
            switch (Environment.OSVersion.Platform) {
                case PlatformID.MacOSX:
                    return MakeUnixCpuName(true) + "-darwin";

                case PlatformID.Unix:
                    // .NET reports macOS as Unix as well
                    return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ?
                        MakeUnixCpuName(true) + "-darwin" : MakeUnixCpuName(false) + "-linux";

                case PlatformID.Win32NT:
                case PlatformID.Win32S:
                case PlatformID.Win32Windows:
                    // MRI's own Windows builds are x64-mswin64 (MSVC) or x64-mingw-ucrt
                    // (RubyInstaller); "i386-mswin32" was a constant from the days when
                    // there was only one, and it told a 64-bit process it was 32-bit.
                    return MakeWindowsPlatformName();

                default:
                    return "unknown";
            }
        }

        /// <summary>RUBY_PLATFORM as MRI's MSVC Windows builds spell it: x64-mswin64 on a
        /// 64-bit process, i386-mswin32 on a 32-bit one.</summary>
        private static string/*!*/ MakeWindowsPlatformName() {
            switch (RuntimeInformation.ProcessArchitecture) {
                case Architecture.X64: return "x64-mswin64";
                case Architecture.Arm64: return "arm64-mswin64";
                default: return "i386-mswin32";
            }
        }

        // The CPU half of RUBY_PLATFORM is the architecture the process runs as, spelled
        // the way MRI's configure (config.guess) spells it; RbConfig's host_cpu matches it.
        private static string/*!*/ MakeUnixCpuName(bool darwin) {
            switch (RuntimeInformation.ProcessArchitecture) {
                case Architecture.X64: return "x86_64";
                case Architecture.Arm64: return darwin ? "arm64" : "aarch64";
                case Architecture.Arm: return "arm";
                default: return "i386";
            }
        }

        private void InitializeFileDescriptors(SharedIO/*!*/ io) {
            Debug.Assert(_fileDescriptors.Count == 0);
            Stream stream = new ConsoleStream(io, ConsoleStreamType.Input);                
            StandardInput = new RubyIO(this, stream, AllocateFileDescriptor(stream), IOMode.ReadOnly);
            stream = new ConsoleStream(io, ConsoleStreamType.Output);
            StandardOutput = new RubyIO(this, stream, AllocateFileDescriptor(stream), IOMode.WriteOnly | IOMode.WriteAppends);
            stream = new ConsoleStream(io, ConsoleStreamType.ErrorOutput);
            StandardErrorOutput = new RubyIO(this, stream, AllocateFileDescriptor(stream), IOMode.WriteOnly | IOMode.WriteAppends);
            // STDERR.sync is true from the start in MRI: a diagnostic should not wait in a buffer
            ((RubyIO)StandardErrorOutput).AutoFlush = true;
        }

        // TODO: internal
        public void RegisterPrimitives(
            Action<RubyModule>/*!*/ mainSingletonTrait,

            Action<RubyModule>/*!*/ basicObjectInstanceTrait,
            Action<RubyModule>/*!*/ basicObjectClassTrait,
            Action<RubyModule> basicObjectConstantsInitializer,

            Action<RubyModule>/*!*/ kernelInstanceTrait,
            Action<RubyModule>/*!*/ kernelClassTrait,
            Action<RubyModule> kernelConstantsInitializer,

            Action<RubyModule>/*!*/ objectInstanceTrait,
            Action<RubyModule>/*!*/ objectClassTrait,
            Action<RubyModule> objectConstantsInitializer,

            Action<RubyModule>/*!*/ moduleInstanceTrait,
            Action<RubyModule>/*!*/ moduleClassTrait,
            Action<RubyModule> moduleConstantsInitializer,

            Action<RubyModule>/*!*/ classInstanceTrait,
            Action<RubyModule>/*!*/ classClassTrait,
            Action<RubyModule> classConstantsInitializer) {

            Assert.NotNull(mainSingletonTrait, basicObjectInstanceTrait, basicObjectClassTrait);
            Assert.NotNull(objectInstanceTrait, kernelInstanceTrait, moduleInstanceTrait, classInstanceTrait);
            Assert.NotNull(objectClassTrait, kernelClassTrait, moduleClassTrait, classClassTrait);

            _mainSingletonTrait = mainSingletonTrait;

            // inheritance hierarchy:
            //
            //                   Class
            //                     ^
            // BasicObject -> BasicObject'
            //      ^              ^
            //    Object   ->    Object'  
            //      ^              ^
            //    Module   ->    Module'
            //      ^              ^
            //    Class    ->    Class'
            //      ^
            //    Object'
            //

            // only Object should expose CLR methods:
            TypeTracker objectTracker = TypeTracker.GetTypeTracker(typeof(object));

            var moduleFactories = new Delegate[] {
                new Func<RubyScope, BlockParam, RubyClass, object>(RubyModule.CreateAnonymousModule),
            };

            var classFactories = new Delegate[] {
                new Func<RubyScope, BlockParam, RubyClass, object, object>(RubyClass.CreateAnonymousClass),
            };

            // locks to comply with lock requirements:
            using (ClassHierarchyLocker()) {
                _basicObjectClass = new RubyClass(this, Symbols.BasicObject, null, null, basicObjectInstanceTrait, basicObjectConstantsInitializer, null, null, null, null, null, false, false, ModuleRestrictions.Builtin & ~ModuleRestrictions.NoOverrides);
                _kernelModule = new RubyModule(this, Symbols.Kernel, kernelInstanceTrait, kernelConstantsInitializer, null, null, null, ModuleRestrictions.Builtin);
                _objectClass = new RubyClass(this, Symbols.Object, objectTracker.Type, null, objectInstanceTrait, objectConstantsInitializer, null, _basicObjectClass, new[] { _kernelModule }, objectTracker, null, false, false, ModuleRestrictions.Builtin & ~ModuleRestrictions.NoOverrides);
                _moduleClass = new RubyClass(this, Symbols.Module, typeof(RubyModule), null, moduleInstanceTrait, moduleConstantsInitializer, moduleFactories, _objectClass, null, null, null, false, false, ModuleRestrictions.Builtin);
                _classClass = new RubyClass(this, Symbols.Class, typeof(RubyClass), null, classInstanceTrait, classConstantsInitializer, classFactories, _moduleClass, null, null, null, false, false, ModuleRestrictions.Builtin);

                _basicObjectClass.InitializeImmediateClass(_basicObjectClass.CreateSingletonClass(_classClass, basicObjectClassTrait));
                _objectClass.InitializeImmediateClass(_objectClass.CreateSingletonClass(_basicObjectClass.ImmediateClass, objectClassTrait));
                _moduleClass.InitializeImmediateClass(_moduleClass.CreateSingletonClass(_objectClass.ImmediateClass, moduleClassTrait));
                _classClass.InitializeImmediateClass(_classClass.CreateSingletonClass(_moduleClass.ImmediateClass, classClassTrait));

                _moduleClass.InitializeDummySingleton();
                _classClass.InitializeDummySingleton();

                _basicObjectClass.ImmediateClass.InitializeImmediateClass(_classClass.GetDummySingletonClass());
                _objectClass.ImmediateClass.InitializeImmediateClass(_classClass.GetDummySingletonClass());
                _moduleClass.ImmediateClass.InitializeImmediateClass(_classClass.GetDummySingletonClass());
                _classClass.ImmediateClass.InitializeImmediateClass(_classClass.GetDummySingletonClass());
                
                _kernelModule.InitializeImmediateClass(_moduleClass, kernelClassTrait);

                _objectClass.SetConstantNoMutateNoLock(_basicObjectClass.Name, _basicObjectClass);
                _objectClass.SetConstantNoMutateNoLock(_moduleClass.Name, _moduleClass);
                _objectClass.SetConstantNoMutateNoLock(_classClass.Name, _classClass);
                _objectClass.SetConstantNoMutateNoLock(_objectClass.Name, _objectClass);
                _objectClass.SetConstantNoMutateNoLock(_kernelModule.Name, _kernelModule);
            }

            AddModuleToCacheNoLock(typeof(BasicObject), _basicObjectClass);
            AddModuleToCacheNoLock(typeof(Kernel), _kernelModule);
            AddModuleToCacheNoLock(objectTracker.Type, _objectClass);
            AddModuleToCacheNoLock(typeof(RubyObject), _objectClass);
            AddModuleToCacheNoLock(_moduleClass.GetUnderlyingSystemType(), _moduleClass);
            AddModuleToCacheNoLock(_classClass.GetUnderlyingSystemType(), _classClass);
        }

        #endregion

        #region CLR Types and Namespaces

        private void AssemblyLoaded(Assembly/*!*/ assembly) {
            _namespaces.LoadAssembly(assembly);
            try {
                AddExtensionAssembly(assembly);
            } finally {
                // The assembly can add types to namespaces whose misses are already cached (defined?
                // and constant sites keep their answer per constant version) - its namespaces are
                // loaded even if its extension methods can't be read.
                using (ClassHierarchyLocker()) {
                    ConstantAccessVersion++;
                }
            }
        }

        // Simple names of the assemblies the host was started with that the on-demand loading below
        // has not tried yet; built on the first miss.
        private HashSet<string> _untriedPlatformAssemblies;

        // Namespace members the on-demand loading below has already looked for.
        private HashSet<string> _platformAssemblyProbes;

        /// <summary>
        /// On .NET Core the class library is split into many assemblies that are loaded only when
        /// referenced, so `System::Console` misses until System.Console.dll has been loaded. When a
        /// constant lookup in a CLR namespace misses, this loads the platform assemblies that could
        /// define <paramref name="name"/> in that namespace: A.B.Name, A.B, A (for a miss in A.B),
        /// then the netstandard facade, which forwards the whole .NET Standard surface. Only
        /// assemblies on the runtime's trusted platform assembly list are considered and each name is
        /// tried once, so a repeated miss costs a set lookup. Returns true if the namespace may now
        /// contain <paramref name="name"/>.
        /// </summary>
        internal bool TryLoadNamespaceMemberAssembly(NamespaceTracker/*!*/ ns, string/*!*/ name) {
            RequiresClassHierarchyLock();

            string nsName = ns.Name;
            if (nsName == null || name.IndexOf('.') >= 0) {
                return false;
            }

            if (_platformAssemblyProbes == null) {
                _untriedPlatformAssemblies = GetPlatformAssemblyNames();
                _platformAssemblyProbes = new HashSet<string>();
            }

            if (_untriedPlatformAssemblies.Count == 0 || !_platformAssemblyProbes.Add(nsName + "." + name)) {
                return false;
            }

            string candidate = nsName + "." + name;
            while (true) {
                if (TryLoadPlatformAssembly(candidate) && ns.ContainsKey(name)) {
                    return true;
                }
                int dot = candidate.LastIndexOf('.');
                if (dot < 0) {
                    break;
                }
                candidate = candidate.Substring(0, dot);
            }

            // The netstandard facade forwards the whole .NET Standard surface. Only for the BCL's own
            // namespaces, though: a namespace included into Object (include Modern) has every top-level
            // constant looked up in it, and a miss there is no reason to load a hundred assemblies.
            return (nsName == "System" || nsName.StartsWith("System.", StringComparison.Ordinal) ||
                    nsName == "Microsoft" || nsName.StartsWith("Microsoft.", StringComparison.Ordinal))
                && TryLoadPlatformAssembly("netstandard") && ns.ContainsKey(name);
        }

        // Loads a platform assembly that has not been tried yet. Returns true if it is now loaded.
        private bool TryLoadPlatformAssembly(string/*!*/ simpleName) {
            if (!_untriedPlatformAssemblies.Remove(simpleName)) {
                return false;
            }

            Assembly assembly;
            try {
                assembly = DomainManager.Platform.LoadAssembly(simpleName);
            } catch (Exception e) {
                Utils.Log(String.Format("Loading platform assembly '{0}' failed: {1}", simpleName, e.Message), "LOADER");
                return false;
            }

            DomainManager.LoadAssembly(assembly);
            return true;
        }

        private static HashSet<string>/*!*/ GetPlatformAssemblyNames() {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (paths != null) {
                foreach (var path in paths.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)) {
                    if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
                        result.Add(Path.GetFileNameWithoutExtension(path));
                    }
                }
            }
            return result;
        }

        internal void AddModuleToCacheNoLock(Type/*!*/ type, RubyModule/*!*/ module) {
            Assert.NotNull(type, module);
            _moduleCache.Add(type, module);
        }

        /// <summary>
        /// Points a CLR type at a RubyModule that is already the module of another type, replacing
        /// whatever was registered for it. Used to give Int64 the Integer class it shares with
        /// Int32 and BigInteger, after the Int64Ops library has registered its own.
        /// </summary>
        internal void ReplaceModuleInCacheNoLock(Type/*!*/ type, RubyModule/*!*/ module) {
            Assert.NotNull(type, module);
            _moduleCache[type] = module;
            _classReadCache.TryRemove(type, out _);
        }

        internal void AddNamespaceToCacheNoLock(NamespaceTracker/*!*/ namespaceTracker, RubyModule/*!*/ module) {
            Assert.NotNull(namespaceTracker, module);

            _namespaceCache.Add(namespaceTracker, module);
        }

        internal RubyModule/*!*/ GetOrCreateModule(NamespaceTracker/*!*/ tracker) {
            Assert.NotNull(tracker);

            lock (ModuleCacheLock) {
                return GetOrCreateModuleNoLock(tracker);
            }
        }

        internal bool TryGetModule(NamespaceTracker/*!*/ namespaceTracker, out RubyModule result) {
            lock (NamespaceCacheLock) {
                return _namespaceCache.TryGetValue(namespaceTracker, out result);
            }
        }

        internal RubyModule/*!*/ GetOrCreateModule(Type/*!*/ moduleType) {
            Debug.Assert(RubyModule.IsModuleType(moduleType));

            lock (ModuleCacheLock) {
                return GetOrCreateModuleNoLock(moduleType);
            }
        }

        public bool TryGetModule(Type/*!*/ type, out RubyModule result) {
            lock (ModuleCacheLock) {
                return _moduleCache.TryGetValue(type, out result);
            }
        }

        internal bool TryGetModuleNoLock(Type/*!*/ type, out RubyModule result) {
            return _moduleCache.TryGetValue(type, out result);
        }

        internal bool TryGetClassNoLock(Type/*!*/ type, out RubyClass result) {
            RubyModule module;
            if (_moduleCache.TryGetValue(type, out module)) {
                result = module as RubyClass;
                if (result == null) {
                    throw new InvalidOperationException("Specified type doesn't represent a class");
                }
                return true;
            } else {
                result = null;
                return false;
            }
        }

        // Lock-free read side of GetOrCreateClass. Every call on an Integer, Float or other CLR value
        // asks for its class (GetClassOf), and taking ModuleCacheLock for a dictionary probe each time
        // was a third or more of numeric code. A CLR type's class, once created, is only replaced by
        // ReplaceModuleInCacheNoLock, which evicts the type here; entries are written under the lock.
        private readonly ConcurrentDictionary<Type, RubyClass>/*!*/ _classReadCache = new ConcurrentDictionary<Type, RubyClass>();

        internal RubyClass/*!*/ GetOrCreateClass(Type/*!*/ type) {
            RubyClass result;
            if (_classReadCache.TryGetValue(type, out result)) {
                return result;
            }
            lock (ModuleCacheLock) {
                result = GetOrCreateClassNoLock(type);
                _classReadCache[type] = result;
                return result;
            }
        }

        private RubyModule/*!*/ GetOrCreateModuleNoLock(NamespaceTracker/*!*/ tracker) {
            Assert.NotNull(tracker);

            RubyModule result;
            if (_namespaceCache.TryGetValue(tracker, out result)) {
                return result;
            }

            result = CreateModule(GetQualifiedName(tracker), null, null, null, null, tracker, null, ModuleRestrictions.None);
            _namespaceCache[tracker] = result;
            return result;
        }

        private RubyModule/*!*/ GetOrCreateModuleNoLock(Type/*!*/ moduleType) {
            Debug.Assert(RubyModule.IsModuleType(moduleType));

            RubyModule result;
            if (_moduleCache.TryGetValue(moduleType, out result)) {
                return result;
            }

            TypeTracker tracker = (TypeTracker)TypeTracker.FromMemberInfo(moduleType);

            RubyModule[] mixins;
            if (moduleType.IsGenericType && !moduleType.IsGenericTypeDefinition) {
                // I<T0..Tn> mixes in its generic definition I<,..,>
                mixins = new[] { GetOrCreateModuleNoLock(moduleType.GetGenericTypeDefinition()) };
            } else {
                mixins = null;
            }

            result = CreateModule(GetQualifiedNameNoLock(moduleType), null, null, null, mixins, null, tracker, ModuleRestrictions.None);
            _moduleCache[moduleType] = result;
            return result;
        }

        /// <summary>
        /// The Ruby superclass of a CLR type that no library initializer gives one: the class of its CLR base type, except
        /// that System::Exception is Ruby's Exception, which a bare rescue lets through. A CLR exception deriving from it directly
        /// (.NET's recommended base for custom exceptions; AggregateException) is an application error like a SystemException,
        /// so it is a StandardError. Ruby's own exception classes get their superclasses from the library initializers; the
        /// types emitted for Ruby subclasses of Exception (IRubyType) and control flow (StackUnwinder) keep Exception.
        /// </summary>
        private RubyClass/*!*/ GetClrSuperClassNoLock(Type/*!*/ type) {
            RubyClass baseClass = GetOrCreateClassNoLock(type.BaseType);
            if (baseClass == _exceptionClass
                && !typeof(IRubyType).IsAssignableFrom(type) && !typeof(StackUnwinder).IsAssignableFrom(type)) {
                // the initializers define StandardError before any such class is asked for (one made earlier keeps Exception)
                Debug.Assert(_standardErrorClass != null);
                return _standardErrorClass ?? baseClass;
            }
            return baseClass;
        }

        private RubyClass/*!*/ GetOrCreateClassNoLock(Type/*!*/ type) {
            Debug.Assert(!RubyModule.IsModuleType(type));

            RubyClass result;
            if (TryGetClassNoLock(type, out result)) {
                return result;
            }

            RubyClass baseClass;

            if (type.IsByRef) {
                baseClass = _objectClass;
            } else {
                baseClass = GetClrSuperClassNoLock(type);
            }

            TypeTracker tracker = (TypeTracker)TypeTracker.FromMemberInfo(type);
            RubyModule[] clrMixins = GetClrMixinsNoLock(type);
            RubyModule[] expandedMixins;

            if (clrMixins != null) {
                using (ClassHierarchyLocker()) {
                    expandedMixins = RubyModule.ExpandMixinsNoLock(baseClass, clrMixins);
                }
            } else {
                expandedMixins = RubyModule.EmptyArray;
            }

            result = CreateClass(
                GetQualifiedNameNoLock(type), type, null, null, null, null, null, 
                baseClass, expandedMixins, tracker, null, false, false, ModuleRestrictions.None
            );

            if (TypeUtils.IsComObjectType(type)) {
                _comObjectClass = result;
            }

            _moduleCache[type] = result;
            return result;
        }

        /// <summary>
        /// An interface is mixed into the type that implements it.
        /// A generic type definition is mixed into its instantiations.
        /// 
        /// In both cases these modules don't themselves contribute any callable CLR methods 
        /// yet they might contribute CLR extension methods and Ruby methods defined on them.
        /// </summary>
        private RubyModule[] GetClrMixinsNoLock(Type/*!*/ type) {
            List<RubyModule> modules = new List<RubyModule>();

            if (type.IsGenericType && !type.IsGenericTypeDefinition) {
                modules.Add(GetOrCreateModuleNoLock(type.GetGenericTypeDefinition()));
            }
            
            if (type.IsArray) {
                if (type.GetArrayRank() > 1) {
                    RubyModule module;
                    if (TryGetModuleNoLock(typeof(MultiDimensionalArray), out module)) {
                        modules.Add(module);
                    }
                }
            } else if (type.IsEnum) {
                if (type.IsDefined(typeof(FlagsAttribute), false)) {
                    RubyModule module;
                    if (TryGetModuleNoLock(typeof(FlagEnumeration), out module)) {
                        modules.Add(module);
                    }
                }
            }

            foreach (Type iface in ReflectionUtils.GetDeclaredInterfaces(type)) {
                modules.Add(GetOrCreateModuleNoLock(iface));
            }

            // Hash patterns match a CLR object's public members. Last, so that an interface or a generic type
            // definition can define its own #deconstruct_keys; a subclass finds the module on its base class.
            // An abstract class has no instances of its own, and leaving it out keeps the library classes that
            // derive from one (System::Char from ValueType, ...) as they are.
            RubyModule patternMatching;
            if (!type.IsAbstract && TryGetModuleNoLock(typeof(ClrPatternMatching), out patternMatching)) {
                modules.Add(patternMatching);
            }

            return modules.Count > 0 ? modules.ToArray() : null;
        }

        #endregion

        #region CLR Extension Methods

        private readonly object ExtensionsLock = new object();
        
        // List of assemblies that might include extension methods but whose processing was delayed until the first call of
        // use_clr_extensions or `using' of a CLR namespace. Null once either has run.
        private List<Assembly> _potentialExtensionAssemblies = new List<Assembly>();

        // A list of extension methods that are available for activation. Grouped by a declaring namespace.
        // Value is null if the namepsace has been activated.
        private Dictionary<string, List<IEnumerable<ExtensionMethodInfo>>> _availableExtensions;

        // Every extension method loaded so far, by declaring namespace: what a `using' of the namespace brings in,
        // whether or not using_clr_extensions has activated it everywhere as well.
        private readonly Dictionary<string, List<ExtensionMethodInfo>>/*!*/ _loadedExtensions = new Dictionary<string, List<ExtensionMethodInfo>>();

        // The namespaces some `using' has named. Their extension methods - those of assemblies loaded later too - are
        // filed with the types they extend (RubyModule.AddScopedExtensionMethodsNoLock), for the calls made where the
        // namespace (or the method's static class) is `using'd to see.
        private readonly HashSet<string>/*!*/ _scopedExtensionNamespaces = new HashSet<string>();

        // Assemblies whose extension methods have been registered (or are queued to be, in _potentialExtensionAssemblies).
        private readonly HashSet<Assembly>/*!*/ _extensionAssemblies = new HashSet<Assembly>();

        /// <summary>
        /// Registers the extension methods of <paramref name="assembly"/>, or queues them until extension methods are first asked
        /// for. Does nothing for an assembly seen already, unless reading its extension methods failed: the exception is thrown
        /// and the next call tries again (a require of the DLL once what it references can be found, say).
        /// </summary>
        internal void AddExtensionAssembly(Assembly/*!*/ assembly) {
            lock (ExtensionsLock) {
                if (!_extensionAssemblies.Add(assembly)) {
                    return;
                }
            }

            if (_potentialExtensionAssemblies != null) {
                lock (ExtensionsLock) {
                    if (_potentialExtensionAssemblies != null) {
                        _potentialExtensionAssemblies.Add(assembly);
                        return;
                    }
                }
            }

            LoadExtensions(ReadExtensionMethods(assembly));
        }

        // Reading an assembly's extension methods loads the types in their signatures, and fails if one lives in an assembly
        // that can't be found. The assembly is then forgotten, so that it's read again next time.
        private IEnumerable<KeyValuePair<string, IEnumerable<ExtensionMethodInfo>>>/*!*/ ReadExtensionMethods(Assembly/*!*/ assembly) {
            try {
                return ReflectionUtils.GetVisibleExtensionMethodGroups(assembly, true);
            } catch {
                lock (ExtensionsLock) {
                    _extensionAssemblies.Remove(assembly);
                }
                throw;
            }
        }

        private static bool IsUnreadableExtensionAssembly(Exception/*!*/ e) {
            return e is IOException || e is BadImageFormatException || e is TypeLoadException || e is LoadError;
        }

        private void ReportUnreadableExtensionAssembly(Assembly/*!*/ assembly, Exception/*!*/ e) {
            ReportWarning(String.Format("extension methods of {0} not read: {1}", assembly.GetName().Name, e.Message.TrimEnd()));
        }

        private void LoadExtensions(IEnumerable<KeyValuePair<string, IEnumerable<ExtensionMethodInfo>>>/*!*/ extensionMethodGroups) {
            List<IEnumerable<ExtensionMethodInfo>> immediatelyActivated = null;
            List<ExtensionMethodInfo> scoped = null;

            lock (ExtensionsLock) {
                foreach (var extensionMethodGroup in extensionMethodGroups) {
                    if (_availableExtensions == null) {
                        _availableExtensions = new Dictionary<string, List<IEnumerable<ExtensionMethodInfo>>>();
                    }

                    string ns = extensionMethodGroup.Key;
                    var methods = new List<ExtensionMethodInfo>(extensionMethodGroup.Value);

                    List<ExtensionMethodInfo> loaded;
                    if (!_loadedExtensions.TryGetValue(ns, out loaded)) {
                        _loadedExtensions.Add(ns, loaded = new List<ExtensionMethodInfo>());
                    }
                    loaded.AddRange(methods);

                    // an assembly loaded after its namespace was `using'd:
                    if (_scopedExtensionNamespaces.Contains(ns)) {
                        if (scoped == null) {
                            scoped = new List<ExtensionMethodInfo>();
                        }
                        scoped.AddRange(methods);
                    }

                    List<IEnumerable<ExtensionMethodInfo>> extensions;
                    if (_availableExtensions.TryGetValue(ns, out extensions)) {
                        if (extensions == null) {
                            if (immediatelyActivated == null) {
                                immediatelyActivated = new List<IEnumerable<ExtensionMethodInfo>>();
                            }
                            extensions = immediatelyActivated;
                        }
                    } else {
                        _availableExtensions.Add(ns, extensions = new List<IEnumerable<ExtensionMethodInfo>>());
                    }
                    extensions.Add(methods);
                }
            }

            if (immediatelyActivated != null) {
                ActivateExtensions(immediatelyActivated);
            }
            if (scoped != null) {
                AddScopedExtensions(scoped);
            }
        }

        /// <summary>
        /// using_clr_extensions: the extension methods of <paramref name="namespace"/> become methods of the types they
        /// extend, for every call in the runtime.
        /// </summary>
        public void ActivateExtensions(string/*!*/ @namespace) {
            ContractUtils.RequiresNotNull(@namespace, "namespace");
            LoadExtensions(@namespace);

            List<IEnumerable<ExtensionMethodInfo>> extensions = null;
            lock (ExtensionsLock) {
                if (_availableExtensions == null) {
                    _availableExtensions = new Dictionary<string, List<IEnumerable<ExtensionMethodInfo>>>();
                }
                _availableExtensions.TryGetValue(@namespace, out extensions);
                
                // activate namespace:
                _availableExtensions[@namespace] = null;
            }

            if (extensions != null) {
                ActivateExtensions(extensions);
            }
        }

        // using_clr_extensions is deprecated in favour of `using'; each runtime says so once.
        private int _usingClrExtensionsDeprecationReported;

        /// <summary>
        /// Warns that using_clr_extensions is deprecated, the first time anything in the runtime calls it where the
        /// warning shows: a call made while $VERBOSE is nil, which prints no warning, leaves it to the next one.
        /// <paramref name="namespaceName"/> is the namespace it was called with, or null for the global one.
        /// </summary>
        public void ReportUsingClrExtensionsDeprecation(string namespaceName) {
            if (Verbose != null && Interlocked.Exchange(ref _usingClrExtensionsDeprecationReported, 1) == 0) {
                ReportWarning(String.Format(
                    "using_clr_extensions is deprecated; use '{0}' instead, which brings the extension methods into the rest of " +
                    "the file, class body or eval string only, as C#'s using directive does",
                    namespaceName != null ? "using " + namespaceName : "using <namespace>"
                ));
            }
        }

        /// <summary>
        /// `using' of a CLR namespace, or of a static class declared in <paramref name="namespace"/>: the namespace's
        /// extension methods become available to the calls made where it (or the class) is `using'd, and to no other
        /// (see RubyModule.ResolveMethodNoLock).
        /// </summary>
        public void ActivateScopedExtensions(string/*!*/ @namespace) {
            ContractUtils.RequiresNotNull(@namespace, "namespace");
            LoadExtensions(@namespace);

            List<ExtensionMethodInfo> extensions = null;
            lock (ExtensionsLock) {
                _hasScopedExtensions = true;
                if (_scopedExtensionNamespaces.Add(@namespace)) {
                    _loadedExtensions.TryGetValue(@namespace, out extensions);
                }
            }

            if (extensions != null) {
                AddScopedExtensions(extensions);
            }
        }

        /// <summary>
        /// Makes sure the extension methods of <paramref name="namespace"/> have been loaded, along with those of
        /// every assembly whose processing was put off until extension methods were first asked for.
        /// </summary>
        private void LoadExtensions(string/*!*/ @namespace) {
            // The types of a namespace can be known through a facade's type forwarders (netstandard, System.Xml),
            // which loads the assemblies that define them without them ever being reported loaded: their extension
            // methods (System.Linq.Enumerable's) would never be seen. Look at where the namespace's types really live.
            NamespaceTracker tracker = _namespaces;
            foreach (string part in @namespace.Length > 0 ? @namespace.Split('.') : ArrayUtils.EmptyStrings) {
                MemberTracker member;
                tracker = (tracker != null && tracker.TryGetValue(part, out member)) ? member as NamespaceTracker : null;
            }
            // An assembly whose extension methods can't be read is reported, and read again by the next `using' of its
            // namespace or require of it; the rest are read.
            if (tracker != null) {
                foreach (var member in tracker) {
                    var typeTracker = member.Value as TypeTracker;
                    if (typeTracker != null && !(typeTracker is TypeGroup)) {
                        Assembly assembly = typeTracker.Type.Assembly;
                        try {
                            AddExtensionAssembly(assembly);
                        } catch (Exception e) when (IsUnreadableExtensionAssembly(e)) {
                            ReportUnreadableExtensionAssembly(assembly, e);
                        }
                    }
                }
            }

            Assembly[] assemblies = null;
            if (_potentialExtensionAssemblies != null) {
                lock (ExtensionsLock) {
                    if (_potentialExtensionAssemblies != null) {
                        assemblies = _potentialExtensionAssemblies.ToArray();
                        _potentialExtensionAssemblies = null;
                    }
                }
            }

            if (assemblies != null) {
                var extensionGroups = new List<KeyValuePair<string, IEnumerable<ExtensionMethodInfo>>>(); 
                foreach (var assembly in assemblies) {
                    try {
                        extensionGroups.AddRange(ReadExtensionMethods(assembly));
                    } catch (Exception e) when (IsUnreadableExtensionAssembly(e)) {
                        ReportUnreadableExtensionAssembly(assembly, e);
                    }
                }
                LoadExtensions(extensionGroups);
            }
        }

        private void ActivateExtensions(List<IEnumerable<ExtensionMethodInfo>>/*!*/ extensionLists) {
            var groupedByType = GroupByExtendedModule(extensionLists);
            using (ClassHierarchyLocker()) {
                lock (ModuleCacheLock) {
                    foreach (var entry in groupedByType) {
                        GetExtendedModuleNoLock(entry.Key).AddExtensionMethodsNoLock(entry.Value);
                    }
                }
            }
        }

        private void AddScopedExtensions(List<ExtensionMethodInfo>/*!*/ extensions) {
            var groupedByType = GroupByExtendedModule(new[] { extensions });
            using (ClassHierarchyLocker()) {
                lock (ModuleCacheLock) {
                    foreach (var entry in groupedByType) {
                        GetExtendedModuleNoLock(entry.Key).AddScopedExtensionMethodsNoLock(entry.Value);
                    }
                }
            }
        }

        // { the type whose Ruby module the extension methods are filed with -> extension methods }
        private static Dictionary<Type, List<ExtensionMethodInfo>>/*!*/ GroupByExtendedModule(IEnumerable<IEnumerable<ExtensionMethodInfo>>/*!*/ extensionLists) {
            var groupedByType = new Dictionary<Type, List<ExtensionMethodInfo>>();
            foreach (var extensionList in extensionLists) {
                foreach (var extension in extensionList) {
                    Type extendedType = extension.ExtendedType;
                    Debug.Assert(!extendedType.IsGenericTypeDefinition && !extendedType.IsPointer && !extendedType.IsByRef);

                    Type target;
                    if (extendedType.ContainsGenericParameters) {
                        if (extendedType.IsGenericParameter) {
                            // TODO: we can do better if there are constraints defined on the parameter
                            target = typeof(object);
                        } else {
                            target = extendedType.IsArray ? typeof(Array) : extendedType.GetGenericTypeDefinition();
                        }
                    } else {
                        target = extendedType;
                    }

                    List<ExtensionMethodInfo> list;
                    if (!groupedByType.TryGetValue(target, out list)) {
                        groupedByType.Add(target, list = new List<ExtensionMethodInfo>());
                    }
                    list.Add(extension);
                }
            }
            return groupedByType;
        }

        private RubyModule/*!*/ GetExtendedModuleNoLock(Type/*!*/ target) {
            return (target.IsGenericTypeDefinition || target.IsInterface) ? GetOrCreateModuleNoLock(target) : GetOrCreateClassNoLock(target);
        }

        #endregion

        #region Class and Module Factories (thread-safe)

        /// <summary>
        /// Class factory. Do not use RubyClass constructor except for special cases (Object, Class, Module, singleton classes).
        /// </summary>
        internal RubyClass/*!*/ CreateClass(string name, Type type, object classSingletonOf,
            Action<RubyModule> instanceTrait, Action<RubyModule> classTrait, Action<RubyModule> constantsInitializer, Delegate/*!*/[] factories,
            RubyClass/*!*/ superClass, RubyModule/*!*/[] expandedMixins, TypeTracker tracker, RubyStruct.Info structInfo, 
            bool isRubyClass, bool isSingletonClass, ModuleRestrictions restrictions) {
            Assert.NotNull(superClass);

            RubyClass result = new RubyClass(this, name, type, classSingletonOf,
                instanceTrait, constantsInitializer, factories, superClass, expandedMixins, tracker, structInfo,
                isRubyClass, isSingletonClass, restrictions
            );

            result.InitializeImmediateClass(superClass.ImmediateClass, classTrait);
            return result;
        }

        /// <summary>
        /// Module factory. Do not use RubyModule constructor except special cases (Kernel).
        /// </summary>
        internal RubyModule/*!*/ CreateModule(string name,
            Action<RubyModule> instanceTrait, Action<RubyModule> classTrait, Action<RubyModule> constantsInitializer,
            RubyModule/*!*/[] expandedMixins, NamespaceTracker namespaceTracker, TypeTracker typeTracker, ModuleRestrictions restrictions) {

            RubyModule result = new RubyModule(
                this, name, instanceTrait, constantsInitializer, expandedMixins, namespaceTracker, typeTracker, restrictions
            );

            result.InitializeImmediateClass(_moduleClass, classTrait);
            return result;
        }

        /// <summary>
        /// Creates a singleton class for specified object unless it already exists. 
        /// </summary>
        public RubyClass/*!*/ GetOrCreateSingletonClass(object obj) {
            RubyModule module = obj as RubyModule;
            if (module != null) {
                return module.GetOrCreateSingletonClass();
            }

            // A frozen string can have no singleton class, so asking a chilled one for its own is
            // the same kind of change as writing to it, and MRI warns the same way.
            MutableString.ReportChilledChange(obj);

            return GetOrCreateInstanceSingleton(obj, null, null, null, null);
        }

        internal RubyClass/*!*/ GetOrCreateMainSingleton(object obj, RubyModule/*!*/[] expandedMixins) {
            return GetOrCreateInstanceSingleton(obj, _mainSingletonTrait, null, null, expandedMixins);
        }

        internal RubyClass/*!*/ GetOrCreateInstanceSingleton(object obj, Action<RubyModule> instanceTrait, Action<RubyModule> classTrait, 
            Action<RubyModule> constantsInitializer, RubyModule/*!*/[] expandedMixins) {
            Debug.Assert(!(obj is RubyModule));
            Debug.Assert(RubyUtils.HasSingletonClass(obj));

            if (obj == null) {
                return _nilClass;
            }

            if (obj is bool) {
                return (bool)obj ? _trueClass : _falseClass;
            }

            RubyInstanceData data = null;
            RubyClass immediate = GetImmediateClassOf(obj, ref data);
            if (immediate.IsSingletonClass) {
                Debug.Assert(!immediate.IsDummySingletonClass);
                return immediate;
            }

            RubyClass result = CreateClass(
                null, null, obj, instanceTrait, classTrait, constantsInitializer, null,
                immediate, expandedMixins, null, null, true, true, ModuleRestrictions.None
            );

            using (ClassHierarchyLocker()) {
                // singleton might have been created by another thread:
                immediate = GetImmediateClassOf(obj, ref data);
                if (immediate.IsSingletonClass) {
                    Debug.Assert(!immediate.IsDummySingletonClass);
                    return immediate;
                }

                SetInstanceSingletonOfNoLock(obj, ref data, result);

                if (!(obj is IRubyObject)) {
                    PerfTrack.NoteEvent(PerfTrack.Categories.Count, "Non-IRO singleton created " + immediate.NominalClass.Name);
                }
            }

            Debug.Assert(result.IsSingletonClass && !result.IsDummySingletonClass);
            return result;
        }

        /// <summary>
        /// Defines a new module nested in the given owner.
        /// The module is published into the global scope if the owner is Object.
        /// 
        /// Thread safe.
        /// </summary>
        internal RubyModule/*!*/ DefineModule(RubyModule/*!*/ owner, string/*!*/ name) {
            RubyModule result = CreateModule(owner.MakeNestedModuleName(name), null, null, null, null, null, null, ModuleRestrictions.None);
            PublishModule(name, owner, result);
            return result;
        }

        /// <summary>
        /// Defines a new class nested in the given owner.
        /// The module is published into the global scope it if is not anonymous and the owner is Object.
        /// 
        /// Thread safe.
        /// Triggers "inherited" event.
        /// </summary>
        internal RubyClass/*!*/ DefineClass(RubyModule/*!*/ owner, string name, RubyClass/*!*/ superClass, RubyStruct.Info structInfo) {
            Assert.NotNull(owner, superClass);

            if (superClass.TypeTracker != null && superClass.TypeTracker.Type.ContainsGenericParameters) {
                throw RubyExceptions.CreateTypeError(String.Format(
                    "{0}: cannot inherit from open generic instantiation {1}. Only closed instantiations are supported.",
                    name, superClass.Name
                ));
            }

            string qualifiedName = owner.MakeNestedModuleName(name);
            RubyClass result = CreateClass(
                qualifiedName, null, null, null, null, null, null, superClass, null, null, structInfo, true, false, ModuleRestrictions.None
            );
            PublishModule(name, owner, result);
            superClass.ClassInheritedEvent(result);

            return result;
        }

        private static void PublishModule(string name, RubyModule/*!*/ owner, RubyModule/*!*/ module) {
            if (name != null) {
                owner.SetConstant(name, module);
                // `module M; end` nested in an anonymous module produces a temporary name, same as `m::M = ...`.
                if (!owner.IsObjectClass && !owner.HasPermanentName) {
                    module.SetName(module.Name, false);
                }
                if (owner.IsObjectClass) {
                    module.Publish(name);
                }
                owner.ConstantAdded(name);
            }
        }

        #endregion

        #region Libraries (thread-safe)

        //
        // Scenarios:
        // 1) define/reopen Ruby class/module (name != null && !builtin)
        //    - Built-in definitions don't reopen existing Ruby classes/modules as they are all declared before any Ruby code can run.
        //    - Only global classes/modules can be reopened (TODO: we need to pass in the containing class).
        //    - If reopening:
        //        - Members are merged into the existing Ruby class/module.
        //        - Underlying system type is ignored when extending an existing Ruby class by a library definition.
        //          We don't want to fail the library load based upon an existence of an instance of a Ruby class (whose CLR type we cannot change).
        //
        // 2) extend CLR type (name == null)
        //    
        
        private T PrepareLibraryModuleDefinition<T>(string name, RubyClass super, RubyModule/*!*/[]/*!*/ mixins, ModuleRestrictions restrictions, bool builtin,
            out RubyModule[] expandedMixins) where T : RubyModule {

            using (ClassHierarchyLocker()) {
                expandedMixins = RubyModule.ExpandMixinsNoLock(super, mixins);
                if (name != null && !builtin) {
                    // Do not run constant initializer - all modules that the name might refer to should have already been set up;
                    // A library definition should only reopen Ruby class/module definition, not another library definition.
                    // A pending autoload of the name is not a definition: `autoload :OpenSSL, "openssl"`
                    // (net/http does that) followed by the require that loads this library.
                    ConstantStorage c;
                    if (_objectClass.TryGetConstantNoAutoloadNoInit(name, out c) && !RubyModule.IsAutoload(c.Value)) {
                        var result = c.Value as T;
                        bool isClass = typeof(T) == typeof(RubyClass);
                        if (result == null || result.IsClass != isClass) {
                            // MRI's rb_define_class/rb_define_module: "Zlib is not a module (Class)"
                            throw RubyExceptions.CreateTypeError("{0} is not a {1} ({2})", name, isClass ? "class" : "module",
                                GetClassName(c.Value));
                        }
                        if (isClass && (restrictions & ModuleRestrictions.AllowReopening) == 0) {
                            throw RubyExceptions.CreateTypeError("cannot redefine {1} '{0}'", name, isClass ? "class" : "module");
                        }
                        return result;
                    }
                }
            }

            return null;
        }

        internal RubyModule/*!*/ DefineLibraryModule(string name, Type/*!*/ type,
            Action<RubyModule> instanceTrait, Action<RubyModule> classTrait, Action<RubyModule> constantsInitializer,
            RubyModule/*!*/[]/*!*/ mixins, ModuleRestrictions restrictions, bool builtin) {
            Assert.NotNull(type);
            Assert.NotNullItems(mixins);
            Debug.Assert(name == null || name.Length != 0);
            Debug.Assert(name != null || (restrictions & ModuleRestrictions.NoUnderlyingType) == 0);

            RubyModule[] expandedMixins;
            RubyModule result = PrepareLibraryModuleDefinition<RubyModule>(name, null, mixins, restrictions, builtin, out expandedMixins);
            bool exists = result != null;

            if (!exists) {
                lock (ModuleCacheLock) {
                    if (!(exists = TryGetModuleNoLock(type, out result))) {
                        if (name == null) {
                            name = GetQualifiedNameNoLock(type);
                        }

                        // Use empty constant initializer rather than null so that we don't try to initialize nested types.
                        result = CreateModule(
                            name, instanceTrait, classTrait, constantsInitializer ?? RubyModule.EmptyInitializer, expandedMixins, null,
                            GetLibraryModuleTypeTracker(type, restrictions),
                            restrictions
                        );

                        AddModuleToCacheNoLock(type, result);
                    }
                }
            }

            if (exists) {
                result.IncludeLibraryModule(instanceTrait, classTrait, constantsInitializer, mixins, builtin);
            }

            return result;
        }

        internal RubyClass/*!*/ DefineLibraryClass(string name, Type/*!*/ type,
            Action<RubyModule> instanceTrait, Action<RubyModule> classTrait, Action<RubyModule> constantsInitializer,
            RubyClass super, RubyModule[]/*!*/ mixins, Delegate/*!*/[] factories, ModuleRestrictions restrictions, bool builtin) {
            Assert.NotNull(type);
            Assert.NotNullItems(mixins);
            Debug.Assert(name != null || (restrictions & ModuleRestrictions.NoUnderlyingType) == 0);
            Debug.Assert(name == null || name.Length != 0);

            RubyModule[] expandedMixins;
            RubyClass result = PrepareLibraryModuleDefinition<RubyClass>(name, super, mixins, restrictions, builtin, out expandedMixins);
            bool exists = result != null;

            if (!exists) {
                lock (ModuleCacheLock) {
                    if (!(exists = TryGetClassNoLock(type, out result))) {
                        if (name == null) {
                            name = GetQualifiedNameNoLock(type);
                        }

                        if (super == null) {
                            // same superclass as when the type is first reached from Ruby, so the hierarchy doesn't depend on load order
                            super = GetClrSuperClassNoLock(type);
                        }

                        // Use empty constant initializer rather than null so that we don't try to initialize nested types.
                        result = CreateClass(
                            name, type, null, instanceTrait, classTrait, constantsInitializer ?? RubyModule.EmptyInitializer, factories,
                            super, expandedMixins, GetLibraryModuleTypeTracker(type, restrictions), null, false, false,
                            restrictions
                        );

                        AddModuleToCacheNoLock(type, result);
                    }
                }
            }

            if (exists) {
                if (super != null && super != result.SuperClass) {
                    throw RubyExceptions.CreateTypeError("superclass mismatch for class {0}", name);
                }

                if (factories != null && factories.Length != 0) {
                    throw RubyExceptions.CreateTypeError("Cannot add factories to an existing class");
                }

                result.IncludeLibraryModule(instanceTrait, classTrait, constantsInitializer, mixins, builtin);
                return result;
            } else if (!builtin) {
                super.ClassInheritedEvent(result);
            }

            return result;
        }

        private static TypeTracker GetLibraryModuleTypeTracker(Type/*!*/ type, ModuleRestrictions restrictions) {
            return (restrictions & ModuleRestrictions.NoUnderlyingType) != 0 ? null : TypeTracker.GetTypeTracker(type);
        }

        #endregion

        #region Getting Modules and Classes from objects, CLR types and CLR namespaces (thread-safe)

        public RubyModule/*!*/ GetModule(Type/*!*/ type) {
            if (RubyModule.IsModuleType(type)) {
                return GetOrCreateModule(type);
            } else {
                return GetOrCreateClass(type);
            }
        }

        public RubyModule/*!*/ GetModule(NamespaceTracker/*!*/ namespaceTracker) {
            return GetOrCreateModule(namespaceTracker);
        }

        public RubyClass/*!*/ GetClass(Type/*!*/ type) {
            ContractUtils.Requires(!RubyModule.IsModuleType(type));
            return GetOrCreateClass(type);
        }

        /// <summary>
        /// Gets a class of the specified object (skips any singletons).
        /// </summary>
        public RubyClass/*!*/ GetClassOf(object obj) {
            ContractUtils.Ensures(!ContractUtils.Result<RubyClass>().IsSingletonClass);
            return TryGetClassOfRubyObject(obj) ?? (_hasAdoptedClrObjects ? TryGetClassOfAdoptedClrObject(obj) : null)
                ?? GetOrCreateClass(obj.GetType());
        }

        private volatile bool _hasAdoptedClrObjects;

        private RubyClass TryGetClassOfAdoptedClrObject(object/*!*/ obj) {
            RubyInstanceData data;
            RubyClass immediate;
            if (RubyUtils.HasObjectState(obj) && TryGetClrTypeInstanceData(obj, out data) && (immediate = data.ImmediateClass) != null) {
                immediate = immediate.GetNonSingletonClass();
                // Usually this is a Ruby subclass adopted by a sealed CLR object. IO#reopen is
                // the other case: MRI can change a File into the built-in IO class in place.
                return immediate;
            }
            return null;
        }

        /// <summary>
        /// Makes <paramref name="cls"/>, a Ruby subclass of the class of <paramref name="obj"/>, the class of
        /// <paramref name="obj"/>. This is how a sealed CLR type (System.Threading.Thread) gets instances of its
        /// Ruby subclasses: there is no CLR type to derive, so the object stays a plain instance of the CLR type
        /// and its Ruby class is recorded in its instance data, where a singleton class would be.
        /// Must be called before the object is visible to Ruby code.
        /// </summary>
        public void AdoptClrObject(object/*!*/ obj, RubyClass/*!*/ cls) {
            ContractUtils.RequiresNotNull(obj, "obj");
            ContractUtils.RequiresNotNull(cls, "cls");
            ContractUtils.Requires(!(obj is IRubyObject) && RubyUtils.HasObjectState(obj), "obj");

            RubyClass clrClass = GetOrCreateClass(obj.GetType());
            ContractUtils.Requires(cls.IsRubyClass && !cls.IsSingletonClass && cls.IsSubclassOf(clrClass), "cls");

            using (ClassHierarchyLocker()) {
                if (!clrClass.HasAdoptedInstances) {
                    clrClass.HasAdoptedInstances = true;
                    // rules bound so far don't check for adopted objects:
                    clrClass.MethodsUpdated("AdoptClrObject");
                }
                _hasAdoptedClrObjects = true;
                GetInstanceData(obj).ImmediateClass = cls;
            }
        }

        private RubyClass TryGetClassOfRubyObject(object obj) {
            if (obj == null) {
                return _nilClass;
            }

            if (obj is bool) {
                return (bool)obj ? _trueClass : _falseClass;
            }

            IRubyObject rubyObj = obj as IRubyObject;
            if (rubyObj != null) {
                var result = rubyObj.ImmediateClass.GetNonSingletonClass();
                Debug.Assert(result != null, "Invalid IRubyObject implementation: Class should not be null");
                return result;
            }

            return null;
        }

        /// <summary>
        /// Gets a singleton or class for <c>obj</c>.
        /// Might return a class object from a foreign runtime (if obj is a runtime bound object).
        /// </summary>
        public RubyClass/*!*/ GetImmediateClassOf(object obj) {
            RubyInstanceData data = null;
            return GetImmediateClassOf(obj, ref data);
        }

        private RubyClass/*!*/ GetImmediateClassOf(object obj, ref RubyInstanceData data) {
            RubyClass result = TryGetImmediateClassOf(obj, ref data);
            if (result != null) {
                return result;
            }

            result = GetClassOf(obj);
            if (data != null) {
                data.UpdateImmediateClass(result);
            }

            return result;
        }

        // thread-safety:
        // If the immediate class reference is being changed (a singleton is being defined) during this operation
        // it is undefined which one of the classes we return. 
        private RubyClass TryGetImmediateClassOf(object obj, ref RubyInstanceData data) {
            IRubyObject rubyObj = obj as IRubyObject;
            if (rubyObj != null) {
                return rubyObj.ImmediateClass;
            } else if (data != null || (data = TryGetInstanceData(obj)) != null) {
                return data.ImmediateClass;
            } else {
                return null;
            }
        }

        // thread-safety: must only be run under a lock that prevents singleton creation on the target object:
        private void SetInstanceSingletonOfNoLock(object obj, ref RubyInstanceData data, RubyClass/*!*/ singleton) {
            RequiresClassHierarchyLock();
            Debug.Assert(!(obj is RubyModule) && singleton != null);

            IRubyObject rubyObj = obj as IRubyObject;
            if (rubyObj != null) {
                rubyObj.ImmediateClass = singleton;
            } else if (data != null) {
                data.ImmediateClass = singleton;
            } else {
                (data = GetInstanceData(obj)).ImmediateClass = singleton;
            }
        }

        internal RubyClass TryGetSingletonOf(object obj, ref RubyInstanceData data) {
            RubyClass immediate = TryGetImmediateClassOf(obj, ref data);
            return immediate != null ? (immediate.IsSingletonClass ? immediate : null) : null;
        }

        public bool IsKindOf(object obj, RubyModule/*!*/ m) {
            return GetImmediateClassOf(obj).HasAncestor(m);
        }

        public bool IsInstanceOf(object value, object classObject) {
            RubyClass c = classObject as RubyClass;
            if (c != null) {
                return GetClassOf(value).IsSubclassOf(c);
            }

            return false;
        }

        #endregion

        #region Module Names

        /// <summary>
        /// Gets the Ruby name of the class of the given object.
        /// </summary>
        public string/*!*/ GetClassName(object obj) {
            return GetClassName(obj, false);
        }

        /// <summary>
        /// Gets the display name of the class of the given object.
        /// Includes singleton names.
        /// </summary>
        public string/*!*/ GetClassDisplayName(object obj) {
            return GetClassName(obj, true);
        }

        private string/*!*/ GetClassName(object obj, bool display) {
            // doesn't create a RubyClass for .NET types

            RubyClass cls = TryGetClassOfRubyObject(obj);
            if (cls != null) {
                return cls.GetNonNullName(this);
            }

            return GetTypeName(obj.GetType(), display);
        }

        public string/*!*/ GetTypeName(Type/*!*/ type, bool display) {
            RubyModule module;
            lock (ModuleCacheLock) {
                if (TryGetModuleNoLock(type, out module)) {
                    if (display) {
                        return module.GetDisplayName(this, false).ToString();
                    } else {
                        return module.Name;
                    }
                } else {
                    return GetQualifiedNameNoLock(type);
                }
            }
        }

        private string/*!*/ GetQualifiedNameNoLock(Type/*!*/ type) {
            return GetQualifiedNameNoLock(type, this, false);
        }

        internal static string/*!*/ GetQualifiedNameNoLock(Type/*!*/ type, RubyContext context, bool noGenericArgs) {
            return AppendQualifiedNameNoLock(new StringBuilder(), type, context, noGenericArgs).ToString();
        }

        private static StringBuilder/*!*/ AppendQualifiedNameNoLock(StringBuilder/*!*/ result, Type/*!*/ type, RubyContext context, bool noGenericArgs) {
            if (type.IsGenericParameter) {
                return result.Append(type.Name);
            }

            // arrays, by-refs, pointers:
            Type elementType = type.GetElementType();
            if (elementType != null) {
                AppendQualifiedNameNoLock(result, elementType, context, noGenericArgs);
                if (type.IsByRef) {
                    result.Append('&');
                } else if (type.IsArray) {
                    result.Append('[');
                    result.Append(',', type.GetArrayRank() - 1);
                    result.Append(']');
                } else {
                    Debug.Assert(type.IsPointer);
                    result.Append('*');
                }
                return result;
            }
            
            // qualifiers:
            if (type.DeclaringType != null) {
                AppendQualifiedNameNoLock(result, type.DeclaringType, context, noGenericArgs);
                result.Append("::");
            } else if (type.Namespace != null) {
                result.Append(type.Namespace.Replace(Type.Delimiter.ToString(), "::"));
                result.Append("::");
            }

            result.Append(ReflectionUtils.GetNormalizedTypeName(type));

            // generic args:
            if (!noGenericArgs && type.IsGenericType) {
                result.Append("[");

                var genericArgs = type.GetGenericArguments();
                for (int i = 0; i < genericArgs.Length; i++) {
                    if (i > 0) {
                        result.Append(", ");
                    }
                    
                    RubyModule module;
                    if (context != null && context.TryGetModuleNoLock(genericArgs[i], out module)) {
                        result.Append(module.Name);
                    } else {
                        AppendQualifiedNameNoLock(result, genericArgs[i], context, noGenericArgs);
                    }
                }

                result.Append("]");
            }

            return result;
        }

        private static string/*!*/ GetQualifiedName(NamespaceTracker/*!*/ namespaceTracker) {
            ContractUtils.RequiresNotNull(namespaceTracker, "namespaceTracker");
            if (namespaceTracker.Name == null) return String.Empty;

            return namespaceTracker.Name.Replace(Type.Delimiter.ToString(), "::");
        }

        #endregion

        #region Member Resolution (thread-safe)

        // thread-safe:
        public MethodResolutionResult ResolveMethod(object target, string/*!*/ name, bool includePrivate) {
            var owner = GetImmediateClassOf(target);
            return owner.ResolveMethod(name, includePrivate ? VisibilityContext.AllVisible : new VisibilityContext(owner));
        }

        // thread-safe:
        public MethodResolutionResult ResolveMethod(object target, string/*!*/ name, VisibilityContext visibility) {
            return GetImmediateClassOf(target).ResolveMethod(name, visibility);
        }

        /// <summary>
        /// Method resolution on behalf of a lexical position, so that reflection done where a
        /// `using' is in effect finds what a call from there would - Kernel#method, #public_method
        /// and #respond_to? all report refined methods in MRI.
        /// </summary>
        public MethodResolutionResult ResolveMethodWithRefinements(object target, string/*!*/ name, VisibilityContext visibility,
            RubyScope scope) {

            return GetImmediateClassOf(target).ResolveMethodWithRefinements(name, visibility, scope);
        }

        // thread-safe:
        public bool TryGetModule(RubyGlobalScope autoloadScope, string/*!*/ moduleName, out RubyModule result) {
            using (ClassHierarchyLocker()) {
                result = _objectClass;
                int pos = 0;
                while (true) {
                    int pos2 = moduleName.IndexOf("::", pos, StringComparison.Ordinal);
                    string partialName;
                    if (pos2 < 0) {
                        partialName = moduleName.Substring(pos);
                    } else {
                        partialName = moduleName.Substring(pos, pos2 - pos);
                        pos = pos2 + 2;
                    }
                    ConstantStorage tmp;
                    if (!result.TryResolveConstantNoLock(autoloadScope, partialName, out tmp)) {
                        result = null;
                        return false;
                    }
                    result = tmp.Value as RubyModule;
                    if (result == null) {
                        return false;
                    } else if (pos2 < 0) {
                        return true;
                    }
                }
            }
        }

        /// <summary>
        /// Set when a qualified constant lookup found the constant but it was private, so the
        /// lookup reported it missing (which is how MRI routes it to #const_missing). The default
        /// #const_missing words its error differently in that case.
        /// </summary>
        [ThreadStatic]
        private static RubyModule _privateConstantReference;

        internal static void SetPrivateConstantReference(RubyModule/*!*/ owner) {
            _privateConstantReference = owner;
        }

        /// <summary>
        /// For a lookup that reports a constant missing without calling #const_missing, which
        /// would otherwise consume the reference: defined? does that.
        /// </summary>
        internal static void ClearPrivateConstantReference() {
            _privateConstantReference = null;
        }

        // thread-safe:
        // Task::Factory is a static property, not a constant: say how to get it (Task.Factory). Only CLR const and
        // static readonly fields are constants (RubyModule.TryGetClrConstantField) - a constant is cached where it is
        // used, which a property like DateTime.Now must not be.
        private static string/*!*/ GetClrStaticMemberHint(RubyModule/*!*/ owner, string/*!*/ name) {
            Type type = (owner.TypeTracker != null) ? owner.TypeTracker.Type : null;
            if (type == null || type.ContainsGenericParameters || (owner.Restrictions & ModuleRestrictions.NoNameMapping) != 0) {
                return String.Empty;
            }

            foreach (var member in type.GetMember(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)) {
                if (member is PropertyInfo || member is FieldInfo || member is MethodInfo) {
                    return String.Format(" ({1} is a static {2} of the CLR type, called as a method: {0}.{1})",
                        owner.Name, name, member is MethodInfo ? "method" : member is PropertyInfo ? "property" : "field");
                }
            }
            return String.Empty;
        }

        public object ResolveMissingConstant(RubyModule/*!*/ owner, string/*!*/ name) {
            var privateReference = _privateConstantReference;
            _privateConstantReference = null;
            if (privateReference != null) {
                throw RubyExceptions.WithNameAndReceiver(this, RubyExceptions.CreateNameError(
                    String.Format("private constant {0}::{1} referenced", GetModuleDisplayName(privateReference), name)
                ), name, privateReference);
            }

            if (owner.IsObjectClass) {
                object value;
                if (RubyOps.TryGetGlobalScopeConstant(this, _globalScope, name, out value)) {
                    return value;
                }

                if ((value = _namespaces.TryGetPackageAny(name)) != null) {
                    return TrackerToModule(value);
                }
            }

            // MRI omits the owner for Object: "uninitialized constant Foo", but keeps it otherwise:
            // "uninitialized constant Math::Nope".  The test is on rb_class_real(owner), so the
            // singleton class of a plain Object counts as Object.
            RubyModule realOwner = owner;
            while (realOwner is RubyClass && ((RubyClass)realOwner).IsSingletonClass && ((RubyClass)realOwner).SuperClass != null) {
                realOwner = ((RubyClass)realOwner).SuperClass;
            }
            throw RubyExceptions.WithNameAndReceiver(this, RubyExceptions.CreateNameError(
                realOwner == ObjectClass
                    ? String.Format("uninitialized constant {0}", name)
                    : String.Format("uninitialized constant {0}::{1}", GetModuleDisplayName(owner), name) + GetClrStaticMemberHint(owner, name)
            ), name, owner);
        }

        /// <summary>
        /// How MRI names a module in an "uninitialized constant" message: whatever #name answers,
        /// falling back to #inspect for an anonymous one. Both are user-overridable, so they are
        /// called rather than read off the module.
        /// </summary>
        /// <summary>
        /// An instance of an exception class that the standard library defines in Ruby rather
        /// than the runtime in C# - looked up by name under <paramref name="owner"/> and built by
        /// calling #new on it. Null when there is no such constant, or when what is there is not
        /// an exception class.
        /// </summary>
        /// <summary>
        /// A null message leaves the exception with its class's default one (an Errno's strerror text).
        /// </summary>
        public Exception CreateLibraryException(RubyModule/*!*/ owner, string/*!*/ className, string message) {
            object exceptionClass;
            if (!owner.TryGetConstant(null, className, out exceptionClass)) {
                return null;
            }

            if (_exceptionFactory == null) {
                Interlocked.CompareExchange(
                    ref _exceptionFactory,
                    CallSite<Func<CallSite, object, object, object>>.Create(RubyCallAction.Make(this, "new", RubyCallSignature.Simple(1))),
                    null
                );
            }
            return _exceptionFactory.Target(_exceptionFactory, exceptionClass, message != null ? MutableString.Create(message, RubyEncoding.UTF8) : null) as Exception;
        }

        /// <summary>
        /// Asks ARGF which file it is reading; $FILENAME is defined as that answer.
        /// </summary>
        internal CallSite<Func<CallSite, object, object>>/*!*/ ArgfFileNameSite {
            get {
                if (_argfFileName == null) {
                    Interlocked.CompareExchange(
                        ref _argfFileName,
                        CallSite<Func<CallSite, object, object>>.Create(RubyCallAction.Make(this, "filename", RubyCallSignature.WithImplicitSelf(0))),
                        null
                    );
                }
                return _argfFileName;
            }
        }

        internal string/*!*/ GetModuleDisplayName(RubyModule/*!*/ owner) {
            if (_moduleName == null) {
                Interlocked.CompareExchange(
                    ref _moduleName,
                    CallSite<Func<CallSite, object, object>>.Create(RubyCallAction.Make(this, "name", RubyCallSignature.WithImplicitSelf(0))),
                    null
                );
            }
            object moduleName = _moduleName.Target(_moduleName, owner);

            var str = moduleName as MutableString;
            if (str != null && str.Length > 0) {
                return str.ToString();
            }
            if (moduleName != null && !(moduleName is MutableString)) {
                return moduleName.ToString();
            }
            return Inspect(owner).ToString();
        }

        // thread-safe:
        internal object TrackerToModule(object value) {
            TypeGroup typeGroup = value as TypeGroup;
            if (typeGroup != null) {
                return value;
            }

            // TypeTracker retrieved from namespace tracker should behave like a RubyClass/RubyModule:
            TypeTracker typeTracker = value as TypeTracker;
            if (typeTracker != null) {
                return GetModule(typeTracker.Type);
            }

            // NamespaceTracker retrieved from namespace tracker should behave like a RubyModule:
            NamespaceTracker namespaceTracker = value as NamespaceTracker;
            if (namespaceTracker != null) {
                return GetModule(namespaceTracker);
            }

            return value;
        }

        #endregion

        #region Object Operations: InstanceData access (thread-safe)

        // Retrieving instance data is thread safe. Operations on the instance data object are not.

        internal RubyInstanceData TryGetInstanceData(object obj) {
            IRubyObject rubyObject = obj as IRubyObject;
            if (rubyObject != null) {
                return rubyObject.TryGetInstanceData();
            }

            if (obj == null) {
                return _nilInstanceData;
            }

            RubyInstanceData result;
            if (!RubyUtils.HasObjectState(obj) && !(obj is BigInteger)) {
                if (Volatile.Read(ref _valueTypeInstanceDataCount) == 0) {
                    return null;
                }
                lock (ValueTypeInstanceDataLock) {
                    _valueTypeInstanceData.TryGetValue(obj, out result);
                }
                return result;
            }

            TryGetClrTypeInstanceData(obj, out result);
            return result;
        }

        internal bool TryGetClrTypeInstanceData(object/*!*/ obj, out RubyInstanceData result) {
            // lock-free: ConditionalWeakTable reads take no lock
            return _referenceTypeInstanceData.TryGetValue(obj, out result);
        }

        /// <summary>
        /// Drops the singleton class of an object of a CLR type, so that the next one it needs is a
        /// fresh one. IO#reopen does this: MRI gives the IO the class of the one it was reopened
        /// with, and that leaves its old singleton class behind.
        /// </summary>
        public void DropClrInstanceSingleton(object/*!*/ obj) {
            RubyInstanceData data;
            if (!(obj is IRubyObject) && TryGetClrTypeInstanceData(obj, out data) && data.InstanceSingleton != null) {
                // an adopted object (AdoptClrObject) keeps its Ruby class:
                RubyClass super = data.InstanceSingleton.SuperClass;
                data.ImmediateClass = (super.IsRubyClass && !super.IsSingletonClass) ? super : null;
            }
        }

        /// <summary>
        /// Replaces the Ruby class associated with a CLR-backed object. IO#reopen uses this to
        /// give the receiver the source IO's class, as MRI does, while retaining its object id.
        /// </summary>
        public void ReplaceClrObjectClass(object/*!*/ obj, RubyClass/*!*/ cls) {
            ContractUtils.RequiresNotNull(obj, "obj");
            ContractUtils.RequiresNotNull(cls, "cls");
            ContractUtils.Requires(!(obj is IRubyObject) && RubyUtils.HasObjectState(obj), "obj");

            RubyClass clrClass = GetOrCreateClass(obj.GetType());
            using (ClassHierarchyLocker()) {
                if (!clrClass.HasAdoptedInstances) {
                    clrClass.HasAdoptedInstances = true;
                    clrClass.MethodsUpdated("ReplaceClrObjectClass");
                }
                _hasAdoptedClrObjects = true;
                GetInstanceData(obj).ImmediateClass = cls;
            }
        }

        internal RubyInstanceData/*!*/ GetInstanceData(object obj) {
            IRubyObject rubyObject = obj as IRubyObject;
            if (rubyObject != null) {
                return rubyObject.GetInstanceData();
            }

            if (obj == null) {
                return _nilInstanceData;
            }

            // A Bignum is an object in MRI: two equal ones computed separately have different ids,
            // so each boxed BigInteger gets its own data (held weakly) instead of one per value.
            RubyInstanceData result;
            if (!RubyUtils.HasObjectState(obj) && !(obj is BigInteger)) {
                lock (ValueTypeInstanceDataLock) {
                    if (!_valueTypeInstanceData.TryGetValue(obj, out result)) {
                        _valueTypeInstanceData.Add(obj, result = new RubyInstanceData());
                        Volatile.Write(ref _valueTypeInstanceDataCount, _valueTypeInstanceData.Count);
                    }
                }
                return result;
            }

            if (_referenceTypeInstanceData.TryGetValue(obj, out result)) {
                return result;
            }
            return _referenceTypeInstanceData.GetValue(obj, _CreateInstanceData);
        }

        #endregion

        #region Object Operations: Instance variables, flags (NOT thread-safe)

        public bool HasInstanceVariables(object obj) {
            RubyInstanceData data = TryGetInstanceData(obj);
            return data != null && data.HasInstanceVariables;
        }

        /// <summary>
        /// The object's instance variables. The runtime keeps a slot or two of its own in the
        /// same table - the finalizer ObjectSpace.define_finalizer attaches is one - spelled with
        /// angle brackets so that they cannot collide with anything Ruby can name, and Ruby must
        /// not see those: they are not the object's state. #instance_variables was listing the
        /// finalizer and Marshal was trying to dump it.
        ///
        /// Names without a leading '@' but otherwise ordinary are left alone: Marshal writes a
        /// Time's zone and offset as exactly those, which is what MRI's stream carries.
        /// </summary>
        public string[]/*!*/ GetInstanceVariableNames(object obj) {
            RubyInstanceData data = TryGetInstanceData(obj);
            if (data == null) {
                return ArrayUtils.EmptyStrings;
            }

            string[] names = data.GetInstanceVariableNames();
            int visible = 0;
            foreach (string name in names) {
                if (IsVisibleInstanceVariableName(name)) {
                    visible++;
                }
            }

            if (visible == names.Length) {
                return names;
            }

            var result = new string[visible];
            int i = 0;
            foreach (string name in names) {
                if (IsVisibleInstanceVariableName(name)) {
                    result[i++] = name;
                }
            }
            return result;
        }

        private static bool IsVisibleInstanceVariableName(string name) {
            return name.Length == 0 || name[0] != '<';
        }

        public bool TryGetInstanceVariable(object obj, string/*!*/ name, out object value) {
            RubyInstanceData data = TryGetInstanceData(obj);
            if (data == null || !data.TryGetInstanceVariable(name, out value)) {
                value = null;
                return false;
            }
            return true;
        }

        private RubyInstanceData MutateInstanceVariables(object obj) {
            RubyInstanceData data;
            if (IsObjectFrozen(obj, out data)) {
                throw RubyExceptions.CreateObjectFrozenError(this, obj);
            }

            // Giving a chilled string literal an instance variable is a change a frozen string
            // could not take either, so MRI warns about it as it warns about a write.
            MutableString.ReportChilledChange(obj);
            return data;
        }

        public void SetInstanceVariable(object obj, string/*!*/ name, object value) {
            var rubyObject = obj as IRubyObject;
            (MutateInstanceVariables(obj) ?? GetInstanceData(obj)).SetInstanceVariable(
                name, value, rubyObject != null ? rubyObject.ImmediateClass : null, null
            );
        }

        /// <summary>
        /// The slow path of a cached `@x = v` (RubyOps.SetInstanceVariable): fills the site's cache.
        /// </summary>
        internal void SetInstanceVariable(object obj, object value, InstanceVariableSite/*!*/ site) {
            var rubyObject = obj as IRubyObject;
            (MutateInstanceVariables(obj) ?? GetInstanceData(obj)).SetInstanceVariable(
                site.Name, value, rubyObject != null ? rubyObject.ImmediateClass : null, site
            );
        }

        public bool TryRemoveInstanceVariable(object obj, string/*!*/ name, out object value) {
            RubyInstanceData data = MutateInstanceVariables(obj) ?? TryGetInstanceData(obj);
            if (data == null || !data.TryRemoveInstanceVariable(name, out value)) {
                value = null;
                return false;
            }
            return true;
        }

        //
        // Thread safety: target object must be a fresh object not be shared with other threads:
        // 
        // Copies instance variables from source to target object.
        // If the source has a singleton class it's members are copied to the target as well.
        // Assumes a fresh instance of target, with no instance data.
        //
        public void CopyInstanceData(object source, object target, bool copySingletonMembers) {
            RubyInstanceData targetData = null;
            Debug.Assert(!copySingletonMembers || !(source is RubyModule));
            Debug.Assert(TryGetInstanceData(target) == null);
            // target object is not a singleton:
            Debug.Assert(!copySingletonMembers || TryGetSingletonOf(target, ref targetData) == null && targetData == null);
            
            RubyInstanceData sourceData = TryGetInstanceData(source);
            if (sourceData != null) {
                if (sourceData.HasInstanceVariables) {
                    sourceData.CopyInstanceVariablesTo(targetData = GetInstanceData(target), target);
                }
            }

            if (copySingletonMembers) {
                using (ClassHierarchyLocker()) {
                    RubyClass singleton = TryGetSingletonOf(source, ref sourceData);
                    if (singleton != null) {
                        var singletonDup = singleton.Duplicate(target);
                        singletonDup.InitializeMembersFrom(singleton);

                        SetInstanceSingletonOfNoLock(target, ref targetData, singletonDup);
                    }
                }
            }
        }

        public IRubyObjectState/*!*/ GetObjectState(object/*!*/ obj) {
            return obj as IRubyObjectState ?? GetInstanceData(obj);
        }

        public IRubyObjectState TryGetObjectState(object/*!*/ obj) {
            return obj as IRubyObjectState ?? TryGetInstanceData(obj);
        }

        public bool IsObjectFrozen(object obj) {
            RubyInstanceData data;
            return IsObjectFrozen(obj, out data);
        }

        private bool IsObjectFrozen(object obj, out RubyInstanceData data) {
            // An immediate - Integer, Float, Symbol, nil, true, false - holds no state at all,
            // which is what Kernel#frozen? reports about it. Saying otherwise here let
            // #instance_variable_set and #remove_instance_variable get as far as looking for an
            // instance variable table on something that can never have one. A Symbol carries a
            // frozen flag of its own, so this has to come first for it.
            if (!RubyUtils.HasObjectState(obj) || obj is double || obj is float) {
                data = null;
                return true;
            }

            var state = obj as IRubyObjectState;
            if (state != null) {
                data = null;
                return state.IsFrozen;
            }

            data = TryGetInstanceData(obj);
            return data != null ? data.IsFrozen : false;
        }

        public bool IsObjectTainted(object obj) {
            var state = TryGetObjectState(obj);
            return state != null ? state.IsTainted : false;
        }

        public bool IsObjectUntrusted(object obj) {
            var state = TryGetObjectState(obj);
            return state != null ? state.IsUntrusted : false;
        }

        public void GetObjectTrust(object obj, out bool tainted, out bool untrusted) {
            var state = TryGetObjectState(obj);
            if (state != null) {
                tainted = state.IsTainted;
                untrusted = state.IsUntrusted;
            } else {
                tainted = false;
                untrusted = false; // TODO: default?
            }
        }

        public void FreezeObject(object obj) {
            GetObjectState(obj).Freeze();
        }

        public void SetObjectTaint(object obj, bool taint) {
            GetObjectState(obj).IsTainted = taint;
        }

        public void SetObjectTrustiness(object obj, bool untrusted) {
            GetObjectState(obj).IsUntrusted = untrusted;
        }

        public object TaintObjectBy(object obj, object source) {
            var sourceState = TryGetObjectState(source);
            if (sourceState != null) {
                bool tainted = sourceState.IsTainted;
                bool untrusted = sourceState.IsUntrusted;
                if (tainted || untrusted) {
                    var state = GetObjectState(obj);
                    state.IsTainted |= tainted;
                    state.IsUntrusted |= untrusted;
                }
            }

            return obj;
        }

        public object FreezeObjectBy(object obj, object source) {
            var sourceState = TryGetObjectState(source);
            if (sourceState != null && sourceState.IsFrozen) {
                GetObjectState(obj).Freeze();
            }
            return obj;
        }

        #endregion

        #region Dynamic Object Operations (thread-safe)

        /// <summary>
        /// Calls "inspect" and converts its result to a string using "to_s" protocol (<see cref="ConvertToSAction"/>).
        /// </summary>
        public MutableString/*!*/ Inspect(object obj) {
            RubyClass cls = GetClassOf(obj);
            var inspect = cls.InspectSite;
            var toS = cls.InspectResultConversionSite;
            return EscapeInspectResult(toS.Target(toS, inspect.Target(inspect, obj)));
        }

        /// <summary>
        /// MRI's rb_inspect: a result with non-ASCII content in an encoding other than the one
        /// output is read in (default internal, else default external) comes back escaped by
        /// rb_str_escape, as US-ASCII, so that Array#inspect or p never mix incompatible encodings.
        /// </summary>
        private MutableString/*!*/ EscapeInspectResult(MutableString/*!*/ str) {
            RubyEncoding target = DefaultInternalEncoding ?? DefaultExternalEncoding;
            RubyEncoding encoding = str.Encoding;
            if (encoding.IsAsciiIdentity && str.IsAscii()) {
                return str;
            }
            if (target.IsAsciiIdentity && encoding == target) {
                return str;
            }

            var result = new StringBuilder();
            bool unicode = encoding.IsUnicodeEncoding;
            var characters = str.GetCharacters();
            while (characters.MoveNext()) {
                var c = characters.Current;
                if (!c.IsValid) {
                    foreach (byte b in c.Invalid) {
                        result.AppendFormat(CultureInfo.InvariantCulture, "\\x{0:X2}", b);
                    }
                    continue;
                }

                int codepoint;
                if (unicode) {
                    codepoint = c.Codepoint;
                } else {
                    // a codepoint of a legacy encoding is its bytes read as one number
                    byte[] bytes = encoding.StrictEncoding.GetBytes(c.IsSurrogate ? new[] { c.Value, c.LowSurrogate } : new[] { c.Value });
                    codepoint = 0;
                    foreach (byte b in bytes) {
                        codepoint = (codepoint << 8) | b;
                    }
                }

                switch (codepoint) {
                    case '\0': result.Append("\\0"); continue;
                    case '\n': result.Append("\\n"); continue;
                    case '\r': result.Append("\\r"); continue;
                    case '\t': result.Append("\\t"); continue;
                    case '\f': result.Append("\\f"); continue;
                    case '\v': result.Append("\\v"); continue;
                    case '\b': result.Append("\\b"); continue;
                    case '\a': result.Append("\\a"); continue;
                    case 0x1b: result.Append("\\e"); continue;
                    case 0x7f: result.Append("\\c?"); continue;
                }

                if (codepoint >= 0x20 && codepoint < 0x7f && (encoding.IsAsciiIdentity || unicode)) {
                    result.Append((char)codepoint);
                } else if (unicode) {
                    result.AppendFormat(CultureInfo.InvariantCulture, codepoint < 0x10000 ? "\\u{0:X4}" : "\\u{{{0:X}}}", codepoint);
                } else {
                    result.AppendFormat(CultureInfo.InvariantCulture, codepoint < 0x100 ? "\\x{0:X2}" : "\\x{{{0:X}}}", codepoint);
                }
            }
            return MutableString.CreateAscii(result.ToString());
        }

        #endregion

        #region Global Variables: General access (thread-safe)

        public object GetGlobalVariable(string/*!*/ name) {
            object value;
            TryGetGlobalVariable(null, name, out value);
            return value;
        }

        public void DefineGlobalVariable(string/*!*/ name, object value) {
            lock (GlobalVariablesLock) {
                _globalVariables[name] = new GlobalVariableInfo(value);
                GlobalVariablesChanged();
            }
        }

        public void DefineReadOnlyGlobalVariable(string/*!*/ name, object value) {
            lock (GlobalVariablesLock) {
                _globalVariables[name] = new ReadOnlyGlobalVariableInfo(value);
                GlobalVariablesChanged();
            }
        }

        public void DefineGlobalVariable(string/*!*/ name, GlobalVariable/*!*/ variable) {
            ContractUtils.RequiresNotNull(variable, "variable");
            lock (GlobalVariablesLock) {
                _globalVariables[name] = variable;
                GlobalVariablesChanged();
            }
        }

        internal void DefineGlobalVariableNoLock(string/*!*/ name, GlobalVariable/*!*/ variable) {
            _globalVariables[name] = variable;
            GlobalVariablesChanged();
        }

        public bool DeleteGlobalVariable(string/*!*/ name) {
            lock (GlobalVariablesLock) {
                GlobalVariablesChanged();
                return _globalVariables.Remove(name);
            }
        }

        public void AliasGlobalVariable(string/*!*/ newName, string/*!*/ oldName) {
            lock (GlobalVariablesLock) {
                GlobalVariable existing;
                if (!_globalVariables.TryGetValue(oldName, out existing)) {
                    DefineGlobalVariableNoLock(oldName, existing = new GlobalVariableInfo(null, false));
                }
                _globalVariables[newName] = existing;
                GlobalVariablesChanged();
            }
        }

        // null scope should be used only when accessed outside Ruby code; in that case no scoped variables are available
        // we need scope here, bacause the variable might be an alias for scoped variable (regex matches):
        public void SetGlobalVariable(RubyScope scope, string/*!*/ name, object value) {
            lock (GlobalVariablesLock) {
                GlobalVariable global;
                if (_globalVariables.TryGetValue(name, out global)) {
                    global.SetValue(this, scope, name, value);
                } else {
                    _globalVariables[name] = new GlobalVariableInfo(value);
                    GlobalVariablesChanged();
                }
            }

            // Kernel#trace_var handlers run after the assignment and outside the lock: they are
            // arbitrary Ruby code and may well touch other globals. The flag keeps the common
            // case - nothing traced at all - to a single field read.
            if (_anyGlobalVariableTraced) {
                FireGlobalVariableTraces(scope, name, value);
            }
        }

        // null scope should be used only when accessed outside Ruby code; in that case no scoped variables are available
        // we need scope here, bacause the variable might be an alias for scoped variable (regex matches):
        public bool TryGetGlobalVariable(RubyScope scope, string/*!*/ name, out object value) {
            lock (GlobalVariablesLock) {
                GlobalVariable global;
                if (_globalVariables.TryGetValue(name, out global)) {
                    value = global.GetValue(this, scope);
                    return true;
                }
            }
            value = null;
            return false;
        }

        internal bool TryGetGlobalVariable(string/*!*/ name, out GlobalVariable variable) {
            lock (GlobalVariablesLock) {
                return _globalVariables.TryGetValue(name, out variable);
            }
        }

        /// <summary>The variable the name denotes and the version of the name table it was found at.</summary>
        internal bool TryGetGlobalVariable(string/*!*/ name, out GlobalVariable variable, out int version) {
            lock (GlobalVariablesLock) {
                version = _globalVariablesVersion;
                return _globalVariables.TryGetValue(name, out variable);
            }
        }

        /// <summary>
        /// The value of a variable a GlobalVariableSite found: what TryGetGlobalVariable would
        /// answer for its name, minus the lookup. An ordinary global is a field read; anything
        /// else (a special or read-only variable, or one a library defined) keeps running under
        /// the lock.
        /// </summary>
        internal object GetGlobalVariableValue(GlobalVariable/*!*/ variable, RubyScope scope) {
            lock (GlobalVariablesLock) {
                return variable.GetValue(this, scope);
            }
        }

        /// <summary>
        /// Runs the Kernel#trace_var handlers of a global that has just been assigned outside
        /// SetGlobalVariable (which runs them itself).
        /// </summary>
        internal void GlobalVariableAssigned(RubyScope scope, string/*!*/ name, object value) {
            if (_anyGlobalVariableTraced) {
                FireGlobalVariableTraces(scope, name, value);
            }
        }

        #endregion

        #region Global Variables: Kernel#trace_var handlers (thread-safe)

        /// <summary>
        /// Name (without the $) to the handlers registered for it, most recent first - which is
        /// the order MRI fires them in. Created on the first trace_var; null until then, so an
        /// untraced program pays nothing but the _anyGlobalVariableTraced read.
        /// </summary>
        private Dictionary<string/*!*/, List<object>/*!*/> _globalVariableTraces;
        private volatile bool _anyGlobalVariableTraced;

        /// <summary>A trace_var handler given as a string, with the context it is evaluated in.</summary>
        public sealed class GlobalVariableTraceCommand {
            public readonly MutableString/*!*/ Code;
            public readonly RubyScope Scope;
            public readonly object Self;

            public GlobalVariableTraceCommand(MutableString/*!*/ code, RubyScope scope, object self) {
                Code = code;
                Scope = scope;
                if (scope != null) {
                    scope.MarkCaptured();
                }
                Self = self;
            }
        }

        public void AddGlobalVariableTrace(string/*!*/ name, object/*!*/ handler) {
            lock (GlobalVariablesLock) {
                if (_globalVariableTraces == null) {
                    _globalVariableTraces = new Dictionary<string, List<object>>();
                }
                List<object> handlers;
                if (!_globalVariableTraces.TryGetValue(name, out handlers)) {
                    _globalVariableTraces[name] = handlers = new List<object>();
                }
                handlers.Insert(0, handler);
                _anyGlobalVariableTraced = true;
            }
        }

        /// <summary>
        /// Removes the handlers registered for the variable - all of them, or the one equal to
        /// <paramref name="handler"/> - and returns those removed, or null if there were none.
        /// </summary>
        public List<object> RemoveGlobalVariableTraces(string/*!*/ name, object handler) {
            lock (GlobalVariablesLock) {
                List<object> handlers;
                if (_globalVariableTraces == null || !_globalVariableTraces.TryGetValue(name, out handlers)) {
                    return null;
                }

                List<object> removed;
                if (handler == null) {
                    removed = handlers;
                    _globalVariableTraces.Remove(name);
                } else {
                    removed = new List<object>();
                    for (int i = handlers.Count - 1; i >= 0; i--) {
                        if (ReferenceEquals(handlers[i], handler)) {
                            removed.Insert(0, handlers[i]);
                            handlers.RemoveAt(i);
                        }
                    }
                    if (handlers.Count == 0) {
                        _globalVariableTraces.Remove(name);
                    }
                    if (removed.Count == 0) {
                        return null;
                    }
                }

                _anyGlobalVariableTraced = _globalVariableTraces.Count > 0;
                return removed;
            }
        }

        private void FireGlobalVariableTraces(RubyScope scope, string/*!*/ name, object value) {
            object[] handlers;
            lock (GlobalVariablesLock) {
                List<object> registered;
                if (_globalVariableTraces == null || !_globalVariableTraces.TryGetValue(name, out registered)) {
                    return;
                }
                // A copy, so that a handler that calls trace_var or untrace_var on the same
                // variable does not mutate the list being walked.
                handlers = registered.ToArray();
            }

            foreach (var handler in handlers) {
                var command = handler as GlobalVariableTraceCommand;
                if (command != null) {
                    RubyUtils.Evaluate(command.Code, command.Scope ?? scope, command.Self, null, null, 1);
                } else {
                    var site = GetOrCreateSendSite<Func<CallSite, object, object, object>>("call", RubyCallSignature.Simple(1));
                    site.Target(site, handler, value);
                }
            }
        }

        #endregion

        #region Global Variables: Special variables (thread-safe)

        /// <summary>
        /// $!
        /// </summary>
        [ThreadStatic]
        private static Exception _currentException;

        public Exception CurrentException {
            get { return _currentException; }
            internal set { _currentException = RubyUtils.GetVisibleException(value); }
        }

        internal Exception SetCurrentException(object value) {
            Exception e = value as Exception;

            // "$! = nil" is allowed
            if (value != null && e == null) {
                throw RubyExceptions.CreateTypeError("assigning non-exception to $!");
            }

            Debug.Assert(RubyUtils.GetVisibleException(e) == e);
            return _currentException = e;
        }

        internal RubyArray GetCurrentExceptionBacktrace() {
            // Under certain circumstances MRI invokes "backtrace" method, but it is quite unstable (crashes sometimes).
            // Therefore we don't call the method and return the backtrace immediately.
            Exception e = _currentException;
            return (e != null) ? RubyExceptionData.GetInstance(e).Backtrace : null;
        }

        internal RubyArray SetCurrentExceptionBacktrace(object value) {
            // first check availability of the current exception:
            Exception e = _currentException;
            if (e == null) {
                throw RubyExceptions.CreateArgumentError("$! not set");
            }

            // MRI's errat_setter goes through #set_backtrace, which also takes an Array of
            // Thread::Backtrace::Location (the Ruby-level layer keeps those)
            if (_setBacktraceSite == null) {
                System.Threading.Interlocked.CompareExchange(ref _setBacktraceSite,
                    CallSite<Func<CallSite, object, object, object>>.Create(RubyCallAction.Make(this, "set_backtrace", 1)), null);
            }
            _setBacktraceSite.Target(_setBacktraceSite, e, value);
            return RubyExceptionData.GetInstance(e).Backtrace;
        }

        private CallSite<Func<CallSite, object, object, object>> _setBacktraceSite;
        
        /// <summary>
        /// $SAFE
        /// </summary>
        [ThreadStatic]
        private static int _currentSafeLevel;

        public int CurrentSafeLevel {
            get { return _currentSafeLevel; }
        }

        public void SetSafeLevel(int value) {
            if (_currentSafeLevel <= value) {
                _currentSafeLevel = value;
            } else {
                throw RubyExceptions.CreateSecurityError(String.Format("tried to downgrade safe level from {0} to {1}",
                    _currentSafeLevel, value));
            }
        }

        #endregion

        #region Symbols

        private readonly Dictionary<MutableString, RubySymbol>/*!*/ _symbols;
        private object SymbolsLock { get { return _symbols; } }

        public RubySymbol/*!*/ CreateSymbol(MutableString/*!*/ str) {
            return CreateSymbol(str, true);
        }

        public RubySymbol/*!*/ CreateAsciiSymbol(string/*!*/ str) {
            // TODO: do not allocate the MutableString if not needed?
            return CreateSymbol(MutableString.CreateAscii(str), false);
        }

        public RubySymbol/*!*/ CreateSymbol(string/*!*/ str, RubyEncoding/*!*/ encoding) {
            // TODO: do not allocate the MutableString if not needed?
            return CreateSymbol(MutableString.CreateMutable(str, encoding), false);
        }

        public RubySymbol/*!*/ CreateSymbol(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            var mstr = MutableString.CreateBinary(bytes, encoding);
            // TODO: do not allocate the MutableString if not needed?
            return CreateSymbol(mstr, false);
        }

        /// <summary>
        /// Creates a symbol that holds on a given string or its copy, if <c>clone</c> is true.
        /// Freezes the string the symbol holds on.
        /// </summary>
        public RubySymbol/*!*/ CreateSymbol(MutableString/*!*/ str, bool clone) {
            // A symbol whose name is ASCII-only is US-ASCII whatever the string it was interned
            // from was tagged with, so that "abc".b.to_sym.equal?(:abc) and the encoding does
            // not depend on which spelling happened to reach the symbol table first. An
            // ASCII-incompatible encoding is left alone: "abc".encode("utf-16le").to_sym keeps
            // UTF-16LE in MRI, because its bytes are not those of the name. (CRuby 4.0.6.)
            if (str.Encoding != RubyEncoding.Ascii && str.Encoding.IsAsciiIdentity && str.IsAscii()) {
                str = MutableString.Create(str.ToString(), RubyEncoding.Ascii);
                clone = false;
            }

            RubySymbol result;
            lock (SymbolsLock) {
                if (!_symbols.TryGetValue(str, out result)) {
                    result = new RubySymbol((clone ? str.Clone() : str).Freeze(), _symbols.Count + RubySymbol.MinId, _runtimeId);
                    _symbols.Add(str, result);
                }
            }
            return result;
        }

        /// <summary>
        /// Searches symbol table for a symbol of given id - slow operation (linear search).
        /// </summary>
        public RubySymbol FindSymbol(int id) {
            lock (SymbolsLock) {
                foreach (var symbol in _symbols.Values) {
                    if (symbol.Id == id) {
                        return symbol;
                    }
                }
            }
            return null;
        }

        public RubyArray/*!*/ GetAllSymbols() {
            lock (SymbolsLock) {
                return new RubyArray(_symbols.Values);
            }
        }

        /// <summary>
        /// TODO
        /// Ruby 1.9 allows arbitrarily encoded identifiers. We could use a RubySymbol in internal tables, however that would also require
        /// dynamic sites to use RubySymbols and CLR methods cached in the tables to be represented by RubySymbols. Seems like too much overhead.
        /// 
        /// For now we take the same approach as with file system paths. We represent the identifiers as CLR strings and whenever we convert 
        /// them to RubySymbol or MutableString we use either the current KCODE or (K)UTF8 encoding. This doesn't guarantee a correct roundtrip 
        /// in the case that a method is defined under K-UTF8, its name is retrieved under K-SJIS, converted to a string and the bytes are examined. 
        /// The conversion might blow up if it contains a character that is not available in SJIS.
        /// 
        /// <para>
        /// Note that the way how 1.9 works makes non-ascii identifiers encoded by non-UTF-8 encoding almost useless. 
        /// Libraries written in such encoding are only usable from code written in the same encoding. 
        /// Thus the common case would probably be that all scripts use UTF-8. For example,
        /// <code>
        /// lib1.rb:
        /// #encoding: UTF-8
        /// class C; def S; end; end
        /// 
        /// lib2.rb:
        /// #encoding: SJIS
        /// class D; def S; end; end
        /// 
        /// c.rb:
        /// #encoding: UTF-8
        /// require 'lib1'
        /// require 'lib2'
        /// C.new.S             # works
        /// D.new.S             # error: no method S
        /// </code>
        /// </para>
        /// 
        /// <para>
        /// Ruby 1.9 also allows incorrectly encoded method names (not identifiers in source code though):
        /// <code>
        /// #encoding: UTF-8
        /// class C
        ///   define_method(:"foo\xce") { }
        /// end
        /// </code>
        /// There seems to be no reason why we should support this.
        /// </para>
        /// </summary>
        public RubyEncoding/*!*/ GetIdentifierEncoding() {
            // TODO:
            return RubyEncoding.UTF8;
        }

        public RubySymbol/*!*/ EncodeIdentifier(string/*!*/ identifier) {
            return CreateSymbol(identifier, GetIdentifierEncoding());
        }

        /// <summary>
        /// Returns an identifier encoded as MutableStrings (Ruby 1.8) or Symbols (Ruby 1.9).
        /// </summary>
        public object/*!*/ StringifyIdentifier(string/*!*/ identifier) {
            // Interning goes through a MutableString and the symbol table's lock; an identifier's symbol
            // never changes and symbols are never collected, so remember it by the CLR string.
            RubySymbol result;
            if (!_identifierSymbols.TryGetValue(identifier, out result)) {
                result = CreateSymbol(identifier, RubyEncoding.UTF8);
                _identifierSymbols.TryAdd(identifier, result);
            }
            return result;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RubySymbol>/*!*/ _identifierSymbols =
            new System.Collections.Concurrent.ConcurrentDictionary<string, RubySymbol>(StringComparer.Ordinal);
        
        /// <summary>
        /// Returns an array of identifiers encoded as MutableStrings (Ruby 1.8) or Symbols (Ruby 1.9).
        /// </summary>
        public RubyArray/*!*/ StringifyIdentifiers(IList<string>/*!*/ identifiers) {
            var result = new RubyArray(identifiers.Count);
            foreach (var id in identifiers) {
                result.Add(StringifyIdentifier(id));
            }
            return result;
        }

        #endregion

        #region IO (thread-safe)

        private sealed class FileDescriptor {
            public int DuplicateCount;
            public readonly Stream/*!*/ Stream;

            public FileDescriptor(Stream/*!*/ stream) {
                Assert.NotNull(stream);
                Stream = stream;
                DuplicateCount = 1;
            }

            public void Close() {
                DuplicateCount--;
                if (DuplicateCount == 0) {
                    Stream.Close();
                }
            }
        }

        private readonly List<FileDescriptor>/*!*/ _fileDescriptors = new List<FileDescriptor>(10);

        public const int StandardInputDescriptor = 0;
        public const int StandardOutputDescriptor = 1;
        public const int StandardErrorOutputDescriptor = 2;

        public object StandardInput { get; set; }
        public object StandardOutput { get; set; }
        public object StandardErrorOutput { get; set; }

        private FileDescriptor TryGetFileDescriptorNoLock(int descriptor) {
            return (descriptor < 0 || descriptor >= _fileDescriptors.Count) ? null : _fileDescriptors[descriptor];
        }

        private int AddFileDescriptorNoLock(FileDescriptor/*!*/ fd) {
            for (int i = 0; i < _fileDescriptors.Count; i++) {
                if (_fileDescriptors[i] == null) {
                    _fileDescriptors[i] = fd;
                    return i;
                }
            }
            _fileDescriptors.Add(fd);
            return _fileDescriptors.Count - 1;
        }

        public Stream GetStream(int descriptor) {
            lock (_fileDescriptors) {
                var fd = TryGetFileDescriptorNoLock(descriptor);
                return (fd != null) ? fd.Stream : null;
            }
        }

        public void SetStream(int descriptor, Stream/*!*/ stream) {
            ContractUtils.RequiresNotNull(stream, "stream");

            lock (_fileDescriptors) {
                var fd = TryGetFileDescriptorNoLock(descriptor);
                if (fd == null) {
                    throw RubyExceptions.CreateEBADF();
                }
                if (fd.Stream != stream) {
                    fd.Close();
                    _fileDescriptors[descriptor] = new FileDescriptor(stream);
                }
            }
        }

        public void RedirectFileDescriptor(int descriptor, int toDescriptor) {
            lock (_fileDescriptors) {
                var fd = TryGetFileDescriptorNoLock(descriptor);
                if (fd == null) {
                    throw RubyExceptions.CreateEBADF();
                }

                var toFd = TryGetFileDescriptorNoLock(toDescriptor);
                if (toFd == null) {
                    throw RubyExceptions.CreateEBADF();
                }

                if (fd == toFd) {
                    return;
                }

                fd.Close();
                toFd.DuplicateCount++;
                _fileDescriptors[descriptor] = toFd;
            }
        }

        /// <summary>
        /// Records a stream against a descriptor number the operating system chose, filling
        /// the table out to it. Only an adopted descriptor needs this: everything IronRuby
        /// opens itself gets the next free index instead.
        /// </summary>
        public void SetOrAllocateDescriptor(int descriptor, Stream/*!*/ stream) {
            ContractUtils.RequiresNotNull(stream, "stream");
            lock (_fileDescriptors) {
                while (_fileDescriptors.Count <= descriptor) {
                    _fileDescriptors.Add(null);
                }
                _fileDescriptors[descriptor] = new FileDescriptor(stream);
            }
        }

        public int AllocateFileDescriptor(Stream/*!*/ stream) {
            ContractUtils.RequiresNotNull(stream, "stream");
            lock (_fileDescriptors) {
                return AddFileDescriptorNoLock(new FileDescriptor(stream));
            }
        }

        public int DuplicateFileDescriptor(int descriptor) {
            lock (_fileDescriptors) {
                var fd = TryGetFileDescriptorNoLock(descriptor);
                if (fd == null) {
                    throw RubyExceptions.CreateEBADF();
                }
                fd.DuplicateCount++;
                return AddFileDescriptorNoLock(fd);
            }
        }

        public void CloseStream(int descriptor) {
            lock (_fileDescriptors) {
                var fd = TryGetFileDescriptorNoLock(descriptor);
                if (fd == null) {
                    throw RubyExceptions.CreateEBADF();
                }
                fd.Close();
                _fileDescriptors[descriptor] = null;
            }
        }

        public void RemoveFileDescriptor(int descriptor) {
            lock (_fileDescriptors) {
                if (TryGetFileDescriptorNoLock(descriptor) == null) {
                    throw RubyExceptions.CreateEBADF();
                }

                _fileDescriptors[descriptor] = null;
            }
        }

        private readonly RuntimeErrorSink/*!*/ _runtimeErrorSink;

        public RuntimeErrorSink/*!*/ RuntimeErrorSink {
            get { return _runtimeErrorSink; }
        }

        public void ReportWarning(string/*!*/ message) {
            ReportWarning(message, false);
        }

        public void ReportWarning(string/*!*/ message, bool isVerbose) {
            _runtimeErrorSink.Add(null, message, SourceSpan.None, isVerbose ? Errors.RuntimeVerboseWarning : Errors.RuntimeWarning, Severity.Warning);
        }

        #region Warning categories, chilled string literals

        /// <summary>
        /// Warning[:deprecated] and friends. MRI's defaults: deprecated follows $VERBOSE being
        /// true, experimental is on, the rest are off.
        /// </summary>
        private readonly Dictionary<string, bool>/*!*/ _warningCategories = new Dictionary<string, bool>();

        public bool IsWarningEnabled(string/*!*/ category) {
            bool enabled;
            lock (_warningCategories) {
                return _warningCategories.TryGetValue(category, out enabled) && enabled;
            }
        }

        public void SetWarningEnabled(string/*!*/ category, bool enabled) {
            lock (_warningCategories) {
                _warningCategories[category] = enabled;
            }
        }

        // Names of methods compiled with a use of their block, while :strict_unused_block was off
        // (MRI's iseq_set_use_block): a block passed to a same-named method is not warned about.
        private readonly HashSet<string>/*!*/ _methodNamesUsingBlock = new HashSet<string>();

        internal void NoteMethodUsingBlock(string/*!*/ name) {
            if (IsWarningEnabled("strict_unused_block")) {
                return;
            }
            lock (_methodNamesUsingBlock) {
                _methodNamesUsingBlock.Add(name);
            }
        }

        internal bool IsMethodNameUsingBlock(string/*!*/ name) {
            lock (_methodNamesUsingBlock) {
                return _methodNamesUsingBlock.Contains(name);
            }
        }

        public static readonly string[]/*!*/ WarningCategories =
            new[] { "deprecated", "experimental", "performance", "strict_unused_block" };

        private void InitializeWarningCategories() {
            SetWarningEnabled("deprecated", false);
            SetWarningEnabled("experimental", true);
            SetWarningEnabled("performance", false);
            SetWarningEnabled("strict_unused_block", false);

            foreach (var category in RubyOptions.WarningCategoryFlags) {
                if (category.StartsWith("no-", StringComparison.Ordinal)) {
                    SetWarningEnabled(category.Substring(3), false);
                } else {
                    SetWarningEnabled(category, true);
                }
            }
        }

        /// <summary>
        /// A warning in the :deprecated category. MRI gates every categorised warning on
        /// $VERBOSE not being nil *and* the category being enabled (error.c rb_warn_deprecated),
        /// and prefixes the message with the location of the frame that triggered it.
        /// </summary>
        public void ReportDeprecationWarning(string/*!*/ message) {
            ReportCategoryWarning("deprecated", message);
        }

        // Set by Module#deprecate_constant and never cleared. Constant lookup is hot and almost no
        // program deprecates a constant, so every read hook is behind this one field read.
        private bool _hasDeprecatedConstants;

        public bool HasDeprecatedConstants {
            get { return _hasDeprecatedConstants; }
        }

        internal void NoteDeprecatedConstant() {
            _hasDeprecatedConstants = true;
        }

        /// <summary>
        /// The warning Module#deprecate_constant asks for, emitted on reference, on #const_get and
        /// on #remove_const - but not on #defined?, #const_defined? or #const_source_location,
        /// which MRI keeps silent.
        /// </summary>
        public void ReportConstantDeprecation(RubyModule/*!*/ owner, string/*!*/ name) {
            ReportDeprecationWarning(String.Format("constant {0}::{1} is deprecated",
                GetModuleDisplayName(owner), name));
        }

        public void ReportCategoryWarning(string/*!*/ category, string/*!*/ message) {
            if (Verbose == null || !IsWarningEnabled(category)) {
                return;
            }

            var text = new StringBuilder();
            string location = TryGetCurrentSourceLocation();
            if (location != null) {
                text.Append(location).Append(": ");
            }
            text.Append("warning: ").Append(message).Append('\n');

            DispatchWarning(MutableString.CreateMutable(text.ToString(), RubyEncoding.UTF8), category);
        }

        /// <summary>
        /// Writes a fully formed warning message to $stderr, resolving $stderr dynamically and
        /// adding nothing to the text. This is the tail of the default Warning#warn.
        /// </summary>
        public void WriteWarningMessage(MutableString/*!*/ message) {
            _runtimeErrorSink.WriteMessage(message);
        }

        #region Warning.warn dispatch

        private RubyModule _warningModule;
        private CallSite<Func<CallSite, object, object, object>> _warnSite;
        private CallSite<Func<CallSite, object, object, object, object>> _warnCategorySite;

        /// <summary>
        /// Hands a finished warning message to Warning.warn so that a Ruby level override sees it,
        /// mirroring MRI's rb_write_warning_str / rb_warn_category.
        ///
        /// <paramref name="category"/> is null for the uncategorised warnings (MRI's rb_warn),
        /// which call the hook with a single positional argument and no category: keyword at all.
        /// A categorised warning passes category:, unless the installed warn takes exactly one
        /// argument - MRI drops the keyword in that case (rb_warning_warn_arity).
        ///
        /// Falls back to writing straight to $stderr when Warning.warn is not resolvable yet: the
        /// very first warnings are emitted while the prelude is still being parsed, before its
        /// `extend self' has run.
        /// No re-entrancy guard and no exception swallowing - MRI has neither.
        /// </summary>
        internal void DispatchWarning(MutableString/*!*/ message, string category) {
            RubyModule warningModule = _warningModule;
            if (warningModule == null) {
                object value;
                warningModule = ObjectClass.TryGetConstant(null, "Warning", out value) ? value as RubyModule : null;
                if (warningModule == null) {
                    _runtimeErrorSink.WriteMessage(message);
                    return;
                }
                _warningModule = warningModule;
            }

            var resolved = GetImmediateClassOf(warningModule).ResolveMethod("warn", VisibilityContext.AllVisible);
            // Until the prelude's `extend self' has run - the main script is parsed before that - the
            // lookup falls through to Kernel#warn, which would add a newline of its own.
            if (!resolved.Found || resolved.Info.DeclaringModule == KernelModule) {
                _runtimeErrorSink.WriteMessage(message);
                return;
            }

            if (category == null || resolved.Info.GetArity() == 1) {
                if (_warnSite == null) {
                    Interlocked.CompareExchange(
                        ref _warnSite,
                        CallSite<Func<CallSite, object, object, object>>.Create(RubyCallAction.Make(this, "warn", 1)),
                        null
                    );
                }
                _warnSite.Target(_warnSite, warningModule, message);
                return;
            }

            var keywords = new Hash(this);
            keywords.IsKeywordArguments = true;
            keywords[CreateAsciiSymbol("category")] = CreateAsciiSymbol(category);

            if (_warnCategorySite == null) {
                Interlocked.CompareExchange(
                    ref _warnCategorySite,
                    CallSite<Func<CallSite, object, object, object, object>>.Create(RubyCallAction.Make(this, "warn", 2)),
                    null
                );
            }
            _warnCategorySite.Target(_warnCategorySite, warningModule, message, keywords);
        }

        #endregion

        /// <summary>
        /// The first mutation of a chilled string literal - one written in a file that said
        /// nothing about frozen_string_literal. MRI warns that it will be frozen in a future
        /// version, under the deprecated category rather than $VERBOSE.
        ///
        /// </summary>
        private void ReportChilledStringMutation(MutableString/*!*/ str) {
            // MRI's rb_warn_unchilled_literal gates on $VERBOSE not being nil as well as on the
            // category, like every other categorised warning.
            if (Verbose == null || !IsWarningEnabled("deprecated")) {
                return;
            }

            string createdAt = MutableString.GetLiteralSite(str);
            var message = new StringBuilder();

            string mutatedAt = TryGetCurrentSourceLocation();
            if (mutatedAt != null) {
                message.Append(mutatedAt).Append(": ");
            }

            if (str.IsChilledSymbolString) {
                // Symbol#to_s hands back a chilled string, and MRI says which symbol it came from
                // rather than calling it a literal - there is no literal to point at. The symbol
                // is spelled exactly as the string reads, quoting and all, as MRI spells it.
                message.Append("warning: string returned by :").Append(str.ToString()).Append(".to_s will be frozen in the future");
            } else {
                message.Append("warning: literal string will be frozen in the future");
                if (createdAt == null) {
                    message.Append(" (run with --debug-frozen-string-literal for more information)");
                }
            }
            message.Append('\n');

            if (createdAt != null) {
                message.Append(createdAt).Append(": info: the string was created here\n");
            }

            DispatchWarning(MutableString.CreateMutable(message.ToString(), RubyEncoding.UTF8), "deprecated");
        }

        /// <summary>
        /// "file:line" for the Ruby frame that is running, or null when there is no Ruby frame to
        /// name. Taken from the backtrace machinery, so it is only worth asking for on a path that
        /// is already reporting something.
        /// </summary>
        private string TryGetCurrentSourceLocation() {
            string path;
            int line;
            return TryGetCurrentSourceLocation(out path, out line) ? path + ":" + line : null;
        }

        /// <summary>
        /// Where in Ruby the current call is, recovered from the call stack. A warning raised by
        /// the runtime carries no source unit of its own, but MRI still prefixes the location it
        /// was raised from, so the sink asks for it rather than printing "unknown:0".
        /// </summary>
        internal bool TryGetCurrentSourceLocation(out string path, out int line) {
            path = null;
            line = 0;
            try {
                var backtrace = RubyExceptionData.CreateBacktrace(this, 0);
                if (backtrace == null || backtrace.Count == 0) {
                    return false;
                }

                // "file:line:in `method'", read from the right: a file name can hold colons of
                // its own - an eval inside an eval is called "(eval at file:line)" - so the line
                // number is the last thing before ":in", not the first thing after the path.
                // A frame without a file (":0:in ..." - a library method, or a CLR frame when
                // backtraces show them) is not where the code was written: the first one that
                // names a file is.
                foreach (var entry in backtrace) {
                    string frame = entry.ToString();
                    int inIndex = frame.LastIndexOf(":in ", StringComparison.Ordinal);
                    string head = (inIndex >= 0) ? frame.Substring(0, inIndex) : frame;

                    int colon = head.LastIndexOf(':');
                    if (colon <= 0 || !Int32.TryParse(head.Substring(colon + 1), out line)) {
                        continue;
                    }

                    path = head.Substring(0, colon);
                    return true;
                }
                line = 0;
                return false;
            } catch (Exception) {
                path = null;
                line = 0;
                return false;
            }
        }

        #endregion

        public RubyEncoding/*!*/ GetPathEncoding() {
            // On everything but Windows the filesystem encoding follows the default external
            // encoding, so Encoding.default_external= moves it too.
            //
            // Only as far as the path layer can carry it, though. Paths here round-trip through a
            // .NET string, so the filesystem encoding has to be able to represent one: ASCII
            // incompatible encodings cannot (MRI's answer to Encoding.default_external =
            // Encoding::UTF_16BE is that every path operation raises
            // Encoding::CompatibilityError, which this layer has no way to report), and neither
            // can ASCII-8BIT, which rejects every character above U+00FF.
            //
            // On Windows file names are Unicode and MRI's filesystem encoding is UTF-8 whatever
            // the console's code page: under code page 850 a path holding a character CP850 has
            // no byte for was an Errno::EINVAL here.
            if (Path.DirectorySeparatorChar == '\\') {
                return RubyEncoding.UTF8;
            }
            var external = _defaultExternalEncoding;
            if (external == null || !external.IsAsciiIdentity || external == RubyEncoding.Binary) {
                return RubyEncoding.UTF8;
            }
            return external;
        }

        /// <summary>
        /// Creates a mutable string encoded using the path (file system) encoding.
        /// </summary>
        /// <exception cref="EncoderFallbackException">Invalid characters present.</exception>
        public MutableString/*!*/ EncodePath(string/*!*/ path) {
            return EncodePath(path, GetPathEncoding());
        }

        public MutableString TryEncodePath(string/*!*/ path) {
            var result = MutableString.Create(path, GetPathEncoding());
            return result.ContainsInvalidCharacters() ? null : result;
        }

        internal static MutableString/*!*/ EncodePath(string/*!*/ path, RubyEncoding/*!*/ encoding) {
            var result = MutableString.Create(path, encoding);
            if (result.ContainsInvalidCharacters()) {

            }
            try {
                return MutableString.Create(path, encoding).CheckEncoding();
            } catch (EncoderFallbackException e) {
                throw RubyExceptions.CreateEINVAL(
                    e,
                    "Path \"{0}\" contains characters that cannot be represented in encoding {1}: {2}",
                    path.ToAsciiString(),
                    encoding.Name,
                    e.Message
                );
            }
        }

        /// <summary>
        /// Transcodes given mutable string to Unicode path that can be passed to the .NET IO system (or host).
        /// </summary>
        /// <exception cref="InvalidError">Invalid characters present.</exception>
        public string/*!*/ DecodePath(MutableString/*!*/ path) {
            try {
                if (path.Encoding == RubyEncoding.Binary) {
                    // force UTF8 encoding to make round-trip work:
                    return path.ToString(Encoding.UTF8);
                } else {
                    return path.ConvertToString();
                }
            } catch (DecoderFallbackException) {
                throw RubyExceptions.CreateEINVAL("Invalid multi-byte sequence in path '{0}'", path.ToAsciiString());
            }
        }

        #endregion

        #region Library Data (thread-safe)

        private Dictionary<object, object> _libraryData;

        private void EnsureLibraryData() {
            if (_libraryData == null) {
                Interlocked.CompareExchange(ref _libraryData, new Dictionary<object, object>(), null);
            }
        }

        public bool TryGetLibraryData(object key, out object value) {
            EnsureLibraryData();

            lock (_libraryData) {
                return _libraryData.TryGetValue(key, out value);
            }
        }

        public object GetOrCreateLibraryData(object key, Func<object> valueFactory) {
            object value;
            if (TryGetLibraryData(key, out value)) {
                return value;
            }

            value = valueFactory();

            object actualResult;
            TryAddLibraryData(key, value, out actualResult);
            return actualResult;
        }

        public bool TryAddLibraryData(object key, object value, out object actualValue) {
            EnsureLibraryData();

            lock (_libraryData) {
                if (_libraryData.TryGetValue(key, out actualValue)) {
                    return false;
                }

                _libraryData.Add(key, actualValue = value);
                return true;
            }
        }

        public void TrySetLibraryData(object key, object value) {
            EnsureLibraryData();

            lock (_libraryData) {
                _libraryData[key] = value;
            }
        }

        #endregion

        #region Parsing, Compilation (thread-safe)

        public override ScriptCode CompileSourceCode(SourceUnit/*!*/ sourceUnit, CompilerOptions/*!*/ options, ErrorSink/*!*/ errorSink) {
            ContractUtils.RequiresNotNull(sourceUnit, "sourceUnit");
            ContractUtils.RequiresNotNull(options, "options");
            ContractUtils.RequiresNotNull(errorSink, "errorSink");
            ContractUtils.Requires(sourceUnit.LanguageContext == this, "Language mismatch.");

#if DEBUG
            if (RubyOptions.LoadFromDisk) {
                string code;
                Utils.Log(String.Format("compiling {0}", sourceUnit.Path ??
                    ((code = sourceUnit.GetCode()).Length < 100 ? code : code.Substring(0, 100))
                    .Replace('\r', ' ').Replace('\n', ' ')
                ), "COMPILER");
            }
#endif
            var rubyOptions = (RubyCompilerOptions)options;

            var lambda = ParseSourceCode<Func<RubyScope, object, object>>(sourceUnit, rubyOptions, errorSink);
            if (lambda == null) {
                return null;
            }

            return new RubyScriptCode(lambda, sourceUnit, rubyOptions.FactoryKind);
        }

#if MEASURE_AST
        private static readonly object _TransformationLock = new object();
        private static readonly Dictionary<ExpressionType, int> _TransformationHistogram = new Dictionary<ExpressionType,int>();
#endif

        /// <summary>
        /// The front end a host chose, or null for prism (IronRuby.Prism's bridge), which is the
        /// only parser IronRuby has: the 1.9-era LALR parser that used to be the fallback is gone.
        /// </summary>
        public static Func<SourceUnit, RubyCompilerOptions, ErrorSink, SourceUnitTree> AlternativeParser;

        private static Func<SourceUnit, RubyCompilerOptions, ErrorSink, SourceUnitTree> _defaultParser;

        /// <summary>The front end in effect: the one a host set, or else prism.</summary>
        public static Func<SourceUnit, RubyCompilerOptions, ErrorSink, SourceUnitTree> EffectiveParser {
            get { return AlternativeParser ?? (_defaultParser ?? (_defaultParser = ResolveDefaultParser())); }
        }

        // IronRuby.Prism references IronRuby, so IronRuby cannot reference it back: find it by name.
        // Without it there is nothing to parse with, which is a deployment error and is reported as
        // one; a present IronRuby.Prism whose native libprism is missing fails at the first parse
        // with DllNotFoundException.
        private static Func<SourceUnit, RubyCompilerOptions, ErrorSink, SourceUnitTree> ResolveDefaultParser() {
            Type bridge = Type.GetType("IronRuby.Prism.PrismAstBridge, IronRuby.Prism", false);
            MethodInfo parse = (bridge != null) ? bridge.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(SourceUnit), typeof(RubyCompilerOptions), typeof(ErrorSink) }, null) : null;
            if (parse == null) {
                throw new InvalidOperationException(
                    "IronRuby.Prism.dll could not be loaded: it is IronRuby's parser and has to be deployed next to IronRuby.dll.");
            }
            return (Func<SourceUnit, RubyCompilerOptions, ErrorSink, SourceUnitTree>)parse.CreateDelegate(typeof(Func<SourceUnit, RubyCompilerOptions, ErrorSink, SourceUnitTree>));
        }


        internal MSA.Expression<T> ParseSourceCode<T>(SourceUnit/*!*/ sourceUnit, RubyCompilerOptions/*!*/ options, ErrorSink/*!*/ errorSink) {
            Debug.Assert(sourceUnit.LanguageContext == this);

            long traceStart = StartupTrace.Now();
            SourceUnitTree ast;
            try {
                ast = EffectiveParser(sourceUnit, options, errorSink);
            } finally {
                StartupTrace.Parsed(traceStart);
            }

            if (ast == null) {
                return null;
            }

            MSA.Expression<T> lambda;
#if MEASURE_AST
            lock (_TransformationLock) {
                var oldHistogram = System.Linq.Expressions.Expression.Histogram;
                System.Linq.Expressions.Expression.Histogram = _TransformationHistogram;
                try {
#endif
            traceStart = StartupTrace.Now();
            try {
                lambda = TransformTree<T>(ast, sourceUnit, options);
            } finally {
                StartupTrace.Transformed(traceStart);
            }
#if MEASURE_AST
                } finally {
                    System.Linq.Expressions.Expression.Histogram = oldHistogram;
                }
            }
#endif

            return lambda;
        }

        internal MSA.Expression<T>/*!*/ TransformTree<T>(SourceUnitTree/*!*/ ast, SourceUnit/*!*/ sourceUnit, RubyCompilerOptions/*!*/ options) {
            var gen = new AstGenerator(
                this,
                options,
                sourceUnit.Document,
                ast.Encoding,
                sourceUnit.Kind == SourceCodeKind.InteractiveCode
            );

            var coverage = _coverage;
            if (coverage != null && gen.Traceable && !gen.SavingToDisk && sourceUnit.Kind != SourceCodeKind.InteractiveCode) {
                gen.Coverage = coverage.GetCoverage(ast, sourceUnit.Path, options.IsEval, options.InitialLocation.Line, sourceUnit.GetCode());
            }

            return ast.Transform<T>(gen);
        }

        private CoverageState _coverage;

        /// <summary>
        /// The Coverage library's measurement, null unless it is set up. Code compiled while it
        /// is set up is measured.
        /// </summary>
        public CoverageState Coverage {
            get { return _coverage; }
            set { _coverage = value; }
        }

        public override CompilerOptions/*!*/ GetCompilerOptions() {
            return new RubyCompilerOptions(_options) {
                FactoryKind = TopScopeFactoryKind.Hosted,
            };
        }

        public override CompilerOptions/*!*/ GetCompilerOptions(Scope/*!*/ scope) {
            var result = new RubyCompilerOptions(_options) {
                FactoryKind = TopScopeFactoryKind.Hosted
            };

            var rubyGlobalScope = (RubyGlobalScope)scope.GetExtension(ContextId);
            if (rubyGlobalScope != null && rubyGlobalScope.TopLocalScope != null) {
                result.LocalNames = rubyGlobalScope.TopLocalScope.GetVisibleLocalNames();
            }

            return result;
        }

        public override ErrorSink GetCompilerErrorSink() {
            return _runtimeErrorSink;
        }

        public override ScriptCode/*!*/ LoadCompiledCode(Delegate/*!*/ method, string path, string customData) {
            // TODO: we need to save the kind of the scope factory:
            SourceUnit su = new SourceUnit(this, NullTextContentProvider.Null, path, SourceCodeKind.File);
            return new RubyScriptCode((Func<RubyScope, object, object>)method, su, TopScopeFactoryKind.Hosted);
        }

        #endregion

        #region Global Scope (thread-safe)

        /// <summary>
        /// Creates a scope extension for a DLR scope unless it already exists for the given scope.
        /// </summary>
        internal RubyGlobalScope/*!*/ InitializeGlobalScope(Scope/*!*/ globalScope, bool createHosted, bool bindGlobals) {
            Assert.NotNull(globalScope);

            var scopeExtension = globalScope.GetExtension(ContextId);
            if (scopeExtension != null) {
                return (RubyGlobalScope)scopeExtension;
            }

            RubyObject mainObject = new RubyObject(_objectClass);
            RubyClass mainSingleton = GetOrCreateMainSingleton(mainObject, null);

            RubyGlobalScope result = new RubyGlobalScope(this, globalScope, mainObject, createHosted);
            // Remember the first one as the fallback for frameless call sites (see MainGlobalScope);
            // the main script's scope replaces it in RubyTopLevelScope.CreateTopLevelScope.
            if (_mainGlobalScope == null) {
                _mainGlobalScope = result;
            }
            if (bindGlobals) {
                mainSingleton.SetMethodNoEvent(this, Symbols.MethodMissing, new RubyScopeMethodMissingInfo(RubyMemberFlags.Private, mainSingleton));
                mainSingleton.SetGlobalScope(result);
            }
            return (RubyGlobalScope)globalScope.SetExtension(ContextId, result);
        }

        /// <summary>
        /// The main script did not parse. MRI reports that as it parses, before the at_exit handlers run.
        /// </summary>
        public bool MainScriptFailedToParse { get; private set; }

        // The path each source file was run under, mapped to where it really was at that moment.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>/*!*/ _sourceFileLocations =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Remembers the absolute, symlink-free location of a source file as it starts to run. MRI
        /// fixes it then: a relative main script is still found after Dir.chdir, and a file loaded
        /// through a symlink after the link is gone (Thread::Backtrace::Location#absolute_path,
        /// __dir__, require_relative).
        /// </summary>
        internal void RegisterSourceFileLocation(string path) {
            if (String.IsNullOrEmpty(path) || path.StartsWith("(", StringComparison.Ordinal)) {
                return;
            }
            try {
                string fullPath = Platform.GetFullPath(path);
                FileSystemInfo target = File.ResolveLinkTarget(fullPath, true);
                _sourceFileLocations[path] = (target != null) ? target.FullName : fullPath;
            } catch (Exception) {
                // not a file on disk; nothing to remember
            }
        }

        /// <summary>The location RegisterSourceFileLocation remembered for a path, or null.</summary>
        public string TryGetSourceFileLocation(string/*!*/ path) {
            string result;
            return _sourceFileLocations.TryGetValue(path, out result) ? result : null;
        }

        public override int ExecuteProgram(SourceUnit/*!*/ program) {
            RegisterSourceFileLocation(program.Path);
            try {
                RubyCompilerOptions options = new RubyCompilerOptions(_options) {
                    FactoryKind = TopScopeFactoryKind.Main
                };

                ScriptCode code;
                try {
                    code = CompileSourceCode(program, options, _runtimeErrorSink);
                } catch (SyntaxError) {
                    MainScriptFailedToParse = true;

                    // MRI requires the -r libraries before it parses the script, so whatever they set
                    // up - an at_exit handler, say - is in place even when the script does not parse.
                    // Here they are loaded as the script starts running, so do it now instead.
                    if (RubyOptions.RequirePaths != null) {
                        RubyTopLevelScope.CreateTopLevelScope(new Scope(), this, true);
                    }
                    throw;
                }

                // -c: parsing (and compiling) the program is all that is asked for
                if (RubyOptions.CheckSyntaxOnly) {
                    Console.Out.Write("Syntax OK\n");
                    Console.Out.Flush();
                    return 0;
                }
                code.Run();
            } catch (SystemExit e) {
                return e.Status;
            }

            return 0;
        }

        #endregion

        #region Shutdown (thread-safe)

        private readonly List<Proc> _shutdownHandlers = new List<Proc>();
        private object ShutdownHandlersLock { get { return _shutdownHandlers; }}

        public void RegisterShutdownHandler(Proc/*!*/ proc) {
            ContractUtils.RequiresNotNull(proc, "proc");

            lock (ShutdownHandlersLock) {
                _shutdownHandlers.Add(proc);
            }
        }

        #region ObjectSpace

        // What ObjectSpace.each_object can see: every module and class, and - with -X:ObjectSpace
        // only, since a weak handle per object is not cheap - every RubyObject: the instances of Ruby
        // classes deriving from Object/BasicObject, structs included. Instances of the builtin types
        // (String, Array, ...) and their subclasses are never recorded.
        internal readonly ObjectSpaceRegistry/*!*/ ObjectSpaceModules = new ObjectSpaceRegistry();
        internal ObjectSpaceRegistry ObjectSpaceObjects;

        public List<object>/*!*/ GetObjectSpaceModules() {
            return ObjectSpaceModules.GetObjects();
        }

        /// <summary>
        /// Null unless objects are recorded (RubyOptions.ObjectSpace).
        /// </summary>
        public List<object> GetObjectSpaceObjects() {
            var registry = ObjectSpaceObjects;
            return registry != null ? registry.GetObjects() : null;
        }

        #endregion

        #region Finalizers

        // ObjectSpace.define_finalizer's pending finalizers. The CLR does not finalize what is still
        // alive when the process ends, and MRI runs every finalizer that has not run by then after
        // the at_exit handlers - so they are also kept here, weakly, to be run at exit.
        private readonly List<WeakReference>/*!*/ _exitFinalizers = new List<WeakReference>();

        public void RegisterExitFinalizer(IExitFinalizer/*!*/ finalizer) {
            lock (_exitFinalizers) {
                _exitFinalizers.Add(new WeakReference(finalizer));
            }
        }

        private void RunExitFinalizers() {
            // a finalizer may define another one, which then runs too
            while (true) {
                WeakReference[] pending;
                lock (_exitFinalizers) {
                    if (_exitFinalizers.Count == 0) {
                        return;
                    }
                    pending = _exitFinalizers.ToArray();
                    _exitFinalizers.Clear();
                }
                foreach (var reference in pending) {
                    var finalizer = reference.Target as IExitFinalizer;
                    if (finalizer != null) {
                        finalizer.RunAtExit();
                    }
                }
            }
        }

        /// <summary>
        /// MRI reports an exception a finalizer raises and goes on; under -W0 it says nothing.
        /// </summary>
        public void ReportFinalizerException(object finalizer, Exception/*!*/ exception) {
            if (Verbose == null) {
                return;
            }
            try {
                _runtimeErrorSink.WriteMessage(MutableString.CreateMutable(
                    "warning: Exception in finalizer " + Inspect(finalizer).ToString() + "\n" + FormatException(exception),
                    RubyEncoding.UTF8
                ));
            } catch (Exception) {
                // reporting must not take the finalizer thread down
            }
        }

        #endregion

        // What the at_exit handlers ended with, kept from the time they run until the process exits.
        private SystemExit _shutdownSystemExit;
        private Exception _shutdownException;

        /// <summary>
        /// Runs the Signal.trap("EXIT") handler and the at_exit handlers registered so far. MRI runs
        /// them before it reports the exception that ended the script - a handler that calls exit!
        /// means it is never reported - so the command line calls this before printing it. How they
        /// ended takes effect when the process exits.
        /// </summary>
        public void RunShutdownHandlers() {
            SystemExit lastSystemExit = _shutdownSystemExit;
            Exception lastException = _shutdownException;
            try {
                RunShutdownHandlers(ref lastSystemExit, ref lastException);
            } finally {
                _shutdownSystemExit = lastSystemExit;
                _shutdownException = lastException;
            }
        }

        /// <summary>
        /// Kills the threads still running when the program ends (the Thread library sets it once it
        /// has started one). Runs after the at_exit handlers, as MRI's rb_thread_terminate_all does.
        /// </summary>
        public Action ThreadTerminator { get; set; }

        private void ExecuteShutdownHandlers() {
            RunShutdownHandlers();

            var terminator = ThreadTerminator;
            if (terminator != null) {
                ThreadTerminator = null;
                try {
                    terminator();
                } catch (Exception) {
                    // the program is over; a thread that won't die just stays behind
                }
            }

            RunExitFinalizers();

            if (_shutdownSystemExit != null) {
                throw _shutdownSystemExit;
            } else if (_shutdownException != null) {
                // at least one unhandled exception:
                throw new SystemExit(1);
            }
        }

        private void RunShutdownHandlers(ref SystemExit lastSystemExit, ref Exception lastException) {

            // `Signal.trap("EXIT")' runs before the at_exit blocks, as MRI's does.
            var exitHandler = ExitSignalHandler;
            if (exitHandler != null) {
                ExitSignalHandler = null;
                try {
                    exitHandler();
                } catch (SystemExit e) {
                    lastSystemExit = e;
                } catch (Exception e) {
                    CurrentException = e;
                    lastException = e;
                    _runtimeErrorSink.WriteMessage(MutableString.CreateMutable(FormatException(e), RubyEncoding.UTF8));
                }
            }

            // Last registered, first run - one at a time, so that a handler registered by another
            // handler runs right after it rather than after all the rest.
            while (true) {
                Proc handler;
                lock (ShutdownHandlersLock) {
                    if (_shutdownHandlers.Count == 0) {
                        break;
                    }
                    handler = _shutdownHandlers[_shutdownHandlers.Count - 1];
                    _shutdownHandlers.RemoveAt(_shutdownHandlers.Count - 1);
                }

                {
                    try {
                        handler.Call(null);
                    } catch (SystemExit e) {
                        // Kernel#at_exit can call exit and set the exitcode. Furthermore, exit can be called 
                        // from multiple blocks registered with Kernel#at_exit.
                        lastSystemExit = e;
                    } catch (Exception e) {
			            CurrentException = e;
                        lastException = e;
                        // TODO: GetIdentifierEncoding
                        _runtimeErrorSink.WriteMessage(MutableString.CreateMutable(FormatException(e), RubyEncoding.UTF8));
                    }
                }
            }

        }

        public override void Shutdown() {
            _upTime.Stop();

            if (RubyOptions.Profile) {
                var profile = Profiler.Instance.GetProfile();
                using (TextWriter writer = File.CreateText("profile.log")) {
                    int maxLength = 0;
                    long totalTicks = 0L;
                    var keys = new string[profile.Count];
                    var values = new long[profile.Count];

                    int i = 0;
                    foreach (var counter in profile) {
                        string methodInfo = counter.Id;
                        if (methodInfo.Length > maxLength) {
                            maxLength = methodInfo.Length;
                        }

                        totalTicks += counter.Ticks;

                        keys[i] = methodInfo;
                        values[i] = counter.Ticks;
                        i++;
                    }

                    Array.Sort(values, keys);

                    for (int j = keys.Length - 1; j >= 0; j--) {
                        long ticks = values[j];

                        writer.WriteLine("{0,-" + (maxLength + 4) + "} {1,8:F0} ms {2,5:F1}%", keys[j],
                            new TimeSpan(Utils.DateTimeTicksFromStopwatch(ticks)).TotalMilliseconds,
                            (((double)ticks) / totalTicks * 100)
                        );
                    }

                    writer.WriteLine("{0,-" + (maxLength + 4) + "} {1,8:F0} ms", "total",
                        new TimeSpan(Utils.DateTimeTicksFromStopwatch(totalTicks)).TotalMilliseconds
                    );
                }
            }

            if (Options.PerfStats) {
                using (TextWriter output = File.CreateText("perfstats.log")) {
                    output.WriteLine(String.Format(@"
  total:         {0}
  binding:       {1} ({2} calls)
",
                        _upTime.Elapsed,
#if MEASURE
                    new TimeSpan(MetaAction.BindingTimeTicks), 
                    MetaAction.BindCallCount
#else
     "N/A", "N/A"
#endif
));

#if MEASURE_BINDING
                    output.WriteLine();
                    output.WriteLine("---- MetaAction kinds ----");
                    output.WriteLine();

                    PerfTrack.DumpHistogram(MetaAction.HistogramOfKinds, output);

                    output.WriteLine();

                    output.WriteLine();
                    output.WriteLine("---- MetaAction instances ----");
                    output.WriteLine();

                    PerfTrack.DumpHistogram(MetaAction.HistogramOfInstances, output);

                    output.WriteLine();
#endif

#if MEASURE_AST
                    output.WriteLine();
                    output.WriteLine("---- Ruby Parser generated Expression Trees ----");
                    output.WriteLine();
                    
                    PerfTrack.DumpHistogram(_TransformationHistogram, output);

                    output.WriteLine();
#endif
                    PerfTrack.DumpStats(output);
                }
            }
            _loader.SaveCompiledCode();

            ExecuteShutdownHandlers();

            _currentException = null;
        }

        #endregion

        #region Exceptions (thread-safe)

        /// <summary>
        /// Formats exceptions like Ruby does.
        /// </summary>
        /// <remarks>
        /// For example,
        /// <code>
        /// repro.rb:2:in `fetch': wrong number of arguments (0 for 1) (ArgumentError)
        ///     from repro.rb:2:in `test'
        ///     from repro.rb:5
        /// </code>
        /// </remarks>
        public override string/*!*/ FormatException(Exception/*!*/ exception) {
            var syntaxError = exception as SyntaxError;
            if (syntaxError != null && syntaxError.HasLineInfo) {
                // "-e:1: expected a `}' ... (SyntaxError)": MRI names the class, as for any other error
                return FormatErrorMessage(syntaxError.Message + " (" + GetClassOf(exception).Name + ")", null,
                    syntaxError.File, syntaxError.Line, syntaxError.Column, syntaxError.LineSourceCode);
            }

            StringBuilder sb = new StringBuilder();
            AppendExceptionReport(sb, exception);

            // MRI follows the report with the exception's causes, each reported the same way:
            var shown = new HashSet<Exception>(ReferenceEqualityComparer<Exception>.Instance) { exception };
            Exception cause = exception;
            while ((cause = RubyExceptionData.GetInstance(cause).Cause) != null && shown.Add(cause)) {
                AppendExceptionReport(sb, cause);
            }

            // display the raw CLR exception & strack trace if requested
            if (Options.ShowClrExceptions) {
                sb.AppendLine().AppendLine();
                sb.AppendLine("CLR exception:");
                sb.Append(base.FormatException(exception));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private CallSite<Func<CallSite, object, object>> _messageSite;

        /// <summary>
        /// What the report says the exception's message is: its #message, as MRI's error printer
        /// asks (through #detailed_message). A class that computes its message in #message -
        /// OptionParser::ParseError does, so puma's "invalid option" error is one - used to be
        /// reported with the message it was constructed with, which there is its class name.
        /// </summary>
        private object GetReportedMessage(Exception/*!*/ exception, RubyExceptionData/*!*/ data) {
            try {
                var site = RubyUtils.GetCallSite(ref _messageSite, this, "message", 0);
                object message = site.Target(site, exception);
                if (message is MutableString) {
                    return message;
                }
            } catch (Exception) {
                // a #message that raises: report what the exception was built with
            }
            return data.Message;
        }

        private void AppendExceptionReport(StringBuilder/*!*/ sb, Exception/*!*/ exception) {
            var exceptionClass = GetClassOf(exception);
            RubyExceptionData data = RubyExceptionData.GetInstance(exception);
            // The message is written as it is: MRI does not escape control characters or
            // backslashes in it, so a message can color itself with "\e[31m". Only bytes that
            // are not characters are shown escaped.
            object reported = GetReportedMessage(exception, data);
            var rawMessage = reported as MutableString;
            string message = (rawMessage != null && !rawMessage.IsBinary && !rawMessage.ContainsInvalidCharacters())
                ? rawMessage.ToString()
                : RubyExceptionData.GetClrMessage(this, reported);

            RubyArray backtrace = data.Backtrace;

            // A message of several lines gets its "(Class)" after the first one, as MRI prints it.
            int newline = message.IndexOf('\n');
            string firstLine = (newline < 0) ? message : message.Substring(0, newline);
            string restLines = (newline < 0) ? "" : message.Substring(newline);

            if (backtrace != null && backtrace.Count > 0) {
                sb.AppendFormat("{0}: {1} ({2}){3}", Protocols.ToClrStringNoThrow(this, backtrace[0]), firstLine, exceptionClass.Name, restLines);
                sb.AppendLine();

                // --backtrace-limit=N keeps N "from" lines and says how many it left out - but only
                // when that is at least two, as MRI's print_backtrace does; -1 means no cap.
                int limit = RubyOptions.BacktraceLimit;
                int count = backtrace.Count;
                if (limit >= 0 && count > limit + 2) {
                    count = limit + 1;
                }
                for (int i = 1; i < count; i++) {
                    sb.Append("\tfrom ").Append(Protocols.ToClrStringNoThrow(this, backtrace[i])).AppendLine();
                }
                if (count < backtrace.Count) {
                    sb.Append("\t ... ").Append(backtrace.Count - count).Append(" levels...").AppendLine();
                }
            } else {
                sb.AppendFormat("unknown: {0} ({1})", message, exceptionClass.Name).AppendLine();
            }
        }

        internal static string/*!*/ FormatErrorMessage(string/*!*/ message, string prefix, string file, int line, int column, string lineSource) {
            var sb = new StringBuilder();
            sb.Append(file ?? "unknown");
            sb.Append(':');
            sb.Append(file != null ? line : 0);
            sb.Append(": ");
            if (prefix != null) {
                sb.Append(prefix);
                sb.Append(": ");
            }
            sb.Append(message);
            sb.AppendLine();
            if (lineSource != null) {
                sb.Append(lineSource);
                sb.AppendLine();
                if (column > 0) {
                    sb.Append(' ', column - 1);
                    sb.Append('^');
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        public Action InterruptSignalHandler { get; set; }

        /// <summary>
        /// What `Signal.trap("EXIT")' installed, if anything. EXIT is not a signal the operating
        /// system ever sends; it is a handler the runtime runs on its way out, ahead of the
        /// at_exit blocks. The Signal library sets it, because the handler is its to call.
        /// </summary>
        public Action ExitSignalHandler { get; set; }

        #endregion

        #region Language Context Overrides

        // -0<octal> names a single byte, which may be above 0x7F
        private static MutableString/*!*/ CreateInputSeparator(string/*!*/ separator) {
            return (separator.Length == 1 && separator[0] > 0x7F)
                ? MutableString.CreateBinary(new[] { (byte)separator[0] })
                : MutableString.CreateAscii(separator);
        }

        public override TService GetService<TService>(params object[] args) {
            if (typeof(TService) == typeof(RubyService)) {
                return (TService)(object)(_rubyService ?? (_rubyService = new RubyService(this, (Microsoft.Scripting.Hosting.ScriptEngine)args[0])));
            }

            // There is no TokenizerService (the DLR's colouring tokenizer) any more: it was the
            // 1.9-era lexer of the legacy parser.
            return base.GetService<TService>(args);
        }

        public override void SetSearchPaths(ICollection<string/*!*/>/*!*/ paths) {
            ContractUtils.RequiresNotNullItems(paths, "paths");
            _loader.SetLoadPaths(paths);
        }

        // Might run an arbitrary user code.
        public override ICollection<string>/*!*/ GetSearchPaths() {
            return _loader.GetLoadPathStrings();
        }

        // Ruby 2.0+ reads source as UTF-8 unless told otherwise.
        public override Encoding/*!*/ DefaultEncoding {
            get { return RubyEncoding.UTF8.StrictEncoding; }
        }

        public override SourceCodeReader/*!*/ GetSourceReader(Stream/*!*/ stream, Encoding/*!*/ defaultEncoding, string path) {
            ContractUtils.RequiresNotNull(stream, "stream");
            ContractUtils.RequiresNotNull(defaultEncoding, "defaultEncoding");
            ContractUtils.Requires(stream.CanRead && stream.CanSeek, "stream", "The stream must support seeking and reading");

            return GetSourceReader(stream, defaultEncoding);
        }

        /// <summary>
        /// A file is UTF-8 unless it says otherwise, and so is a binary or ASCII string given to
        /// eval. A string in any other encoding is eval'd as the characters it holds in that
        /// encoding - reading its bytes as UTF-8 failed outright for most Shift_JIS or EUC-JP text.
        /// </summary>
        private static Encoding/*!*/ SourceDefaultEncoding(Encoding/*!*/ defaultEncoding) {
            switch (defaultEncoding.CodePage) {
                case RubyEncoding.CodePageBinary:
                case RubyEncoding.CodePageAscii:
                case RubyEncoding.CodePageUTF8:
                    return RubyEncoding.UTF8.StrictEncoding;
            }
            var encoding = RubyEncoding.GetRubyEncoding(defaultEncoding);
            return encoding.IsAsciiIdentity ? encoding.StrictEncoding : RubyEncoding.UTF8.StrictEncoding;
        }

        private SourceCodeReader/*!*/ GetSourceReader(Stream/*!*/ stream, Encoding/*!*/ defaultEncoding) {
            long initialPosition = stream.Position;
            var reader = new StreamReader(stream, BinaryEncoding.Instance, true);

            // reads preamble, if present:
            reader.Peek();

            Encoding preambleEncoding = (reader.CurrentEncoding != BinaryEncoding.Instance) ? reader.CurrentEncoding : null;
            Encoding rubyPreambleEncoding = null;

            // Ruby knows only the UTF-8 BOM. A UTF-16 or UTF-32 one is two or four bytes that are
            // not UTF-8, and the file is a syntax error ("invalid multibyte char") rather than a
            // file in that encoding.
            if (preambleEncoding != null && preambleEncoding.CodePage != RubyEncoding.CodePageUTF8) {
                preambleEncoding = null;
                stream.Seek(initialPosition, SeekOrigin.Begin);
                reader = new StreamReader(stream, BinaryEncoding.Instance, false);
            }

            // header:
            string encodingName;
            if (Tokenizer.TryParseEncodingHeader(reader, out encodingName)) {
                rubyPreambleEncoding = GetEncodingByRubyName(encodingName);

                // Check if the preamble encoding is an identity on preamble bytes.
                // If not we shouldn't allow such encoding since the encoding of the preamble would be different from the encoding of the file.
                if (!RubyEncoding.AsciiIdentity(rubyPreambleEncoding)) {
                    throw new IOException(String.Format("Encoding '{0}' is not allowed in preamble.", rubyPreambleEncoding.WebName));
                }
            }

            // skip the encoding preamble, the resulting stream shouldn't detect the preamble again
            // (this is necessary to override the preamble by Ruby specific one):
            if (preambleEncoding != null) {
                initialPosition += preambleEncoding.GetPreamble().Length;
            }

            stream.Seek(initialPosition, SeekOrigin.Begin);

            // Ruby 2.0 made UTF-8 the default source encoding; before that it was
            // US-ASCII and a magic comment was required for anything else. A magic
            // comment or a BOM still wins.
            var encoding = rubyPreambleEncoding ?? preambleEncoding ?? SourceDefaultEncoding(defaultEncoding);
            if (encoding == RubyEncoding.UTF8.StrictEncoding) {
                // Bytes that are not UTF-8 are prism's to report, as the syntax error MRI gives for
                // them, so they have to survive being read: they are decoded to the escapes
                // RubyEncoding.EscapingEncoding turns back into the same bytes, which is what the
                // parser is handed. Decoding them strictly threw a bare DecoderFallbackException.
                var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                var data = bytes.ToArray();
                string text;
                try {
                    text = encoding.GetString(data);
                } catch (DecoderFallbackException) {
                    text = RubyEncoding.UTF8.EscapingEncoding.GetString(data);
                }
                return new SourceCodeReader(new StringReader(text), encoding);
            }
            return new SourceCodeReader(new StreamReader(stream, encoding, false), encoding);
        }

        /// <exception cref="ArgumentException">Unknown encoding.</exception>
        /// <summary>
        /// An encoding named on the command line by -E or --encoding. A bad name there is a
        /// startup error, not a Ruby exception - there is no Ruby program running yet to rescue
        /// it - so the .NET lookup failure is replaced by MRI's own wording.
        /// </summary>
        private RubyEncoding/*!*/ GetCommandLineEncoding(string/*!*/ name) {
            try {
                return RubyEncoding.GetRubyEncoding(GetEncodingByRubyName(name));
            } catch (ArgumentException) {
                throw new ArgumentException("unknown encoding name - " + name);
            } catch (NotSupportedException) {
                throw new ArgumentException("unknown encoding name - " + name);
            }
        }

        public Encoding/*!*/ GetEncodingByRubyName(string/*!*/ name) {
            ContractUtils.RequiresNotNull(name, "name");

            var upperName = name.ToUpperInvariant();
            switch (upperName) {
                case "BINARY":
                case "ASCII-8BIT": return BinaryEncoding.Instance;
                case "FILESYSTEM": return GetPathEncoding().StrictEncoding;
                case "LOCALE": return _options.LocaleEncoding.StrictEncoding;
                case "EXTERNAL": return _defaultExternalEncoding.StrictEncoding;
                // Mono doesn't recognize 'SJIS' encoding name:
                case "WINDOWS-31J": return Encoding.GetEncoding(RubyEncoding.CodePageSJIS);
                case "MACCYRILLIC": return Encoding.GetEncoding(10007);

                // encodings whose name only differs in casing are returned by Windows:
                case "EUC-JP": return Encoding.GetEncoding(RubyEncoding.CodePageEUCJP);
                case "ISO-2022-JP": return Encoding.GetEncoding(50220);

                // the encoding name doesn't correspond to its code page:
                case "CP1025": return Encoding.GetEncoding(21025);

                // Ruby's names for code pages .NET spells differently - see GetRubySpecificName.
                case "MACROMAN": return Encoding.GetEncoding(10000);
                case "MACJAPANESE": return Encoding.GetEncoding(10001);
                case "MACGREEK": return Encoding.GetEncoding(10006);
                case "MACROMANIA": return Encoding.GetEncoding(10010);
                case "MACUKRAINE": return Encoding.GetEncoding(10017);
                case "MACTHAI": return Encoding.GetEncoding(10021);
                case "MACCENTEURO": return Encoding.GetEncoding(10029);
                case "MACICELAND": return Encoding.GetEncoding(10079);
                case "MACTURKISH": return Encoding.GetEncoding(10081);
                case "MACCROATIAN": return Encoding.GetEncoding(10082);
                case "IBM720": return Encoding.GetEncoding(720);
                case "IBM862": return Encoding.GetEncoding(862);
                case "CP949": return Encoding.GetEncoding(949);
                case "GBK": return Encoding.GetEncoding(936);
                case "GB2312": return Encoding.GetEncoding(51936);
                case "EUC-KR": return Encoding.GetEncoding(51949);
                case "GB18030": return Encoding.GetEncoding(54936);

                // Encodings Ruby has and .NET does not. Without these, "UTF-16" and "UTF-32"
                // resolved to .NET's utf-16/utf-32, which are Ruby's UTF-16LE and UTF-32LE, and
                // "TIS-620" resolved to Windows-874, which has a larger repertoire.
                // Not through Encoding.GetEncoding("UTF-7"): that is refused unless the host enables
                // System.Text.Encoding.EnableUnsafeUTF7Encoding (see RubyEncoding.CreateUTF7Encoding).
                case "UTF-7": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF7).StrictEncoding;
                case "UTF-16": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF16).StrictEncoding;
                case "UTF-32": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF32).StrictEncoding;
                case "CESU-8": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageCESU8).StrictEncoding;
                case "TIS-620": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageTIS620).StrictEncoding;
                case "EMACS-MULE": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageEmacsMule).StrictEncoding;
                case "SHIFT_JIS": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageShiftJIS).StrictEncoding;
                case "UTF8-MAC": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF8Mac).StrictEncoding;
                case "CP51932": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageCP51932).StrictEncoding;
                case "EUCJP-MS": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageEucJpMs).StrictEncoding;
                case "STATELESS-ISO-2022-JP": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageStatelessISO2022JP).StrictEncoding;
                case "ISO-2022-JP-2": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageISO2022JP2).StrictEncoding;
                case "GB12345": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageGB12345).StrictEncoding;
                case "EUC-TW": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageEUCTW).StrictEncoding;
                case "GB1988": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageGB1988).StrictEncoding;
                case "ISO-8859-10": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageISO8859_10).StrictEncoding;
                case "ISO-8859-14": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageISO8859_14).StrictEncoding;
                case "ISO-8859-16": return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageISO8859_16).StrictEncoding;

                default:
                    string alias;
                    if (RubyEncoding.Aliases.TryGetValue(name, out alias)) {
                        return GetEncodingByRubyName(alias);
                    }
                    if (upperName.StartsWith("CP", StringComparison.Ordinal)) {
                        int codepage;
                        if (Int32.TryParse(upperName.Substring(2), out codepage)) {
                            try {
                                return Encoding.GetEncoding(codepage);
                            } catch (NotSupportedException) {
                                // the encoding name is not correct
                            }
                        }
                    }
                    return Encoding.GetEncoding(name);
            }
        }

        /// <exception cref="ArgumentException">Unknown encoding.</exception>
        public RubyEncoding/*!*/ GetRubyEncoding(MutableString/*!*/ name) {
            // These are the messages MRI gives, and they reach the user from #force_encoding,
            // #encode, Integer#chr and IO as well as from Encoding.find. .NET's own message for an
            // unknown name talks about Encoding.RegisterProvider, which means nothing in Ruby.
            if (!name.IsAscii()) {
                throw RubyExceptions.CreateArgumentError("invalid encoding name (non ASCII)");
            }
            try {
                return RubyEncoding.GetRubyEncoding(GetEncodingByRubyName(name.ToString()));
            } catch (ArgumentException) {
                throw RubyExceptions.CreateArgumentError("unknown encoding name - {0}", name.ToAsciiString());
            }
        }

        /// <exception cref="ArgumentException">Unknown encoding.</exception>
        public RubyEncoding/*!*/ GetRubyEncoding(string/*!*/ name) {
            return RubyEncoding.GetRubyEncoding(GetEncodingByRubyName(name));
        }

        public override string/*!*/ FormatObject(DynamicOperations/*!*/ operations, object obj) {
            var inspectSite = operations.GetOrCreateSite<object, object>(
                RubyCallAction.Make(this, "inspect", RubyCallSignature.WithImplicitSelf(1))
            );

            var tosSite = operations.GetOrCreateSite<object, MutableString>(ConvertToSAction.Make(this));

            return tosSite.Target(tosSite, inspectSite.Target(inspectSite, obj)).ToString();
        }

        #endregion

        #region MetaObject binding

        public override GetMemberBinder/*!*/ CreateGetMemberBinder(string/*!*/ name, bool ignoreCase) {
            // TODO:
            if (ignoreCase) {
                return base.CreateGetMemberBinder(name, ignoreCase);
            }
            return _metaBinderFactory.InteropGetMember(name);
        }

        public override SetMemberBinder/*!*/ CreateSetMemberBinder(string name, bool ignoreCase) {
            // TODO:
            if (ignoreCase) {
                return base.CreateSetMemberBinder(name, ignoreCase);
            }

            // TODO: name mangling
            return _metaBinderFactory.InteropSetMemberExact(name);
        }

        public override InvokeMemberBinder/*!*/ CreateCallBinder(string/*!*/ name, bool ignoreCase, CallInfo/*!*/ callInfo) {
            // TODO:
            if (ignoreCase || callInfo.ArgumentNames.Count != 0) {
                return base.CreateCallBinder(name, ignoreCase, callInfo);
            }
            return _metaBinderFactory.InteropInvokeMember(name, callInfo);
        }

        public override CreateInstanceBinder/*!*/ CreateCreateBinder(CallInfo/*!*/ callInfo) {
            // TODO:
            if (callInfo.ArgumentNames.Count != 0) {
                return base.CreateCreateBinder(callInfo);
            }

            return _metaBinderFactory.InteropCreateInstance(callInfo);
        }

        public override ConvertBinder/*!*/ CreateConvertBinder(Type toType, bool? explicitCast) {
            return _metaBinderFactory.InteropConvert(toType, explicitCast ?? true);
        }

        // TODO: override GetMemberNames?
        public IList<string>/*!*/ GetForeignDynamicMemberNames(object obj) {
            if (obj is IRubyDynamicMetaObjectProvider) {
                return ArrayUtils.EmptyStrings;
            }
            if (TypeUtils.IsComObject(obj)) {
                return new List<string>(Microsoft.Scripting.ComInterop.ComBinder.GetDynamicMemberNames(obj));
            }
            return GetMemberNames(obj);
        }

        #endregion

        #region Dynamic Sites (thread-safe)

        private CallSite<Func<CallSite, object, MutableString>> _stringConversionSite;

        public CallSite<Func<CallSite, object, MutableString>>/*!*/ StringConversionSite {
            get { return RubyUtils.GetCallSite(ref _stringConversionSite, ConvertToSAction.Make(this)); }
        }

        private readonly Dictionary<Key<string, RubyCallSignature>, CallSite>/*!*/ _sendSites =
            new Dictionary<Key<string, RubyCallSignature>, CallSite>();

        private object SendSitesLock { get { return _sendSites; } }

        public CallSite<TSiteFunc>/*!*/ GetOrCreateSendSite<TSiteFunc>(string/*!*/ methodName, RubyCallSignature callSignature)
            where TSiteFunc : class {

            lock (SendSitesLock) {
                CallSite site;
                if (_sendSites.TryGetValue(Key.Create(methodName, callSignature), out site)) {
                    return (CallSite<TSiteFunc>)site;
                }

                var newSite = CallSite<TSiteFunc>.Create(RubyCallAction.Make(this, methodName, callSignature));
                _sendSites.Add(Key.Create(methodName, callSignature), newSite);
                return newSite;
            }
        }

        public DynamicDelegateCreator/*!*/ DelegateCreator {
            get {
                if (_delegateCreator == null) {
                    Interlocked.CompareExchange(ref _delegateCreator, new DynamicDelegateCreator(this), null);
                }

                return _delegateCreator;
            }
        }

        #endregion

        #region Ruby Events

        private CallSite<Func<CallSite, object, object, object>> _respondTo;
        private CallSite<Func<CallSite, object, object>> _moduleName;
        private CallSite<Func<CallSite, object, object>> _argfFileName;
        private CallSite<Func<CallSite, object, object, object>> _exceptionFactory;
        private CallSite<Func<CallSite, object, object>> _toInt;
        private CallSite<Func<CallSite, object, object>> _toStr;

        internal object Send(ref CallSite<Func<CallSite, object, object, object>> site, string/*!*/ eventName,
            object target, string/*!*/ memberName) {

            if (site == null) {
                Interlocked.CompareExchange(
                    ref site,
                    CallSite<Func<CallSite, object, object, object>>.Create(RubyCallAction.Make(this, eventName, RubyCallSignature.WithImplicitSelf(1))),
                    null
                );
            }

            return site.Target(site, target, EncodeIdentifier(memberName));
        }

        /// <summary>
        /// The to_int protocol, for the few places that need it without a call-site cache of
        /// their own (assignment to $. and friends). A Float truncates, as it does in MRI.
        /// </summary>
        internal int CastToFixnum(object value) {
            if (value is int) {
                return (int)value;
            }
            if (value is double) {
                return (int)(double)value;
            }

            if (value != null && RespondTo(value, "to_int")) {
                if (_toInt == null) {
                    Interlocked.CompareExchange(ref _toInt,
                        CallSite<Func<CallSite, object, object>>.Create(RubyCallAction.Make(this, "to_int", RubyCallSignature.WithImplicitSelf(0))),
                        null);
                }
                object converted = _toInt.Target(_toInt, value);
                if (converted is int) {
                    return (int)converted;
                }
            }

            throw RubyExceptions.CreateImplicitConversionError(GetClassDisplayName(value), "Integer");
        }

        /// <summary>The to_str protocol, for assignment to $0.</summary>
        internal MutableString/*!*/ CastToString(object value) {
            var str = value as MutableString;
            if (str != null) {
                return str;
            }

            if (value != null && RespondTo(value, "to_str")) {
                if (_toStr == null) {
                    Interlocked.CompareExchange(ref _toStr,
                        CallSite<Func<CallSite, object, object>>.Create(RubyCallAction.Make(this, "to_str", RubyCallSignature.WithImplicitSelf(0))),
                        null);
                }
                var converted = _toStr.Target(_toStr, value) as MutableString;
                if (converted != null) {
                    return converted;
                }
            }

            throw RubyExceptions.CreateImplicitConversionError(GetClassDisplayName(value), "String");
        }

        public bool RespondTo(object target, string/*!*/ methodName) {
            return RubyOps.IsTrue(Send(ref _respondTo, "respond_to?", target, methodName));
        }

        internal void ReportTraceEvent(string/*!*/ operation, RubyScope/*!*/ scope, RubyModule/*!*/ module, string/*!*/ name, string fileName, int lineNumber) {
#if FEATURE_THREAD && FEATURE_EXCEPTION_STATE
            if (RubyUtils.IsReclaimingFiber) {
                return;
            }
#endif
            if (_traceListener != null && !_traceListenerSuspended) {
                try {
                    _traceListenerSuspended = true;

                    _traceListener.Call(null, new[] {
                        MutableString.CreateAscii(operation),                                         // event
                        fileName != null ? scope.RubyContext.EncodePath(fileName) : null,             // file
                        ScriptingRuntimeHelpers.Int32ToObject(lineNumber),                            // line
                        EncodeIdentifier(name),                                                       // TODO: alias
                        new Binding(scope),                                                           // binding
                        module.IsSingletonClass ? ((RubyClass)module).SingletonClassOf : module       // module
                    });
                } finally {
                    _traceListenerSuspended = false;
                }
            }
        }

        #endregion
    }
}

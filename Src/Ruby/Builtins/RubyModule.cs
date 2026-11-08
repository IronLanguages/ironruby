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
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using IronRuby.Compiler;
using IronRuby.Compiler.Generation;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Actions.Calls;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    [Flags]
    public enum ModuleRestrictions {
        None = 0,

        /// <summary>
        /// Module doesn't allow its methods to be overridden.
        /// Used for built-ins, except for Object.
        /// </summary>
        NoOverrides = 1,

        /// <summary>
        /// Module doesn't allow its methods to be called by mangled (FooBar -> foo_bar) or mapped ([] -> get_Item) names.
        /// Used for built-ins.
        /// </summary>
        NoNameMapping = 2,

        /// <summary>
        /// Module is not published in the runtime global scope.
        /// </summary>
        NotPublished = 4,

        /// <summary>
        /// Module doesn't expose the underlying CLR type. The behavior is the same as if it was written in Ruby.
        /// (clr_new and clr_ctor won't work on such class/module, CLR methods won't be visible, etc.).
        /// </summary>
        NoUnderlyingType = 8,

        /// <summary>
        /// By default a non-builtin library load fails if it defines a class whose name conflicts with an existing constant name.
        /// If this restriction is applied to a class it can reopen an existing Ruby class of the same name but it can't specify an underlying type.
        /// This is required so that the library load doesn't depend on whether or not any instances of the existing Ruby class already exist. 
        /// </summary>
        AllowReopening = 16 | NoUnderlyingType,

        /// <summary>
        /// Default restrictions for built-in modules.
        /// </summary>
        Builtin = NoOverrides | NoNameMapping | NotPublished,

        All = Builtin
    }

    [Flags]
    public enum MethodLookup {
        Default = 0,
        Virtual = 1,
        ReturnForwarder = 2,
        FallbackToObject = 4,
    }

#if DEBUG
    [DebuggerDisplay("{DebugName}")]
#endif
    [DebuggerTypeProxy(typeof(RubyModule.DebugView))]
    [ReflectionCached]
    public partial class RubyModule : IDuplicable, IRubyObject {
        public static readonly RubyModule[]/*!*/ EmptyArray = new RubyModule[0];

        // interlocked
        internal static int _globalMethodVersion = 0;

        // interlocked; bumped only where a class's method version is (IncrementMethodVersion)
        private static int _methodTableVersion = 0;

        /// <summary>
        /// Read by JIT-compiled code as its one global guard: every method table change that can
        /// alter a resolution call sites depend on bumps it, so a specialization that snapshots it is
        /// invalidated by any def, undef, alias, include, extend or singleton-method definition that
        /// overrides something. Creating a class or module does not: a new module changes no existing
        /// class's resolution, and Class.new, Struct.new or a fresh singleton class after a method
        /// had been specialized would otherwise send that method down the generic path for good.
        /// </summary>
        public static int GlobalMethodVersion {
            get { return System.Threading.Volatile.Read(ref _methodTableVersion); }
        }
        internal static int _globalModuleId = 0;

        private enum State {
            Uninitialized,
            Initializing,
            Initialized
        }

        private readonly RubyContext/*!*/ _context;
        
        #region CLR Types and Namespaces

        // the namespace this module represents or null:
        private readonly NamespaceTracker _namespaceTracker;

        // The type this module represents or null. 
        // TODO: unify with _underlyingSystemType on classes
        private readonly TypeTracker _typeTracker;

        public TypeTracker TypeTracker {
            get { return _typeTracker; }
        }

        public NamespaceTracker NamespaceTracker {
            get { return _namespaceTracker; }
        }

        public bool IsInterface {
            get { return _typeTracker != null && _typeTracker.Type.IsInterface; }
        }

        public bool IsClrModule {
            get { return _typeTracker != null && IsModuleType(_typeTracker.Type); }
        }

        public virtual Type/*!*/ GetUnderlyingSystemType() {
            if (IsClrModule) {
                return _typeTracker.Type;
            } else {
                throw new InvalidOperationException();
            }
        }
        
        #endregion

        private readonly ModuleRestrictions _restrictions;
        private readonly WeakReference/*!*/ _weakSelf;
        
        // name of the module or null for anonymous modules:
        private string _name;

        // True if _name is a "temporary" name in MRI's sense: either set by Module#set_temporary_name or
        // derived from an outer module that doesn't have a permanent name itself. A temporary name can be
        // replaced (by set_temporary_name, or when the module is reachable from a permanently named one),
        // a permanent name can't.
        private bool _isTemporaryName;

        // Lazy interlocked init'd.
        // internal: the instance variable fast path reads it directly (RubyOps.GetInstanceVariable)
        internal RubyInstanceData _instanceData;
        
        #region Immediate/Singleton/Super Class

        // Lazy interlocked init'd.
        // Classes
        //   Null immediately after construction, initialized by RubyContext.CreateClass factory to a singleton class.
        //   This lazy initialization is needed to allow circular references among Kernel, Object, Module and Class.
        //   We create the singleton class eagerly in the factory since classes' singleton classes form a hierarchy parallel 
        //   to the main inheritance hierarachy of classes. If we didn't we would need to update super-references of all singleton subclasses 
        //   that were created after a singleton class is lazily created.
        // Modules
        //   Initialized to the class of the module and may later be changed to a singleton class (only if the singleton is needed).
        //   We don't create the singleton class eagerly to optimize rule generation. If we did, each (meta)module instance would receive its own immediate class
        //   different from other instances of the (meta)module. And thus the instances would use different method table versions eventhough they are the same.
        // Singleton Classes
        //   Self reference for dummy singletons (tha last singletons in the singleton chain).
        private RubyClass _immediateClass;

        /// <summary>
        /// A dummy singleton class is an immutable class that has no members and is used to terminate singleton class chain.
        /// </summary>
        /// <remarks>
        /// A method invoked on the last class in the singleton class chain before the dummy singleton is searched in the inheritance hierarchy of the dummy singleton:
        /// [dummy singleton, singleton(Class), singleton(Module), singleton(Object), Class, Module, Object].
        /// The ImmediateClass reference cannot be null (that would cause null-ref exception in rules), so we need to set it to the dummy.
        /// </remarks>
        public bool IsDummySingletonClass {
            get { return _immediateClass == this; }
        }

        public virtual bool IsSingletonClass {
            get { return false; }
        }

        public virtual bool IsClass {
            get { return false; }
        }

        public bool IsObjectClass {
            get { return ReferenceEquals(this, Context.ObjectClass); }
        }

        public bool IsBasicObjectClass {
            get { return ReferenceEquals(this, Context.BasicObjectClass); }
        }

        internal virtual RubyClass GetSuperClass() {
            return null;
        }

        // thread safe:
        internal void InitializeImmediateClass(RubyClass/*!*/ cls) {
            Debug.Assert(_immediateClass == null);
            _immediateClass = cls;
        }

        // thread safe:
        internal void InitializeImmediateClass(RubyClass/*!*/ singletonSuperClass, Action<RubyModule> trait) {
            Assert.NotNull(singletonSuperClass);

            RubyClass immediate;
            if (IsClass) {
                // class: eager singleton class construction:
                immediate = CreateSingletonClass(singletonSuperClass, trait);
                immediate.InitializeImmediateClass(_context.ClassClass.GetDummySingletonClass());
            } else if (trait != null) {
                // module: eager singleton class construction:
                immediate = CreateSingletonClass(singletonSuperClass, trait);
                immediate.InitializeImmediateClass(singletonSuperClass.GetDummySingletonClass());
            } else {
                // module: lazy singleton class construction:
                immediate = singletonSuperClass;
            }

            InitializeImmediateClass(immediate);
        }

        #endregion

        #region Mutable state guarded by ClassHierarchyLock

        [Emitted]
        public readonly VersionHandle Version;
        public readonly int Id;

        // List of dependent classes - subclasses of this class and classes to which this module is included to (forms a DAG).
        private WeakList<RubyClass> _dependentClasses;

        // Modules (not classes) whose flattened mixin list contains this module. Since Ruby 3.0 a module
        // included into this one later shows up in their ancestors too, so the update must reach them.
        private WeakList<RubyModule> _dependentModules;

#if DEBUG
        private int _referringMethodRulesSinceLastUpdate;
        
        public string DebugName { 
            get {
                string name;
                if (IsSingletonClass) {
                    object s = ((RubyClass)this).SingletonClassOf;
                    RubyModule m = s as RubyModule;
                    if (m != null) {
                        name = m.DebugName;
                    } else {
                        name = RuntimeHelpers.GetHashCode(s).ToString("x");
                    }
                    name = "S(" + name + ")";
                } else {
                    name = _name;
                }
                return name + " #" + Id;
            }
        }
#endif

        private enum MemberTableState {
            Uninitialized = 0,
            Initializing = 1,
            Initialized = 2
        }

        // constant table:
        private MemberTableState _constantsState = MemberTableState.Uninitialized;
        private Action<RubyModule> _constantsInitializer;
        private Dictionary<string, ConstantStorage> _constants;
        private Dictionary<string, RubyEncoding> _constantEncodings;

        // names this module declared private with Module#private_constant; null until one is
        // (guarded by the class hierarchy lock, like _constants)
        private HashSet<string> _privateConstants;

        // names this module declared deprecated with Module#deprecate_constant; null until
        // one is (guarded by the class hierarchy lock, like _constants)
        private HashSet<string> _deprecatedConstants;

        // private constants of IronRuby's own Ruby code that MRI has no counterpart of, hidden with
        // Module#__hide_constant__ from const_defined?, const_get and the rest of constant reflection;
        // null until one is. Replaced (under the class hierarchy lock) rather than changed: read without a lock.
        private HashSet<string> _hiddenConstants;

        // what Module#name hands out; see GetNameString. Cleared whenever the name changes.
        private MutableString _nameString;

        // { constant-name -> (source path, source line) }; lazily allocated, only holds constants
        // whose definition site is known (Module#const_source_location).
        private Dictionary<string, ConstantLocation> _constantLocations;

        // A class rather than a KeyValuePair<string, int>: a Dictionary whose values are a struct is
        // System.Private.CoreLib code ReadyToRun cannot precompile, JIT-compiled at startup otherwise.
        private sealed class ConstantLocation {
            internal readonly string/*!*/ SourcePath;
            internal readonly int SourceLine;

            internal ConstantLocation(string/*!*/ sourcePath, int sourceLine) {
                SourcePath = sourcePath;
                SourceLine = sourceLine;
            }
        }

        // method table:
        private MemberTableState _methodsState = MemberTableState.Uninitialized;
        private Dictionary<string, RubyMemberInfo> _methods;
        private Action<RubyModule> _methodsInitializer;

        // Names of the helpers the core prelude defines in this module for its own use (see IsUnlisted).
        private HashSet<string> _preludeHelpers;

        // class variable table:
        private Dictionary<string, object> _classVariables;

        //
        // The entire list of modules included in this one. Newly-added mixins are at the front of the array.
        // When adding a module that itself contains other modules, Ruby tries to maintain the ordering of the
        // contained modules so that method resolution is reasonably consistent.
        //
        // MRO walk: _prepends[0], ..., _prepends[p-1], this, _mixins[0], _mixins[1], ..., _mixins[n-1], super, ...
        private RubyModule[]/*!*/ _mixins;

        //
        // The entire list of modules prepended to this one (Module#prepend). Unlike mixins these are searched
        // *before* this module itself, so a prepended method wins over the module's own definition and `super`
        // from it reaches the module's own method. Newly-prepended modules are at the front of the array.
        // The array is flattened the same way _mixins is: prepending a module splices in that module's whole
        // ancestor chain (its own prepends, itself, its mixins).
        //
        private RubyModule[]/*!*/ _prepends;

        // A list of extension methods included into this type or null if none were included.
        // { method-name -> methods }
        internal Dictionary<string, List<ExtensionMethodInfo>> _extensionMethods;

        // The extension methods of this type that `using' a namespace (or a static class) can bring in,
        // or null: those of every namespace some `using' has named. Unlike _extensionMethods they are
        // not part of any method group; a call sees the ones active where it is made (see
        // ResolveMethodNoLock).  { CLR or Ruby method name (Where, where) -> methods }
        private Dictionary<string, List<ExtensionMethodInfo>> _scopedExtensionMethods;

        // { (name, activation, the group lookup found or NoBaseGroup) -> the method group a call of name sees
        // there }: the CLR method group extended by the active extension methods, or a group of those
        // alone (null if none applies).  For the class version in _scopedExtensionGroupsVersion; classes only.
        private Dictionary<(string, RefinementActivation, object), RubyMemberInfo> _scopedExtensionGroups;
        private int _scopedExtensionGroupsVersion;
        private static readonly object NoBaseGroup = new object();

        //
        // Refinements (Module#refine).  Two independent roles:
        //
        // - _refinements is set on the module that *calls* refine: { refined module -> refinement module }.
        //   It is what Module#refinements reports and what `using' walks.
        // - _refinedModule is set on the anonymous module that refine *returns*: the module it refines.
        //   It is what Refinement#target reports, and it is what tells ResolveSuperMethodNoLock where
        //   `super' from inside a refinement continues.
        //
        // Neither participates in the MRO: a refinement is spliced in front of the module it refines only
        // for calls whose lexical scope has activated it, which happens in RubyCallAction.Resolve.
        //
        private Dictionary<RubyModule/*!*/, RubyModule/*!*/> _refinements;
        private RubyModule _refinedModule;

        // set alongside _refinedModule: the module whose `refine' call made this refinement, and
        // so the module a `using' names to activate it
        private RubyModule _refinementHolder;
        
        #endregion

        #region Dynamic Sites

        // RubyModule, symbol -> object
        private CallSite<Func<CallSite, object, object, object>> _constantMissingCallbackSite;
        private CallSite<Func<CallSite, object, object, object>> _constantAddedCallbackSite;
        private CallSite<Func<CallSite, object, object, object>> _methodAddedCallbackSite;
        private CallSite<Func<CallSite, object, object, object>> _methodRemovedCallbackSite;
        private CallSite<Func<CallSite, object, object, object>> _methodUndefinedCallbackSite;

        internal object ConstantMissing(string/*!*/ name) {
            return Context.Send(ref _constantMissingCallbackSite, "const_missing", this, name);
        }

        /// <summary>
        /// Fires the Module#const_added hook. Called after the constant has been stored so that the callback
        /// can read it back with const_get, and - for `class X < Y` - after the superclass is known but before
        /// the "inherited" event, which is the order MRI documents.
        /// </summary>
        public void ConstantAdded(string/*!*/ name) {
            Assert.NotNull(name);
            Context.CallHook(ref _constantAddedCallbackSite, RubyClass.Hook.ConstantAdded, this, name);
        }

        // Ruby 1.8: called after method is added, except for alias_method which calls it before
        // Ruby 1.9: called before method is added
        public virtual void MethodAdded(string/*!*/ name) {
            Assert.NotNull(name);
            Debug.Assert(!IsSingletonClass);

            Context.CallHook(ref _methodAddedCallbackSite, RubyClass.Hook.MethodAdded, this, name);
        }

        internal virtual void MethodRemoved(string/*!*/ name) {
            Assert.NotNull(name);
            Debug.Assert(!IsSingletonClass);

            Context.CallHook(ref _methodRemovedCallbackSite, RubyClass.Hook.MethodRemoved, this, name);
        }

        internal virtual void MethodUndefined(string/*!*/ name) {
            Assert.NotNull(name);
            Debug.Assert(!IsSingletonClass);

            Context.CallHook(ref _methodUndefinedCallbackSite, RubyClass.Hook.MethodUndefined, this, name);
        }

        #endregion

        public ModuleRestrictions Restrictions {
            get { return _restrictions; }
        }

        internal RubyModule[]/*!*/ Mixins {
            get { return _mixins; }
        }

        internal RubyModule[]/*!*/ Prepends {
            get { return _prepends; }
        }

        #region Refinements

        /// <summary>
        /// Non-null if this module is a refinement, i.e. was created by Module#refine.  Then this is the
        /// module being refined (Refinement#target).
        /// </summary>
        public RubyModule RefinedModule {
            get { return _refinedModule; }
        }

        public bool IsRefinement {
            get { return _refinedModule != null; }
        }

        /// <summary>
        /// Copies a member in as this module's own, so that #owner reports the refinement.  Used by
        /// Refinement#import_methods, which cannot include the source module.
        /// </summary>
        public void ImportMethod(string/*!*/ name, RubyMemberInfo/*!*/ member) {
            var method = member as RubyMethodInfo;
            if (method != null && _refinementHolder != null) {
                // Imported methods have to be able to call each other: a body that says
                // `self.indent(n)' is calling a refined method, and that only resolves where the
                // refinement is in use. The copy therefore gets a scope of its own, sitting
                // between the body and the scope it was written in, with the refinement's holder
                // activated - which is exactly what a `using' at that place would have done.
                var scope = new RubyModuleScope(method.DeclaringScope, this);
                scope.ActivateRefinements(_refinementHolder);
                SetMethodNoEvent(Context, name, method.CopyWithScope(scope, this));
                return;
            }

            SetMethodNoEvent(Context, name, member.Copy(member.Flags, this));
        }

        /// <summary>
        /// Module#refine: returns the anonymous refinement module for <paramref name="refinedModule"/>,
        /// creating it on the first call.  Reopening the same class in the same holder yields the same
        /// module, which is what CRuby does.
        /// </summary>
        public RubyModule/*!*/ GetOrCreateRefinement(RubyModule/*!*/ refinedModule) {
            ContractUtils.RequiresNotNull(refinedModule, "refinedModule");
            using (Context.ClassHierarchyLocker()) {
                if (_refinements == null) {
                    _refinements = new Dictionary<RubyModule, RubyModule>();
                }
                RubyModule existing;
                if (_refinements.TryGetValue(refinedModule, out existing)) {
                    return existing;
                }
                RubyModule refinement = new RubyModule(Context.RefinementClass, null);
                refinement._refinedModule = refinedModule;
                refinement._refinementHolder = this;
                _refinements.Add(refinedModule, refinement);
                Context.RegisterRefinedModule(refinedModule);
                return refinement;
            }
        }

        /// <summary>
        /// The refinements this module declared itself, in no particular order.  Module#refinements does
        /// not report refinements of included modules.
        /// </summary>
        public void GetOwnRefinements(List<RubyModule/*!*/>/*!*/ result) {
            using (Context.ClassHierarchyLocker()) {
                if (_refinements != null) {
                    result.AddRange(_refinements.Values);
                }
            }
        }

        /// <summary>
        /// The refinements `using this' would activate: this module's own plus those of every module it
        /// includes (CRuby does follow includes for activation, only Module#refinements is own-only).
        /// </summary>
        internal void GetAllRefinements(List<RubyModule/*!*/>/*!*/ result) {
            using (Context.ClassHierarchyLocker()) {
                GetAllRefinementsNoLock(result);
            }
        }

        internal void GetAllRefinementsNoLock(List<RubyModule/*!*/>/*!*/ result) {
            ForEachAncestor(false, (m) => {
                if (m._refinements != null) {
                    foreach (RubyModule r in m._refinements.Values) {
                        if (!result.Contains(r)) {
                            result.Add(r);
                        }
                    }
                }
                return false;
            });
        }

        /// <summary>
        /// Appends this module's (and its includes') refinement of <paramref name="refinedModule"/>, if any.
        /// </summary>
        internal void GetActiveRefinementsOf(RubyModule/*!*/ refinedModule, List<RubyModule/*!*/>/*!*/ result) {
            GetActiveRefinementsOf(refinedModule, result, null);
        }

        /// <summary>
        /// Like the overload above, but only the refinements in <paramref name="snapshot"/> - the ones a
        /// `using' of this module found when it ran - unless that is null.
        /// </summary>
        internal void GetActiveRefinementsOf(RubyModule/*!*/ refinedModule, List<RubyModule/*!*/>/*!*/ result, HashSet<RubyModule> snapshot) {
            ForEachAncestor(false, (m) => {
                RubyModule refinement;
                if (m._refinements != null && m._refinements.TryGetValue(refinedModule, out refinement)) {
                    if (!result.Contains(refinement) && (snapshot == null || snapshot.Contains(refinement))) {
                        result.Add(refinement);
                    }
                }
                return false;
            });
        }

        #endregion

        public string Name {
            get { return _name; }
            internal set { _name = value; _nameString = null; }
        }

        /// <summary>
        /// True if the module has a name that cannot be changed any more - i.e. it is (transitively) reachable
        /// from Object via constants. MRI calls this a "permanent" name; Module#set_temporary_name refuses to
        /// touch one and a module that acquires one hands permanent names down to its nested modules.
        /// </summary>
        public bool HasPermanentName {
            get { return _name != null && !_isTemporaryName; }
        }

        internal bool HasTemporaryName {
            get { return _name != null && _isTemporaryName; }
        }

        /// <summary>
        /// Sets the module's name and propagates the change to nested modules that don't have a permanent name
        /// of their own, the way MRI's rb_set_class_path/set_sub_temporary_name do.
        /// </summary>
        public void SetName(string name, bool permanent) {
            SetName(name, permanent, null);
        }

        private void SetName(string name, bool permanent, Dictionary<object, bool> visited) {
            _name = name;
            _nameString = null;
            _isTemporaryName = name != null && !permanent;
            Version.SetName(name);

            // Collect first: the recursive call re-enters the constant tables.
            List<KeyValuePair<string, RubyModule>> nested = null;
            using (Context.ClassHierarchyLocker()) {
                EnumerateConstants((module, constName, value) => {
                    var m = value as RubyModule;
                    if (m != null && m != this && m.HasTemporaryName) {
                        if (nested == null) {
                            nested = new List<KeyValuePair<string, RubyModule>>();
                        }
                        nested.Add(new KeyValuePair<string, RubyModule>(constName, m));
                    }
                    return false;
                });
            }

            if (nested == null) {
                return;
            }

            if (visited == null) {
                visited = new Dictionary<object, bool>(ReferenceEqualityComparer<object>.Instance);
                visited[this] = true;
            }

            foreach (var entry in nested) {
                if (visited.ContainsKey(entry.Value)) {
                    continue;
                }
                visited[entry.Value] = true;
                entry.Value.SetName(name == null ? null : MakeNestedModuleName(entry.Key), permanent, visited);
            }
        }

        public RubyContext/*!*/ Context {
            get { return _context; }
        }

        internal virtual RubyGlobalScope GlobalScope {
            get { return null; }
        }

        internal WeakReference/*!*/ WeakSelf {
            get { return _weakSelf; }
        }

        internal Dictionary<string, List<ExtensionMethodInfo>> ExtensionMethods {
            get { return _extensionMethods; }
        }

        // default allocator:
        public RubyModule(RubyClass/*!*/ metaModuleClass) 
            : this(metaModuleClass, null) {
        }

        // creates an empty (meta)module:
        protected RubyModule(RubyClass/*!*/ metaModuleClass, string name)
            : this(metaModuleClass.Context, name, null, null, null, null, null, ModuleRestrictions.None) {
            
            // metaModuleClass represents a subclass of Module or its duplicate (Kernel#dup)
            InitializeImmediateClass(metaModuleClass, null);
        }

        internal RubyModule(RubyContext/*!*/ context, string name, Action<RubyModule> methodsInitializer, Action<RubyModule> constantsInitializer,
            RubyModule/*!*/[] expandedMixins, NamespaceTracker namespaceTracker, TypeTracker typeTracker, ModuleRestrictions restrictions) {

            Assert.NotNull(context);
            Debug.Assert(namespaceTracker == null || typeTracker == null || typeTracker.Type == typeof(object));
            Debug.Assert(expandedMixins == null ||
                CollectionUtils.TrueForAll(expandedMixins, (m) => m != this && m != null && !m.IsClass && m.Context == context)
            );

            _context = context;
            _name = name;
            _methodsInitializer = methodsInitializer;
            _constantsInitializer = constantsInitializer;
            _namespaceTracker = namespaceTracker;
            _typeTracker = typeTracker;
            _mixins = expandedMixins ?? EmptyArray;
            _prepends = EmptyArray;
            _restrictions = restrictions;
            _weakSelf = new WeakReference(this);

            Version = new VersionHandle(Interlocked.Increment(ref _globalMethodVersion));
            Version.SetName(name);
            Id = Interlocked.Increment(ref _globalModuleId);
            context.ObjectSpaceModules.Add(this);
        }

        #region Initialization (thread-safe)

        internal bool ConstantInitializationNeeded {
            get { return _constantsState == MemberTableState.Uninitialized; }
        }

        private void InitializeConstantTableNoLock() {
            if (!ConstantInitializationNeeded) return;

            _constants = new Dictionary<string, ConstantStorage>();
            _constantsState = MemberTableState.Initializing;

            try {
                if (_constantsInitializer != EmptyInitializer) {
                    if (_constantsInitializer != null) {
                        Utils.Log(_name ?? "<anonymous>", "CT_INIT");
                        // TODO: use lock-free operations in initializers
                        _constantsInitializer(this);
                    } else if (_typeTracker != null && !_typeTracker.Type.IsInterface) {
                        // Load types eagerly. We do this only for CLR types that have no constant initializer (not builtins) and 
                        // a constant access is performed (otherwise this method wouldn't be called).
                        // 
                        // Note: Interfaces cannot declare nested types in C#, we follow the suit here.
                        // We don't currently need this restriction but once we implement generic type overload inheritance properly
                        // we would need to deal with inheritance from interfaces, which might be too complex. 
                        // 
                        LoadNestedTypes();
                    }
                }
            } finally {
                _constantsInitializer = null;
                _constantsState = MemberTableState.Initialized;
            }
        }

        internal static readonly Action<RubyModule> EmptyInitializer = (_) => { };

        internal bool MethodInitializationNeeded {
            get { return _methodsState == MemberTableState.Uninitialized; }
        }

        private void InitializeMethodTableNoLock() {
            if (!MethodInitializationNeeded) return;

            InitializeDependencies();

            _methods = new Dictionary<string, RubyMemberInfo>();
            _methodsState = MemberTableState.Initializing;
            Context.MethodTableInitializationDepth++;

            try {
                if (_methodsInitializer != null) {
                    Utils.Log(_name ?? "<anonymous>", "MT_INIT");
                    // TODO: use lock-free operations in initializers?
                    _methodsInitializer(this);
                }
            } finally {
                _methodsInitializer = null;
                _methodsState = MemberTableState.Initialized;
                Context.MethodTableInitializationDepth--;
            }
        }

        internal void InitializeMethodsNoLock() {
            if (MethodInitializationNeeded) {
                InitializeMethodsNoLock(GetUninitializedAncestors(true));
            }
        }

        internal void InitializeMethodsNoLock(IList<RubyModule/*!*/>/*!*/ modules) {
            for (int i = modules.Count - 1; i >= 0; i--) {
                modules[i].InitializeMethodTableNoLock();
            }
        }

        internal void InitializeConstantsNoLock() {
            if (ConstantInitializationNeeded) {
                InitializeConstantsNoLock(GetUninitializedAncestors(false));
            }
        }

        internal void InitializeConstantsNoLock(IList<RubyModule/*!*/>/*!*/ modules) {
            for (int i = modules.Count - 1; i >= 0; i--) {
                modules[i].InitializeConstantTableNoLock();
            }
        }

        private List<RubyModule>/*!*/ GetUninitializedAncestors(bool methods) {
            var result = new List<RubyModule>();
            result.AddRange(_prepends);
            result.Add(this);
            result.AddRange(_mixins);
            var super = GetSuperClass();
            while (super != null && (methods ? super.MethodInitializationNeeded : super.ConstantInitializationNeeded)) {
                result.AddRange(super._prepends);
                result.Add(super);
                result.AddRange(super._mixins);
                super = super.SuperClass;
            }
            return result;
        }

        private void InitializeClassVariableTable() {
            if (_classVariables == null) {
                Interlocked.CompareExchange(ref _classVariables, new Dictionary<string, object>(), null);
            }
        }

        private void LoadNestedTypes() {
            Context.RequiresClassHierarchyLock();
            Debug.Assert(_constants != null && _constants.Count == 0);

            // TODO: Inherited generic overloads. We need a custom TypeGroup to do it right - part of the type group might be removed

            // TODO: protected types
            var bindingFlags = BindingFlags.Public | BindingFlags.DeclaredOnly;
            if (Context.DomainManager.Configuration.PrivateBinding) {
                bindingFlags |= BindingFlags.NonPublic;
            }

            // if the constant is redefined/removed from the base class. This is similar to method overload inheritance.
            Type[] types = _typeTracker.Type.GetNestedTypes(bindingFlags);
            var trackers = new List<TypeTracker>();
            var names = new List<string>();
            foreach (var type in types) {
                TypeTracker tracker = (NestedTypeTracker)MemberTracker.FromMemberInfo(type);

                var name = (type.IsGenericType) ? ReflectionUtils.GetNormalizedTypeName(type) : type.Name;
                int index = names.IndexOf(name);
                if (index != -1) {
                    trackers[index] = TypeGroup.UpdateTypeEntity(trackers[index], tracker);
                    names[index] = name;
                } else {
                    trackers.Add(tracker);
                    names.Add(name);
                }
            }

            for (int i = 0; i < trackers.Count; i++) {
                var tracker = trackers[i];
                ConstantStorage storage;
                if (tracker is TypeGroup) {
                    storage = new ConstantStorage(tracker, new WeakReference(tracker));
                } else {
                    var module = Context.GetModule(tracker.Type);
                    storage = new ConstantStorage(module, module.WeakSelf);
                }
                _constants[names[i]] = storage;
            }
        }

        internal void InitializeMembersFrom(RubyModule/*!*/ module) {
            Context.RequiresClassHierarchyLock();
            Mutate();

            Assert.NotNull(module);

            if (module._namespaceTracker != null && _constants == null) {
                // initialize the module so that we can copy all constants from it:
                module.InitializeConstantsNoLock();

                // initialize all ancestors of self:
                InitializeConstantsNoLock();
            } else {
                _constantsInitializer = Utils.CloneInvocationChain(module._constantsInitializer);
                _constantsState = module._constantsState;
            }

            _constants = (module._constants != null) ? new Dictionary<string, ConstantStorage>(module._constants) : null;
            _constantEncodings = (module._constantEncodings != null) ?
                new Dictionary<string, RubyEncoding>(module._constantEncodings) : null;
            _constantLocations = (module._constantLocations != null) ?
                new Dictionary<string, ConstantLocation>(module._constantLocations) : null;

            // copy namespace members:
            if (module._namespaceTracker != null) {
                Debug.Assert(_constants != null);
                foreach (KeyValuePair<string, object> constant in module._namespaceTracker) {
                    _constants.Add(constant.Key, new ConstantStorage(constant.Value));
                }
            }

            _methodsInitializer = Utils.CloneInvocationChain(module._methodsInitializer);
            _methodsState = module._methodsState;
            if (module._methods != null) {
                _methods = new Dictionary<string, RubyMemberInfo>(module._methods.Count);
                foreach (var method in module._methods) {
                    // the undefined/hidden markers are shared singletons, not copied (Integer.dup copies undef'd .new):
                    _methods[method.Key] = (method.Value.IsUndefined || method.Value.IsHidden || method.Value.IsInteropMember) ?
                        method.Value : method.Value.Copy(method.Value.Flags, this);
                }
            } else {
                _methods = null;
            }

            var sourceClassVariables = module._classVariables;
            if (sourceClassVariables != null) {
                // under the source's own table lock: another thread may be assigning to it
                lock (sourceClassVariables) {
                    _classVariables = new Dictionary<string, object>(sourceClassVariables);
                }
            } else {
                _classVariables = null;
            }
            _mixins = ArrayUtils.Copy(module._mixins);
            _prepends = ArrayUtils.Copy(module._prepends);

            // dependentModules - skip
            // tracker - skip, .NET members not copied
            
            // TODO:
            // - handle overloads cached in groups
            // - version updates
            MethodsUpdated("InitializeFrom");
        }

        public void InitializeModuleCopy(RubyModule/*!*/ module) {
            if (_context.IsObjectFrozen(this)) {
                throw RubyExceptions.CreateObjectFrozenError(_context, this);
            }

            using (Context.ClassHierarchyLocker()) {
                InitializeMembersFrom(module);
            }
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            
            // capture the current immediate class (it can change any time if it not a singleton class)
            RubyClass immediate = _immediateClass;

            RubyModule result = new RubyModule(immediate.IsSingletonClass ? immediate.SuperClass : immediate, null);

            // Singleton members are copied here, not in InitializeCopy. A module's singleton
            // methods are its module methods - what `def self.x' writes - and MRI's Module#dup
            // keeps them, unlike the singleton of an ordinary object, which #dup drops.
            if (immediate.IsSingletonClass) {
                var singletonClass = result.GetOrCreateSingletonClass();
                using (Context.ClassHierarchyLocker()) {
                    singletonClass.InitializeMembersFrom(immediate);
                    // its own instance variables too (rb_singleton_class_clone)
                    _context.CopyInstanceData(immediate, singletonClass, false);
                }
            }
            
            // copy instance variables:
            _context.CopyInstanceData(this, result, false);
            return result;
        }

        #endregion

        #region Versioning (thread-safe)

        // A version of a frozen module can still change if its super-classes/mixins change.
        private void Mutate() {
            // The dummy singleton standing in front of Integer and Symbol - what
            // `1.instance_eval { }' looks methods up through - is not a place to put one. MRI
            // refuses the same way, because there is no singleton class to put it in.
            if (IsDummySingletonClass) {
                throw RubyExceptions.CreateTypeError("can't define singleton");
            }
            if (IsFrozen) {
                // MRI raises FrozenError and names what is frozen:
                //   `def frozen_obj.x`    => "can't modify frozen Object: #<Object:0x...>"
                //                            (the singleton class is frozen with its object, but
                //                             the error is reported about the object)
                //   `class FrozenC; end`  => "can't modify frozen Class: FrozenC"
                var cls = this as RubyClass;
                if (cls != null && cls.IsSingletonClass) {
                    throw RubyExceptions.CreateObjectFrozenError(_context, cls.SingletonClassOf);
                }
                throw RubyExceptions.CreateObjectFrozenError(_context, this);
            }
        }

        [Conditional("DEBUG")]
        internal void OwnedMethodCachedInSite() {
            Context.RequiresClassHierarchyLock();
#if DEBUG
            _referringMethodRulesSinceLastUpdate++;
#endif
        }

        internal virtual void InitializeDependencies() {
            // nop
        }

        internal WeakList<RubyClass/*!*/>/*!*/ DependentClasses {
            get {
                Context.RequiresClassHierarchyLock();
                if (_dependentClasses == null) {
                    _dependentClasses = new WeakList<RubyClass>();
                }
                return _dependentClasses;
            }
        }

        /// <summary>
        /// The classes that name this one as their direct superclass, excluding singleton
        /// classes -- Ruby 3.1's Class#subclasses. The dependent-class list exists to
        /// propagate method-cache invalidation and holds every descendant, so it is filtered
        /// down to the direct ones here.
        /// </summary>
        public List<RubyClass>/*!*/ GetDirectSubclasses() {
            var result = new List<RubyClass>();
            using (Context.ClassHierarchyLocker()) {
                if (_dependentClasses != null) {
                    foreach (var cls in _dependentClasses) {
                        if (cls != null && !cls.IsSingletonClass && ReferenceEquals(cls.SuperClass, this)) {
                            result.Add(cls);
                        }
                    }
                }
            }
            return result;
        }

        internal void AddDependentClass(RubyClass/*!*/ dependentClass) {
            Context.RequiresClassHierarchyLock();
            Assert.NotNull(dependentClass);

            foreach (var cls in DependentClasses) {
                if (ReferenceEquals(dependentClass, cls)) {
                    return;
                }
            }

            DependentClasses.Add(dependentClass.WeakSelf);
        }

        private void IncrementMethodVersion() {
            if (IsClass) {
                Version.Method = Interlocked.Increment(ref _globalMethodVersion);
                Interlocked.Increment(ref _methodTableVersion);
            }
        }

        internal void MethodsUpdated(string/*!*/ reason) {
            Context.RequiresClassHierarchyLock();

            int affectedModules = 0;
            int affectedRules = 0;
            Func<RubyModule, bool> visitor = (module) => {
                module.IncrementMethodVersion();
#if DEBUG
                affectedModules++;
                affectedRules += module._referringMethodRulesSinceLastUpdate;
                module._referringMethodRulesSinceLastUpdate = 0;
#endif
                // TODO (opt?): stop updating if a class that defines a method of the same name is reached.
                return false;
            };

            visitor(this);
            ForEachRecursivelyDependentClass(visitor);

            Utils.Log(String.Format("{0,-50} {1,-30} affected={2,-5} rules={3,-5}", Name, reason, affectedModules, affectedRules), "UPDATED");
        }

        /// <summary>
        /// Invalidates the rules bound to this module and to every module that depends on it, visiting
        /// each once. (MethodsUpdated walks every path of the dependency graph, which from BasicObject
        /// is far too many.)
        /// </summary>
        internal void AllDependentMethodsUpdated(string/*!*/ reason) {
            Context.RequiresClassHierarchyLock();

            var visited = new HashSet<RubyModule>(ReferenceEqualityComparer<RubyModule>.Instance);
            var stack = new Stack<RubyModule>();
            stack.Push(this);
            while (stack.Count > 0) {
                var module = stack.Pop();
                if (!visited.Add(module)) {
                    continue;
                }
                module.IncrementMethodVersion();
                if (module._dependentClasses != null) {
                    foreach (var cls in module._dependentClasses) {
                        stack.Push(cls);
                    }
                }
            }

            Utils.Log(String.Format("{0,-50} {1,-30} affected={2,-5}", Name, reason, visited.Count), "UPDATED");
        }

        /// <summary>
        /// Calls given action on all modules that are directly or indirectly nested into this module.
        /// </summary>
        private bool ForEachRecursivelyDependentClass(Func<RubyModule, bool>/*!*/ action) {
            Context.RequiresClassHierarchyLock();

            if (_dependentClasses != null) {
                foreach (var cls in _dependentClasses) {
                    if (action(cls)) {
                        return true;
                    }

                    if (cls.ForEachRecursivelyDependentClass(action)) {
                        return true;
                    }
                }
            }
            return false;
        }

        #endregion

        #region IRubyObject Members

        // thread-safe:
        public RubyClass ImmediateClass {
            get {
                return _immediateClass;
            }
            set {
                throw new InvalidOperationException("Cannot change the immediate class of a module");
            }
        }

        // thread-safe:
        public RubyInstanceData TryGetInstanceData() {
            return _instanceData;
        }

        // thread-safe:
        public RubyInstanceData GetInstanceData() {
            return RubyOps.GetInstanceData(ref _instanceData);
        }

        // thread-safe:
        public virtual bool IsFrozen {
            get { return IsModuleFrozen; }
        }

        // thread-safe: _instanceData cannot be unset
        internal bool IsModuleFrozen {
            get { return _instanceData != null && _instanceData.IsFrozen; }
        }

        // thread-safe:
        public void Freeze() {
            GetInstanceData().Freeze();
        }

        int IRubyObject.BaseGetHashCode() {
            return base.GetHashCode();
        }

        bool IRubyObject.BaseEquals(object other) {
            return base.Equals(other);
        }

        string/*!*/ IRubyObject.BaseToString() {
            return base.ToString();
        }

        public override string/*!*/ ToString() {
            return _name ?? "<anonymous>";
        }

        #endregion

        #region Factories (thread-safe)

        // Ruby constructor:
        public static object CreateAnonymousModule(RubyScope/*!*/ scope, BlockParam body, RubyClass/*!*/ self) {
            RubyModule newModule = new RubyModule(self, null);
            return (body != null) ? RubyUtils.EvaluateInModule(newModule, body, new[] { (object)newModule }, newModule) : newModule;
        }

        // thread safe:
        public RubyClass/*!*/ GetOrCreateSingletonClass() {
            if (IsDummySingletonClass) {
                throw new InvalidOperationException("Dummy singleton class has no singleton class");
            }

            RubyClass immediate = _immediateClass;
            RubyClass singletonSuper;
            RubyClass singletonImmediate;

            if (!immediate.IsSingletonClass) {
                // finish module singleton initialization:
                Debug.Assert(!IsClass);
                singletonSuper = immediate;
                singletonImmediate = immediate.GetDummySingletonClass();
            } else if (immediate.IsDummySingletonClass) {
                // expanding singleton chain:
                singletonSuper = immediate.SuperClass;
                singletonImmediate = immediate;

                // The singleton of a class's singleton class descends from the singleton of the
                // superclass's singleton class, as in MRI: #<Class:#<Class:K>> < #<Class:#<Class:H>>
                // for K < H. The chain ends at BasicObject's, whose superclass is #<Class:Class>.
                var cls = this as RubyClass;
                if (cls != null && cls.IsSingletonClass && cls.SingletonClassOf is RubyClass) {
                    var super = cls.SuperClass;
                    if (super != null && super.IsSingletonClass && !super.IsDummySingletonClass) {
                        singletonSuper = super.GetOrCreateSingletonClass();
                    }
                }
            } else {
                return immediate;
            }

            var singleton = CreateSingletonClass(singletonSuper, null);
            singleton.InitializeImmediateClass(singletonImmediate);
            Interlocked.CompareExchange(ref _immediateClass, singleton, immediate);

            Debug.Assert(_immediateClass.IsSingletonClass && !_immediateClass.IsDummySingletonClass);
            return _immediateClass;
        }

        /// <summary>
        /// Create a new singleton class for this module. 
        /// Doesn't attach this module to it yet, the caller needs to do so.
        /// </summary>
        /// <remarks>Thread safe.</remarks>
        internal RubyClass/*!*/ CreateSingletonClass(RubyClass/*!*/ superClass, Action<RubyModule> trait) {
            // Note that in MRI, member tables of dummy singleton are shared with the class the dummy is singleton for
            // This is obviously an implementation detail leaking to the language and we don't support that.

            // real class object and it's singleton share the tracker:
            TypeTracker tracker = (IsSingletonClass) ? null : _typeTracker;

            // Singleton should have the same restrictions as the module it is singleton for.
            // Reason: We need static methods of builtins (e.g. Object#Equals) not to be exposed under Ruby names (Object#equals).
            // We also want static methods of non-builtins to be visible under both CLR and Ruby names. 
            var result = new RubyClass(
                Context, null, null, this, trait, null, null, superClass, null, tracker, null, false, true, this.Restrictions
            );
#if DEBUG
            result.Version.SetName(result.DebugName);
#endif
            return result;
        }

        #endregion

        #region Ancestors (thread-safe)

        // Return true from action to terminate enumeration.
        public bool ForEachAncestor(bool inherited, Func<RubyModule/*!*/, bool>/*!*/ action) {
            Context.RequiresClassHierarchyLock();
            
            if (inherited) {
                return ForEachAncestor(action);
            } else {
                return ForEachDeclaredAncestor(action);
            }
        }

        internal virtual bool ForEachAncestor(Func<RubyModule/*!*/, bool>/*!*/ action) {
            Context.RequiresClassHierarchyLock();

            return ForEachDeclaredAncestor(action);
        }

        internal bool ForEachDeclaredAncestor(Func<RubyModule/*!*/, bool>/*!*/ action) {
            Context.RequiresClassHierarchyLock();

            // prepended modules come before this module (Module#prepend):
            foreach (RubyModule p in _prepends) {
                if (action(p)) return true;
            }

            // this module:
            if (action(this)) return true;

            // mixins:
            foreach (RubyModule m in _mixins) {
                if (action(m)) return true;
            }

            return false;
        }

        #endregion

        #region Constants (thread-safe)

        // Value of constant that is to be auto-loaded on first use.
        internal sealed class AutoloadedConstant {
            private readonly MutableString/*!*/ _path;
            private readonly string/*!*/ _featureName;
            private bool _loaded;

            // The full path of the file, once it has been required some other way than through this
            // autoload - directly, or before the autoload was even declared. MRI then considers the
            // autoload done for as long as that feature stays in $LOADED_FEATURES: it remains among
            // the module's constants but defines nothing - #autoload? is nil, #const_defined? false.
            // Deleting the feature from $LOADED_FEATURES brings the autoload back.
            private volatile string _requiredFeature;

            // The thread running the file, or null. MRI hides an autoload in progress from the very
            // thread performing it -- so that the file's own assignment is what defines the constant,
            // and #defined?, #const_defined? and #autoload? answer as if it were not there -- while
            // every other thread still sees the pending autoload and waits for it.
            private Thread _loadingThread;

            // File already loaded? An auto-loaded constant can be referenced by mutliple classes (via duplication).
            // After the constant is accessed in one of them and the file is loaded access to the other duplicates doesn't trigger file load.
            public bool Loaded { get { return _loaded; } }
            public MutableString/*!*/ Path { get { return _path; } }

            // Declared by the core prelude, for a class MRI has from the start (Set): #autoload? does
            // not name a file for it, since there is none to MRI.
            internal bool IsCore { get; set; }

            // The file name without directory or .rb extension - what a #require of some other
            // spelling of the same file has to share with it before resolving both is worth it.
            internal string/*!*/ FeatureName { get { return _featureName; } }

            public AutoloadedConstant(MutableString/*!*/ path) {
                Assert.NotNull(path);
                Debug.Assert(path.IsFrozen);
                _path = path;
                _featureName = Loader.GetFeatureName(path.ConvertToString());
            }

            public void RequiredAs(string/*!*/ fullPath) {
                _requiredFeature = fullPath;
            }

            public bool IsSatisfied(Loader/*!*/ loader) {
                string feature = _requiredFeature;
                return feature != null && !IsLoading && loader.IsFeatureRequiredOrRequiring(feature);
            }

            public bool IsDead {
                get { return _loaded && !IsLoading; }
            }

            public bool IsLoading {
                get { lock (this) { return _loadingThread != null; } }
            }

            public bool IsLoadingOnCurrentThread {
                get { lock (this) { return _loadingThread == Thread.CurrentThread; } }
            }

            public void BeginLoad() {
                lock (this) { _loadingThread = Thread.CurrentThread; }
            }

            // What the loading thread assigned to the constant (and where) while the file ran. MRI
            // keeps such a value in the autoload until the file has finished: the loading thread
            // sees the constant, every other thread still sees the pending autoload - and waits for
            // it rather than find a half-built class.
            private RubyModule _pendingOwner;
            private string _pendingName;
            private object _pendingValue;
            private string _pendingSourcePath;
            private int _pendingSourceLine;

            internal bool TrySetPendingValue(RubyModule/*!*/ owner, string/*!*/ name, object value) {
                lock (this) {
                    if (_loadingThread != Thread.CurrentThread) {
                        return false;
                    }
                    _pendingOwner = owner;
                    _pendingName = name;
                    _pendingValue = value;
                    return true;
                }
            }

            internal bool TrySetPendingLocation(string/*!*/ sourcePath, int sourceLine) {
                lock (this) {
                    if (_loadingThread != Thread.CurrentThread || _pendingOwner == null) {
                        return false;
                    }
                    _pendingSourcePath = sourcePath;
                    _pendingSourceLine = sourceLine;
                    return true;
                }
            }

            // The value the current thread assigned while loading the file, if it is that thread.
            internal bool TryGetPendingValue(out object value) {
                lock (this) {
                    if (_loadingThread == Thread.CurrentThread && _pendingOwner != null) {
                        value = _pendingValue;
                        return true;
                    }
                }
                value = null;
                return false;
            }

            internal bool TryGetPendingLocation(out string sourcePath, out int sourceLine) {
                lock (this) {
                    if (_loadingThread == Thread.CurrentThread && _pendingSourcePath != null) {
                        sourcePath = _pendingSourcePath;
                        sourceLine = _pendingSourceLine;
                        return true;
                    }
                }
                sourcePath = null;
                sourceLine = 0;
                return false;
            }

            public void EndLoad() {
                RubyModule owner;
                lock (this) {
                    owner = _pendingOwner;
                }

                if (owner != null) {
                    // publish the value before any waiting thread is let go
                    using (owner.Context.ClassHierarchyLocker()) {
                        lock (this) {
                            owner.PublishAutoloadedValueNoLock(this, _pendingName, _pendingValue, _pendingSourcePath, _pendingSourceLine);
                            _pendingOwner = null;
                            _pendingName = null;
                            _pendingValue = null;
                            _pendingSourcePath = null;
                            _loadingThread = null;
                            Monitor.PulseAll(this);
                        }
                    }
                    return;
                }

                lock (this) {
                    _loadingThread = null;
                    Monitor.PulseAll(this);
                }
            }

            /// <summary>
            /// Waits for another thread's load to finish. The class hierarchy lock must be released
            /// first, or the loading thread could never make progress.
            /// </summary>
            public void WaitForLoad() {
                lock (this) {
                    while (_loadingThread != null && _loadingThread != Thread.CurrentThread) {
                        Monitor.Wait(this);
                    }
                }
            }

            public bool Load(RubyGlobalScope/*!*/ autoloadScope) {
                if (_loaded) {
                    return false;
                }

                 using (autoloadScope.Context.ClassHierarchyUnlocker()) {
                     _loaded = true;
                     return IsCore
                         ? autoloadScope.Context.Loader.LoadAsCorePrelude(() => RequireFromMain(autoloadScope))
                         : RequireFromMain(autoloadScope);
                 }
            }

            // MRI loads the file by calling main.require(path), so a #require redefined on main -
            // or a mock of it - is what runs. The builtin one is called directly.
            private bool RequireFromMain(RubyGlobalScope/*!*/ autoloadScope) {
                var context = autoloadScope.Context;
                object main = autoloadScope.MainObject;
                var method = context.ResolveMethod(main, "require", VisibilityContext.AllVisible).Info;
                // It has to be the builtin itself that is skipped, not everything Kernel declares:
                // Zeitwerk reopens Kernel and defines its own #require there, and it is that method
                // an autoload of a directory (an implicit namespace) relies on being called.
                if (method == null || method is RubyLibraryMethodInfo) {
                    return context.Loader.LoadFile(autoloadScope.Scope, null, _path, LoadFlags.Require);
                }

                var site = CallSite<Func<CallSite, object, object, object>>.Create(
                    RubyCallAction.Make(context, "require", RubyCallSignature.WithImplicitSelf(1))
                );
                return RubyOps.IsTrue(site.Target(site, main, _path));
            }
        }

        public static bool IsAutoload(object value) {
            return value is AutoloadedConstant;
        }

        // An autoload of a file already required, or of the file this thread is requiring right now (an
        // autoload of __FILE__), is satisfied from the start. Every autoload is registered with the
        // loader, which is what lets a direct #require of the same file stand in for it.
        private AutoloadedConstant/*!*/ CreateAutoload(MutableString/*!*/ path, string requiredFeature) {
            var result = new AutoloadedConstant(path) { IsCore = _context.Loader.IsLoadingCorePrelude };
            if (requiredFeature != null) {
                result.RequiredAs(requiredFeature);
            }
            _context.Loader.RegisterAutoload(result);
            return result;
        }

        public string/*!*/ MakeNestedModuleName(string nestedModuleSimpleName) {
            if (IsObjectClass || nestedModuleSimpleName == null) {
                return nestedModuleSimpleName;
            }

            // An anonymous outer module contributes "#<Module:0x...>", not nothing:
            // module m::N inside m = Module.new is "#<Module:0x...>::N", not "::N".
            string outer = _name ?? GetDisplayName(_context, false).ToString();
            return outer + "::" + nestedModuleSimpleName;
        }

        // not thread-safe
        public void ForEachConstant(bool inherited, Func<RubyModule/*!*/, string/*!*/, object, bool>/*!*/ action) {
            Context.RequiresClassHierarchyLock();

            ForEachAncestor(inherited, delegate(RubyModule/*!*/ module) {
                // notification that we entered the module (it could have no constant):
                if (action(module, null, Missing.Value)) return true;

                return module.EnumerateConstants(action);
            });
        }

        /// <summary>
        /// Module#private_constant / #public_constant. A private constant can still be reached
        /// from the module itself and from lexical scopes nested in it; what it stops is a
        /// qualified reference from outside - `Mod::NAME`.
        /// </summary>
        public void SetConstantVisibility(string/*!*/ name, bool isPrivate) {
            using (Context.ClassHierarchyLocker()) {
                // MRI's rb_const_lookup looks in the module's own table only: a constant
                // inherited from a superclass cannot have its visibility changed from here.
                ConstantStorage storage;
                if (!TryGetConstantNoAutoloadCheck(name, out storage)) {
                    throw RubyExceptions.CreateNameError(String.Format("constant {0}::{1} not defined",
                        Context.GetModuleDisplayName(this), name));
                }

                if (isPrivate) {
                    if (_privateConstants == null) {
                        _privateConstants = new HashSet<string>();
                    }
                    _privateConstants.Add(name);
                } else if (_privateConstants != null) {
                    _privateConstants.Remove(name);
                }
                _context.ConstantAccessVersion++;
            }
        }

        /// <summary>True if this module itself declared the constant private.</summary>
        public bool IsPrivateConstant(string/*!*/ name) {
            return _privateConstants != null && _privateConstants.Contains(name);
        }

        /// <summary>
        /// Module#__hide_constant__: makes the constant private, and leaves it out of constant reflection
        /// too - IronRuby's Ruby code keeps its own state in constants that MRI keeps in C, where Ruby
        /// cannot see it. The code nested in the module still reads it by name, as it reads a private one.
        /// </summary>
        public void HideConstant(string/*!*/ name) {
            SetConstantVisibility(name, true);
            using (Context.ClassHierarchyLocker()) {
                var names = _hiddenConstants;
                if (names == null || !names.Contains(name)) {
                    Volatile.Write(ref _hiddenConstants, names != null ? new HashSet<string>(names) { name } : new HashSet<string> { name });
                }
                Context.NoteHiddenConstant(name);
            }
        }

        /// <summary>True if this module itself hid the constant (<see cref="HideConstant"/>).</summary>
        public bool IsHiddenConstant(string/*!*/ name) {
            var names = Volatile.Read(ref _hiddenConstants);
            return names != null && names.Contains(name);
        }

        /// <summary>
        /// True if the module that declares <paramref name="name"/> - this one or the first
        /// ancestor that has it - hid it.
        /// </summary>
        public bool IsHiddenConstantInAncestors(string/*!*/ name) {
            Context.RequiresClassHierarchyLock();
            var owner = GetConstantOwnerNoLock(name);
            return owner != null && owner.IsHiddenConstant(name);
        }

        /// <summary>
        /// Module#deprecate_constant. Like #private_constant this looks in the module's own table
        /// only: a constant inherited from an ancestor cannot be deprecated from here.
        /// </summary>
        public void SetConstantDeprecated(string/*!*/ name) {
            using (Context.ClassHierarchyLocker()) {
                ConstantStorage storage;
                if (!TryGetConstantNoAutoloadCheck(name, out storage)) {
                    throw RubyExceptions.CreateNameError(String.Format("constant {0}::{1} not defined",
                        Context.GetModuleDisplayName(this), name));
                }

                if (_deprecatedConstants == null) {
                    _deprecatedConstants = new HashSet<string>();
                }
                _deprecatedConstants.Add(name);
                _context.NoteDeprecatedConstant();
                _context.ConstantAccessVersion++;
            }
        }

        /// <summary>True if this module itself declared the constant deprecated.</summary>
        public bool IsDeprecatedConstant(string/*!*/ name) {
            return _deprecatedConstants != null && _deprecatedConstants.Contains(name);
        }

        /// <summary>
        /// The module that declares <paramref name="name"/> if that module declared it deprecated,
        /// otherwise null. MRI's warning names the owner rather than the module the reference went
        /// through: with C owned by Base, `Sub::C` says "constant Base::C is deprecated".
        /// </summary>
        internal RubyModule GetDeprecatedConstantOwnerNoLock(string/*!*/ name) {
            if (!_context.HasDeprecatedConstants) {
                return null;
            }
            var owner = GetConstantOwnerNoLock(name);
            return (owner != null && owner.IsDeprecatedConstant(name)) ? owner : null;
        }

        /// <summary>
        /// The module that declares <paramref name="name"/>: this one or the first ancestor that
        /// has it, or null if none does.
        /// </summary>
        internal RubyModule GetConstantOwnerNoLock(string/*!*/ name) {
            Context.RequiresClassHierarchyLock();

            RubyModule owner = null;
            ForEachAncestor(true, (module) => {
                ConstantStorage storage;
                if (module.TryGetConstantNoAutoloadCheck(name, out storage)) {
                    owner = module;
                    return true;
                }
                return false;
            });
            return owner;
        }

        /// <summary>
        /// True if the module that declares <paramref name="name"/> - this one or the first
        /// ancestor that has it - declared it private.
        /// </summary>
        internal bool IsPrivateConstantInAncestors(string/*!*/ name) {
            var owner = GetConstantOwnerNoLock(name);
            return owner != null && owner.IsPrivateConstant(name);
        }

        /// <summary>
        /// True if the only place <paramref name="name"/> is found is Object. A qualified
        /// reference - `Mod::NAME` - does not reach a top-level constant that way: MRI stops the
        /// ancestor search before Object unless the receiver is Object itself.
        /// </summary>
        internal bool IsTopLevelConstantOnly(string/*!*/ name) {
            if (IsObjectClass) {
                return false;
            }
            var owner = GetConstantOwnerNoLock(name);
            return owner != null && owner.IsObjectClass;
        }

        // thread-safe:
        public void SetConstant(string/*!*/ name, object value) {
            using (Context.ClassHierarchyLocker()) {
                SetConstantNoLock(name, value);
            }
        }

        /// <summary>
        /// Records where a constant of this module was defined, for Module#const_source_location.
        /// Constants defined by libraries have no location (MRI reports [] for those).
        /// The table is allocated lazily so modules whose constants all come from C#/library code pay nothing.
        /// </summary>
        public void SetConstantLocation(string/*!*/ name, string sourcePath, int sourceLine) {
            // Nor do the constants the core prelude defines: they are MRI's C-defined core.
            if (sourcePath == null || Context.Loader.IsLoadingCorePrelude) {
                return;
            }
            using (Context.ClassHierarchyLocker()) {
                SetConstantLocationNoLock(name, sourcePath, sourceLine);
            }
        }

        public void SetConstantEncoding(string/*!*/ name, RubyEncoding/*!*/ encoding) {
            using (Context.ClassHierarchyLocker()) {
                if (_constantEncodings == null) {
                    _constantEncodings = new Dictionary<string, RubyEncoding>();
                }
                _constantEncodings[name] = encoding;
            }
        }

        public RubyEncoding/*!*/ GetConstantEncoding(string/*!*/ name) {
            RubyEncoding encoding;
            return _constantEncodings != null && _constantEncodings.TryGetValue(name, out encoding)
                ? encoding : RubyEncoding.UTF8;
        }

        private void SetConstantLocationNoLock(string/*!*/ name, string/*!*/ sourcePath, int sourceLine) {
            ConstantStorage existing;
            AutoloadedConstant autoloaded;
            if (_constants != null && _constants.TryGetValue(name, out existing) && !existing.IsRemoved
                && (autoloaded = existing.Value as AutoloadedConstant) != null && autoloaded.TrySetPendingLocation(sourcePath, sourceLine)) {
                return;
            }

            if (_constantLocations == null) {
                _constantLocations = new Dictionary<string, ConstantLocation>();
            }
            _constantLocations[name] = new ConstantLocation(sourcePath, sourceLine);
        }

        public bool TryGetConstantLocation(string/*!*/ name, out string sourcePath, out int sourceLine) {
            using (Context.ClassHierarchyLocker()) {
                ConstantStorage existing;
                AutoloadedConstant autoloaded;
                if (_constants != null && _constants.TryGetValue(name, out existing) && !existing.IsRemoved
                    && (autoloaded = existing.Value as AutoloadedConstant) != null && autoloaded.TryGetPendingLocation(out sourcePath, out sourceLine)) {
                    return true;
                }
            }

            ConstantLocation location;
            if (_constantLocations != null && _constantLocations.TryGetValue(name, out location)) {
                sourcePath = location.SourcePath;
                sourceLine = location.SourceLine;
                return true;
            }
            sourcePath = null;
            sourceLine = 0;
            return false;
        }

        private void RemoveConstantLocationNoLock(string/*!*/ name) {
            if (_constantLocations != null) {
                _constantLocations.Remove(name);
            }
        }

        internal void Publish(string/*!*/ name) {
            RubyOps.ScopeSetMember(_context.TopGlobalScope, name, this);
        }

        private void SetConstantNoLock(string/*!*/ name, object value) {
            Mutate();
            SetConstantNoMutateNoLock(name, value);
        }

        internal void SetConstantNoMutateNoLock(string/*!*/ name, object value) {
            Context.RequiresClassHierarchyLock();

            InitializeConstantsNoLock();
            _context.ConstantAccessVersion++;

            // assigned by the file an autoload of the constant is running: kept by the autoload until it is done
            ConstantStorage existing;
            AutoloadedConstant autoloaded;
            if (!(value is AutoloadedConstant) && _constants.TryGetValue(name, out existing) && !existing.IsRemoved
                && (autoloaded = existing.Value as AutoloadedConstant) != null && autoloaded.TrySetPendingValue(this, name, value)) {
                return;
            }

            _constants[name] = new ConstantStorage(value);
        }

        // An autoload finished: what its file assigned to the constant becomes the constant, unless
        // the autoload is no longer what the constant holds.
        private void PublishAutoloadedValueNoLock(AutoloadedConstant/*!*/ autoloaded, string/*!*/ name, object value,
            string sourcePath, int sourceLine) {
            Context.RequiresClassHierarchyLock();

            ConstantStorage existing;
            if (_constants != null && _constants.TryGetValue(name, out existing) && !existing.IsRemoved && ReferenceEquals(existing.Value, autoloaded)) {
                Mutate();
                _context.ConstantAccessVersion++;
                _constants[name] = new ConstantStorage(value);
                if (sourcePath != null) {
                    SetConstantLocationNoLock(name, sourcePath, sourceLine);
                }
            }
        }

        /// <summary>
        /// Sets constant of this module. 
        /// Returns true if the constant is already defined in the module and it is not an autoloaded constant.
        /// </summary>
        /// <remarks>
        /// Thread safe.
        /// </remarks>
        public bool SetConstantChecked(string/*!*/ name, object value) {
            using (Context.ClassHierarchyLocker()) {
                ConstantStorage existing;
                var result = TryLookupConstantNoLock(false, false, null, name, out existing);
                SetConstantNoLock(name, value);
                return result == ConstantLookupResult.Found;
            }
        }
        
        // thread-safe:
        public void SetAutoloadedConstant(string/*!*/ name, MutableString/*!*/ path) {
            string requiredFeature = Context.Loader.GetRequiredFeature(path.ConvertToString());
            using (Context.ClassHierarchyLocker()) {
                ConstantStorage existing;
                if (TryGetConstantNoAutoloadCheck(name, out existing)) {
                    var autoloaded = existing.Value as AutoloadedConstant;
                    // A real constant wins - autoload is a nop. A pending autoload is replaced by the new one.
                    if (autoloaded == null || autoloaded.Loaded) {
                        return;
                    }
                }
                SetConstantNoLock(name, CreateAutoload(MutableString.Create(path).Freeze(), requiredFeature));
            }
        }

        // thread-safe:
        public MutableString GetAutoloadedConstantPath(string/*!*/ name) {
            return GetAutoloadedConstantPath(name, false);
        }

        // thread-safe:
        public MutableString GetAutoloadedConstantPath(string/*!*/ name, bool inherit) {
            using (Context.ClassHierarchyLocker()) {
                MutableString result = null;
                ForEachAncestor(inherit, (module) => {
                    ConstantStorage storage;
                    AutoloadedConstant autoloaded;
                    if (module.TryGetConstantNoAutoloadCheck(name, out storage)) {
                        // The thread running the file sees no pending autoload of its own, so
                        // #autoload? answers nil there while other threads still get the path.
                        // Load() marks itself loaded before running the file, so "not loaded yet"
                        // alone would hide the pending autoload from every thread for its duration.
                        if ((autoloaded = storage.Value as AutoloadedConstant) != null
                            && !autoloaded.IsCore
                            && (!autoloaded.Loaded || autoloaded.IsLoading)
                            && !autoloaded.IsLoadingOnCurrentThread
                            && !autoloaded.IsSatisfied(Context.Loader)) {
                            result = autoloaded.Path;
                        }
                        // a constant found in this module ends the search whether it is an autoload or not
                        return true;
                    }
                    return false;
                });
                return result;
            }
        }

        internal bool TryResolveConstant(RubyContext/*!*/ callerContext, RubyGlobalScope autoloadScope, string/*!*/ name, out ConstantStorage value) {
            return callerContext != Context ?
                TryResolveConstant(autoloadScope, name, out value) :
                TryResolveConstantNoLock(autoloadScope, name, out value);
        }

        public bool TryGetConstant(RubyGlobalScope autoloadScope, string/*!*/ name, out object value) {
            ConstantStorage storage;
            var result = TryGetConstant(autoloadScope, name, out storage);
            value = storage.Value;
            return result;
        }

        /// <summary>
        /// Get constant defined in this module or any of its ancestors, which is what
        /// Module#const_defined? searches unless it is passed inherit: false.
        /// </summary>
        public bool TryResolveConstant(RubyGlobalScope autoloadScope, string/*!*/ name, out object value) {
            ConstantStorage storage;
            var result = TryResolveConstant(autoloadScope, name, out storage);
            value = storage.Value;
            return result;
        }

        /// <summary>
        /// Get constant defined in this module.
        /// </summary>
        internal bool TryGetConstant(RubyGlobalScope autoloadScope, string/*!*/ name, out ConstantStorage value) {
            using (Context.ClassHierarchyLocker()) {
                return TryGetConstantNoLock(autoloadScope, name, out value);
            }
        }

        /// <summary>
        /// Get constant defined in this module.
        /// </summary>
        internal bool TryGetConstantNoLock(RubyGlobalScope autoloadScope, string/*!*/ name, out ConstantStorage value) {
            Context.RequiresClassHierarchyLock();
            return TryLookupConstantNoLock(false, false, autoloadScope, name, out value) != ConstantLookupResult.NotFound;
        }

        /// <summary>
        /// Get constant defined in this module or any of its ancestors. 
        /// Autoloads if autoloadScope is not null.
        /// </summary>
        /// <remarks>
        /// Thread safe.
        /// </remarks>
        internal bool TryResolveConstant(RubyGlobalScope autoloadScope, string/*!*/ name, out ConstantStorage value) {
            using (Context.ClassHierarchyLocker()) {
                return TryResolveConstantNoLock(autoloadScope, name, out value);
            }
        }        

        /// <summary>
        /// Get constant defined in this module or any of its ancestors.
        /// </summary>
        internal bool TryResolveConstantNoLock(RubyGlobalScope autoloadScope, string/*!*/ name, out ConstantStorage value) {
            Context.RequiresClassHierarchyLock();
            return TryLookupConstantNoLock(true, true, autoloadScope, name, out value) != ConstantLookupResult.NotFound;
        }

        private enum ConstantLookupResult {
            NotFound = 0,
            Found = 1,
            FoundAutoload = 2,
        }

        private ConstantLookupResult TryLookupConstantNoLock(bool included, bool inherited, RubyGlobalScope autoloadScope,
            string/*!*/ name, out ConstantStorage value) {

            Context.RequiresClassHierarchyLock();
            Debug.Assert(included || !inherited);

            value = default(ConstantStorage);
            while (true) {
                ConstantStorage result;

                RubyModule owner = included ? 
                    TryResolveConstantNoAutoloadCheck(inherited, name, out result) :
                    (TryGetConstantNoAutoloadCheck(name, out result) ? this : null);

                if (owner == null) {
                    return ConstantLookupResult.NotFound;
                }

                var autoloaded = result.Value as AutoloadedConstant;
                if (autoloaded == null) {
                    value = result;
                    return ConstantLookupResult.Found;
                }

                // The thread running the file does not see its own pending autoload at all - only
                // the value the file has assigned to the constant so far.
                if (autoloaded.IsLoadingOnCurrentThread) {
                    object pending;
                    if (autoloaded.TryGetPendingValue(out pending)) {
                        value = new ConstantStorage(pending);
                        return ConstantLookupResult.Found;
                    }
                    return ConstantLookupResult.NotFound;
                }

                // Nor is an autoload whose file has already been required: it defines nothing.
                if (autoloaded.IsSatisfied(Context.Loader) || autoloadScope == null && autoloaded.IsDead) {
                    return ConstantLookupResult.NotFound;
                }

                if (autoloadScope == null) {
                    return ConstantLookupResult.FoundAutoload;
                }

                if (autoloadScope.Context != Context) {
                    throw RubyExceptions.CreateTypeError(String.Format("Cannot autoload constants to a foreign runtime #{0}", autoloadScope.Context.RuntimeId));
                }

                // Another thread is already running the file. MRI makes this one wait for it rather
                // than load the file twice or answer with a half-built constant. Only a real
                // dereference waits: #const_defined? and #autoload? pass no autoload scope and have
                // already answered above.
                if (autoloaded.IsLoading) {
                    using (Context.ClassHierarchyUnlocker()) {
                        autoloaded.WaitForLoad();
                    }
                    continue;
                }

                string autoloadPath;
                int autoloadLine;
                bool hadLocation = owner.TryGetConstantLocation(name, out autoloadPath, out autoloadLine);

                // The autoloaded constant stays in place while the file loads. Removing it up front,
                // which is what this used to do, dropped it out of Module#constants and made every
                // other thread see it as undefined for the duration.
                // Beginning and ending a load changes what this thread can see without changing any
                // constant, and #defined? caches its answer per constant version -- so the version
                // has to move or the cached answer from before the load is reused inside it.
                bool loaded;
                autoloaded.BeginLoad();
                _context.EnterAutoload();
                try {
                    loaded = autoloaded.Load(autoloadScope);
                } catch (Exception) {
                    // MRI keeps the constant registered as an autoload when the file fails to load, so that
                    // referencing it again retries the load. A fresh AutoloadedConstant is needed because the
                    // old one already marked itself as loaded.
                    owner.SetConstantNoMutateNoLock(name, owner.CreateAutoload(autoloaded.Path, null));
                    if (hadLocation) {
                        owner.SetConstantLocationNoLock(name, autoloadPath, autoloadLine);
                    }
                    throw;
                } finally {
                    autoloaded.EndLoad();
                    _context.LeaveAutoload();
                }

                // The file is expected to have assigned the constant. If it did not, the pending
                // autoload goes away and the constant is simply undefined.
                ConstantStorage current;
                if (owner.TryGetConstantNoAutoloadCheck(name, out current) && ReferenceEquals(current.Value, autoloaded)) {
                    object removed;
                    owner.TryRemoveConstantNoLock(name, out removed);
                    if (loaded) {
                        using (Context.ClassHierarchyUnlocker()) {
                            Context.ReportWarning(String.Format("Expected {0} to define {1} but it didn't",
                                autoloaded.Path, owner.MakeNestedModuleName(name)), true);
                        }
                    }
                }

                if (!loaded) {
                    return ConstantLookupResult.NotFound;
                }
            }
        }

        // Returns the owner of the constant or null if the constant is not found.
        private RubyModule TryResolveConstantNoAutoloadCheck(bool inherited, string/*!*/ name, out ConstantStorage value) {
            Context.RequiresClassHierarchyLock();

            var storage = default(ConstantStorage);
            RubyModule owner = null;
            if (ForEachAncestor(inherited, (module) => (owner = module).TryGetConstantNoAutoloadCheck(name, out storage))) {
                value = storage;
                return owner;
            } else {
                value = storage;
                return null;
            }
        }

        // Returns the owner of the constant (this module) or null if the constant is not found.
        internal bool TryGetConstantNoAutoloadCheck(string/*!*/ name, out ConstantStorage storage) {
            Context.RequiresClassHierarchyLock();

            if (name.Length == 0) {
                storage = default(ConstantStorage);
                return false;
            }

            InitializeConstantsNoLock();
            if (_constants.TryGetValue(name, out storage)) {
                if (storage.IsRemoved) {
                    storage = default(ConstantStorage);
                    return false;
                } else {
                    return true;
                }
            }

            if (_namespaceTracker != null) {
                object value;
                // the member can live in a platform assembly nothing has loaded yet:
                if (_namespaceTracker.TryGetValue(name, out value) ||
                    _context.TryLoadNamespaceMemberAssembly(_namespaceTracker, name) && _namespaceTracker.TryGetValue(name, out value)) {
                    storage = new ConstantStorage( _context.TrackerToModule(value));
                    return true;
                }
            }

            object fieldValue;
            if (_typeTracker != null && TryGetClrConstantField(name, out fieldValue)) {
                storage = new ConstantStorage(fieldValue);
                return true;
            }

            storage = default(ConstantStorage);
            return false;
        }

        /// <summary>
        /// A CLR class's const and static readonly fields are its constants - System::Math::PI, Int32::MaxValue,
        /// String::Empty, an enum's values - as a Java class's static final fields are in JRuby. Other static members,
        /// mutable fields and properties included, are methods: Math.Max, Task.Factory, DateTime.Now.
        /// </summary>
        private bool TryGetClrConstantField(string/*!*/ name, out object value) {
            value = null;
            Type type = _typeTracker.Type;
            if ((Restrictions & ModuleRestrictions.NoNameMapping) != 0 || type.ContainsGenericParameters || !Tokenizer.IsConstantName(name)) {
                return false;
            }

            var flags = BindingFlags.Static | BindingFlags.DeclaredOnly | BindingFlags.Public |
                (_context.DomainManager.Configuration.PrivateBinding ? BindingFlags.NonPublic : 0);

            FieldInfo field = type.GetField(name, flags);
            if (field == null || !(field.IsLiteral || field.IsInitOnly)) {
                return false;
            }

            value = field.GetValue(null);
            return true;
        }

        // Direct lookup into the constant table (if it exists).
        internal bool TryGetConstantNoAutoloadNoInit(string/*!*/ name, out ConstantStorage storage) {
            Context.RequiresClassHierarchyLock();
            storage = default(ConstantStorage);
            return _constants != null && _constants.TryGetValue(name, out storage) && !storage.IsRemoved;
        }

        // thread-safe:
        public bool TryRemoveConstant(string/*!*/ name, out object value) {
            bool result;
            using (Context.ClassHierarchyLocker()) {
                result = TryRemoveConstantNoLock(name, out value);
            }

            // a top-level module was also published to the host scope (see Publish), where the
            // missing-constant fallback would still find it
            if (result && IsObjectClass && value is RubyModule) {
                RubyOps.ScopeRemoveMember(_context.TopGlobalScope, name, value);
            }
            return result;
        }

        private bool TryRemoveConstantNoLock(string/*!*/ name, out object value) {
            Context.RequiresClassHierarchyLock();

            InitializeConstantsNoLock();

            bool result;
            ConstantStorage storage;
            if (_constants.TryGetValue(name, out storage)) {
                if (storage.IsRemoved) {
                    value = null;
                    return false;
                } else {
                    value = storage.Value;
                    result = true;
                }
            } else {
                value = null;
                result = false;
            }

            object namespaceValue;
            if (_namespaceTracker != null && _namespaceTracker.TryGetValue(name, out namespaceValue)) {
                _constants[name] = ConstantStorage.Removed;
                _context.ConstantAccessVersion++;
                value = namespaceValue;
                result = true;
            } else if (result) {
                _constants.Remove(name);
                _context.ConstantAccessVersion++;
            }

            if (result) {
                RemoveConstantLocationNoLock(name);
                if (_constantEncodings != null) {
                    _constantEncodings.Remove(name);
                }
            }

            return result;
        }

        public bool EnumerateConstants(Func<RubyModule, string, object, bool>/*!*/ action) {
            Context.RequiresClassHierarchyLock();

            InitializeConstantsNoLock();

            foreach (var constant in _constants) {
                var name = constant.Key;
                var storage = constant.Value;
                if (!storage.IsRemoved && action(this, name, storage.Value)) {
                    return true;
                }
            }

            if (_namespaceTracker != null) {
                foreach (KeyValuePair<string, object> constant in _namespaceTracker) {
                    string name = constant.Key;
                    // we check if we haven't already yielded the value so that we don't yield values hidden by a user defined constant:
                    if (!_constants.ContainsKey(name) && action(this, name, constant.Value)) {
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion

        #region Methods (thread-safe)

        // not thread-safe:
        public void ForEachInstanceMethod(bool inherited, Func<RubyModule/*!*/, string/*!*/, RubyMemberInfo, bool>/*!*/ action) {
            Context.RequiresClassHierarchyLock();

            ForEachAncestor(inherited, delegate(RubyModule/*!*/ module) {
                // CLR modules are visited like any other: method lookup finds the methods libraries define on an
                // interface module, and the default interface methods, so listing them has to as well.

                // notification that we entered the module (it could have no method):
                if (action(module, null, null)) return true;

                return module.EnumerateMethods(action);
            });
        }

        // thread-safe:
        public void AddMethodAlias(string/*!*/ newName, string/*!*/ oldName) {
            RubyMemberInfo method;
            using (Context.ClassHierarchyLocker()) {
                // MRI: aliases a super-forwarder not the real method.
                method = ResolveMethodNoLock(oldName, VisibilityContext.AllVisible, MethodLookup.FallbackToObject | MethodLookup.ReturnForwarder).Info;

                // `alias b a' inside a refinement means "the a of the module being refined": the refinement
                // starts empty, so its own lookup would find nothing.
                if (method == null && _refinedModule != null) {
                    method = _refinedModule.ResolveMethodNoLock(oldName, VisibilityContext.AllVisible,
                        MethodLookup.FallbackToObject | MethodLookup.ReturnForwarder).Info;
                }

                if (method == null) {
                    throw RubyExceptions.CreateUndefinedMethodError(this, oldName);
                }

                // Alias preserves visibility and declaring module even though the alias is declared in a different module (e.g. subclass) =>
                // we can share method info (in fact, sharing is sound with Method#== semantics - it returns true on aliased methods).
                // 
                // CLR members: 
                // Detaches the member from its underlying type (by creating a copy).
                // Note: We need to copy overload group since otherwise it might mess up caching if the alias is defined in a sub-module and 
                // overloads of the same name that are not included in the overload group are inherited to this module.
                // EnumerateMethods also relies on overload groups only representing cached CLR members.
                // MRI keeps initialize/initialize_copy/initialize_clone/initialize_dup/respond_to_missing?
                // private no matter what the aliased method's visibility was.
                RubyMemberFlags flags = method.Flags;
                if (RubyUtils.IsForcedPrivateMethod(newName)) {
                    flags = (flags & ~RubyMemberFlags.VisibilityMask) | RubyMemberFlags.Private;
                }

                // The alias is its own method entry even when nothing about it differs, because
                // #owner and #original_name have to be able to tell it apart from the method it
                // aliases: MRI reports the module the alias was written in and the name the body
                // was originally given.  DeclaringModule stays put so that `super' inside the
                // body keeps resolving from where the body lives.
                var alias = method.Copy(flags, method.DeclaringModule);
                alias.AliasOwner = this;
                alias.OriginalName = method.OriginalName ?? oldName;
                SetMethodNoEventNoLock(Context, newName, alias);
            }

            MethodAdded(newName);
        }

        // Module#define_method:
        public void SetDefinedMethodNoEventNoLock(RubyContext/*!*/ callerContext, string/*!*/ name, RubyMemberInfo/*!*/ method, RubyMethodVisibility visibility,
            string sourceName = null) {
            // CLR members: Detaches the member from its underlying type (by creating a copy).
            // Note: Method#== returns false on defined methods and redefining the original method doesn't affect the new one:
            var defined = method.Copy((RubyMemberFlags)visibility, this);
            // define_method(:new_name, some_method) keeps reporting the name the body was
            // written with, exactly as an alias does
            defined.OriginalName = method.OriginalName ?? sourceName;
            SetMethodNoEventNoLock(callerContext, name, defined);
        }

        // Module#module_function/private/protected/public:
        public void SetVisibilityNoEventNoLock(RubyContext/*!*/ callerContext, string/*!*/ name, RubyMemberInfo/*!*/ method, RubyMethodVisibility visibility) {
            Context.RequiresClassHierarchyLock();

            RubyMemberInfo existing;
            bool skipHidden = false;
            if (TryGetMethod(name, ref skipHidden, out existing)) {
                // If this module defines the method itself, change the visibility of *that* definition.
                // `method' is the result of a full MRO lookup, which with Module#prepend can land on an
                // override in a prepended module; copying that one down here would make its `super' recurse.
                var redefined = (existing != null && !existing.IsUndefined) ? existing : method;

                // CLR members: Detaches the member from its underlying type (by creating a copy).
                SetMethodNoEventNoLock(callerContext, name, redefined.Copy((RubyMemberFlags)visibility, this));
            } else {
                SetMethodNoEventNoLock(callerContext, name, new SuperForwarderInfo((RubyMemberFlags)visibility, method.DeclaringModule, name));
            }
        }

        // Module#module_function:
        public void SetModuleFunctionNoEventNoLock(RubyContext/*!*/ callerContext, string/*!*/ name, RubyMemberInfo/*!*/ method) {
            Debug.Assert(!IsClass);

            // CLR members: Detaches the member from its underlying type (by creating a copy).
            // TODO: check for CLR instance members, it should be an error to call module_function on them:
            var singletonClass = GetOrCreateSingletonClass();
            var rubyMethod = method as RubyMethodInfo;
            singletonClass.SetMethodNoEventNoLock(callerContext, name, rubyMethod != null
                ? rubyMethod.CopyAsModuleFunction(RubyMemberFlags.Public, singletonClass)
                : method.Copy(RubyMemberFlags.Public, singletonClass));
        }

        // thread-safe:
        public void AddMethod(RubyContext/*!*/ callerContext, string/*!*/ name, RubyMemberInfo/*!*/ method) {
            Assert.NotNull(name, method);
            Mutate();
            SetMethodNoEvent(callerContext, name, method);
            MethodAdded(name);
        }

        // thread-safe:
        public void SetMethodNoEvent(RubyContext/*!*/ callerContext, string/*!*/ name, RubyMemberInfo/*!*/ method) {
            using (Context.ClassHierarchyLocker()) {
                SetMethodNoEventNoLock(callerContext, name, method);
            }
        }

        public void SetMethodNoEventNoLock(RubyContext/*!*/ callerContext, string/*!*/ name, RubyMemberInfo/*!*/ method) {
            Mutate();
            SetMethodNoMutateNoEventNoLock(callerContext, name, method);
        }

        internal void SetMethodNoMutateNoEventNoLock(RubyContext/*!*/ callerContext, string/*!*/ name, RubyMemberInfo/*!*/ method) {
            Context.RequiresClassHierarchyLock();
            Assert.NotNull(name, method);

            if (callerContext != _context) {
                throw RubyExceptions.CreateTypeError(String.Format("Cannot define a method on a {0} '{1}' defined in a foreign runtime #{2}",
                    IsClass ? "class" : "module", _name, _context.RuntimeId));
            }

            if (method.IsUndefined && name == Symbols.Initialize) {
                throw RubyExceptions.CreateTypeError("Cannot undefine 'initialize' method");
            }

            PrepareMethodUpdate(name, method);

            InitializeMethodsNoLock();
            _methods[name] = method;

            if (IsHelperName(name)) {
                if (_context.Loader.IsLoadingCorePrelude) {
                    (_preludeHelpers ??= new HashSet<string>()).Add(name);
                } else {
                    _preludeHelpers?.Remove(name);
                }
            }

            // A refinement is in no one's ancestors, so PrepareMethodUpdate reaches no class: the calls it
            // changes are those of the module it refines.
            if (_refinedModule != null) {
                _context.RefinedMethodChanged(_refinedModule, name);
            }
        }

        internal void AddExtensionMethodsNoLock(List<ExtensionMethodInfo>/*!*/ extensions) {
            Context.RequiresClassHierarchyLock();

            PrepareExtensionMethodsUpdate(extensions);

            if (_extensionMethods == null) {
                _extensionMethods = new Dictionary<string, List<ExtensionMethodInfo>>();
            }

            foreach (var extension in extensions) {
                List<ExtensionMethodInfo> overloads;
                if (!_extensionMethods.TryGetValue(extension.Method.Name, out overloads)) {
                    _extensionMethods.Add(extension.Method.Name, overloads = new List<ExtensionMethodInfo>());
                }

                // If the signature of the extension is the same as other overloads the overload resolver should prefer non extension method.
                overloads.Add(extension);
            }
        }

        internal virtual void PrepareExtensionMethodsUpdate(List<ExtensionMethodInfo>/*!*/ extensions) {
            if (_dependentClasses != null) {
                foreach (var cls in _dependentClasses) {
                    cls.PrepareExtensionMethodsUpdate(extensions);
                }
            }
        }

        internal virtual void PrepareMethodUpdate(string/*!*/ methodName, RubyMemberInfo/*!*/ method) {
            InitializeMethodsNoLock();

            // Prepare all classes where this module is included for a method update.
            // TODO (optimization): we might end up walking some classes multiple times, could we mark them somehow as visited?
            if (_dependentClasses != null) {
                foreach (var cls in _dependentClasses) {
                    cls.PrepareMethodUpdate(methodName, method, 0);
                }
            }
        }

        // Returns max level in which a group has been invalidated.
        internal int InvalidateGroupsInDependentClasses(string/*!*/ methodName, int maxLevel) {
            int result = -1;
            if (_dependentClasses != null) {
                foreach (var cls in _dependentClasses) {
                    result = Math.Max(result, cls.InvalidateGroupsInSubClasses(methodName, maxLevel));
                }
            }

            return result;
        }

        internal bool TryGetDefinedMethod(string/*!*/ name, out RubyMemberInfo method) {
            Context.RequiresClassHierarchyLock();
            if (_methods == null) {
                method = null;
                return false;
            }
            return _methods.TryGetValue(name, out method);
        }

        internal bool TryGetDefinedMethod(string/*!*/ name, ref bool skipHidden, out RubyMemberInfo method) {
            Context.RequiresClassHierarchyLock();

            if (TryGetDefinedMethod(name, out method)) {
                if (method.IsHidden || skipHidden && !method.IsRemovable) {
                    skipHidden = true;
                    method = null;
                    return false;
                } else {
                    return true;
                }
            }
            return false;
        }

        internal IEnumerable<KeyValuePair<string/*!*/, RubyMemberInfo/*!*/>>/*!*/ GetMethods() {
            Context.RequiresClassHierarchyLock();
            return _methods;
        }
        
        /// <summary>
        /// Direct addition to the method table. Used only for core method table operations.
        /// Do not use unless absolutely sure there is no overriding method used in a dynamic site.
        /// </summary>
        internal void AddMethodNoCacheInvalidation(string/*!*/ name, RubyMemberInfo/*!*/ method) {
            Context.RequiresClassHierarchyLock();
            Debug.Assert(_methods != null);
            _methods.Add(name, method);
        }

        /// <summary>
        /// Direct removal from the method table. Used only for core method table operations.
        /// </summary>
        internal bool RemoveMethodNoCacheInvalidation(string/*!*/ name) {
            Context.RequiresClassHierarchyLock();
            Debug.Assert(_methods != null);
            return _methods.Remove(name);
        }

        // thread-safe:
        public bool RemoveMethod(string/*!*/ name) {
            if (RemoveMethodNoEvent(name)) {
                MethodRemoved(name);
                return true;
            }
            return false;
        }

        // thread-safe:
        private bool RemoveMethodNoEvent(string/*!*/ name) {
            Mutate();
            using (Context.ClassHierarchyLocker()) {
                InitializeMethodsNoLock();

                RubyMemberInfo method;
                if (_methods.TryGetValue(name, out method)) {
                    if (method.IsHidden || method.IsUndefined) {
                        return false;
                    } else if (IsBasicObjectClass && name == Symbols.Initialize) {
                        // We prohibit removing Object#initialize to simplify object construction logic.
                        return false;
                    } else if (method.IsRemovable) {
                        // Method is used in a dynamic site or group => update version of all dependencies of this module.
                        if (method.InvalidateSitesOnOverride || method.InvalidateGroupsOnRemoval) {
                            MethodsUpdated("RemoveMethod: " + name);
                        }

                        // Method hides CLR overloads => update method groups in all dependencies of this module.
                        // TODO (opt): Do not update the entire subtree, update subtrees of all invalidated groups.
                        // TODO (opt): We can calculate max-level but it requires maintanance whenever a method is overridden 
                        //             and whenever method group is lazily created (TryGetClrMethod).
                        if (method.InvalidateGroupsOnRemoval) {
                            InvalidateGroupsInDependentClasses(name, Int32.MaxValue);
                        }

                        _methods.Remove(name);

                        if (_refinedModule != null) {
                            _context.RefinedMethodChanged(_refinedModule, name);
                        }
                    } else {
                        SetMethodNoEventNoLock(Context, name, RubyMemberInfo.HiddenMethod);
                    }
                    return true;
                } else if (TryGetClrMember(name, false, out method)) {
                    Debug.Assert(!method.IsRemovable);
                    SetMethodNoEventNoLock(Context, name, RubyMemberInfo.HiddenMethod);
                    return true;
                } else {
                    return false;
                }
            }
        }

        // thread-safe:
        public void UndefineMethod(string/*!*/ name) {
            UndefineMethodNoEvent(name);
            MethodUndefined(name);
        }

        // thread-safe:
        public void UndefineMethodNoEvent(string/*!*/ name) {
            SetMethodNoEvent(Context, name, RubyMethodInfo.UndefinedMethod);
        }

        // thread-safe:
        public void HideMethod(string/*!*/ name) {
            SetMethodNoEvent(Context, name, RubyMethodInfo.HiddenMethod);
        }

        // thread-safe:
        public MethodResolutionResult ResolveMethod(string/*!*/ name, VisibilityContext visibility) {
            // The all-visible lookup of a class is what reflection and the runtime's own protocol checks
            // ask for (method_defined?, respond_to_missing?, _dump, ...): answer it from the class's cache.
            if (IsClass && visibility.Class == null && visibility.Visible == RubyMethodAttributes.VisibilityMask) {
                return GetMethodLookup(name).Result;
            }

            using (Context.ClassHierarchyLocker()) {
                return ResolveMethodNoLock(name, visibility);
            }
        }

        /// <summary>
        /// What a call of <paramref name="name"/> written where <paramref name="refinements"/> are active resolves
        /// to, for code that binds such a call itself (the method JIT).  Not registered for invalidation the way a
        /// call site's resolution is: the caller guards on RubyModule.GlobalMethodVersion.
        /// </summary>
        internal MethodResolutionResult ResolveMethod(string/*!*/ name, VisibilityContext visibility, RefinementActivation refinements) {
            if (refinements == null || refinements.IsEmpty) {
                return ResolveMethod(name, visibility);
            }
            using (Context.ClassHierarchyLocker()) {
                return ResolveMethodNoLock(name, visibility, MethodLookup.Default, refinements);
            }
        }

        public MethodResolutionResult ResolveMethodForSiteNoLock(string/*!*/ name, VisibilityContext visibility) {
            return ResolveMethodForSiteNoLock(name, visibility, MethodLookup.Default);
        }

        internal MethodResolutionResult ResolveMethodForSiteNoLock(string/*!*/ name, VisibilityContext visibility, MethodLookup options) {
            return ResolveMethodNoLock(name, visibility, options).InvalidateSitesOnOverride();
        }

        /// <summary>
        /// Method resolution as of a lexical position, so that reflection done where a `using' is
        /// in effect sees what a call from there would - Module#instance_method does.
        /// </summary>
        public MethodResolutionResult ResolveMethodWithRefinements(string/*!*/ name, VisibilityContext visibility, RubyScope scope) {
            using (Context.ClassHierarchyLocker()) {
                var result = ResolveMethodNoLock(name, visibility, MethodLookup.ReturnForwarder,
                    (scope != null) ? scope.GetActiveRefinements() : null).InvalidateSitesOnOverride();

                // `public :m' in a module that inherits m leaves an entry there that only forwards
                // to the inherited body. MRI's Method made out of it reports that module as its
                // #owner and the forwarder's visibility, so hand back a copy of the body that says so.
                if (result.Found && result.Info.IsSuperForwarder) {
                    var forwarder = (SuperForwarderInfo)result.Info;
                    var target = result.Owner.ResolveSuperMethodNoLock(forwarder.SuperName, result.Owner).InvalidateSitesOnOverride();
                    if (!target.Found) {
                        return target;
                    }

                    var copy = target.Info.Copy(forwarder.Flags, target.Info.DeclaringModule);
                    copy.AliasOwner = forwarder.AliasOwner ?? result.Owner;
                    copy.OriginalName = forwarder.OriginalName ?? target.Info.OriginalName;
                    return new MethodResolutionResult(copy, result.Owner, true);
                }

                return result;
            }
        }

        /// <summary>The module whose `refine' call made this refinement, or null for anything else.</summary>
        public RubyModule RefinementHolder {
            get { return _refinementHolder; }
        }

        public MethodResolutionResult ResolveMethodNoLock(string/*!*/ name, VisibilityContext visibility) {
            return ResolveMethodNoLock(name, visibility, MethodLookup.Default);
        }

        public MethodResolutionResult ResolveMethodNoLock(string/*!*/ name, VisibilityContext visibility, MethodLookup options) {
            return ResolveMethodNoLock(name, visibility, options, null);
        }

        /// <summary>
        /// <paramref name="refinements"/> is the refinement activation of the *caller's lexical scope*, or
        /// null for a lookup that is not on behalf of a lexical position (reflection, send, protocol calls).
        /// A refinement of module M is searched immediately ahead of M itself, which is the only place the
        /// MRO changes; nothing is spliced into _prepends/_mixins, so the ancestors a program can observe
        /// stay exactly what CRuby reports.
        /// </summary>
        internal MethodResolutionResult ResolveMethodNoLock(string/*!*/ name, VisibilityContext visibility, MethodLookup options,
            RefinementActivation refinements) {

            Context.RequiresClassHierarchyLock();
            Assert.NotNull(name);

            InitializeMethodsNoLock();
            RubyMemberInfo info = null;
            RubyModule owner = null;
            bool skipHidden = false;
            bool foundCallerSelf = false;
            MethodResolutionResult result;

            // The CLR extension methods `using'd where the call is made, and whether the search passed a module they
            // extend before it found the method:
            var scopedExtensions = (refinements != null && IsClass && (options & MethodLookup.Virtual) == 0) ?
                refinements.Extensions : null;
            bool passedScopedExtensions = false;

            // Without refinements, the walk without a closure: this is the lookup every call site's binding does,
            // and the closure's captured locals were allocated on every call (which is also why the walk with
            // refinements is a method of its own). Scoped CLR extensions come only with a `using', so only with
            // refinements active.
            bool found = (refinements != null && !refinements.IsEmpty) ?
                FindMethodWithRefinementsNoLock(name, options, visibility, refinements, scopedExtensions, ref passedScopedExtensions,
                    ref skipHidden, ref foundCallerSelf, out info, out owner) :
                FindMethodInAncestorsNoLock(name, (options & MethodLookup.Virtual) != 0, visibility.Class, ref skipHidden, ref foundCallerSelf, out info, out owner);

            if (found) {
                if (info == null || info.IsUndefined) {
                    result = MethodResolutionResult.NotFound;
                } else if (!IsMethodVisible(info, owner, visibility, foundCallerSelf)) {
                    result = new MethodResolutionResult(info, owner, false);
                } else if (info.IsSuperForwarder) {
                    if ((options & MethodLookup.ReturnForwarder) != 0) {
                        result = new MethodResolutionResult(info, owner, true);
                    } else {
                        // start again with owner's super ancestor and ignore visibility:
                        result = owner.ResolveSuperMethodNoLock(((SuperForwarderInfo)info).SuperName, owner);
                    }
                } else {
                    result = new MethodResolutionResult(info, owner, true);
                }
            } else {
                result = MethodResolutionResult.NotFound;
            }

            // TODO: BasicObject
            // Note: all classes include Object in ancestors, so we don't need to search it again:
            if (!result.Found && (options & MethodLookup.FallbackToObject) != 0 && !IsClass) {
                return _context.ObjectClass.ResolveMethodNoLock(name, visibility, options & ~MethodLookup.FallbackToObject, refinements);
            }

            // The CLR extension methods `using'd where the call is made are where using_clr_extensions puts them for
            // everyone, which is where a refinement of the type they extend would be: a CLR method group anywhere gets
            // them as more overloads (List#count; an exact tie goes to the instance member), and they shadow a Ruby method
            // the type picks up after it (IEnumerable[T]'s select ahead of Enumerable#select) - but not one ahead of it
            // (Array#select: an Array's ancestors don't include IEnumerable[T]).
            if (scopedExtensions != null) {
                if (result.Found && result.Info is RubyMethodGroupInfo) {
                    return AddScopedExtensionsNoLock(name, result, refinements, scopedExtensions);
                }
                if (passedScopedExtensions) {
                    var extended = AddScopedExtensionsNoLock(name, MethodResolutionResult.NotFound, refinements, scopedExtensions);
                    if (extended.Found) {
                        // a site bound to them must re-bind if what they shadow changes, as a definition ahead of them would
                        result.InvalidateSitesOnOverride();
                    }
                    return extended;
                }
            }

            return result;
        }

        private bool FindMethodWithRefinementsNoLock(string/*!*/ name, MethodLookup options, VisibilityContext visibility,
            RefinementActivation/*!*/ refinements, RefinementActivation.ClrExtensionScope scopedExtensions, ref bool passedScopedExtensionsRef,
            ref bool skipHiddenRef, ref bool foundCallerSelfRef, out RubyMemberInfo infoOut, out RubyModule ownerOut) {

            RubyMemberInfo info = null;
            RubyModule owner = null;
            bool skipHidden = skipHiddenRef;
            bool foundCallerSelf = foundCallerSelfRef;
            bool passedScopedExtensions = passedScopedExtensionsRef;
            var refinementBuffer = new List<RubyModule>();

            bool found = ForEachAncestor((module) => {
                refinementBuffer.Clear();
                refinements.GetRefinementsOf(module, refinementBuffer);
                foreach (RubyModule refinement in refinementBuffer) {
                    // a refinement is not in anyone's ancestors, so nothing else initializes it
                    refinement.InitializeMethodsNoLock();
                    owner = refinement;
                    if (refinement.TryGetMethod(name, ref skipHidden, (options & MethodLookup.Virtual) != 0, out info)) {
                        return true;
                    }
                }
                owner = module;
                foundCallerSelf |= module == visibility.Class;
                if (module.TryGetMethod(name, ref skipHidden, (options & MethodLookup.Virtual) != 0, out info)) {
                    return true;
                }
                passedScopedExtensions |= scopedExtensions != null && module.HasScopedExtensionMethodsNoLock(name, scopedExtensions);
                return false;
            });

            skipHiddenRef = skipHidden;
            foundCallerSelfRef = foundCallerSelf;
            passedScopedExtensionsRef = passedScopedExtensions;
            infoOut = info;
            ownerOut = owner;
            return found;
        }

        /// <summary>
        /// What ResolveMethodNoLock's ForEachAncestor walk does when no refinement is active: the ancestors in method
        /// resolution order (see ForEachAncestor and RubyClass.ForEachAncestor) until one defines the method, noting
        /// whether the caller's class was passed on the way.
        /// </summary>
        private bool FindMethodInAncestorsNoLock(string/*!*/ name, bool virtualLookup, RubyModule callerClass,
            ref bool skipHidden, ref bool foundCallerSelf, out RubyMemberInfo info, out RubyModule owner) {

            info = null;
            owner = null;
            if (IsClass) {
                for (RubyClass c = (RubyClass)this; c != null; c = c.SuperClass) {
                    if (c.FindMethodInDeclaredAncestorsNoLock(name, virtualLookup, callerClass, ref skipHidden, ref foundCallerSelf, ref info, ref owner)) {
                        return true;
                    }
                }
                return false;
            }
            return FindMethodInDeclaredAncestorsNoLock(name, virtualLookup, callerClass, ref skipHidden, ref foundCallerSelf, ref info, ref owner);
        }

        private bool FindMethodInDeclaredAncestorsNoLock(string/*!*/ name, bool virtualLookup, RubyModule callerClass,
            ref bool skipHidden, ref bool foundCallerSelf, ref RubyMemberInfo info, ref RubyModule owner) {

            foreach (RubyModule p in _prepends) {
                owner = p;
                foundCallerSelf |= p == callerClass;
                if (p.TryGetMethod(name, ref skipHidden, virtualLookup, out info)) return true;
            }

            owner = this;
            foundCallerSelf |= this == callerClass;
            if (TryGetMethod(name, ref skipHidden, virtualLookup, out info)) return true;

            foreach (RubyModule m in _mixins) {
                owner = m;
                foundCallerSelf |= m == callerClass;
                if (m.TryGetMethod(name, ref skipHidden, virtualLookup, out info)) return true;
            }
            return false;
        }

        // Extension methods have the places using_clr_extensions gives them, the CLR method groups of classes backed by a CLR
        // type: a class's place has its own and those of the interfaces it implements (a Dictionary's select is LINQ's,
        // not the Ruby one its IDictionary picks up ahead of IEnumerable[T]). An interface module, a Ruby class (one that
        // includes IEnumerable[Object] too) and a singleton class have none of their own.
        private bool IsScopedExtensionPlace {
            get { return IsClass && !IsSingletonClass && _typeTracker != null; }
        }

        private bool HasScopedExtensionMethodsNoLock(string/*!*/ name, RefinementActivation.ClrExtensionScope/*!*/ extensions) {
            if (!IsScopedExtensionPlace) {
                return false;
            }
            if (HasOwnScopedExtensionMethodsNoLock(name, extensions)) {
                return true;
            }
            foreach (var mixin in Mixins) {
                if (mixin.HasOwnScopedExtensionMethodsNoLock(name, extensions)) {
                    return true;
                }
            }
            return false;
        }

        private bool HasOwnScopedExtensionMethodsNoLock(string/*!*/ name, RefinementActivation.ClrExtensionScope/*!*/ extensions) {
            List<ExtensionMethodInfo> candidates;
            if (_scopedExtensionMethods != null && _scopedExtensionMethods.TryGetValue(name, out candidates)) {
                foreach (var extension in candidates) {
                    if (extensions.Contains(extension.Method)) {
                        return true;
                    }
                }
            }
            return false;
        }

        private MethodResolutionResult AddScopedExtensionsNoLock(string/*!*/ name, MethodResolutionResult result,
            RefinementActivation/*!*/ refinements, RefinementActivation.ClrExtensionScope/*!*/ extensions) {

            int version = Version.Method;
            if (_scopedExtensionGroups == null || _scopedExtensionGroupsVersion != version) {
                _scopedExtensionGroups = new Dictionary<(string, RefinementActivation, object), RubyMemberInfo>();
                _scopedExtensionGroupsVersion = version;
            }

            var baseGroup = result.Found ? (RubyMethodGroupInfo)result.Info : null;
            var key = (name, refinements, (object)baseGroup ?? NoBaseGroup);
            RubyMemberInfo group;
            if (!_scopedExtensionGroups.TryGetValue(key, out group)) {
                var present = new HashSet<MethodBase>();
                var overloads = new List<OverloadInfo>();
                if (baseGroup != null) {
                    foreach (var overload in baseGroup.MethodBases) {
                        overloads.Add(overload);
                        if (overload.ReflectionInfo != null) {
                            present.Add(overload.ReflectionInfo);
                        }
                    }
                }

                int baseCount = overloads.Count;
                Action<RubyModule> addExtensions = (module) => {
                    List<ExtensionMethodInfo> candidates;
                    if (module._scopedExtensionMethods != null && module._scopedExtensionMethods.TryGetValue(name, out candidates)) {
                        foreach (var extension in candidates) {
                            // (an extension activated by using_clr_extensions as well is in the group already)
                            if (extensions.Contains(extension.Method) && present.Add(extension.Method)) {
                                overloads.Add(new ReflectionOverloadInfo(extension.Method));
                            }
                        }
                    }
                };
                ForEachAncestor((module) => {
                    if (module.IsScopedExtensionPlace) {
                        addExtensions(module);
                        foreach (var mixin in module.Mixins) {
                            addExtensions(mixin);
                        }
                    }
                    return false;
                });

                group = (overloads.Count > baseCount) ? new RubyMethodGroupInfo(overloads.ToArray(), this, false) : null;
                _scopedExtensionGroups.Add(key, group);
            }

            if (group == null) {
                return result;
            }

            // A site bound to the extended group must re-bind when what it extends (or, for a name that found
            // no group, a method of the name defined anywhere) would change the call - as for the group itself.
            if (result.Found) {
                result.InvalidateSitesOnOverride();
            } else {
                result.InvalidateSitesOnMissingMethodAddition(name, Context);
            }
            return new MethodResolutionResult(group, this, true);
        }

        /// <summary>
        /// True if `using <paramref name="module"/>' brings in CLR extension methods: <paramref name="module"/> is a
        /// CLR namespace (C#'s `using System.Linq;'), whose name is returned in <paramref name="ns"/>, or a CLR
        /// static class (C#'s `using static'), returned in <paramref name="staticClass"/>.
        /// </summary>
        public static bool IsClrExtensionSource(RubyModule/*!*/ module, out string ns, out Type staticClass) {
            if (module.NamespaceTracker != null) {
                ns = module.NamespaceTracker.Name;
                staticClass = null;
                return true;
            }

            ns = null;
            staticClass = null;
            Type type;
            if (module.IsClass && module.TypeTracker != null && (type = module.TypeTracker.Type).IsAbstract && type.IsSealed &&
                !type.IsGenericTypeDefinition) {
                staticClass = type;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Adds extension methods of this type from a namespace some `using' has named: the calls made where the
        /// namespace (or the methods' static class) is used can see them from now on.
        /// </summary>
        internal void AddScopedExtensionMethodsNoLock(List<ExtensionMethodInfo>/*!*/ extensions) {
            Context.RequiresClassHierarchyLock();

            if (_scopedExtensionMethods == null) {
                _scopedExtensionMethods = new Dictionary<string, List<ExtensionMethodInfo>>();
            }

            // Filed under the CLR name and the Ruby one (Where: where; ToJSON: to_json), the names a call can use.
            var names = new HashSet<string>();
            foreach (var extension in extensions) {
                string clrName = extension.Method.Name;
                AddScopedExtensionMethod(clrName, extension, names);

                string mangled = RubyUtils.TryMangleName(clrName);
                if (mangled != null && mangled != clrName) {
                    AddScopedExtensionMethod(mangled, extension, names);
                }
            }

            Context.LexicalMethodsAdded(this, names, "ScopedExtensions");
        }

        private void AddScopedExtensionMethod(string/*!*/ name, ExtensionMethodInfo extension, HashSet<string>/*!*/ names) {
            List<ExtensionMethodInfo> overloads;
            if (!_scopedExtensionMethods.TryGetValue(name, out overloads)) {
                _scopedExtensionMethods.Add(name, overloads = new List<ExtensionMethodInfo>());
            }
            if (!overloads.Contains(extension)) {
                overloads.Add(extension);
                names.Add(name);
            }
        }

        private bool IsMethodVisible(RubyMemberInfo/*!*/ method, RubyModule/*!*/ owner, VisibilityContext visibility, bool foundCallerSelf) {
            // Visibility not constrained by a class:
            // - call with implicit self => all methods are visible.
            // - interop call => only public methods are visible.
            if (visibility.Class == null) {
                return visibility.IsVisible(method.Visibility);
            } 
            
            if (method.Visibility == RubyMethodVisibility.Protected) {
                // A protected method is visible if the caller's self immediate class is a descendant of the method owner.
                if (foundCallerSelf) {
                    return true;
                }
                return IsProtectedMethodVisible(owner, visibility.Class);
            } 

            return method.Visibility == RubyMethodVisibility.Public;
        }

        // A method of its own so that the closure's captured parameter is not allocated on every visibility check.
        private bool IsProtectedMethodVisible(RubyModule/*!*/ owner, RubyModule/*!*/ callerClass) {
            // walk ancestors from caller's self class (visibilityContext)
            // until the method owner is found or this module is found (this module is a descendant of the owner):
            return callerClass.ForEachAncestor((module) => module == owner || module == this);
        }

        // skip one method in the method resolution order (MRO)
        public MethodResolutionResult ResolveSuperMethodNoLock(string/*!*/ name, RubyModule/*!*/ callerModule) {
            Context.RequiresClassHierarchyLock();
            Assert.NotNull(name, callerModule);

            InitializeMethodsNoLock();

            RubyMemberInfo info = null;
            RubyModule owner = null;
            bool foundModule = false;
            bool skipHidden = false;

            // `super' from inside a refinement: the refinement behaves as if it were prepended to the
            // module it refines, so super continues at that module *inclusive* rather than skipping it.
            RubyModule refined = callerModule.RefinedModule;
            if (refined != null) {
                InitializeMethodsNoLock();
                if (ForEachAncestor((module) => {
                    foundModule |= module == refined;
                    if (!foundModule) {
                        return false;
                    }
                    owner = module;
                    return module.TryGetMethod(name, ref skipHidden, out info) && !info.IsSuperForwarder;
                }) && info != null && !info.IsUndefined) {
                    return new MethodResolutionResult(info, owner, true);
                }
                return MethodResolutionResult.NotFound;
            }

            // start searching for the method in the MRO parent of the declaringModule:
            if (ForEachAncestor((module) => {
                if (module == callerModule) {
                    foundModule = true;
                    return false;
                }

                owner = module;
                return foundModule && module.TryGetMethod(name, ref skipHidden, out info) && !info.IsSuperForwarder;
            }) && !info.IsUndefined) {
                return new MethodResolutionResult(info, owner, true);
            }

            return MethodResolutionResult.NotFound;
        }

        // thread-safe:
        public RubyMemberInfo GetMethod(string/*!*/ name) {
            ContractUtils.RequiresNotNull(name, "name");
            using (Context.ClassHierarchyLocker()) {
                InitializeMethodsNoLock();

                RubyMemberInfo method;
                bool skipHidden = false;
                TryGetMethod(name, ref skipHidden, out method);
                return method;
            }
        }

        internal bool TryGetMethod(string/*!*/ name, ref bool skipHidden, out RubyMemberInfo method) {
            return TryGetMethod(name, ref skipHidden, false, out method);
        }

        internal bool TryGetMethod(string/*!*/ name, ref bool skipHidden, bool virtualLookup, out RubyMemberInfo method) {
            Context.RequiresClassHierarchyLock();
            Debug.Assert(_methods != null);

            // lookup Ruby method first:    
            if (TryGetDefinedMethod(name, ref skipHidden, out method)) {
                return true;
            }

            if (virtualLookup) {
                string mangled;
                // Note: property and default indexers getters and setters use FooBar, FooBar= and [], []= names, respectively, in virtual sites:
                if ((mangled = RubyUtils.TryMangleMethodName(name)) != null && TryGetDefinedMethod(mangled, ref skipHidden, out method)
                    && method.IsRubyMember) {
                    return true;
                }

                // Special mappings:
                // Do not map to Kernel#hash/eql?/to_s to prevent recursion in case Object.GetHashCode/Equals/ToString is removed.
                if (this != Context.KernelModule) {
                    if (name == "GetHashCode" && TryGetDefinedMethod("hash", out method) && method.IsRubyMember) {
                        return true;
                    } else if (name == "Equals" && TryGetDefinedMethod("eql?", out method) && method.IsRubyMember) {
                        return true;
                    } if (name == "ToString" && TryGetDefinedMethod("to_s", out method) && method.IsRubyMember) {
                        return true;
                    }
                }

                // IComparable.CompareTo and IEnumerable.GetEnumerator of a Ruby class that defines <=> or mixes in Enumerable
                // (see GetImpliedClrInterface). Only a <=> written in Ruby: a library one (System::IComparable's) calls CompareTo.
                if (name == "CompareTo" && TryGetDefinedMethod("<=>", out method) && method.IsRubyMember && !(method is RubyLibraryMethodInfo)) {
                    return true;
                }
                if (name == "GetEnumerator" && GetImpliedClrInterface() == typeof(System.Collections.IEnumerable) && TryGetDefinedMethod("to_a", out method)) {
                    return true;
                }
            }

            return !skipHidden && TryGetClrMember(name, virtualLookup, out method);
        }

        private bool TryGetClrMember(string/*!*/ name, bool virtualLookup, out RubyMemberInfo method) {
            bool timed = PhaseTimer.Enter(PhaseTimer.ClrMember);
            try {
                return TryGetClrMemberNoTiming(name, virtualLookup, out method);
            } finally {
                PhaseTimer.Exit(timed);
            }
        }

        private bool TryGetClrMemberNoTiming(string/*!*/ name, bool virtualLookup, out RubyMemberInfo method) {
            // Skip hidden CLR overloads.
            // An interface exposes only its default methods, a generic type definition nothing (see GetDefaultInterfaceMethods).
            if (_typeTracker != null) {
                // Note: Do not allow mangling for CLR virtual lookups - we want to match the overridden name exactly as is, 
                // so that it corresponds to the base method call the override stub performs.
                bool mapNames = (Restrictions & ModuleRestrictions.NoNameMapping) == 0;
                bool unmangleNames = !virtualLookup && (mapNames || IsUnmangledBuiltin());

                if (IsModuleType(_typeTracker.Type) ?
                    TryGetDefaultInterfaceMember(name, unmangleNames, out method) :
                    TryGetClrMember(_typeTracker.Type, name, mapNames, unmangleNames, out method)) {
                    _methods.Add(name, method);
                    return true;
                }
            }

            method = null;
            return false;
        }

        /// <summary>
        /// Integer and Float map no names (<see cref="ModuleRestrictions.NoNameMapping"/>), yet the CLR instance methods of
        /// their Int32 and Double take snake_case names as any CLR type's do: 5.to_string("X"), 1.5.compare_to(2.0). Such a
        /// member is still left out of the method lists (<see cref="IsUnlisted"/>), and an operator or indexer is not mapped.
        /// </summary>
        private bool IsUnmangledBuiltin() {
            return _typeTracker.Type.IsPrimitive && this is RubyClass cls && !cls.IsSingletonClass;
        }

        // The public instance members of an interface and its base interfaces that have a default implementation (C# 8),
        // or an empty array.
        private MethodInfo[] _defaultInterfaceMethods;

        private MethodInfo/*!*/[]/*!*/ GetDefaultInterfaceMethods() {
            if (_defaultInterfaceMethods == null) {
                Type type = _typeTracker.Type;
                var result = new List<MethodInfo>();
                // A generic definition's methods cannot be called, only those of its instantiations. The base interfaces
                // are searched too: an interface module does not include the modules of the interfaces it extends.
                if (type.IsInterface && !type.ContainsGenericParameters) {
                    AddDefaultInterfaceMethods(result, type);
                    foreach (Type baseInterface in type.GetInterfaces()) {
                        AddDefaultInterfaceMethods(result, baseInterface);
                    }
                }
                _defaultInterfaceMethods = result.ToArray();
            }
            return _defaultInterfaceMethods;
        }

        private static void AddDefaultInterfaceMethods(List<MethodInfo>/*!*/ result, Type/*!*/ interfaceType) {
            foreach (MethodInfo method in interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                if (method.IsAbstract || method.ContainsGenericParameters) {
                    continue;
                }

                // a member that hides one of the same signature in a base interface wins over it:
                int hidden = result.FindIndex((existing) => existing.Name == method.Name && 
                    ReflectionUtils.GetParameterTypes(existing.GetParameters()).ValueEquals(ReflectionUtils.GetParameterTypes(method.GetParameters())));

                if (hidden < 0) {
                    result.Add(method);
                } else if (result[hidden].DeclaringType.IsAssignableFrom(method.DeclaringType)) {
                    result[hidden] = method;
                }
            }
        }

        /// <summary>
        /// Abstract interface members are metadata: an object exposes its implementation of them through its class.
        /// A default interface method has a body of its own, which is all a class implementing the interface without
        /// overriding the member has (and all a Ruby class including it has), so it is callable from the interface module.
        /// The call dispatches through the interface: an implementation, if there is one, still wins.
        /// </summary>
        private bool TryGetDefaultInterfaceMember(string/*!*/ name, bool unmangleNames, out RubyMemberInfo method) {
            method = null;
            var defaults = GetDefaultInterfaceMethods();
            if (defaults.Length == 0 || name.Length == 0) {
                return false;
            }

            // a method, a property getter or a property setter:
            bool isSetter = name.Length > 1 && name.LastCharacter() == '=';
            string clrName = isSetter ? name.Substring(0, name.Length - 1) : name;
            string altName = unmangleNames ? RubyUtils.TryUnmangleMethodName(clrName) : null;

            List<OverloadInfo> overloads = null;
            foreach (var dim in defaults) {
                string dimName = dim.Name;
                if (dim.IsSpecialName) {
                    if (!dimName.StartsWith(isSetter ? "set_" : "get_", StringComparison.Ordinal)) {
                        continue;
                    }
                    dimName = dimName.Substring(4);
                } else if (isSetter) {
                    continue;
                }

                // the name the member is listed under (see RubyClass.GetIrregularlyMangledName):
                if (dimName == clrName || dimName == altName ||
                    unmangleNames && RubyUtils.HasMangledName(dimName) && RubyUtils.TryMangleName(dimName) == clrName) {
                    if (overloads == null) {
                        overloads = new List<OverloadInfo>();
                    }
                    overloads.Add(new ReflectionOverloadInfo(dim));
                }
            }

            if (overloads == null) {
                return false;
            }

            // an attached CLR member: virtual sites forward to the default body rather than call it through the interface
            method = new RubyOverloadGroupInfo(overloads.ToArray(), this, null, false);
            return true;
        }

        internal IEnumerable<string/*!*/>/*!*/ EnumerateDefaultInterfaceMembers() {
            foreach (var dim in GetDefaultInterfaceMethods()) {
                if (!dim.IsSpecialName) {
                    yield return dim.Name;
                } else if (dim.Name.StartsWith("get_", StringComparison.Ordinal)) {
                    yield return dim.Name.Substring(4);
                } else if (dim.Name.StartsWith("set_", StringComparison.Ordinal)) {
                    yield return dim.Name.Substring(4) + "=";
                }
            }
        }

        protected virtual bool TryGetClrMember(Type/*!*/ type, string/*!*/ name, bool mapNames, bool unmangleNames, out RubyMemberInfo method) {
            method = null;
            return false;
        }

        public bool EnumerateMethods(Func<RubyModule, string, RubyMemberInfo, bool>/*!*/ action) {
            Context.RequiresClassHierarchyLock();

            InitializeMethodsNoLock();

            foreach (KeyValuePair<string, RubyMemberInfo> method in _methods) {
                // Exclude attached CLR members as they only represent cached CLR method calls and these methods are enumerated below.
                // Include undefined and CLR hidden members - the action uses them to hide the names.
                if (method.Value.IsRubyMember) {
                    if (action(this, method.Key, method.Value)) {
                        return true;
                    }
                }
            }

            // CLR members (do not include abstract interface members - they are not callable methods, just metadata):
            if (_typeTracker != null) {
                foreach (string name in IsModuleType(_typeTracker.Type) ? EnumerateDefaultInterfaceMembers() : EnumerateClrMembers(_typeTracker.Type)) {
                    if (action(this, name, RubyMemberInfo.InteropMember)) {
                        return true;
                    }
                }
            }

            return false;
        }

        protected virtual IEnumerable<string/*!*/>/*!*/ EnumerateClrMembers(Type/*!*/ type) {
            return ArrayUtils.EmptyStrings;
        }

        /// <summary>
        /// inherited == false, attributes &amp; attr == Instance:
        ///   - get methods in the "self" module
        ///   - also include methods on singleton ancestor classes until a non-singleton class is reached
        /// inherited == false, attributes &amp; attr == Singleton:
        ///   - get methods only in the "self" module if it's a singleton class
        ///   - do not visit mixins nor super classes 
        /// inherited == true, attributes &amp; attr == Singleton:
        ///   - walk all ancestors until a non-singleton class is reached (do not include non-singleton's methods)
        /// inherited == true, attributes &amp; attr == None:
        ///   - walk all ancestors until an Object is reached
        /// 
        /// Methods are filtered by visibility specified in attributes (mutliple visibilities could be specified).
        /// A name undefined in a module is not visible in that module and its ancestors.
        /// Method names are not duplicated in the result.
        /// </summary>
        /// <remarks>
        /// Not thread safe.
        /// </remarks>
        public void ForEachMember(bool inherited, RubyMethodAttributes attributes, IEnumerable<string> foreignMembers, 
            Action<string/*!*/, RubyModule/*!*/, RubyMemberInfo/*!*/>/*!*/ action) {
            ForEachMember(inherited, attributes, foreignMembers, false, action);
        }

        /// <param name="objectMethods">
        /// Kernel#public_methods(false) and its private and protected siblings, as against
        /// Module#instance_methods(false): MRI lists the object's singleton class, whatever is
        /// mixed into it, and the object's class itself - but not modules prepended to that
        /// class, nor anything above it.
        /// </param>
        public void ForEachMember(bool inherited, RubyMethodAttributes attributes, IEnumerable<string> foreignMembers, bool objectMethods,
            Action<string/*!*/, RubyModule/*!*/, RubyMemberInfo/*!*/>/*!*/ action) {

            Context.RequiresClassHierarchyLock();

            var visited = new Dictionary<string, RubyMemberInfo>();

            // We can look for instance methods, singleton methods or all methods.
            // The difference is when we stop searching.
            bool instanceMethods = (attributes & RubyMethodAttributes.Instance) != 0;
            bool singletonMethods = (attributes & RubyMethodAttributes.Singleton) != 0;

            // TODO: if we allow creating singletons for foreign objects we need to change this:
            if (foreignMembers != null) {
                foreach (var name in foreignMembers) {
                    action(name, this, RubyMethodInfo.InteropMember);
                    visited.Add(name, RubyMethodInfo.InteropMember);
                }
            }

            // #singleton_methods reaches only what belongs to the object: its singleton class,
            // whatever is mixed into that, and the same for its class's singletons. A module
            // mixed into a real class in the chain belongs to that class - `Module.prepend M'
            // does not put M's methods on every module's singleton. Those have to be gathered up
            // front, because a prepended module is visited before the class that prepended it.
            HashSet<RubyModule> classOwned = null;
            if (singletonMethods) {
                classOwned = new HashSet<RubyModule>();
                for (RubyClass c = this as RubyClass; c != null; c = c.SuperClass) {
                    if (!c.IsSingletonClass) {
                        c.ForEachDeclaredAncestor((m) => { classOwned.Add(m); return false; });
                    }
                }
            }

            // The modules prepended to the object's class, which Kernel#public_methods(false) does
            // not list although they precede the class in the walk: everything the first class
            // that is not a singleton declares as an ancestor (its included modules come after it,
            // where the walk has stopped).
            HashSet<RubyModule> firstClassOwned = null;
            if (instanceMethods && !inherited && objectMethods) {
                for (RubyClass c = this as RubyClass; c != null; c = c.SuperClass) {
                    if (!c.IsSingletonClass) {
                        firstClassOwned = new HashSet<RubyModule>();
                        RubyClass owner = c;
                        c.ForEachDeclaredAncestor((m) => { if (!ReferenceEquals(m, owner)) firstClassOwned.Add(m); return false; });
                        break;
                    }
                }
            }

            bool stop = false;
            bool seenSelf = false;
            ForEachInstanceMethod(true, delegate(RubyModule/*!*/ module, string name, RubyMemberInfo member) {

                if (member == null) {
                    // notification received before any method of the module

                    if (stop) {
                        return true;
                    }

                    if (instanceMethods) {
                        // instance_methods(false) lists what the module itself defines: not what
                        // a module prepended to it defines (those come first in the walk), and
                        // for a singleton class not the class behind it.
                        if (!inherited) {
                            if (objectMethods) {
                                // through the singleton classes, up to and including the object's class
                                stop = module.IsClass && !module.IsSingletonClass;
                            } else {
                                if (seenSelf) {
                                    return true;
                                }
                                seenSelf = ReferenceEquals(module, this);
                            }
                        }
                    } else if (singletonMethods) {
                        if (!inherited && module != this || classOwned.Contains(module)) {
                            return true;
                        }
                    } else {
                        stop = !inherited;
                    }

                } else if (!visited.ContainsKey(name)) {
                    if (instanceMethods && !inherited && (objectMethods ? firstClassOwned != null && firstClassOwned.Contains(module) : !ReferenceEquals(module, this))) {
                        // a prepended module's member: not this module's, and it must not hide
                        // the one this module defines under the same name
                        return false;
                    }

                    // yield the member only if it has the right visibility:
                    if (!member.IsUndefined && !member.IsHidden && (((RubyMethodAttributes)member.Visibility & attributes) != 0)) {
                        action(name, module, member);
                    }

                    // visit the member even if it doesn't have the right visibility so that any overridden member with the right visibility
                    // won't later be visited:
                    visited.Add(name, member);
                }

                return false;
            });
        }

        /// <summary>
        /// Whether Module#instance_methods, Kernel#methods and the rest of that family leave out a member <paramref name="owner"/>
        /// has - one that stays callable, but that MRI has no counterpart of, so that the lists come out as MRI's:
        /// <list type="bullet">
        /// <item>A CLR member of the type behind a Ruby built-in (a module that maps no names, see
        /// <see cref="ModuleRestrictions.NoNameMapping"/>): Object#GetHashCode, Object.ReferenceEquals,
        /// LocalJumpError#ExitValue - or a library method standing in for one (NilClass#GetType). A CLR type's own
        /// members are listed: only the built-ins are Ruby's classes.</item>
        /// <item>IronRuby's own helpers, whose names start with two underscores (__ir_inspect__, __cmp_operand__):
        /// those a library defines with [RubyMethod], and those the core prelude (ruby4.rb and the files it
        /// requires) defines, aliases or makes private while it loads. MRI's own methods of that shape are listed,
        /// as is a method of that name that a program defines.</item>
        /// </list>
        /// </summary>
        public static bool IsUnlisted(RubyModule/*!*/ owner, string/*!*/ name, RubyMemberInfo/*!*/ member) {
            if (member.IsInteropMember || member is RubyLibraryMethodInfo && IsSystemObjectMethodName(name)) {
                return (owner.Restrictions & ModuleRestrictions.NoNameMapping) != 0;
            }

            return IsHelperName(name) && (member is RubyLibraryMethodInfo || owner._preludeHelpers != null && owner._preludeHelpers.Contains(name));
        }

        private static bool IsSystemObjectMethodName(string/*!*/ name) {
            switch (name) {
                case "Equals":
                case "GetHashCode":
                case "GetType":
                case "ToString":
                case "ReferenceEquals":
                    return true;
            }
            return false;
        }

        private static bool IsHelperName(string/*!*/ name) {
            if (!name.StartsWith("__", StringComparison.Ordinal)) {
                return false;
            }

            switch (name) {
                case "__send__":
                case "__id__":
                case "__method__":
                case "__callee__":
                case "__dir__":
                    return false;
            }
            return true;
        }

        public void ForEachMember(bool inherited, RubyMethodAttributes attributes, Action<string/*!*/, RubyModule/*!*/, RubyMemberInfo/*!*/>/*!*/ action) {
            ForEachMember(inherited, attributes, null, action);
        }

        #endregion

        #region Class variables

        // The class-variable table is its own lock. It has to be: a class variable is written by
        // plain Ruby code (@@x = v) from any thread, while the lookup walks the ancestors under the
        // class-hierarchy lock - so the two cannot share one lock without the writer taking a
        // process-wide lock on every assignment. A Dictionary<,> written by two threads at once
        // does not merely lose an entry, it corrupts its buckets and throws for the rest of the
        // process; RubyGems hits this, because its Happy-Eyeballs connect leaves threads running
        // while Gem::Specification assigns its class variables.
        //
        // The table lock is always the innermost one - nothing here takes the class-hierarchy lock
        // while holding it - so the order the two are acquired in is fixed and cannot deadlock.

        public void ForEachClassVariable(bool inherited, Func<RubyModule, string, object, bool>/*!*/ action) {
            Context.RequiresClassHierarchyLock();

            ForEachAncestor(inherited, delegate(RubyModule/*!*/ module) {
                // notification that we entered the module (it could have no class variable):
                if (action(module, null, Missing.Value)) return true;

                return module.EnumerateClassVariables(action);
            });
        }

        public void SetClassVariable(string/*!*/ name, object value) {
            InitializeClassVariableTable();

            Mutate();
            var table = _classVariables;
            lock (table) {
                table[name] = value;
            }
        }

        public bool TryGetClassVariable(string/*!*/ name, out object value) {
            value = null;
            var table = _classVariables;
            if (table == null) {
                return false;
            }
            lock (table) {
                return table.TryGetValue(name, out value);
            }
        }

        public bool RemoveClassVariable(string/*!*/ name) {
            var table = _classVariables;
            if (table == null) {
                return false;
            }
            lock (table) {
                return table.Remove(name);
            }
        }

        public RubyModule TryResolveClassVariable(string/*!*/ name, out object value) {
            Assert.NotNull(name);

            RubyModule front = null, target = null;
            object targetValue = null;

            // MRI's CVAR_LOOKUP: the variable is the one furthest up the ancestors; one defined
            // lower down as well is "overtaken" by it, which is an error.
            using (Context.ClassHierarchyLocker()) {
                ForEachAncestor(delegate(RubyModule/*!*/ module) {
                    object moduleValue;
                    if (module.TryGetClassVariable(name, out moduleValue)) {
                        if (front == null) {
                            front = module;
                        }
                        target = module;
                        targetValue = moduleValue;
                    }
                    return false;
                });
            }

            if (front != target) {
                throw new RuntimeError(String.Format("class variable {0} of {1} is overtaken by {2}",
                    name, Context.GetModuleDisplayName(front), Context.GetModuleDisplayName(target)));
            }

            value = targetValue;
            return target;
        }

        public bool EnumerateClassVariables(Func<RubyModule, string, object, bool>/*!*/ action) {
            var table = _classVariables;
            if (table != null) {
                KeyValuePair<string, object>[] variables;
                // A snapshot: the action is Ruby code, and running it while holding the table lock
                // would let it deadlock against its own assignment to a class variable.
                lock (table) {
                    variables = new KeyValuePair<string, object>[table.Count];
                    ((ICollection<KeyValuePair<string, object>>)table).CopyTo(variables, 0);
                }
                foreach (KeyValuePair<string, object> variable in variables) {
                    if (action(this, variable.Key, variable.Value)) return true;
                }
            }

            return false;
        }

        #endregion

        #region Mixins (thread-safe)

        /// <summary>
        /// Returns true if the CLR type is treated as Ruby module (as opposed to a Ruby class)
        /// </summary>
        public static bool IsModuleType(Type/*!*/ type) {
            return type.IsInterface || type.IsGenericTypeDefinition;
        }

        // thread-safe:
        // thread-safe without the class hierarchy lock: is_a?, kind_of? and Module#=== call this on
        // every check, and the lock plus the closure ForEachAncestor needs were most of their cost.
        // The walk only reads _prepends and _mixins, which are replaced, never mutated in place, under
        // the lock, and the superclass chain; an include/prepend running concurrently is seen or not,
        // just as with the lock, which only orders this check before or after it.
        public bool HasAncestor(RubyModule/*!*/ module) {
            RubyClass cls = this as RubyClass;
            if (cls == null) {
                return IsDeclaredAncestor(module);
            }
            for (RubyClass c = cls; c != null; c = c.SuperClass) {
                if (c.IsDeclaredAncestor(module)) return true;
            }
            return false;
        }

        // The modules ForEachDeclaredAncestor visits, read without the lock (see HasAncestor).
        private bool IsDeclaredAncestor(RubyModule/*!*/ module) {
            if (this == module) return true;
            foreach (RubyModule p in _prepends) {
                if (p == module) return true;
            }
            foreach (RubyModule m in _mixins) {
                if (m == module) return true;
            }
            return false;
        }

        public bool HasAncestorNoLock(RubyModule/*!*/ module) {
            Context.RequiresClassHierarchyLock();
            return ForEachAncestor(true, (m) => m == module);
        }

        // thread-safe:
        public RubyModule[]/*!*/ GetMixins() {
            using (Context.ClassHierarchyLocker()) {
                return ArrayUtils.Copy(_mixins);
            }
        }

        // thread-safe:
        public RubyModule[]/*!*/ GetPrepends() {
            using (Context.ClassHierarchyLocker()) {
                return ArrayUtils.Copy(_prepends);
            }
        }

        // thread-safe:
        public void IncludeModules(params RubyModule[]/*!*/ modules) {
            using (Context.ClassHierarchyLocker()) {
                IncludeModulesNoLock(modules);
            }
        }
        
        // thread-safe:
        public void PrependModules(params RubyModule[]/*!*/ modules) {
            using (Context.ClassHierarchyLocker()) {
                PrependModulesNoLock(modules);
            }
        }

        /// <summary>
        /// Module#prepend. Inserts the given modules (with their own ancestor chains spliced in) *before* this
        /// module in the method resolution order. Unlike include, prepend does not skip modules that are already
        /// ancestors of the super class -- MRI happily produces a duplicate entry in that case.
        /// </summary>
        internal void PrependModulesNoLock(RubyModule[]/*!*/ modules) {
            Context.RequiresClassHierarchyLock();
            Mutate();

            RubyUtils.RequirePrepends(this, modules);

            RubyModule[] expanded = ExpandPrependsNoLock(_prepends, modules);

            foreach (RubyModule module in expanded) {
                if (module.IsInterface && !CanIncludeClrInterface) {
                    if (Array.IndexOf(_prepends, module) == -1) {
                        throw new InvalidOperationException(String.Format(
                            "Interface '{0}' cannot be prepended to class '{1}' because its underlying type has already been created",
                            module.Name, Name
                        ));
                    }
                }
            }

            var oldPrepends = _prepends;
            if (oldPrepends.Length == expanded.Length) {
                // nothing new was inserted (re-prepending an already prepended module is a no-op in MRI):
                return;
            }

            PrependsUpdated(oldPrepends, _prepends = expanded);
            _context.ConstantAccessVersion++;
        }

        /// <summary>
        /// Expands the modules being prepended into a flat list. Prepending a module splices in that module's
        /// whole ancestor chain (its prepends, itself, then its mixins), mirroring what MRI does.
        /// Modules that are already in the list keep their original position.
        /// </summary>
        private static RubyModule[]/*!*/ ExpandPrependsNoLock(RubyModule/*!*/[]/*!*/ existing, IList<RubyModule/*!*/>/*!*/ added) {
            List<RubyModule> expanded = new List<RubyModule>(existing);

            foreach (RubyModule module in added) {
                Assert.NotNull(module);

                int index = 0;
                foreach (RubyModule ancestor in GetFlattenedAncestors(module)) {
                    int at = expanded.IndexOf(ancestor);
                    if (at >= 0) {
                        index = at + 1;
                    } else {
                        expanded.Insert(index, ancestor);
                        index++;
                    }
                }
            }

            return expanded.ToArray();
        }

        // The declared ancestors of a (non-class) module in MRO order: its prepends, itself, its mixins.
        private static List<RubyModule/*!*/>/*!*/ GetFlattenedAncestors(RubyModule/*!*/ module) {
            var result = new List<RubyModule>(module._prepends.Length + 1 + module._mixins.Length);
            result.AddRange(module._prepends);
            result.Add(module);
            result.AddRange(module._mixins);
            return result;
        }

        /// <summary>
        /// A module gained prepends after it had already been mixed into (or prepended to) other modules.
        /// Their flattened arrays must gain the new modules right before this one.
        /// RubyClass overrides this to also invalidate call sites; it calls back here to do the propagation.
        /// </summary>
        internal virtual void PrependsUpdated(RubyModule/*!*/[]/*!*/ oldPrepends, RubyModule/*!*/[]/*!*/ newPrepends) {
            PropagatePrependsToDependentClasses(oldPrepends, newPrepends);
        }

        internal void PropagatePrependsToDependentClasses(RubyModule/*!*/[]/*!*/ oldPrepends, RubyModule/*!*/[]/*!*/ newPrepends) {
            Context.RequiresClassHierarchyLock();

            if (_dependentClasses == null) {
                return;
            }

            foreach (var cls in _dependentClasses) {
                cls.SpliceAncestorUpdate(this, oldPrepends, newPrepends);
            }
        }

        /// <summary>
        /// <paramref name="owner"/> (which appears somewhere in this module's flattened mixin or prepend array)
        /// gained prepends; splice the newly added ones in right before it.
        /// </summary>
        internal void SpliceAncestorUpdate(RubyModule/*!*/ owner, RubyModule/*!*/[]/*!*/ oldPrepends, RubyModule/*!*/[]/*!*/ newPrepends) {
            Context.RequiresClassHierarchyLock();

            var addedList = new List<RubyModule>();
            foreach (var m in newPrepends) {
                if (Array.IndexOf(oldPrepends, m) == -1) {
                    addedList.Add(m);
                }
            }

            if (addedList.Count == 0) {
                return;
            }

            var added = addedList.ToArray();

            int mixinIndex = Array.IndexOf(_mixins, owner);
            if (mixinIndex >= 0) {
                var oldMixins = _mixins;
                var expanded = SpliceBefore(oldMixins, mixinIndex, added);
                if (expanded.Length != oldMixins.Length) {
                    MixinsUpdated(oldMixins, _mixins = expanded);
                }
            }

            int prependIndex = Array.IndexOf(_prepends, owner);
            if (prependIndex >= 0) {
                var old = _prepends;
                var expanded = SpliceBefore(old, prependIndex, added);
                if (expanded.Length != old.Length) {
                    PrependsUpdated(old, _prepends = expanded);
                }
            }

            _context.ConstantAccessVersion++;
        }

        private static RubyModule[]/*!*/ SpliceBefore(RubyModule/*!*/[]/*!*/ list, int index, RubyModule/*!*/[]/*!*/ added) {
            var result = new List<RubyModule>(list);
            int at = index;
            foreach (var m in added) {
                if (result.IndexOf(m) >= 0) {
                    continue;
                }
                result.Insert(at, m);
                at++;
            }
            return result.ToArray();
        }

        internal void IncludeModulesNoLock(RubyModule[]/*!*/ modules) {
            Context.RequiresClassHierarchyLock();
            Mutate();

            RubyUtils.RequireMixins(this, modules);

            // MRI treats `include M' as a no-op when M is already an ancestor via a prepend:
            if (_prepends.Length > 0) {
                var filtered = new List<RubyModule>(modules.Length);
                foreach (var m in modules) {
                    if (Array.IndexOf(_prepends, m) == -1) {
                        filtered.Add(m);
                    }
                }
                if (filtered.Count == 0) {
                    return;
                }
                modules = filtered.ToArray();
            }

            RubyModule[] expanded = ExpandMixinsNoLock(GetSuperClass(), _mixins, modules);

            foreach (RubyModule module in expanded) {
                if (module.IsInterface && !CanIncludeClrInterface) {
                    if (Array.IndexOf(_mixins, module) == -1) {
                        throw new InvalidOperationException(String.Format(
                            "Interface '{0}' cannot be included in class '{1}' because its underlying type has already been created",
                            module.Name, Name
                        ));
                    }
                } 
            }

            MixinsUpdated(_mixins, _mixins = expanded);
            _context.ConstantAccessVersion++;

            if (!IsClass) {
                PropagateMixinsToDependentsNoLock();
            }
        }

        /// <summary>
        /// This module gained mixins after it had itself been included elsewhere. MRI (since 3.0) makes the
        /// new modules ancestors of every class and module that includes this one, right after it.
        /// </summary>
        private void PropagateMixinsToDependentsNoLock() {
            Context.RequiresClassHierarchyLock();

            if (_dependentClasses != null) {
                foreach (var cls in _dependentClasses) {
                    cls.ReexpandMixinNoLock(this);
                }
            }

            if (_dependentModules != null) {
                foreach (var module in _dependentModules) {
                    module.ReexpandMixinNoLock(this);
                }
            }
        }

        private void ReexpandMixinNoLock(RubyModule/*!*/ mixin) {
            if (Array.IndexOf(_mixins, mixin) == -1) {
                // a subclass, or a module that prepends rather than includes the mixin
                return;
            }

            RubyModule[] expanded = ExpandMixinsNoLock(GetSuperClass(), _mixins, new[] { mixin });
            if (expanded.Length == _mixins.Length) {
                return;
            }

            foreach (RubyModule module in expanded) {
                if (module.IsInterface && !CanIncludeClrInterface && Array.IndexOf(_mixins, module) == -1) {
                    // the CLR type is already built and cannot gain the interface
                    return;
                }
            }

            MixinsUpdated(_mixins, _mixins = expanded);
            _context.ConstantAccessVersion++;

            if (!IsClass) {
                PropagateMixinsToDependentsNoLock();
            }
        }

        internal void InitializeNewMixin(RubyModule/*!*/ mixin) {
            if (_methodsState != MemberTableState.Uninitialized) {
                mixin.InitializeMethodTableNoLock();
            }

            if (_constantsState != MemberTableState.Uninitialized) {
                mixin.InitializeConstantTableNoLock();
            }
        }

        internal virtual void MixinsUpdated(RubyModule/*!*/[]/*!*/ oldMixins, RubyModule/*!*/[]/*!*/ newMixins) {
            // RubyClass overrides this and records itself in DependentClasses instead
            foreach (var mixin in newMixins) {
                if (Array.IndexOf(oldMixins, mixin) == -1) {
                    mixin.AddDependentModule(this);
                }
            }
        }

        private void AddDependentModule(RubyModule/*!*/ dependentModule) {
            Context.RequiresClassHierarchyLock();

            if (_dependentModules == null) {
                _dependentModules = new WeakList<RubyModule>();
            } else {
                foreach (var module in _dependentModules) {
                    if (ReferenceEquals(dependentModule, module)) {
                        return;
                    }
                }
            }

            _dependentModules.Add(dependentModule.WeakSelf);
        }

        // Requires hierarchy lock
        public static RubyModule[]/*!*/ ExpandMixinsNoLock(RubyClass superClass, RubyModule/*!*/[]/*!*/ modules) {
            return ExpandMixinsNoLock(superClass, EmptyArray, modules);
        }

        // Requires hierarchy lock
        private static RubyModule[]/*!*/ ExpandMixinsNoLock(RubyClass superClass, RubyModule/*!*/[]/*!*/ existing, IList<RubyModule/*!*/>/*!*/ added) {
            Assert.NotNull(existing);
            Assert.NotNull(added);
            
            List<RubyModule> expanded = new List<RubyModule>(existing);
            ExpandMixinsNoLock(superClass, expanded, 0, added, true);
            return expanded.ToArray();
        }

        // Requires hierarchy lock
        private static int ExpandMixinsNoLock(RubyClass superClass, List<RubyModule/*!*/>/*!*/ existing, int index, IList<RubyModule/*!*/>/*!*/ added, 
            bool recursive) {

            foreach (RubyModule module in added) {
                Assert.NotNull(module);

                int newIndex = existing.IndexOf(module);
                if (newIndex >= 0) {
                    // Module is already present in _mixins
                    // Update the insertion point so that we retain ordering of dependencies
                    // If we're still in the initial level of recursion, repeat for module's mixins
                    index = newIndex + 1;
                    if (recursive) {
                        index = ExpandMixinsNoLock(superClass, existing, index, module._mixins, false);
                    }
                } else {
                    // Module is not yet present in _mixins
                    // Modules prepended to it precede it in the MRO, so splice them in at the insertion point first:
                    if (module._prepends.Length > 0) {
                        index = ExpandMixinsNoLock(superClass, existing, index, module._prepends, false);
                    }

                    // Recursively insert module dependencies at the insertion point, then insert module itself
                    newIndex = ExpandMixinsNoLock(superClass, existing, index, module._mixins, false);

                    // insert module only if it is not an ancestor of the superclass:
                    if (superClass == null || !superClass.HasAncestorNoLock(module)) {
                        existing.Insert(index, module);
                        index = newIndex + 1;
                    } else {
                        index = newIndex;
                    }
                }
            }
            return index;
        }

        protected virtual bool CanIncludeClrInterface {
            get { return true; }
        }

        /// <summary>
        /// The CLR interface the type of a Ruby class mixing in this module implements although the module is not a CLR interface:
        /// Comparable makes the class an IComparable (CompareTo calls &lt;=&gt;), Enumerable an IEnumerable (GetEnumerator
        /// lists Enumerable#to_a), so that CLR code can sort and iterate its instances.
        /// </summary>
        internal Type GetImpliedClrInterface() {
            if ((_restrictions & ModuleRestrictions.Builtin) != ModuleRestrictions.Builtin || _typeTracker != null) {
                return null;
            }
            switch (_name) {
                case "Comparable": return typeof(IComparable);
                case "Enumerable": return typeof(System.Collections.IEnumerable);
                default: return null;
            }
        }

        internal List<Type> GetImplementedInterfaces(Type/*!*/ baseType) {
            List<Type> interfaces = new List<Type>();
            using (Context.ClassHierarchyLocker()) {
                foreach (RubyModule m in _mixins) {
                    if (m.IsInterface && !m.TypeTracker.Type.IsGenericTypeDefinition && !interfaces.Contains(m.TypeTracker.Type)) {
                        interfaces.Add(m.TypeTracker.Type);
                    }

                    // only where a type can be generated for it (a Struct's RubyStruct is sealed, NativeAOT emits no types)
                    Type implied = m.GetImpliedClrInterface();
                    if (implied != null && !implied.IsAssignableFrom(baseType) && !interfaces.Contains(implied) &&
                        !baseType.IsSealed && System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) {
                        interfaces.Add(implied);
                    }
                }
            }
            return interfaces;
        }

        private void IncludeTraitNoLock(ref Action<RubyModule> initializer, MemberTableState tableState, Action<RubyModule>/*!*/ trait) {
            Assert.NotNull(trait);

            if (tableState == MemberTableState.Uninitialized) {
                if (initializer != null && initializer != EmptyInitializer) {
                    initializer += trait;
                } else {
                    initializer = trait;
                }
            } else {
                // TODO: postpone? hold lock?
                using (Context.ClassHierarchyUnlocker()) {
                    trait(this);
                }
            }
        }

        internal void IncludeLibraryModule(Action<RubyModule> instanceTrait, Action<RubyModule> classTrait, Action<RubyModule> constantsInitializer, 
            RubyModule/*!*/[]/*!*/ mixins, bool builtin) {

            // Do not allow non-builtin library inclusions to a frozen module.
            // We need to ensure that initializers are not modified once a module is frozen.
            if (!builtin) {
                Mutate();
            }

            using (Context.ClassHierarchyLocker()) {
                if (instanceTrait != null) {
                    IncludeTraitNoLock(ref _methodsInitializer, _methodsState, instanceTrait);
                }

                if (constantsInitializer != null) {
                    IncludeTraitNoLock(ref _constantsInitializer, _constantsState, constantsInitializer);
                }

                if (classTrait != null) {
                    var singleton = GetOrCreateSingletonClass();
                    singleton.IncludeTraitNoLock(ref singleton._methodsInitializer, singleton._methodsState, classTrait);
                }
                    
                // updates the module version:
                IncludeModulesNoLock(mixins);
            }
        }

        #endregion

        #region Names

        /// <summary>
        /// Ruby name of the module, never null. Anonymous modules and classes report
        /// their display name (#<Class:0x...>) the way MRI does in error messages.
        /// </summary>
        public string/*!*/ GetNonNullName(RubyContext/*!*/ context) {
            return _name != null ? GetName(context) : GetDisplayName(context, false).ToString();
        }

        public string GetName(RubyContext/*!*/ context) {
            return context == _context ? _name : _name + "@" + _context.RuntimeId;
        }

        /// <summary>
        /// What Module#name answers: nil for an anonymous module and for any singleton class, and
        /// otherwise a frozen String that is the *same* object on every call until the name
        /// changes - both of which MRI guarantees.
        /// </summary>
        public MutableString GetNameString(RubyContext/*!*/ context) {
            if (IsSingletonClass || _name == null) {
                return null;
            }

            // another runtime sees the name decorated with the runtime id; not worth caching
            if (context != _context) {
                return MutableString.CreateMutable(GetName(context), context.GetIdentifierEncoding()).Freeze();
            }

            var result = _nameString;
            if (result == null) {
                _nameString = result = MutableString.CreateMutable(_name, context.GetIdentifierEncoding()).Freeze();
            }
            return result;
        }

        public MutableString GetDisplayName(RubyContext/*!*/ context, bool showEmptyName) {
            if (IsSingletonClass) {
                RubyClass c = (RubyClass)this;
                object singletonOf;
                MutableString result = MutableString.CreateMutable(context.GetIdentifierEncoding());

                int nestings = 0;
                while (true) {
                    nestings++;
                    result.Append("#<Class:");

                    singletonOf = c.SingletonClassOf;

                    RubyModule module = singletonOf as RubyModule;

                    if (module != null && !module.IsSingletonClass && module.Name == null) {
                        // the singleton class of an anonymous class or module: the inner half is
                        // that module's own description, "#<Class:0x...>", not the name of its
                        // superclass - which for a singleton class is itself nameless
                        result.Append(module.GetDisplayName(context, false));
                        break;
                    }

                    if (module == null) {
                        nestings++;
                        result.Append("#<");
                        // the object's class, which if anonymous has no name to print here - MRI
                        // falls back to how that class describes itself, "#<Class:0x...>"
                        RubyClass objectClass = c.SuperClass;
                        if (objectClass.Name != null) {
                            result.Append(objectClass.GetName(context));
                        } else {
                            result.Append(objectClass.GetDisplayName(context, false));
                        }
                        result.Append(':');
                        RubyUtils.AppendFormatHexObjectId(result, RubyUtils.GetObjectId(_context, singletonOf));
                        break;
                    }

                    if (!module.IsSingletonClass) {
                        result.Append(module.GetName(context));
                        break;
                    }

                    c = (RubyClass)module;
                }
                return result.Append('>', nestings);
            } else if (_name == null) {
                if (showEmptyName) {
                    return null;
                } else {
                    MutableString result = MutableString.CreateMutable(context.GetIdentifierEncoding());
                    result.Append("#<");
                    result.Append(_context.GetClassOf(this).GetName(context));
                    result.Append(':');
                    RubyUtils.AppendFormatHexObjectId(result, RubyUtils.GetObjectId(_context, this));
                    result.Append('>');
                    return result;
                }
            } else {
                return MutableString.CreateMutable(GetName(context), context.GetIdentifierEncoding());
            }
        }

        #endregion      

        #region Debug View

        // see: RubyObject.DebuggerDisplayValue
        internal string/*!*/ GetDebuggerDisplayValue(object obj) {
            return Context.Inspect(obj).ConvertToString();
        }

        // see: RubyObject.DebuggerDisplayType
        internal string/*!*/ GetDebuggerDisplayType() {
            return Name;
        }

        internal sealed class DebugView {
            private readonly RubyModule/*!*/ _obj;

            public DebugView(RubyModule/*!*/ obj) {
                Assert.NotNull(obj);
                _obj = obj;
            }

            #region RubyObjectDebugView

            [DebuggerDisplay("{GetModuleName(A),nq}", Name = "{GetClassKind(),nq}", Type = "")]
            public object A {
                get { return _obj.ImmediateClass; }
            }

            [DebuggerDisplay("{D}", Name = "frozen?", Type = "")]
            public bool D {
                get { return _obj.IsFrozen; }
                set { if (value) { _obj.Freeze(); } }
            }

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public object E {
                get {
                    var instanceData = _obj.TryGetInstanceData();
                    if (instanceData == null) {
                        return new RubyInstanceData.VariableDebugView[0];
                    }

                    return instanceData.GetInstanceVariablesDebugView(_obj.ImmediateClass.Context);
                }
            }

            private string GetClassKind() {
                return _obj.ImmediateClass.IsSingletonClass ? "singleton class" : "class";
            }

            private static string GetModuleName(object module) {
                var m = (RubyModule)module;
                return m != null ? m.GetDisplayName(m.Context, false).ToString() : null;
            }

            #endregion

            [DebuggerDisplay("{GetModuleName(F),nq}", Name = "super", Type = "")]
            public object F {
                get { return _obj.GetSuperClass(); }
            }

            [DebuggerDisplay("", Name = "mixins", Type = "")]
            public object G {
                get { return _obj.GetMixins(); }
            }

            [DebuggerDisplay("", Name = "instance methods", Type = "")]
            public object H {
                get { return GetMethods(RubyMethodAttributes.Instance); }
            }

            [DebuggerDisplay("", Name = "singleton methods", Type = "")]
            public object I {
                get { return GetMethods(RubyMethodAttributes.Singleton); }
            }

            private Dictionary<string, RubyMemberInfo> GetMethods(RubyMethodAttributes attributes) {
                // TODO: custom view for methods, sorted
                var result = new Dictionary<string, RubyMemberInfo>();
                using (_obj.Context.ClassHierarchyLocker()) {
                    _obj.ForEachMember(false, attributes | RubyMethodAttributes.VisibilityMask, (name, _, info) => {
                        result[name] = info;
                    });
                }
                return result;
            }

            // TODO: class variables
        }

        #endregion
    }
}

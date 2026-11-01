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
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using IronRuby.Builtins;
using Microsoft.Scripting;
using Microsoft.Scripting.Interpreter;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Threading;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime {

    public enum ScopeKind {
        TopLevel,
        Method,
        Module,
        Block,

        /// <summary>
        /// Block scope used by defined_method.
        /// </summary>
        BlockMethod,

        /// <summary>
        /// Block scope used by module_eval.
        /// </summary>
        BlockModule,

        /// <summary>
        /// BEGIN block scope.
        /// </summary>
        FileInitializer,

        /// <summary>
        /// The scope Binding#dup and Binding#clone hang their copy on.
        /// </summary>
        BindingCopy
    }

    public class RuntimeFlowControl {
        // null -> this is an inactive flow-control scope (method or top)
        // self -> this is an active RubyMethodScope or light RuntimeFlowControl scope (top scope cannot be active)
        // other -> the inner-most flow-control scope
        internal RuntimeFlowControl _activeFlowControlScope;

        internal void InitializeRfc(Proc proc) {
            if (proc != null && proc.Kind == ProcKind.Block) {
                proc.Kind = ProcKind.Proc;
                proc.Converter = this;
            }
        }

        internal RuntimeFlowControl() {
            // Perf note: Initialization is performed completely in the most derived scope classes to improve perf.
        }

        internal bool IsActiveMethod {
            get { return _activeFlowControlScope == this; }
        }

        internal void LeaveMethod() {
            Debug.Assert(!(this is RubyBlockScope) && !(this is RubyModuleScope));
            _activeFlowControlScope = null;
        }

        internal RuntimeFlowControl/*!*/ FlowControlScope {
            get { return _activeFlowControlScope ?? this; }
        }
    }
        
    [DebuggerTypeProxy(typeof(RubyScope.DebugView))]
    public abstract class RubyScope : RuntimeFlowControl {
        internal bool InLoop;
        internal bool InRescue;

        // Set once something may hold on to this scope after the call it belongs to has returned: a
        // Proc, Binding, method or nested scope made in it, a TracePoint event. A block scope that is
        // not captured is reused for the next call of its block (see RubyOps.TakeReusableBlockScope).
        internal bool IsCaptured;

        // Libraries call this for a scope they keep after returning.
        public void MarkCaptured() {
            if (!IsCaptured) {
                IsCaptured = true;
            }
        }

        // closure:
        internal /*and protected*/ MutableTuple _locals; // null if there are no variables
        internal /*and protected*/ string/*!*/[] _variableNames; // empty if there are no variables

        internal /*and protected readonly*/ RubyTopLevelScope/*!*/ _top;
        internal /*and protected readonly*/ RubyScope _parent;

        internal /*and protected readonly*/ object _selfObject;

        // cached ImmediateClassOf(_selfObject):
        private RubyClass _selfImmediateClass;

        // set by private/public/protected/module_function
        internal /*and protected*/ RubyMethodAttributes _methodAttributes;

        /// <summary>
        /// The state only eval, Binding and refinements need, out of line: a scope is allocated for
        /// every method call and every block call that needs one, and each field here would be paid
        /// for by all of them. Null until one of these is first set.
        /// </summary>
        internal sealed class RareState {
            // closure: name -> slot of the compiled locals, built for eval and Binding on demand
            public Dictionary<string, int> StaticLocalMapping;
            // closure: locals an eval or Binding#local_variable_set introduced
            public Dictionary<string, object> DynamicLocals;

            // Refinements activated by `using' in this scope, in activation order; null until `using' runs here.
            public List<RubyModule> UsedModules;
            // Parallel to UsedModules: what each `using' saw of the module's refinements (null = all, live).
            public List<HashSet<RubyModule>> UsedModuleSnapshots;

            // Memoized effective activation table for this scope (own + everything lexically enclosing),
            // stamped with RubyContext.RefinementVersion so that a `using' anywhere forces a recompute.
            public RefinementActivation Refinements;
            public int RefinementsVersion;

            // see RubyMethodScope.DefinitionRefinements
            public StrongBox<RefinementActivation> MethodDefinitionRefinements;
        }

        private RareState _rare;

        internal RareState/*!*/ Rare {
            get { return _rare ?? Interlocked.CompareExchange(ref _rare, new RareState(), null) ?? _rare; }
        }

        internal RareState RareOrNull {
            get { return _rare; }
        }

        private Dictionary<string, int> StaticLocalMapping {
            get { var r = _rare; return (r != null) ? r.StaticLocalMapping : null; }
            set { Rare.StaticLocalMapping = value; }
        }

        private Dictionary<string, object> DynamicLocals {
            get { var r = _rare; return (r != null) ? r.DynamicLocals : null; }
        }

        private List<RubyModule> UsedModules {
            get { var r = _rare; return (r != null) ? r.UsedModules : null; }
        }

        private List<HashSet<RubyModule>> UsedModuleSnapshots {
            get { var r = _rare; return (r != null) ? r.UsedModuleSnapshots : null; }
        }

        internal InterpretedFrame InterpretedFrame { get; set; }

        /// <summary>
        /// The backtrace label of the frame this scope belongs to, without its blocks - "Object#m",
        /// "<class:C>", "<main>" - and how many blocks deep the scope is in it.
        /// </summary>
        internal string GetFrameBaseLabel(out int blockLevels) {
            blockLevels = 0;
            for (RubyScope scope = this; scope != null; scope = scope.Parent) {
                switch (scope.Kind) {
                    case ScopeKind.Block:
                    case ScopeKind.BlockMethod:
                    case ScopeKind.BlockModule:
                        blockLevels++;
                        break;

                    case ScopeKind.Method:
                        var method = (RubyMethodScope)scope;
                        return IronRuby.Compiler.Ast.MethodDefinition.QualifyFrameLabel(method.DefinitionName, method.DeclaringModule);

                    case ScopeKind.Module:
                        // an instance_eval/class_eval string runs in its caller's frame
                        if (scope is RubyModuleEvalScope) {
                            break;
                        }
                        var module = scope.Module;
                        var cls = module as RubyClass;
                        if (cls != null && cls.IsSingletonClass) {
                            return "singleton class";
                        }
                        string name = module.Name ?? "";
                        int separator = name.LastIndexOf("::", StringComparison.Ordinal);
                        if (separator >= 0) {
                            name = name.Substring(separator + 2);
                        }
                        return (cls != null ? "<class:" : "<module:") + name + ">";

                    case ScopeKind.TopLevel:
                        return ((RubyTopLevelScope)scope).IsMain ? "<main>" : "<top (required)>";
                }
            }
            return null;
        }

        /// <summary>
        /// The file and line this scope's code is executing, for the builtin it has just called.
        /// Interpreted code knows it from its frame for free; compiled code has to be found on the
        /// CLR stack, which costs a stack walk with file info.
        /// </summary>
        public bool TryGetCurrentSourceLocation(out string path, out int line) {
            var frame = InterpretedFrame;
            if (frame != null) {
                var debugInfo = frame.GetDebugInfo(frame.InstructionIndex);
                if (debugInfo != null && debugInfo.FileName != null) {
                    path = debugInfo.FileName;
                    line = debugInfo.StartLine;
                    return true;
                }
            }
            return RubyUtils.TryGetCallerSourceLocation(RubyContext, out path, out line);
        }

        public abstract ScopeKind Kind { get; }
        public abstract bool InheritsLocalVariables { get; }

        internal RubyScope() {
            // Perf note: Initialization is performed completely in the most derived scope classes to improve perf.
        }

        // The per-call state of a scope reused for another call of its block, as a new scope has it.
        internal void ResetForReuse(MutableTuple locals, object selfObject, InterpretedFrame interpretedFrame) {
            _activeFlowControlScope = _parent.FlowControlScope;
            InLoop = false;
            InRescue = false;
            _locals = locals;
            _selfObject = selfObject;
            _selfImmediateClass = null;
            _methodAttributes = RubyMethodAttributes.PublicInstance;
            _rare = null;
            InterpretedFrame = interpretedFrame;
        }

        public virtual RubyModule Module {
            get { return null; }
        }

        // The module whose own constant table stands for this scope in the lexical part of a lookup.
        internal virtual RubyModule LexicalConstantModule {
            get { return Module; }
        }

        public object SelfObject {
            get { return _selfObject; }
        }

        internal RubyClass/*!*/ SelfImmediateClass {
            get {
                if (_selfImmediateClass == null) {
                    // thread-safe, since all threads will calculate the same result:
                    _selfImmediateClass = RubyContext.GetImmediateClassOf(_selfObject);
                }
                return _selfImmediateClass; 
            }
        }

        public RubyMethodVisibility Visibility {
            get { return (RubyMethodVisibility)(_methodAttributes & RubyMethodAttributes.VisibilityMask); }
        }

        public RubyMethodAttributes MethodAttributes {
            get { return _methodAttributes; }
            set { _methodAttributes = value; }
        }

        public RubyGlobalScope/*!*/ GlobalScope {
            get { return _top.RubyGlobalScope; }
        }

        public RubyTopLevelScope/*!*/ Top {
            get { return _top; }
        }

        public RubyContext/*!*/ RubyContext {
            get { return _top.RubyContext; }
        }

        internal MutableTuple Locals {
            get { return _locals; }
        }

        internal bool LocalsInitialized {
            get { return _variableNames != null; }
        }

        public RubyScope Parent {
            get { return _parent; }
        }

        public bool IsEmpty {
            get { return RubyContext.EmptyScope == this; }
        }

        protected virtual bool IsClosureScope {
            get { return false; }
        }

        protected virtual bool IsFlowControlScope {
            get { return false; }
        }

        #region Local Variables

        internal void SetLocals(MutableTuple locals, string/*!*/[]/*!*/ variableNames) {
            Debug.Assert(_variableNames == null);
            _locals = locals;
            _variableNames = variableNames;
        }

        internal void SetEmptyLocals() {
            Debug.Assert(_variableNames == null);
            _variableNames = ArrayUtils.EmptyStrings;
            _locals = null;
        }

        private void EnsureBoxes() {
            if (StaticLocalMapping == null) {
                if (_variableNames == null) {
                    // Locals not installed yet (see GetDeclaredLocalVariables). Don't cache an
                    // empty mapping - it would permanently hide the real locals once SetLocals runs.
                    return;
                }
                int count = _variableNames.Length;
                Dictionary<string, int> boxes = new Dictionary<string, int>(count);
                for (int i = 0; i < count; i++) {
                    boxes[_variableNames[i]] = i;
                }
                StaticLocalMapping = boxes;
            }
        }

        // `it' and _1.._9 live in the block's variable table, but eval and Binding do not see them
        // as locals: `eval("it = 1")' makes a new variable rather than overwriting the parameter.
        // A scope of more than MutableTuple.MaxSize locals keeps them in a tuple of tuples
        // (ScopeBuilder.NewTuple), where slot i of the outer one is not local i.
        private object GetLocalValue(int index) {
            int size = _variableNames.Length;
            return (size > MutableTuple.MaxSize) ? _locals.GetNestedValue(size, index) : _locals.GetValue(index);
        }

        private void SetLocalValue(int index, object value) {
            int size = _variableNames.Length;
            if (size > MutableTuple.MaxSize) {
                _locals.SetNestedValue(size, index, value);
            } else {
                _locals.SetValue(index, value);
            }
        }

        private bool IsImplicitParameter(string/*!*/ name) {
            var names = OwnImplicitParameterNames;
            return names != null && Array.IndexOf(names, name) >= 0;
        }

        /// <summary>The value of this scope's own `it` or _1.._9, for Binding#implicit_parameter_get.</summary>
        public object GetImplicitParameterValue(string/*!*/ name) {
            EnsureBoxes();

            int index;
            if (StaticLocalMapping != null && StaticLocalMapping.TryGetValue(name, out index)) {
                return GetLocalValue(index);
            }
            return null;
        }

        private bool TryGetLocal(string/*!*/ name, out object value) {
            EnsureBoxes();

            int index;
            if (StaticLocalMapping != null && StaticLocalMapping.TryGetValue(name, out index) && !IsImplicitParameter(name)) {
                Debug.Assert(_locals != null);
                value = GetLocalValue(index);
                return true;
            }

            if (DynamicLocals == null) {
                value = null;
                return false;
            }

            lock (DynamicLocals) {
                return DynamicLocals.TryGetValue(name, out value);
            }
        }

        private bool TrySetLocal(string/*!*/ name, object value) {
            EnsureBoxes();

            int index;
            if (StaticLocalMapping != null && StaticLocalMapping.TryGetValue(name, out index) && !IsImplicitParameter(name)) {
                Debug.Assert(_locals != null);
                SetLocalValue(index, value);
                return true;
            }

            if (DynamicLocals == null) {
                return false;
            }

            lock (DynamicLocals) {
                if (!DynamicLocals.ContainsKey(name)) {
                    return false;
                }

                DynamicLocals[name] = value;
            }
            return true;
        }

        private IEnumerable<KeyValuePair<string, object>>/*!*/ GetDeclaredLocalVariables() {
            // TOPLEVEL_BINDING is published (and -r files run) before the main script's prologue
            // calls SetLocals, so the declared-locals table can legitimately be missing here.
            if (LocalsInitialized) {
                for (int i = 0; i < _variableNames.Length; i++) {
                    Debug.Assert(_locals != null);
                    yield return new KeyValuePair<string, object>(_variableNames[i], GetLocalValue(i));
                }
            }

            if (DynamicLocals != null) {
                lock (DynamicLocals) {
                    foreach (var entry in DynamicLocals) {
                        yield return entry;
                    }
                }
            }
        }

        private IEnumerable<string>/*!*/ GetDeclaredLocalSymbols() {
            // a variable an eval or Binding#local_variable_set introduced is newer than the ones
            // the scope was compiled with, and MRI lists it first
            if (DynamicLocals != null) {
                lock (DynamicLocals) {
                    foreach (string name in DynamicLocals.Keys) {
                        yield return name;
                    }
                }
            }

            // see GetDeclaredLocalVariables: the table may not be installed yet
            if (LocalsInitialized) {
                for (int i = 0; i < _variableNames.Length; i++) {
                    yield return _variableNames[i];
                }
            }
        }

        /// <summary>
        /// The implicit block parameters - `it` or `_1`.. - this scope brought into being by
        /// mentioning them. They live in the scope's variable table like any other parameter, but
        /// MRI does not count them as local variables. Null when there are none.
        /// </summary>
        public virtual string[] OwnImplicitParameterNames {
            get { return null; }
        }

        public List<string/*!*/>/*!*/ GetVisibleLocalNames() {
            var result = new List<string>();
            // a block parameter shadowing an outer local is the same name twice on the way up,
            // and MRI reports it once
            var seen = new HashSet<string>();
            RubyScope scope = this;
            while (true) {
                var implicitParameters = scope.OwnImplicitParameterNames;
                foreach (string name in scope.GetDeclaredLocalSymbols()) {
                    if (implicitParameters != null && Array.IndexOf(implicitParameters, name) >= 0) {
                        continue;
                    }
                    // compiler-generated locals such as a flip-flop's state
                    if (name.Length > 0 && name[0] == '#') {
                        continue;
                    }
                    if (seen.Add(name)) {
                        result.Add(name);
                    }
                }

                if (!scope.InheritsLocalVariables) {
                    return result;
                }

                scope = scope.Parent;
            }
        }

        public object ResolveLocalVariable(string/*!*/ name) {
            RubyScope scope = this;
            while (true) {
                object value;
                if (scope.TryGetLocal(name, out value)) {
                    return value;
                }

                if (!scope.InheritsLocalVariables) {
                    return null;
                }

                scope = scope.Parent;
            }
        }

        // Defines the variable as nil unless it is already visible from here.
        internal void DeclareLocalVariable(string/*!*/ name) {
            RubyScope scope = this;
            while (true) {
                object value;
                if (scope.TryGetLocal(name, out value)) {
                    return;
                }

                if (!scope.InheritsLocalVariables) {
                    DefineDynamicVariable(name, null);
                    return;
                }

                scope = scope.Parent;
            }
        }

        internal object ResolveAndSetLocalVariable(string/*!*/ name, object value) {
            RubyScope scope = this;
            while (true) {
                if (scope.TrySetLocal(name, value)) {
                    return value;
                }

                if (!scope.InheritsLocalVariables) {
                    DefineDynamicVariable(name, value);
                    return value;
                }

                scope = scope.Parent;
            }
        }

        /// <summary>
        /// Defines dynamic variable in this scope.
        /// module-eval scopes need to override this since they should set the variable in their parent scope.
        /// </summary>
        internal virtual void DefineDynamicVariable(string/*!*/ name, object value) {
            if (DynamicLocals == null) {
                Interlocked.CompareExchange(ref Rare.DynamicLocals, new Dictionary<string, object>(), null);
            }

            lock (DynamicLocals) {
                DynamicLocals[name] = value;
            }
        }

        #endregion

        #region Refinements

        /// <summary>
        /// `using M' in this scope.  Activation is lexical, so it is recorded on the scope object rather
        /// than anywhere global; the scope object is the runtime representative of the lexical position
        /// (a method's scope parent is the scope that was current at `def' time, a block's is its
        /// defining scope), which is the same chain Module.nesting walks.
        /// </summary>
        public void ActivateRefinements(RubyModule/*!*/ module) {
            ActivateRefinements(module, false);
        }

        /// <param name="snapshot">
        /// True for `using': only the refinements <paramref name="module"/> has now are activated (methods
        /// added to them later still count).  False for the implicit activation of a refinement's own
        /// module, which follows the module as it grows.
        /// </param>
        public void ActivateRefinements(RubyModule/*!*/ module, bool snapshot) {
            HashSet<RubyModule> refinements = null;
            if (snapshot) {
                var list = new List<RubyModule>();
                module.GetAllRefinements(list);
                refinements = new HashSet<RubyModule>(list);
            }

            if (UsedModules == null) {
                var rare = Rare;
                rare.UsedModules = new List<RubyModule>();
                rare.UsedModuleSnapshots = new List<HashSet<RubyModule>>();
            } else {
                // re-activating moves it to the front of this scope's precedence order
                int index = UsedModules.IndexOf(module);
                if (index >= 0) {
                    UsedModules.RemoveAt(index);
                    UsedModuleSnapshots.RemoveAt(index);
                }
            }
            UsedModules.Add(module);
            UsedModuleSnapshots.Add(refinements);

            // Invalidate every memoized table and, through the rule guards built on them, every cached
            // call site that could be affected.
            RubyContext.RefinementVersion++;
        }

        /// <summary>
        /// The refinements active at this lexical position.  Canonical: a scope with no `using' of its own
        /// returns its parent's instance, and a chain with none anywhere returns
        /// <see cref="RefinementActivation.Empty"/>, so reference equality of the result is a sound (and
        /// cheap) call-site guard.
        /// </summary>
        public RefinementActivation/*!*/ GetActiveRefinements() {
            var context = RubyContext;
            if (!context.HasRefinements) {
                return RefinementActivation.Empty;
            }

            int version = context.RefinementVersion;
            var cached = _rare;
            if (cached != null && cached.Refinements != null && cached.RefinementsVersion == version) {
                return cached.Refinements;
            }

            RefinementActivation outer;
            var blockScope = this as RubyBlockScope;
            RefinementActivation blockOverride = (blockScope != null) ? blockScope.BlockFlowControl.Proc.RefinementOverride : null;
            var methodScope = this as RubyMethodScope;
            if (blockOverride != null) {
                outer = blockOverride;
            } else if (methodScope != null) {
                // A method sees the refinements that were active where and when it was defined, not
                // those its declaring scope has since acquired: `using' after a `def' does not reach
                // into it.  The declaring scope is shared by every method defined in it, so the
                // definition-time state comes from the compiled body instead - see
                // RubyMethodBody.SetDefinitionRefinements.  A body compiled with no record was
                // defined before anything was refined at all.
                var recorded = methodScope.DefinitionRefinements;
                if (recorded == null) {
                    outer = RefinementActivation.Empty;
                } else {
                    outer = recorded.Value ?? ((_parent != null) ? _parent.GetActiveRefinements() : RefinementActivation.Empty);
                }
            } else {
                outer = (_parent != null) ? _parent.GetActiveRefinements() : RefinementActivation.Empty;
            }
            RefinementActivation result = (UsedModules != null) ? RefinementActivation.Create(outer, UsedModules, UsedModuleSnapshots) : outer;

            var rare = Rare;
            rare.Refinements = result;
            rare.RefinementsVersion = version;
            return result;
        }

        #endregion

        #region Lexical Resolution

        public RubyModule/*!*/ GetInnerMostModuleForConstantLookup() {
            return GetInnerMostModule(false, RubyContext.ObjectClass);
        }

        public RubyModule/*!*/ GetInnerMostModuleForMethodLookup() {
            return GetInnerMostModule(false, Top.MethodLookupModule ?? RubyContext.ObjectClass);
        }

        public RubyModule/*!*/ GetInnerMostModuleForClassVariableLookup() {
            return GetInnerMostModule(true, RubyContext.ObjectClass);
        }

        /// <summary>Null when no class or module body encloses the scope, i.e. at the top level.</summary>
        internal RubyModule GetInnerMostModuleForClassVariableAccess() {
            return GetInnerMostModule(true, null);
        }
        
        private RubyModule/*!*/ GetInnerMostModule(bool skipSingletons, RubyModule/*!*/ fallbackModule) {
            RubyScope scope = this;
            do {
                RubyModule result = scope.Module;
                if (result != null && (!skipSingletons || !result.IsSingletonClass)) {
                    return result;
                }
                scope = scope.Parent;
            } while (scope != null);
            return fallbackModule;
        }

        /// <summary>
        /// The name the enclosing method was defined under, or null outside any method. A block
        /// reports the method it was written in, and a block define_method turned into a method
        /// body reports the name it was given.
        /// </summary>
        public string GetCurrentMethodName() {
            return GetCurrentMethodName(false);
        }

        /// <summary>
        /// With <paramref name="callee"/> the name the method was called by (differs through an alias).
        /// </summary>
        public string GetCurrentMethodName(bool callee) {
            for (RubyScope scope = this; scope != null; scope = scope.Parent) {
                switch (scope.Kind) {
                    case ScopeKind.Method:
                        var methodScope = (RubyMethodScope)scope;
                        return callee ? methodScope.CalleeName : methodScope.DefinitionName;

                    case ScopeKind.BlockMethod:
                        return ((RubyBlockScope)scope).BlockFlowControl.Proc.Method.DefinitionName;

                    case ScopeKind.TopLevel:
                        return null;
                }
            }
            return null;
        }

        public RubyMethodScope GetInnerMostMethodScope() {
            RubyScope scope = this;
            while (scope != null && scope.Kind != ScopeKind.Method) {
                scope = scope.Parent;
            }
            return (RubyMethodScope)scope;
        }

        public RubyClosureScope/*!*/ GetInnerMostClosureScope() {
            RubyScope scope = this;
            while (scope != null && !scope.IsClosureScope) {
                scope = scope.Parent;
            }
            return (RubyClosureScope)scope;
        }

        public void GetInnerMostBlockOrMethodScope(out RubyBlockScope blockScope, out RubyMethodScope methodScope) {
            methodScope = null;
            blockScope = null;
            RubyScope scope = this;
            while (scope != null) {
                switch (scope.Kind) {
                    case ScopeKind.Block:
                    case ScopeKind.BlockMethod:
                    case ScopeKind.BlockModule:
                        blockScope = (RubyBlockScope)scope;
                        return;

                    case ScopeKind.Method:
                        methodScope = (RubyMethodScope)scope;
                        return;
                }

                scope = scope.Parent;
            }
        }

        /// <summary>
        /// Returns 
        /// -1 if there is no method or block-method scope,
        /// 0 if the target scope is a method scope,
        /// id > 0 of the target lambda for block-method scopes.
        /// </summary>
        internal int GetSuperCallTarget(out RubyModule declaringModule, out string/*!*/ methodName, out RubyScope targetScope) {
            RubyScope scope = this;
            while (true) {
                Debug.Assert(scope != null);

                switch (scope.Kind) {
                    case ScopeKind.Method:
                        RubyMethodScope methodScope = (RubyMethodScope)scope;
                        // See RubyOps.DefineMethod for why we can use Method here.
                        declaringModule = methodScope.DeclaringModule;
                        methodName = methodScope.DefinitionName;
                        targetScope = scope;
                        return 0;

                    case ScopeKind.BlockMethod:
                        RubyLambdaMethodInfo info = ((RubyBlockScope)scope).BlockFlowControl.Proc.Method;
                        Debug.Assert(info != null);
                        declaringModule = info.DeclaringModule;
                        methodName = info.DefinitionName;
                        targetScope = scope;
                        return info.Id;

                    case ScopeKind.TopLevel:
                        declaringModule = null;
                        methodName = null;
                        targetScope = null;
                        return -1;
                }

                scope = scope.Parent;
            }
        }

        public RubyScope/*!*/ GetMethodAttributesDefinitionScope() {
            RubyScope scope = this;
            while (true) {
                switch (scope.Kind) {
                    case ScopeKind.Module:
                    case ScopeKind.BlockModule:
                    case ScopeKind.Method:
                    case ScopeKind.TopLevel:
                    case ScopeKind.FileInitializer:
                        return scope;
                }

                scope = scope.Parent;
            }
        }

        public RubyModule/*!*/ GetMethodDefinitionOwner() {
            // MRI 1.9: 
            // - define_method doesn't influence the definition owner (unlike MRI 1.8)
            // - skips module_eval blocks above method scope 
            // MRI 1.8: 
            // - skips module_eval and define_method blocks above method scope.
            // IronRuby: 
            // - Fallback to the top-level singleton class when hosted.

            RubyScope scope = this;
            while (true) {
                Debug.Assert(scope != null);

                switch (scope.Kind) {
                    case ScopeKind.TopLevel:
                        Debug.Assert(scope == Top);
                        return Top.MethodLookupModule ?? Top.TopModuleOrObject;

                    case ScopeKind.Module:
                        Debug.Assert(scope.Module != null);
                        return scope.Module;

                    case ScopeKind.Method:
                        // A `def` nested in a method body defines into the same place the
                        // enclosing definition went, so keep walking the lexical chain - which
                        // may well end in a class_eval-like block scope (`Class.new { def m; def
                        // n; end; end }` defines n on the new class, not on Object).
                        break;

                    case ScopeKind.BlockMethod:
                        if (RubyContext.RubyOptions.Compatibility == RubyCompatibility.Ruby19) {
                            break;
                        }
                        return ((RubyBlockScope)scope).BlockFlowControl.Proc.Method.DeclaringModule;

                    case ScopeKind.BlockModule:
                        BlockParam blockParam = ((RubyBlockScope)scope).BlockFlowControl;
                        Debug.Assert(blockParam.MethodLookupModule != null);
                        return blockParam.MethodLookupModule;
                }

                scope = scope.Parent;
            }
        }

        // thread-safe:
        // Returns null on success, the lexically inner-most module on failure.
        internal RubyModule TryResolveConstantNoLock(RubyGlobalScope autoloadScope, string/*!*/ name, out ConstantStorage result) {
            RubyModule owner;
            return TryResolveConstantNoLock(autoloadScope, name, out result, out owner);
        }

        // thread-safe:
        // As above, and on success also reports the module the constant was found in - null unless
        // the runtime has seen Module#deprecate_constant, since finding it can cost an extra
        // ancestor walk and only the deprecation warning needs it.
        internal RubyModule TryResolveConstantNoLock(RubyGlobalScope autoloadScope, string/*!*/ name, out ConstantStorage result,
            out RubyModule owner) {

            var context = RubyContext;
            owner = null;
            context.RequiresClassHierarchyLock();
            
            RubyScope scope = this;

            // lexical lookup first:
            RubyModule innerMostModule = null;
            do {
                RubyModule module = scope.Module;

                if (module != null) {
	                Debug.Assert(module.Context == context);

                    RubyModule lexicalModule = scope.LexicalConstantModule;
                    if (lexicalModule.TryGetConstantNoLock(autoloadScope, name, out result)) {
                        owner = lexicalModule;
                        return null;
                    }

                    // remember the module:
                    if (innerMostModule == null) {
                        innerMostModule = module;
                    }
                }

                scope = scope.Parent;
            } while (scope != null);

            // check the inner most module and it's base classes/mixins:
            if (innerMostModule != null) {
                if (innerMostModule.TryResolveConstantNoLock(autoloadScope, name, out result)) {
                    if (context.HasDeprecatedConstants) {
                        owner = innerMostModule.GetConstantOwnerNoLock(name);
                    }
                    return null;
                }

                // MRI falls back to Object's constants only from a module. A class reaches Object
                // through its own ancestors, which have just been searched - unless it descends
                // from BasicObject rather than Object, and such a class is not meant to see
                // Object's constants at all: `class C < BasicObject; Kernel; end' is a NameError.
                if (innerMostModule.IsClass) {
                    return innerMostModule;
                }
            } else {
                innerMostModule = context.ObjectClass;
            }

            if (context.ObjectClass.TryResolveConstantNoLock(autoloadScope, name, out result)) {
                if (context.HasDeprecatedConstants) {
                    owner = context.ObjectClass.GetConstantOwnerNoLock(name);
                }
                return null;
            }

            return innerMostModule;
        }

        #endregion

        #region Debug View
#pragma warning disable 414 // msc: unused field

#if DEBUG
        private string _debugName;

        public override string ToString() {
            return _debugName;
        }
#endif
        [Conditional("DEBUG")]
        public void SetDebugName(string name) {
#if DEBUG
            _debugName = name;
#endif
        }

        internal sealed class DebugView {
            private readonly RubyScope/*!*/ _scope;
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
            private readonly string _selfClassName;
            
            public DebugView(RubyScope/*!*/ scope) {
                Assert.NotNull(scope);
                _scope = scope;
                var name = _scope.RubyContext.GetImmediateClassOf(_scope._selfObject).GetDisplayName(_scope.RubyContext, true);
                _selfClassName = name != null ? name.ConvertToString() : null;
            }

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public VariableView[]/*!*/ A0 {
                get {
                    List<VariableView> result = new List<VariableView>();
                    RubyScope scope = _scope;
                    while (true) {
                        foreach (var variable in scope.GetDeclaredLocalVariables()) {
                            string name = variable.Key;
                            string className = _scope.RubyContext.GetClassDisplayName(variable.Value);
                            if (scope != _scope) {
                                name += " (outer)";
                            }
                            result.Add(new VariableView(name, variable.Value, className));
                        }

                        if (!scope.InheritsLocalVariables) {
                            break;
                        }
                        scope = scope.Parent;
                    }
                    return result.ToArray();
                }
            }

            [DebuggerDisplay("{A1}", Name = "self", Type = "{_selfClassName,nq}")]
            public object A1 {
                get { return _scope._selfObject; }
            }

            [DebuggerDisplay("{B}", Name = "MethodAttributes", Type = "")]
            public object B {
                get { return _scope._methodAttributes; }
            }

            [DebuggerDisplay("{C}", Name = "ParentScope", Type = "")]
            public object C {
                get { return (RubyScope)_scope.Parent; }
            }

            [DebuggerDisplay("", Name = "Raw Variables", Type = "")]
            public object D {
                get {
                    var result = new Dictionary<string, object>();
                    foreach (var variable in _scope.GetDeclaredLocalVariables()) {
                        result.Add(variable.Key, variable.Value);
                    }
                    return result;
                }
            }

            [DebuggerDisplay("{_value}", Name = "{_name,nq}", Type = "{_valueClassName,nq}")]
            internal struct VariableView {
                [DebuggerBrowsable(DebuggerBrowsableState.Never)]
                private readonly string/*!*/ _name;
                [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
                private readonly object _value;
                [DebuggerBrowsable(DebuggerBrowsableState.Never)]
                private readonly string/*!*/ _valueClassName;

                public VariableView(string/*!*/ name, object value, string/*!*/ valueClassName) {
                    _name = name;
                    _value = value;
                    _valueClassName = valueClassName;
                }
            }
        }

#pragma warning restore 414 // msc: unused field
        #endregion
    }

    public abstract class RubyClosureScope : RubyScope {
        // $+
        private MatchData _currentMatch;

        // $_
        private object _lastInputLine;

        internal RubyClosureScope() {
            // Perf note: Initialization is performed completely in the most derived scope classes to improve perf.
        }

        protected override bool IsClosureScope {
            get { return true; }
        }

        public MatchData CurrentMatch {
            get { return _currentMatch; }
            set { _currentMatch = value; }
        }

        public object LastInputLine {
            get { return _lastInputLine; }
            set { _lastInputLine = value; }
        }

        internal MutableString GetCurrentMatchGroup(int index) {
            Debug.Assert(index >= 0);

            var match = _currentMatch;
            return (match != null) ? match.GetGroupValue(index) : null;
        }

        internal MutableString GetCurrentPreMatch() {
            var match = _currentMatch;
            return (match != null) ? match.GetPreMatch() : null;
        }

        internal MutableString GetCurrentPostMatch() {
            var match = _currentMatch;
            return (match != null) ? match.GetPostMatch() : null;
        }

        internal MutableString GetCurrentMatchLastGroup() {
            var match = _currentMatch;
            if (match != null) {
                // TODO: cache the last successful group index?
                for (int i = match.GroupCount - 1; i >= 0; i--) {
                    MutableString result = match.GetGroupValue(i);
                    if (result != null) {
                        return result;
                    }
                }
            }

            return null;
        }

#if TODO_DebugView
         [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
            private readonly string/*!*/ _matchClassName;
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
            private readonly string/*!*/ _lastInputLine;
var closureScope = scope as RubyClosureScope;
                if (closureScope != null) {
                    _matchClassName = _scope.RubyContext.GetImmediateClassOf(closureScope.CurrentMatch).GetDisplayName(true).ConvertToString();
                    _lastInputLine = _scope.RubyContext.GetImmediateClassOf(closureScope.LastInputLine).GetDisplayName(true).ConvertToString();
                }
                

            [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
            [DebuggerDisplay("{A2 != null ? A2.ToString() : \"nil\",nq}", Name = "$~", Type = "{_matchClassName,nq}")]
            public Match A2 {
                get { return (_scope._currentMatch != null) ? _scope._currentMatch.Match : null; }
            }

            [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
            [DebuggerDisplay("{A2 != null ? A2.ToString() : \"nil\",nq}", Name = "$~", Type = "{_matchClassName,nq}")]
            public Match A2 {
                get { return (_scope._currentMatch != null) ? _scope._currentMatch.Match : null; }
            }
#endif
    }

    public sealed class RubyMethodScope : RubyClosureScope {
        private readonly RubyModule/*!*/ _declaringModule;
        private readonly string/*!*/ _definitionName;
        private readonly Proc _blockParameter;

        // Lowest 2 bits are flags:
        internal const int HasBlockFlag = 1;
        internal const int HasUnsplatFlag = 2;
        private readonly int _visibleParameterCountAndSignatureFlags;

        internal bool HasUnsplatParameter {
            get { return (_visibleParameterCountAndSignatureFlags & HasUnsplatFlag) != 0; }
        }

        internal bool HasBlockParameter {
            get { return (_visibleParameterCountAndSignatureFlags & HasBlockFlag) != 0; }
        }

        internal int VisibleParameterCount {
            get { return _visibleParameterCountAndSignatureFlags >> 2; }
        }

        public override ScopeKind Kind { get { return ScopeKind.Method; } }
        public override bool InheritsLocalVariables { get { return false; } }

        // Singleton module-function method shares this pointer with instance method. See RubyOps.DefineMethod for details.
        internal RubyModule/*!*/ DeclaringModule {
            get { return _declaringModule; }
        }

        internal string/*!*/ DefinitionName {
            get { return _definitionName; }
        }

        // Set when the method was called through an alias; see RubyOps.MarkAliasCall.
        private string _calleeName;

        /// <summary>
        /// The name the method was called by - what Kernel#__callee__ answers.
        /// </summary>
        internal string/*!*/ CalleeName {
            get { return _calleeName ?? _definitionName; }
            set { _calleeName = value; }
        }

        /// <summary>
        /// The name the method was defined under - what Kernel#__method__ answers.
        /// </summary>
        public string/*!*/ MethodName {
            get { return _definitionName; }
        }
        
        public Proc BlockParameter {
            get { return _blockParameter; }
        }

        /// <summary>
        /// The refinements active where the method was defined, stored by the compiled body right after
        /// it creates this scope; null for a body compiled before anything was refined.  Public because
        /// the method body assigns it directly.
        /// </summary>
        public StrongBox<RefinementActivation> DefinitionRefinements {
            get { var r = RareOrNull; return (r != null) ? r.MethodDefinitionRefinements : null; }
            set { Rare.MethodDefinitionRefinements = value; }
        }

        internal RubyMethodScope(MutableTuple locals, string/*!*/[]/*!*/ variableNames, int visibleParameterCountAndSignatureFlags,
            RubyScope/*!*/ parent, RubyModule/*!*/ declaringModule, string/*!*/ definitionName,
            object selfObject, Proc blockParameter, InterpretedFrame interpretedFrame) {
            Assert.NotNull(parent, declaringModule, definitionName);

            // RuntimeFlowControl:
            _activeFlowControlScope = this;
            
            // RubyScope:
            _parent = parent;
            _top = parent.Top;
            _selfObject = selfObject;
            _methodAttributes = RubyMethodAttributes.PublicInstance;
            _locals = locals;
            _variableNames = variableNames;
            _visibleParameterCountAndSignatureFlags = visibleParameterCountAndSignatureFlags;
            InterpretedFrame = interpretedFrame;
            
            // RubyMethodScope:
            _declaringModule = declaringModule;
            _definitionName = definitionName;
            _blockParameter = blockParameter;

            InitializeRfc(blockParameter);
        }

#if DEBUG
        // The debug name is built on demand: composing it in the constructor put a string
        // concatenation and an allocation on every single method call.
        public override string ToString() {
            return "method " + _definitionName + ((_blockParameter != null) ? "&" : null);
        }
#endif

        public string/*!*/[]/*!*/ GetVisibleParameterNames() {
            int firstVisibleParameter = HasBlockParameter ? 1 : 0;
            var result = new string[VisibleParameterCount];
            
            // parameters precede other variables:
            for (int i = 0; i < result.Length; i++) {
                result[i] = _variableNames[firstVisibleParameter + i];
            }

            return result;
        }
    }

    public sealed class RubyModuleScope : RubyClosureScope {
        private readonly RubyModule _module;

        public override ScopeKind Kind { get { return ScopeKind.Module; } }
        public override bool InheritsLocalVariables { get { return false; } }

        public override RubyModule Module { get { return _module; } }

        internal RubyModuleScope(RubyScope/*!*/ parent, RubyModule/*!*/ module) {
            Assert.NotNull(parent, module);
            parent.MarkCaptured();

            // RuntimeFlowControl:
            _activeFlowControlScope = parent.FlowControlScope;

            // RubyScope:
            _parent = parent;
            _top = parent.Top;
            _selfObject = module;
            _methodAttributes = RubyMethodAttributes.PrivateInstance;

            // RubyModuleScope:
            _module = module;
            InLoop = parent.InLoop;
            InRescue = parent.InRescue;
            MethodAttributes = RubyMethodAttributes.PublicInstance;
        }
    }

    /// <summary>
    /// String based module_eval injects a module scope (represented by this class) into the lexical scope chain.
    /// </summary>
    /// <remarks>
    /// Note that block-based module_eval is different:
    /// 
    /// <code>
    /// class A
    ///   X = 1
    /// end
    /// 
    /// A.module_eval { p X }  # error
    /// A.module_eval "p X"    # 1
    /// </code>
    /// </remarks>
    public sealed class RubyModuleEvalScope : RubyClosureScope {
        private readonly RubyModule _module;
        private RubyModule _lexicalConstantModule;

        // MRI's instance_eval of a string does not create the receiver's singleton class up front:
        // until something is defined in it, the eval's cref is the receiver's class, whose own
        // constants are then seen before the caller's lexical scopes.
        internal override RubyModule LexicalConstantModule {
            get { return _lexicalConstantModule ?? _module; }
        }

        internal void SetLexicalConstantModule(RubyModule module) {
            _lexicalConstantModule = module;
        }

        public override ScopeKind Kind { get { return ScopeKind.Module; } }
        public override bool InheritsLocalVariables { get { return true; } }

        public override RubyModule Module { get { return _module; } }

        internal RubyModuleEvalScope(RubyScope/*!*/ parent, RubyModule module, object selfObject) {
            Assert.NotNull(parent);
            parent.MarkCaptured();

            // RuntimeFlowControl:
            _activeFlowControlScope = parent.FlowControlScope;

            // RubyScope:
            _parent = parent;
            _top = parent.Top;
            _selfObject = selfObject;
            _methodAttributes = RubyMethodAttributes.PublicInstance;

            // RubyModuleScope:
            _module = module;
            InLoop = parent.InLoop;
            InRescue = parent.InRescue;
            SetEmptyLocals();
        }

        internal override void DefineDynamicVariable(string/*!*/ name, object value) {
            _parent.DefineDynamicVariable(name, value);
        }
    }

    public sealed class RubyFileInitializerScope : RubyClosureScope {
        public override ScopeKind Kind { get { return ScopeKind.FileInitializer; } }
        public override bool InheritsLocalVariables { get { return false; } }

        internal RubyFileInitializerScope(MutableTuple locals, string[]/*!*/ variableNames, RubyScope/*!*/ parent) {
            parent.MarkCaptured();

            // RuntimeFlowControl:
            _activeFlowControlScope = parent.FlowControlScope;

            // RubyScope:
            _parent = parent;
            _top = parent.Top;
            _selfObject = parent.SelfObject;
            _methodAttributes = RubyMethodAttributes.PublicInstance;
            _locals = locals;
            _variableNames = variableNames;
            InLoop = parent.InLoop;
            InRescue = parent.InRescue;
        }

        internal override void DefineDynamicVariable(string/*!*/ name, object value) {
            _parent.DefineDynamicVariable(name, value);
        }
    }

    public sealed class RubyBlockScope : RubyClosureScope {
        private BlockParam/*!*/ _blockFlowControl;

        // The block a thread runs keeps its own $~ and $_, as MRI's thread root frame does; any
        // other block shares them with the method it is in.
        protected override bool IsClosureScope {
            get { return _blockFlowControl.IsThreadRoot; }
        }

        public override ScopeKind Kind { 
            get {
                if (_blockFlowControl.IsMethod) {
                    return ScopeKind.BlockMethod;
                }

                if (_blockFlowControl.MethodLookupModule == null) {
                    return ScopeKind.Block; 
                }

                return ScopeKind.BlockModule;
            } 
        }

        public override bool InheritsLocalVariables { 
            get { return true; } 
        }

        public BlockParam/*!*/ BlockFlowControl {
            get { return _blockFlowControl; }
        }

        // Set only for a block whose body has a return in a nested block: 1 while the body runs
        // (and catches a LambdaUnwinder aimed at this scope), 2 once it has finished.
        internal byte LambdaReturnState;

        public override string[] OwnImplicitParameterNames {
            get {
                var signature = _blockFlowControl.Proc.Dispatcher.ParameterSignature;
                return signature != null ? signature.ImplicitParameterNames : null;
            }
        }

        internal RubyBlockScope(MutableTuple locals, string/*!*/[]/*!*/ variableNames,
            BlockParam/*!*/ blockFlowControl, object selfObject, InterpretedFrame interpretedFrame) {
            var parent = blockFlowControl.Proc.LocalScope;

            // RuntimeFlowControl:
            _activeFlowControlScope = parent.FlowControlScope;

            // RubyScope:
            _parent = parent;
            _top = parent.Top;
            _selfObject = selfObject;
            _methodAttributes = RubyMethodAttributes.PublicInstance;
            _locals = locals;
            _variableNames = variableNames;
            InterpretedFrame = interpretedFrame;
            
            // RubyBlockScope:
            _blockFlowControl = blockFlowControl;
        }

        // Makes this scope, which nothing has captured, the scope of another call of its block.
        internal void Reinitialize(MutableTuple locals, BlockParam/*!*/ blockFlowControl, object selfObject, InterpretedFrame interpretedFrame) {
            _blockFlowControl = blockFlowControl;
            ResetForReuse(locals, selfObject, interpretedFrame);
            CurrentMatch = null;
            LastInputLine = null;
            LambdaReturnState = 0;
        }
    }

    /// <summary>
    /// The scope a copied Binding sees. MRI's Binding#dup shares every variable that already
    /// exists with the original - assigning to one is visible through the other - while a variable
    /// the copy defines afterwards stays private to it. A scope that inherits its parent's locals
    /// and keeps its own dynamic ones does exactly that: an existing name resolves up into the
    /// original's storage, and a new one is defined here.
    /// </summary>
    public sealed class RubyBindingCopyScope : RubyScope {
        public override ScopeKind Kind { get { return ScopeKind.BindingCopy; } }
        public override bool InheritsLocalVariables { get { return true; } }

        // No module of its own: lexical walks (Module.nesting, constant lookup) pass through to the parent.

        public RubyBindingCopyScope(RubyScope/*!*/ parent, object selfObject) {
            parent.MarkCaptured();

            // RuntimeFlowControl:
            _activeFlowControlScope = parent.FlowControlScope;

            // RubyScope:
            _parent = parent;
            _top = parent.Top;
            _selfObject = selfObject;
            _methodAttributes = parent.MethodAttributes;
            InLoop = parent.InLoop;
            InRescue = parent.InRescue;
            SetEmptyLocals();
        }
    }

    public sealed class RubyTopLevelScope : RubyClosureScope {
        public override ScopeKind Kind { get { return ScopeKind.TopLevel; } }
        public override bool InheritsLocalVariables { get { return false; } }

        // The main script's top level, whose frame is "<main>"; a required file's is "<top (required)>".
        // A hosted scope counts as main.
        internal bool IsMain { get; set; } = true;

        // the thread running the top-level code while it is active (see RubyOps.InitializeScope)
        internal int ActiveThreadId { get; set; }

        private readonly RubyGlobalScope/*!*/ _globalScope;
        private readonly RubyContext/*!*/ _context;
        private readonly RubyModule _methodLookupModule;
        private readonly RubyModule _wrappingModule;

        public RubyGlobalScope/*!*/ RubyGlobalScope {
            get {
                if (_globalScope == null) {
                    // This is the context's empty scope: the stand-in used by call sites that carry no
                    // Ruby frame - a Method object invoked as a block (flat_map(&method(:const_get))),
                    // or a library method called from C#. A library method asking it for a global scope
                    // wants the one of the program that is running (autoload, loaded features), so hand
                    // that over instead of failing the call.
                    var main = _context.MainGlobalScope;
                    if (main == null) {
                        throw new InvalidOperationException("Empty scope has no global scope.");
                    }
                    return main;
                }
                return _globalScope; 
            }
        }

        internal new RubyContext/*!*/ RubyContext {
            get { return _context; }
        }

        public override RubyModule Module {
            get { return _wrappingModule; }            
        }

        /// <summary>
        /// Method and class lookup in top-level hosted scope behave like if it was instance_eval'd as a proc in MRI 1.8, i.e.
        /// methods are resolved in the singleton class of the main object.
        /// </summary>
        public RubyModule MethodLookupModule {
            get { return _methodLookupModule; }
        }

        internal RubyModule/*!*/ TopModuleOrObject {
            get { return _wrappingModule ?? _globalScope.Context.ObjectClass; }
        }

        // empty scope:
        internal RubyTopLevelScope(RubyContext/*!*/ context) {
            Assert.NotNull(context);

            // RubyScope:
            _top = this;
            _methodAttributes = RubyMethodAttributes.PrivateInstance;

            // RubyTopLevelScope:
            _context = context;
            SetEmptyLocals();
        }

        internal RubyTopLevelScope(RubyGlobalScope/*!*/ globalScope, RubyModule scopeModule, RubyModule methodLookupModule, 
            RubyObject/*!*/ selfObject) {
            Assert.NotNull(globalScope, selfObject);

            // RubyScope:
            _top = this;
            _selfObject = selfObject;
            _methodAttributes = RubyMethodAttributes.PrivateInstance;

            // RubyTopLevelScope:
            _globalScope = globalScope;
            _context = globalScope.Context;
            _wrappingModule = scopeModule;
            _methodLookupModule = methodLookupModule;
        }

        #region Factories

        internal static RubyTopLevelScope/*!*/ CreateTopLevelScope(Scope/*!*/ globalScope, RubyContext/*!*/ context, bool isMain) {
            RubyGlobalScope rubyGlobalScope = context.InitializeGlobalScope(globalScope, false, false);

            RubyTopLevelScope scope = new RubyTopLevelScope(rubyGlobalScope, null, null, rubyGlobalScope.MainObject);
            scope.IsMain = isMain;
            if (isMain) {
                context.MainGlobalScope = rubyGlobalScope;
                scope.SetDebugName("top-main");
                context.ObjectClass.SetConstant("TOPLEVEL_BINDING", new Binding(scope) { SourcePath = "<main>", SourceLine = 0 });
                if (context.RubyOptions.RequirePaths != null) {
                    bool first = true;
                    // a warm-start worker loaded these into this very scope ahead of time
                    int loaded = context.TakeWarmLoadedRequirePaths();
                    foreach (var path in context.RubyOptions.RequirePaths) {
                        if (loaded > 0) {
                            loaded--;
                            first = false;
                            continue;
                        }
                        // the first is the core prelude (gem_prelude.rb or ruby4.rb)
                        if (first && (path == "gem_prelude.rb" || path == "ruby4.rb")) {
                            context.Loader.LoadCorePrelude(globalScope, rubyGlobalScope.MainObject, path);
                        } else {
                            context.Loader.LoadFile(globalScope, rubyGlobalScope.MainObject, MutableString.Create(path, RubyEncoding.UTF8), LoadFlags.Require);
                        }
                        first = false;
                    }
                }
            } else {
                scope.SetDebugName("top-required");
            }

            return scope;
        }

        internal static RubyTopLevelScope/*!*/ CreateHostedTopLevelScope(Scope/*!*/ globalScope, RubyContext/*!*/ context, bool bindGlobals) {
            RubyGlobalScope rubyGlobalScope = context.InitializeGlobalScope(globalScope, true, bindGlobals);

            // Reuse existing top-level scope if available:
            RubyTopLevelScope scope = rubyGlobalScope.TopLocalScope;
            if (scope == null) {
                scope = new RubyTopLevelScope(
                    rubyGlobalScope, null, bindGlobals ? rubyGlobalScope.MainSingleton : null, rubyGlobalScope.MainObject
                );

                scope.SetDebugName(bindGlobals ? "top-level-bound" : "top-level");
                rubyGlobalScope.SetTopLocalScope(scope);
            } else {
                // If we reuse a local scope from previous execution all local variables are accessed dynamically.
                // Therefore we shouldn't have any new static local variables.
            }

            return scope;
        }

        [ThreadStatic]
        private static RubyModule _pendingWrapModule;

        /// <summary>
        /// The module Kernel#load was handed as its `wrap` argument (Ruby 3.1 lets it be a Module
        /// rather than only true). Kernel#load sets it around the call into the loader and the
        /// factory below takes it; it is per-thread and read once because #load is re-entrant and
        /// the value only applies to the one file about to run.
        /// </summary>
        public static RubyModule PendingWrapModule {
            get { return _pendingWrapModule; }
            set { _pendingWrapModule = value; }
        }

        internal static RubyTopLevelScope/*!*/ CreateWrappedTopLevelScope(Scope/*!*/ globalScope, RubyContext/*!*/ context) {
            RubyGlobalScope rubyGlobalScope = context.InitializeGlobalScope(globalScope, false, false);

            // load(path, true) wraps the file in a fresh anonymous module; load(path, mod) wraps it
            // in that very module, so its constants and methods are reachable through it afterwards.
            RubyModule module = _pendingWrapModule;
            _pendingWrapModule = null;
            module = module ?? context.CreateModule(null, null, null, null, null, null, null, ModuleRestrictions.None);
            RubyObject mainObject = new RubyObject(context.ObjectClass);

            // MRI's self there is a clone of main extended with the wrap module, so the modules
            // main has been extended with come after it.
            RubyModule[] mixins = new[] { module };
            object toplevelBinding;
            if (context.ObjectClass.TryGetConstant(null, "TOPLEVEL_BINDING", out toplevelBinding) && toplevelBinding is Binding) {
                RubyClass mainClass = context.GetImmediateClassOf(((Binding)toplevelBinding).SelfObject);
                if (mainClass.IsSingletonClass) {
                    var list = new List<RubyModule>(mixins);
                    foreach (RubyModule mixin in mainClass.GetMixins()) {
                        if (!list.Contains(mixin)) {
                            list.Add(mixin);
                        }
                    }
                    mixins = list.ToArray();
                }
            }
            context.GetOrCreateMainSingleton(mainObject, mixins);

            RubyTopLevelScope scope = new RubyTopLevelScope(rubyGlobalScope, module, null, mainObject);
            scope.IsMain = false;
            scope.SetDebugName("top-level-wrapped");

            return scope;
        }

        #endregion
    }
}

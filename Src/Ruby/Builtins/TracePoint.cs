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
using System.Threading;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {

    [Flags]
    public enum TraceEvents {
        None = 0,
        Line = 0x1,
        Class = 0x2,
        End = 0x4,
        Call = 0x8,
        Return = 0x10,
        CCall = 0x20,
        CReturn = 0x40,
        Raise = 0x80,
        BCall = 0x100,
        BReturn = 0x200,
        ThreadBegin = 0x400,
        ThreadEnd = 0x800,
        FiberSwitch = 0x1000,
        ScriptCompiled = 0x2000,
        Rescue = 0x4000,
        All = 0x7fff,
    }

    /// <summary>
    /// What a hook is told about the event it is running for. MRI keeps one of these per thread
    /// (ec->trace_arg) while a hook runs; TracePoint's attribute readers answer from it and raise
    /// "access from outside" when there is none.
    /// </summary>
    public sealed class TraceEventInfo {
        public TraceEvents Event;
        public RubyContext/*!*/ Context;
        public RubyScope Scope;
        public object Self;
        public string Path;
        public int Line;
        public object ReturnValue;
        public Exception Exception;
        public MutableString EvalScript;

        // Set for :call/:return (the method the scope belongs to); computed from the scope otherwise.
        public string MethodName;
        public string CalleeName;
        public RubyModule DefinedClass;
        public RubyMemberInfo Method;
        public bool MethodResolved;
    }

    /// <summary>
    /// TracePoint. Event delivery is kept free when nothing is traced: emitted code and the runtime
    /// test <see cref="ActiveEvents"/> - the union of the events of every enabled TracePoint - before
    /// calling in here, and only then build a <see cref="TraceEventInfo"/>.
    /// </summary>
    public sealed class TracePoint {
        /// <summary>
        /// Union of the events of all enabled TracePoints in the process (all runtimes).
        /// Read by emitted code, hence public.
        /// </summary>
        public static int ActiveEvents;

        /// <summary>
        /// Events the runtime itself wants delivered although no TracePoint asked for them: the
        /// objspace library's allocation tracing takes the location of each allocation from the
        /// :line hooks (see <see cref="IronRuby.Runtime.ObjectTracking"/>).
        /// </summary>
        private static int _forcedEvents;

        private static readonly object _lock = new object();
        private static TracePoint[] _enabledTracePoints = new TracePoint[0];

        // The event whose hook is running on this thread; while it is set no other event is
        // delivered on the thread (MRI suspends tracing in hooks), unless allow_reentry clears it.
        [ThreadStatic]
        private static TraceEventInfo _currentEvent;

        private readonly RubyContext/*!*/ _context;
        private readonly TraceEvents _events;
        private readonly Proc/*!*/ _handler;
        private bool _enabled;
        private Thread _targetThread;
        private TraceTarget _target;

        // A hook of Kernel#set_trace_func or Thread#set_trace_func/add_trace_func: _handler is called
        // with MRI's six arguments rather than with the TracePoint.
        private readonly bool _isTraceFunc;

        /// <summary>The events set_trace_func reports (MRI's RUBY_EVENT_ALL).</summary>
        public const TraceEvents TraceFuncEvents =
            TraceEvents.Line | TraceEvents.Class | TraceEvents.End | TraceEvents.Call | TraceEvents.Return |
            TraceEvents.CCall | TraceEvents.CReturn | TraceEvents.Raise;

        public TracePoint(RubyContext/*!*/ context, TraceEvents events, Proc/*!*/ handler) {
            Assert.NotNull(context, handler);
            _context = context;
            _events = events;
            _handler = handler;
        }

        private TracePoint(RubyContext/*!*/ context, Proc/*!*/ traceFunc, Thread thread)
            : this(context, TraceFuncEvents, traceFunc) {
            _isTraceFunc = true;
            _targetThread = thread;
        }

        public RubyContext/*!*/ Context { get { return _context; } }
        public TraceEvents Events { get { return _events; } }
        public bool IsEnabled { get { return _enabled; } }
        public Thread TargetThread { get { return _targetThread; } set { _targetThread = value; } }
        public bool HasTarget { get { return _target != null; } }

        public static TraceEventInfo CurrentEvent {
            get { return _currentEvent; }
            set { _currentEvent = value; }
        }

        #region Enabling

        public void Enable() {
            lock (_lock) {
                if (_enabled) {
                    return;
                }
                _enabled = true;
                var list = new List<TracePoint>(_enabledTracePoints);
                // MRI runs the most recently enabled hook first.
                list.Insert(0, this);
                _enabledTracePoints = list.ToArray();
                UpdateActiveEvents();
            }
        }

        public void EnableForTarget(TraceTarget/*!*/ target) {
            lock (_lock) {
                _target = target;
            }
            Enable();
        }

        public void Disable() {
            lock (_lock) {
                _target = null;
                _targetThread = null;
                if (!_enabled) {
                    return;
                }
                _enabled = false;
                var list = new List<TracePoint>(_enabledTracePoints);
                list.Remove(this);
                _enabledTracePoints = list.ToArray();
                UpdateActiveEvents();
            }
        }

        /// <summary>
        /// Switches <paramref name="events"/> on or off for the runtime's own sake. Only used for
        /// events that need no per-TracePoint setup (:line); the hooks then run with no TracePoint
        /// to deliver to, which costs a walk of an empty list.
        /// </summary>
        public static void ForceEvents(TraceEvents events, bool enable) {
            lock (_lock) {
                if (enable) {
                    _forcedEvents |= (int)events;
                } else {
                    _forcedEvents &= ~(int)events;
                }

                int active = _forcedEvents;
                foreach (var tp in _enabledTracePoints) {
                    active |= (int)tp._events;
                }
                ActiveEvents = active;
            }
        }

        private void UpdateActiveEvents() {
            int events = _forcedEvents;
            foreach (var tp in _enabledTracePoints) {
                events |= (int)tp._events;
            }
            ActiveEvents = events;

            // :c_call/:c_return are compiled into the call-site rules of library methods only while
            // some TracePoint wants them (see RubyLibraryMethodInfo), so switching them on or off
            // throws away every rule bound so far.
            bool cTracing = (events & (int)(TraceEvents.CCall | TraceEvents.CReturn)) != 0;
            if (cTracing != CCallTracing) {
                CCallTracing = cTracing;
                using (_context.ClassHierarchyLocker()) {
                    _context.BasicObjectClass.AllDependentMethodsUpdated("TracePoint c_call");
                }
            }
        }

        /// <summary>
        /// Whether rules for calls to library methods report :c_call and :c_return.
        /// </summary>
        [CLSCompliant(false)] // a volatile field is not CLS-compliant
        public static volatile bool CCallTracing;

        /// <summary>
        /// set_trace_func: removes the trace funcs installed for <paramref name="thread"/> (null: the
        /// ones for every thread, Kernel#set_trace_func's) and installs <paramref name="traceFunc"/>
        /// unless it is null.
        /// </summary>
        public static void SetTraceFunc(RubyContext/*!*/ context, Proc traceFunc, Thread thread) {
            foreach (var tp in _enabledTracePoints) {
                if (tp._isTraceFunc && tp._context == context && tp._targetThread == thread) {
                    tp.Disable();
                }
            }
            if (traceFunc != null) {
                AddTraceFunc(context, traceFunc, thread);
            }
        }

        /// <summary>Thread#add_trace_func: another hook, run before the ones already there.</summary>
        public static void AddTraceFunc(RubyContext/*!*/ context, Proc/*!*/ traceFunc, Thread thread) {
            // The constructor sets the thread, so the hook is published (under the lock) already
            // limited to it; a trace func is never enabled again once disabled.
            new TracePoint(context, traceFunc, thread).Enable();
        }

        #endregion

        #region Delivery

        public static void Deliver(TraceEventInfo/*!*/ info) {
            if (RubyUtils.IsReclaimingFiber) {
                return;
            }
            if (_currentEvent != null) {
                return;
            }

            // a hook can keep the scope (TracePoint#binding): it is not reused for another block call
            if (info.Scope != null) {
                info.Scope.MarkCaptured();
            }

            var tracePoints = _enabledTracePoints;
            Thread thread = null;

            // MRI calls the global hooks before the ones enabled for a target.
            for (int pass = 0; pass < 2; pass++) {
                foreach (var tp in tracePoints) {
                    if ((tp._events & info.Event) == 0 || tp._context != info.Context || (tp._target != null) != (pass == 1)) {
                        continue;
                    }

                    if (tp._targetThread != null && tp._targetThread != (thread ?? (thread = RubyUtils.CurrentRubyThread))) {
                        continue;
                    }

                    var target = tp._target;
                    if (target != null && !target.Matches(info)) {
                        continue;
                    }

                    // an earlier hook may have disabled this one
                    if (!tp._enabled) {
                        continue;
                    }

                    tp.Call(info);
                }
            }
        }

        private void Call(TraceEventInfo/*!*/ info) {
            // MRI runs the hook with $! cleared and puts it back afterwards.
            Exception savedException = _context.CurrentException;
            _currentEvent = info;
            try {
                _context.CurrentException = null;
                if (_isTraceFunc) {
                    CallTraceFunc(info);
                } else {
                    _handler.Call(null, this);
                }
            } finally {
                _currentEvent = null;
                _context.CurrentException = savedException;
            }
        }

        // MRI's call_trace_func: event, file, line, id, binding, klass. C functions have no binding,
        // and a singleton class is reported as the object it belongs to.
        private void CallTraceFunc(TraceEventInfo/*!*/ info) {
            ResolveMethod(info);
            bool cFunction = (info.Event & (TraceEvents.CCall | TraceEvents.CReturn)) != 0;
            RubyModule module = info.DefinedClass;
            object klass = module;
            if (module != null && module.IsSingletonClass) {
                klass = ((RubyClass)module).SingletonClassOf;
            }

            _handler.Call(null, new object[] {
                MutableString.CreateAscii(TraceFuncEventName(info.Event)),
                info.Path != null ? _context.EncodePath(info.Path) : null,
                ScriptingRuntimeHelpers.Int32ToObject(info.Line),
                info.MethodName != null ? (object)_context.EncodeIdentifier(info.MethodName) : null,
                (!cFunction && info.Scope != null) ? new Binding(info.Scope) : null,
                klass,
            });
        }

        private static string/*!*/ TraceFuncEventName(TraceEvents e) {
            switch (e) {
                case TraceEvents.Line: return "line";
                case TraceEvents.Class: return "class";
                case TraceEvents.End: return "end";
                case TraceEvents.Call: return "call";
                case TraceEvents.Return: return "return";
                case TraceEvents.CCall: return "c-call";
                case TraceEvents.CReturn: return "c-return";
                case TraceEvents.Raise: return "raise";
                default: return "unknown";
            }
        }

        #endregion

        // The files of the core library written in Ruby. In MRI that code is C and produces no
        // events of its own, so neither does it here.
        private static readonly string[] _coreLibraryFiles = { "ruby4.rb", "argf.rb", "enumerator.rb", "set.rb", "thread.rb", "objspace.rb" };

        public static bool IsCoreLibraryPath(string path) {
            if (path == null) {
                return false;
            }
            // ".../ironruby/<file>"
            foreach (var file in _coreLibraryFiles) {
                int separator = path.Length - file.Length - 1;
                if (separator >= 8 && path.EndsWith(file, StringComparison.Ordinal) && (path[separator] == '/' || path[separator] == '\\') &&
                    String.CompareOrdinal(path, separator - 8, "ironruby", 0, 8) == 0) {
                    return true;
                }
            }
            return false;
        }

        #region Event data

        /// <summary>
        /// The method an event happened in: the method itself for :call/:return, the method a
        /// block or a line is written in otherwise; null at top level and in class bodies.
        /// </summary>
        public static void ResolveMethod(TraceEventInfo/*!*/ info) {
            if (info.MethodResolved) {
                return;
            }
            info.MethodResolved = true;

            for (RubyScope scope = info.Scope; scope != null; scope = scope.Parent) {
                switch (scope.Kind) {
                    case ScopeKind.Method:
                        var methodScope = (RubyMethodScope)scope;
                        info.MethodName = methodScope.DefinitionName;
                        info.CalleeName = methodScope.CalleeName;
                        info.DefinedClass = methodScope.DeclaringModule;
                        info.Method = FindMethod(methodScope.DeclaringModule, methodScope.DefinitionName);
                        return;

                    case ScopeKind.BlockMethod:
                        var method = ((RubyBlockScope)scope).BlockFlowControl.Proc.Method;
                        info.MethodName = info.CalleeName = method.DefinitionName;
                        info.DefinedClass = method.DeclaringModule;
                        info.Method = method;
                        return;

                    case ScopeKind.TopLevel:
                    case ScopeKind.Module:
                        return;
                }
            }
        }

        private static RubyMemberInfo FindMethod(RubyModule/*!*/ module, string/*!*/ name) {
            RubyMemberInfo method;
            using (module.Context.ClassHierarchyLocker()) {
                if (module.TryGetDefinedMethod(name, out method)) {
                    return method;
                }
            }
            return null;
        }

        #endregion

        #region Hooks called from the runtime and emitted code

        internal static void OnMethodCall(RubyMethodScope/*!*/ scope) {
            if (_currentEvent != null) {
                return;
            }

            var info = new TraceEventInfo {
                Event = TraceEvents.Call,
                Context = scope.RubyContext,
                Scope = scope,
                Self = scope.SelfObject,
            };
            ResolveMethod(info);
            SetMethodLocation(info, false);
            if (!IsCoreLibraryPath(info.Path)) {
                if ((ActiveEvents & (int)TraceEvents.Call) != 0) {
                    Deliver(info);
                }
                return;
            }

            // A method of the core library written in Ruby is a C function in MRI: calling it from a
            // program is a :c_call, reported at the caller's location. Its calls to others aren't.
            if ((ActiveEvents & (int)TraceEvents.CCall) != 0 && !_inLibraryCallEvent) {
                info.Event = TraceEvents.CCall;
                info.Path = null;
                _inLibraryCallEvent = true;
                try {
                    RubyArray trace = RubyExceptionData.CreateRawBacktrace(info.Context);
                    if (trace != null && trace.Count > 1 && IsCoreLibraryPath(ParseFrame(trace[0], out info.Line))) {
                        info.Path = ParseFrame(trace[1], out info.Line);
                    }
                } catch (Exception) {
                    info.Path = null;
                } finally {
                    _inLibraryCallEvent = false;
                }
                if (info.Path != null && !IsCoreLibraryPath(info.Path)) {
                    Deliver(info);
                }
            }
        }

        // "path:line:in ..." => path, line
        private static string ParseFrame(object frameObject, out int line) {
            line = 0;
            string frame = frameObject.ToString();
            int inIndex = frame.LastIndexOf(":in ", StringComparison.Ordinal);
            if (inIndex > 0) {
                frame = frame.Substring(0, inIndex);
            }
            int colon = frame.LastIndexOf(':');
            if (colon > 0 && Int32.TryParse(frame.Substring(colon + 1), out line)) {
                return frame.Substring(0, colon);
            }
            return null;
        }

        private static void SetMethodLocation(TraceEventInfo/*!*/ info, bool end) {
            var rubyMethod = info.Method as RubyMethodInfo;
            if (rubyMethod != null && rubyMethod.Document != null) {
                info.Path = rubyMethod.Document.FileName;
                info.Line = end ? rubyMethod.SourceSpan.End.Line : rubyMethod.SourceSpan.Start.Line;
            }
        }

        internal static void OnBlockCall(RubyBlockScope/*!*/ scope) {
            if (_currentEvent != null) {
                return;
            }

            var dispatcher = scope.BlockFlowControl.Proc.Dispatcher;
            if (IsCoreLibraryPath(dispatcher.SourcePath)) {
                return;
            }

            // MRI: a method defined by define_method reports :call, then :b_call for its block.
            if (scope.Kind == ScopeKind.BlockMethod && (ActiveEvents & (int)TraceEvents.Call) != 0) {
                var info = new TraceEventInfo {
                    Event = TraceEvents.Call,
                    Context = scope.RubyContext,
                    Scope = scope,
                    Self = scope.SelfObject,
                    Path = dispatcher.SourcePath,
                    Line = dispatcher.SourceLine,
                };
                ResolveMethod(info);
                Deliver(info);
            }

            if ((ActiveEvents & (int)TraceEvents.BCall) != 0) {
                Deliver(new TraceEventInfo {
                    Event = TraceEvents.BCall,
                    Context = scope.RubyContext,
                    Scope = scope,
                    Self = scope.SelfObject,
                    Path = dispatcher.SourcePath,
                    Line = dispatcher.SourceLine,
                });
            }
        }

        internal static void OnLine(RubyScope scope, string path, int line) {
            if (scope == null || _currentEvent != null) {
                return;
            }

            Deliver(new TraceEventInfo {
                Event = TraceEvents.Line,
                Context = scope.RubyContext,
                Scope = scope,
                Self = scope.SelfObject,
                Path = path,
                Line = line,
            });
        }

        internal static void OnReturn(RubyScope scope, object value, string path, int line) {
            if (scope == null || _currentEvent != null) {
                return;
            }

            if (scope is RubyBlockScope) {
                DeliverReturn(TraceEvents.BReturn, scope, value, path, line);
                // define_method's block: :b_return, then :return of the method
                if (scope.Kind == ScopeKind.BlockMethod) {
                    DeliverReturn(TraceEvents.Return, scope, value, path, line);
                }
            } else {
                DeliverReturn(scope.Kind == ScopeKind.Method ? TraceEvents.Return : TraceEvents.End, scope, value, path, line);
            }
        }

        private static void DeliverReturn(TraceEvents e, RubyScope/*!*/ scope, object value, string path, int line) {
            if ((ActiveEvents & (int)e) == 0) {
                return;
            }

            var info = new TraceEventInfo {
                Event = e,
                Context = scope.RubyContext,
                Scope = scope,
                Self = scope.SelfObject,
                Path = path,
                Line = line,
                ReturnValue = value,
            };
            if (e == TraceEvents.Return) {
                ResolveMethod(info);
            }
            Deliver(info);
        }

        internal static void OnClass(RubyScope scope, string path, int line) {
            if (scope == null || _currentEvent != null) {
                return;
            }

            Deliver(new TraceEventInfo {
                Event = TraceEvents.Class,
                Context = scope.RubyContext,
                Scope = scope,
                Self = scope.SelfObject,
                Path = path,
                Line = line,
            });
        }

        public static void OnException(TraceEvents e, RubyScope scope, RubyContext/*!*/ context, Exception/*!*/ exception) {
            if (_currentEvent != null) {
                return;
            }

            var info = new TraceEventInfo {
                Event = e,
                Context = context,
                Scope = scope,
                Self = scope != null ? scope.SelfObject : null,
                Exception = exception,
            };
            SetCallerLocation(info);
            Deliver(info);
        }

        /// <summary>
        /// :c_call / :c_return of a library method, reported from the call-site rule. The location is
        /// the caller's, as in MRI.
        /// </summary>
        internal static void OnLibraryCall(TraceEvents e, RubyScope scope, object self, RubyMemberInfo/*!*/ method, string/*!*/ name,
            object returnValue) {

            if (_currentEvent != null || _inLibraryCallEvent || (ActiveEvents & (int)e) == 0) {
                return;
            }

            // MRI's TracePoint methods are written in Ruby (trace_point.rb) and report nothing
            if (method.DeclaringModule.Name == "TracePoint") {
                return;
            }

            var info = new TraceEventInfo {
                Event = e,
                Context = method.Context,
                Scope = scope,
                Self = self,
                ReturnValue = returnValue,
                MethodName = name,
                CalleeName = name,
                DefinedClass = method.DeclaringModule,
                Method = method,
                MethodResolved = true,
            };

            // finding the caller's location calls library methods of its own
            _inLibraryCallEvent = true;
            try {
                SetCallerLocation(info);
            } finally {
                _inLibraryCallEvent = false;
            }
            if (!IsCoreLibraryPath(info.Path)) {
                Deliver(info);
            }
        }

        [ThreadStatic]
        private static bool _inLibraryCallEvent;

        public static void OnThread(TraceEvents e, RubyContext/*!*/ context, Thread/*!*/ thread) {
            if (_currentEvent != null) {
                return;
            }

            Deliver(new TraceEventInfo {
                Event = e,
                Context = context,
                Self = thread,
            });
        }

        public static void OnScriptCompiled(RubyScope scope, RubyContext/*!*/ context, MutableString/*!*/ script) {
            if (_currentEvent != null) {
                return;
            }

            var info = new TraceEventInfo {
                Event = TraceEvents.ScriptCompiled,
                Context = context,
                Scope = scope,
                Self = scope != null ? scope.SelfObject : null,
                EvalScript = script,
            };
            SetCallerLocation(info);
            Deliver(info);
        }

        // The events raised from library code (raise, eval) are not given their location by the
        // compiler; the innermost Ruby frame of the backtrace is where MRI reports them.
        private static void SetCallerLocation(TraceEventInfo/*!*/ info) {
            try {
                RubyArray trace = RubyExceptionData.CreateRawBacktrace(info.Context);
                if (trace == null || trace.Count == 0) {
                    return;
                }
                string frame = trace[0].ToString();
                int inIndex = frame.LastIndexOf(":in ", StringComparison.Ordinal);
                if (inIndex > 0) {
                    frame = frame.Substring(0, inIndex);
                }
                int colon = frame.LastIndexOf(':');
                int line;
                if (colon > 0 && Int32.TryParse(frame.Substring(colon + 1), out line)) {
                    info.Path = frame.Substring(0, colon);
                    info.Line = line;
                }
            } catch (Exception) {
                // the location is decoration; never let finding it break the event
            }
        }

        #endregion
    }

    /// <summary>
    /// The code a TracePoint enabled with target: is restricted to: a method body or a block, and
    /// the blocks written in it. Events of other methods it calls are not included.
    /// </summary>
    public sealed class TraceTarget {
        private readonly RubyModule _methodModule;
        private readonly string _methodName;
        private readonly BlockDispatcher _block;
        private readonly int _line;

        public TraceTarget(RubyModule/*!*/ methodModule, string/*!*/ methodName, int line) {
            _methodModule = methodModule;
            _methodName = methodName;
            _line = line;
        }

        public TraceTarget(BlockDispatcher/*!*/ block, int line) {
            _block = block;
            _line = line;
        }

        public bool Matches(TraceEventInfo/*!*/ info) {
            if (_line != 0 && (info.Event != TraceEvents.Line || info.Line != _line)) {
                return false;
            }

            // The frame the event happened in, then the frames of the blocks it is lexically nested in.
            for (RubyScope scope = info.Scope; scope != null; scope = scope.Parent) {
                switch (scope.Kind) {
                    case ScopeKind.Method:
                        var methodScope = (RubyMethodScope)scope;
                        return _methodName != null && methodScope.DefinitionName == _methodName && methodScope.DeclaringModule == _methodModule;

                    case ScopeKind.Block:
                    case ScopeKind.BlockMethod:
                    case ScopeKind.BlockModule:
                        if (_block != null && ((RubyBlockScope)scope).BlockFlowControl.Proc.Dispatcher == _block) {
                            return true;
                        }
                        break;

                    case ScopeKind.TopLevel:
                    case ScopeKind.Module:
                        return false;
                }
            }
            return false;
        }
    }
}

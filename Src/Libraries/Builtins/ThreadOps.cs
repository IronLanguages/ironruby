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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// Ruby threads are represented by CLR thread objects (System.Threading.Thread).
    ///
    /// Thread#raise, Thread#kill and friends act on another thread cooperatively: the exception is parked
    /// and the target picks it up at its next safe point (see RubyUtils.RaiseAsyncException).
    /// </summary>
    [RubyClass("Thread", Extends = typeof(Thread), Inherits = typeof(object))]
    public static partial class ThreadOps {
        static bool _globalAbortOnException;
        static bool _globalReportOnException = true;

        /// <summary>
        /// The ThreadState enumeration is a flag, and multiple values could be set simultaneously. Also,
        /// there is other state that IronRuby tracks. RubyThreadStatus flattens out the different states
        /// into non-overlapping values.
        /// </summary>
        private enum RubyThreadStatus {
            /// <summary>
            /// Ruby does not expose such a state. However, since IronRuby uses CLR threads, this state can exist for
            /// threads that are not created directly from Ruby code
            /// </summary>
            Unstarted,

            Running,

            Sleeping,

            Completed,

            /// <summary>
            /// If Thread#kill has been called, and the thread is not sleeping
            /// </summary>
            Aborting,

            /// <summary>
            /// An unhandled exception was thrown by the thread
            /// </summary>
            Aborted
        }

        public class RubyThreadInfo {
            // Keyed by the Thread object, not its ManagedThreadId (Largo/ironruby#126): .NET hands a
            // dead thread's id to a new thread once the old Thread object has been collected, and the
            // new thread must start with an info of its own, not the dead one's locals, name, group
            // and fiber flags. The table holds an info only as long as something holds its Thread -
            // a running thread is rooted by the runtime, a finished one by whatever Ruby object still
            // refers to it (Thread#value, #[] on a dead thread keep working) - so nothing leaks.
            private static readonly ConditionalWeakTable<Thread, RubyThreadInfo> _mapping = new ConditionalWeakTable<Thread, RubyThreadInfo>();

            // The infos Threads enumerates (ConditionalWeakTable has no cheap enumeration). An entry
            // whose thread has finished is pruned here; the info itself stays reachable through
            // _mapping for as long as its Thread is. Guarded by _mappingLock.
            private static readonly List<RubyThreadInfo> _registered = new List<RubyThreadInfo>();
            private static readonly object _mappingLock = new object();
            private static int _pruneAt = 64;

            // Lets the runtime's own native waits (DescriptorStream's poll) report the thread as
            // sleeping, the way the waits in this library do by setting Blocked themselves.
            static RubyThreadInfo() {
                RubyUtils.NativeWaitHook = blocked => {
                    var info = FromThread(Thread.CurrentThread);
                    bool was = info._blocked;
                    info._blocked = blocked;
                    return was;
                };
                RubyUtils.ManagedWaitHook = waiting => {
                    var info = FromThread(Thread.CurrentThread);
                    bool was = info._inManagedWait;
                    info._inManagedWait = waiting;
                    return was;
                };
                RubyUtils.AsyncExceptionRedirectHook = ForwardToActiveFiber;
            }

            /// <summary>
            /// An exception parked for this CLR thread while it is not the running fiber of its Ruby
            /// thread - it resumed another fiber and waits for it, its fiber is finishing, or it sits
            /// in the fiber pool - goes on to the fiber that is running.
            /// </summary>
            private static bool ForwardToActiveFiber() {
                if (TryForwardToActiveFiber()) {
                    return true;
                }
                // A fiber thread outside its fiber keeps it parked: FiberCore.Run picks it up once
                // the fiber can take it, and a thread that goes away drops it.
                RubyThreadInfo info = FromThread(Thread.CurrentThread);
                return info.IsFiberWorker && !info.InFiberRun;
            }

            /// <summary>
            /// Moves what is parked for this CLR thread to the running fiber of the Ruby thread it
            /// runs on behalf of (<see cref="RubyUtils.CurrentRubyThread"/>), if that is another one.
            /// </summary>
            internal static bool TryForwardToActiveFiber() {
                Thread current = Thread.CurrentThread;
                Thread active = FromThread(RubyUtils.CurrentRubyThread).ActiveFiberThread;
                return active != null && !ReferenceEquals(active, current) && active.IsAlive && ForwardAsyncException(current, active);
            }

            /// <summary>Moves what is parked for <paramref name="from"/> to <paramref name="to"/>.</summary>
            internal static bool ForwardAsyncException(Thread/*!*/ from, Thread/*!*/ to) {
                if (!RubyUtils.ForwardAsyncException(from, to)) {
                    return false;
                }
                // as RaiseAsyncException does: Kernel#sleep waits on the run signal
                FromThread(to).RunForInterrupt();
                return true;
            }
            private readonly Dictionary<RubySymbol, object> _threadLocalStorage;
            private ThreadGroup _group;
            private readonly Thread _thread;
            // In a wait a Ruby program can see (RubyUtils.BlockingWait), the only kind Thread#status
            // reports as "sleep": a syscall (_blocked), or a managed wait (_inManagedWait), which counts
            // once the CLR has the thread in WaitSleepJoin too. Written only by the thread itself.
            private volatile bool _blocked;
            private volatile bool _inManagedWait;
            private bool _abortOnException;
            private AutoResetEvent _runSignal = new AutoResetEvent(false);
            // Guarded by _sleepLock, which makes "is it sleeping? then wake it" (Run) and "the sleep is
            // over; drop a wake-up it did not take" (EndSleep) happen one after the other.
            private bool _isSleeping;
            private readonly object/*!*/ _sleepLock = new object();

            private RubyThreadInfo(Thread thread) {
                _threadLocalStorage = new Dictionary<RubySymbol, object>();
                _group = ThreadGroup.Default;
                _thread = thread;
                ReportOnException = _globalReportOnException;
            }

            // The calling thread's own info. Nearly every lookup is for the current thread - Thread.current,
            // Thread#[] on it, Mutex, Monitor - and a thread's entry in _mapping is never replaced while the thread runs, so
            // once looked up it can be answered without the global lock.
            [ThreadStatic]
            private static RubyThreadInfo _current;

            internal static RubyThreadInfo FromThread(Thread t) {
                RubyThreadInfo current = _current;
                if (current != null && ReferenceEquals(current._thread, t)) {
                    return current;
                }
                return Lookup(t);
            }

            /// <summary>The info of the calling CLR thread (for a fiber, the fiber's own thread).</summary>
            internal static RubyThreadInfo Current {
                get { return _current ?? Lookup(Thread.CurrentThread); }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static RubyThreadInfo Lookup(Thread t) {
                RubyThreadInfo result;
                lock (_mappingLock) {
                    if (!_mapping.TryGetValue(t, out result)) {
                        result = new RubyThreadInfo(t);
                        _mapping.Add(t, result);
                        _registered.Add(result);
                        if (_registered.Count >= _pruneAt) {
                            PruneFinished();
                            _pruneAt = Math.Max(64, _registered.Count * 2);
                        }
                    }
                }
                if (ReferenceEquals(t, Thread.CurrentThread) && ReferenceEquals(result._thread, t)) {
                    _current = result;
                }
                return result;
            }

            /// <summary>
            /// Drops the infos of threads that have finished from <see cref="_registered"/>, which would
            /// otherwise keep every Thread the process ever made alive. A thread that has not been
            /// started yet stays: it is about to run. Caller holds _mappingLock.
            /// </summary>
            private static void PruneFinished() {
                _registered.RemoveAll(info => (info._thread.ThreadState & System.Threading.ThreadState.Stopped) != 0);
            }

            /// <summary>
            /// Drops the info of a Ruby thread whose block is over from <see cref="_registered"/>. Left for the
            /// next prune - up to 64 threads later, or twice as many as were alive at the last one - it kept the
            /// finished Thread reachable, so the Thread was promoted through the GCs in between and then stayed,
            /// with the CLR's record of the dead thread, until a gen-2 collection.
            /// </summary>
            internal static void Unregister(RubyThreadInfo/*!*/ info) {
                lock (_mappingLock) {
                    // from the end: a thread that finishes is usually one of the latest
                    int index = _registered.LastIndexOf(info);
                    if (index >= 0) {
                        _registered.RemoveAt(index);
                    }
                }
            }

            /// <summary>Registers the calling thread; cheap once it is.</summary>
            internal static void RegisterCurrentThread() {
                if (_current == null) {
                    Lookup(Thread.CurrentThread);
                }
            }

            internal object this[RubySymbol/*!*/ key] {
                get {
                    lock (_threadLocalStorage) {
                        object result;
                        if (!_threadLocalStorage.TryGetValue(key, out result)) {
                            result = null;
                        }
                        return result;
                    }
                }
                set {
                    lock (_threadLocalStorage) {
                        if (value == null) {
                            _threadLocalStorage.Remove(key);
                        } else {
                            _threadLocalStorage[key] = value;
                        }
                    }
                }
            }

            /// <summary>Forgets every fiber-local, for a fiber thread that goes back to the pool.</summary>
            internal void ClearLocals() {
                lock (_threadLocalStorage) {
                    _threadLocalStorage.Clear();
                }
            }

            internal bool HasKey(RubySymbol/*!*/ key) {
                lock (_threadLocalStorage) {
                    return _threadLocalStorage.ContainsKey(key);
                }
            }

            internal RubyArray GetKeys() {
                lock (_threadLocalStorage) {
                    RubyArray result = new RubyArray(_threadLocalStorage.Count);
                    foreach (RubySymbol key in _threadLocalStorage.Keys) {
                        // the runtime's own bookkeeping (the Ruby-level Fiber's) is not the program's
                        if (!key.ToString().StartsWith("__ir_", StringComparison.Ordinal)) {
                            result.Add(key);
                        }
                    }
                    return result;
                }
            }

            internal ThreadGroup Group {
                get {
                    return _group;
                }
                set {
                    Interlocked.Exchange(ref _group, value);
                }
            }

            internal Thread Thread {
                get {
                    return _thread;
                }
            }

            internal Exception Exception { get; set; }
            internal object Result { get; set; }

            /// <summary>
            /// The Ractor the thread belongs to: the one that started it, or the one it is the main
            /// thread of. Null for the main Ractor's threads.
            /// </summary>
            internal RubyRactor Ractor { get; set; }
            internal bool CreatedFromRuby { get; set; }

            /// <summary>
            /// Set by a thread Ruby created while it runs Ruby code: from just before its block until it has
            /// cleaned up after it. Outside that the CLR reports the thread as WaitSleepJoin while it starts
            /// up and while it is torn down, where MRI's thread is "run".
            /// </summary>
            internal volatile bool RunsRuby;

            /// <summary>
            /// The runtime whose Thread.new made this thread; null for a thread Ruby did not create
            /// (a host's, the main thread). Each RubyContext is a VM of its own, as far as which
            /// threads it has goes.
            /// </summary>
            internal RubyContext Context {
                get {
                    RubyContext context;
                    return (_context != null && _context.TryGetTarget(out context)) ? context : null;
                }
                set { _context = (value != null) ? new WeakReference<RubyContext>(value) : null; }
            }

            // Weak: an info lives as long as its Thread (the main thread's for good), and a strong
            // reference here would keep a runtime the host has let go of alive that long.
            private WeakReference<RubyContext> _context;

            /// <summary>
            /// A CLR thread that has run a fiber of some thread's (ThreadOps.Fiber.cs). It is never a
            /// Ruby thread itself, before, between or after its fibers - Thread.current inside a
            /// fiber is the thread that owns it - so the Ruby thread lists leave it out for good.
            /// </summary>
            internal volatile bool IsFiberWorker;

            /// <summary>
            /// The fiber thread is inside FiberCore.Run - its fiber's code, with interrupts masked
            /// until the fiber's block runs. Outside it (starting up, between two fibers) a
            /// Thread#raise parked for the thread must not be raised there: nothing would catch it.
            /// </summary>
            internal volatile bool InFiberRun;
            internal bool ExitRequested { get; set; }
            internal MutableString Name { get; set; }
            internal bool ReportOnException { get; set; }
            internal bool IsSleeping { get { return _isSleeping; } }

            /// <summary>
            /// The Ruby name of the native blocking call the thread is parked in
            /// ("TCPServer#accept"), or null. A thread sitting in a native call has no CLR
            /// frame that maps back to a Ruby method, so Thread#backtrace would otherwise stop
            /// at the call site; MRI shows the method itself on top. Written only by the thread
            /// it describes, read by any thread.
            /// </summary>
            internal volatile string BlockedLabel;
            /// <summary>
            /// The CLR thread of the fiber this thread is running, for Thread#raise; null before the
            /// thread has run a fiber. Changed by the fiber handing control on (FiberCore.MakeRunning),
            /// under <see cref="FiberSwitchLock"/>.
            /// </summary>
            internal volatile Thread ActiveFiberThread;

            /// <summary>
            /// Makes "find the running fiber and park a Thread#raise for it" (RaiseAsyncException)
            /// and "make another fiber the running one and move what is parked" (FiberCore.MakeRunning)
            /// happen one after the other: the raise is then either parked before the switch, and
            /// travels with control, or after it, for the new fiber - never for the fiber that has
            /// just handed control on, which would pass it along only later, when MRI's thread has
            /// long raised it.
            /// </summary>
            internal readonly object/*!*/ FiberSwitchLock = new object();
            internal FiberCore FiberExecution;

            /// <summary>
            /// A fiber thread parked in the pool between two fibers (ThreadOps.Fiber.cs). It is no
            /// Ruby thread of anybody's while it waits, so the thread lists leave it out.
            /// </summary>
            internal volatile bool IsIdleFiberWorker;

            /// <summary>
            /// MRI's Thread#priority is a small integer clamped to -3..3 that outlives the thread;
            /// the CLR's is a five-valued enum that a dead thread refuses to answer at all. Keep the
            /// Ruby value here and treat the CLR priority as a write-only projection of it.
            /// </summary>
            internal int Priority { get; set; }

            /// <summary>"file:line" of the Thread.new call, which MRI puts in Thread#inspect.</summary>
            internal string Location { get; set; }

            /// <summary>
            /// Set while a thread of a Thread subclass is between allocation and the Thread#initialize
            /// (called by the subclass's #initialize through super) that starts it.
            /// </summary>
            internal ThreadStarter Starter { get; set; }

            // Thread#[] is fiber-local in MRI (and, since every fiber is its own CLR thread here, the
            // dictionary above already is); Thread#thread_variable_get is thread-local, so it needs
            // storage of its own.
            private readonly Dictionary<RubySymbol, object> _threadVariables = new Dictionary<RubySymbol, object>();

            internal object GetThreadVariable(RubySymbol/*!*/ key) {
                lock (_threadVariables) {
                    object result;
                    return _threadVariables.TryGetValue(key, out result) ? result : null;
                }
            }

            internal void SetThreadVariable(RubySymbol/*!*/ key, object value) {
                lock (_threadVariables) {
                    if (value == null) {
                        _threadVariables.Remove(key);
                    } else {
                        _threadVariables[key] = value;
                    }
                }
            }

            internal bool HasThreadVariable(RubySymbol/*!*/ key) {
                lock (_threadVariables) {
                    return _threadVariables.ContainsKey(key);
                }
            }

            internal RubyArray/*!*/ GetThreadVariableKeys() {
                lock (_threadVariables) {
                    RubyArray result = new RubyArray(_threadVariables.Count);
                    foreach (RubySymbol key in _threadVariables.Keys) {
                        result.Add(key);
                    }
                    return result;
                }
            }
            
            internal bool InManagedWait {
                get { return _inManagedWait; }
            }

            internal bool Blocked {
                get {
                    return _blocked;
                }
                set {
                    System.Diagnostics.Debug.Assert(Thread.CurrentThread == _thread);
                    _blocked = value;
                }
            }

            internal bool AbortOnException {
                get {
                    return _abortOnException;
                }
                set {
                    _abortOnException = value;
                }
            }

            internal static RubyThreadInfo[] Threads {
                get {
                    lock (_mappingLock) {
                        PruneFinished();
                        List<RubyThreadInfo> result = new List<RubyThreadInfo>(_registered.Count);
                        foreach (RubyThreadInfo info in _registered) {
                            if (info.Thread.IsAlive && !info.IsIdleFiberWorker) {
                                result.Add(info);
                            }
                        }
                        return result.ToArray();
                    }
                }
            }

            /// <summary>
            /// The Ruby threads of a runtime, as Thread.list and ThreadGroup#list see them:
            /// <see cref="Threads"/> less the threads of another runtime - two ScriptRuntimes in one
            /// process are two VMs, and neither lists the other's threads - and less the CLR threads
            /// that run (or have run, or hold suspended) a fiber of a thread's. A fiber
            /// has a CLR thread of its own here, but in MRI it is part of the thread that owns it, and
            /// Thread.current inside it is that thread.
            /// </summary>
            internal static RubyThreadInfo[]/*!*/ GetRubyThreads(RubyContext/*!*/ context) {
                // The runtime's main thread counts even when no Ruby code has run on it yet
                // (a host that creates the runtime on one thread and runs Ruby on another).
                Thread main = context.MainThread;
                if (main != null && main.IsAlive) {
                    FromThread(main);
                }
                RubyThreadInfo[] all = Threads;
                var result = new List<RubyThreadInfo>(all.Length);
                foreach (RubyThreadInfo info in all) {
                    if (info.IsFiberWorker) {
                        continue;
                    }
                    // The runtime's main thread is always its own, even when another runtime's Thread.new
                    // made it; any other thread belongs to the runtime that made it, or, made by none (a
                    // host's), to the one asking from it now.
                    RubyContext owner = info.Context;
                    if (info.Thread != context.MainThread && (owner != null ? owner != context : info.Thread != Thread.CurrentThread)) {
                        continue;
                    }
                    FiberCore fiber = info.FiberExecution;
                    if (fiber == null || fiber.Owner == info.Thread) {
                        result.Add(info);
                    }
                }
                return result.ToArray();
            }

            /// <summary>
            /// The Ruby threads of the Ractor the current thread belongs to: what Thread.list and
            /// ThreadGroup#list answer, which in MRI list only the asking Ractor's living threads.
            /// </summary>
            internal static RubyThreadInfo[]/*!*/ GetRactorThreads(RubyContext/*!*/ context) {
                var ractor = Current.Ractor;
                return Array.FindAll(GetRubyThreads(context), info => info.Ractor == ractor);
            }

            /// <summary>
            /// We do not use Thread.Sleep here as another thread can call Thread#wakeup/Thread#run. Instead, we use our own
            /// lock which can be signalled from another thread.
            /// </summary>
            internal void Sleep() {
                // A Thread#kill/#raise that landed just before we got here parked its exception but
                // could not interrupt us - we were not waiting yet. Deliver it instead of sleeping
                // on, which would never end.
                RubyUtils.CheckAsyncException();
                bool wasWaiting = _inManagedWait;
                bool wasSleeping = BeginSleep();
                try {
                    // One parked since that look found no sleep to end (RunForInterrupt); one parked
                    // from now on does.
                    RubyUtils.CheckAsyncException();
                    _inManagedWait = true;
                    try {
                        _runSignal.WaitOne();
                    } catch (ThreadInterruptedException) {
                        // Thread#kill / Thread#raise nudged us: deliver the parked exception. If there is none
                        // the interrupt was spurious and the sleep simply ends (MRI's sleep is also allowed to
                        // return early). The RunForInterrupt that follows the interrupt finds the exception
                        // still parked or not: if it does, the end of the sleep drops its wake-up.
                        _inManagedWait = wasWaiting;
                        RubyUtils.TranslateThreadInterrupt();
                    }
                } finally {
                    EndSleep(wasSleeping);
                    _inManagedWait = wasWaiting;
                }
            }

            /// <summary>
            /// Same as Sleep() but with a timeout; returns true if the full timeout elapsed.
            /// </summary>
            internal bool Sleep(int milliseconds) {
                RubyUtils.CheckAsyncException();
                bool wasWaiting = _inManagedWait;
                bool wasSleeping = BeginSleep();
                try {
                    RubyUtils.CheckAsyncException();
                    _inManagedWait = true;
                    try {
                        return _runSignal.WaitOne(milliseconds);
                    } catch (ThreadInterruptedException) {
                        _inManagedWait = wasWaiting;
                        RubyUtils.TranslateThreadInterrupt();
                        return false;
                    }
                } finally {
                    EndSleep(wasSleeping);
                    _inManagedWait = wasWaiting;
                }
            }

            // Answers whether the thread was sleeping already: a trap handler run by a sleep's safe point
            // may sleep in its turn.
            private bool BeginSleep() {
                using (new RubyUtils.UninterruptedLock(_sleepLock)) {
                    bool wasSleeping = _isSleeping;
                    _isSleeping = true;
                    return wasSleeping;
                }
            }

            /// <summary>
            /// Ends a sleep, and drops a Run() it did not take. Thread#raise and Thread#kill interrupt
            /// the thread and then Run() it, for a sleep the interrupt cannot reach yet; a Run() that
            /// came after the interrupt had ended the sleep stayed latched, and the thread's next sleep
            /// returned at once - for a fiber thread from the pool, the sleep of another thread's fiber.
            /// MRI's Thread#wakeup only wakes a thread that is sleeping, and a later sleep is not cut
            /// short by it either.
            /// </summary>
            private void EndSleep(bool wasSleeping) {
                using (new RubyUtils.UninterruptedLock(_sleepLock)) {
                    _isSleeping = wasSleeping;
                    if (!wasSleeping) {
                        _runSignal.Reset();
                    }
                }
            }

            internal void Run() {
                using (new RubyUtils.UninterruptedLock(_sleepLock)) {
                    if (_isSleeping) {
                        _runSignal.Set();
                    }
                }
            }

            /// <summary>
            /// The Run() that follows a Thread#raise / Thread#kill parked for this thread, for a sleep the
            /// interrupt could not reach (RubyUtils.NudgeThread interrupts only a thread that is already
            /// waiting). Only while the exception is still parked: once the thread has taken it, the
            /// sleep it ended is over, and a sleep the thread has gone into since - after rescuing the
            /// exception - is not this raise's to end.
            /// </summary>
            internal void RunForInterrupt() {
                using (new RubyUtils.UninterruptedLock(_sleepLock)) {
                    if (_isSleeping && RubyUtils.HasPendingAsyncException(_thread)) {
                        _runSignal.Set();
                    }
                }
            }

            /// <summary>
            /// Like Run(), but signals even if the thread has not reached its wait yet. ConditionVariable
            /// registers a waiter before it releases the mutex, so a #signal can arrive first.
            /// </summary>
            internal void Wake() {
                _runSignal.Set();
            }

            /// <summary>
            /// Drops a Wake() the thread's sleep did not consume - one that came after a timeout ran
            /// out or an exception ended the wait - so that it does not cut the next sleep short.
            /// </summary>
            internal void ClearWake() {
                _runSignal.Reset();
            }
        }

        //  declared private instance methods:
        //    initialize
        //  declared protected instance methods:
        //  declared public instance methods:

        private static Exception MakeKeyTypeException(RubyContext/*!*/ context, object key) {
            return RubyExceptions.CreateTypeError("{0} is not a symbol nor a string", context.Inspect(key).ToString());
        }

        /// <summary>
        /// Thread-local keys are Symbols; a String is interned and anything else is given a chance to
        /// convert itself with #to_str (but never #to_sym, which MRI is explicit about).
        /// </summary>
        private static RubySymbol/*!*/ ToKey(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, object key) {
            RubySymbol symbol = key as RubySymbol;
            if (symbol != null) {
                return symbol;
            }

            MutableString str = key as MutableString;
            if (str == null && key != null) {
                str = Protocols.TryCastToString(toStr, key);
            }

            if (str == null) {
                throw MakeKeyTypeException(context, key);
            }

            return context.CreateSymbol(str);
        }

        /// <summary>
        /// Thread#[] and friends are fiber-local in MRI. Every fiber runs on its own CLR thread
        /// here, so "the current fiber's storage" is the current CLR thread's - which stopped
        /// being the same object as Thread.current when Thread.current started answering the
        /// fiber's owning thread. Another thread is addressed by its own storage, as in MRI,
        /// where it is that thread's root fiber's.
        /// </summary>
        private static RubyThreadInfo/*!*/ FiberLocals(Thread/*!*/ self) {
            return self == RubyUtils.CurrentRubyThread ? RubyThreadInfo.Current : RubyThreadInfo.FromThread(self);
        }

        [RubyMethod("[]")]
        public static object GetElement(Thread/*!*/ self, [NotNull]RubySymbol/*!*/ key) {
            return FiberLocals(self)[key];
        }

        [RubyMethod("[]")]
        public static object GetElement(RubyContext/*!*/ context, Thread/*!*/ self, [NotNull]MutableString/*!*/ key) {
            return GetElement(self, context.CreateSymbol(key));
        }

        [RubyMethod("[]")]
        public static object GetElement(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key) {
            return GetElement(self, ToKey(toStr, context, key));
        }

        /// <summary>
        /// Freezing a Thread freezes its local storage: MRI raises FrozenError with a message about
        /// the locals rather than about the thread object.
        /// </summary>
        private static void CheckLocalsNotFrozen(RubyContext/*!*/ context, Thread/*!*/ self) {
            if (context.IsObjectFrozen(self)) {
                throw new FrozenError("can't modify frozen thread locals").SetReceiver(self);
            }
        }

        [RubyMethod("[]=")]
        public static object SetElement(RubyContext/*!*/ context, Thread/*!*/ self, [NotNull]RubySymbol/*!*/ key, object value) {
            CheckLocalsNotFrozen(context, self);
            FiberLocals(self)[key] = value;
            return value;
        }

        [RubyMethod("[]=")]
        public static object SetElement(RubyContext/*!*/ context, Thread/*!*/ self, [NotNull]MutableString/*!*/ key, object value) {
            return SetElement(context, self, context.CreateSymbol(key), value);
        }

        [RubyMethod("[]=")]
        public static object SetElement(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key, object value) {
            return SetElement(context, self, ToKey(toStr, context, key), value);
        }

        [RubyMethod("abort_on_exception")]
        public static object AbortOnException(Thread/*!*/ self) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            return info.AbortOnException;
        }

        [RubyMethod("abort_on_exception=")]
        public static object AbortOnException(Thread/*!*/ self, bool value) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            info.AbortOnException = value;
            return value;
        }

        [RubyMethod("alive?")]
        public static bool IsAlive(Thread/*!*/ self) {
            RubyThreadInfo.RegisterCurrentThread();
            return self.IsAlive;
        }

        /// <summary>
        /// The Ruby stack of this thread, or nil if there is none to report.  Thread#backtrace is
        /// defined in Ruby on top of this so that it can do MRI's start/length slicing; this is only
        /// the part that has to reach into the runtime.
        ///
        /// A thread that has not started or has already finished has no stack and answers nil, as in
        /// MRI.  A running thread answers a snapshot: it does not stop to be read, so by the time the
        /// array is handed back the thread has probably moved on.
        /// </summary>
        [RubyMethod("__native_backtrace__", RubyMethodAttributes.PrivateInstance)]
        public static object NativeBacktrace(RubyContext/*!*/ context, Thread/*!*/ self) {
            RubyThreadInfo.RegisterCurrentThread();
            if (!self.IsAlive) {
                return null;
            }
            RubyArray trace = RubyExceptionData.CreateBacktrace(context, self);
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            string blockedIn = info.IsSleeping ? "Kernel#sleep" : info.BlockedLabel;
            if (trace != null && trace.Count > 0 && blockedIn != null) {
                string caller = trace[0].ToString();
                int label = caller.LastIndexOf(":in ", StringComparison.Ordinal);
                if (label >= 0) {
                    caller = caller.Substring(0, label);
                }
                // the frame of the core method written in Ruby that the thread is blocked in (the prelude's Kernel#sleep)
                // has its label already
                string blockedFrame = caller + ":in '" + blockedIn + "'";
                if (trace[0].ToString() != blockedFrame) {
                    trace.Insert(0, MutableString.CreateMutable(blockedFrame, RubyEncoding.UTF8));
                }
            }
            return trace;
        }

        [RubyMethod("group")]
        public static ThreadGroup Group(Thread/*!*/ self) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            return info.Group;
        }

        /// <summary>
        /// MRI: "#&lt;Thread:0x... file:line status&gt;", with "@name" after the id when the thread has
        /// one.  The string is BINARY unless a name forces a wider encoding, because the file name
        /// is a path and paths have no encoding of their own.
        /// </summary>
        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, Thread/*!*/ self) {
            RubyThreadInfo.RegisterCurrentThread();

            MutableString result = MutableString.CreateMutable(RubyEncoding.Binary);
            result.Append("#<");
            result.Append(context.GetClassDisplayName(self));
            result.Append(':');
            RubyUtils.AppendFormatHexObjectId(result, RubyUtils.GetObjectId(context, self));

            RubyThreadInfo selfInfo = RubyThreadInfo.FromThread(self);
            MutableString name = selfInfo.Name;
            if (name != null) {
                result.Append('@');
                result.Append(name);
            }

            if (selfInfo.Location != null) {
                result.Append(' ');
                result.Append(selfInfo.Location);
            }

            result.Append(' ');

            RubyThreadStatus status = GetStatus(self);
            switch (status) {
                case RubyThreadStatus.Unstarted:
                    result.Append("unstarted");
                    break;
                case RubyThreadStatus.Running:
                    result.Append("run");
                    break;
                case RubyThreadStatus.Sleeping:
                    result.Append("sleep");
                    break;
                case RubyThreadStatus.Aborting:
                    result.Append("aborting");
                    break;
                case RubyThreadStatus.Completed:
                case RubyThreadStatus.Aborted:
                    result.Append("dead");
                    break;
            }

            result.Append('>');
            return result;
        }

        // How long one wait inside Thread#join lasts before the joining thread reaches a safe point.
        private const int JoinSliceMilliseconds = 20;

        /// <summary>
        /// Joins in slices rather than in one CLR Thread.Join. A signal is delivered by running its
        /// trap handler on the main thread at a safe point, and a thread parked in Thread.Join has
        /// none: `trap("TERM") { ... }; server_thread.join` - which is how puma's single mode, and
        /// most servers, wait - never ran its handler, so the process ignored SIGTERM and SIGINT.
        /// Answers whether the thread finished within <paramref name="timeout"/>.
        /// </summary>
        private static bool JoinWithSafePoints(Thread/*!*/ self, int timeout) {
            long deadline = (timeout == Timeout.Infinite) ? Int64.MaxValue : Environment.TickCount64 + timeout;
            while (true) {
                try {
                    long remaining = deadline - Environment.TickCount64;
                    int slice = (int)Math.Max(0, Math.Min(remaining, JoinSliceMilliseconds));
                    bool joined;
                    using (RubyUtils.BlockingWait.Enter()) {
                        joined = self.Join(slice);
                    }
                    if (joined) {
                        return true;
                    }
                    if (remaining <= JoinSliceMilliseconds) {
                        return false;
                    }
                    RubyUtils.CheckAsyncException();
                } catch (ThreadInterruptedException) {
                    RubyUtils.TranslateThreadInterrupt();
                }
            }
        }

        [RubyMethod("join")]
        public static Thread/*!*/ Join(RubyContext/*!*/ context, Thread/*!*/ self) {
            RubyThreadInfo.RegisterCurrentThread();
            RequireOtherThread(context, self);

            JoinWithSafePoints(self, Timeout.Infinite);

            Exception threadException = RubyThreadInfo.FromThread(self).Exception;
            if (threadException != null) {
                throw threadException;
            }

            return self;
        }

        /// <summary>
        /// Thread#join(timeout). A nil timeout means "no timeout", so it cannot go through the
        /// Float conversion the other values do.
        /// </summary>
        [RubyMethod("join")]
        public static Thread Join(RubyContext/*!*/ context, Thread/*!*/ self, object timeout) {
            if (timeout == null) {
                return Join(context, self);
            }
            // MRI's time-interval conversion, not Float(): a String is a TypeError here rather
            // than the ArgumentError that Float("bar") would raise.
            double seconds;
            if (timeout is int) {
                seconds = (int)timeout;
            } else if (timeout is double) {
                seconds = (double)timeout;
            } else if (timeout is System.Numerics.BigInteger) {
                seconds = (double)(System.Numerics.BigInteger)timeout;
            } else {
                throw RubyExceptions.CreateImplicitConversionError(context.GetClassDisplayName(timeout), "Float");
            }
            return Join(context, self, seconds);
        }

        // A thread cannot wait for itself, it would never wake up; and nothing waits for the main
        // thread, whose end is the end of the program.
        private static void RequireOtherThread(RubyContext/*!*/ context, Thread/*!*/ self) {
            if (ReferenceEquals(self, Thread.CurrentThread)) {
                throw new ThreadError("Target thread must not be current thread");
            }
            if (ReferenceEquals(self, context.MainThread)) {
                throw new ThreadError("Target thread must not be main thread");
            }
        }

        public static Thread/*!*/ Join(RubyContext/*!*/ context, Thread/*!*/ self, double seconds) {
            RubyThreadInfo.RegisterCurrentThread();
            RequireOtherThread(context, self);

            if (!(self.ThreadState == ThreadState.AbortRequested || self.ThreadState == ThreadState.Aborted)) {
                double ms = seconds * 1000;
                int timeout = (ms < Int32.MinValue || ms > Int32.MaxValue) ? Timeout.Infinite : (int)ms;
                bool joined = JoinWithSafePoints(self, timeout);
                if (!joined) {
                    return null;
                }
            }

            Exception threadException = RubyThreadInfo.FromThread(self).Exception;
            if (threadException != null) {
                throw threadException;
            }

            return self;
        }

        [RubyMethod("kill")]
        [RubyMethod("exit")]
        [RubyMethod("terminate")]
        public static Thread Kill(Thread/*!*/ self) {
            RubyThreadInfo.RegisterCurrentThread();
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);

            if (!self.IsAlive) {
                return self;
            }

            if (GetStatus(self) == RubyThreadStatus.Sleeping && info.ExitRequested) {
                // Thread must be sleeping in an ensure clause. Wake up the thread and allow ensure clause to complete
                info.Run();
                return self;
            }

            info.ExitRequested = true;
            if (self == Thread.CurrentThread) {
                // No need for the asynchronous machinery - just start unwinding.
                throw new RubyUtils.ThreadExitSignal();
            }

            RubyUtils.ExitThread(self);
            info.RunForInterrupt();
            return self;
        }

        [RubyMethod("key?")]
        public static object HasKey(Thread/*!*/ self, [NotNull]RubySymbol/*!*/ key) {
            return FiberLocals(self).HasKey(key);
        }

        [RubyMethod("key?")]
        public static object HasKey(RubyContext/*!*/ context, Thread/*!*/ self, [NotNull]MutableString/*!*/ key) {
            return HasKey(self, context.CreateSymbol(key));
        }

        [RubyMethod("key?")]
        public static object HasKey(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key) {
            return HasKey(self, ToKey(toStr, context, key));
        }

        [RubyMethod("keys")]
        public static object Keys(RubyContext/*!*/ context, Thread/*!*/ self) {
            return FiberLocals(self).GetKeys();
        }

        #region priority, priority=
        [RubyMethod("priority")]
        public static object Priority(Thread/*!*/ self) {
            RubyThreadInfo.RegisterCurrentThread();
            return RubyThreadInfo.FromThread(self).Priority;
        }

        [RubyMethod("priority=")]
        public static object Priority(Thread/*!*/ self, [DefaultProtocol]int priority) {
            RubyThreadInfo.RegisterCurrentThread();

            // MRI clamps to -3..3 and reports back the clamped value.
            int clamped = priority < -3 ? -3 : (priority > 3 ? 3 : priority);
            RubyThreadInfo.FromThread(self).Priority = clamped;

            try {
                if (clamped <= -2) {
                    self.Priority = ThreadPriority.Lowest;
                } else if (clamped == -1) {
                    self.Priority = ThreadPriority.BelowNormal;
                } else if (clamped == 0) {
                    self.Priority = ThreadPriority.Normal;
                } else if (clamped == 1) {
                    self.Priority = ThreadPriority.AboveNormal;
                } else {
                    self.Priority = ThreadPriority.Highest;
                }
            } catch (ThreadStateException) {
                // The thread has already finished. MRI still remembers the value, so we do too;
                // there is simply nothing left to apply it to.
            }

            return priority;
        }
        #endregion
        #region raise, fail

        private static CallSite<Func<CallSite, object, object>> _exceptionSite;

        private static void RaiseAsyncException(RubyContext/*!*/ context, Thread thread, Exception exception) {
            // MRI delivers the interrupt to the thread's running fiber, and each fiber here is a
            // CLR thread of its own.
            RubyThreadInfo ownerInfo = RubyThreadInfo.FromThread(thread);

            // A thread Thread#kill has been called on dies of the kill, as MRI's does: a raise that
            // comes in behind it is dropped, whether the kill is still parked or already unwinding
            // the thread. Parked, it replaced the kill; in flight, it replaced the ThreadExitSignal
            // and the thread died of the raise, which #join then raised again.
            if (ownerInfo.ExitRequested) {
                return;
            }

            RubyThreadInfo info;
            RubyThreadStatus status;
            using (new RubyUtils.UninterruptedLock(ownerInfo.FiberSwitchLock)) {
                Thread activeFiber = ownerInfo.ActiveFiberThread;
                if (activeFiber != null && activeFiber.IsAlive) {
                    thread = activeFiber;
                }
                info = RubyThreadInfo.FromThread(thread);
                status = GetStatus(thread);
                if (thread == Thread.CurrentThread) {
                    throw exception;
                }

                // rethrow semantics, preserves the backtrace associated with the exception:
                RubyUtils.ParkAsyncException(thread, exception, e => {
                    // MRI raises exception.exception in the target thread
                    var site = RubyUtils.GetCallSite(ref _exceptionSite, context, "exception", 0);
                    return site.Target(site, e) as Exception;
                });
            }
            // Not under the lock: the thread may be waiting for it, and an interrupt would break that
            // wait off (RubyUtils.ParkAsyncException).
            RubyUtils.NudgeThread(thread);

            // Kernel#sleep and Thread.stop use the Ruby run signal. This is race-safe even when
            // the target has announced sleep but has not entered WaitOne yet.
            info.RunForInterrupt();
        }

        /// <summary>
        /// Thread#raise takes exactly the arguments Kernel#raise does, including `cause:`, and is
        /// built from the same helper so the two cannot drift apart.
        ///
        /// The cause is resolved in the *calling* thread's context, which is what MRI 4.0 does:
        /// the exception carries the cause of whoever raised it, not of the thread it lands in.
        /// </summary>
        [RubyMethod("raise")]
        [RubyStackTraceHidden]
        [ReceivesKeywords]
        public static void RaiseException(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyContext/*!*/ context, Thread/*!*/ self, params object[]/*!*/ args) {

            // A bare Thread#raise is a plain RuntimeError even on the current thread: unlike
            // Kernel#raise it does not re-raise $!, and on another thread MRI cannot see that
            // thread's $! anyway.
            Exception e = KernelOps.CreateExceptionToRaise(respondToStorage, storage0, storage1, setBackTraceStorage, context, args, false);

            // Inside a fiber the current Ruby thread is the fiber's owner; raising on it means
            // raising here, not nudging the thread the fiber was started from.
            if (self == RubyUtils.CurrentRubyThread) {
                throw e;
            }

            RaiseAsyncException(context, self, e);
        }

        #endregion

        //    safe_level

        // TODO: these two methods interrupt a sleeping thread via the Thread.Interrupt API.
        // Unfortunately, this API interrupts the sleeping thread by throwing a ThreadInterruptedException.
        // In many Ruby programs (eg the specs) this causes the thread to terminate, which is NOT the
        // expected behavior. This is tracked by Rubyforge bug # 21157

        [RubyMethod("run")]
        [RubyMethod("wakeup")]
        public static Thread Run(Thread/*!*/ self) {
            RubyThreadInfo.RegisterCurrentThread();
            if (!self.IsAlive) {
                throw new ThreadError("killed thread");
            }
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            info.Run();
            return self;
        }

        private static RubyThreadStatus GetStatus(Thread thread) {
            ThreadState state = thread.ThreadState;
            RubyThreadInfo info = RubyThreadInfo.FromThread(thread);

            if ((state & ThreadState.Unstarted) == ThreadState.Unstarted) {
                if (info.CreatedFromRuby) {
                    // Ruby threads do not have an unstarted status. We must be in the tiny window when ThreadOps.CreateThread
                    // created the thread, but has not called Thread.Start on it yet.
                    return RubyThreadStatus.Running;
                } else {
                    // This is a thread created from outside Ruby. In such a case, we do not know when Thread.Start
                    // will be called on it. So we report it as unstarted.
                    return RubyThreadStatus.Unstarted;
                }
            }

            if ((state & (ThreadState.Stopped|ThreadState.Aborted)) != 0) {
                if (RubyThreadInfo.FromThread(thread).Exception == null) {
                    return RubyThreadStatus.Completed;
                } else {
                    return RubyThreadStatus.Aborted;
                }
            }

            // "sleep" is a wait the program can see - Kernel#sleep, a held Mutex, Queue#pop, Thread#join,
            // a blocking read - which marks the thread for its duration (RubyUtils.BlockingWait).
            // WaitSleepJoin alone is no guide: it also covers every brief wait for an internal lock and
            // the handshakes of thread start-up, through which MRI's thread is "run", and a thread that
            // looked asleep there made `should_not block_caller` fail at random under load.
            // A thread running a fiber does so on the fiber's CLR thread, its own waiting for control
            // to come back: it is as blocked as that fiber is.
            if (IsBlocked(thread, info)) {
                return RubyThreadStatus.Sleeping;
            }

            if ((state & ThreadState.AbortRequested) != 0) {
                return RubyThreadStatus.Aborting;
            }

            // Thread#kill has been called and the thread is still winding its ensure blocks down.
            // MRI reports "aborting" for exactly that window; it is not a CLR thread state, so it
            // has to come from the exit flag we set in Kill.
            if (info.ExitRequested) {
                return RubyThreadStatus.Aborting;
            }

            // Running, or WaitSleepJoin in a wait that is not the program's
            return RubyThreadStatus.Running;
        }

        private static bool IsBlocked(Thread/*!*/ thread, RubyThreadInfo/*!*/ info) {
            Thread fiber = info.ActiveFiberThread;
            if (fiber != null && !ReferenceEquals(fiber, thread) && fiber.IsAlive) {
                thread = fiber;
                info = RubyThreadInfo.FromThread(fiber);
            }
            if (info.Blocked) {
                return true;
            }
            // The mark first, then the CLR state: a thread that has marked itself but not reached its
            // wait yet is not "sleep", for Thread#kill interrupts only a thread that is waiting
            // (RubyUtils.NudgeThread), and the spec that sees "sleep" goes on to kill it.
            return info.InManagedWait && (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
        }

        [RubyMethod("status")]
        public static object Status(Thread/*!*/ self) {
            RubyThreadInfo.RegisterCurrentThread();
            switch (GetStatus(self)) {
                case RubyThreadStatus.Unstarted:
                    return MutableString.CreateAscii("unstarted");
                case RubyThreadStatus.Running:
                    return MutableString.CreateAscii("run");
                case RubyThreadStatus.Sleeping:
                    return MutableString.CreateAscii("sleep");
                case RubyThreadStatus.Aborting:
                    return MutableString.CreateAscii("aborting");
                case RubyThreadStatus.Completed:
                    return false;
                case RubyThreadStatus.Aborted:
                    return null;
                default:
                    throw new ArgumentException("unknown thread status");
            }
        }

        [RubyMethod("value")]
        public static object Value(RubyContext/*!*/ context, Thread/*!*/ self) {
            Join(context, self);
            return RubyThreadInfo.FromThread(self).Result;
        }

        //    stop?

        //  declared singleton methods

        [RubyMethod("abort_on_exception", RubyMethodAttributes.PublicSingleton)]
        public static object GlobalAbortOnException(object self) {
            return _globalAbortOnException;
        }

        [RubyMethod("abort_on_exception=", RubyMethodAttributes.PublicSingleton)]
        public static object GlobalAbortOnException(object self, bool value) {
            _globalAbortOnException = value;
            return value;
        }

        /// <summary>
        /// Every fiber runs on its own CLR thread here, but Ruby says a fiber belongs to the
        /// thread that created it - so inside a fiber this answers that thread, not the one the
        /// fiber happens to be running on.
        /// </summary>
        [RubyMethod("current", RubyMethodAttributes.PublicSingleton)]
        public static Thread/*!*/ Current(object self) {
            RubyThreadInfo.RegisterCurrentThread();
            return RubyUtils.CurrentRubyThread;
        }

        //    exclusive
        //    fork
        [RubyMethod("list", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ List(RubyContext/*!*/ context, object self) {
            RubyThreadInfo.RegisterCurrentThread();

            RubyThreadInfo[] threads = RubyThreadInfo.GetRactorThreads(context);
            RubyArray result = new RubyArray(threads.Length);
            foreach (RubyThreadInfo threadInfo in threads) {
                Thread thread = threadInfo.Thread;
                if (thread != null) {
                    result.Add(thread);
                }
            }

            return result;
        }

        [RubyMethod("main", RubyMethodAttributes.PublicSingleton)]
        public static Thread/*!*/ GetMainThread(RubyContext/*!*/ context, RubyClass self) {
            // in a Ractor, the Ractor's own main thread, as in MRI
            var ractor = RubyThreadInfo.Current.Ractor;
            return (ractor != null && ractor.MainThread != null) ? ractor.MainThread : context.MainThread;
        }

        [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
        [ReceivesKeywords]
        public static Thread/*!*/ CreateThread(CallSiteStorage<Func<CallSite, object, Proc, RubyArray, object>>/*!*/ storage,
            BlockParam startRoutine, object self, params object[]/*!*/ args) {

            RubyContext context = storage.Context;
            RubyClass cls = self as RubyClass;
            if (cls == null || !cls.IsRubyClass) {
                // Thread itself: its #initialize is the one that starts the thread, so skip the call.
                if (startRoutine == null) {
                    throw new ThreadError("must be called with a block");
                }
                return StartThread(context, null, startRoutine, args);
            }

            // A Thread subclass: its #initialize decides the block (calling super with one), and the
            // thread starts in Thread#initialize.
            ThreadStarter starter;
            Thread result = AllocateThread(context, cls, out starter);
            RubyThreadInfo info = RubyThreadInfo.FromThread(result);
            info.Starter = starter;

            var initialize = storage.GetCallSite("initialize",
                new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock));
            initialize.Target(initialize, result, startRoutine != null ? startRoutine.Proc : null, RubyOps.MakeArrayN(args));

            if (info.Starter != null) {
                info.Starter = null;
                throw new ThreadError(String.Format("uninitialized thread - check '{0}#initialize'", cls.GetDisplayName(context, false)));
            }
            return result;
        }

        // Thread.start and Thread.fork bypass #initialize; without a block they fail the way
        // building a Proc without one does, not the way Thread.new does.
        [RubyMethod("start", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("fork", RubyMethodAttributes.PublicSingleton)]
        [ReceivesKeywords]
        public static Thread/*!*/ StartThread(RubyContext/*!*/ context, BlockParam startRoutine, object self, params object[]/*!*/ args) {
            if (startRoutine == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }
            RubyClass cls = self as RubyClass;
            return StartThread(context, (cls != null && cls.IsRubyClass) ? cls : null, startRoutine, args);
        }

        internal sealed class ThreadStarter {
            internal BlockParam StartRoutine;
            internal object[] Args;
            internal Thread Creator;
            // a Ractor's main thread runs this instead of yielding StartRoutine, in a group of its own
            internal Func<object> Body;
            internal ThreadGroup Group;
        }

        /// <summary>
        /// Starts the main thread of <paramref name="ractor"/>, which runs <paramref name="body"/>
        /// and is a Ruby thread in every other respect (Thread.current, report_on_exception).
        /// </summary>
        internal static Thread/*!*/ StartRactorThread(RubyContext/*!*/ context, RubyRactor/*!*/ ractor, Func<object>/*!*/ body) {
            ThreadStarter starter;
            Thread result = AllocateThread(context, null, out starter);
            RubyThreadInfo info = RubyThreadInfo.FromThread(result);
            info.Ractor = ractor;
            info.Location = null;
            info.Group = starter.Group = new ThreadGroup();
            ractor.MainThread = result;
            starter.Body = body;
            result.Start();
            return result;
        }

        private static Thread/*!*/ StartThread(RubyContext/*!*/ context, RubyClass subclass, BlockParam/*!*/ startRoutine, object[]/*!*/ args) {
            ThreadStarter starter;
            Thread result = AllocateThread(context, subclass, out starter);
            Start(result, starter, startRoutine, args);
            return result;
        }

        private static void Start(Thread/*!*/ thread, ThreadStarter/*!*/ starter, BlockParam/*!*/ startRoutine, object[]/*!*/ args) {
            startRoutine.IsThreadRoot = true;
            starter.StartRoutine = startRoutine;
            starter.Args = args;
            thread.Start();
        }

        // Keep ThreadStarter out of the context's lifetime-long termination callback closure.
        private static Action CreateThreadTerminator(RubyContext context) {
            return () => TerminateAllThreads(context);
        }

        /// <summary>
        /// Creates the CLR thread for a Ruby thread, ready to start once <paramref name="starter"/> has
        /// been given its block. System.Threading.Thread is sealed, so a thread of a Ruby subclass of
        /// Thread is a plain CLR thread whose Ruby class is recorded by RubyContext.AdoptClrObject.
        /// </summary>
        private static Thread/*!*/ AllocateThread(RubyContext/*!*/ context, RubyClass subclass, out ThreadStarter/*!*/ starter) {
            if (OperatingSystem.IsBrowser()) {
                // single-threaded WebAssembly: Thread.Start would throw PlatformNotSupportedException
                throw RubyExceptions.CreateNotImplementedError(
                    "Thread.new is not available in the browser: .NET on WebAssembly runs on a single thread (so do Fiber and Enumerator#next, which are built on threads)");
            }
            RubyThreadInfo creator = RubyThreadInfo.FromThread(Thread.CurrentThread);
            ThreadGroup group = creator.Group;
            ThreadStarter s = starter = new ThreadStarter();
            if (context.ThreadTerminator == null) {
                context.ThreadTerminator = CreateThreadTerminator(context);
            }
            s.Creator = Thread.CurrentThread;
            Thread result = new Thread(new ThreadStart(() => RubyThreadStart(context, s.StartRoutine, s.Args, s.Group ?? group, s.Creator, s.Body)),
                RubyUtils.ThreadStackSize);
            if (subclass != null) {
                context.AdoptClrObject(result, subclass);
            }

            // Everything the thread answers about itself before it has run a single instruction -
            // #inspect, #priority, #report_on_exception - has to be in place before Start().
            RubyThreadInfo info = RubyThreadInfo.FromThread(result);
            info.CreatedFromRuby = true;
            info.Context = context;
            info.Group = group;
            info.Priority = creator.Priority;
            info.Location = GetCallerLocation(context);
            info.Ractor = creator.Ractor;

            // MRI: a new thread's root fiber inherits the storage of the fiber that created it.
            // Fiber is implemented in Ruby, so all that is needed here is to carry the creating
            // fiber's State over; Fiber.__root__ reads it (Src/StdLib/ironruby/ruby4.rb). Not the
            // public Fiber: a long-lived thread would keep an abandoned creator from being reclaimed.
            object creatorFiber = RubyThreadInfo.FromThread(Thread.CurrentThread)[context.CreateAsciiSymbol("__ir_fiber_current__")];
            object creatorState = GetCurrentFiberState(context, null);
            if (creatorFiber != null && creatorState != null) {
                info[context.CreateAsciiSymbol("__ir_fiber_parent__")] = creatorState;
            }

            // Ruby exits when the main thread exits. So all other threads need to be marked as background threads
            result.IsBackground = true;
            return result;
        }

        /// <summary>
        /// "file:line" of the Ruby frame that called Thread.new, for Thread#inspect.
        /// </summary>
        internal static string GetCallerLocation(RubyContext/*!*/ context) {
            try {
                // the frame records, not a CLR stack walk: even cut short after one frame (#184), the walk
                // was most of what Thread.new cost
                MutableString caller = RubyExceptionData.GetCallerFrame(context);
                if (caller != null) {
                    string frame = caller.ToString();
                    int inIndex = frame.LastIndexOf(":in ", StringComparison.Ordinal);
                    return inIndex > 0 ? frame.Substring(0, inIndex) : frame;
                }
            } catch (Exception) {
                // A location is decoration; never let looking for one break Thread.new.
            }
            return null;
        }

        /// <summary>
        /// MRI's thread error report: the thread that died, then the exception exactly as an
        /// unhandled exception is printed at top level. It goes to $stderr rather than to the CLR
        /// console, because $stderr is what Ruby code (and the specs) can redirect.
        /// </summary>
        internal static void ReportThreadException(RubyContext/*!*/ context, RubyThreadInfo/*!*/ info, Exception/*!*/ e) {
            try {
                StringBuilder report = new StringBuilder();
                report.Append(Inspect(context, info.Thread).ToString());
                report.Append(" terminated with exception (report_on_exception is true):");
                report.Append('\n');
                report.Append(context.FormatException(e));
                context.WriteWarningMessage(MutableString.CreateMutable(report.ToString(), RubyEncoding.UTF8));
            } catch (Exception) {
                // Reporting must never be what kills the process.
            }
        }

        private static void WaitForCreatorToBlock(Thread creator) {
            if (creator == null) {
                return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (creator.IsAlive && (creator.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && watch.ElapsedMilliseconds < 100) {
                // not Thread.Sleep: this thread has to look running (Thread#status) while it waits
                if (!Thread.Yield()) {
                    Thread.SpinWait(100);
                }
            }
        }

        private static void RubyThreadStart(RubyContext/*!*/ context, BlockParam startRoutine, object[] args, ThreadGroup group,
            Thread creator, Func<object> body) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(Thread.CurrentThread);
            info.CreatedFromRuby = true;

            info.Group = group;

            try {
                // Thread#kill / Thread#raise may have been called before the thread got a chance to run.
                RubyUtils.CheckAsyncException();

                if ((TracePoint.ActiveEvents & (int)(TraceEvents.ThreadBegin | TraceEvents.ThreadEnd)) != 0) {
                    // Under MRI's GVL a new thread runs once its creator blocks (or its time slice ends), so
                    // a :thread_begin/:thread_end hook sees whatever the creator did with Thread.new's result -
                    // typically stored it in a variable the hook compares Thread.current with. Wait for that.
                    WaitForCreatorToBlock(creator);
                }
                info.RunsRuby = true;
                if ((TracePoint.ActiveEvents & (int)TraceEvents.ThreadBegin) != 0) {
                    TracePoint.OnThread(TraceEvents.ThreadBegin, context, Thread.CurrentThread);
                }

                object threadResult;
                // TODO: break/returns might throw LocalJumpError if the RFC that was created for startRoutine is not active anymore:
                if (body != null) {
                    threadResult = body();
                } else if (startRoutine.Yield(args, out threadResult) && startRoutine.Returning(threadResult, out threadResult)) {
                    info.Exception = new ThreadError("return can't jump across threads");
                }
                info.Result = threadResult;
            } catch (MethodUnwinder) {
                info.Exception = new ThreadError("return can't jump across threads");
            } catch (Exception e) {
                if (e is ThreadInterruptedException) {
                    // We nudge a thread with Thread.Interrupt to deliver Thread#kill / Thread#raise. If the
                    // interrupt reached a wait we do not wrap, translate it here.
                    Exception pending = RubyUtils.GetPendingAsyncException(Thread.CurrentThread);
                    if (pending != null) {
                        try {
                            e = RubyUtils.FinishAsyncException(pending);
                        } catch (Exception finishError) {
                            e = finishError;
                        }
                    }
                }

                // The thread's block is over and the thread dies of `e`. A Thread#kill that waited
                // behind it (or anything parked since) would only cut the report below short: writing
                // it to a Ruby $stderr is a safe point. MRI does not check for interrupts here either.
                RubyUtils.DropPendingAsyncExceptions(Thread.CurrentThread);

                if (RubyUtils.IsRubyThreadExit(e) || info.ExitRequested) {
                    // Note that "e" may not be the exit signal at this point: if an exception was raised from a
                    // finally block, we get that here instead.
                    Utils.Log(String.Format("Thread {0} exited.", info.Thread.ManagedThreadId), "THREAD");
                    info.Result = null;
                    if (!RubyUtils.IsRubyThreadExit(e) && !(e is ThreadInterruptedException)) {
                        Exception visible = RubyUtils.GetVisibleException(e);
                        RubyExceptionData.GetInstance(visible).CaptureExceptionTrace(context);
                        info.Exception = visible;

                        // A thread killed while it was already unwinding a Thread#raise still dies
                        // of that exception, and MRI reports it.
                        if (info.ReportOnException && !(visible is SystemExit)) {
                            ReportThreadException(context, info, visible);
                        }
                    }
                } else {
                    e = RubyUtils.GetVisibleException(e);
                    // a start routine that is a method (Thread.new(&method(:m))) has no Ruby frame that captured the backtrace:
                    RubyExceptionData.GetInstance(e).CaptureExceptionTrace(context);
                    info.Exception = e;

                    StringBuilder trace = new StringBuilder();
                    trace.Append(e.Message);
                    trace.AppendLine();
                    trace.AppendLine();
                    trace.Append(e.StackTrace);
                    trace.AppendLine();
                    trace.AppendLine();
                    RubyExceptionData data = RubyExceptionData.GetInstance(e);
                    if (data.Backtrace != null) {
                        foreach (var frame in data.Backtrace) {
                            trace.Append(frame.ToString());
                        }
                    }

                    Utils.Log(trace.ToString(), "THREAD");

                    // A SystemExit that reaches a thread's top level means the program asked to
                    // terminate, so MRI hands it to the main thread whatever abort_on_exception
                    // says - and without the "terminated with exception" report.
                    bool exiting = e is SystemExit;

                    if (!exiting && info.ReportOnException) {
                        ReportThreadException(context, info, e);
                    }

                    if (exiting || _globalAbortOnException || info.AbortOnException) {
                        // MRI re-raises the exception on the main thread. Never rethrow it here:
                        // this is a background thread, and an unhandled exception on one takes the
                        // whole process down. Park it for the main thread instead - it is delivered
                        // at the main thread's next blocking point, and #join still re-raises it
                        // because info.Exception is set.
                        // in a Ractor, to the Ractor's main thread
                        Thread mainThread = info.Ractor != null ? info.Ractor.MainThread : context.MainThread;
                        if (mainThread != null && mainThread != Thread.CurrentThread) {
                            RubyUtils.RaiseAsyncException(mainThread, e);
                        }
                    }
                }
            } finally {
                if ((TracePoint.ActiveEvents & (int)TraceEvents.ThreadEnd) != 0) {
                    try {
                        TracePoint.OnThread(TraceEvents.ThreadEnd, context, Thread.CurrentThread);
                    } catch (Exception) {
                        // the thread is finished; a failing hook cannot change that
                    }
                }

                // MRI releases every mutex a thread still holds when it dies.
                IronRuby.StandardLibrary.Threading.RubyMutex.ReleaseLocksOf(Thread.CurrentThread);

                // A Thread#raise that arrived too late to be delivered dies with the thread, and so
                // does a Thread#kill waiting behind it. Left parked they would keep every other
                // thread's safe points on the slow path.
                RubyUtils.DropPendingAsyncExceptions(Thread.CurrentThread);
                info.RunsRuby = false;
                RubyThreadInfo.Unregister(info);
            }
        }

        [RubyMethod("pass", RubyMethodAttributes.PublicSingleton)]
        public static void Yield(object self) {
            RubyThreadInfo.RegisterCurrentThread();
            // Thread.pass is a safe point for a pending Thread#kill / Thread#raise, but it is not
            // a blocking call - Thread.handle_interrupt's :on_blocking still defers here.
            RubyUtils.CheckAsyncException(false);

            // Thread.Yield rather than Thread.Sleep(0): Sleep puts the thread into
            // WaitSleepJoin, so a thread spinning on `Thread.pass until ...` - which is how the
            // specs wait for each other - reported its status as "sleep" instead of "run".
            if (!Thread.Yield()) {
                Thread.Sleep(0);
            }
        }

        [RubyMethod("stop", RubyMethodAttributes.PublicSingleton)]
        public static void Stop(RubyContext/*!*/ context, object self) {
            // MRI's rb_thread_stop: with no other thread alive nothing could ever wake this one.
            // (Kernel#sleep has no such check: a signal can end it.)
            RubyThreadInfo.RegisterCurrentThread();
            if (RubyThreadInfo.GetRactorThreads(context).Length <= 1) {
                throw new ThreadError("stopping only thread\n\tnote: use sleep to stop forever");
            }
            DoSleep();
        }

        internal static void DoSleep() {
            RubyThreadInfo.RegisterCurrentThread();
            RubyThreadInfo info = RubyThreadInfo.FromThread(Thread.CurrentThread);
            info.Sleep();
            // Waking up is a safe point: a signal that arrived while we slept runs its trap handler
            // here, which is where MRI runs it too.
            RubyUtils.CheckAsyncException();
        }

        /// <summary>
        /// Ends a Kernel#sleep on another thread without interrupting it, so that a signal parked
        /// for the main thread is picked up at the safe point right after the sleep.
        /// </summary>
        internal static void WakeForSignal(Thread/*!*/ thread) {
            RubyThreadInfo.FromThread(thread).Run();
        }

        /// <summary>
        /// Kernel#sleep(n). Uses the same signal as Thread.stop so that Thread#run/#wakeup ends the sleep early
        /// (MRI behavior) and so that Thread#kill/#raise can interrupt it. Returns the number of seconds slept.
        /// </summary>
        internal static int DoSleep(int milliseconds) {
            RubyThreadInfo.RegisterCurrentThread();
            RubyThreadInfo info = RubyThreadInfo.FromThread(Thread.CurrentThread);
            long start = Environment.TickCount64;
            info.Sleep(milliseconds);
            RubyUtils.CheckAsyncException();
            return (int)Math.Round((Environment.TickCount64 - start) / 1000.0);
        }

        /// <summary>
        /// Kernel#sleep with a fractional-millisecond timeout. WaitHandle truncates its timeout to
        /// whole milliseconds, so preserve the remainder with a short interruptible spin instead
        /// of turning every sub-millisecond sleep into sleep(0).
        /// </summary>
        internal static int DoSleep(double milliseconds) {
            RubyThreadInfo.RegisterCurrentThread();
            RubyThreadInfo info = RubyThreadInfo.FromThread(Thread.CurrentThread);
            var timer = System.Diagnostics.Stopwatch.StartNew();

            int wholeMilliseconds = (int)Math.Floor(milliseconds);
            bool wasWoken = info.Sleep(wholeMilliseconds);
            while (!wasWoken && timer.Elapsed.TotalMilliseconds < milliseconds) {
                wasWoken = info.Sleep(0);
                if (!wasWoken) {
                    Thread.SpinWait(32);
                }
            }

            RubyUtils.CheckAsyncException();
            return (int)Math.Round(timer.Elapsed.TotalSeconds);
        }

        /// <summary>Kernel#sleep(n) exposed to the threading library (Mutex#sleep).</summary>
        public static int SleepForLibrary(int milliseconds) {
            return DoSleep(milliseconds);
        }

        #region name, name=

        [RubyMethod("name")]
        public static MutableString GetName(Thread/*!*/ self) {
            return RubyThreadInfo.FromThread(self).Name;
        }

        [RubyMethod("name=")]
        public static object SetName(Thread/*!*/ self, [DefaultProtocol]MutableString name) {
            if (name != null && name.IndexOf((byte)0) >= 0) {
                throw RubyExceptions.CreateArgumentError("string contains null byte");
            }
            RubyThreadInfo.FromThread(self).Name = name;
            if (name != null) {
                try {
                    self.Name = name.ToString();
                } catch (InvalidOperationException) {
                    // the CLR only lets a thread be named once
                }
            }
            return name;
        }

        #endregion

        #region thread variables

        [RubyMethod("thread_variable_get")]
        public static object GetThreadVariable(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key) {
            return RubyThreadInfo.FromThread(self).GetThreadVariable(ToKey(toStr, context, key));
        }

        [RubyMethod("thread_variable_set")]
        public static object SetThreadVariable(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key, object value) {
            CheckLocalsNotFrozen(context, self);
            RubyThreadInfo.FromThread(self).SetThreadVariable(ToKey(toStr, context, key), value);
            return value;
        }

        [RubyMethod("thread_variable?")]
        public static bool HasThreadVariable(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key) {
            return RubyThreadInfo.FromThread(self).HasThreadVariable(ToKey(toStr, context, key));
        }

        [RubyMethod("thread_variables")]
        public static RubyArray/*!*/ GetThreadVariables(Thread/*!*/ self) {
            return RubyThreadInfo.FromThread(self).GetThreadVariableKeys();
        }

        #endregion

        #region set_trace_func, add_trace_func

        /// <summary>Kernel#set_trace_func for this thread only.</summary>
        [RubyMethod("set_trace_func")]
        public static Proc SetTraceFunc(RubyContext/*!*/ context, Thread/*!*/ self, object traceFunc) {
            Proc proc = KernelOps.CastTraceFunc(traceFunc);
            TracePoint.SetTraceFunc(context, proc, self);
            return proc;
        }

        [RubyMethod("add_trace_func")]
        public static Proc AddTraceFunc(RubyContext/*!*/ context, Thread/*!*/ self, object traceFunc) {
            Proc proc = KernelOps.CastTraceFunc(traceFunc);
            if (proc == null) {
                throw RubyExceptions.CreateTypeError("trace_func needs to be Proc");
            }
            TracePoint.AddTraceFunc(context, proc, self);
            return proc;
        }

        #endregion

        // Thread#fetch lives in Src/StdLib/ironruby/ruby4.rb: the miss has to raise KeyError, and
        // KeyError is one of the exception classes defined in Ruby above the runtime.

        #region report_on_exception

        [RubyMethod("report_on_exception")]
        public static object ReportOnException(Thread/*!*/ self) {
            return RubyThreadInfo.FromThread(self).ReportOnException;
        }

        [RubyMethod("report_on_exception=")]
        public static object ReportOnException(Thread/*!*/ self, object value) {
            RubyThreadInfo.FromThread(self).ReportOnException = RubyOps.IsTrue(value);
            return value;
        }

        [RubyMethod("report_on_exception", RubyMethodAttributes.PublicSingleton)]
        public static object GlobalReportOnException(object self) {
            return _globalReportOnException;
        }

        [RubyMethod("report_on_exception=", RubyMethodAttributes.PublicSingleton)]
        public static object GlobalReportOnException(object self, object value) {
            _globalReportOnException = RubyOps.IsTrue(value);
            return value;
        }

        #endregion

        [RubyMethod("kill", RubyMethodAttributes.PublicSingleton)]
        public static Thread/*!*/ KillThread(object self, [NotNull]Thread/*!*/ thread) {
            return Kill(thread);
        }

        /// <summary>Thread.exit / Thread.stop take no argument and act on the current thread.</summary>
        [RubyMethod("exit", RubyMethodAttributes.PublicSingleton)]
        public static Thread/*!*/ ExitCurrentThread(object self) {
            return Kill(Thread.CurrentThread);
        }

        /// <summary>
        /// Thread.new starts the thread itself, so anything that reaches #initialize is a second
        /// initialization of a thread that is already running - which MRI refuses.
        /// </summary>
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        [ReceivesKeywords]
        public static Thread/*!*/ Reinitialize(RubyContext/*!*/ context, BlockParam block, Thread/*!*/ self, params object[]/*!*/ args) {
            // A thread of a Thread subclass, allocated by Thread.new and waiting for its block:
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            ThreadStarter starter = info.Starter;
            if (starter != null) {
                if (block == null) {
                    throw new ThreadError("must be called with a block");
                }
                info.Starter = null;
                Start(self, starter, block, args);
                return self;
            }

            if (info.Location != null) {
                throw new ThreadError("already initialized thread - " + info.Location);
            }
            throw new ThreadError("already initialized thread");
        }

        #region handle_interrupt, pending_interrupt?

        /// <summary>
        /// Thread.handle_interrupt(ExceptionClass => :immediate | :on_blocking | :never) { ... }.
        ///
        /// Thread#raise and Thread#kill are delivered cooperatively here - the exception is parked
        /// for the target thread and thrown at its next safe point - so masking one is a matter of
        /// telling the safe point to leave it parked. That is all this does: push the mask, run the
        /// block, pop it, and then take whatever accumulated while it was up.
        ///
        /// The check at the top delivers an already-pending interrupt that the *new* mask makes
        /// :immediate, which is MRI's documented way of forcing a deferred interrupt to be taken.
        /// </summary>
        [RubyMethod("handle_interrupt", RubyMethodAttributes.PublicSingleton)]
        public static object HandleInterrupt(RubyContext/*!*/ context, BlockParam block, object self, [NotNull]Hash/*!*/ mask) {
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("block is needed.");
            }

            var classes = new RubyModule[mask.Count];
            var timings = new int[mask.Count];
            int i = 0;
            foreach (var entry in mask) {
                classes[i] = entry.Key as RubyModule;
                var timing = entry.Value as RubySymbol;
                string name = (timing != null) ? timing.ToString() : null;
                switch (name) {
                    case "immediate": timings[i] = RubyUtils.InterruptImmediate; break;
                    case "on_blocking": timings[i] = RubyUtils.InterruptOnBlocking; break;
                    case "never": timings[i] = RubyUtils.InterruptNever; break;
                    default: throw RubyExceptions.CreateArgumentError("unknown mask signature");
                }
                i++;
            }

            RubyUtils.PushInterruptMask(classes, timings);
            try {
                // Not a blocking call: MRI's check here is RUBY_VM_CHECK_INTS, so an interrupt the
                // new mask makes :on_blocking stays parked until the block blocks (timeout's
                // issue 41 - test_handle_interrupt_with_interrupt_mask_inheritance).
                RubyUtils.CheckAsyncException(false);

                object result;
                block.Yield(out result);
                return result;
            } finally {
                RubyUtils.PopInterruptMask();
                // Anything the mask held back is taken here, replacing an exception the block was
                // already unwinding with - which is what MRI does. Again not a blocking point: an
                // enclosing :on_blocking mask keeps holding its exceptions.
                RubyUtils.CheckAsyncException(false);
            }
        }

        private static bool IsInterruptPending(RubyContext/*!*/ context, Thread/*!*/ thread, object error) {
            Exception e = RubyUtils.PeekPendingAsyncException(thread);
            if (e == null || RubyUtils.IsRubyThreadExit(e)) {
                return false;
            }
            RubyModule cls = error as RubyModule;
            return (cls == null) || context.GetClassOf(e).HasAncestor(cls);
        }

        [RubyMethod("pending_interrupt?", RubyMethodAttributes.PublicSingleton)]
        public static bool HasPendingInterrupt(RubyContext/*!*/ context, object self, [Optional]object error) {
            return IsInterruptPending(context, Thread.CurrentThread, error == Missing.Value ? null : error);
        }

        [RubyMethod("pending_interrupt?")]
        public static bool HasPendingInterrupt(RubyContext/*!*/ context, Thread/*!*/ self, [Optional]object error) {
            return IsInterruptPending(context, self, error == Missing.Value ? null : error);
        }

        #endregion

        /// <summary>Backs Thread::Backtrace.limit, which is written in Ruby.</summary>
        [RubyMethod("__backtrace_limit__", RubyMethodAttributes.PrivateSingleton)]
        public static int GetBacktraceLimit(RubyContext/*!*/ context, object self) {
            return context.RubyOptions.BacktraceLimit;
        }

        #region ignore_deadlock

        // Deadlock detection is MRI's; there is none here, so the flag is remembered and does nothing.
        private static bool _ignoreDeadlock;

        [RubyMethod("ignore_deadlock", RubyMethodAttributes.PublicSingleton)]
        public static object GetIgnoreDeadlock(object self) {
            return _ignoreDeadlock;
        }

        [RubyMethod("ignore_deadlock=", RubyMethodAttributes.PublicSingleton)]
        public static object SetIgnoreDeadlock(object self, object value) {
            _ignoreDeadlock = RubyOps.IsTrue(value);
            return value;
        }

        #endregion

        /// <summary>
        /// Called by the Fiber implementation: a fiber runs on its own CLR thread but has to count as the
        /// thread that owns its fiber group wherever Ruby ownership semantics apply (Mutex, mostly).
        /// </summary>
        [RubyMethod("__set_fiber_owner__", RubyMethodAttributes.PublicSingleton)]
        public static object SetFiberOwner(object self, [NotNull]Thread/*!*/ owner) {
            RubyUtils.SetFiberOwnerThread(owner);
            RubyThreadInfo.FromThread(owner).ActiveFiberThread = Thread.CurrentThread;
            return owner;
        }

        // The runtimes whose program has ended. Per runtime: several share a process (hosting,
        // IronRuby.Tests), and one that had shut down used to count as the end of every other's
        // program too - a fiber that finished in another runtime afterwards never handed control
        // back, and its resumer waited for ever.
        private static readonly ConditionalWeakTable<RubyContext, object>/*!*/ _terminatingContexts =
            new ConditionalWeakTable<RubyContext, object>();

        internal static bool IsTerminating(RubyContext/*!*/ context) {
            object mark;
            return _terminatingContexts.TryGetValue(context, out mark);
        }

        /// <summary>
        /// True once the program has ended and the remaining threads are being killed. The Fiber
        /// implementation asks, so that a killed fiber does not hand control back to the fiber that
        /// resumed it: that fiber is suspended, and in MRI nothing of it runs again.
        /// </summary>
        [RubyMethod("__terminating__", RubyMethodAttributes.PublicSingleton)]
        public static bool IsTerminating(RubyContext/*!*/ context, object self) {
            return IsTerminating(context);
        }

        /// <summary>
        /// MRI kills every other thread when the program ends (after the at_exit handlers), and waits
        /// for them. What runs is the ensure clauses of each thread's current fiber; a suspended
        /// fiber is abandoned. Every fiber here is a CLR thread of its own, so "kill the thread" means
        /// kill the CLR thread of the fiber that is running, and leave the others parked.
        /// </summary>
        private static void TerminateAllThreads(RubyContext/*!*/ context) {
            _terminatingContexts.AddOrUpdate(context, context);

            List<Thread> killed = new List<Thread>();
            List<Thread> busy = new List<Thread>();

            foreach (RubyThreadInfo info in RubyThreadInfo.Threads) {
                Thread thread = info.Thread;
                if (thread == Thread.CurrentThread || thread == context.MainThread || !info.CreatedFromRuby || !thread.IsAlive) {
                    continue;
                }

                // Another runtime's thread: its program has not ended (see _terminatingContexts).
                RubyContext owner = info.Context;
                if (owner != null && owner != context) {
                    continue;
                }

                if (info.IsIdleFiberWorker || (info.FiberExecution != null && info.FiberExecution.Status == "suspended")) {
                    // a suspended fiber (or a thread whose root fiber is suspended)
                    continue;
                }

                // The kill is delivered at the thread's next blocking point, and a thread blocked in a
                // managed wait (sleep, Queue#pop, Mutex, ConditionVariable) is woken for it. One that is
                // computing, or blocked in a system call, may never get there: give it a moment only.
                bool waiting = (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
                try {
                    Kill(thread);
                    (waiting ? killed : busy).Add(thread);
                } catch (Exception) {
                    // the thread finished in the meantime
                }
            }

            // MRI waits as long as it takes; don't let a thread stuck in an ensure clause hang the exit.
            JoinAll(killed, TimeSpan.FromSeconds(5));
            JoinAll(busy, TimeSpan.FromMilliseconds(100));
        }

        private static void JoinAll(List<Thread>/*!*/ threads, TimeSpan timeout) {
            DateTime deadline = DateTime.UtcNow + timeout;
            foreach (Thread thread in threads) {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !thread.Join(left)) {
                    return;
                }
            }
        }

        [RubyMethod("stop?", RubyMethodAttributes.PublicInstance)]
        public static bool IsStopped(Thread self) {
            RubyThreadInfo.RegisterCurrentThread();
            RubyThreadStatus status = GetStatus(self);
            return status == RubyThreadStatus.Sleeping || status == RubyThreadStatus.Completed || status == RubyThreadStatus.Aborted;
        }
    }
}

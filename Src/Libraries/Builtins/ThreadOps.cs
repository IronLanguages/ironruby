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
#if FEATURE_THREAD
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    using Debug = System.Diagnostics.Debug;

    /// <summary>
    /// Ruby threads are represented by CLR thread objects (System.Threading.Thread).
    /// Ruby 1.8.N has green threads where the language does the thread scheduling. We map the green threads 
    /// directly to CLR threads.
    /// 
    /// Ruby supports asynchronously manipulating of an arbitrary thread with methods like Thread#raise, Thread#exit, etc.
    /// For such methods, we use Thread.Abort which is unsafe. Howevever, Ruby 1.9 may not support green threads,
    /// and this will not be an issue then.
    /// </summary>
    [RubyClass("Thread", Extends = typeof(Thread), Inherits = typeof(object), BuildConfig = "FEATURE_THREAD")]
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
            private static readonly Dictionary<int, RubyThreadInfo> _mapping = new Dictionary<int, RubyThreadInfo>();

            // Lets the runtime's own native waits (DescriptorStream's poll) report the thread as
            // sleeping, the way the waits in this library do by setting Blocked themselves.
            static RubyThreadInfo() {
                RubyUtils.NativeWaitHook = blocked => {
                    var info = FromThread(Thread.CurrentThread);
                    bool was = info._blocked;
                    info._blocked = blocked;
                    return was;
                };
            }
            private readonly Dictionary<RubySymbol, object> _threadLocalStorage;
            private ThreadGroup _group;
            private readonly Thread _thread;
            private bool _blocked;
            private bool _abortOnException;
            private AutoResetEvent _runSignal = new AutoResetEvent(false);
            private bool _isSleeping;

            private RubyThreadInfo(Thread thread) {
                _threadLocalStorage = new Dictionary<RubySymbol, object>();
                _group = ThreadGroup.Default;
                _thread = thread;
                ReportOnException = _globalReportOnException;
            }

            internal static RubyThreadInfo FromThread(Thread t) {
                RubyThreadInfo result;
                lock (_mapping) {
                    int key = t.ManagedThreadId;
                    if (!_mapping.TryGetValue(key, out result)) {
                        result = new RubyThreadInfo(t);
                        _mapping[key] = result;
                    }
                }
                return result;
            }

            internal static void RegisterThread(Thread t) {
                FromThread(t);
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
            internal bool CreatedFromRuby { get; set; }
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
            internal volatile Thread ActiveFiberThread;
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
                    lock (_mapping) {
                        List<RubyThreadInfo> result = new List<RubyThreadInfo>(_mapping.Count);
                        foreach (KeyValuePair<int, RubyThreadInfo> entry in _mapping) {
                            if (entry.Value.Thread.IsAlive && !entry.Value.IsIdleFiberWorker) {
                                result.Add(entry.Value);
                            }
                        }
                        return result.ToArray();
                    }
                }
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
                try {
                    _isSleeping = true;
                    try {
                        _runSignal.WaitOne();
                    } catch (ThreadInterruptedException) {
                        // Thread#kill / Thread#raise nudged us: deliver the parked exception. If there is none
                        // the interrupt was spurious and the sleep simply ends (MRI's sleep is also allowed to
                        // return early). Both wake us - Run() sets the signal and RaiseAsyncException
                        // interrupts - so drop the signal the interrupt left latched, or the next sleep
                        // would return at once.
                        _runSignal.Reset();
                        RubyUtils.TranslateThreadInterrupt();
                    }
                } finally {
                    _isSleeping = false;
                }
            }

            /// <summary>
            /// Same as Sleep() but with a timeout; returns true if the full timeout elapsed.
            /// </summary>
            internal bool Sleep(int milliseconds) {
                RubyUtils.CheckAsyncException();
                try {
                    _isSleeping = true;
                    try {
                        return _runSignal.WaitOne(milliseconds);
                    } catch (ThreadInterruptedException) {
                        _runSignal.Reset();
                        RubyUtils.TranslateThreadInterrupt();
                        return false;
                    }
                } finally {
                    _isSleeping = false;
                }
            }

            internal void Run() {
                if (_isSleeping) {
                    _runSignal.Set();
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
            return RubyThreadInfo.FromThread(self == RubyUtils.CurrentRubyThread ? Thread.CurrentThread : self);
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
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
                trace.Insert(0, MutableString.CreateMutable(caller + ":in '" + blockedIn + "'", RubyEncoding.UTF8));
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);

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
                    if (self.Join(slice)) {
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
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
            info.Run();
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            return RubyThreadInfo.FromThread(self).Priority;
        }

        [RubyMethod("priority=")]
        public static object Priority(Thread/*!*/ self, [DefaultProtocol]int priority) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);

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

#if FEATURE_EXCEPTION_STATE
        private static CallSite<Func<CallSite, object, object>> _exceptionSite;

        private static void RaiseAsyncException(RubyContext/*!*/ context, Thread thread, Exception exception) {
            Thread activeFiber = RubyThreadInfo.FromThread(thread).ActiveFiberThread;
            if (activeFiber != null && activeFiber.IsAlive) {
                thread = activeFiber;
            }
            RubyThreadInfo info = RubyThreadInfo.FromThread(thread);
            RubyThreadStatus status = GetStatus(thread);

            // rethrow semantics, preserves the backtrace associated with the exception:
            RubyUtils.RaiseAsyncException(thread, exception, e => {
                // MRI raises exception.exception in the target thread
                var site = RubyUtils.GetCallSite(ref _exceptionSite, context, "exception", 0);
                return site.Target(site, e) as Exception;
            });
            // Kernel#sleep and Thread.stop use the Ruby run signal. This is race-safe even when
            // the target has announced sleep but has not entered WaitOne yet.
            info.Run();

            if (status == RubyThreadStatus.Sleeping) {
                // Thread.Abort can interrupt a thread with ThreadState.WaitSleepJoin. However, Thread.Abort 
                // is deferred while the thread is in a catch block. If there is a Kernel.sleep in a catch block,
                // then that sleep will not be interrupted. 
                // TODO: We should call Run to nudge the thread if its CurrentException is not-null, and 
                // ThreadOps.Stop should have a checkpoint to see whether an async exception needs to be thrown

                // Run(thread);
            }
        }
#endif

        /// <summary>
        /// Thread#raise takes exactly the arguments Kernel#raise does, including `cause:`, and is
        /// built from the same helper so the two cannot drift apart.
        ///
        /// The cause is resolved in the *calling* thread's context, which is what MRI 4.0 does:
        /// the exception carries the cause of whoever raised it, not of the thread it lands in.
        /// </summary>
        [RubyMethod("raise")]
        [RubyStackTraceHidden]
        public static void RaiseException(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyContext/*!*/ context, Thread/*!*/ self, params object[]/*!*/ args) {

#if FEATURE_EXCEPTION_STATE
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
#else
            throw new NotImplementedError("Thread#raise not supported on this platform");
#endif
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
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

            if ((state & ThreadState.WaitSleepJoin) == ThreadState.WaitSleepJoin) {
                // We will report a thread to be sleeping more often than in CRuby. This is because any "lock" statement
                // can potentially cause ThreadState.WaitSleepJoin. Also, "Thread.pass" does System.Threading.Thread.Sleep(0)
                // which also briefly changes the state to ThreadState.WaitSleepJoin
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

            if ((state & ThreadState.Running) == ThreadState.Running) {
                if (info.Blocked) {
                    return RubyThreadStatus.Sleeping;
                } else {
                    return RubyThreadStatus.Running;
                }
            }

#pragma warning disable 162 // msc: unreachable code
            throw new ArgumentException("unknown thread status: " + state);
#pragma warning restore 162
        }

        [RubyMethod("status")]
        public static object Status(Thread/*!*/ self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
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

        private static void SetCritical(RubyContext/*!*/ context, bool value) {
            // Debug.Assert(context.RubyOptions.Compatibility < RubyCompatibility.Ruby19);
            if (value) {
                bool lockTaken = false;
                try {
                    MonitorUtils.Enter(context.CriticalMonitor, ref lockTaken);
                } finally {
                    // thread could have been aborted just before/after Monitor.Enter acquired the lock:
                    if (lockTaken) {
                        context.CriticalThread = Thread.CurrentThread;
                    }
                }
            } else {
                Monitor.Exit(context.CriticalMonitor);
                context.CriticalThread = null;
            }
        }

        public static bool Critical(RubyContext/*!*/ context, object self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            return context.CriticalThread != null;
        }

        public static void Critical(RubyContext/*!*/ context, object self, bool value) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            SetCritical(context, value);
        }

        /// <summary>
        /// Every fiber runs on its own CLR thread here, but Ruby says a fiber belongs to the
        /// thread that created it - so inside a fiber this answers that thread, not the one the
        /// fiber happens to be running on.
        /// </summary>
        [RubyMethod("current", RubyMethodAttributes.PublicSingleton)]
        public static Thread/*!*/ Current(object self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            return RubyUtils.CurrentRubyThread;
        }

        //    exclusive
        //    fork
        [RubyMethod("list", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ List(object self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);

            RubyThreadInfo[] threads = RubyThreadInfo.Threads;
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
            return context.MainThread;
        }

        [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
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
            RubyThreadInfo creator = RubyThreadInfo.FromThread(Thread.CurrentThread);
            ThreadGroup group = creator.Group;
            ThreadStarter s = starter = new ThreadStarter();
            if (context.ThreadTerminator == null) {
                context.ThreadTerminator = CreateThreadTerminator(context);
            }
            s.Creator = Thread.CurrentThread;
            Thread result = new Thread(new ThreadStart(() => RubyThreadStart(context, s.StartRoutine, s.Args, group, s.Creator)));
            if (subclass != null) {
                context.AdoptClrObject(result, subclass);
            }

            // Everything the thread answers about itself before it has run a single instruction -
            // #inspect, #priority, #report_on_exception - has to be in place before Start().
            RubyThreadInfo info = RubyThreadInfo.FromThread(result);
            info.CreatedFromRuby = true;
            info.Group = group;
            info.Priority = creator.Priority;
            info.Location = GetCallerLocation(context);

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
        private static string GetCallerLocation(RubyContext/*!*/ context) {
            try {
                RubyArray trace = RubyExceptionData.CreateBacktrace(context, 1);
                if (trace != null && trace.Count > 0) {
                    string frame = trace[0].ToString();
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
        private static void ReportThreadException(RubyContext/*!*/ context, RubyThreadInfo/*!*/ info, Exception/*!*/ e) {
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

        private static void RubyThreadStart(RubyContext/*!*/ context, BlockParam/*!*/ startRoutine, object[]/*!*/ args, ThreadGroup group,
            Thread creator) {
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
                if ((TracePoint.ActiveEvents & (int)TraceEvents.ThreadBegin) != 0) {
                    TracePoint.OnThread(TraceEvents.ThreadBegin, context, Thread.CurrentThread);
                }

                object threadResult;
                // TODO: break/returns might throw LocalJumpError if the RFC that was created for startRoutine is not active anymore:
                if (startRoutine.Yield(args, out threadResult) && startRoutine.Returning(threadResult, out threadResult)) {
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

                if (RubyUtils.IsRubyThreadExit(e) || info.ExitRequested) {
                    // Note that "e" may not be the exit signal at this point: if an exception was raised from a
                    // finally block, we get that here instead.
                    Utils.Log(String.Format("Thread {0} exited.", info.Thread.ManagedThreadId), "THREAD");
                    info.Result = null;
                    if (!RubyUtils.IsRubyThreadExit(e) && !(e is ThreadInterruptedException)) {
                        Exception visible = RubyUtils.GetVisibleException(e);
                        info.Exception = visible;

                        // A thread killed while it was already unwinding a Thread#raise still dies
                        // of that exception, and MRI reports it.
                        if (info.ReportOnException && !(visible is SystemExit)) {
                            ReportThreadException(context, info, visible);
                        }
                    }
                } else {
                    e = RubyUtils.GetVisibleException(e);
                    RubyExceptionData.ActiveExceptionHandled(e);
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
                        Thread mainThread = context.MainThread;
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

                // A Thread#raise that arrived too late to be delivered dies with the thread. Left
                // parked it would keep every other thread's safe points on the slow path.
                RubyUtils.GetPendingAsyncException(Thread.CurrentThread);

                // Its not a good idea to terminate a thread which has set Thread.critical=true, but its hard to predict
                // which thread will be scheduled next, even with green threads. However, ConditionVariable.create_timer 
                // in monitor.rb explicitly does "Thread.critical=true; other_thread.raise" before exiting, and expects
                // other_thread to be scheduled immediately.
                // To deal with such code, we release the critical monitor here if the current thread is holding it
                if (context.RubyOptions.Compatibility < RubyCompatibility.Ruby19 && context.CriticalThread == Thread.CurrentThread) {
                    SetCritical(context, false);
                }
            }
        }

        [RubyMethod("pass", RubyMethodAttributes.PublicSingleton)]
        public static void Yield(object self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
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
            if (context.CriticalThread == Thread.CurrentThread) {
                SetCritical(context, false);
            }
            DoSleep();
        }

        internal static void DoSleep() {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            // TODO: MRI throws an exception if you try to stop the main thread
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
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

        [RubyMethod("exit", RubyMethodAttributes.PublicSingleton)]
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

        private static volatile bool _terminating;

        /// <summary>
        /// True once the program has ended and the remaining threads are being killed. The Fiber
        /// implementation asks, so that a killed fiber does not hand control back to the fiber that
        /// resumed it: that fiber is suspended, and in MRI nothing of it runs again.
        /// </summary>
        [RubyMethod("__terminating__", RubyMethodAttributes.PublicSingleton)]
        public static bool IsTerminating(object self) {
            return _terminating;
        }

        /// <summary>
        /// MRI kills every other thread when the program ends (after the at_exit handlers), and waits
        /// for them. What runs is the ensure clauses of each thread's current fiber; a suspended
        /// fiber is abandoned. Every fiber here is a CLR thread of its own, so "kill the thread" means
        /// kill the CLR thread of the fiber that is running, and leave the others parked.
        /// </summary>
        private static void TerminateAllThreads(RubyContext/*!*/ context) {
            _terminating = true;

            List<Thread> killed = new List<Thread>();
            List<Thread> busy = new List<Thread>();

            foreach (RubyThreadInfo info in RubyThreadInfo.Threads) {
                Thread thread = info.Thread;
                if (thread == Thread.CurrentThread || thread == context.MainThread || !info.CreatedFromRuby || !thread.IsAlive) {
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
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            RubyThreadStatus status = GetStatus(self);
            return status == RubyThreadStatus.Sleeping || status == RubyThreadStatus.Completed || status == RubyThreadStatus.Aborted;
        }
    }
}
#endif

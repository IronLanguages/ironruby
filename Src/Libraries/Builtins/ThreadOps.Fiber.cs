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
using System.Diagnostics;
using System.Threading;
using System.Runtime.CompilerServices;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    // The primitives the Ruby-level Fiber (Src/StdLib/ironruby/ruby4.rb) switches with.
    public static partial class ThreadOps {

        /// <summary>
        /// The mailbox a fiber waits on while it is not running. Every fiber is a CLR thread of its
        /// own and exactly one fiber of a group runs at a time, so a switch is "post to the target's
        /// mailbox, then take from my own".
        ///
        /// Taking spins for a while before it blocks. A resume / Fiber.yield round trip whose other
        /// side answers quickly - Enumerator#next over a generator, a fiber that yields in a loop -
        /// then costs two cache-line transfers instead of two kernel wake-ups, which is most of what
        /// a switch used to cost. A wake-up costs tens of microseconds on a busy machine, so the spin
        /// may run about as long before it gives up. Two misses in a row pause spinning for that
        /// mailbox - a fiber that is resumed rarely, or threads that cannot run side by side, would
        /// only burn a core on it - and every 32nd take tries again. The spin yields the CPU now and
        /// then, in case the thread it waits for is queued behind it. Single-core machines never
        /// spin.
        ///
        /// A blocked take is a Monitor wait, so Thread#kill / Thread#raise (delivered with
        /// Thread.Interrupt) wake it exactly as they wake Queue#pop, which the mailbox used to be.
        /// </summary>
        public sealed class FiberMailbox {
            // How long a take spins before it blocks, and how spinning is paused and probed.
            private static readonly long _spinTicks = Stopwatch.Frequency / 10000;          // 100us
            private const int SpinMissesToPause = 2;
            private const int PausedTakesPerProbe = 32;
            private static readonly bool _canSpin = Environment.ProcessorCount > 1;

            private readonly object/*!*/ _lock = new object();

            // Pending messages: practically always none or one, so the first one has a slot of its own.
            private object _message;
            private Queue<object> _overflow;
            private volatile int _count;

            // The receiver is parked in Monitor.Wait (guarded by _lock).
            private bool _blocked;

            // Spin misses in a row; takes since spinning was paused (spinning is paused while
            // _misses has reached SpinMissesToPause).
            private int _misses;
            private int _pausedTakes;

            // A fiber's own mailbox, whose take a Thread#raise does not break off (FiberCore).
            internal bool HandsOverControl;

            // The fiber group's owner, and its RubyThreadInfo, cached for FiberTake.
            private Thread _owner;
            private RubyThreadInfo _ownerInfo;

            public void Post(object message) {
                using (new RubyUtils.UninterruptedLock(_lock)) {
                    int count = _count;
                    if (count == 0) {
                        _message = message;
                    } else {
                        if (_overflow == null) {
                            _overflow = new Queue<object>();
                        }
                        _overflow.Enqueue(message);
                    }
                    _count = count + 1;
                    if (_blocked) {
                        Monitor.Pulse(_lock);
                    }
                }
            }

            public object Take() {
                if (_count == 0 && _canSpin) {
                    if (_misses < SpinMissesToPause || ++_pausedTakes >= PausedTakesPerProbe) {
                        _pausedTakes = 0;
                        if (Spin()) {
                            _misses = 0;
                        } else if (_misses < SpinMissesToPause) {
                            _misses++;
                        }
                    }
                }

                while (true) {
                    bool interrupted = false;
                    using (new RubyUtils.UninterruptedLock(_lock)) {
                        if (_count == 0) {
                            _blocked = true;
                            try {
                                Monitor.Wait(_lock);
                            } catch (ThreadInterruptedException) {
                                interrupted = true;
                            } finally {
                                _blocked = false;
                            }
                        }

                        // A message that is already here wins over an interrupt: control has arrived,
                        // and the parked exception is raised once this side runs (FiberCore.Switch).
                        // Raising it instead would leave the message for the next take - a later
                        // Fiber#resume would return at once, with the fiber it resumed still running.
                        if (_count != 0) {
                            object message = _message;
                            int count = _count - 1;
                            _message = (count > 0) ? _overflow.Dequeue() : null;
                            _count = count;
                            return message;
                        }
                    }

                    if (interrupted) {
                        // Thread#kill / Thread#raise nudged us: deliver the parked exception (outside the
                        // lock, it runs Ruby code), or hand it on to the fiber that is running instead
                        // of us (RubyThreadInfo.ForwardToActiveFiber). If nothing is parked the wait
                        // simply goes on. A fiber waiting for control raises a Thread#raise only once
                        // control is here (FiberCore.Switch): it may be the running fiber already, its
                        // message posted a moment from now, and raising now would leave that message
                        // for the next take.
                        if (HandsOverControl) {
                            RubyUtils.TranslateFiberWaitInterrupt();
                        } else {
                            RubyUtils.TranslateThreadInterrupt();
                        }
                    }
                }
            }

            /// <summary>
            /// Take without spinning, giving up after <paramref name="milliseconds"/>; for the pool.
            /// </summary>
            internal bool TryTake(int milliseconds, out object message) {
                long deadline = Environment.TickCount64 + milliseconds;
                while (true) {
                    bool interrupted = false;
                    using (new RubyUtils.UninterruptedLock(_lock)) {
                        if (_count == 0) {
                            int left = (int)(deadline - Environment.TickCount64);
                            if (left <= 0) {
                                message = null;
                                return false;
                            }
                            _blocked = true;
                            try {
                                Monitor.Wait(_lock, left);
                            } catch (ThreadInterruptedException) {
                                interrupted = true;
                            } finally {
                                _blocked = false;
                            }
                        }

                        if (_count != 0) {
                            message = _message;
                            int count = _count - 1;
                            _message = (count > 0) ? _overflow.Dequeue() : null;
                            _count = count;
                            return true;
                        }
                    }

                    if (interrupted) {
                        RubyUtils.TranslateThreadInterrupt();
                    }
                }
            }

            private bool Spin() {
                long start = Stopwatch.GetTimestamp();
                int round = 0;
                while (true) {
                    for (int i = 0; i < 4; i++) {
                        Thread.SpinWait(2);
                        if (_count != 0) {
                            return true;
                        }
                    }
                    if ((++round & 7) == 0) {
                        Thread.Yield();
                        if (_count != 0) {
                            return true;
                        }
                    }
                    if (Stopwatch.GetTimestamp() - start > _spinTicks) {
                        return false;
                    }
                }
            }

            internal RubyThreadInfo/*!*/ GetOwnerInfo(Thread/*!*/ owner) {
                RubyThreadInfo info = _ownerInfo;
                if (info == null || !ReferenceEquals(_owner, owner)) {
                    info = RubyThreadInfo.FromThread(owner);
                    _owner = owner;
                    _ownerInfo = info;
                }
                return info;
            }
        }

        // The worker retains execution state, never its public Ruby Fiber handle. A registration
        // attached to that handle signals abandonment when the handle becomes unreachable.
        public sealed class FiberCore {
            private readonly FiberMailbox _mailbox;
            private readonly Thread _owner;
            private readonly RubyContext _context;
            private readonly WeakReference<object> _handle;
            private readonly bool _root;
            private readonly RubySymbol _currentKey;
            private readonly RubySymbol _parentKey;
            private readonly RubyThreadInfo/*!*/ _ownerInfo;
            private RubyThreadInfo _threadInfo;
            // The CLR thread this fiber runs on: known once the fiber has one (Attach, Activate).
            private volatile Thread _thread;
            private volatile bool _completed;
            // A resumer is part of an active call chain until its child hands control back.
            private object _pinnedHandle;
            private const int ReclaimKind = 7;
            private static readonly FiberMessage _reclaimMessage = new FiberMessage(ReclaimKind, null);
            public object State { get; set; }
            public string Status { get; private set; }
            public object Handle {
                get {
                    object handle;
                    return _handle.TryGetTarget(out handle) ? handle : null;
                }
            }

            /// <summary>The thread this fiber belongs to, which is Thread.current inside it.</summary>
            internal Thread/*!*/ Owner {
                get { return _owner; }
            }

            internal FiberCore(RubyContext context, object handle, Thread owner, bool root) {
                _mailbox = new FiberMailbox();
                _mailbox.HandsOverControl = true;
                _owner = owner;
                _context = context;
                _handle = new WeakReference<object>(handle);
                _root = root;
                _currentKey = context.CreateAsciiSymbol("__ir_fiber_current__");
                _parentKey = context.CreateAsciiSymbol("__ir_fiber_parent__");
                _ownerInfo = RubyThreadInfo.FromThread(owner);
                if (root) {
                    // a root fiber is the thread it is created on
                    _thread = Thread.CurrentThread;
                }
                Status = root ? "resumed" : "created";
            }

            /// <summary>
            /// Records the CLR thread the fiber is going to run on, before anybody hands it control
            /// (Fiber#__start__): from then on a switch to it can make it the running fiber at once.
            /// </summary>
            public void Attach(Thread thread) {
                // a fiber thread from now on: it may be told to raise before it gets to Run
                RubyThreadInfo.FromThread(thread).IsFiberWorker = true;
                _thread = thread;
            }

            public void Terminate() {
                _completed = true;
                Status = "terminated";
            }

            internal void RequestReclaim() {
                if (!_root && !_completed) {
                    _mailbox.Post(_reclaimMessage);
                }
            }

            // Keep the temporary strong reference out of Run/Await's suspended CLR frames.
            [MethodImpl(MethodImplOptions.NoInlining)]
            private void Activate(bool running) {
                object handle = Handle;
                if (handle == null) {
                    RubyUtils.BeginFiberReclamation();
                }
                _currentFiber = handle;
                _currentFiberContext = _context;
                _currentFiberState = State;
                var info = _threadInfo ?? (_threadInfo = RubyThreadInfo.FromThread(Thread.CurrentThread));
                info[_currentKey] = handle;
                info.FiberExecution = this;
                if (!_root) {
                    // set before it is ever cleared again, so that no moment between two fibers -
                    // FiberExecution gone, not yet parked - counts this thread as a Ruby thread
                    info.IsFiberWorker = true;
                }
                RubyUtils.SetFiberOwnerThread(_owner);
                _thread = Thread.CurrentThread;
                if (running && !ReferenceEquals(_ownerInfo.ActiveFiberThread, Thread.CurrentThread)) {
                    // normally already done by the fiber that handed us control (MakeRunning)
                    using (new RubyUtils.UninterruptedLock(_ownerInfo.FiberSwitchLock)) {
                        _ownerInfo.ActiveFiberThread = Thread.CurrentThread;
                    }
                }
            }

            /// <summary>
            /// Called by the fiber that hands control to this one, before it posts the message:
            /// this fiber is the thread's running fiber from now on, for Thread#raise. Setting that
            /// only once this fiber has woken up (Activate) left a window in which an interrupt went
            /// to the fiber handing over - which raised it on its way back, in Fiber#resume, while
            /// this one went on running - or, for a fiber that had finished, to a thread on its way
            /// into the pool. Whatever is already parked for the fiber handing over moves too.
            /// </summary>
            private void MakeRunning() {
                Thread thread = _thread;
                if (thread == null) {
                    return;
                }
                Thread current = Thread.CurrentThread;
                // Atomic with respect to Thread#raise (RubyThreadInfo.FiberSwitchLock).
                using (new RubyUtils.UninterruptedLock(_ownerInfo.FiberSwitchLock)) {
                    _ownerInfo.ActiveFiberThread = thread;
                    if (RubyUtils.IsSafePointRequested && !ReferenceEquals(current, thread)) {
                        // no nudge: the message we are about to post wakes the thread, which raises
                        // the exception once it runs
                        RubyUtils.MoveAsyncException(current, thread);
                    }
                }
            }

            private void Deactivate() {
                _currentFiber = null;
                var info = _threadInfo ?? (_threadInfo = RubyThreadInfo.FromThread(Thread.CurrentThread));
                info[_currentKey] = null;
            }

            // Catch only at the execution boundary, after all user frames have unwound.
            // In particular, Ruby rescue Exception cannot intercept the reclaim signal.
            public void Run(object state) {
                RubyThreadInfo info = RubyThreadInfo.FromThread(Thread.CurrentThread);
                info.IsFiberWorker = true;
                info.InFiberRun = true;
                // Interrupts wait until __run__ opens Thread.handle_interrupt(Object => :immediate)
                // around the fiber's block. Its own handle_interrupt(Object => :never) is not up
                // yet at the safe point of the call to __run__: one raised there, before the
                // fiber's first message, would escape the fiber altogether.
                RubyUtils.PushInterruptMask(new RubyModule[] { _context.ObjectClass }, new[] { RubyUtils.InterruptNever });
                try {
                    info[_parentKey] = null;
                    // Not the running fiber yet: it waits for its first message (__run__).
                    Activate(false);
                    var site = _context.GetOrCreateSendSite<Func<CallSite, object, object>>("__run__", RubyCallSignature.Simple(0));
                    site.Target(site, state);
                } catch (Exception) when (RubyUtils.IsReclaimingFiber) {
                    // CLR cleanup may replace the signal with another exception. The entire
                    // discarded stack is still being reclaimed; do not report that as Ruby code.
                    Terminate();
                    IronRuby.StandardLibrary.Threading.RubyMutex.ReleaseLocksOf(Thread.CurrentThread);
                } finally {
                    RubyUtils.PopInterruptMask();
                    info.InFiberRun = false;
                    RubyUtils.EndFiberReclamation();
                }
            }

            public void Post(int kind, object payload) {
                MakeRunning();
                _mailbox.Post(new FiberMessage(kind, payload));
            }

            public FiberMessage Await() {
                Deactivate();
                return Take();
            }

            private FiberMessage Take() {
                var message = (FiberMessage)_mailbox.Take();
                if (message.Kind == ReclaimKind) {
                    RubyUtils.BeginFiberReclamation();
                }
                Activate(true);
                Status = "resumed";
                return message;
            }

            // A transfer from a fiber that is itself resumed also gets control back: the target's
            // end returns to the deepest fiber of the root's resume chain, which is this one.
            public FiberMessage Switch(FiberCore target, int kind, object payload, bool resumed) {
                if (kind == FiberMessage.ResumeKind || kind == FiberMessage.RaiseKind || kind == FiberMessage.KillKind ||
                    (kind == FiberMessage.TransferKind && resumed)) {
                    _pinnedHandle = Handle;
                }
                try {
                    Status = "suspended";
                    Deactivate();
                    target.Post(kind, payload);
                    FiberMessage message = Take();
                    // MRI checks for interrupts as soon as control is back (fiber_switch): one that
                    // reached the thread while the other fiber ran, and was handed back with control,
                    // is raised by #resume / Fiber.yield / #transfer, not at some later safe point.
                    RubyUtils.CheckAsyncException(false);
                    return message;
                } finally {
                    // Take restores the running handle before releasing this pin. Also drop it
                    // when an interrupt unwinds the wait instead of delivering a message.
                    _pinnedHandle = null;
                }
            }

            public FiberMessage Yield(FiberCore target, RubyArray args) {
                return Switch(target, FiberMessage.YieldKind, PackArguments(args), false);
            }

            private static object PackArguments(RubyArray args) {
                return args.Count == 0 ? null : args.Count == 1 ? args[0] : args;
            }

            public sealed class FiberMessage {
                // Shared with the private Fiber protocol in ruby4.rb.
                public const int ResumeKind = 0, TransferKind = 1, YieldKind = 2,
                    ReturnKind = 3, RaiseKind = 4, ErrorKind = 5, KillKind = 6;
                public int Kind { get; }
                public object Payload { get; }
                public object Value {
                    get {
                        return Kind == ResumeKind || Kind == TransferKind
                            ? PackArguments((RubyArray)Payload) : Payload;
                    }
                }

                internal FiberMessage(int kind, object payload) {
                    Kind = kind;
                    Payload = payload;
                }
            }
        }

        private sealed class FiberRegistration {
            internal readonly FiberCore Core;
            internal FiberRegistration(FiberCore core) { Core = core; }
            ~FiberRegistration() { Core.RequestReclaim(); }
        }

        private static readonly ConditionalWeakTable<object, FiberRegistration> _fiberHandles =
            new ConditionalWeakTable<object, FiberRegistration>();

        [RubyMethod("__fiber_core__", RubyMethodAttributes.PublicSingleton)]
        public static FiberCore CreateFiberCore(RubyContext context, object self, object handle, Thread owner, bool root) {
            var core = new FiberCore(context, handle, owner, root);
            _fiberHandles.Add(handle, new FiberRegistration(core));
            return core;
        }

        [RubyMethod("__fiber_state__", RubyMethodAttributes.PublicSingleton)]
        public static object GetCurrentFiberState(RubyContext context, object self) {
            return ReferenceEquals(_currentFiberContext, context) ? _currentFiberState : null;
        }

        // Fiber.current of this CLR thread (a fiber never leaves the thread it runs on), for the
        // context it belongs to. The same thing is kept in the thread's locals as :__ir_fiber_current__
        // for the C# code that looks there; this copy is what makes Fiber.current cheap.
        [ThreadStatic]
        private static object _currentFiber;
        [ThreadStatic]
        private static object _currentFiberState;
        [ThreadStatic]
        private static RubyContext _currentFiberContext;

        #region the pool of fiber threads

        // Starting a CLR thread waits until the OS has scheduled it, which is most of what creating
        // a fiber cost. The thread of a fiber that has finished therefore parks here for a while,
        // and the next fiber to start runs on it. Before it parks it does for the finished fiber
        // what the end of the thread would have done (drop its mutexes and any undelivered
        // Thread#raise), and it forgets the fiber's locals, so the next fiber starts as on a new
        // thread. A parked thread is left out of Thread.list and ThreadGroup#list.
        private sealed class FiberWorker {
            internal readonly Thread/*!*/ Thread;
            internal readonly FiberMailbox/*!*/ Jobs = new FiberMailbox();

            internal FiberWorker(Thread/*!*/ thread) {
                Thread = thread;
            }
        }

        private static readonly List<FiberWorker>/*!*/ _idleFiberWorkers = new List<FiberWorker>();
        private const int MaxIdleFiberWorkers = 8;
        private const int FiberWorkerIdleMilliseconds = 2000;

        [ThreadStatic]
        private static FiberWorker _fiberWorker;

        /// <summary>
        /// Hands <paramref name="fiber"/> to a parked fiber thread and answers that thread, or nil when
        /// none is parked (the fiber then gets a new thread).
        /// </summary>
        [RubyMethod("__fiber_reuse__", RubyMethodAttributes.PublicSingleton)]
        public static Thread ReuseFiberThread(object self, object fiber) {
            FiberWorker worker;
            while (true) {
                using (new RubyUtils.UninterruptedLock(_idleFiberWorkers)) {
                    int last = _idleFiberWorkers.Count - 1;
                    if (last < 0) {
                        return null;
                    }
                    worker = _idleFiberWorkers[last];
                    _idleFiberWorkers.RemoveAt(last);
                }
                if (worker.Thread.IsAlive) {
                    break;
                }
            }

            // The thread now runs a fiber of the current thread's, as a new one would have.
            RubyThreadInfo info = RubyThreadInfo.FromThread(worker.Thread);
            info.Group = RubyThreadInfo.FromThread(Thread.CurrentThread).Group;
            info.IsIdleFiberWorker = false;
            worker.Jobs.Post(fiber);
            return worker.Thread;
        }

        /// <summary>
        /// Called on a fiber thread whose fiber has finished: cleans up after the fiber and waits
        /// for the next one. Nil means the thread should end (the pool is full, nobody came in
        /// time, or the program is ending).
        /// </summary>
        [RubyMethod("__fiber_park__", RubyMethodAttributes.PublicSingleton)]
        public static object ParkFiberThread(object self) {
            Thread current = Thread.CurrentThread;
            IronRuby.StandardLibrary.Threading.RubyMutex.ReleaseLocksOf(current);
            // A Thread#raise that reached the fiber as it finished belongs to the fiber now running
            // in its thread; anything else parked here dies with the fiber.
            HandOffParkedInterrupt();
            RubyUtils.DropPendingAsyncExceptions(current);
            RubyThreadInfo info = RubyThreadInfo.FromThread(current);
            info.ClearLocals();
            _currentFiber = null;
            _currentFiberState = null;
            _currentFiberContext = null;
            info.FiberExecution = null;

            FiberWorker worker = _fiberWorker ?? (_fiberWorker = new FiberWorker(current));
            using (new RubyUtils.UninterruptedLock(_idleFiberWorkers)) {
                if (_terminating || _idleFiberWorkers.Count >= MaxIdleFiberWorkers) {
                    return null;
                }
                info.IsIdleFiberWorker = true;
                _idleFiberWorkers.Add(worker);
            }

            bool parked = true;
            try {
                object fiber;
                if (worker.Jobs.TryTake(FiberWorkerIdleMilliseconds, out fiber)) {
                    parked = false;
                    // not to the next fiber, which may well be another thread's
                    HandOffParkedInterrupt();
                    return fiber;
                }
                using (new RubyUtils.UninterruptedLock(_idleFiberWorkers)) {
                    if (_idleFiberWorkers.Remove(worker)) {
                        // retired; stays hidden until the thread is gone
                        parked = false;
                        return null;
                    }
                }
                // ReuseFiberThread took this thread just now: its fiber is on the way
                parked = false;
                object next = worker.Jobs.Take();
                HandOffParkedInterrupt();
                return next;
            } finally {
                if (parked) {
                    // left by an exception (Thread#kill): nobody may hand this thread a fiber now
                    using (new RubyUtils.UninterruptedLock(_idleFiberWorkers)) {
                        _idleFiberWorkers.Remove(worker);
                    }
                }
            }
        }

        // Moves a Thread#raise parked for this fiber thread, whose fiber is done, to the fiber that
        // runs in the thread the finished fiber belonged to (still RubyUtils.CurrentRubyThread here).
        // When there is none - that thread has ended - the raise is dropped: it must not reach the
        // next fiber to run here, which may well be another thread's.
        private static void HandOffParkedInterrupt() {
            if (!RubyUtils.IsSafePointRequested) {
                return;
            }
            Thread current = Thread.CurrentThread;
            Exception e = RubyUtils.PeekPendingAsyncException(current);
            if (e != null && !RubyUtils.IsRubyThreadExit(e) && !RubyThreadInfo.TryForwardToActiveFiber()) {
                RubyUtils.RetractAsyncException(current, e);
            }
        }

        /// <summary>
        /// Releases the mutexes the fiber running on this thread still holds, as the end of the
        /// thread would. Called by a fiber that has finished, before it hands control back: the
        /// fiber it hands it to may run (and lock) right away, while this thread is still alive.
        /// </summary>
        [RubyMethod("__fiber_release_locks__", RubyMethodAttributes.PublicSingleton)]
        public static object ReleaseFiberLocks(object self) {
            IronRuby.StandardLibrary.Threading.RubyMutex.ReleaseLocksOf(Thread.CurrentThread);
            return null;
        }

        #endregion

        [RubyMethod("__fiber_mailbox__", RubyMethodAttributes.PublicSingleton)]
        public static FiberMailbox/*!*/ CreateFiberMailbox(object self) {
            return new FiberMailbox();
        }

        [RubyMethod("__fiber_current__", RubyMethodAttributes.PublicSingleton)]
        public static object GetCurrentFiber(RubyContext/*!*/ context, object self) {
            return ReferenceEquals(_currentFiberContext, context) ? _currentFiber : null;
        }

        [RubyMethod("__fiber_current_set__", RubyMethodAttributes.PublicSingleton)]
        public static object SetCurrentFiber(RubyContext/*!*/ context, object self, object fiber) {
            RubyThreadInfo.FromThread(Thread.CurrentThread)[context.CreateAsciiSymbol("__ir_fiber_current__")] = fiber;
            _currentFiber = fiber;
            _currentFiberContext = context;
            FiberRegistration registration;
            if (fiber != null && _fiberHandles.TryGetValue(fiber, out registration)) {
                _currentFiberState = registration.Core.State;
                RubyThreadInfo.FromThread(Thread.CurrentThread).FiberExecution = registration.Core;
            } else {
                _currentFiberState = null;
                RubyThreadInfo.FromThread(Thread.CurrentThread).FiberExecution = null;
            }
            return fiber;
        }

        /// <summary>
        /// Hands a fiber's result (or error) to the fiber that gets control back. Through a library
        /// method, not a call to <see cref="FiberCore.Post"/> from Ruby: a CLR Object parameter would
        /// turn a Ruby String into a System.String, and the resumer would get a new UTF-8 String.
        /// </summary>
        [RubyMethod("__fiber_core_post__", RubyMethodAttributes.PublicSingleton)]
        public static object FiberCorePost(object self, [NotNull]FiberCore/*!*/ core, int kind, object payload) {
            core.Post(kind, payload);
            return null;
        }

        [RubyMethod("__fiber_post__", RubyMethodAttributes.PublicSingleton)]
        public static object FiberPost(object self, [NotNull]FiberMailbox/*!*/ mailbox, object message) {
            mailbox.Post(message);
            return null;
        }

        /// <summary>
        /// Waits for the next message to the fiber running on this thread, then makes this thread
        /// the one that runs on behalf of <paramref name="owner"/> (what __set_fiber_owner__ does).
        /// </summary>
        [RubyMethod("__fiber_take__", RubyMethodAttributes.PublicSingleton)]
        public static object FiberTake(object self, [NotNull]FiberMailbox/*!*/ mailbox, [NotNull]Thread/*!*/ owner) {
            object message = mailbox.Take();
            RubyUtils.SetFiberOwnerThread(owner);
            mailbox.GetOwnerInfo(owner).ActiveFiberThread = Thread.CurrentThread;
            return message;
        }
    }
}
#endif

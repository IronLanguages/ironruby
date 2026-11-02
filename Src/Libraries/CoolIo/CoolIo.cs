/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

// The C half of the cool.io gem - ext/cool.io's cool.io_ext, an event loop over libev - for
// IronRuby.  The gem's own Ruby (lib/cool.io.rb and lib/cool.io/*: Loop#run, Coolio::IO,
// TCPSocket, Server, DNSResolver, AsyncWatcher, ...) is not vendored: the user's installed gem
// loads on top of this, as on CRuby.  Its `cool_require "cool.io_ext"` finds
// Src/StdLib/ironruby/cool.io_ext.rb, which loads this namespace.
//
// What ext/cool.io defines, and what this defines in its place:
//
//   Coolio::Loop          ev_loop_new (private), run_once([timeout]), run_nonblock
//   Coolio::Watcher       attach, detach, enable, disable, evloop, attached?, enabled?
//   Coolio::IOWatcher     initialize(io, flags = "r"), on_readable, on_writable
//   Coolio::TimerWatcher  initialize(interval, repeating = false), reset, on_timer
//   Coolio::StatWatcher   initialize(path, interval = 0), path, on_change(previous, current)
//   Coolio::Buffer        the byte queue Coolio::IO writes through, read_from / write_to
//   Coolio::Utils         ncpus, maxfds, maxfds=
//   Struct::StatInfo      what a StatWatcher reports (defined in cool.io_ext.rb)
//
// libev is not here; its semantics are.  A watcher started on a loop is "active" in libev's
// sense, which is not the same as attached-and-enabled in cool.io's: a one-shot timer that has
// fired stays attached and enabled but is no longer active, and #reset can start a disabled
// timer again.  run_once blocks only while something is active (or a timeout was given), so a
// loop with nothing attached returns at once, as ev_run(EVRUN_ONCE) does.  Events are collected
// first and dispatched after, as the extension does through its eventbuf: timers in the order
// they expired, then stat changes, then IO; a watcher detached by an earlier callback in the same
// round does not get its event.  An IOWatcher ready for reading and writing at once gets
// on_readable only, as in the C dispatch.
//
// Readiness is asked the way NIO::Selector (Src/Libraries/Nio4r) and IO.select
// (Src/Libraries/Builtins/IoReadiness.cs) ask it: one poll(2) over every watched descriptor on
// Unix, one Socket.Select over every watched socket on Windows; an in-process IO.pipe - which is
// what AsyncWatcher signals through - wakes the wait through the loop's ReadinessWaiter, as does
// a watcher being attached or a timer reset from another thread.  StatWatcher uses inotify(7) on
// Linux, as libev does (a change is seen at once and nothing is polled while the file exists),
// and polls lstat at the watcher's interval elsewhere or while the path does not exist.  Waits
// are cut into slices so that Thread#kill and Thread#raise reach a thread blocked in run_once.
// libev is not thread-safe and does not notice a watcher attached from another thread until its
// wait ends; this loop is woken by one.  No epoll backend yet: a loop asks every watched
// descriptor on each wait, which is what poll(2) costs and plenty for fluentd's handful.
//
// One extension serves every version of the gem, so where cool.io's C changed it follows the
// newest (1.11): Watcher#attached? answers true rather than the Integer 0 1.9 leaked, detaching
// an enabled watcher goes through its #disable, a loop asked only for backends the platform lacks
// raises "no usable backend", StatInfo carries 64-bit sizes and blksize/blocks, and Buffer#read
// takes any Integer.  Two differences from CRuby's 1.9.1 are libev artefacts: a timer due during
// run_once fires in that run_once (libev's millisecond rounding often wakes it a hair early and
// leaves the timer to the next one), and an exception from a callback leaves no stale events
// behind for the next run_once to count.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.StandardLibrary.Nio4r;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.StandardLibrary.CoolIo {

    [RubyModule("Coolio")]
    public static class CoolioModule {

        // libev's revents bits, which is what the extension hands its dispatch callbacks.
        internal const int EV_READ = 0x01;
        internal const int EV_WRITE = 0x02;
        internal const int EV_TIMER = 0x100;
        internal const int EV_STAT = 0x1000;

        // libev's numbers: ev_run never blocks longer than MAX_BLOCKTIME, ev_stat polls every
        // DEF_STAT_INTERVAL seconds when given no interval and never more often than
        // MIN_STAT_INTERVAL.
        internal const double MaxBlockTime = 59.743;
        internal const double DefaultStatInterval = 5.0074891;
        internal const double MinStatInterval = 0.1074891;

        private static readonly double _tickSeconds = 1.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>A monotonic clock in seconds, which is what libev's timers run on.</summary>
        internal static double Now() {
            return System.Diagnostics.Stopwatch.GetTimestamp() * _tickSeconds;
        }

        private static readonly bool _isUnix = Path.DirectorySeparatorChar == '/';

        internal static int GetIntIvar(RubyContext/*!*/ context, object obj, string/*!*/ name) {
            object value;
            context.TryGetInstanceVariable(obj, name, out value);
            if (value is int) {
                return (int)value;
            }
            if (value == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion from nil to integer");
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into Integer", context.GetClassDisplayName(value));
        }

        #region Loop

        /// <summary>One entry of the extension's eventbuf: a watcher and what happened to it.</summary>
        internal struct PendingEvent {
            public Watcher Watcher;
            public int Revents;
        }

        [RubyClass("Loop")]
        public sealed class Loop : RubyObject {
            // Guards the active sets.  Watchers may be attached, enabled or reset from any thread
            // (fluentd attaches from its main thread while the loop thread is in run_once); the
            // callbacks only ever run on the thread calling run_once.
            internal readonly object _lock = new object();

            // libev's active watchers, in the order they were started.
            internal readonly List<IOWatcher>/*!*/ _ios = new List<IOWatcher>();
            internal readonly List<TimerWatcher>/*!*/ _timers = new List<TimerWatcher>();
            internal readonly List<StatWatcher>/*!*/ _stats = new List<StatWatcher>();

            // Bumped whenever the active sets change, so that a wait in progress re-reads them.
            internal int _version;
            private volatile bool _blocking;

            internal bool _initialized;
            private bool _running;
            internal readonly List<PendingEvent>/*!*/ _pending = new List<PendingEvent>();

            internal ReadinessWaiter _waiter;
            internal Inotify _inotify;
            private bool _inotifyTried;

            private CallSite<Func<CallSite, object, object>> _onReadable, _onWritable, _onTimer, _detach, _disable;
            private CallSite<Func<CallSite, object, object, object, object>> _onChange;

            public Loop(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Loop(ImmediateClass.NominalClass);
            }

            internal RubyContext/*!*/ Context {
                get { return ImmediateClass.Context; }
            }

            // libev's flag bits: EVFLAG_NOENV, and the backends this loop's waits stand in for -
            // select and poll everywhere poll(2) is, epoll on Linux, kqueue on the BSDs and macOS,
            // select alone on Windows.
            private const int EVFLAG_NOENV = 0x1000000;
            private const int EVBACKEND_MASK = 0xFFFF;
            private const int EVBACKEND_SELECT = 0x1, EVBACKEND_POLL = 0x2, EVBACKEND_EPOLL = 0x4, EVBACKEND_KQUEUE = 0x8;

            private static int SupportedBackends {
                get {
                    if (!_isUnix) {
                        return EVBACKEND_SELECT;
                    }
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                        return EVBACKEND_SELECT | EVBACKEND_POLL | EVBACKEND_EPOLL;
                    }
                    return EVBACKEND_SELECT | EVBACKEND_POLL | EVBACKEND_KQUEUE;
                }
            }

            [RubyMethod("ev_loop_new", RubyMethodAttributes.PrivateInstance)]
            public static object EvLoopNew(Loop/*!*/ self, [DefaultProtocol]int flags) {
                if (self._initialized) {
                    throw RubyExceptions.CreateRuntimeError("loop already initialized");
                }
                // ev_loop_new: LIBEV_FLAGS in the environment replaces the flags unless the loop
                // was told not to consult it, and a loop asked only for backends this platform
                // lacks has none - which cool.io 1.10 reports as below (1.9 went on to crash).
                if ((flags & EVFLAG_NOENV) == 0) {
                    string env = Environment.GetEnvironmentVariable("LIBEV_FLAGS");
                    int fromEnv;
                    if (env != null && Int32.TryParse(env.Trim(), out fromEnv)) {
                        flags = fromEnv;
                    }
                }
                int backends = flags & EVBACKEND_MASK;
                if (backends != 0 && (backends & SupportedBackends) == 0) {
                    throw RubyExceptions.CreateRuntimeError("unable to initialize the event loop (no usable backend)");
                }
                self._initialized = true;
                self._waiter = ReadinessWaiter.Create();
                return null;
            }

            [RubyMethod("run_once")]
            public static int RunOnce(ConversionStorage<double>/*!*/ floatConversion, Loop/*!*/ self, [Optional]object timeout) {
                double? seconds = null;
                if (timeout != null && !(timeout is Missing)) {
                    double value = Protocols.CastToFloat(floatConversion, timeout);
                    if (value < 0) {
                        throw RubyExceptions.CreateArgumentError("time interval must be positive");
                    }
                    seconds = value;
                }
                return self.Run(false, seconds);
            }

            [RubyMethod("run_nonblock")]
            public static int RunNonBlock(Loop/*!*/ self) {
                return self.Run(true, null);
            }

            private int Run(bool nonblock, double? timeout) {
                if (!_initialized) {
                    throw RubyExceptions.CreateRuntimeError("loop not initialized");
                }
                if (_running) {
                    // The extension asserts this away; its eventbuf cannot be shared.
                    throw RubyExceptions.CreateRuntimeError("loop is already running");
                }

                _running = true;
                try {
                    Collect(nonblock, timeout);
                    int count = _pending.Count;
                    // Detaching a watcher from a callback clears its later entries, so the
                    // buffer is read by index and may change under us.
                    for (int i = 0; i < _pending.Count; i++) {
                        var e = _pending[i];
                        if (e.Watcher != null) {
                            e.Watcher.Dispatch(this, e.Revents);
                        }
                    }
                    return count;
                } finally {
                    lock (_lock) {
                        _pending.Clear();
                    }
                    _running = false;
                }
            }

            /// <summary>Forgets a detached watcher's events still waiting to be dispatched.</summary>
            internal void ForgetPending(Watcher/*!*/ watcher) {
                lock (_lock) {
                    for (int i = 0; i < _pending.Count; i++) {
                        if (ReferenceEquals(_pending[i].Watcher, watcher)) {
                            _pending[i] = new PendingEvent();
                        }
                    }
                }
            }

            private void Feed(Watcher/*!*/ watcher, int revents) {
                lock (_lock) {
                    _pending.Add(new PendingEvent { Watcher = watcher, Revents = revents });
                }
            }

            /// <summary>
            /// Called with the lock held whenever a watcher starts or stops: a run_once blocked on
            /// another thread re-reads the active sets (and, for a new timer, its deadline).
            /// </summary>
            internal void Changed() {
                _version++;
                if (_blocking && _waiter != null) {
                    _waiter.Signal();
                }
            }

            internal Inotify GetInotify() {
                if (!_inotifyTried) {
                    _inotifyTried = true;
                    _inotify = Inotify.Create();
                }
                return _inotify;
            }

            #region Callbacks

            private CallSite<Func<CallSite, object, object>>/*!*/ CallbackSite(ref CallSite<Func<CallSite, object, object>> site, string/*!*/ name) {
                if (site == null) {
                    // An implicit-self call, as rb_funcall is: the callbacks may be protected
                    // (AsyncWatcher#on_readable, Coolio::IO#on_readable) or private.
                    Interlocked.CompareExchange(ref site, CallSite<Func<CallSite, object, object>>.Create(
                        RubyCallAction.Make(Context, name, new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf))), null);
                }
                return site;
            }

            internal void CallOnReadable(object watcher) {
                var site = CallbackSite(ref _onReadable, "on_readable");
                site.Target(site, watcher);
            }

            internal void CallOnWritable(object watcher) {
                var site = CallbackSite(ref _onWritable, "on_writable");
                site.Target(site, watcher);
            }

            internal void CallDetach(object watcher) {
                var site = CallbackSite(ref _detach, "detach");
                site.Target(site, watcher);
            }

            internal void CallDisable(object watcher) {
                var site = CallbackSite(ref _disable, "disable");
                site.Target(site, watcher);
            }

            internal void CallOnTimer(object watcher) {
                var site = CallbackSite(ref _onTimer, "on_timer");
                site.Target(site, watcher);
            }

            internal void CallOnChange(object watcher, object previous, object current) {
                if (_onChange == null) {
                    Interlocked.CompareExchange(ref _onChange, CallSite<Func<CallSite, object, object, object, object>>.Create(
                        RubyCallAction.Make(Context, "on_change", new RubyCallSignature(2, RubyCallFlags.HasImplicitSelf))), null);
                }
                _onChange.Target(_onChange, watcher, previous, current);
            }

            #endregion

            #region Waiting

            // One active IOWatcher as this run_once sees it.
            private struct Probe {
                public IOWatcher Watcher;
                public RubyIO IO;
                public int Events;
                public Socket Socket;
                public RubyPipe Pipe;
                public int Descriptor;
                public int Ready;
            }

            /// <summary>
            /// ev_run(EVRUN_ONCE) or ev_run(EVRUN_NOWAIT): waits until something active is due -
            /// an IO ready, a timer expired, a stat changed - or the timeout passes, and feeds what
            /// happened into _pending.  Blocks only while something is active.
            /// </summary>
            private void Collect(bool nonblock, double? timeout) {
                double start = Now();
                double deadline;
                int active;
                lock (_lock) {
                    active = _ios.Count + _timers.Count + _stats.Count;
                }
                if (nonblock || (active == 0 && timeout == null)) {
                    deadline = start;
                } else {
                    deadline = start + MaxBlockTime;
                    if (timeout != null) {
                        // the extension's own fudge: libev does not like zero timers
                        deadline = Math.Min(deadline, start + timeout.Value + 0.0001);
                    }
                }

                var info = ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
                bool wasBlocked = info.Blocked;
                Probe[] probes = null;
                int count = 0;
                int version = -1;
                StatWatcher[] stats = null;
                HashSet<RubyPipe> pipes = null;
                bool statDirty = false;

                try {
                    info.Blocked = true;
                    while (true) {
                        double nextTimer = Double.PositiveInfinity;
                        double nextStatPoll = Double.PositiveInfinity;
                        bool watchInotify;
                        lock (_lock) {
                            watchInotify = _inotify != null && Inotify.Wanted(this);
                            if (version != _version) {
                                version = _version;
                                if (pipes != null) {
                                    foreach (var pipe in pipes) {
                                        pipe.RemoveWaiter(_waiter);
                                    }
                                    pipes = null;
                                }
                                probes = new Probe[_ios.Count];
                                count = 0;
                                foreach (var watcher in _ios) {
                                    var probe = new Probe { Watcher = watcher, IO = watcher._io, Events = watcher._events, Descriptor = -1 };
                                    if (!probe.IO.Closed) {
                                        watcher.Resolve();
                                        probe.Socket = watcher._socket;
                                        probe.Pipe = watcher._pipe;
                                        probe.Descriptor = watcher._descriptor;
                                        if (probe.Pipe != null && _waiter != null && !nonblock) {
                                            if ((pipes ?? (pipes = new HashSet<RubyPipe>())).Add(probe.Pipe)) {
                                                probe.Pipe.AddWaiter(_waiter);
                                            }
                                        }
                                    }
                                    probes[count++] = probe;
                                }
                                stats = _stats.ToArray();
                            }
                            foreach (var timer in _timers) {
                                nextTimer = Math.Min(nextTimer, timer._at);
                            }
                            foreach (var stat in _stats) {
                                if (stat._polling) {
                                    nextStatPoll = Math.Min(nextStatPoll, stat._nextPoll);
                                }
                            }
                            if (_waiter != null && !nonblock) {
                                _waiter.Reset();
                            }
                            _blocking = !nonblock;
                        }

                        int ready = 0;
                        for (int i = 0; i < count; i++) {
                            probes[i].Ready = ProbeWithoutKernel(ref probes[i]);
                            if (probes[i].Ready != 0) {
                                ready++;
                            }
                        }

                        double now = Now();
                        double until = nonblock ? now : Math.Min(deadline, Math.Min(nextTimer, nextStatPoll));
                        bool due = ready > 0 || statDirty || nextTimer <= now || nextStatPoll <= now;
                        int wait = 0;
                        if (!due && until > now) {
                            double ms = Math.Ceiling((until - now) * 1000);
                            wait = (int)Math.Min(ms, (pipes != null && _waiter == null) ? 1 : IoReadiness.SliceMilliseconds);
                        }

                        bool inotifyEvent;
                        ready += AskKernel(probes, count, wait, watchInotify, out inotifyEvent);
                        if (inotifyEvent) {
                            statDirty = true;
                        }

                        now = Now();
                        if (ready > 0 || statDirty || nextTimer <= now || nextStatPoll <= now || now >= until) {
                            break;
                        }
                        RubyUtils.CheckAsyncException();
                    }
                } finally {
                    _blocking = false;
                    if (pipes != null) {
                        foreach (var pipe in pipes) {
                            pipe.RemoveWaiter(_waiter);
                        }
                    }
                    info.Blocked = wasBlocked;
                }

                // What happened, in the order the extension's eventbuf would have it.
                double t = Now();
                lock (_lock) {
                    // Timers that have expired, earliest first.
                    List<TimerWatcher> expired = null;
                    foreach (var timer in _timers) {
                        if (timer._at <= t) {
                            (expired ?? (expired = new List<TimerWatcher>())).Add(timer);
                        }
                    }
                    if (expired != null) {
                        // stable: List.Sort is not
                        var ordered = new List<KeyValuePair<double, int>>(expired.Count);
                        for (int i = 0; i < expired.Count; i++) {
                            ordered.Add(new KeyValuePair<double, int>(expired[i]._at, i));
                        }
                        ordered.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
                        foreach (var entry in ordered) {
                            var timer = expired[entry.Value];
                            timer.Expire(this, t);
                            Feed(timer, EV_TIMER);
                        }
                    }
                }

                if (stats != null) {
                    foreach (var stat in stats) {
                        if (stat._active && ReferenceEquals(stat._loop, this) && (statDirty || (stat._polling && stat._nextPoll <= t))) {
                            if (stat.Check(this, t)) {
                                Feed(stat, EV_STAT);
                            }
                        }
                    }
                }

                for (int i = 0; i < count; i++) {
                    var probe = probes[i];
                    if (probe.Ready == 0 || !probe.Watcher._active) {
                        continue;
                    }
                    if (probe.IO.Closed) {
                        // libev kills a watcher whose descriptor has gone away (EBADF): it is
                        // reported once, ready for everything it asked for, and stopped.
                        lock (_lock) {
                            probe.Watcher.Stop(this);
                        }
                    }
                    Feed(probe.Watcher, probe.Ready);
                }
            }

            /// <summary>
            /// What can be known without a syscall: a closed IO is ready for everything it was
            /// watched for, bytes in IronRuby's own buffer make an IO readable, an in-process pipe
            /// knows its own state, and a stream with no descriptor is ready for what its mode
            /// allows (as IO.select calls it).
            /// </summary>
            private static int ProbeWithoutKernel(ref Probe probe) {
                var io = probe.IO;
                if (io.Closed) {
                    return probe.Events;
                }

                int ready = 0;
                RubyBufferedStream stream;
                try {
                    stream = io.GetStream();
                } catch (Exception) {
                    return probe.Events;
                }
                if ((probe.Events & EV_READ) != 0 && stream.DataBuffered) {
                    ready |= EV_READ;
                }

                if (probe.Pipe != null) {
                    if ((probe.Events & EV_READ) != 0 && io.Mode.CanRead() && probe.Pipe.CanReadWithoutBlocking) {
                        ready |= EV_READ;
                    }
                    if ((probe.Events & EV_WRITE) != 0 && io.Mode.CanWrite() && probe.Pipe.CanWriteWithoutBlocking) {
                        ready |= EV_WRITE;
                    }
                } else if (probe.Watcher._windowsPipe != null) {
                    if ((probe.Events & EV_READ) != 0 && io.Mode.CanRead() && IoReadiness.IsWindowsPipeReadable(probe.Watcher._windowsPipe)) {
                        ready |= EV_READ;
                    }
                    if ((probe.Events & EV_WRITE) != 0 && io.Mode.CanWrite()) {
                        ready |= EV_WRITE;
                    }
                } else if (probe.Socket == null && probe.Descriptor < 0) {
                    if ((probe.Events & EV_READ) != 0 && io.Mode.CanRead()) {
                        ready |= EV_READ;
                    }
                    if ((probe.Events & EV_WRITE) != 0 && io.Mode.CanWrite()) {
                        ready |= EV_WRITE;
                    }
                }
                return ready;
            }

            /// <summary>
            /// One kernel wait over every probe with a descriptor or socket, the inotify
            /// descriptor and the waiter, of at most <paramref name="milliseconds"/>.  Adds what
            /// it finds to each probe's Ready and answers how many probes became ready.
            /// </summary>
            private int AskKernel(Probe[]/*!*/ probes, int count, int milliseconds, bool watchInotify, out bool inotifyEvent) {
                inotifyEvent = false;
                try {
                    if (RubyIO.HasPoll) {
                        return AskPoll(probes, count, milliseconds, watchInotify, out inotifyEvent);
                    }
                    return AskSelect(probes, count, milliseconds);
                } catch (ObjectDisposedException) {
                    // an IO closed by another thread mid-wait: the next round reports it closed
                    return 0;
                } catch (SocketException) {
                    return 0;
                } catch (ThreadInterruptedException) {
                    RubyUtils.TranslateThreadInterrupt();
                    return 0;
                }
            }

            private const short POLLIN = 0x001, POLLOUT = 0x004, POLLERR = 0x008, POLLHUP = 0x010, POLLNVAL = 0x020;

            private int AskPoll(Probe[]/*!*/ probes, int count, int milliseconds, bool watchInotify, out bool inotifyEvent) {
                inotifyEvent = false;
                var fds = new int[count + 2];
                var events = new short[count + 2];
                var owners = new int[count + 2];
                int n = 0;
                for (int i = 0; i < count; i++) {
                    int fd = probes[i].Descriptor;
                    if (fd < 0 || probes[i].IO.Closed) {
                        continue;
                    }
                    fds[n] = fd;
                    events[n] = (short)((((probes[i].Events & EV_READ) != 0) ? POLLIN : 0) |
                                        (((probes[i].Events & EV_WRITE) != 0) ? POLLOUT : 0));
                    owners[n] = i;
                    n++;
                }

                int inotifyIndex = -1;
                if (watchInotify) {
                    inotifyIndex = n;
                    fds[n] = _inotify.Descriptor;
                    events[n] = POLLIN;
                    owners[n] = -1;
                    n++;
                }

                int waiterIndex = -1;
                if (_waiter != null && milliseconds > 0) {
                    waiterIndex = n;
                    fds[n] = (int)_waiter.Socket.Handle;
                    events[n] = POLLIN;
                    owners[n] = -1;
                    n++;
                }

                if (n == 0) {
                    if (milliseconds > 0) {
                        Thread.Sleep(Math.Min(milliseconds, 1));
                    }
                    return 0;
                }

                var revents = new short[n];
                int result = RubyIO.Poll(fds, events, revents, n, milliseconds);
                if (result < 0) {
                    // poll itself failed (EBADF from a descriptor closed under us, say); do not spin
                    if (milliseconds > 0) {
                        Thread.Sleep(1);
                    }
                    return 0;
                }
                if (result == 0) {
                    return 0;
                }

                int newlyReady = 0;
                for (int j = 0; j < n; j++) {
                    if (revents[j] == 0 || j == waiterIndex) {
                        continue;
                    }
                    if (j == inotifyIndex) {
                        inotifyEvent = _inotify.Drain(this);
                        continue;
                    }
                    int i = owners[j];
                    int ready = 0;
                    // A hung-up or failed descriptor is ready in the sense that the next read or
                    // write returns at once, with EOF or the error - libev reports it the same way.
                    if ((revents[j] & (POLLIN | POLLERR | POLLHUP | POLLNVAL)) != 0) {
                        ready |= EV_READ;
                    }
                    if ((revents[j] & (POLLOUT | POLLERR | POLLHUP | POLLNVAL)) != 0) {
                        ready |= EV_WRITE;
                    }
                    ready &= probes[i].Events;
                    if (ready != 0) {
                        if (probes[i].Ready == 0) {
                            newlyReady++;
                        }
                        probes[i].Ready |= ready;
                    }
                }
                return newlyReady;
            }

            private int AskSelect(Probe[]/*!*/ probes, int count, int milliseconds) {
                var reads = new List<Socket>();
                var writes = new List<Socket>();
                var errors = new List<Socket>();
                var owners = new Dictionary<Socket, List<int>>();
                for (int i = 0; i < count; i++) {
                    var socket = probes[i].Socket;
                    if (probes[i].Watcher._windowsPipe != null) {
                        // Socket.Select cannot watch a pipe; look at it again in a millisecond.
                        milliseconds = Math.Min(milliseconds, 1);
                    }
                    if (socket == null || probes[i].IO.Closed) {
                        continue;
                    }
                    List<int> list;
                    if (!owners.TryGetValue(socket, out list)) {
                        owners[socket] = list = new List<int>();
                    }
                    list.Add(i);
                    if ((probes[i].Events & EV_READ) != 0 && !reads.Contains(socket)) {
                        reads.Add(socket);
                    }
                    if ((probes[i].Events & EV_WRITE) != 0 && !writes.Contains(socket)) {
                        writes.Add(socket);
                        // a connect that failed is reported in the error set, not the write set
                        errors.Add(socket);
                    }
                }
                if (_waiter != null && milliseconds > 0) {
                    reads.Add(_waiter.Socket);
                }

                if (reads.Count == 0 && writes.Count == 0) {
                    if (milliseconds > 0) {
                        Thread.Sleep(Math.Min(milliseconds, 1));
                    }
                    return 0;
                }

                Socket.Select(reads.Count > 0 ? reads : null, writes.Count > 0 ? writes : null,
                    errors.Count > 0 ? errors : null, milliseconds * 1000);

                int newlyReady = 0;
                newlyReady += Mark(probes, owners, reads, EV_READ);
                newlyReady += Mark(probes, owners, writes, EV_WRITE);
                newlyReady += Mark(probes, owners, errors, EV_WRITE);
                return newlyReady;
            }

            private static int Mark(Probe[]/*!*/ probes, Dictionary<Socket, List<int>>/*!*/ owners, List<Socket>/*!*/ sockets, int what) {
                int newlyReady = 0;
                foreach (var socket in sockets) {
                    List<int> list;
                    if (owners.TryGetValue(socket, out list)) {
                        foreach (int i in list) {
                            int ready = what & probes[i].Events;
                            if (ready == 0) {
                                continue;
                            }
                            if (probes[i].Ready == 0) {
                                newlyReady++;
                            }
                            probes[i].Ready |= ready;
                        }
                    }
                }
                return newlyReady;
            }

            #endregion
        }

        #endregion

        #region Watcher

        [RubyClass("Watcher")]
        public class Watcher : RubyObject {
            // The loop this watcher is attached to (Coolio's evloop), whether cool.io calls it
            // enabled, and whether libev would call it active.
            internal Loop _loop;
            internal bool _enabled;
            internal bool _active;

            public Watcher(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Watcher(ImmediateClass.NominalClass);
            }

            internal RubyContext/*!*/ Context {
                get { return ImmediateClass.Context; }
            }

            /// <summary>ev_TYPE_start; called with the loop's lock held.</summary>
            internal virtual void Start(Loop/*!*/ loop) {
            }

            /// <summary>ev_TYPE_stop; called with the loop's lock held.</summary>
            internal virtual void Stop(Loop/*!*/ loop) {
            }

            /// <summary>The extension's dispatch_callback: calls the Ruby callback.</summary>
            internal virtual void Dispatch(Loop/*!*/ loop, int revents) {
            }

            [RubyConstructor]
            public static Watcher/*!*/ Create(RubyClass/*!*/ self, params object[]/*!*/ args) {
                throw RubyExceptions.CreateRuntimeError("watcher base class should not be initialized directly");
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(Watcher/*!*/ self, params object[]/*!*/ args) {
                throw RubyExceptions.CreateRuntimeError("watcher base class should not be initialized directly");
            }

            // Coolio::Watcher#attach - the bookkeeping the subclasses' #attach finishes with
            // (rb_call_super): the loop's @watchers and @active_watchers.
            internal void AttachBookkeeping(object loop) {
                var context = Context;
                _enabled = true;

                object watchers;
                context.TryGetInstanceVariable(loop, "@watchers", out watchers);
                var hash = watchers as Hash;
                if (hash == null) {
                    // "we should never get here", says the extension
                    hash = new Hash(context);
                    context.SetInstanceVariable(loop, "@watchers", hash);
                }
                hash[this] = ScriptingRuntimeHelpers.True;

                object active;
                context.TryGetInstanceVariable(loop, "@active_watchers", out active);
                context.SetInstanceVariable(loop, "@active_watchers", ScriptingRuntimeHelpers.Int32ToObject(active == null ? 1 : GetIntIvar(context, loop, "@active_watchers") + 1));
            }

            internal void DetachBookkeeping() {
                var loop = _loop;
                if (loop == null) {
                    throw RubyExceptions.CreateRuntimeError("not attached to a loop");
                }
                var context = Context;

                object watchers;
                context.TryGetInstanceVariable(loop, "@watchers", out watchers);
                var hash = watchers as Hash;
                if (hash != null) {
                    hash.Remove(this);
                }

                if (_enabled) {
                    context.SetInstanceVariable(loop, "@active_watchers", ScriptingRuntimeHelpers.Int32ToObject(GetIntIvar(context, loop, "@active_watchers") - 1));
                }
                _enabled = false;

                // Pending events of this watcher are skipped by the dispatch in progress, so that
                // a watcher detached by an earlier callback is not called.
                loop.ForgetPending(this);
                _loop = null;
            }

            internal void EnableBookkeeping() {
                if (_enabled) {
                    throw RubyExceptions.CreateRuntimeError("already enabled");
                }
                _enabled = true;
                var context = Context;
                context.SetInstanceVariable(_loop, "@active_watchers", ScriptingRuntimeHelpers.Int32ToObject(GetIntIvar(context, _loop, "@active_watchers") + 1));
            }

            internal void DisableBookkeeping() {
                if (!_enabled) {
                    throw RubyExceptions.CreateRuntimeError("already disabled");
                }
                _enabled = false;
                var context = Context;
                context.SetInstanceVariable(_loop, "@active_watchers", ScriptingRuntimeHelpers.Int32ToObject(GetIntIvar(context, _loop, "@active_watchers") - 1));
            }

            internal static Loop/*!*/ RequireLoop(RubyContext/*!*/ context, object loop) {
                var result = loop as Loop;
                if (result == null) {
                    throw RubyExceptions.CreateArgumentError("expected loop to be an instance of Coolio::Loop, not {0}",
                        context.Inspect(loop).ToString());
                }
                return result;
            }

            // The Watcher_Detach / Watcher_Enable / Watcher_Disable macros and the #attach the
            // subclasses' methods are made of, as cool.io 1.11 has them: re-attaching calls
            // #detach, and detaching an enabled watcher calls #disable - both dynamically, so a
            // subclass that overrides them is told (1.9 called the C functions directly).

            internal void ReattachCheck() {
                if (_loop != null) {
                    _loop.CallDetach(this);
                }
            }

            internal void AttachTo(Loop/*!*/ loop) {
                ReattachCheck();
                _loop = loop;
                lock (loop._lock) {
                    Start(loop);
                }
                AttachBookkeeping(loop);
            }

            internal void DetachFromLoop() {
                var loop = _loop;
                if (loop == null) {
                    throw RubyExceptions.CreateRuntimeError("not attached to a loop");
                }
                if (_enabled) {
                    loop.CallDisable(this);
                }
                lock (loop._lock) {
                    // in case an overridden #disable did not get as far as stopping it
                    Stop(loop);
                }
                DetachBookkeeping();
            }

            internal void EnableOnLoop() {
                var loop = _loop;
                if (loop == null) {
                    throw RubyExceptions.CreateRuntimeError("not attached to a loop");
                }
                EnableBookkeeping();
                lock (loop._lock) {
                    Start(loop);
                }
            }

            internal void DisableOnLoop() {
                var loop = _loop;
                if (loop == null) {
                    throw RubyExceptions.CreateRuntimeError("not attached to a loop");
                }
                if (_enabled) {
                    lock (loop._lock) {
                        Stop(loop);
                    }
                }
                DisableBookkeeping();
            }

            [RubyMethod("attach")]
            public static Watcher/*!*/ Attach(Watcher/*!*/ self, object loop) {
                self.AttachBookkeeping(loop);
                return self;
            }

            [RubyMethod("detach")]
            public static Watcher/*!*/ Detach(Watcher/*!*/ self) {
                self.DetachBookkeeping();
                return self;
            }

            [RubyMethod("enable")]
            public static Watcher/*!*/ Enable(Watcher/*!*/ self) {
                if (self._loop == null) {
                    throw RubyExceptions.CreateRuntimeError("not attached to a loop");
                }
                self.EnableBookkeeping();
                return self;
            }

            [RubyMethod("disable")]
            public static Watcher/*!*/ Disable(Watcher/*!*/ self) {
                if (self._loop == null) {
                    throw RubyExceptions.CreateRuntimeError("not attached to a loop");
                }
                self.DisableBookkeeping();
                return self;
            }

            [RubyMethod("evloop")]
            public static object GetLoop(Watcher/*!*/ self) {
                return self._loop;
            }

            /// <summary>
            /// true or false, as cool.io 1.11 answers.  Before that the extension returned a C
            /// comparison as a VALUE, which made an attached watcher's answer the Integer 0
            /// (cool.io #132); it tests the same either way.
            /// </summary>
            [RubyMethod("attached?")]
            public static bool IsAttached(Watcher/*!*/ self) {
                return self._loop != null;
            }

            [RubyMethod("enabled?")]
            public static bool IsEnabled(Watcher/*!*/ self) {
                return self._enabled;
            }
        }

        #endregion

        #region IOWatcher

        [RubyClass("IOWatcher")]
        public sealed class IOWatcher : Watcher {
            internal object _ioObject;
            internal RubyIO _io;
            internal int _events;

            // What the loop hands the kernel for this IO, worked out once rather than on every
            // run_once, and again if the IO was given another stream (IO#reopen).
            internal Socket _socket;
            internal RubyPipe _pipe;
            internal FileStream _windowsPipe;
            internal int _descriptor = -1;
            private Stream _resolvedFor;

            public IOWatcher(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new IOWatcher(ImmediateClass.NominalClass);
            }

            internal void Resolve() {
                Stream stream;
                try {
                    stream = _io.GetStream().BaseStream;
                } catch (Exception) {
                    return;
                }
                if (ReferenceEquals(stream, _resolvedFor)) {
                    return;
                }
                _resolvedFor = stream;
                _socket = IoReadiness.GetSocket(_io);
                _pipe = (_socket == null) ? IoReadiness.GetPipe(_io) : null;
                _windowsPipe = (_socket == null && _pipe == null) ? IoReadiness.GetWindowsPipe(_io) : null;
                _descriptor = !RubyIO.HasPoll ? -1
                    : (_socket != null) ? (int)_socket.Handle
                    : (_pipe != null) ? -1
                    : _io.KernelDescriptor;
            }

            [RubyConstructor]
            public static IOWatcher/*!*/ Create(RespondToStorage/*!*/ respondToStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
                ConversionStorage<MutableString>/*!*/ stringCast,
                RubyClass/*!*/ self, object io, [Optional]object flags) {
                var result = new IOWatcher(self);
                Initialize(respondToStorage, toIoStorage, stringCast, result, io, flags);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(RespondToStorage/*!*/ respondToStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
                ConversionStorage<MutableString>/*!*/ stringCast,
                IOWatcher/*!*/ self, object io, [Optional]object flags) {

                var context = self.Context;
                string what = (flags == null || flags is Missing) ? "r" : StringOf(stringCast, context, flags);
                int events;
                switch (what) {
                    case "r": events = EV_READ; break;
                    case "w": events = EV_WRITE; break;
                    case "rw": events = EV_READ | EV_WRITE; break;
                    default:
                        throw RubyExceptions.CreateArgumentError("invalid event type: '{0}' (must be 'r', 'w', or 'rw')", what);
                }

                var rubyIO = NioModule.ToIO(respondToStorage, toIoStorage, context, io);
                if (self._loop != null) {
                    // re-initializing an attached watcher: libev would be handed a running
                    // watcher to reinitialise; keep the loop consistent instead
                    self.DetachFromLoop();
                }
                self._ioObject = io;
                self._io = rubyIO;
                self._events = events;
                self._resolvedFor = null;
                return null;
            }

            internal override void Start(Loop/*!*/ loop) {
                if (_active) {
                    return;
                }
                _active = true;
                loop._ios.Add(this);
                loop.Changed();
            }

            internal override void Stop(Loop/*!*/ loop) {
                if (!_active) {
                    return;
                }
                _active = false;
                loop._ios.Remove(this);
                loop.Changed();
            }

            internal override void Dispatch(Loop/*!*/ loop, int revents) {
                if ((revents & EV_READ) != 0) {
                    loop.CallOnReadable(this);
                } else if ((revents & EV_WRITE) != 0) {
                    loop.CallOnWritable(this);
                } else {
                    throw RubyExceptions.CreateRuntimeError("unknown revents value for ev_io: {0}", revents);
                }
            }

            [RubyMethod("attach")]
            public static IOWatcher/*!*/ Attach(IOWatcher/*!*/ self, object loop) {
                self.AttachTo(RequireLoop(self.Context, loop));
                return self;
            }

            [RubyMethod("detach")]
            public static IOWatcher/*!*/ Detach(IOWatcher/*!*/ self) {
                self.DetachFromLoop();
                return self;
            }

            [RubyMethod("enable")]
            public static IOWatcher/*!*/ Enable(IOWatcher/*!*/ self) {
                self.EnableOnLoop();
                return self;
            }

            [RubyMethod("disable")]
            public static IOWatcher/*!*/ Disable(IOWatcher/*!*/ self) {
                self.DisableOnLoop();
                return self;
            }

            [RubyMethod("on_readable")]
            public static object OnReadable(IOWatcher/*!*/ self) {
                return null;
            }

            [RubyMethod("on_writable")]
            public static object OnWritable(IOWatcher/*!*/ self) {
                return null;
            }
        }

        /// <summary>rb_String: #to_str, then #to_s.</summary>
        internal static string/*!*/ StringOf(ConversionStorage<MutableString>/*!*/ stringCast, RubyContext/*!*/ context, object obj) {
            var str = obj as MutableString;
            if (str != null) {
                return str.ToString();
            }
            var symbol = obj as RubySymbol;
            if (symbol != null) {
                return symbol.ToString();
            }
            return Protocols.ConvertToString(stringCast, obj).ToString();
        }

        #endregion

        #region TimerWatcher

        [RubyClass("TimerWatcher")]
        public sealed class TimerWatcher : Watcher {
            // libev's ev_at: relative to now while stopped, absolute while started.
            internal double _at;
            internal double _repeat;

            public TimerWatcher(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new TimerWatcher(ImmediateClass.NominalClass);
            }

            [RubyConstructor]
            public static TimerWatcher/*!*/ Create(ToFloatStorage/*!*/ floatConversion,
                RubyClass/*!*/ self, object interval, [Optional]object repeating) {
                var result = new TimerWatcher(self);
                Initialize(floatConversion, result, interval, repeating);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(ToFloatStorage/*!*/ floatConversion,
                TimerWatcher/*!*/ self, object interval, [Optional]object repeating) {

                if (repeating is Missing) {
                    repeating = null;
                }
                double seconds = floatConversion.Convert(interval);
                var context = self.Context;
                context.SetInstanceVariable(self, "@interval", seconds);
                context.SetInstanceVariable(self, "@repeating", repeating);

                if (self._loop != null) {
                    self.DetachFromLoop();
                }
                self._at = seconds;
                self._repeat = (repeating is bool && (bool)repeating) ? seconds : 0;
                return null;
            }

            internal override void Start(Loop/*!*/ loop) {
                if (_active) {
                    return;
                }
                _at += Now();
                _active = true;
                loop._timers.Add(this);
                loop.Changed();
            }

            internal override void Stop(Loop/*!*/ loop) {
                if (!_active) {
                    return;
                }
                _at -= Now();
                _active = false;
                loop._timers.Remove(this);
                loop.Changed();
            }

            /// <summary>timers_reify: a repeating timer goes round again, a one-shot one stops.</summary>
            internal void Expire(Loop/*!*/ loop, double now) {
                if (_repeat > 0) {
                    _at += _repeat;
                    if (_at < now) {
                        _at = now;
                    }
                } else {
                    Stop(loop);
                }
            }

            internal override void Dispatch(Loop/*!*/ loop, int revents) {
                loop.CallOnTimer(this);
            }

            [RubyMethod("attach")]
            public static TimerWatcher/*!*/ Attach(ToFloatStorage/*!*/ floatConversion, TimerWatcher/*!*/ self, object loop) {
                var evloop = RequireLoop(self.Context, loop);
                self.ReattachCheck();
                self._loop = evloop;

                // ev_timer_set from the instance variables, which is where #attach reads them
                var context = self.Context;
                object interval, repeating;
                context.TryGetInstanceVariable(self, "@interval", out interval);
                context.TryGetInstanceVariable(self, "@repeating", out repeating);
                double seconds = floatConversion.Convert(interval);
                lock (evloop._lock) {
                    self._at = seconds;
                    self._repeat = (repeating is bool && (bool)repeating) ? seconds : 0;
                    self.Start(evloop);
                }
                self.AttachBookkeeping(evloop);
                return self;
            }

            [RubyMethod("detach")]
            public static TimerWatcher/*!*/ Detach(TimerWatcher/*!*/ self) {
                self.DetachFromLoop();
                return self;
            }

            [RubyMethod("enable")]
            public static TimerWatcher/*!*/ Enable(TimerWatcher/*!*/ self) {
                self.EnableOnLoop();
                return self;
            }

            [RubyMethod("disable")]
            public static TimerWatcher/*!*/ Disable(TimerWatcher/*!*/ self) {
                self.DisableOnLoop();
                return self;
            }

            /// <summary>
            /// ev_timer_again: a started timer restarts its interval from now (or stops, if it
            /// does not repeat); a stopped repeating one starts.
            /// </summary>
            [RubyMethod("reset")]
            public static TimerWatcher/*!*/ Reset(TimerWatcher/*!*/ self) {
                var loop = self._loop;
                if (loop == null) {
                    throw RubyExceptions.CreateRuntimeError("not attached to a loop");
                }
                lock (loop._lock) {
                    if (self._active) {
                        if (self._repeat > 0) {
                            self._at = Now() + self._repeat;
                            loop.Changed();
                        } else {
                            self.Stop(loop);
                        }
                    } else if (self._repeat > 0) {
                        self._at = self._repeat;
                        self.Start(loop);
                    }
                }
                return self;
            }

            [RubyMethod("on_timer")]
            public static object OnTimer(TimerWatcher/*!*/ self) {
                return null;
            }
        }

        /// <summary>
        /// rb_convert_type(obj, T_FLOAT, "Float", "to_f"), which the watchers' initializers use:
        /// whatever answers #to_f with a Float - nil included, as nil.to_f is 0.0.
        /// </summary>
        public sealed class ToFloatStorage : RubyCallSiteStorage {
            public ToFloatStorage(RubyContext/*!*/ context) : base(context) { }

            private CallSite<Func<CallSite, object, object, object>> _respondTo;
            private CallSite<Func<CallSite, object, object>> _toF;

            internal double Convert(object obj) {
                if (obj is double) {
                    return (double)obj;
                }
                var context = Context;
                var respondTo = RubyUtils.GetCallSite(ref _respondTo, context, "respond_to?", RubyCallSignature.WithImplicitSelf(1));
                if (!Protocols.RespondTo(respondTo, context, obj, "to_f")) {
                    throw RubyExceptions.CreateTypeError("can't convert {0} into Float", ConversionTarget(context, obj));
                }
                var toF = RubyUtils.GetCallSite(ref _toF, context, "to_f", RubyCallSignature.WithImplicitSelf(0));
                object result = toF.Target(toF, obj);
                if (result is double) {
                    return (double)result;
                }
                throw RubyExceptions.CreateTypeError("can't convert {0} to Float ({0}#to_f gives {1})",
                    context.GetClassDisplayName(obj), context.GetClassDisplayName(result));
            }

            private static string/*!*/ ConversionTarget(RubyContext/*!*/ context, object obj) {
                if (obj == null) {
                    return "nil";
                }
                if (obj is bool) {
                    return (bool)obj ? "true" : "false";
                }
                return context.GetClassDisplayName(obj);
            }
        }

        #endregion

        #region StatWatcher

        /// <summary>The fields of struct stat that libev's ev_stat compares and cool.io reports.</summary>
        internal struct StatData {
            public long Dev, Ino, Rdev, Size, Blocks;
            public int Mode, Nlink, Uid, Gid, BlockSize;
            public long ATime, MTime, CTime;   // seconds, as st_atime is

            public bool SameAs(ref StatData other) {
                return Dev == other.Dev && Ino == other.Ino && Mode == other.Mode && Nlink == other.Nlink
                    && Uid == other.Uid && Gid == other.Gid && Rdev == other.Rdev && Size == other.Size
                    && ATime == other.ATime && MTime == other.MTime && CTime == other.CTime;
            }
        }

        [RubyClass("StatWatcher")]
        public sealed class StatWatcher : Watcher {
            internal string _path;
            internal double _interval;
            internal StatData _attr, _prev;

            // Whether the path is polled (no inotify watch on the file itself), and when next.
            internal bool _polling;
            internal double _nextPoll;
            internal int _wd = -1;

            public StatWatcher(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new StatWatcher(ImmediateClass.NominalClass);
            }

            [RubyConstructor]
            public static StatWatcher/*!*/ Create(ToFloatStorage/*!*/ floatConversion,
                ConversionStorage<MutableString>/*!*/ stringCast,
                RubyClass/*!*/ self, object path, [Optional]object interval) {
                var result = new StatWatcher(self);
                Initialize(floatConversion, stringCast, result, path, interval);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(ToFloatStorage/*!*/ floatConversion,
                ConversionStorage<MutableString>/*!*/ stringCast,
                StatWatcher/*!*/ self, object path, [Optional]object interval) {

                var str = path as MutableString ?? MutableString.Create(StringOf(stringCast, self.Context, path), RubyEncoding.UTF8);
                if (str.ToString().IndexOf('\0') >= 0) {
                    // StringValueCStr
                    throw RubyExceptions.CreateArgumentError("string contains null byte");
                }
                double seconds = (interval == null || interval is Missing) ? 0 : floatConversion.Convert(interval);
                self.Context.SetInstanceVariable(self, "@path", str);

                if (self._loop != null) {
                    self.DetachFromLoop();
                }
                self._path = str.ToString();
                self._interval = seconds;
                return null;
            }

            private double PollInterval {
                get {
                    if (_interval == 0) {
                        return DefaultStatInterval;
                    }
                    return Math.Max(_interval, MinStatInterval);
                }
            }

            /// <summary>
            /// ev_stat_stat: lstat(2) the path.  A path that is not there keeps the last answer
            /// with st_nlink 0, as libev's does (lstat leaves the buffer alone when it fails).
            /// </summary>
            private void Stat() {
                StatData data;
                if (TryLStat(_path, out data)) {
                    if (data.Nlink == 0) {
                        data.Nlink = 1;
                    }
                    _attr = data;
                } else {
                    _attr.Nlink = 0;
                }
            }

            internal override void Start(Loop/*!*/ loop) {
                if (_active) {
                    return;
                }
                Stat();
                _active = true;
                Watch(loop);
                loop._stats.Add(this);
                loop.Changed();
            }

            internal override void Stop(Loop/*!*/ loop) {
                if (!_active) {
                    return;
                }
                _active = false;
                Unwatch(loop);
                loop._stats.Remove(this);
                loop.Changed();
            }

            /// <summary>
            /// infy_add: an inotify watch on the file, so that nothing need be polled; failing
            /// that (no inotify, or no file yet), the nearest existing directory above it is
            /// watched for hints and the path is polled at the interval.
            /// </summary>
            private void Watch(Loop/*!*/ loop) {
                var inotify = loop.GetInotify();
                _polling = true;
                _wd = -1;
                if (inotify != null) {
                    _wd = inotify.AddFile(_path);
                    if (_wd >= 0) {
                        _polling = false;
                    } else {
                        _wd = inotify.AddNearestDirectory(_path);
                    }
                }
                _nextPoll = Now() + PollInterval;
            }

            private void Unwatch(Loop/*!*/ loop) {
                if (_wd >= 0 && loop._inotify != null) {
                    int wd = _wd;
                    _wd = -1;
                    // another watcher may share the watch (same inode); it keeps it
                    foreach (var other in loop._stats) {
                        if (!ReferenceEquals(other, this) && other._wd == wd) {
                            return;
                        }
                    }
                    loop._inotify.Remove(wd);
                }
            }

            /// <summary>
            /// stat_timer_cb: lstat again and report whether anything libev compares changed.  A
            /// change re-arms the inotify watch, the file now being another one, or gone.
            /// </summary>
            internal bool Check(Loop/*!*/ loop, double now) {
                var previous = _attr;
                Stat();
                if (_polling) {
                    _nextPoll = now + PollInterval;
                }
                if (previous.SameAs(ref _attr)) {
                    return false;
                }
                _prev = previous;
                lock (loop._lock) {
                    if (_active) {
                        Unwatch(loop);
                        Watch(loop);
                        loop.Changed();
                    }
                }
                return true;
            }

            internal void Rewatch(Loop/*!*/ loop) {
                // the watch was removed under us (IN_IGNORED: the file was deleted or moved)
                _wd = -1;
                Watch(loop);
            }

            internal override void Dispatch(Loop/*!*/ loop, int revents) {
                var context = Context;
                object structClass;
                RubyClass statInfo = null;
                if (context.GetClass(typeof(RubyStruct)).TryGetConstant(null, "StatInfo", out structClass)) {
                    statInfo = structClass as RubyClass;
                }
                if (statInfo == null) {
                    throw RubyExceptions.CreateNameError("uninitialized constant Struct::StatInfo");
                }
                object previous = BuildStatInfo(statInfo, ref _prev);
                object current = BuildStatInfo(statInfo, ref _attr);
                loop.CallOnChange(this, previous, current);
            }

            private static object/*!*/ BuildStatInfo(RubyClass/*!*/ statInfo, ref StatData data) {
                var result = RubyStruct.Create(statInfo);
                result.SetValues(new object[] {
                    TimeAt(data.MTime),
                    TimeAt(data.CTime),
                    TimeAt(data.ATime),
                    Integer(data.Dev),
                    Integer(data.Ino),
                    data.Mode,
                    data.Nlink,
                    data.Uid,
                    data.Gid,
                    Integer(data.Rdev),
                    Integer(data.Size),
                    data.BlockSize,
                    Integer(data.Blocks),
                });
                return result;
            }

            private static object/*!*/ Integer(long value) {
                return (value >= Int32.MinValue && value <= Int32.MaxValue) ? ScriptingRuntimeHelpers.Int32ToObject((int)value) : (object)(System.Numerics.BigInteger)value;
            }

            private static RubyTime/*!*/ TimeAt(long seconds) {
                return RubyTime.FromExactSeconds(ExactNum.FromInteger(seconds), RubyTimeZoneKind.Local, ExactNum.Zero);
            }

            [RubyMethod("attach")]
            public static StatWatcher/*!*/ Attach(StatWatcher/*!*/ self, object loop) {
                self.AttachTo(RequireLoop(self.Context, loop));
                return self;
            }

            [RubyMethod("detach")]
            public static StatWatcher/*!*/ Detach(StatWatcher/*!*/ self) {
                self.DetachFromLoop();
                return self;
            }

            [RubyMethod("enable")]
            public static StatWatcher/*!*/ Enable(StatWatcher/*!*/ self) {
                self.EnableOnLoop();
                return self;
            }

            [RubyMethod("disable")]
            public static StatWatcher/*!*/ Disable(StatWatcher/*!*/ self) {
                self.DisableOnLoop();
                return self;
            }

            [RubyMethod("on_change")]
            public static object OnChange(StatWatcher/*!*/ self, object previous, object current) {
                return null;
            }

            [RubyMethod("path")]
            public static object GetPath(StatWatcher/*!*/ self) {
                object path;
                self.Context.TryGetInstanceVariable(self, "@path", out path);
                return path;
            }
        }

        internal static bool TryLStat(string/*!*/ path, out StatData result) {
            result = new StatData();
            if (Posix.IsAvailable) {
                Posix.StatData data;
                int errno;
                if (!Posix.TryStat(path, false, out data, out errno)) {
                    return false;
                }
                result.Dev = data.Dev;
                result.Ino = data.Ino;
                result.Mode = data.Mode;
                result.Nlink = data.Nlink;
                result.Uid = data.Uid;
                result.Gid = data.Gid;
                result.Rdev = data.Rdev;
                result.Size = data.Size;
                result.BlockSize = data.BlockSize;
                result.Blocks = data.Blocks;
                result.ATime = data.ATimeSec;
                result.MTime = data.MTimeSec;
                result.CTime = data.CTimeSec;
                return true;
            }

            // Windows: what FileSystemInfo knows.
            try {
                FileSystemInfo info = new FileInfo(path);
                if (!info.Exists) {
                    info = new DirectoryInfo(path);
                    if (!info.Exists) {
                        return false;
                    }
                    result.Mode = 0x4000 | 0x1ED;
                } else {
                    result.Size = ((FileInfo)info).Length;
                    result.Mode = 0x8000 | (((info.Attributes & FileAttributes.ReadOnly) != 0) ? 0x124 : 0x1A4);
                }
                result.Nlink = 1;
                result.ATime = new DateTimeOffset(info.LastAccessTimeUtc).ToUnixTimeSeconds();
                result.MTime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
                result.CTime = new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeSeconds();
                return true;
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// inotify(7), one descriptor per loop, which the loop's poll(2) watches next to the IOs -
        /// libev's EV_USE_INOTIFY.  Linux only; elsewhere StatWatcher polls.
        /// </summary>
        internal sealed class Inotify {
            private const int IN_NONBLOCK = 0x800, IN_CLOEXEC = 0x80000;
            private const uint IN_MODIFY = 0x2, IN_ATTRIB = 0x4, IN_MOVED_FROM = 0x40, IN_MOVED_TO = 0x80,
                IN_CREATE = 0x100, IN_DELETE = 0x200, IN_DELETE_SELF = 0x400, IN_MOVE_SELF = 0x800,
                IN_UNMOUNT = 0x2000, IN_Q_OVERFLOW = 0x4000, IN_IGNORED = 0x8000,
                IN_DONT_FOLLOW = 0x2000000, IN_MASK_ADD = 0x20000000;

            // libev's mask for a watched file, and for a directory watched for hints
            private const uint FileMask = IN_ATTRIB | IN_DELETE_SELF | IN_MOVE_SELF | IN_MODIFY | IN_CREATE | IN_DELETE
                | IN_MOVED_FROM | IN_MOVED_TO | IN_DONT_FOLLOW | IN_MASK_ADD;
            private const uint DirectoryMask = FileMask;

            [DllImport("libc", SetLastError = true)]
            private static extern int inotify_init1(int flags);

            [DllImport("libc", SetLastError = true)]
            private static extern int inotify_add_watch(int fd, byte[] path, uint mask);

            [DllImport("libc", SetLastError = true)]
            private static extern int inotify_rm_watch(int fd, int wd);

            [DllImport("libc", SetLastError = true)]
            private static extern IntPtr read(int fd, byte[] buffer, IntPtr count);

            [DllImport("libc", SetLastError = true)]
            private static extern int close(int fd);

            private readonly int _fd;
            private readonly byte[]/*!*/ _buffer = new byte[4096];

            private Inotify(int fd) {
                _fd = fd;
            }

            ~Inotify() {
                close(_fd);
            }

            internal static Inotify Create() {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || !RubyIO.HasPoll) {
                    return null;
                }
                try {
                    int fd = inotify_init1(IN_NONBLOCK | IN_CLOEXEC);
                    return fd >= 0 ? new Inotify(fd) : null;
                } catch (Exception) {
                    return null;
                }
            }

            internal int Descriptor {
                get { return _fd; }
            }

            /// <summary>Whether any active StatWatcher of the loop holds a watch: the lock is held.</summary>
            internal static bool Wanted(Loop/*!*/ loop) {
                foreach (var stat in loop._stats) {
                    if (stat._wd >= 0) {
                        return true;
                    }
                }
                return false;
            }

            private static byte[]/*!*/ ToPath(string/*!*/ path) {
                return Encoding.UTF8.GetBytes(path + "\0");
            }

            internal int AddFile(string/*!*/ path) {
                return inotify_add_watch(_fd, ToPath(path), FileMask);
            }

            internal int AddNearestDirectory(string/*!*/ path) {
                string dir = path;
                try {
                    dir = System.IO.Path.GetFullPath(path);
                } catch (Exception) {
                }
                while (true) {
                    string parent;
                    try {
                        parent = System.IO.Path.GetDirectoryName(dir);
                    } catch (Exception) {
                        return -1;
                    }
                    if (String.IsNullOrEmpty(parent) || parent == dir) {
                        return -1;
                    }
                    dir = parent;
                    int wd = inotify_add_watch(_fd, ToPath(dir), DirectoryMask);
                    if (wd >= 0) {
                        return wd;
                    }
                }
            }

            internal void Remove(int wd) {
                inotify_rm_watch(_fd, wd);
            }

            /// <summary>
            /// Reads every queued event.  Watches the kernel dropped (IN_IGNORED: the file went
            /// away) are added again, as libev re-adds them.  Answers whether anything happened.
            /// </summary>
            internal bool Drain(Loop/*!*/ loop) {
                bool any = false;
                List<int> ignored = null;
                while (true) {
                    long n = (long)read(_fd, _buffer, (IntPtr)_buffer.Length);
                    if (n <= 0) {
                        break;
                    }
                    any = true;
                    int offset = 0;
                    while (offset + 16 <= n) {
                        int wd = BitConverter.ToInt32(_buffer, offset);
                        uint mask = BitConverter.ToUInt32(_buffer, offset + 4);
                        int len = BitConverter.ToInt32(_buffer, offset + 12);
                        if ((mask & (IN_IGNORED | IN_UNMOUNT)) != 0) {
                            (ignored ?? (ignored = new List<int>())).Add(wd);
                        }
                        offset += 16 + len;
                    }
                }
                if (ignored != null) {
                    lock (loop._lock) {
                        foreach (int wd in ignored) {
                            foreach (var stat in loop._stats) {
                                if (stat._wd == wd) {
                                    stat.Rewatch(loop);
                                }
                            }
                        }
                        loop.Changed();
                    }
                }
                return any;
            }
        }

        #endregion

        #region Buffer

        /// <summary>The #&lt; and #&gt; convert_node_size asks a size.</summary>
        public sealed class NodeSizeStorage : RubyCallSiteStorage {
            public NodeSizeStorage(RubyContext/*!*/ context) : base(context) { }

            private CallSite<Func<CallSite, object, object, object>> _lessThan, _greaterThan;

            internal CallSite<Func<CallSite, object, object, object>>/*!*/ LessThan {
                get { return RubyUtils.GetCallSite(ref _lessThan, Context, "<", 1); }
            }

            internal CallSite<Func<CallSite, object, object, object>>/*!*/ GreaterThan {
                get { return RubyUtils.GetCallSite(ref _greaterThan, Context, ">", 1); }
            }
        }

        /// <summary>
        /// Coolio::Buffer: a byte queue with non-blocking reads and writes to an IO.  The C keeps
        /// a pooled list of node_size-byte chunks; the chunk size here only decides how much one
        /// read asks the IO for, which is all it is visible as.
        /// </summary>
        [RubyClass("Buffer")]
        public sealed class Buffer : RubyObject {
            internal const int MaxBufferSize = 0x40000000;
            private const int DefaultNodeSize = 16384;

            private static int _defaultNodeSize = DefaultNodeSize;

            private byte[]/*!*/ _data = new byte[0];
            private int _start, _count;
            private int _nodeSize = _defaultNodeSize;

            public Buffer(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Buffer(ImmediateClass.NominalClass);
            }

            [RubyConstant("MAX_SIZE")]
            public const int MAX_SIZE = MaxBufferSize;

            /// <summary>convert_node_size: size &lt; 1 or size &gt; MAX_SIZE, asked of the object itself.</summary>
            private static int ConvertNodeSize(NodeSizeStorage/*!*/ storage, ConversionStorage<int>/*!*/ fixnumCast, object size) {
                var lt = storage.LessThan;
                var gt = storage.GreaterThan;
                if (Protocols.IsTrue(lt.Target(lt, size, ScriptingRuntimeHelpers.Int32ToObject(1))) ||
                    Protocols.IsTrue(gt.Target(gt, size, ScriptingRuntimeHelpers.Int32ToObject(MaxBufferSize)))) {
                    throw RubyExceptions.CreateArgumentError("invalid buffer size");
                }
                return Protocols.CastToFixnum(fixnumCast, size);
            }

            [RubyMethod("default_node_size", RubyMethodAttributes.PublicSingleton)]
            public static int GetDefaultNodeSize(RubyClass/*!*/ self) {
                return _defaultNodeSize;
            }

            [RubyMethod("default_node_size=", RubyMethodAttributes.PublicSingleton)]
            public static object SetDefaultNodeSize(NodeSizeStorage/*!*/ storage, ConversionStorage<int>/*!*/ fixnumCast, RubyClass/*!*/ self, object size) {
                _defaultNodeSize = ConvertNodeSize(storage, fixnumCast, size);
                return size;
            }

            [RubyConstructor]
            public static Buffer/*!*/ Create(NodeSizeStorage/*!*/ storage, ConversionStorage<int>/*!*/ fixnumCast, RubyClass/*!*/ self, [Optional]object size) {
                var result = new Buffer(self);
                Initialize(storage, fixnumCast, result, size);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(NodeSizeStorage/*!*/ storage, ConversionStorage<int>/*!*/ fixnumCast, Buffer/*!*/ self, [Optional]object size) {
                if (!(size is Missing)) {
                    self._nodeSize = ConvertNodeSize(storage, fixnumCast, size);
                }
                return null;
            }

            #region the queue

            private void EnsureRoom(int more) {
                long needed = (long)_count + more;
                if (needed > Int32.MaxValue) {
                    throw new OutOfMemoryException();
                }
                if (_start + needed <= _data.Length) {
                    return;
                }
                if (needed <= _data.Length && _start > 0 && _count <= _data.Length / 2) {
                    System.Buffer.BlockCopy(_data, _start, _data, 0, _count);
                    _start = 0;
                    return;
                }
                long size = Math.Max(Math.Max(_data.Length * 2L, needed), 64);
                var data = new byte[Math.Min(size, Int32.MaxValue)];
                System.Buffer.BlockCopy(_data, _start, data, 0, _count);
                _data = data;
                _start = 0;
            }

            private void Append(byte[]/*!*/ bytes, int offset, int length) {
                EnsureRoom(length);
                System.Buffer.BlockCopy(bytes, offset, _data, _start + _count, length);
                _count += length;
            }

            private void Prepend(byte[]/*!*/ bytes) {
                if (bytes.Length <= _start) {
                    _start -= bytes.Length;
                    System.Buffer.BlockCopy(bytes, 0, _data, _start, bytes.Length);
                    _count += bytes.Length;
                    return;
                }
                var data = new byte[Math.Max(_count + bytes.Length, Math.Max(64, _data.Length))];
                System.Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
                System.Buffer.BlockCopy(_data, _start, data, bytes.Length, _count);
                _data = data;
                _start = 0;
                _count += bytes.Length;
            }

            private void Consume(int length) {
                _start += length;
                _count -= length;
                if (_count == 0) {
                    _start = 0;
                }
            }

            #endregion

            [RubyMethod("clear")]
            public static object Clear(Buffer/*!*/ self) {
                self._start = self._count = 0;
                return null;
            }

            [RubyMethod("size")]
            public static int Size(Buffer/*!*/ self) {
                return self._count;
            }

            [RubyMethod("empty?")]
            public static bool IsEmpty(Buffer/*!*/ self) {
                return self._count == 0;
            }

            [RubyMethod("<<")]
            [RubyMethod("append")]
            [RubyMethod("write")]
            public static MutableString/*!*/ Append(Buffer/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ data) {
                var bytes = data.ToByteArray();
                self.Append(bytes, 0, bytes.Length);
                return data;
            }

            [RubyMethod("prepend")]
            public static MutableString/*!*/ Prepend(Buffer/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ data) {
                self.Prepend(data.ToByteArray());
                return data;
            }

            /// <summary>
            /// The whole buffer, or at most length bytes of it.  The length is converted with
            /// #to_int and may be any Integer: below one is an ArgumentError (even for an empty
            /// buffer), and more than the buffer holds is the whole buffer, however large.
            /// </summary>
            [RubyMethod("read")]
            public static MutableString/*!*/ Read(ConversionStorage<IntegerValue>/*!*/ integerConversion, Buffer/*!*/ self, [Optional]object length) {
                int count;
                if (!(length is Missing)) {
                    var requested = Protocols.CastToInteger(integerConversion, length);
                    if (requested.IsFixnum ? requested.Fixnum < 1 : requested.Bignum.Sign < 0) {
                        throw RubyExceptions.CreateArgumentError("length must be greater than zero");
                    }
                    count = (requested.IsFixnum && requested.Fixnum < self._count) ? requested.Fixnum : self._count;
                } else {
                    count = self._count;
                }
                if (self._count == 0) {
                    return MutableString.CreateBinary();
                }
                var result = MutableString.CreateBinary(count);
                result.Append(self._data, self._start, count);
                self.Consume(count);
                return result;
            }

            [RubyMethod("read_frame")]
            public static bool ReadFrame(ConversionStorage<int>/*!*/ fixnumCast, Buffer/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ data, object mark) {
                byte markByte = unchecked((byte)Protocols.CastToFixnum(fixnumCast, mark));
                if (self._count == 0) {
                    return false;
                }
                int index = Array.IndexOf(self._data, markByte, self._start, self._count);
                int length = (index >= 0) ? index - self._start + 1 : self._count;
                data.Append(self._data, self._start, length);
                self.Consume(length);
                return index >= 0;
            }

            [RubyMethod("to_str")]
            public static MutableString/*!*/ ToStr(Buffer/*!*/ self) {
                var result = MutableString.CreateBinary(self._count);
                result.Append(self._data, self._start, self._count);
                return result;
            }

            /// <summary>
            /// Reads what the IO has without waiting: until a read comes back short or would
            /// block.  nil at end of file (what was read before it stays in the buffer).
            /// </summary>
            [RubyMethod("read_from")]
            public static object ReadFrom(RespondToStorage/*!*/ respondToStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
                Buffer/*!*/ self, object io) {

                var rubyIO = NioModule.ToIO(respondToStorage, toIoStorage, self.ImmediateClass.Context, io);
                int total = 0;
                while (true) {
                    int want = self._nodeSize - (self._count % self._nodeSize);
                    MutableString chunk;
                    try {
                        chunk = RubyIOOps.ReadNoBlock(rubyIO, want, null);
                    } catch (EOFError) {
                        return null;
                    } catch (Errno.ResourceTemporarilyUnavailableError) {
                        return total;
                    }
                    int got = chunk.GetByteCount();
                    if (got == 0) {
                        return null;
                    }
                    var bytes = chunk.ToByteArray();
                    self.Append(bytes, 0, got);
                    total += got;
                    if (got < want) {
                        return total;
                    }
                }
            }

            /// <summary>
            /// Writes as much as the IO takes without waiting; what was written leaves the
            /// buffer.  Answers how many bytes that was.
            /// </summary>
            [RubyMethod("write_to")]
            public static int WriteTo(RespondToStorage/*!*/ respondToStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
                Buffer/*!*/ self, object io) {

                var rubyIO = NioModule.ToIO(respondToStorage, toIoStorage, self.ImmediateClass.Context, io);
                int total = 0;
                while (self._count > 0) {
                    int length = Math.Min(self._count, self._nodeSize);
                    var chunk = MutableString.CreateBinary(length);
                    chunk.Append(self._data, self._start, length);
                    int written;
                    try {
                        written = RubyIOOps.WriteNoBlock(rubyIO, chunk);
                    } catch (Errno.ResourceTemporarilyUnavailableError) {
                        return total;
                    }
                    if (written <= 0) {
                        return total;
                    }
                    self.Consume(written);
                    total += written;
                    if (written < length) {
                        return total;
                    }
                }
                return total;
            }
        }

        #endregion

        #region Utils

        [RubyModule("Utils")]
        public static class UtilsModule {
            [DllImport("libc", SetLastError = true)]
            private static extern int getrlimit(int resource, ulong[] rlim);

            [DllImport("libc", SetLastError = true)]
            private static extern int setrlimit(int resource, ulong[] rlim);

            // RLIMIT_NOFILE: 7 on Linux, 8 on the BSDs and macOS
            private static int RlimitNoFile {
                get { return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? 7 : 8; }
            }

            [RubyMethod("ncpus", RubyMethodAttributes.PublicSingleton)]
            public static int NCpus(RubyModule/*!*/ self) {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                    // the extension counts /proc/cpuinfo's "processor" lines: every CPU the
                    // machine has, not the ones this process may run on
                    int count = 0;
                    try {
                        foreach (var line in File.ReadLines("/proc/cpuinfo")) {
                            if (line.StartsWith("processor", StringComparison.Ordinal)) {
                                count++;
                            }
                        }
                    } catch (Exception) {
                        throw RubyExceptions.CreateSystemCallError("fopen");
                    }
                    return count;
                }
                if (_isUnix) {
                    return Environment.ProcessorCount;
                }
                throw RubyExceptions.CreateRuntimeError("operation not supported");
            }

            [RubyMethod("maxfds", RubyMethodAttributes.PublicSingleton)]
            public static object MaxFds(RubyModule/*!*/ self) {
                if (!_isUnix) {
                    throw RubyExceptions.CreateRuntimeError("operation not supported");
                }
                var rlim = new ulong[2];
                if (getrlimit(RlimitNoFile, rlim) < 0) {
                    throw Posix.Error(Marshal.GetLastWin32Error(), "getrlimit");
                }
                // INT2NUM of an rlim_t: RLIM_INFINITY comes out as -1
                return ScriptingRuntimeHelpers.Int32ToObject(unchecked((int)rlim[0]));
            }

            [RubyMethod("maxfds=", RubyMethodAttributes.PublicSingleton)]
            public static object SetMaxFds(ConversionStorage<int>/*!*/ fixnumCast, RubyModule/*!*/ self, object max) {
                if (!_isUnix) {
                    throw RubyExceptions.CreateRuntimeError("operation not supported");
                }
                int value = Protocols.CastToFixnum(fixnumCast, max);
                var rlim = new ulong[2];
                if (getrlimit(RlimitNoFile, rlim) < 0) {
                    throw Posix.Error(Marshal.GetLastWin32Error(), "getrlimit");
                }
                rlim[0] = unchecked((ulong)(long)value);
                if (setrlimit(RlimitNoFile, rlim) < 0) {
                    throw Posix.Error(Marshal.GetLastWin32Error(), "setrlimit");
                }
                return max;
            }
        }

        #endregion
    }
}

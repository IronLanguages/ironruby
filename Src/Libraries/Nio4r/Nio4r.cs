/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

// The C half of the nio4r gem - NIO::Selector, NIO::Monitor and NIO::ByteBuffer, which nio4r's
// own ext/nio4r implements over libev - for IronRuby.  The Ruby half (lib/nio.rb and friends) is
// vendored under Src/StdLib/ironruby/nio*.
//
// There is no public epoll or kqueue wrapper in the BCL, and libev cannot be loaded.  The
// selector asks the kernel itself: epoll(7) on Linux (Epoll.cs, the syscalls by P/Invoke), one
// poll(2) over every registered descriptor on other Unixes, one Socket.Select over every
// registered socket on Windows - libev's :epoll, :poll and :select backends.  poll and select
// are O(registered IOs) per #select, as is the gem's pure-Ruby backend on top of IO.select; the
// difference from that backend is everything around the syscall: the interest set lives here
// rather than being rebuilt as Ruby arrays on every call, and a ready monitor is found from the
// kernel's answer rather than by asking each IO again.  See Src/StdLib/ironruby/nio4r_ext.rb for
// the measurements behind the choice.
//
// IronRuby's IO.pipe is an in-process queue with no descriptor for the kernel to watch; a pipe
// registered here wakes the selector through its ReadinessWaiter, as it does IO.select
// (Src/Libraries/Builtins/IoReadiness.cs).  #wakeup uses the same waiter.

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.StandardLibrary.Nio4r {

    [RubyModule("NIO")]
    public static class NioModule {

        // libev's EV_READ and EV_WRITE, which is how the C extension keeps interests too.
        internal const int InterestRead = 1;
        internal const int InterestWrite = 2;

        internal static int ToInterest(RubyContext/*!*/ context, object interest, string/*!*/ what) {
            var symbol = interest as RubySymbol;
            if (symbol == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Symbol)",
                    context.GetClassDisplayName(interest));
            }
            switch (symbol.ToString()) {
                case "r": return InterestRead;
                case "w": return InterestWrite;
                case "rw": return InterestRead | InterestWrite;
                default:
                    throw RubyExceptions.CreateArgumentError("invalid {0} type {1} (must be :r, :w, or :rw)",
                        what, context.Inspect(interest).ToString());
            }
        }

        internal static object ToSymbol(RubyContext/*!*/ context, int interests) {
            switch (interests) {
                case InterestRead: return context.CreateAsciiSymbol("r");
                case InterestWrite: return context.CreateAsciiSymbol("w");
                case InterestRead | InterestWrite: return context.CreateAsciiSymbol("rw");
                default: return null;
            }
        }

        /// <summary>rb_convert_type(io, T_FILE, "IO", "to_io"), which is what the C extension does.</summary>
        internal static RubyIO/*!*/ ToIO(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, RubyContext/*!*/ context, object obj) {

            var io = obj as RubyIO;
            if (io != null) {
                return io;
            }
            if (Protocols.RespondTo(respondToStorage, obj, "to_io")) {
                var site = toIoStorage.GetCallSite("to_io", 0);
                object converted = site.Target(site, obj);
                io = converted as RubyIO;
                if (io != null) {
                    return io;
                }
                throw RubyExceptions.CreateTypeError("can't convert {0} to IO ({0}#to_io gives {1})",
                    context.GetClassDisplayName(obj), context.GetClassDisplayName(converted));
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into IO", context.GetClassDisplayName(obj));
        }

        #region Selector

        [RubyClass("Selector")]
        public sealed class Selector : RubyObject {
            // IO (as the caller passed it) => its monitor.  Identity, as a Ruby Hash of IOs is.
            internal readonly Dictionary<object, Monitor>/*!*/ _selectables =
                new Dictionary<object, Monitor>(ReferenceEqualityComparer.Instance);

            // Reentrant, like the C extension's lock_holder dance: a #select block may register
            // and deregister.  Held for the whole of #select, as the extension holds its Mutex,
            // which is why #wakeup exists and does not take it.
            private readonly object/*!*/ _lock = new object();

            private ReadinessWaiter _waiter;
            private int _wakeupRequested;
            private bool _closed;

            // The :epoll backend's instance, or null for :poll / :select.
            private Epoll _epoll;
            private string/*!*/ _backend = DefaultBackend;
            private long _nextToken = 1;   // 0 is the waiter
            private readonly Dictionary<long, Monitor>/*!*/ _tokens = new Dictionary<long, Monitor>();

            public Selector(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Selector(ImmediateClass.NominalClass);
            }

            /// <summary>
            /// What this platform offers, best first - libev's names for the same mechanisms:
            /// epoll(7) on Linux, poll(2) on any Unix, and Winsock's select on Windows.
            /// </summary>
            private static string[]/*!*/ SupportedBackends {
                get {
                    if (!RubyIO.HasPoll) {
                        return new[] { "select" };
                    }
                    return Epoll.IsAvailable ? new[] { "epoll", "poll" } : new[] { "poll" };
                }
            }

            private static string/*!*/ DefaultBackend {
                get { return SupportedBackends[0]; }
            }

            [RubyMethod("backends", RubyMethodAttributes.PublicSingleton)]
            public static RubyArray/*!*/ Backends(RubyClass/*!*/ self) {
                var result = new RubyArray();
                foreach (var name in SupportedBackends) {
                    result.Add(self.Context.CreateAsciiSymbol(name));
                }
                return result;
            }

            // Class#new on a library class binds to a constructor, not to #initialize - see
            // RubyClass.BuildObjectConstructionNoFlow - so each class here has a factory that runs
            // its #initialize.  A Ruby subclass still gets allocate-and-initialize.
            [RubyConstructor]
            public static Selector/*!*/ Create(RubyClass/*!*/ self, [Optional]object backend) {
                var result = new Selector(self);
                Initialize(self.Context, result, backend);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(RubyContext/*!*/ context, Selector/*!*/ self, [Optional]object backend) {
                string name = DefaultBackend;
                if (backend != null && !(backend is Missing)) {
                    var symbol = backend as RubySymbol;
                    if (symbol == null || Array.IndexOf(SupportedBackends, symbol.ToString()) < 0) {
                        throw RubyExceptions.CreateArgumentError("unsupported backend: {0}", context.Inspect(backend).ToString());
                    }
                    name = symbol.ToString();
                }
                self._waiter = ReadinessWaiter.Create();
                if (name == "epoll") {
                    self._epoll = Epoll.Create();
                    if (self._epoll == null) {
                        name = RubyIO.HasPoll ? "poll" : "select";
                    } else if (self._waiter != null) {
                        self._epoll.Set((int)self._waiter.Socket.Handle, Epoll.EPOLLIN, 0, false);
                    }
                }
                self._backend = name;
                return null;
            }

            [RubyMethod("backend")]
            public static RubySymbol/*!*/ Backend(RubyContext/*!*/ context, Selector/*!*/ self) {
                self.RequireOpen();
                return context.CreateAsciiSymbol(self._backend);
            }

            private void RequireOpen() {
                if (_closed) {
                    throw RubyExceptions.CreateIOError("selector is closed");
                }
            }

            /// <summary>
            /// Takes the lock without parking the thread where Thread#kill cannot reach it: a
            /// thread waiting to register while another selects waits in slices.
            /// </summary>
            internal void Lock() {
                if (System.Threading.Monitor.TryEnter(_lock)) {
                    return;
                }
                var info = ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
                bool wasBlocked = info.Blocked;
                try {
                    info.Blocked = true;
                    while (true) {
                        RubyUtils.CheckAsyncException();
                        try {
                            if (System.Threading.Monitor.TryEnter(_lock, IoReadiness.SliceMilliseconds)) {
                                return;
                            }
                        } catch (ThreadInterruptedException) {
                            RubyUtils.TranslateThreadInterrupt();
                        }
                    }
                } finally {
                    info.Blocked = wasBlocked;
                }
            }

            internal void Unlock() {
                System.Threading.Monitor.Exit(_lock);
            }

            [RubyMethod("register")]
            public static Monitor/*!*/ Register(RespondToStorage/*!*/ respondToStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
                RubyContext/*!*/ context, Selector/*!*/ self, object io, object interests) {

                self.Lock();
                try {
                    self.RequireOpen();
                    if (io != null && self._selectables.ContainsKey(io)) {
                        throw RubyExceptions.CreateArgumentError("this IO is already registered with selector");
                    }

                    var monitor = new Monitor(context.GetClass(typeof(Monitor)));
                    monitor.Setup(respondToStorage, toIoStorage, context, io, interests, self);
                    self._selectables[io] = monitor;
                    return monitor;
                } finally {
                    self.Unlock();
                }
            }

            [RubyMethod("deregister")]
            public static object Deregister(Selector/*!*/ self, object io) {
                self.Lock();
                try {
                    return self.DeregisterLocked(io);
                } finally {
                    self.Unlock();
                }
            }

            internal Monitor DeregisterLocked(object io) {
                Monitor monitor;
                if (io == null || !_selectables.TryGetValue(io, out monitor)) {
                    return null;
                }
                _selectables.Remove(io);
                ForgetEpoll(monitor);
                monitor.CloseInternal(false);
                return monitor;
            }

            [RubyMethod("registered?")]
            public static bool IsRegistered(Selector/*!*/ self, object io) {
                return io != null && self._selectables.ContainsKey(io);
            }

            [RubyMethod("empty?")]
            public static bool IsEmpty(Selector/*!*/ self) {
                return self._selectables.Count == 0;
            }

            [RubyMethod("wakeup")]
            public static object Wakeup(Selector/*!*/ self) {
                self.RequireOpen();
                Interlocked.Exchange(ref self._wakeupRequested, 1);
                if (self._waiter != null) {
                    self._waiter.Signal();
                }
                return null;
            }

            [RubyMethod("close")]
            public static object Close(Selector/*!*/ self) {
                self.Lock();
                try {
                    if (!self._closed) {
                        self._closed = true;
                        if (self._epoll != null) {
                            self._epoll.Dispose();
                            self._epoll = null;
                            self._tokens.Clear();
                        }
                        if (self._waiter != null) {
                            self._waiter.Dispose();
                        }
                    }
                    return null;
                } finally {
                    self.Unlock();
                }
            }

            [RubyMethod("closed?")]
            public static bool IsClosed(Selector/*!*/ self) {
                return self._closed;
            }

            [RubyMethod("select")]
            public static object Select(ConversionStorage<double>/*!*/ floatConversion, BlockParam block,
                Selector/*!*/ self, [Optional]object timeout) {

                int milliseconds = Timeout.Infinite;
                if (timeout != null && !(timeout is Missing)) {
                    double seconds = Protocols.CastToFloat(floatConversion, timeout);
                    if (seconds < 0) {
                        throw RubyExceptions.CreateArgumentError("time interval must be positive");
                    }
                    double ms = Math.Ceiling(seconds * 1000);
                    milliseconds = (Double.IsNaN(ms) || ms >= Int32.MaxValue) ? Timeout.Infinite : (int)ms;
                }

                self.Lock();
                try {
                    self.RequireOpen();
                    return self.SelectLocked(block, milliseconds);
                } finally {
                    self.Unlock();
                }
            }

            // One registered IO as this #select sees it.
            private struct Probe {
                public Monitor Monitor;
                public RubyIO IO;
                public int Interests;
                public Socket Socket;       // Windows; and Unix, for the descriptor
                public RubyPipe Pipe;
                public int Descriptor;      // Unix: what poll(2) is handed, or -1
                public int Ready;
                // A TLS socket's transport (IoReadiness.IReadinessHook): the interests it answers
                // for itself, whatever the kernel says about the socket, and its answer.
                public IReadinessHook Hook;
                public int HookOwned;
                public int HookReady;
            }

            private object SelectLocked(BlockParam block, int milliseconds) {
                long deadline = (milliseconds == Timeout.Infinite) ? Int64.MaxValue : Environment.TickCount64 + milliseconds;

                var info = ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
                bool wasBlocked = info.Blocked;
                List<RubyPipe> pipes = null;
                List<IReadinessHook> hooks = null;
                bool hooked = false;
                Probe[] probes;
                try {
                    info.Blocked = true;

                    // The registered set cannot change under us - we hold the lock - until a block
                    // runs, and by then the snapshot has been used.
                    probes = new Probe[_selectables.Count];
                    int count = 0;
                    foreach (var monitor in _selectables.Values) {
                        monitor._readiness = 0;
                        if (monitor._interests == 0) {
                            continue;
                        }
                        var probe = new Probe { Monitor = monitor, IO = monitor._rubyIO, Interests = monitor._interests, Descriptor = -1 };
                        if (!probe.IO.Closed) {
                            monitor.Resolve();
                            probe.Socket = monitor._socket;
                            probe.Pipe = monitor._pipe;
                            probe.Descriptor = monitor._descriptor;
                            if (probe.Pipe != null && _waiter != null) {
                                probe.Pipe.AddWaiter(_waiter);
                                (pipes ?? (pipes = new List<RubyPipe>())).Add(probe.Pipe);
                            }
                            probe.Hook = IoReadiness.GetHook(probe.IO);
                            if (probe.Hook != null) {
                                hooked = true;
                                if (_waiter != null) {
                                    probe.Hook.AddWaiter(_waiter);
                                    (hooks ?? (hooks = new List<IReadinessHook>())).Add(probe.Hook);
                                }
                            }
                        }
                        monitor._probeIndex = count;
                        probes[count++] = probe;
                    }

                    int readyCount;
                    bool woken;
                    while (true) {
                        if (_waiter != null) {
                            _waiter.Reset();
                        }
                        woken = Interlocked.Exchange(ref _wakeupRequested, 0) != 0;

                        readyCount = 0;
                        for (int i = 0; i < count; i++) {
                            probes[i].Ready = ProbeWithoutKernel(ref probes[i]);
                            if (probes[i].Ready != 0) {
                                readyCount++;
                            }
                        }

                        long remaining = deadline - Environment.TickCount64;
                        int wait = (readyCount > 0 || woken || remaining <= 0) ? 0
                            : (int)Math.Min(remaining, ((pipes != null || hooked) && _waiter == null) ? 1 : IoReadiness.SliceMilliseconds);

                        readyCount += AskKernel(probes, count, wait);

                        // What a hook answers for, it answers alone: the kernel may call its socket
                        // readable while the bytes are still on their way through the hook's read.
                        if (hooked) {
                            readyCount = 0;
                            for (int i = 0; i < count; i++) {
                                if (probes[i].Hook != null) {
                                    probes[i].Ready = (probes[i].Ready & ~probes[i].HookOwned) | probes[i].HookReady;
                                }
                                if (probes[i].Ready != 0) {
                                    readyCount++;
                                }
                            }
                        }

                        if (readyCount > 0 || woken || Volatile.Read(ref _wakeupRequested) != 0) {
                            Interlocked.Exchange(ref _wakeupRequested, 0);
                            break;
                        }
                        if (deadline - Environment.TickCount64 <= 0) {
                            return null;
                        }
                        RubyUtils.CheckAsyncException();
                    }

                    // Report.  With a block the monitors are yielded one by one, as libev's callback
                    // does, and the answer is how many there were; without, an Array of them.
                    RubyArray result = (block == null) ? new RubyArray(readyCount) : null;
                    int reported = 0;
                    for (int i = 0; i < count; i++) {
                        if (probes[i].Ready == 0) {
                            continue;
                        }
                        probes[i].Monitor._readiness = probes[i].Ready;
                        reported++;
                        if (block != null) {
                            object blockResult;
                            if (block.Yield(probes[i].Monitor, out blockResult)) {
                                return blockResult;
                            }
                        } else {
                            result.Add(probes[i].Monitor);
                        }
                    }
                    return (block != null) ? (object)ScriptingRuntimeHelpers.Int32ToObject(reported) : result;
                } finally {
                    if (pipes != null) {
                        foreach (var pipe in pipes) {
                            pipe.RemoveWaiter(_waiter);
                        }
                    }
                    if (hooks != null) {
                        foreach (var hook in hooks) {
                            hook.RemoveWaiter(_waiter);
                        }
                    }
                    info.Blocked = wasBlocked;
                }
            }

            /// <summary>
            /// What can be known without a syscall.  A closed IO is ready for everything it was
            /// registered for - libev reports a descriptor that has gone away the same way, so a
            /// reactor gets to find out on its next read - and bytes already in IronRuby's own
            /// buffer make an IO readable whatever the descriptor says.
            /// </summary>
            private static int ProbeWithoutKernel(ref Probe probe) {
                var io = probe.IO;
                if (io.Closed) {
                    return probe.Interests;
                }

                int ready = 0;
                var stream = io.GetStream();
                if ((probe.Interests & InterestRead) != 0 && stream.DataBuffered) {
                    ready |= InterestRead;
                }

                if (probe.Hook != null) {
                    probe.HookOwned = probe.HookReady = 0;
                    foreach (int interest in new[] { InterestRead, InterestWrite }) {
                        if ((probe.Interests & interest) == 0) {
                            continue;
                        }
                        bool? answer = probe.Hook.IsReady(interest == InterestRead ? IoReadiness.Read : IoReadiness.Write);
                        if (answer.HasValue) {
                            probe.HookOwned |= interest;
                            if (answer.Value) {
                                probe.HookReady |= interest;
                            }
                        }
                    }
                    ready |= probe.HookReady;
                }

                if (probe.Pipe != null) {
                    if ((probe.Interests & InterestRead) != 0 && io.Mode.CanRead() && probe.Pipe.CanReadWithoutBlocking) {
                        ready |= InterestRead;
                    }
                    if ((probe.Interests & InterestWrite) != 0 && io.Mode.CanWrite() && probe.Pipe.CanWriteWithoutBlocking) {
                        ready |= InterestWrite;
                    }
                } else if (probe.Monitor._windowsPipe != null) {
                    // Windows: a pipe is asked with PeekNamedPipe, as IO.select asks it.
                    if ((probe.Interests & InterestRead) != 0 && io.Mode.CanRead() && IoReadiness.IsWindowsPipeReadable(probe.Monitor._windowsPipe)) {
                        ready |= InterestRead;
                    }
                    if ((probe.Interests & InterestWrite) != 0 && io.Mode.CanWrite()) {
                        ready |= InterestWrite;
                    }
                } else if ((probe.Socket == null && probe.Descriptor < 0) || probe.Monitor._epollUnsupported) {
                    // Nothing to ask - a stream with no descriptor.  IO.select calls such a thing
                    // ready for what its mode allows, and so does this.
                    if ((probe.Interests & InterestRead) != 0 && io.Mode.CanRead()) {
                        ready |= InterestRead;
                    }
                    if ((probe.Interests & InterestWrite) != 0 && io.Mode.CanWrite()) {
                        ready |= InterestWrite;
                    }
                }
                return ready;
            }

            /// <summary>
            /// One kernel call over every probe with a descriptor or a socket, plus the waiter,
            /// waiting at most <paramref name="milliseconds"/>.  Adds what it finds to each probe's
            /// Ready and answers how many probes became ready that were not already.
            /// </summary>
            private int AskKernel(Probe[]/*!*/ probes, int count, int milliseconds) {
                try {
                    if (_epoll != null) {
                        return AskEpoll(probes, count, milliseconds);
                    }
                    return RubyIO.HasPoll ? AskPoll(probes, count, milliseconds) : AskSelect(probes, count, milliseconds);
                } catch (ObjectDisposedException) {
                    // An IO closed by another thread mid-wait; the next round reports it closed.
                    return 0;
                } catch (SocketException) {
                    return 0;
                } catch (ThreadInterruptedException) {
                    RubyUtils.TranslateThreadInterrupt();
                    return 0;
                }
            }

            #region epoll

            /// <summary>
            /// Brings the kernel's interest list in line with the monitors - what changed since the
            /// last #select: a new registration, new interests, none at all - then waits in
            /// epoll_wait.  A monitor whose IO has been closed is forgotten rather than removed:
            /// closing the descriptor took it out of the epoll set already, and the number may by
            /// now belong to another IO, whose registration an EPOLL_CTL_DEL would remove.
            /// </summary>
            private int AskEpoll(Probe[]/*!*/ probes, int count, int milliseconds) {
                foreach (var monitor in _selectables.Values) {
                    if (monitor._rubyIO.Closed) {
                        if (monitor._epollFd >= 0) {
                            _tokens.Remove(monitor._epollToken);
                            monitor._epollFd = -1;
                        }
                        continue;
                    }

                    monitor.Resolve();
                    int fd = monitor._descriptor;
                    uint want = 0;
                    if (fd >= 0 && monitor._pipe == null) {
                        if ((monitor._interests & InterestRead) != 0) {
                            want |= Epoll.EPOLLIN | Epoll.EPOLLRDHUP;
                        }
                        if ((monitor._interests & InterestWrite) != 0) {
                            want |= Epoll.EPOLLOUT;
                        }
                    }

                    if (monitor._epollFd >= 0 && (monitor._epollFd != fd || want == 0)) {
                        // Reopened onto another descriptor, or nothing to watch any more.
                        _epoll.Remove(monitor._epollFd);
                        _tokens.Remove(monitor._epollToken);
                        monitor._epollFd = -1;
                    }
                    if (want == 0 || monitor._epollUnsupported || (monitor._epollFd == fd && monitor._epollMask == want)) {
                        continue;
                    }

                    bool registered = monitor._epollFd == fd;
                    if (!registered) {
                        monitor._epollToken = _nextToken++;
                    }
                    int errno = _epoll.Set(fd, want, monitor._epollToken, registered);
                    if (errno == 0) {
                        monitor._epollFd = fd;
                        monitor._epollMask = want;
                        _tokens[monitor._epollToken] = monitor;
                    } else {
                        // EPERM: a regular file, which epoll cannot watch and poll(2) calls always
                        // ready. Treated the same way, by ProbeWithoutKernel.
                        monitor._epollUnsupported = true;
                    }
                }

                int newlyReady = 0;
                _epoll.Wait(Math.Max(count, 1) + 1, milliseconds, (token, events) => {
                    Monitor monitor;
                    if (token == 0 || !_tokens.TryGetValue(token, out monitor)) {
                        return;
                    }
                    int i = monitor._probeIndex;
                    if (i < 0 || i >= count || !ReferenceEquals(probes[i].Monitor, monitor)) {
                        return;
                    }
                    int ready = 0;
                    if ((events & (Epoll.EPOLLIN | Epoll.EPOLLRDHUP | Epoll.EPOLLHUP | Epoll.EPOLLERR)) != 0) {
                        ready |= InterestRead;
                    }
                    if ((events & (Epoll.EPOLLOUT | Epoll.EPOLLERR)) != 0) {
                        ready |= InterestWrite;
                    }
                    ready &= probes[i].Interests;
                    if (ready != 0) {
                        if (probes[i].Ready == 0) {
                            newlyReady++;
                        }
                        probes[i].Ready |= ready;
                    }
                });
                return newlyReady;
            }

            private void ForgetEpoll(Monitor/*!*/ monitor) {
                if (monitor._epollFd < 0) {
                    return;
                }
                if (_epoll != null && !monitor._rubyIO.Closed) {
                    _epoll.Remove(monitor._epollFd);
                }
                _tokens.Remove(monitor._epollToken);
                monitor._epollFd = -1;
            }

            #endregion

            private const short POLLIN = 0x001, POLLOUT = 0x004, POLLERR = 0x008, POLLHUP = 0x010, POLLNVAL = 0x020;

            private int AskPoll(Probe[]/*!*/ probes, int count, int milliseconds) {
                var fds = new int[count + 1];
                var events = new short[count + 1];
                var owners = new int[count + 1];
                int n = 0;
                for (int i = 0; i < count; i++) {
                    int fd = probes[i].Descriptor;
                    if (fd < 0 || probes[i].IO.Closed) {
                        continue;
                    }
                    fds[n] = fd;
                    events[n] = (short)((((probes[i].Interests & InterestRead) != 0) ? POLLIN : 0) |
                                        (((probes[i].Interests & InterestWrite) != 0) ? POLLOUT : 0));
                    owners[n] = i;
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
                if (RubyIO.Poll(fds, events, revents, n, milliseconds) <= 0) {
                    return 0;
                }

                int newlyReady = 0;
                for (int j = 0; j < n; j++) {
                    if (j == waiterIndex || revents[j] == 0) {
                        continue;
                    }
                    int i = owners[j];
                    int ready = 0;
                    // A hung-up or failed descriptor is ready in the sense that the next read or
                    // write returns at once, with EOF or the error - libev reports it the same way.
                    const short broken = POLLERR | POLLHUP | POLLNVAL;
                    if ((revents[j] & (POLLIN | broken)) != 0) {
                        ready |= InterestRead;
                    }
                    if ((revents[j] & (POLLOUT | POLLERR | POLLNVAL)) != 0) {
                        ready |= InterestWrite;
                    }
                    ready &= probes[i].Interests;
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
                var owners = new Dictionary<Socket, int>();
                for (int i = 0; i < count; i++) {
                    var socket = probes[i].Socket;
                    if (probes[i].Monitor._windowsPipe != null) {
                        // Socket.Select cannot watch a pipe; look at it again in a millisecond.
                        milliseconds = Math.Min(milliseconds, 1);
                    }
                    if (socket == null || probes[i].IO.Closed) {
                        continue;
                    }
                    owners[socket] = i;
                    if ((probes[i].Interests & InterestRead) != 0) {
                        reads.Add(socket);
                    }
                    if ((probes[i].Interests & InterestWrite) != 0) {
                        writes.Add(socket);
                        // A connect that failed is reported in the error set, not the write set.
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
                newlyReady += Mark(probes, owners, reads, InterestRead);
                newlyReady += Mark(probes, owners, writes, InterestWrite);
                newlyReady += Mark(probes, owners, errors, InterestWrite);
                return newlyReady;
            }

            private static int Mark(Probe[]/*!*/ probes, Dictionary<Socket, int>/*!*/ owners, List<Socket>/*!*/ sockets, int interest) {
                int newlyReady = 0;
                foreach (var socket in sockets) {
                    int i;
                    if (owners.TryGetValue(socket, out i)) {
                        if (probes[i].Ready == 0) {
                            newlyReady++;
                        }
                        probes[i].Ready |= interest;
                    }
                }
                return newlyReady;
            }
        }

        #endregion

        #region Monitor

        [RubyClass("Monitor")]
        public sealed class Monitor : RubyObject {
            internal object _io;
            internal RubyIO _rubyIO;

            // What the selector hands the kernel for this IO, worked out once rather than on every
            // #select - asking a Socket for its Handle is not free (it marks the handle exposed),
            // and at a thousand registered sockets the asking cost more than the poll(2).  Redone
            // if the IO turns out to have been given a different stream (IO#reopen).
            internal Socket _socket;
            internal RubyPipe _pipe;
            internal System.IO.FileStream _windowsPipe;
            internal int _descriptor = -1;

            // The :epoll backend's view of this monitor: the descriptor and events the kernel was
            // last told about (-1: none), the token it reports them under, and whether epoll
            // refused the descriptor altogether.  _probeIndex is this monitor's slot in the
            // #select in progress.
            internal int _epollFd = -1;
            internal uint _epollMask;
            internal long _epollToken;
            internal bool _epollUnsupported;
            internal int _probeIndex = -1;
            private System.IO.Stream _resolvedFor;

            internal void Resolve() {
                var stream = _rubyIO.GetStream().BaseStream;
                if (ReferenceEquals(stream, _resolvedFor)) {
                    return;
                }
                _resolvedFor = stream;
                _socket = IoReadiness.GetSocket(_rubyIO);
                _pipe = (_socket == null) ? IoReadiness.GetPipe(_rubyIO) : null;
                _windowsPipe = (_socket == null && _pipe == null) ? IoReadiness.GetWindowsPipe(_rubyIO) : null;
                _descriptor = !RubyIO.HasPoll ? -1
                    : (_socket != null) ? (int)_socket.Handle
                    : (_pipe != null) ? -1
                    : _rubyIO.KernelDescriptor;
            }
            internal int _interests;
            internal int _readiness;
            internal Selector _selector;
            internal object _value;

            public Monitor(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Monitor(ImmediateClass.NominalClass);
            }

            private RubyContext/*!*/ Context {
                get { return ImmediateClass.Context; }
            }

            internal void Setup(RespondToStorage/*!*/ respondToStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
                RubyContext/*!*/ context, object io, object interests, Selector/*!*/ selector) {

                int value = ToInterest(context, interests, "event");
                _rubyIO = ToIO(respondToStorage, toIoStorage, context, io);
                _io = io;
                _interests = value;
                _selector = selector;
            }

            [RubyConstructor]
            public static Monitor/*!*/ Create(RespondToStorage/*!*/ respondToStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
                RubyClass/*!*/ self, object io, object interests, [NotNull]Selector/*!*/ selector) {
                var result = new Monitor(self);
                result.Setup(respondToStorage, toIoStorage, self.Context, io, interests, selector);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(RespondToStorage/*!*/ respondToStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
                RubyContext/*!*/ context, Monitor/*!*/ self, object io, object interests, [NotNull]Selector/*!*/ selector) {
                self.Setup(respondToStorage, toIoStorage, context, io, interests, selector);
                return null;
            }

            [RubyMethod("close")]
            public static object Close(Monitor/*!*/ self, [Optional]object deregister) {
                bool alsoDeregister = deregister is Missing || deregister == null || Protocols.IsTrue(deregister);
                self.Close(alsoDeregister);
                return null;
            }

            private void Close(bool deregister) {
                var selector = _selector;
                if (selector == null) {
                    return;
                }
                _selector = null;
                if (deregister) {
                    selector.Lock();
                    try {
                        selector.DeregisterLocked(_io);
                    } finally {
                        selector.Unlock();
                    }
                }
            }

            internal void CloseInternal(bool deregister) {
                Close(deregister);
            }

            [RubyMethod("closed?")]
            public static bool IsClosed(Monitor/*!*/ self) {
                return self._selector == null;
            }

            [RubyMethod("io")]
            public static object GetIO(Monitor/*!*/ self) {
                return self._io;
            }

            [RubyMethod("selector")]
            public static object GetSelector(Monitor/*!*/ self) {
                return self._selector;
            }

            [RubyMethod("interests")]
            public static object GetInterests(Monitor/*!*/ self) {
                return ToSymbol(self.Context, self._interests);
            }

            [RubyMethod("interests=")]
            public static object SetInterests(Monitor/*!*/ self, object interests) {
                self.UpdateInterests(interests == null ? 0 : ToInterest(self.Context, interests, "interest"));
                return GetInterests(self);
            }

            [RubyMethod("add_interest")]
            public static object AddInterest(Monitor/*!*/ self, object interest) {
                self.UpdateInterests(self._interests | ToInterest(self.Context, interest, "interest"));
                return GetInterests(self);
            }

            [RubyMethod("remove_interest")]
            public static object RemoveInterest(Monitor/*!*/ self, object interest) {
                self.UpdateInterests(self._interests & ~ToInterest(self.Context, interest, "interest"));
                return GetInterests(self);
            }

            private void UpdateInterests(int interests) {
                if (_selector == null) {
                    throw new EOFError("monitor is closed");
                }
                // A plain field write: a #select in progress took its snapshot under the selector's
                // lock and sees this on its next call, which is when libev would see it too.
                _interests = interests;
            }

            [RubyMethod("value")]
            public static object GetValue(Monitor/*!*/ self) {
                return self._value;
            }

            [RubyMethod("value=")]
            public static object SetValue(Monitor/*!*/ self, object value) {
                return self._value = value;
            }

            [RubyMethod("readiness")]
            public static object GetReadiness(Monitor/*!*/ self) {
                return ToSymbol(self.Context, self._readiness);
            }

            [RubyMethod("readable?")]
            public static bool IsReadable(Monitor/*!*/ self) {
                return (self._readiness & InterestRead) != 0;
            }

            [RubyMethod("writable?")]
            [RubyMethod("writeable?")]
            public static bool IsWritable(Monitor/*!*/ self) {
                return (self._readiness & InterestWrite) != 0;
            }
        }

        #endregion

        #region ByteBuffer

        /// <summary>
        /// A fixed-size byte buffer with a position, a limit and a mark - Java NIO's ByteBuffer,
        /// as the C extension has it.  One deliberate difference: #[] and #each answer bytes as
        /// 0..255.  The extension indexes a plain `char *`, so on x86 (where char is signed) a
        /// byte above 127 comes back negative, and on ARM it does not; the gem's pure-Ruby and
        /// Java implementations answer 0..255 everywhere, and so does this.
        /// </summary>
        [RubyClass("ByteBuffer")]
        public sealed class ByteBuffer : RubyObject {
            private byte[]/*!*/ _buffer = Utils.EmptyBytes;
            private int _position, _limit, _capacity, _mark = -1;

            public ByteBuffer(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new ByteBuffer(ImmediateClass.NominalClass);
            }

            [RubyException("OverflowError")]
            public class OverflowError : System.IO.IOException {
                public OverflowError() : this(null, null) { }
                public OverflowError(string message) : this(message, null) { }
                public OverflowError(string message, Exception inner) : base(message ?? "OverflowError", inner) { }
            }

            [RubyException("UnderflowError")]
            public class UnderflowError : System.IO.IOException {
                public UnderflowError() : this(null, null) { }
                public UnderflowError(string message) : this(message, null) { }
                public UnderflowError(string message, Exception inner) : base(message ?? "UnderflowError", inner) { }
            }

            [RubyException("MarkUnsetError")]
            public class MarkUnsetError : System.IO.IOException {
                public MarkUnsetError() : this(null, null) { }
                public MarkUnsetError(string message) : this(message, null) { }
                public MarkUnsetError(string message, Exception inner) : base(message ?? "MarkUnsetError", inner) { }
            }

            [RubyConstructor]
            public static ByteBuffer/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol]int capacity) {
                var result = new ByteBuffer(self);
                Initialize(result, capacity);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(ByteBuffer/*!*/ self, [DefaultProtocol]int capacity) {
                if (capacity < 0) {
                    throw RubyExceptions.CreateArgumentError("negative buffer size");
                }
                self._capacity = capacity;
                self._buffer = new byte[capacity];
                Clear(self);
                return self;
            }

            [RubyMethod("clear")]
            public static ByteBuffer/*!*/ Clear(ByteBuffer/*!*/ self) {
                Array.Clear(self._buffer, 0, self._buffer.Length);
                self._position = 0;
                self._limit = self._capacity;
                self._mark = -1;
                return self;
            }

            [RubyMethod("position")]
            public static int GetPosition(ByteBuffer/*!*/ self) {
                return self._position;
            }

            [RubyMethod("position=")]
            public static object SetPosition(ByteBuffer/*!*/ self, [DefaultProtocol]int position) {
                if (position < 0) {
                    throw RubyExceptions.CreateArgumentError("negative position given");
                }
                if (position > self._limit) {
                    throw RubyExceptions.CreateArgumentError("specified position exceeds limit");
                }
                self._position = position;
                if (self._mark > self._position) {
                    self._mark = -1;
                }
                return position;
            }

            [RubyMethod("limit")]
            public static int GetLimit(ByteBuffer/*!*/ self) {
                return self._limit;
            }

            [RubyMethod("limit=")]
            public static object SetLimit(ByteBuffer/*!*/ self, [DefaultProtocol]int limit) {
                if (limit < 0) {
                    throw RubyExceptions.CreateArgumentError("negative limit given");
                }
                if (limit > self._capacity) {
                    throw RubyExceptions.CreateArgumentError("specified limit exceeds capacity");
                }
                self._limit = limit;
                if (self._position > limit) {
                    self._position = limit;
                }
                if (self._mark > limit) {
                    self._mark = -1;
                }
                return limit;
            }

            [RubyMethod("capacity")]
            [RubyMethod("size")]
            public static int GetCapacity(ByteBuffer/*!*/ self) {
                return self._capacity;
            }

            [RubyMethod("remaining")]
            public static int Remaining(ByteBuffer/*!*/ self) {
                return self._limit - self._position;
            }

            [RubyMethod("full?")]
            public static bool IsFull(ByteBuffer/*!*/ self) {
                return self._position == self._limit;
            }

            [RubyMethod("get")]
            public static MutableString/*!*/ Get(ConversionStorage<int>/*!*/ fixnumCast, ByteBuffer/*!*/ self, [Optional]object length) {
                int count;
                if (length == null || length is Missing) {
                    count = self._limit - self._position;
                } else {
                    count = Protocols.CastToFixnum(fixnumCast, length);
                }
                if (count < 0) {
                    throw RubyExceptions.CreateArgumentError("negative length given");
                }
                if (count > self._limit - self._position) {
                    throw new UnderflowError("not enough data in buffer");
                }
                var result = MutableString.CreateBinary(count);
                result.Append(self._buffer, self._position, count);
                self._position += count;
                return result;
            }

            [RubyMethod("[]")]
            public static int Fetch(ByteBuffer/*!*/ self, [DefaultProtocol]int index) {
                if (index < 0) {
                    throw RubyExceptions.CreateArgumentError("negative index given");
                }
                if (index >= self._limit) {
                    throw RubyExceptions.CreateArgumentError("specified index exceeds limit");
                }
                return self._buffer[index];
            }

            [RubyMethod("<<")]
            public static ByteBuffer/*!*/ Put(ByteBuffer/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ data) {
                int length = data.GetByteCount();
                if (length > self._limit - self._position) {
                    throw new OverflowError("buffer is full");
                }
                data.ToByteArray().AsSpan(0, length).CopyTo(self._buffer.AsSpan(self._position));
                self._position += length;
                return self;
            }

            /// <summary>
            /// One non-blocking read into the space between position and limit: the bytes read,
            /// 0 when the read would block - and 0 at end of file too, which is what read(2)
            /// answers there and so what the C extension answers.
            /// </summary>
            [RubyMethod("read_from")]
            public static int ReadFrom(RespondToStorage/*!*/ respondToStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
                RubyContext/*!*/ context, ByteBuffer/*!*/ self, object io) {

                var rubyIO = ToIO(respondToStorage, toIoStorage, context, io);
                int room = self._limit - self._position;
                if (room == 0) {
                    throw new OverflowError("buffer is full");
                }

                MutableString data;
                try {
                    data = RubyIOOps.ReadNoBlock(rubyIO, room, null);
                } catch (EOFError) {
                    return 0;
                } catch (Errno.ResourceTemporarilyUnavailableError) {
                    return 0;
                }

                int read = data.GetByteCount();
                data.ToByteArray().AsSpan(0, read).CopyTo(self._buffer.AsSpan(self._position));
                self._position += read;
                return read;
            }

            [RubyMethod("write_to")]
            public static int WriteTo(RespondToStorage/*!*/ respondToStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
                RubyContext/*!*/ context, ByteBuffer/*!*/ self, object io) {

                var rubyIO = ToIO(respondToStorage, toIoStorage, context, io);
                int pending = self._limit - self._position;
                if (pending == 0) {
                    throw new UnderflowError("no data remaining in buffer");
                }

                var data = MutableString.CreateBinary(pending);
                data.Append(self._buffer, self._position, pending);
                int written;
                try {
                    written = RubyIOOps.WriteNoBlock(rubyIO, data);
                } catch (Errno.ResourceTemporarilyUnavailableError) {
                    return 0;
                }
                self._position += written;
                return written;
            }

            [RubyMethod("flip")]
            public static ByteBuffer/*!*/ Flip(ByteBuffer/*!*/ self) {
                self._limit = self._position;
                self._position = 0;
                self._mark = -1;
                return self;
            }

            [RubyMethod("rewind")]
            public static ByteBuffer/*!*/ Rewind(ByteBuffer/*!*/ self) {
                self._position = 0;
                self._mark = -1;
                return self;
            }

            [RubyMethod("mark")]
            public static ByteBuffer/*!*/ Mark(ByteBuffer/*!*/ self) {
                self._mark = self._position;
                return self;
            }

            [RubyMethod("reset")]
            public static ByteBuffer/*!*/ Reset(ByteBuffer/*!*/ self) {
                if (self._mark < 0) {
                    throw new MarkUnsetError("mark has not been set");
                }
                self._position = self._mark;
                return self;
            }

            [RubyMethod("compact")]
            public static ByteBuffer/*!*/ Compact(ByteBuffer/*!*/ self) {
                int remaining = self._limit - self._position;
                Buffer.BlockCopy(self._buffer, self._position, self._buffer, 0, remaining);
                self._position = remaining;
                self._limit = self._capacity;
                return self;
            }

            [RubyMethod("each")]
            public static object Each(BlockParam block, ByteBuffer/*!*/ self) {
                if (block == null) {
                    throw RubyExceptions.CreateArgumentError("no block given");
                }
                for (int i = 0; i < self._limit; i++) {
                    object blockResult;
                    if (block.Yield(ScriptingRuntimeHelpers.Int32ToObject(self._buffer[i]), out blockResult)) {
                        return blockResult;
                    }
                }
                return self;
            }

            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, ByteBuffer/*!*/ self) {
                var result = RubyUtils.ObjectToMutableStringPrefix(context, self);
                result.AppendFormat(" @position={0} @limit={1} @capacity={2}>", self._position, self._limit, self._capacity);
                return result;
            }
        }

        #endregion
    }
}

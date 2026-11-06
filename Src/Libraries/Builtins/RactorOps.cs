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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    using CloneStorage = CallSiteStorage<Func<CallSite, object, object, object, object>>;
    using AllocateStorage = CallSiteStorage<Func<CallSite, RubyClass, object>>;
    using UnaryStorage = CallSiteStorage<Func<CallSite, object, object>>;

    #region Exceptions

    public class RactorError : RuntimeError {
        public RactorError() : this(null, null) { }
        public RactorError(string message) : this(message, null) { }
        public RactorError(string message, Exception inner) : base(message ?? "Ractor::Error", inner) { }
    }

    public class RactorIsolationError : RactorError {
        public RactorIsolationError() : this(null, null) { }
        public RactorIsolationError(string message) : this(message, null) { }
        public RactorIsolationError(string message, Exception inner) : base(message ?? "Ractor::IsolationError", inner) { }
    }

    public class RactorRemoteError : RactorError {
        public RactorRemoteError() : this(null, null) { }
        public RactorRemoteError(string message) : this(message, null) { }
        public RactorRemoteError(string message, Exception inner) : base(message ?? "Ractor::RemoteError", inner) { }
    }

    public class RactorMovedError : RactorError {
        public RactorMovedError() : this(null, null) { }
        public RactorMovedError(string message) : this(message, null) { }
        public RactorMovedError(string message, Exception inner) : base(message ?? "Ractor::MovedError", inner) { }
    }

    public class RactorUnsafeError : RactorError {
        public RactorUnsafeError() : this(null, null) { }
        public RactorUnsafeError(string message) : this(message, null) { }
        public RactorUnsafeError(string message, Exception inner) : base(message ?? "Ractor::UnsafeError", inner) { }
    }

    public class RactorClosedError : StopIteration {
        public RactorClosedError() : this(null, null) { }
        public RactorClosedError(string message) : this(message, null) { }
        public RactorClosedError(string message, Exception inner) : base(message ?? "Ractor::ClosedError", inner) { }
    }

    #endregion

    /// <summary>
    /// Ruby 4.0's Ractor on CLR threads. A Ractor is a Ruby thread (with its own ThreadGroup) whose
    /// threads all know the Ractor they belong to; messages travel through Ractor::Port queues and
    /// are copied (or moved, or shared when shareable) on the way, as MRI does it. What is not
    /// enforced is the isolation itself: a Ractor can still reach global variables, class
    /// variables, and unshareable constants of the main Ractor (see README, "Ractor").
    /// </summary>
    [RubyClass("Ractor", Inherits = typeof(object))]
    public sealed class RubyRactor {
        private readonly RubyContext/*!*/ _context;
        private readonly Registry/*!*/ _registry;
        private readonly int _id;
        private readonly MutableString _name;
        private readonly string _location;

        // guards every port this Ractor owns, and the Ractor's own state; ports wait on it
        internal readonly object/*!*/ Sync = new object();

        private readonly RactorPort/*!*/ _defaultPort;
        private readonly List<RactorPort>/*!*/ _openPorts = new List<RactorPort>();
        private readonly List<RactorPort>/*!*/ _monitors = new List<RactorPort>();
        private readonly Dictionary<RubySymbol, object>/*!*/ _locals = new Dictionary<RubySymbol, object>();
        private int _lastPortId;

        private bool _terminated;
        private object _value;
        private Exception _exception;
        private RubyRactor _successor;

        /// <summary>
        /// Per runtime: the main Ractor, the ids handed out and how many Ractors are alive.
        /// </summary>
        private sealed class Registry {
            internal RubyRactor Main;
            internal int LastId;
            internal int Alive = 1;
        }

        private static readonly object RegistryKey = new object();

        private RubyRactor(RubyContext/*!*/ context, Registry/*!*/ registry, int id, MutableString name, string location) {
            _context = context;
            _registry = registry;
            _id = id;
            _name = name;
            _location = location;
            _defaultPort = new RactorPort(this, NextPortId());
            _openPorts.Add(_defaultPort);
        }

        internal RubyContext/*!*/ Context { get { return _context; } }

        /// <summary>
        /// The thread that runs the Ractor's block; an exception that ends another thread of the
        /// Ractor with abort_on_exception (or a SystemExit) is re-raised in it. Null for the main Ractor.
        /// </summary>
        internal Thread MainThread { get; set; }

        internal int Id { get { return _id; } }

        internal int NextPortId() {
            return Interlocked.Increment(ref _lastPortId);
        }

        private static Registry/*!*/ GetRegistry(RubyContext/*!*/ context) {
            return (Registry)context.GetOrCreateLibraryData(RegistryKey, () => {
                var registry = new Registry();
                registry.Main = new RubyRactor(context, registry, ++registry.LastId, null, null);
                return registry;
            });
        }

        internal static RubyRactor/*!*/ GetMain(RubyContext/*!*/ context) {
            return GetRegistry(context).Main;
        }

        internal static RubyRactor/*!*/ GetCurrent(RubyContext/*!*/ context) {
            return ThreadOps.RubyThreadInfo.Current.Ractor ?? GetMain(context);
        }

        #region Ports

        internal void AddOpenPort(RactorPort/*!*/ port) {
            lock (Sync) {
                if (_terminated) {
                    port.Closed = true;
                } else {
                    _openPorts.Add(port);
                }
            }
        }

        internal void RemoveOpenPort(RactorPort/*!*/ port) {
            _openPorts.Remove(port);
        }

        /// <summary>
        /// Waits (holding <see cref="Sync"/>) for a message on any of <paramref name="ports"/>, all of them this
        /// Ractor's: the port it arrived on and the message. A closed port with nothing left in it raises.
        /// </summary>
        internal object/*!*/ Receive(IList<RactorPort>/*!*/ ports, out RactorPort/*!*/ port) {
            lock (Sync) {
                while (true) {
                    foreach (var p in ports) {
                        object message;
                        if (p.TryDequeue(out message)) {
                            port = p;
                            return message;
                        }
                        if (p.Closed) {
                            throw new RactorClosedError("The port was already closed");
                        }
                    }
                    try {
                        Monitor.Wait(Sync);
                    } catch (ThreadInterruptedException) {
                        RubyUtils.TranslateThreadInterrupt();
                    }
                }
            }
        }

        #endregion

        #region Life cycle

        private object Run(BlockParam/*!*/ block, object[]/*!*/ args) {
            object result;
            try {
                result = RubyUtils.EvaluateInSingleton(this, block, args);
            } catch (Exception e) {
                if (RubyUtils.IsRubyThreadExit(e) || e is ThreadInterruptedException) {
                    KillOtherThreads();
                    Terminate(null, null);
                    throw;
                }
                KillOtherThreads();
                Exception visible = RubyUtils.GetVisibleException(e);
                RubyExceptionData.GetInstance(visible).CaptureExceptionTrace(_context);
                // reported before the Ractor counts as terminated, so that the report comes first
                var info = ThreadOps.RubyThreadInfo.Current;
                if (info.ReportOnException && !(visible is SystemExit)) {
                    ThreadOps.ReportThreadException(_context, info, visible);
                }
                Terminate(null, visible);
                return null;
            }
            // killed while finishing: the kill had not reached a safe point yet, but it still wins
            if (ThreadOps.RubyThreadInfo.Current.ExitRequested) {
                result = null;
            }
            KillOtherThreads();
            Terminate(result, null);
            return result;
        }

        /// <summary>
        /// The other threads of a Ractor are killed when its block ends, as MRI does.
        /// </summary>
        private void KillOtherThreads() {
            foreach (var info in ThreadOps.RubyThreadInfo.GetRubyThreads(_context)) {
                Thread thread = info.Thread;
                if (info.Ractor == this && thread != null && thread != Thread.CurrentThread && thread.IsAlive) {
                    ThreadOps.Kill(thread);
                }
            }
        }

        private void Terminate(object value, Exception exception) {
            RactorPort[] monitors;
            lock (Sync) {
                if (_terminated) {
                    return;
                }
                _terminated = true;
                _value = value;
                _exception = exception;
                foreach (var port in _openPorts) {
                    port.Closed = true;
                }
                _openPorts.Clear();
                monitors = _monitors.ToArray();
                _monitors.Clear();
                Monitor.PulseAll(Sync);
            }
            Interlocked.Decrement(ref _registry.Alive);

            var token = _context.CreateAsciiSymbol(exception != null ? "aborted" : "exited");
            foreach (var port in monitors) {
                port.TrySend(token);
            }
        }

        private bool AddMonitor(RactorPort/*!*/ port) {
            bool exception;
            lock (Sync) {
                if (!_terminated) {
                    _monitors.Add(port);
                    return true;
                }
                exception = _exception != null;
            }
            port.TrySend(_context.CreateAsciiSymbol(exception ? "aborted" : "exited"));
            return false;
        }

        private void Unmonitor(RactorPort/*!*/ port) {
            lock (Sync) {
                _monitors.Remove(port);
            }
        }

        private object TakeValue(RubyRactor/*!*/ current) {
            lock (Sync) {
                if (_successor == null) {
                    _successor = current;
                } else if (_successor != current) {
                    throw new RactorError("Only the successor ractor can take a value");
                }
                if (_exception != null) {
                    var error = new RactorRemoteError("thrown by remote Ractor.");
                    RubyExceptionData.GetInstance(error).SetCause(_exception);
                    _context.SetInstanceVariable(error, "@ractor", this);
                    throw error;
                }
                return _value;
            }
        }

        private void JoinFrom(RubyRactor/*!*/ current) {
            var port = new RactorPort(current, current.NextPortId());
            current.AddOpenPort(port);
            try {
                AddMonitor(port);
                RactorPort from;
                object token = current.Receive(new[] { port }, out from);
                if (token is RubySymbol && token.ToString() == "aborted") {
                    TakeValue(current);
                }
            } finally {
                port.Close();
            }
        }

        #endregion

        #region Ractor.new, current, main, count, select, receive

        [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
        public static RubyRactor/*!*/ Create(CloneStorage/*!*/ cloneStorage, AllocateStorage/*!*/ allocateStorage,
            ConversionStorage<MutableString>/*!*/ toStr, BlockParam block, RubyClass/*!*/ self, params object[]/*!*/ args) {

            var context = self.Context;
            MutableString name = null;
            if (args.Length > 0) {
                var options = args[args.Length - 1] as Hash;
                if (options != null && options.IsKeywordArguments) {
                    Array.Resize(ref args, args.Length - 1);
                    foreach (var entry in options) {
                        var key = entry.Key as RubySymbol;
                        if (key == null || key.ToString() != "name") {
                            throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", context.Inspect(entry.Key));
                        }
                        if (entry.Value != null) {
                            name = Protocols.CastToString(toStr, entry.Value);
                            if (!name.Encoding.IsAsciiIdentity) {
                                throw RubyExceptions.CreateArgumentError("ASCII incompatible encoding ({0})", name.Encoding.Name);
                            }
                            name = name.Clone();
                            name.Freeze();
                        }
                    }
                }
            }

            if (block == null) {
                throw RubyExceptions.CreateArgumentError("must be called with a block");
            }
            var registry = GetRegistry(context);
            if (Volatile.Read(ref registry.Alive) == 1) {
                context.ReportCategoryWarning("experimental", "Ractor API is experimental and may change in future versions of Ruby.");
            }

            // MRI has made the Ractor (and used up its id) by the time it finds the block is not isolated
            int id = Interlocked.Increment(ref registry.LastId);
            CheckIsolated(block.Proc);

            // the arguments are a message: copied unless they are shareable
            var copier = new Copier(context, cloneStorage, allocateStorage, false);
            for (int i = 0; i < args.Length; i++) {
                args[i] = copier.Copy(args[i]);
            }

            var ractor = new RubyRactor(context, registry, id, name,
                ThreadOps.GetCallerLocation(context));
            Interlocked.Increment(ref registry.Alive);

            block.IsThreadRoot = true;
            ThreadOps.StartRactorThread(context, ractor, () => ractor.Run(block, args));
            return ractor;
        }

        /// <summary>
        /// MRI isolates the block of Ractor.new: one that uses a local variable of an enclosing scope
        /// cannot run in another Ractor. The compiler records which ones a block uses.
        /// </summary>
        private static void CheckIsolated(Proc/*!*/ proc) {
            var names = proc.Dispatcher.OuterVariableNames;
            if (names != null && names.Length > 0) {
                throw RubyExceptions.CreateArgumentError("{0}",
                    "can not isolate a Proc because it accesses outer variables (" + String.Join(", ", names) + ").");
            }
        }

        [RubyMethod("allocate", RubyMethodAttributes.PublicSingleton)]
        public static RubyRactor/*!*/ Allocate(RubyClass/*!*/ self) {
            throw RubyExceptions.CreateTypeError("allocator undefined for Ractor");
        }

        [RubyMethod("current", RubyMethodAttributes.PublicSingleton)]
        public static RubyRactor/*!*/ GetCurrent(RubyClass/*!*/ self) {
            return GetCurrent(self.Context);
        }

        [RubyMethod("main", RubyMethodAttributes.PublicSingleton)]
        public static RubyRactor/*!*/ GetMain(RubyClass/*!*/ self) {
            return GetMain(self.Context);
        }

        [RubyMethod("main?", RubyMethodAttributes.PublicSingleton)]
        public static bool IsMain(RubyClass/*!*/ self) {
            return GetCurrent(self.Context) == GetMain(self.Context);
        }

        [RubyMethod("count", RubyMethodAttributes.PublicSingleton)]
        public static int Count(RubyClass/*!*/ self) {
            return Volatile.Read(ref GetRegistry(self.Context).Alive);
        }

        [RubyMethod("receive", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("recv", RubyMethodAttributes.PublicSingleton)]
        public static object ReceiveCurrent(RubyClass/*!*/ self) {
            return GetCurrent(self.Context)._defaultPort.Receive();
        }

        [RubyMethod("receive", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("recv", RubyMethodAttributes.PrivateInstance)]
        public static object Receive(RubyRactor/*!*/ self) {
            return self._defaultPort.Receive();
        }

        /// <summary>
        /// Waits for the first of the given ports to receive a message, or of the given Ractors to
        /// terminate: [port, message] or [ractor, its value].
        /// </summary>
        [RubyMethod("select", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ Select(RubyClass/*!*/ self, params object[]/*!*/ sources) {
            if (sources.Length == 0) {
                throw RubyExceptions.CreateArgumentError("specify at least one Ractor::Port or Ractor");
            }

            var context = self.Context;
            var current = GetCurrent(context);
            var ports = new RactorPort[sources.Length];
            var monitored = new Dictionary<RactorPort, RubyRactor>();
            try {
                for (int i = 0; i < sources.Length; i++) {
                    var ractor = sources[i] as RubyRactor;
                    if (ractor != null) {
                        var port = new RactorPort(current, current.NextPortId());
                        current.AddOpenPort(port);
                        monitored[port] = ractor;
                        ractor.AddMonitor(port);
                        ports[i] = port;
                    } else if ((ports[i] = sources[i] as RactorPort) == null) {
                        throw RubyExceptions.CreateArgumentError("should be Ractor::Port or Ractor");
                    }
                    ports[i].RequireReceiver(current);
                }

                RactorPort from;
                object message = current.Receive(ports, out from);
                RubyRactor source;
                if (monitored.TryGetValue(from, out source)) {
                    return RubyOps.MakeArray2(source, source.TakeValue(current));
                }
                return RubyOps.MakeArray2(from, message);
            } finally {
                foreach (var entry in monitored) {
                    entry.Value.Unmonitor(entry.Key);
                    entry.Key.Close();
                }
            }
        }

        #endregion

        #region send, <<, join, value, monitor, unmonitor, close, default_port

        [RubyMethod("send")]
        [RubyMethod("<<")]
        public static RubyRactor/*!*/ Send(CloneStorage/*!*/ cloneStorage, AllocateStorage/*!*/ allocateStorage,
            RubyRactor/*!*/ self, object message, [Optional]Hash options) {
            self._defaultPort.Send(cloneStorage, allocateStorage, message, options);
            return self;
        }

        [RubyMethod("join")]
        public static RubyRactor/*!*/ Join(RubyRactor/*!*/ self) {
            self.JoinFrom(GetCurrent(self._context));
            return self;
        }

        [RubyMethod("value")]
        public static object GetValue(RubyRactor/*!*/ self) {
            var current = GetCurrent(self._context);
            self.JoinFrom(current);
            return self.TakeValue(current);
        }

        [RubyMethod("monitor")]
        public static bool MonitorPort(RubyRactor/*!*/ self, [NotNull]RactorPort/*!*/ port) {
            return self.AddMonitor(port);
        }

        [RubyMethod("unmonitor")]
        public static RubyRactor/*!*/ UnmonitorPort(RubyRactor/*!*/ self, [NotNull]RactorPort/*!*/ port) {
            self.Unmonitor(port);
            return self;
        }

        [RubyMethod("close")]
        public static RactorPort/*!*/ Close(RubyRactor/*!*/ self) {
            return RactorPort.Close(self._defaultPort);
        }

        [RubyMethod("default_port")]
        public static RactorPort/*!*/ GetDefaultPort(RubyRactor/*!*/ self) {
            return self._defaultPort;
        }

        #endregion

        #region name, inspect

        [RubyMethod("name")]
        public static MutableString GetName(RubyRactor/*!*/ self) {
            return self._name;
        }

        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ Inspect(RubyRactor/*!*/ self) {
            var result = MutableString.CreateMutable(RubyEncoding.UTF8);
            result.Append("#<Ractor:#").Append(self._id.ToString());
            if (self._name != null) {
                result.Append(' ').Append(self._name);
            }
            if (self._location != null) {
                result.Append(' ').Append(self._location);
            }
            // MRI says "running" of a Ractor waiting for a message too
            string status;
            lock (self.Sync) {
                status = self._terminated ? "terminated" : "running";
            }
            return result.Append(' ').Append(status).Append('>');
        }

        #endregion

        #region Ractor-local storage

        private static RubySymbol/*!*/ ToLocalKey(RubyContext/*!*/ context, object key) {
            var symbol = key as RubySymbol;
            if (symbol != null) {
                return symbol;
            }
            var str = key as MutableString;
            if (str != null) {
                return context.CreateSymbol(str);
            }
            throw RubyExceptions.CreateTypeError("{0}", context.Inspect(key) + " is not a symbol nor a string");
        }

        private object GetLocal(RubySymbol/*!*/ key) {
            lock (_locals) {
                object value;
                return _locals.TryGetValue(key, out value) ? value : null;
            }
        }

        private object SetLocal(RubySymbol/*!*/ key, object value) {
            lock (_locals) {
                _locals[key] = value;
            }
            return value;
        }

        private RubyRactor/*!*/ RequireCurrent(string/*!*/ operation) {
            if (GetCurrent(_context) != this) {
                throw new RuntimeError("Cannot " + operation + " ractor local storage for non-current ractor");
            }
            return this;
        }

        [RubyMethod("[]")]
        public static object GetLocal(RubyRactor/*!*/ self, object key) {
            return self.RequireCurrent("get").GetLocal(ToLocalKey(self._context, key));
        }

        [RubyMethod("[]=")]
        public static object SetLocal(RubyRactor/*!*/ self, object key, object value) {
            return self.RequireCurrent("set").SetLocal(ToLocalKey(self._context, key), value);
        }

        [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
        public static object GetCurrentLocal(RubyClass/*!*/ self, object key) {
            return GetCurrent(self.Context).GetLocal(ToLocalKey(self.Context, key));
        }

        [RubyMethod("[]=", RubyMethodAttributes.PublicSingleton)]
        public static object SetCurrentLocal(RubyClass/*!*/ self, object key, object value) {
            return GetCurrent(self.Context).SetLocal(ToLocalKey(self.Context, key), value);
        }

        /// <summary>
        /// The value stored under the key, or the block's value, stored, if there is none yet. Atomic:
        /// two threads of a Ractor asking at once get the same value.
        /// </summary>
        [RubyMethod("store_if_absent", RubyMethodAttributes.PublicSingleton)]
        public static object StoreIfAbsent(BlockParam block, RubyClass/*!*/ self, object key) {
            var ractor = GetCurrent(self.Context);
            var symbol = ToLocalKey(self.Context, key);
            lock (ractor._locals) {
                object value;
                if (ractor._locals.TryGetValue(symbol, out value)) {
                    return value;
                }
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }
                if (block.Yield(symbol, out value)) {
                    return value;
                }
                ractor._locals[symbol] = value;
                return value;
            }
        }

        #endregion

        #region shareable?, make_shareable, shareable_proc, shareable_lambda

        [RubyMethod("shareable?", RubyMethodAttributes.PublicSingleton)]
        public static bool IsShareable(RubyClass/*!*/ self, object obj) {
            return Shareability.IsShareable(self.Context, obj);
        }

        [RubyMethod("make_shareable", RubyMethodAttributes.PublicSingleton)]
        public static object MakeShareable(CloneStorage/*!*/ cloneStorage, AllocateStorage/*!*/ allocateStorage, UnaryStorage/*!*/ freezeStorage,
            RubyClass/*!*/ self, object obj, [Optional]Hash options) {

            var context = self.Context;
            bool copy = false;
            if (options != null) {
                foreach (var entry in options) {
                    var key = entry.Key as RubySymbol;
                    if (!options.IsKeywordArguments || key == null || key.ToString() != "copy") {
                        throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", context.Inspect(entry.Key));
                    }
                    copy = RubyOps.IsTrue(entry.Value);
                }
            }
            if (copy) {
                obj = new Copier(context, cloneStorage, allocateStorage, false).Copy(obj);
            }
            new Shareability(context, freezeStorage).MakeShareable(obj);
            return obj;
        }

        [RubyMethod("shareable_proc", RubyMethodAttributes.PublicSingleton)]
        public static Proc/*!*/ ShareableProc(BlockParam block, RubyClass/*!*/ self, [Optional]Hash options) {
            return MakeShareableProc(block, self, options, false);
        }

        [RubyMethod("shareable_lambda", RubyMethodAttributes.PublicSingleton)]
        public static Proc/*!*/ ShareableLambda(BlockParam block, RubyClass/*!*/ self, [Optional]Hash options) {
            return MakeShareableProc(block, self, options, true);
        }

        private static Proc/*!*/ MakeShareableProc(BlockParam block, RubyClass/*!*/ self, Hash options, bool lambda) {
            var context = self.Context;
            object procSelf = null;
            if (options != null) {
                foreach (var entry in options) {
                    var key = entry.Key as RubySymbol;
                    if (!options.IsKeywordArguments || key == null || key.ToString() != "self") {
                        throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", context.Inspect(entry.Key));
                    }
                    procSelf = entry.Value;
                }
            }
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }
            if (!Shareability.IsShareable(context, procSelf)) {
                // MRI formats it with #to_s
                throw new RactorIsolationError("self should be shareable: " +
                    (procSelf is MutableString ? procSelf.ToString() : context.Inspect(procSelf).ToString()));
            }
            var result = block.Proc.CopyWithSelf(procSelf, lambda);
            Shareability.CheckProc(context, result, "make a Proc shareable");
            Shareability.MarkShareable(result);
            context.FreezeObject(result);
            return result;
        }

        #endregion

        #region Exception classes, MovedObject

        [RubyException("Error", Extends = typeof(RactorError))]
        public static class ErrorOps {
        }

        [RubyException("IsolationError", Extends = typeof(RactorIsolationError))]
        public static class IsolationErrorOps {
        }

        [RubyException("RemoteError", Extends = typeof(RactorRemoteError))]
        public static class RemoteErrorOps {
            [RubyMethod("ractor")]
            public static object GetRactor(RubyContext/*!*/ context, RactorRemoteError/*!*/ self) {
                object ractor;
                return context.TryGetInstanceVariable(self, "@ractor", out ractor) ? ractor : null;
            }
        }

        [RubyException("MovedError", Extends = typeof(RactorMovedError))]
        public static class MovedErrorOps {
        }

        [RubyException("ClosedError", Extends = typeof(RactorClosedError))]
        public static class ClosedErrorOps {
        }

        [RubyException("UnsafeError", Extends = typeof(RactorUnsafeError))]
        public static class UnsafeErrorOps {
        }

        private static readonly object MovedObjectKey = new object();

        /// <summary>
        /// Ractor::MovedObject: the class an object takes once it has been moved to another Ractor.
        /// Every method of it, BasicObject's included, raises Ractor::MovedError. A Ruby class (no
        /// CLR type of its own), so that a String or Array can be given it in place.
        /// </summary>
        [RubyConstant("MovedObject")]
        public static RubyClass/*!*/ GetMovedObjectClass(RubyModule/*!*/ module) {
            return GetMovedObjectClass(module.Context);
        }

        internal static RubyClass/*!*/ GetMovedObjectClass(RubyContext/*!*/ context) {
            return (RubyClass)context.GetOrCreateLibraryData(MovedObjectKey, () => {
                var cls = context.CreateRubyClass("Ractor::MovedObject", context.BasicObjectClass);
                var missing = new RubyLibraryMethodInfo(
                    new[] { LibraryOverload.Create(new Func<object, object[], object>(MovedObjectMissing), true, 0, 0) },
                    RubyMethodVisibility.Public, cls
                );
                foreach (var name in new[] { "method_missing", "__send__", "!", "==", "!=", "__id__", "equal?", "instance_eval", "instance_exec" }) {
                    cls.AddMethod(context, name, missing);
                }
                return cls;
            });
        }

        private static object MovedObjectMissing(object self, params object[] args) {
            throw new RactorMovedError("can not send any methods to a moved object");
        }

        #endregion
    }

    /// <summary>
    /// Ractor::Port: a queue of messages that only the Ractor that made it may receive from (or
    /// close), and that any Ractor may send to. Shareable: a port is passed by reference.
    /// </summary>
    [RubyClass("Port", DefineIn = typeof(RubyRactor), Inherits = typeof(object))]
    public sealed class RactorPort {
        private readonly RubyRactor/*!*/ _owner;
        private readonly int _id;
        private readonly Queue<object>/*!*/ _queue = new Queue<object>();

        // guarded by _owner.Sync
        internal bool Closed;

        internal RactorPort(RubyRactor/*!*/ owner, int id) {
            _owner = owner;
            _id = id;
        }

        internal bool TryDequeue(out object message) {
            if (_queue.Count > 0) {
                message = _queue.Dequeue();
                return true;
            }
            message = null;
            return false;
        }

        internal void RequireReceiver(RubyRactor/*!*/ current) {
            if (current != _owner) {
                throw new RactorError("only allowed from the creator Ractor of this port");
            }
        }

        internal object Receive() {
            RequireReceiver(RubyRactor.GetCurrent(_owner.Context));
            RactorPort port;
            return _owner.Receive(new[] { this }, out port);
        }

        private bool Enqueue(object message) {
            lock (_owner.Sync) {
                if (Closed) {
                    return false;
                }
                _queue.Enqueue(message);
                Monitor.PulseAll(_owner.Sync);
                return true;
            }
        }

        internal bool TrySend(object message) {
            return Enqueue(message);
        }

        internal void Send(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ cloneStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage, object message, Hash options) {

            var context = _owner.Context;
            bool move = false;
            if (options != null) {
                foreach (var entry in options) {
                    var key = entry.Key as RubySymbol;
                    if (!options.IsKeywordArguments || key == null || key.ToString() != "move") {
                        throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", context.Inspect(entry.Key));
                    }
                    move = RubyOps.IsTrue(entry.Value);
                }
            }

            bool closed;
            lock (_owner.Sync) {
                closed = Closed;
            }
            if (closed) {
                throw new RactorClosedError("The port was already closed");
            }

            var copier = new Copier(context, cloneStorage, allocateStorage, move);
            object payload = copier.Copy(message);
            if (!Enqueue(payload)) {
                throw new RactorClosedError("The port was already closed");
            }
            copier.FinishMove();
        }

        internal void Close() {
            lock (_owner.Sync) {
                Closed = true;
                _owner.RemoveOpenPort(this);
                Monitor.PulseAll(_owner.Sync);
            }
        }

        [RubyConstructor]
        public static RactorPort/*!*/ Create(RubyClass/*!*/ self) {
            var owner = RubyRactor.GetCurrent(self.Context);
            var port = new RactorPort(owner, owner.NextPortId());
            owner.AddOpenPort(port);
            return port;
        }

        [RubyMethod("receive")]
        public static object Receive(RactorPort/*!*/ self) {
            return self.Receive();
        }

        [RubyMethod("send")]
        [RubyMethod("<<")]
        public static RactorPort/*!*/ Send(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ cloneStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            RactorPort/*!*/ self, object message, [Optional]Hash options) {
            self.Send(cloneStorage, allocateStorage, message, options);
            return self;
        }

        [RubyMethod("close")]
        public static RactorPort/*!*/ Close(RactorPort/*!*/ self) {
            if (RubyRactor.GetCurrent(self._owner.Context) != self._owner) {
                throw new RactorError("closing port by other ractors is not allowed");
            }
            self.Close();
            return self;
        }

        [RubyMethod("closed?")]
        public static bool IsClosed(RactorPort/*!*/ self) {
            lock (self._owner.Sync) {
                return self.Closed;
            }
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RactorPort/*!*/ self) {
            return MutableString.CreateAscii("#<Ractor::Port to:#" + self._owner.Id + " id:" + self._id + ">");
        }
    }

    /// <summary>
    /// What may be shared between Ractors, and making an object graph so (Ractor.make_shareable).
    /// Shareable: immediates, modules, Ractors and ports, and frozen objects that refer only to
    /// shareable objects - except the kinds MRI never shares (threads, mutexes, enumerators...).
    /// A Proc or Method is shareable once made so. Objects found shareable are remembered: they
    /// are frozen, so they cannot stop being shareable.
    /// </summary>
    internal sealed class Shareability {
        private static readonly ConditionalWeakTable<object, object> _shareable = new ConditionalWeakTable<object, object>();

        // CRuby's T_DATA kinds that are not RUBY_TYPED_FROZEN_SHAREABLE: freezing does not make them shareable
        private static readonly HashSet<string> _neverShareable = new HashSet<string> {
            "Thread", "Thread::Mutex", "Thread::Queue", "Thread::SizedQueue", "Thread::ConditionVariable", "Monitor",
            "Random", "Enumerator", "Enumerator::Yielder", "Enumerator::Generator", "Enumerator::Chain", "Enumerator::Product",
            "Enumerator::ArithmeticSequence", "Binding", "ObjectSpace::WeakMap", "ObjectSpace::WeakKeyMap", "Fiber",
            "Encoding::Converter", "StringIO", "TracePoint", "Ractor::MovedObject",
        };

        private readonly RubyContext/*!*/ _context;
        private readonly CallSiteStorage<Func<CallSite, object, object>> _freezeStorage;

        internal Shareability(RubyContext/*!*/ context, CallSiteStorage<Func<CallSite, object, object>> freezeStorage) {
            _context = context;
            _freezeStorage = freezeStorage;
        }

        internal static void MarkShareable(object/*!*/ obj) {
            _shareable.AddOrUpdate(obj, null);
        }

        private static readonly object EnvKey = new object();

        // ENV: one object for the whole process, as in MRI
        private static bool IsEnv(RubyContext/*!*/ context, object/*!*/ obj) {
            object env = context.GetOrCreateLibraryData(EnvKey, () => {
                object value;
                return context.ObjectClass.TryGetConstant(null, "ENV", out value) ? value : EnvKey;
            });
            return obj == env;
        }

        private static bool IsAlwaysShareable(object obj) {
            return obj == null || obj is bool || obj is int || obj is long || obj is double || obj is BigInteger
                || obj is RubySymbol || obj is RubyModule || obj is RubyRactor || obj is RactorPort || obj is RubyEncoding
                || obj is char || obj is float || obj is decimal;
        }

        internal static bool IsNeverShareable(RubyContext/*!*/ context, object/*!*/ obj) {
            for (RubyClass cls = context.GetClassOf(obj); cls != null; cls = cls.SuperClass) {
                if (cls.Name != null && _neverShareable.Contains(cls.Name)) {
                    return true;
                }
            }
            return false;
        }

        internal static bool IsShareable(RubyContext/*!*/ context, object obj) {
            if (IsAlwaysShareable(obj)) {
                return true;
            }
            object dummy;
            if (_shareable.TryGetValue(obj, out dummy) || IsEnv(context, obj)) {
                return true;
            }
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (!IsShareable(context, obj, visited)) {
                return false;
            }
            foreach (var item in visited) {
                MarkShareable(item);
            }
            return true;
        }

        private static bool IsShareable(RubyContext/*!*/ context, object obj, HashSet<object>/*!*/ visited) {
            object dummy;
            if (IsAlwaysShareable(obj) || _shareable.TryGetValue(obj, out dummy) || !visited.Add(obj)) {
                return true;
            }
            if (!context.IsObjectFrozen(obj) || obj is Proc || obj is RubyMethod || IsNeverShareable(context, obj)) {
                return false;
            }
            foreach (var reference in GetReferences(context, obj)) {
                if (!IsShareable(context, reference, visited)) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The objects <paramref name="obj"/> refers to that decide whether it is shareable.
        /// </summary>
        internal static IEnumerable<object>/*!*/ GetReferences(RubyContext/*!*/ context, object/*!*/ obj) {
            foreach (var name in context.GetStateInstanceVariableNames(obj)) {
                object value;
                if (context.TryGetInstanceVariable(obj, name, out value)) {
                    yield return value;
                }
            }

            var array = obj as RubyArray;
            if (array != null) {
                foreach (var item in array.ToArray()) {
                    yield return item;
                }
                yield break;
            }

            var hash = obj as Hash;
            if (hash != null) {
                var pairs = new List<KeyValuePair<object, object>>(hash);
                foreach (var pair in pairs) {
                    yield return pair.Key;
                    yield return pair.Value;
                }
                yield return hash.DefaultValue;
                if (hash.DefaultProc != null) {
                    yield return hash.DefaultProc;
                }
                yield break;
            }

            var structure = obj as RubyStruct;
            if (structure != null) {
                foreach (var item in structure.Values) {
                    yield return item;
                }
                yield break;
            }

            var range = obj as Range;
            if (range != null) {
                yield return range.Begin;
                yield return range.End;
                yield break;
            }

            var match = obj as MatchData;
            if (match != null) {
                yield return match.OriginalString;
                yield break;
            }

            var exception = obj as Exception;
            if (exception != null) {
                // the class name stands for "no message" (MRI's nil), which refers to nothing
                object message = RubyExceptionData.GetInstance(exception).Message;
                var str = message as MutableString;
                if (str == null || !str.ToString().Equals(context.GetClassOf(exception).Name)) {
                    yield return message;
                }
            }
        }

        internal static void CheckProc(RubyContext/*!*/ context, Proc/*!*/ proc, string/*!*/ operation) {
            var assigned = proc.Dispatcher.AssignedOuterVariableNames;
            if (assigned != null && assigned.Length > 0) {
                throw RubyExceptions.CreateArgumentError("{0}",
                    "can not " + operation + " because it accesses outer variables (" + String.Join(", ", assigned) + ").");
            }
            var names = proc.Dispatcher.OuterVariableNames;
            if (names != null) {
                foreach (var name in names) {
                    object value = proc.LocalScope.ResolveLocalVariable(name);
                    if (!IsShareable(context, value)) {
                        throw new RactorIsolationError("cannot make a shareable Proc because it can refer unshareable object " +
                            context.Inspect(value).ToString() + " from variable '" + name + "'");
                    }
                }
            }
        }

        internal void MakeShareable(object obj) {
            if (IsShareable(_context, obj)) {
                return;
            }
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            Visit(obj, visited);
            foreach (var item in visited) {
                MarkShareable(item);
            }
        }

        private void Visit(object obj, HashSet<object>/*!*/ visited) {
            object dummy;
            if (IsAlwaysShareable(obj) || _shareable.TryGetValue(obj, out dummy) || !visited.Add(obj)) {
                return;
            }

            var proc = obj as Proc;
            if (proc != null) {
                if (proc.SymbolName == null) {
                    if (proc.Dispatcher.WrapsMethod) {
                        throw new RuntimeError("not supported yet");
                    }
                    if (!IsShareable(_context, proc.Self)) {
                        throw new RactorIsolationError("Proc's self is not shareable: " + _context.Inspect(proc).ToString());
                    }
                    CheckProc(_context, proc, "make a Proc shareable");
                }
                _context.FreezeObject(proc);
                return;
            }

            var method = obj as RubyMethod;
            if (method != null) {
                if (!IsShareable(_context, method.Target)) {
                    throw new RactorError("can not make shareable object for " + _context.Inspect(method).ToString() +
                        " because it refers unshareable objects");
                }
                Freeze(obj);
                return;
            }

            if (IsNeverShareable(_context, obj)) {
                throw new RactorError("can not make shareable object for " + _context.Inspect(obj).ToString());
            }

            Freeze(obj);
            foreach (var reference in GetReferences(_context, obj)) {
                Visit(reference, visited);
            }
        }

        private void Freeze(object/*!*/ obj) {
            if (_context.IsObjectFrozen(obj)) {
                return;
            }
            var site = _freezeStorage.GetCallSite("freeze", 0);
            site.Target(site, obj);
            if (!_context.IsObjectFrozen(obj)) {
                throw new RactorError("#freeze does not freeze object correctly");
            }
        }
    }

    /// <summary>
    /// The copy a message is turned into on its way to another Ractor: shareable objects as they
    /// are, the rest cloned deeply (cycles and shared references preserved). When moving, the
    /// originals then become Ractor::MovedObject (<see cref="FinishMove"/>).
    /// </summary>
    internal sealed class Copier {
        private readonly RubyContext/*!*/ _context;
        private readonly CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ _cloneStorage;
        private readonly CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ _allocateStorage;
        private readonly bool _move;
        private readonly Dictionary<object, object>/*!*/ _copies = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);

        internal Copier(RubyContext/*!*/ context, CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ cloneStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage, bool move) {
            _context = context;
            _cloneStorage = cloneStorage;
            _allocateStorage = allocateStorage;
            _move = move;
        }

        internal object Copy(object obj) {
            object copy;
            if (Shareability.IsShareable(_context, obj)) {
                return obj;
            }
            if (_copies.TryGetValue(obj, out copy)) {
                return copy;
            }

            // a move takes the object's slot over, which MRI can do only for its built-in kinds of object
            bool structural = obj is MutableString || obj is RubyArray || obj is Hash || obj is RubyStruct || obj is Range
                || obj is RubyRegex || obj is MatchData || obj is Exception
                || (obj is IRubyObject && !Shareability.IsNeverShareable(_context, obj));
            if (_move && !structural) {
                throw new RactorError(String.Format("can not move {0} object.", _context.GetClassDisplayName(obj)));
            }
            // a copy is a clone, and MRI's rb_obj_clone has no allocator for these
            if (obj is Proc || obj is RubyMethod || obj is UnboundMethod || obj is Thread || obj is Binding) {
                throw RubyExceptions.CreateTypeError("allocator undefined for {0}", _context.GetClassDisplayName(obj));
            }
            if (!structural && Shareability.IsNeverShareable(_context, obj)) {
                throw new RactorError(String.Format("can not copy {0} object.", _context.GetClassDisplayName(obj)));
            }

            var range = obj as Range;
            if (range != null) {
                copy = Range.CreateValidated(Copy(range.Begin), Copy(range.End), range.ExcludeEnd);
                _copies[obj] = copy;
                return copy;
            }

            if (!RubyUtils.TryDuplicateObject(_cloneStorage, _allocateStorage, obj, false, out copy)) {
                return obj;
            }
            _copies[obj] = copy;

            foreach (var name in _context.GetStateInstanceVariableNames(copy)) {
                object value;
                if (_context.TryGetInstanceVariable(copy, name, out value)) {
                    object valueCopy = Copy(value);
                    if (valueCopy != value) {
                        _context.SetInstanceVariable(copy, name, valueCopy);
                    }
                }
            }

            var array = copy as RubyArray;
            if (array != null) {
                for (int i = 0; i < array.Count; i++) {
                    array[i] = Copy(array[i]);
                }
            }

            var hash = copy as Hash;
            if (hash != null) {
                var pairs = new List<KeyValuePair<object, object>>(hash);
                hash.Clear();
                foreach (var pair in pairs) {
                    hash[Copy(pair.Key)] = Copy(pair.Value);
                }
                if (hash.DefaultProc != null) {
                    Copy(hash.DefaultProc);
                }
                hash.DefaultValue = Copy(hash.DefaultValue);
            }

            var structure = copy as RubyStruct;
            if (structure != null) {
                for (int i = 0; i < structure.ItemCount; i++) {
                    structure[i] = Copy(structure[i]);
                }
            }

            if (_context.IsObjectFrozen(obj)) {
                _context.FreezeObject(copy);
            }
            return copy;
        }

        /// <summary>
        /// Once the copy has been delivered, every object that was copied for a move becomes a
        /// Ractor::MovedObject, as MRI turns the original into one.
        /// </summary>
        internal void FinishMove() {
            if (!_move) {
                return;
            }
            var moved = RubyRactor.GetMovedObjectClass(_context);
            foreach (var original in _copies.Keys) {
                var rubyObject = original as IRubyObject;
                if (rubyObject != null) {
                    rubyObject.ImmediateClass = moved;
                } else if (RubyUtils.HasObjectState(original)) {
                    _context.ReplaceClrObjectClass(original, moved);
                }
            }
        }
    }
}

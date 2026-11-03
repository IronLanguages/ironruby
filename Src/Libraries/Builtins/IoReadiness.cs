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
using System.Net;
using System.Net.Sockets;
using System.Threading;
using IronRuby.Runtime;
using IronRuby.StandardLibrary.Sockets;

namespace IronRuby.Builtins {

    /// <summary>
    /// A thread's way of being woken out of a readiness wait by something the kernel does not
    /// know about - an in-process IO.pipe being written to, or closed. It is a UDP socket bound
    /// to the loopback interface and connected to itself: a byte sent on it makes it readable,
    /// so it can sit in the same poll(2) / Socket.Select as the sockets being watched, on Linux
    /// and on Windows alike. That is what lets IO.select([pipe, socket]) block in the kernel
    /// instead of polling every millisecond - and nio4r's selector and puma's server loop both
    /// put an IO.pipe next to their sockets, in every select they make.
    /// </summary>
    internal sealed class ReadinessWaiter {
        [ThreadStatic]
        private static ReadinessWaiter _current;

        [ThreadStatic]
        private static bool _unavailable;

        private readonly Socket/*!*/ _socket;
        private readonly byte[]/*!*/ _buffer = new byte[16];
        private int _signalled;

        private ReadinessWaiter(Socket/*!*/ socket) {
            _socket = socket;
        }

        /// <summary>
        /// This thread's waiter, or null when no loopback socket can be had (an IPv4-less
        /// sandbox); the waits then fall back to polling.
        /// </summary>
        internal static ReadinessWaiter Current {
            get {
                if (_current == null && !_unavailable) {
                    _current = Create();
                    _unavailable = (_current == null);
                }
                return _current;
            }
        }

        /// <summary>
        /// A waiter of its own, for an object that is woken from other threads (NIO::Selector's
        /// #wakeup) rather than a thread's. Null when no loopback socket can be had.
        /// </summary>
        internal static ReadinessWaiter Create() {
            Socket socket = null;
            try {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                socket.Connect(socket.LocalEndPoint);
                socket.Blocking = false;
                return new ReadinessWaiter(socket);
            } catch (SocketException) {
                if (socket != null) {
                    socket.Dispose();
                }
                return null;
            }
        }

        internal void Dispose() {
            _socket.Dispose();
        }

        internal Socket/*!*/ Socket {
            get { return _socket; }
        }

        /// <summary>Wakes the waiting thread; any number of signals before it wakes are one.</summary>
        internal void Signal() {
            if (Interlocked.Exchange(ref _signalled, 1) == 0) {
                try {
                    _socket.Send(_buffer, 0, 1, SocketFlags.None);
                } catch (SocketException) {
                } catch (ObjectDisposedException) {
                }
            }
        }

        /// <summary>
        /// Takes back any signal, before the waiter looks at what is ready. The socket is drained
        /// first and the flag cleared after, and the order matters: a Signal that finds the flag
        /// still set and sends nothing came after its pipe changed, and the caller's readiness
        /// check - which runs after this - sees that change; a Signal after the flag is cleared
        /// leaves its byte for the next wait to wake on. A byte that is stale by then only costs
        /// one spurious wakeup, and the next Reset drains it.
        /// </summary>
        internal void Reset() {
            try {
                while (_socket.Available > 0) {
                    _socket.Receive(_buffer);
                }
            } catch (SocketException) {
            }
            Volatile.Write(ref _signalled, 0);
        }
    }

    /// <summary>
    /// A layer that reads and writes an IO's stream itself, so that the kernel no longer knows
    /// whether a read or a write of that layer would block: OpenSSL's TLS transport, whose
    /// SslStream may already have drained the socket into a read that is still in flight, or
    /// hold decrypted bytes the socket never shows. IO.select, IO#wait and NIO::Selector ask it
    /// first, and it wakes their waiter when its answer changes.
    /// </summary>
    internal interface IReadinessHook {
        /// <summary>
        /// For IoReadiness.Read or Write: true or false when the hook knows, null to ask the
        /// socket. Asking may start the I/O that answers it.
        /// </summary>
        bool? IsReady(int kind);
        void AddWaiter(ReadinessWaiter/*!*/ waiter);
        void RemoveWaiter(ReadinessWaiter/*!*/ waiter);
    }

    /// <summary>
    /// What IO.select, IO#wait and NIO::Selector#select have in common: watching a set of IOs and
    /// blocking until one of them might have become ready. Readiness itself is decided by the
    /// caller (IoOps.IsReady); this only knows how to sleep until it is worth asking again, in one
    /// kernel call where the kernel can see everything being watched:
    ///
    ///   * a socket is its own socket - poll(2) on its descriptor on Unix, Socket.Select on Windows;
    ///   * any other kernel descriptor (a file, a popen pipe, the console) is polled on Unix;
    ///   * an in-process IO.pipe wakes the thread's ReadinessWaiter when it changes.
    ///
    /// The sleep is at most SliceMilliseconds, so that Thread#kill and Thread#raise - which cannot
    /// interrupt a thread inside a syscall - are noticed within that time.
    /// </summary>
    internal static class IoReadiness {
        internal const int SliceMilliseconds = 50;

        internal const int Read = 0;
        internal const int Write = 1;
        internal const int Error = 2;

        private static readonly bool _isUnix = System.IO.Path.DirectorySeparatorChar == '/';

        internal static Socket GetSocket(RubyIO/*!*/ io) {
            var buffered = io.GetStream();
            var socketStream = buffered.BaseStream as SocketStream;
            return (socketStream != null) ? socketStream.Socket : null;
        }

        internal static RubyPipe GetPipe(RubyIO/*!*/ io) {
            return io.GetStream().BaseStream as RubyPipe;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RubyIO, IReadinessHook>/*!*/ _hooks =
            new System.Runtime.CompilerServices.ConditionalWeakTable<RubyIO, IReadinessHook>();

        // No hook was ever set: the common case pays one read of a field.
        private static volatile bool _anyHooks;

        internal static void SetHook(RubyIO/*!*/ io, IReadinessHook/*!*/ hook) {
            _hooks.AddOrUpdate(io, hook);
            _anyHooks = true;
        }

        internal static IReadinessHook GetHook(RubyIO/*!*/ io) {
            IReadinessHook hook;
            return _anyHooks && _hooks.TryGetValue(io, out hook) ? hook : null;
        }

        #region Windows anonymous pipes

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr buffer, int size,
            IntPtr bytesRead, out int bytesAvailable, IntPtr bytesLeft);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern int GetFileType(Microsoft.Win32.SafeHandles.SafeFileHandle handle);

        private const int FILE_TYPE_DISK = 1;
        private const int FILE_TYPE_PIPE = 3;

        /// <summary>
        /// The Windows pipe behind an IO - IO.pipe and popen are CreatePipe handles in FileStreams
        /// there (WindowsSpawn.OsPipe) - or null. Windows has no poll(2) and Socket.Select only
        /// takes sockets, so without this a pipe had no readiness to report and IO.select called
        /// it ready whenever its mode allowed: a server loop that selects on a pipe next to its
        /// listeners (puma's does) then read from it and blocked.
        /// </summary>
        internal static System.IO.FileStream GetWindowsPipe(RubyIO/*!*/ io) {
            if (_isUnix) {
                return null;
            }
            var file = io.GetStream().BaseStream as System.IO.FileStream;
            if (file == null) {
                return null;
            }
            try {
                return GetFileType(file.SafeFileHandle) == FILE_TYPE_PIPE ? file : null;
            } catch (Exception) {
                return null;
            }
        }

        /// <summary>
        /// Whether a read from a Windows pipe would return at once: bytes are waiting, or the
        /// writer has gone (PeekNamedPipe fails with ERROR_BROKEN_PIPE) and the read is EOF.
        /// </summary>
        internal static bool IsWindowsPipeReadable(System.IO.FileStream/*!*/ pipe) {
            int available;
            if (!PeekNamedPipe(pipe.SafeFileHandle, IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero)) {
                return true;
            }
            return available > 0;
        }

        #endregion

        /// <summary>
        /// Whether a read from the IO's stream would return at once. Asked where a read already
        /// has bytes in hand and must not risk blocking with them. poll(2) on Unix; on Windows,
        /// where there is no libc to poll with, the socket, the pipe or the disk file is asked
        /// itself - a disk file is always readable, as poll(2) says of a regular file.
        /// </summary>
        internal static bool IsReadableNow(RubyIO/*!*/ io, System.IO.Stream/*!*/ baseStream) {
            if (_isUnix) {
                return DescriptorStream.IsReadableNow(RubyIO.DescriptorOf(baseStream));
            }
            var socketStream = baseStream as SocketStream;
            if (socketStream != null) {
                return socketStream.Socket.Poll(0, SelectMode.SelectRead);
            }
            var pipe = GetWindowsPipe(io);
            if (pipe != null) {
                return IsWindowsPipeReadable(pipe);
            }
            var file = baseStream as System.IO.FileStream;
            try {
                return file != null && GetFileType(file.SafeFileHandle) == FILE_TYPE_DISK;
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// Whether any of <paramref name="ios"/> can only tell the waiter about a change - an
        /// in-process pipe, or an IO with a readiness hook - so that a wait needs one.
        /// </summary>
        internal static bool NeedsWaiter(IList<RubyIO>/*!*/ ios) {
            for (int i = 0; i < ios.Count; i++) {
                if (!ios[i].Closed && (GetPipe(ios[i]) != null || GetHook(ios[i]) != null)) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Registers the waiter with every in-process pipe and readiness hook among
        /// <paramref name="ios"/>, and answers them - null if there were none - for Unregister to
        /// take it off again.
        /// </summary>
        internal static List<object> Register(ReadinessWaiter/*!*/ waiter, IList<RubyIO>/*!*/ ios) {
            List<object> registered = null;
            for (int i = 0; i < ios.Count; i++) {
                if (ios[i].Closed) {
                    continue;
                }
                var pipe = GetPipe(ios[i]);
                if (pipe != null) {
                    pipe.AddWaiter(waiter);
                    (registered ?? (registered = new List<object>())).Add(pipe);
                }
                var hook = GetHook(ios[i]);
                if (hook != null) {
                    hook.AddWaiter(waiter);
                    (registered ?? (registered = new List<object>())).Add(hook);
                }
            }
            return registered;
        }

        internal static void Unregister(ReadinessWaiter/*!*/ waiter, List<object> registered) {
            if (registered != null) {
                foreach (var item in registered) {
                    var pipe = item as RubyPipe;
                    if (pipe != null) {
                        pipe.RemoveWaiter(waiter);
                    } else {
                        ((IReadinessHook)item).RemoveWaiter(waiter);
                    }
                }
            }
        }

        /// <summary>
        /// Sleeps until one of the IOs may have become ready, the waiter is signalled, or
        /// <paramref name="milliseconds"/> pass. It may return early; the caller always asks again.
        /// <paramref name="ios"/> and <paramref name="kinds"/> are parallel (Read, Write, Error).
        /// </summary>
        internal static void Block(IList<RubyIO>/*!*/ ios, IList<int>/*!*/ kinds, ReadinessWaiter waiter, int milliseconds) {
            if (milliseconds <= 0) {
                return;
            }
            try {
                if (_isUnix) {
                    BlockUnix(ios, kinds, waiter, milliseconds);
                } else {
                    BlockWindows(ios, kinds, waiter, milliseconds);
                }
            } catch (ObjectDisposedException) {
                // Closed by another thread while we slept; the caller's readiness check reports it.
            } catch (SocketException) {
            } catch (ThreadInterruptedException) {
                RubyUtils.TranslateThreadInterrupt();
            }
        }

        private static void BlockUnix(IList<RubyIO>/*!*/ ios, IList<int>/*!*/ kinds, ReadinessWaiter waiter, int milliseconds) {
            var fds = new List<int>(ios.Count + 1);
            var events = new List<short>(ios.Count + 1);
            bool unwatchedPipe = false;

            for (int i = 0; i < ios.Count; i++) {
                var io = ios[i];
                if (io.Closed) {
                    return;
                }

                // A hook nobody can be woken by is only seen by asking it again, and soon.
                unwatchedPipe |= (waiter == null && GetHook(io) != null);

                int descriptor;
                var socket = GetSocket(io);
                if (socket != null) {
                    descriptor = (int)socket.Handle;
                } else if (GetPipe(io) != null) {
                    unwatchedPipe |= (waiter == null);
                    continue;
                } else {
                    descriptor = io.KernelDescriptor;
                    if (descriptor < 0) {
                        continue;
                    }
                }

                fds.Add(descriptor);
                events.Add(kinds[i] == Read ? RubyIO.POLLIN : kinds[i] == Write ? RubyIO.POLLOUT : (short)0x002);
            }

            if (waiter != null) {
                fds.Add((int)waiter.Socket.Handle);
                events.Add(RubyIO.POLLIN);
            }

            // A pipe nobody can signal us about is only seen by asking it again, and soon.
            if (unwatchedPipe) {
                milliseconds = 1;
            }

            if (fds.Count == 0 || !RubyIO.PollWait(fds, events, milliseconds)) {
                SleepSlice(milliseconds);
            }
        }

        private static void BlockWindows(IList<RubyIO>/*!*/ ios, IList<int>/*!*/ kinds, ReadinessWaiter waiter, int milliseconds) {
            List<Socket> reads = null, writes = null, errors = null;
            bool unwatchedPipe = false;

            for (int i = 0; i < ios.Count; i++) {
                if (ios[i].Closed) {
                    return;
                }
                unwatchedPipe |= (waiter == null && GetHook(ios[i]) != null);
                var socket = GetSocket(ios[i]);
                if (socket == null) {
                    // A Windows pipe cannot sit in Socket.Select either: it is asked again soon.
                    unwatchedPipe |= (waiter == null && GetPipe(ios[i]) != null) || GetWindowsPipe(ios[i]) != null;
                    continue;
                }
                switch (kinds[i]) {
                    case Read: (reads ?? (reads = new List<Socket>())).Add(socket); break;
                    case Write: (writes ?? (writes = new List<Socket>())).Add(socket); break;
                    default: (errors ?? (errors = new List<Socket>())).Add(socket); break;
                }
            }

            if (waiter != null) {
                (reads ?? (reads = new List<Socket>())).Add(waiter.Socket);
            }

            if (unwatchedPipe) {
                milliseconds = 1;
            }

            if (reads == null && writes == null && errors == null) {
                SleepSlice(milliseconds);
                return;
            }

            Socket.Select(reads, writes, errors, milliseconds * 1000);
        }

        private static void SleepSlice(int milliseconds) {
            // Nothing the kernel could wake us for: sleep in short steps, as the waits always did.
            Thread.Sleep(Math.Min(milliseconds, 1));
        }
    }
}

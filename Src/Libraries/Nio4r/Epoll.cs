/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IronRuby.StandardLibrary.Nio4r {

    /// <summary>
    /// epoll(7), for NIO::Selector's :epoll backend - Linux only, and only when epoll_create1
    /// answers; everywhere else the selector uses poll(2) or Socket.Select.  The BCL has no
    /// public wrapper (its own epoll lives inside System.Net.Sockets' async engine), so this is
    /// the syscalls themselves.
    ///
    /// Why bother, when poll(2) works: poll is O(registered) per call and epoll_wait is
    /// O(ready).  With a thousand idle keep-alive connections registered - which is what
    /// puma's reactor holds - one poll(2) costs more than the request it is waiting for.
    ///
    /// struct epoll_event is { uint32 events; uint64 data; }, which the kernel's headers pack
    /// on x86 and x86-64 (12 bytes) and align naturally elsewhere (16 bytes); the events are
    /// read and written as bytes at the right stride rather than through a struct whose
    /// layout would be right on only one of them.
    /// </summary>
    internal sealed class Epoll : IDisposable {
        internal const uint EPOLLIN = 0x001, EPOLLPRI = 0x002, EPOLLOUT = 0x004, EPOLLERR = 0x008, EPOLLHUP = 0x010;
        internal const uint EPOLLRDHUP = 0x2000;

        private const int EPOLL_CTL_ADD = 1, EPOLL_CTL_DEL = 2, EPOLL_CTL_MOD = 3;
        private const int EPOLL_CLOEXEC = 0x80000;
        private const int ENOENT = 2, EEXIST = 17;

        [DllImport("libc", SetLastError = true)]
        private static extern int epoll_create1(int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int epoll_ctl(int epfd, int op, int fd, byte[] ev);

        [DllImport("libc", SetLastError = true)]
        private static extern int epoll_wait(int epfd, byte[] events, int maxevents, int timeout);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid {
            public Handle(int fd) : base(true) {
                SetHandle((IntPtr)fd);
            }

            protected override bool ReleaseHandle() {
                return close((int)handle) == 0;
            }
        }

        private static readonly int Stride =
            (RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
             RuntimeInformation.ProcessArchitecture == Architecture.X86) ? 12 : 16;

        private static readonly bool _available = Probe();

        private readonly Handle/*!*/ _handle;
        private readonly byte[]/*!*/ _one = new byte[16];
        private byte[]/*!*/ _events = new byte[64 * 16];

        private static bool Probe() {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                return false;
            }
            try {
                int fd = epoll_create1(EPOLL_CLOEXEC);
                if (fd < 0) {
                    return false;
                }
                close(fd);
                return true;
            } catch (DllNotFoundException) {
                return false;
            } catch (EntryPointNotFoundException) {
                return false;
            }
        }

        internal static bool IsAvailable {
            get { return _available; }
        }

        private Epoll(Handle/*!*/ handle) {
            _handle = handle;
        }

        /// <summary>A new epoll instance, or null if there is none to be had.</summary>
        internal static Epoll Create() {
            if (!_available) {
                return null;
            }
            int fd = epoll_create1(EPOLL_CLOEXEC);
            return (fd < 0) ? null : new Epoll(new Handle(fd));
        }

        private int Descriptor {
            get { return (int)_handle.DangerousGetHandle(); }
        }

        private void Encode(uint events, long token) {
            BitConverter.TryWriteBytes(new Span<byte>(_one, 0, 4), events);
            BitConverter.TryWriteBytes(new Span<byte>(_one, Stride - 8, 8), token);
        }

        /// <summary>
        /// Watches <paramref name="fd"/> for <paramref name="events"/>, reporting it as
        /// <paramref name="token"/>; changes the events if it is watched already.  Answers 0, or
        /// the errno - EPERM for a descriptor epoll cannot watch (a regular file).
        /// </summary>
        internal int Set(int fd, uint events, long token, bool registered) {
            Encode(events, token);
            int op = registered ? EPOLL_CTL_MOD : EPOLL_CTL_ADD;
            if (epoll_ctl(Descriptor, op, fd, _one) == 0) {
                return 0;
            }
            int errno = Marshal.GetLastWin32Error();
            // The kernel forgot it (the descriptor was closed and reopened underneath), or never
            // had it: try the other operation once.
            if ((op == EPOLL_CTL_MOD && errno == ENOENT) || (op == EPOLL_CTL_ADD && errno == EEXIST)) {
                op = (op == EPOLL_CTL_MOD) ? EPOLL_CTL_ADD : EPOLL_CTL_MOD;
                if (epoll_ctl(Descriptor, op, fd, _one) == 0) {
                    return 0;
                }
                errno = Marshal.GetLastWin32Error();
            }
            return errno;
        }

        internal void Remove(int fd) {
            Encode(0, 0);
            epoll_ctl(Descriptor, EPOLL_CTL_DEL, fd, _one);
        }

        /// <summary>
        /// epoll_wait: calls <paramref name="report"/> with each ready token and its events, and
        /// answers how many there were (0 on timeout or EINTR).
        /// </summary>
        internal int Wait(int maxEvents, int milliseconds, Action<long, uint>/*!*/ report) {
            maxEvents = Math.Max(1, Math.Min(maxEvents, 4096));
            if (_events.Length < maxEvents * Stride) {
                _events = new byte[maxEvents * Stride];
            }
            int n = epoll_wait(Descriptor, _events, maxEvents, milliseconds);
            if (n < 0) {
                return 0;
            }
            for (int i = 0; i < n; i++) {
                uint events = BitConverter.ToUInt32(_events, i * Stride);
                long token = BitConverter.ToInt64(_events, i * Stride + Stride - 8);
                report(token, events);
            }
            return n;
        }

        public void Dispose() {
            _handle.Dispose();
        }
    }
}

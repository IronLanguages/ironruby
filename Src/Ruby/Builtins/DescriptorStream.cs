/* ****************************************************************************
 *
 * A Stream over a raw file descriptor, for the two things FileStream cannot do.
 *
 * FileStream tracks a position of its own and writes with pwrite(2) whenever it
 * believes the handle is seekable. Two of them over descriptors that share one
 * open file description - which is exactly what a child's standard output and
 * standard error are once both have been redirected to the same file - then each
 * write at their own offset and overwrite each other, where CRuby's sequential
 * writes append.
 *
 * And a read FileStream has started cannot be interrupted: closing the IO from
 * another thread, which Ruby defines as making the blocked read raise IOError,
 * leaves the reader parked in read(2) for ever. Waiting in poll(2) with a short
 * timeout and re-checking a flag gives the reader somewhere to notice from.
 *
 * ***************************************************************************/

using System;
using System.IO;
using System.Runtime.InteropServices;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    /// <summary>A stream that knows the descriptor it is reading and writing.</summary>
    public interface IDescriptorStream {
        int Descriptor { get; }
    }

    /// <summary>
    /// A stream that can make one write the kernel may cut short, and say how much it took -
    /// what #write_nonblock is. Negative means nothing fit.
    /// </summary>
    public interface INonBlockingWriteStream {
        int WriteNonBlocking(byte[]/*!*/ buffer, int offset, int count);
    }

    public sealed class DescriptorStream : Stream, IDescriptorStream, INonBlockingWriteStream {
        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd {
            public int fd;
            public short events;
            public short revents;
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "poll")]
        private static extern int sys_poll([In, Out] PollFd[] fds, uint count, int timeoutMilliseconds);

        [DllImport("libc", SetLastError = true, EntryPoint = "read")]
        private static extern IntPtr sys_read(int fd, byte[] buffer, IntPtr count);

        [DllImport("libc", SetLastError = true, EntryPoint = "write")]
        private static extern IntPtr sys_write(int fd, byte[] buffer, IntPtr count);

        [DllImport("libc", SetLastError = true, EntryPoint = "close")]
        private static extern int sys_close(int fd);

        [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
        private static extern int sys_fcntl(int fd, int cmd, int arg);

        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        private static extern int sys_open(string path, int flags);

        private const int O_RDONLY = 0;
        private const int O_DIRECTORY = 0x10000;
        private const int O_CLOEXEC = 0x80000;

        /// <summary>
        /// open(2) of a directory for reading - which FileStream refuses and MRI's File.open
        /// allows; reading from it then fails with EISDIR. Null where it cannot be done.
        /// </summary>
        public static DescriptorStream TryOpenDirectory(string/*!*/ path) {
            if (Path.DirectorySeparatorChar != '/') {
                return null;
            }
            int fd;
            try {
                fd = sys_open(path, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
            } catch (DllNotFoundException) {
                return null;
            } catch (EntryPointNotFoundException) {
                return null;
            }
            return (fd < 0) ? null : new DescriptorStream(fd, true, false, true);
        }

        private const short POLLIN = 0x001;
        private const short POLLOUT = 0x004;
        private const short POLLERR = 0x008;
        private const short POLLHUP = 0x010;
        private const int EINTR = 4;
        private const int EIO = 5;
        private const int EAGAIN = 11;   // == EWOULDBLOCK on Linux
        public const int EPIPE = 32;
        private const int EISDIR = 21;
        private const int F_GETFL = 3;
        private const int F_SETFL = 4;
        private const int O_NONBLOCK = 0x800;

        private readonly System.Threading.ManualResetEvent _closing = new System.Threading.ManualResetEvent(false);
        private int _descriptor;
        private volatile bool _closed;
        private readonly bool _readable;
        private readonly bool _writable;
        private readonly bool _ownsDescriptor;

        [DllImport("libc", EntryPoint = "isatty")]
        private static extern int sys_isatty(int fd);

        public DescriptorStream(int descriptor, bool readable, bool writable, bool ownsDescriptor) {
            _descriptor = descriptor;
            _readable = readable;
            _writable = writable;
            _ownsDescriptor = ownsDescriptor;
        }

        public int Descriptor {
            get { return _descriptor; }
        }

        public override bool CanRead { get { return _readable && !_closed; } }
        public override bool CanWrite { get { return _writable && !_closed; } }
        public override bool CanSeek { get { return false; } }

        public override long Length {
            get { throw new NotSupportedException(); }
        }

        public override long Position {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        // Not cached: IO#reopen dup2s another file onto the same descriptor, and a stale
        // answer would send a reopened terminal's timed read into an endless poll.
        private bool IsTerminal {
            get { return sys_isatty(_descriptor) == 1; }
        }

        [DllImport("libc", EntryPoint = "tcgetattr")]
        private static extern int sys_tcgetattr(int fd, byte[] termios);

        /// <summary>
        /// A terminal in non-canonical mode with VMIN 0 - IO#getch(min: 0, time: t), or
        /// IO#raw(min: 0) - makes read(2) itself return empty-handed once VTIME runs out (at
        /// once when it is 0), and that empty read is the answer: getch returns nil. poll(2)
        /// knows nothing of VTIME, so waiting for it first would wait for ever.  Linux's
        /// struct termios only (c_lflag at 12, c_cc at 17); elsewhere this answers false.
        /// </summary>
        private bool TimedTerminalRead() {
            if (!IsTerminal || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                return false;
            }
            const int ICANON = 0x0002;
            const int VMIN = 6;
            byte[] termios = new byte[60];
            if (sys_tcgetattr(_descriptor, termios) != 0) {
                return false;
            }
            return (BitConverter.ToUInt32(termios, 12) & ICANON) == 0 && termios[17 + VMIN] == 0;
        }

        public override void Flush() {
            // write(2) has already handed everything to the kernel, but stdio-style flush still
            // reports a reader that disappeared after the last buffered write. poll exposes that
            // state without writing an extra byte.
            //
            // Not on a terminal: a pty master reports POLLHUP as soon as the child on the slave
            // side has gone, which can be a moment after it read the very bytes just written
            // (cat reading its ^D) - that is no failed write, and MRI raises nothing.
            if (_writable && !_closed && !IsTerminal) {
                var fds = new PollFd[1];
                fds[0].fd = _descriptor;
                fds[0].events = POLLOUT;
                int ready = sys_poll(fds, 1, 0);
                if (ready > 0 && (fds[0].revents & (POLLERR | POLLHUP)) != 0) {
                    throw new IOException("write failed", EPIPE);
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) {
            if (count == 0) {
                return 0;
            }
            byte[] chunk = new byte[count];
            while (true) {
                if (_closed) {
                    throw RubyExceptions.CreateIOError("stream closed in another thread");
                }

                // Ask first, without waiting, and only then park in poll(2) - see WaitFor.
                var fds = new PollFd[1];
                fds[0].fd = _descriptor;
                fds[0].events = POLLIN;
                int ready = sys_poll(fds, 1, 0);
                if (ready < 0) {
                    if (Marshal.GetLastWin32Error() == EINTR) {
                        continue;
                    }
                    throw new IOException("poll failed");
                }
                if (ready == 0 && !TimedTerminalRead()) {
                    WaitFor(POLLIN);
                    continue;
                }

                // The close may have come in while this thread was in poll; the descriptor is
                // gone by now, so read(2) would report a bad descriptor rather than the
                // IOError Ruby defines for a stream closed underneath a blocked read.
                if (_closed) {
                    throw RubyExceptions.CreateIOError("stream closed in another thread");
                }

                int read = (int)sys_read(_descriptor, chunk, (IntPtr)count);
                if (read < 0) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == EINTR || error == EAGAIN) {
                        // EAGAIN: another reader took the bytes poll(2) saw, or the descriptor
                        // was left non-blocking by #read_nonblock. Wait for more either way.
                        continue;
                    }
                    if (error == EISDIR) {
                        // a directory opened by TryOpenDirectory
                        throw RubyExceptions.CreateEISDIR("read");
                    }
                    if (error == EIO) {
                        // a pty master whose slave side is all closed - see InputOutputError
                        throw new InputOutputError();
                    }
                    throw new IOException("read failed", error);
                }
                Buffer.BlockCopy(chunk, 0, buffer, offset, read);
                return read;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) {
            int written = 0;
            byte[] chunk = new byte[count];
            Buffer.BlockCopy(buffer, offset, chunk, 0, count);
            while (written < count) {
                byte[] rest;
                if (written == 0) {
                    rest = chunk;
                } else {
                    rest = new byte[count - written];
                    Buffer.BlockCopy(chunk, written, rest, 0, count - written);
                }
                int n = (int)sys_write(_descriptor, rest, (IntPtr)(count - written));
                if (n < 0) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == EINTR) {
                        continue;
                    }
                    if (error == EAGAIN) {
                        // #write_nonblock left the descriptor non-blocking, which MRI does too;
                        // a blocking write on it has to wait for room itself.
                        WaitFor(POLLOUT);
                        continue;
                    }
                    // EPIPE and the rest: the caller turns the errno into the Ruby exception.
                    throw new IOException("write failed", error);
                }
                written += n;
            }
        }

        // How long one poll(2) waits before the thread looks up to see whether it has been
        // closed underneath, killed or raised into. None of those can wake a thread inside poll.
        private const int WaitSliceMilliseconds = 20;

        /// <summary>
        /// Blocks in poll(2) until the descriptor reports the given event or this end is closed.
        ///
        /// This used to ask poll(2) with no timeout and then sleep 5 ms on a managed event, so
        /// that the thread looked asleep to Thread#status (a thread in a P/Invoke is Running to
        /// the CLR). But that made every wakeup wait for the next tick: a reader woke up to 5 ms
        /// after its data arrived, and a thread handing a byte to another through IO.pipe - which
        /// is how puma's and nio4r's wakeups work - took milliseconds per round trip. Now the
        /// kernel wakes the reader as soon as there is data, and the thread says it is blocked
        /// through RubyUtils.EnterNativeWait instead. A close from another thread, Thread#kill
        /// and Thread#raise are noticed within one slice.
        /// </summary>
        private void WaitFor(short events) {
            bool wasBlocked = RubyUtils.EnterNativeWait();
            try {
                while (!_closed) {
                    var fds = new PollFd[1];
                    fds[0].fd = _descriptor;
                    fds[0].events = events;
                    int ready = sys_poll(fds, 1, WaitSliceMilliseconds);
                    if (ready > 0) {
                        return;
                    }
                    if (ready < 0 && Marshal.GetLastWin32Error() != EINTR) {
                        throw new IOException("poll failed");
                    }
                    RubyUtils.CheckAsyncException();
                }
            } finally {
                RubyUtils.ExitNativeWait(wasBlocked);
            }
        }

        /// <summary>
        /// Puts the descriptor into O_NONBLOCK, as MRI's read_nonblock/write_nonblock do - and,
        /// like MRI, leaves it there, which is what io/nonblock then reports.
        /// </summary>
        private void SetNonBlocking() {
            int flags = sys_fcntl(_descriptor, F_GETFL, 0);
            if (flags >= 0 && (flags & O_NONBLOCK) == 0) {
                sys_fcntl(_descriptor, F_SETFL, flags | O_NONBLOCK);
            }
        }

        /// <summary>
        /// One read(2) that does not wait: the number of bytes read, 0 at end of file, or -1
        /// when the read would have blocked.
        /// </summary>
        /// <summary>
        /// True when read(2) on the descriptor would not block. Asked where a read has already
        /// produced bytes and must not risk an EWOULDBLOCK that would take them with it.
        /// </summary>
        public static bool IsReadableNow(int descriptor) {
            if (descriptor < 0) {
                return false;
            }
            var fds = new PollFd[1];
            fds[0].fd = descriptor;
            fds[0].events = POLLIN;
            return sys_poll(fds, 1, 0) > 0;
        }

        public int ReadNonBlocking(byte[]/*!*/ buffer, int offset, int count) {
            if (_closed) {
                throw RubyExceptions.CreateIOError("stream closed in another thread");
            }
            SetNonBlocking();
            byte[] chunk = new byte[count];
            while (true) {
                int read = (int)sys_read(_descriptor, chunk, (IntPtr)count);
                if (read >= 0) {
                    Buffer.BlockCopy(chunk, 0, buffer, offset, read);
                    return read;
                }
                int error = Marshal.GetLastWin32Error();
                if (error == EINTR) {
                    continue;
                }
                if (error == EAGAIN) {
                    return -1;
                }
                if (error == EIO) {
                    throw new InputOutputError();
                }
                throw new IOException("read failed", error);
            }
        }

        /// <summary>
        /// One write(2) that does not wait: the number of bytes the kernel took, which may be
        /// fewer than asked for just as with O_NONBLOCK, or -1 when the write would have blocked.
        /// </summary>
        public int WriteNonBlocking(byte[]/*!*/ buffer, int offset, int count) {
            if (_closed) {
                throw RubyExceptions.CreateIOError("stream closed in another thread");
            }
            SetNonBlocking();
            return WriteOnce(buffer, offset, count);
        }

        /// <summary>
        /// One write(2), leaving the descriptor's blocking mode as it is: IO#syswrite is a single
        /// syscall in MRI too, which is why it can report a short write on a full pipe rather than
        /// waiting for room. Returns -1 when a non-blocking descriptor refused the write.
        /// </summary>
        public int WriteOnce(byte[]/*!*/ buffer, int offset, int count) {
            if (_closed) {
                throw RubyExceptions.CreateIOError("stream closed in another thread");
            }
            byte[] chunk = new byte[count];
            Buffer.BlockCopy(buffer, offset, chunk, 0, count);
            while (true) {
                int written = (int)sys_write(_descriptor, chunk, (IntPtr)count);
                if (written >= 0) {
                    return written;
                }
                int error = Marshal.GetLastWin32Error();
                if (error == EINTR) {
                    continue;
                }
                if (error == EAGAIN) {
                    return -1;
                }
                // EPIPE and the rest: the caller turns the errno into the Ruby exception.
                throw new IOException("write failed", error);
            }
        }

        protected override void Dispose(bool disposing) {
            _closed = true;
            _closing.Set();
            int descriptor = _descriptor;
            if (_ownsDescriptor && descriptor >= 0) {
                _descriptor = -1;
                sys_close(descriptor);
            }
            base.Dispose(disposing);
        }
    }
}

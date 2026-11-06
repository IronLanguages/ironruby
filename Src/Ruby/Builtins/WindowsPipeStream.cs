/* ****************************************************************************
 *
 * A Windows anonymous pipe end (CreatePipe), read and written the way Ruby expects
 * of a pipe - what DescriptorStream is on Unix.
 *
 * A FileStream over the handle reads with a synchronous ReadFile, and three things
 * follow from that. The thread sits inside a P/Invoke, which the CLR reports as
 * Running, so Thread#status said "run" and Thread#stop? false for a thread blocked
 * in a pipe read - and ruby/spec waits with `Thread.pass until thread.stop?` before
 * it does anything to such a thread, for ever. Closing the IO from another thread,
 * which Ruby defines as making the blocked read raise IOError, could not reach the
 * read at all. And there was no way to read or write without waiting, so
 * #read_nonblock on an empty pipe and #write_nonblock on a full one blocked.
 *
 * Here a blocking read or write tells the thread library it is blocked
 * (RubyUtils.EnterNativeWait), a close from another thread cancels it with
 * CancelSynchronousIo, the non-blocking read asks PeekNamedPipe how much there is
 * to read, and the non-blocking write asks the pipe how much room it has.
 *
 * It is still a FileStream, so everything that asks for the handle behind an IO
 * (spawn redirections, IO.select's PeekNamedPipe) keeps finding it.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    /// <summary>One read(2) that does not wait: bytes read, 0 at end of file, -1 when it would block.</summary>
    public interface INonBlockingReadStream {
        int ReadNonBlocking(byte[]/*!*/ buffer, int offset, int count);
    }

    /// <summary>
    /// One write(2), as IO#syswrite makes it: on a descriptor in non-blocking mode it may take
    /// only part of the bytes, and -1 means it took none.
    /// </summary>
    public interface ISingleWriteStream {
        int WriteOnce(byte[]/*!*/ buffer, int offset, int count);
    }

    public sealed class WindowsPipeStream : FileStream, INonBlockingReadStream, INonBlockingWriteStream, ISingleWriteStream {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern unsafe bool ReadFile(SafeFileHandle handle, byte* buffer, int count, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern unsafe bool WriteFile(SafeFileHandle handle, byte* buffer, int count, out int written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(SafeFileHandle handle, IntPtr buffer, int size,
            IntPtr bytesRead, out int bytesAvailable, IntPtr bytesLeft);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelSynchronousIo(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(int desiredAccess, bool inheritHandle, int threadId);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int GetFileType(IntPtr handle);

        private const int FILE_TYPE_UNKNOWN = 0;
        private const int FILE_TYPE_PIPE = 3;
        private const int THREAD_TERMINATE = 0x0001;    // what CancelSynchronousIo needs
        private const int ERROR_BROKEN_PIPE = 109;
        private const int ERROR_NO_DATA = 232;          // "the pipe is being closed": the reader is gone
        private const int ERROR_OPERATION_ABORTED = 995;

        private volatile bool _closed;

        // The thread handles of the reads and writes in progress, for a close from another
        // thread to cancel.
        private readonly List<IntPtr>/*!*/ _inProgress = new List<IntPtr>();

        // Serialises the non-blocking writes, so that two of them cannot both take the same room.
        private readonly object/*!*/ _writeLock = new object();

        public WindowsPipeStream(SafeFileHandle/*!*/ handle, FileAccess access)
            : base(handle, access, 1, false) {
        }

        /// <summary>
        /// Whether <paramref name="handle"/> is a pipe - an anonymous one or a named one - or one
        /// whose reader has already gone: GetFileType then fails and says FILE_TYPE_UNKNOWN, and
        /// a child whose parent closed its end before the child wrote anything is exactly the
        /// process that most needs to hear EPIPE.
        /// </summary>
        public static bool IsPipe(IntPtr handle) {
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) {
                return false;
            }
            int type = GetFileType(handle);
            return type == FILE_TYPE_PIPE || (type == FILE_TYPE_UNKNOWN && Marshal.GetLastWin32Error() != 0);
        }

        private static Exception/*!*/ ClosedInAnotherThread() {
            return RubyExceptions.CreateIOError("stream closed in another thread");
        }

        #region Reads

        public override int Read(byte[]/*!*/ buffer, int offset, int count) {
            return Read(new Span<byte>(buffer, offset, count));
        }

        public override int Read(Span<byte> buffer) {
            if (buffer.Length == 0) {
                return 0;
            }
            while (true) {
                IntPtr thread = Enter();
                bool wasBlocked = RubyUtils.EnterNativeWait();
                int read, error;
                bool ok;
                try {
                    unsafe {
                        fixed (byte* p = buffer) {
                            ok = ReadFile(SafeFileHandle, p, buffer.Length, out read, IntPtr.Zero);
                        }
                    }
                    error = ok ? 0 : Marshal.GetLastWin32Error();
                } finally {
                    RubyUtils.ExitNativeWait(wasBlocked);
                    Leave(thread);
                }

                if (ok) {
                    return read;
                }
                if (error == ERROR_BROKEN_PIPE) {
                    // The writer is gone and everything it wrote has been read: end of file.
                    return 0;
                }
                if (error == ERROR_OPERATION_ABORTED) {
                    if (_closed) {
                        throw ClosedInAnotherThread();
                    }
                    // Cancelled by somebody else: let the thread take what was raised into it, and
                    // otherwise read on.
                    RubyUtils.CheckAsyncException();
                    continue;
                }
                throw new IOException("read failed", Marshal.GetHRForLastWin32Error());
            }
        }

        public override int ReadByte() {
            Span<byte> one = stackalloc byte[1];
            return Read(one) == 0 ? -1 : one[0];
        }

        public int ReadNonBlocking(byte[]/*!*/ buffer, int offset, int count) {
            if (_closed) {
                throw ClosedInAnotherThread();
            }
            if (count == 0) {
                return 0;
            }
            int available;
            if (!PeekNamedPipe(SafeFileHandle, IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero)) {
                if (Marshal.GetLastWin32Error() == ERROR_BROKEN_PIPE) {
                    return 0;
                }
                throw new IOException("read failed", Marshal.GetHRForLastWin32Error());
            }
            if (available == 0) {
                return -1;
            }
            // There are bytes in the pipe, so this read returns at once.
            return Read(buffer, offset, Math.Min(count, available));
        }

        #endregion

        #region Writes

        public override void Write(byte[]/*!*/ buffer, int offset, int count) {
            Write(new ReadOnlySpan<byte>(buffer, offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer) {
            while (buffer.Length > 0) {
                int written = WriteFileOnce(buffer, true);
                buffer = buffer.Slice(written);
            }
        }

        public override void WriteByte(byte value) {
            ReadOnlySpan<byte> one = stackalloc byte[] { value };
            Write(one);
        }

        /// <summary>
        /// Whether IO#syswrite should write without waiting: io/nonblock's #nonblock= on a pipe
        /// says so. Windows has no O_NONBLOCK to keep on the handle, so the stream carries it.
        /// </summary>
        public bool NonBlocking { get; set; }

        public int WriteOnce(byte[]/*!*/ buffer, int offset, int count) {
            if (NonBlocking) {
                return WriteNonBlocking(buffer, offset, count);
            }
            Write(buffer, offset, count);
            return count;
        }

        /// <summary>
        /// As many bytes as the pipe has room for, or -1 when it has none - a write(2) on an
        /// O_NONBLOCK pipe.
        ///
        /// Not by switching the handle to PIPE_NOWAIT: that fails with ERROR_PIPE_BUSY as soon as
        /// the pipe holds unread data, a PIPE_NOWAIT write larger than the free space writes
        /// nothing at all, and the mode belongs to the pipe end, which a child shares once it has
        /// been handed the handle. The pipe says how much room it has (WriteQuotaAvailable, as
        /// libuv asks it), and a write of no more than that completes at once.
        /// </summary>
        public int WriteNonBlocking(byte[]/*!*/ buffer, int offset, int count) {
            if (_closed) {
                throw ClosedInAnotherThread();
            }
            if (count == 0) {
                return 0;
            }
            lock (_writeLock) {
                int room = WriteQuotaAvailable();
                if (room == 0) {
                    // Full - or the reader is gone, which the write itself reports as EPIPE.
                    if (!IsReaderGone()) {
                        return -1;
                    }
                    room = count;
                }
                return WriteFileOnce(new ReadOnlySpan<byte>(buffer, offset, Math.Min(count, room)), false);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_STATUS_BLOCK {
            public IntPtr Status;
            public UIntPtr Information;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_PIPE_LOCAL_INFORMATION {
            public uint NamedPipeType;
            public uint NamedPipeConfiguration;
            public uint MaximumInstances;
            public uint CurrentInstances;
            public uint InboundQuota;
            public uint ReadDataAvailable;
            public uint OutboundQuota;
            public uint WriteQuotaAvailable;
            public uint NamedPipeState;
            public uint NamedPipeEnd;
        }

        private const int FilePipeLocalInformation = 24;
        private const uint FILE_PIPE_CLOSING_STATE = 4;

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationFile(SafeFileHandle handle, out IO_STATUS_BLOCK status,
            out FILE_PIPE_LOCAL_INFORMATION info, int length, int informationClass);

        private bool TryQueryPipe(out FILE_PIPE_LOCAL_INFORMATION info) {
            IO_STATUS_BLOCK status;
            return NtQueryInformationFile(SafeFileHandle, out status, out info,
                Marshal.SizeOf(typeof(FILE_PIPE_LOCAL_INFORMATION)), FilePipeLocalInformation) >= 0;
        }

        /// <summary>The room left in the pipe, in bytes; the whole request when the pipe cannot say.</summary>
        private int WriteQuotaAvailable() {
            FILE_PIPE_LOCAL_INFORMATION info;
            if (!TryQueryPipe(out info)) {
                return Int32.MaxValue;
            }
            return (int)Math.Min(info.WriteQuotaAvailable, (uint)Int32.MaxValue);
        }

        private bool IsReaderGone() {
            FILE_PIPE_LOCAL_INFORMATION info;
            return TryQueryPipe(out info) && info.NamedPipeState == FILE_PIPE_CLOSING_STATE;
        }

        private int WriteFileOnce(ReadOnlySpan<byte> buffer, bool mayBlock) {
            while (true) {
                IntPtr thread = mayBlock ? Enter() : IntPtr.Zero;
                bool wasBlocked = mayBlock && RubyUtils.EnterNativeWait();
                int written, error;
                bool ok;
                try {
                    if (!mayBlock && _closed) {
                        throw ClosedInAnotherThread();
                    }
                    unsafe {
                        fixed (byte* p = buffer) {
                            ok = WriteFile(SafeFileHandle, p, buffer.Length, out written, IntPtr.Zero);
                        }
                    }
                    error = ok ? 0 : Marshal.GetLastWin32Error();
                } finally {
                    if (mayBlock) {
                        RubyUtils.ExitNativeWait(wasBlocked);
                        Leave(thread);
                    }
                }

                if (ok) {
                    return written;
                }
                if (error == ERROR_NO_DATA || error == ERROR_BROKEN_PIPE) {
                    // Nobody will read this: EPIPE, which IoOps turns into Errno::EPIPE, or into
                    // SIGPIPE for the standard streams. .NET's own console stream swallows this
                    // error, so a `loop { puts }` child never noticed its reader had gone.
                    throw new IOException("write failed", DescriptorStream.EPIPE);
                }
                if (error == ERROR_OPERATION_ABORTED) {
                    if (_closed) {
                        throw ClosedInAnotherThread();
                    }
                    RubyUtils.CheckAsyncException();
                    continue;
                }
                throw new IOException("write failed (error " + error + ")", unchecked((int)0x80070000) | error);
            }
        }

        public override void Flush() {
            // Nothing is buffered here; WriteFile has handed everything to the pipe already.
        }

        #endregion

        #region Cancellation

        /// <summary>
        /// Registers the calling thread as about to wait on the pipe, so that a close from another
        /// thread can cancel the wait. Throws if the stream is already closed.
        /// </summary>
        private IntPtr Enter() {
            IntPtr thread = OpenThread(THREAD_TERMINATE, false, GetCurrentThreadId());
            lock (_inProgress) {
                if (_closed) {
                    if (thread != IntPtr.Zero) {
                        CloseHandle(thread);
                    }
                    throw ClosedInAnotherThread();
                }
                if (thread != IntPtr.Zero) {
                    _inProgress.Add(thread);
                }
            }
            return thread;
        }

        private void Leave(IntPtr thread) {
            if (thread == IntPtr.Zero) {
                return;
            }
            lock (_inProgress) {
                _inProgress.Remove(thread);
            }
            CloseHandle(thread);
        }

        /// <summary>
        /// Cancels every read and write in progress on this stream. A thread can be between
        /// registering and entering ReadFile when the cancel comes, which then finds nothing to
        /// cancel, so it is repeated until each of them has left.
        /// </summary>
        private void CancelInProgress() {
            for (int attempt = 0; attempt < 2000; attempt++) {
                lock (_inProgress) {
                    if (_inProgress.Count == 0) {
                        return;
                    }
                    foreach (IntPtr thread in _inProgress) {
                        CancelSynchronousIo(thread);
                    }
                }
                Thread.Sleep(1);
            }
        }

        protected override void Dispose(bool disposing) {
            lock (_inProgress) {
                _closed = true;
            }
            CancelInProgress();
            base.Dispose(disposing);
        }

        #endregion
    }
}

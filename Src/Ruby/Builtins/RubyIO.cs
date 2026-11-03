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
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    /// <summary>
    /// IO builtin class. Wraps a BCL Stream object. Implementation of Ruby methods is in IoOps.cs in IronRuby.Libraries assembly.
    /// </summary>
    public partial class RubyIO : IDisposable {
        private RubyContext/*!*/ _context;
        // CRuby's fptr->encs.enc / fptr->encs.enc2. Either may be null, which means
        // "no encoding was pinned onto this stream, follow Encoding.default_external
        // whenever it is asked for". _enc2 is non-null only when a transcoding is in
        // effect, and then _enc2 is the external side and _enc the internal one.
        private RubyEncoding _enc;
        private RubyEncoding _enc2;

        // the :invalid, :undef, :replace and decorator options the stream was opened with,
        // which the transcoding on the way in and on the way out has to honour
        private IDictionary<object, object> _conversionOptions;

        // -1 if uninitialized or closed:
        private int _fileDescriptor;

        // null if uninitialized or closed:
        private RubyBufferedStream _stream;

        private bool _autoFlush;
        private IOMode _mode;
        public int LineNumber { get; set; }

        #region Constants

        public const int SEEK_SET = 0;
        public const int SEEK_CUR = 1;
        public const int SEEK_END = 2;

        public static SeekOrigin ToSeekOrigin(int rubySeekOrigin) {
            switch (rubySeekOrigin) {
                case SEEK_SET: return SeekOrigin.Begin;
                case SEEK_END: return SeekOrigin.End;
                case SEEK_CUR: return SeekOrigin.Current;
                default: throw RubyExceptions.CreateArgumentError("Invalid argument");
            }
        }

        public static long GetSeekPosition(long length, long position, long seekOffset, SeekOrigin origin) {
            switch (origin) {
                case SeekOrigin.Begin: return seekOffset;
                case SeekOrigin.End: return length + seekOffset;
                case SeekOrigin.Current: return position + seekOffset;
            }
            throw Assert.Unreachable;
        }

        #endregion

        #region Construction

        public RubyIO(RubyContext/*!*/ context) {
            ContractUtils.RequiresNotNull(context, "context");

            _context = context;
            _fileDescriptor = -1;
            _stream = null;
        }

        public RubyIO(RubyContext/*!*/ context, Stream/*!*/ stream, IOMode mode) 
            : this(context, stream, context.AllocateFileDescriptor(stream), mode) {
        }

        public RubyIO(RubyContext/*!*/ context, StreamReader reader, StreamWriter writer, IOMode mode)
            : this(context, new DuplexStream(reader, writer), mode) {
        }

        public RubyIO(RubyContext/*!*/ context, Stream/*!*/ stream, int descriptor, IOMode mode) 
            : this(context) {
            ContractUtils.RequiresNotNull(context, "context");
            ContractUtils.RequiresNotNull(stream, "stream");
            SetStream(stream);
            _mode = mode;
            _fileDescriptor = descriptor;
        }

        public void Reset(Stream/*!*/ stream, IOMode mode) {
            _mode = mode;
            SetStream(stream);
            SetFileDescriptor(Context.AllocateFileDescriptor(stream));
        }

        #endregion

        #region Descriptor, Encoding, Flags

        public RubyContext/*!*/ Context {
            get { return _context; }
        }

        /// <summary>
        /// CRuby's fptr->encs.enc: the encoding reads produce. Null when nothing was pinned.
        /// </summary>
        public RubyEncoding Enc {
            get { return _enc; }
        }

        /// <summary>
        /// CRuby's fptr->encs.enc2: the external encoding when a transcoding is in effect,
        /// null otherwise.
        /// </summary>
        public RubyEncoding Enc2 {
            get { return _enc2; }
        }

        /// <summary>
        /// Whether an encoding was actually pinned onto this stream - in the mode string, in
        /// the options hash, or through #set_encoding - as opposed to being left to follow
        /// the context defaults.
        /// </summary>
        public bool EncodingSpecified {
            get { return _enc != null || _enc2 != null; }
        }

        /// <summary>
        /// The external encoding actually in force, for the code that has to turn characters
        /// into bytes. Never null: an unpinned stream follows Encoding.default_external.
        /// </summary>
        public RubyEncoding/*!*/ ExternalEncoding {
            get { return _enc2 ?? _enc ?? _context.DefaultExternalEncoding; }
        }

        /// <summary>
        /// The internal encoding actually in force, i.e. what reads transcode *to*. Null when
        /// there is no transcoding, which is CRuby's answer for #internal_encoding too.
        /// </summary>
        public RubyEncoding InternalEncoding {
            get { return _enc2 != null ? _enc : null; }
        }

        /// <summary>
        /// CRuby's rb_io_ext_int_to_encs (io.c). A null external means "the default"; the
        /// distinction matters, because a stream that merely inherited the default external
        /// encoding keeps following it when Encoding.default_external is reassigned, while
        /// one that was given an encoding explicitly does not.
        /// </summary>
        public void SetEncodings(RubyEncoding external, RubyEncoding @internal) {
            bool defaultExternal = false;
            if (external == null) {
                external = _context.DefaultExternalEncoding;
                defaultExternal = true;
            }

            if (external == RubyEncoding.Binary) {
                // Binary on the outside means the bytes come through untouched.
                @internal = null;
            } else if (@internal == null) {
                @internal = _context.DefaultInternalEncoding;
            }

            if (@internal == null || @internal == external) {
                _enc = (defaultExternal && @internal != external) ? null : external;
                _enc2 = null;
            } else {
                _enc = @internal;
                _enc2 = external;
            }
        }

        /// <summary>
        /// Copies the encoding state verbatim, for #dup and #clone.
        /// </summary>
        public void CopyEncodingsFrom(RubyIO/*!*/ other) {
            _enc = other._enc;
            _enc2 = other._enc2;
        }

        public IDictionary<object, object> ConversionOptions {
            get { return _conversionOptions; }
            set { _conversionOptions = value; }
        }

        public int GetFileDescriptor() {
            RequireOpen();
            return _fileDescriptor;
        }

        public void SetFileDescriptor(int value) {
            ContractUtils.Requires(value >= 0);
            RequireOpen();
            _fileDescriptor = value; 
        }

        /// <summary>
        /// Returns true if the IO object represents stdin/stdout/stderr (no matter whether or not the actual streams are redirected).
        /// </summary>
        public ConsoleStreamType? ConsoleStreamType {
            get {
                // a closed stream is no longer any console's; asking it would raise, and
                // #inspect asks about a closed IO
                if (Closed) {
                    return null;
                }
                // A copy that could not dup(2) the descriptor - #dup of a console stream on
                // Windows - wraps the original's buffered stream in one of its own, so the
                // console may be more than one buffer down.
                Stream stream = GetStream().BaseStream;
                RubyBufferedStream buffered;
                while ((buffered = stream as RubyBufferedStream) != null) {
                    stream = buffered.BaseStream;
                }
                var console = stream as ConsoleStream;
                return console != null ? console.StreamType : (ConsoleStreamType?)null;
            } 
        }

        internal static bool IsConsoleDescriptor(int fileDescriptor) {
            return fileDescriptor >= 0 && fileDescriptor < 3;
        }

        public bool IsConsoleDescriptor() {
            return IsConsoleDescriptor(_fileDescriptor);
        }

        public bool Closed {
            get { return _mode.IsClosed(); }
        }

        public bool Initialized {
            get { return Closed || _stream != null; }
        }

        // On Unix MRI performs no end-of-line translation, in text mode or otherwise:
        private static readonly bool _IsWindowsPlatform = System.IO.Path.DirectorySeparatorChar == '\\';

        /// <summary>
        /// True when the stream was opened with the "b" flag.  Distinct from
        /// PreserveEndOfLines, which is vacuously true off Windows.
        /// </summary>
        public bool IsBinmode {
            get { return (_mode & IOMode.PreserveEndOfLines) != 0; }
        }

        public bool PreserveEndOfLines {
            get { 
                return !_IsWindowsPlatform || (_mode & IOMode.PreserveEndOfLines) != 0; 
            }
            set {
                if (value) {
                    _mode |= IOMode.PreserveEndOfLines;
                } else {
                    _mode &= ~IOMode.PreserveEndOfLines;
                }
            }
        }

        public bool AutoFlush {
            get { return _autoFlush; }
            set { _autoFlush = value; }
        }

        #endregion

        #region Basic Stream Operations

        public RubyBufferedStream/*!*/ GetStream() {
            if (Closed) {
                throw RubyExceptions.CreateIOError("closed stream");
            }

            RequireInitialized();
            return _stream;
        }

        public void SetStream(Stream/*!*/ stream) {
            ContractUtils.RequiresNotNull(stream, "stream");
            _stream = new RubyBufferedStream(stream, _context.RubyOptions.Compatibility >= RubyCompatibility.Ruby19);
        }

        public void RequireInitialized() {
            if (!Closed && _stream == null) {
                throw RubyExceptions.CreateIOError("uninitialized stream");
            }
        }

        public void RequireOpen() {
            GetStream();
        }

        public void RequireWritable() {
            GetWritableStream();
        }

        public void RequireReadable() {
            GetReadableStream();
        }

        public RubyBufferedStream/*!*/ GetWritableStream() {
            var result = GetStream();
            if (!_mode.CanWrite()) {
                throw RubyExceptions.CreateIOError("not opened for writing");
            }
            if (!result.CanWrite) {
                throw RubyExceptions.CreateEBADF();
            }
            return result;
        }

        public RubyBufferedStream/*!*/ GetReadableStream() {
            var result = GetStream();
            if (!_mode.CanRead()) {
                throw RubyExceptions.CreateIOError("not opened for reading");
            }
            if (!result.CanRead) {
                throw RubyExceptions.CreateEBADF();
            }
            return result;
        }

        public long Position {
            get {
                var stream = GetStream();
                long position;
                if (TrySeekDescriptor(stream, 0, SeekOrigin.Current, out position)) {
                    return position;
                }
                try {
                    return stream.Position;
                } catch (ObjectDisposedException) {
                    throw RubyExceptions.CreateEBADF();
                }
            }
            set {
                var stream = GetStream();
                long position;
                if (TrySeekDescriptor(stream, value, SeekOrigin.Begin, out position)) {
                    return;
                }
                try {
                    stream.Position = value;
                } catch (ObjectDisposedException) {
                    throw RubyExceptions.CreateEBADF();
                }
            }
        }

        public void Seek(long offset, SeekOrigin origin) {
            var stream = GetStream();
            long position;
            if (TrySeekDescriptor(stream, offset, origin, out position)) {
                return;
            }
            try {
                stream.Seek(offset, origin);
            } catch (IOException) {
                throw CreateSeekError();
            } catch (ObjectDisposedException) {
                throw RubyExceptions.CreateEBADF();
            }
        }

        // The whence of a seek, or MRI's EINVAL (naming the file) for anything else.
        public SeekOrigin CheckSeekOrigin(int whence) {
            switch (whence) {
                case SEEK_SET: return SeekOrigin.Begin;
                case SEEK_END: return SeekOrigin.End;
                case SEEK_CUR: return SeekOrigin.Current;
                default: throw CreateSeekError();
            }
        }

        // MRI names the file in a failed seek ("Invalid argument @ rb_io_seek - path");
        // a stream without a path (a pipe, a socket) gets the bare message.
        private Exception/*!*/ CreateSeekError() {
            var file = this as RubyFile;
            if (file != null && file.Path != null) {
                return RubyExceptions.CreateEINVAL("Invalid argument - {0}", file.Path);
            }
            return RubyExceptions.CreateEINVAL();
        }

        [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
        private static extern long sys_lseek(int fd, long offset, int whence);

        /// <summary>
        /// lseek(2) for a stream that cannot seek itself but sits on a descriptor that can - the
        /// standard streams after #reopen, above all. `$stdout.reopen(tempfile)` points descriptor
        /// 1 at the file with dup2, but Ruby still writes through the console stream it had, and
        /// that stream answers every Seek with NotSupportedException. minitest's
        /// capture_subprocess_io reopens $stdout onto a Tempfile and then rewinds it, so it failed
        /// with a raw System::NotSupportedException. A descriptor that really cannot seek (a pipe,
        /// a terminal) gets MRI's Errno::ESPIPE.
        /// </summary>
        private bool TrySeekDescriptor(RubyBufferedStream/*!*/ stream, long offset, SeekOrigin origin, out long position) {
            position = 0;
            if (!_hasFileControl || stream.CanSeek) {
                return false;
            }
            int descriptor = KernelDescriptor;
            if (descriptor < 0) {
                return false;
            }

            stream.Flush();
            position = sys_lseek(descriptor, offset, (int)origin);
            if (position < 0) {
                const int ESPIPE = 29;
                if (Marshal.GetLastWin32Error() == ESPIPE) {
                    object errno;
                    if (_context.ObjectClass.TryGetConstant(null, "Errno", out errno) && errno is RubyModule) {
                        var error = _context.CreateLibraryException((RubyModule)errno, "ESPIPE", null);
                        if (error != null) {
                            throw error;
                        }
                    }
                }
                throw CreateSeekError();
            }
            return true;
        }

        public void Flush() {
            var stream = GetStream();
            try {
                stream.Flush();
            } catch (ObjectDisposedException) {
                throw RubyExceptions.CreateEBADF();
            }
        }

        public long Length {
            get {
                var stream = GetStream();
                try {
                    return stream.Length;
                } catch (ObjectDisposedException) {
                    throw RubyExceptions.CreateEBADF();
                }
            }

            set {
                var stream = GetStream();
                try {
                    stream.SetLength(value);
                } catch (ObjectDisposedException) {
                    throw RubyExceptions.CreateIOError("closed stream");
                } catch (NotSupportedException) {
                    throw RubyExceptions.CreateIOError("not opened for writing");
                }
            }
        }

        public int WriteBytes(byte[]/*!*/ buffer, int index, int count) {
            ContractUtils.RequiresNotNull(buffer, "buffer");
            return WriteBytes(buffer, null, index, count);
        }

        public int WriteBytes(MutableString/*!*/ buffer, int index, int count) {
            ContractUtils.RequiresNotNull(buffer, "buffer");
            return WriteBytes(null, buffer, index, count);
        }

        // TODO: transcoding
        private int WriteBytes(byte[] bytes, MutableString str, int index, int count) {
            var stream = GetWritableStream();

            if ((_mode & IOMode.WriteAppends) != 0 && stream.CanSeek) {
                stream.Seek(0, SeekOrigin.End);
            }

            try {
                if (bytes != null) {
                    return stream.WriteBytes(bytes, index, count, PreserveEndOfLines);
                } else {
                    return stream.WriteBytes(str, index, count, PreserveEndOfLines);
                }
            } catch (ObjectDisposedException) {
                throw RubyExceptions.CreateEBADF();
            }
        }

        public void Dispose() {
            Close();
        }

        public void Close() {
            int fd = _fileDescriptor;
            _mode = _mode.Close();
            _fileDescriptor = -1;

            if (_stream != null) {
                _stream = null;
                _context.CloseStream(fd);
            }
        }

        /// <summary>
        /// Closes this object and leaves the descriptor open, which is IO#close with #autoclose
        /// false. What was written is flushed, as MRI does.
        /// </summary>
        public void CloseKeepingDescriptor() {
            if (_stream != null) {
                try {
                    _stream.Flush();
                } catch (ObjectDisposedException) {
                }
            }
            _mode = _mode.Close();
            _fileDescriptor = -1;
            _stream = null;
        }

        /// <summary>
        /// Transfers this IO's stream to a composite owner without closing or flushing it.
        /// The descriptor table entry is removed by the caller before this is used.
        /// </summary>
        public void DetachStream() {
            _mode = _mode.Close();
            _fileDescriptor = -1;
            _stream = null;
        }

        public void CloseWriter() {
            var duplex = GetStream().BaseStream as DuplexStream;

            // closing the side of a duplex stream that is already closed closes the rest (as MRI does)
            if (duplex != null && !_mode.CanWrite()) {
                Close();
                return;
            }
            if (duplex == null && _mode.CanRead()) {
                throw RubyExceptions.CreateIOError("closing non-duplex IO for writing");
            }
            
            if (duplex != null) {
                duplex.Writer.Dispose();
            }

            _mode = _mode.CloseWrite();
            if (_mode.IsClosed()) {
                Close();
            }
        }

        public void CloseReader() {
            var duplex = GetStream().BaseStream as DuplexStream;
            if (duplex != null && !_mode.CanRead()) {
                Close();
                return;
            }
            if (duplex == null && _mode.CanWrite()) {
                throw RubyExceptions.CreateIOError("closing non-duplex IO for reading");
            } 
            
            if (duplex != null) {
                duplex.Reader.Dispose();
            }

            _mode = _mode.CloseRead();
            if (_mode.IsClosed()) {
                Close();
            }
        }

        public IOMode Mode {
            get { return _mode; }
            set { _mode = value; }
        }

        #endregion

        #region Operations

        public virtual WaitHandle/*!*/ CreateReadWaitHandle() {
            // TODO:
            throw new NotSupportedException();
        }

        public virtual WaitHandle/*!*/ CreateWriteWaitHandle() {
            // TODO:
            throw new NotSupportedException();
        }

        public virtual WaitHandle/*!*/ CreateErrorWaitHandle() {
            // TODO:
            throw new NotSupportedException();
        }

        public virtual int SetReadTimeout(int timeout) {
            if (timeout > 0) {
                throw RubyExceptions.CreateEBADF();
            }
            return 0;
        }

        /// <summary>
        /// Runs a read or a write in the non-blocking mode #read_nonblock / #write_nonblock ask
        /// for. The default is to just run it: a regular file never blocks, which is also why
        /// O_NONBLOCK has no effect on one in MRI. A pipe or a socket overrides this, or the
        /// caller reaches for the single-shot syscall on the descriptor directly.
        /// </summary>
        public virtual void NonBlockingOperation(Action operation, bool isRead) {
            RequireOpen();
            operation();
        }

        /// <summary>
        /// The operating system's descriptor for this stream, or -1 when there is not one.
        /// IronRuby's "file descriptor" is an index into a per-context table, so anything that
        /// calls a syscall has to dig the real handle out of the underlying FileStream - passing
        /// #fileno to fcntl or poll asks about an unrelated descriptor.
        /// </summary>
        public int NativeDescriptor {
            get {
                if (!_hasFileControl) {
                    return -1;
                }
                var buffered = _stream;
                if (buffered == null) {
                    return -1;
                }
                return DescriptorOf(buffered.BaseStream);
            }
        }

        /// <summary>
        /// The descriptor the kernel knows this IO by. The standard streams are the three it
        /// reserves whatever IronRuby's table says, because the table index and the descriptor
        /// agree there by construction and the stream behind them is a console stream rather
        /// than a FileStream.
        /// </summary>
        public int KernelDescriptor {
            get {
                int descriptor = _fileDescriptor;
                if (_hasFileControl && descriptor >= 0 && descriptor <= 2) {
                    return descriptor;
                }
                return NativeDescriptor;
            }
        }

        [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
        private static extern int sys_dup(int fd);

        [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
        private static extern int sys_dup2(int fd, int newFd);

        /// <summary>
        /// dup2(2) of another IO's descriptor onto this one's, which is what Ruby's #reopen
        /// is. Returns false when either side has no descriptor the kernel knows about, and
        /// the caller then falls back to swapping streams in IronRuby's table.
        /// </summary>
        public static bool TryRedirectDescriptor(RubyIO/*!*/ io, RubyIO/*!*/ source, out System.IO.Stream rebuilt) {
            rebuilt = null;

            // No dup2 to call off Unix; the caller swaps streams in IronRuby's own table
            // instead, which is what it does for any IO without a kernel descriptor.
            if (!_hasFileControl) {
                return false;
            }

            int target = io.KernelDescriptor;
            int from = source.KernelDescriptor;
            if (target < 0 || from < 0) {
                return false;
            }
            io.Flush();
            source.Flush();

            // dup2 makes the two descriptors share one file offset, and the source may have read
            // further than Ruby has seen - a buffered read pulls in a block at a time. Put the
            // descriptor back where the reading actually got to before handing it over.
            DiscardReadAhead(source);

            // Asking a FileStream for its handle seeks the descriptor to where that stream
            // thinks it is, so it has to happen before the descriptor starts naming the other
            // file - afterwards it would undo the offset the two are meant to share.
            var replaced = (target > 2) ? io.GetStream().BaseStream as System.IO.FileStream : null;
            var replacedHandle = (replaced != null) ? replaced.SafeFileHandle : null;

            if (target != from && sys_dup2(from, target) < 0) {
                return false;
            }

            // The descriptor now names the other IO's file. The stream that was reading the old
            // one has to go with it: it was opened for a different access mode, and it buffers
            // bytes of a file this IO no longer has. The descriptor is taken away from it first,
            // so that finalizing it does not close the descriptor out from under the new stream.
            if (replacedHandle != null) {
                rebuilt = TryAdoptDescriptor(target, true);
                if (rebuilt != null) {
                    replacedHandle.SetHandleAsInvalid();
                }
            }

            return true;
        }

        private static void DiscardReadAhead(RubyIO/*!*/ io) {
            try {
                var stream = io.GetStream();
                if (!stream.CanSeek) {
                    return;
                }
                stream.Seek(stream.Position, System.IO.SeekOrigin.Begin);

                // A FileStream buffers too, and seeking inside its own buffer leaves the
                // descriptor where the last block read left it. Asking for the handle is what
                // makes it put the descriptor where the stream says it is.
                var file = stream.BaseStream as System.IO.FileStream;
                if (file != null) {
                    var handle = file.SafeFileHandle;
                    GC.KeepAlive(handle);
                }
            } catch (System.Exception) {
                // a stream that cannot say where it is has nothing to put back
            }
        }

        /// <summary>dup(2), so that a copy of an IO survives its original being reopened.</summary>
        public static int TryDuplicateDescriptor(RubyIO/*!*/ io) {
            int descriptor = io.KernelDescriptor;
            return (descriptor < 0 || !_hasFileControl) ? -1 : sys_dup(descriptor);
        }

        /// <summary>The operating system descriptor a stream reads and writes, or -1.</summary>
        public static int DescriptorOf(System.IO.Stream stream) {
            var known = stream as IDescriptorStream;
            if (known != null) {
                return known.Descriptor;
            }
            var file = stream as System.IO.FileStream;
            return (file != null) ? (int)file.SafeFileHandle.DangerousGetHandle() : -1;
        }

        /// <summary>
        /// The descriptor MRI's IO would hold for a stream. IO.popen(cmd, "r+") reads and writes
        /// two pipes, and MRI's IO is the reading one (the writing one is a second IO tied to it).
        /// </summary>
        public static int PrimaryDescriptorOf(System.IO.Stream stream) {
            var duplex = stream as DuplexStream;
            if (duplex != null) {
                stream = (duplex.Reader != null) ? duplex.Reader.BaseStream : duplex.Writer.BaseStream;
            }
            return DescriptorOf(stream);
        }

        /// <summary>
        /// Whether the descriptor is open, and how. The low two bits of IOMode are O_ACCMODE
        /// by construction. Ruby's default mode of "r" is an answer about a path; a descriptor
        /// already knows what it was opened for, which is what MRI reports for one handed to
        /// IO.new without a mode.
        /// </summary>
        public static bool TryGetDescriptorMode(int descriptor, out IOMode mode) {
            mode = IOMode.ReadOnly;
            if (!_hasFileControl || descriptor < 0) {
                return false;
            }
            int flags = sys_fcntl(descriptor, F_GETFL, 0);
            if (flags < 0) {
                return false;
            }
            mode = (IOMode)(flags & (int)IOMode.ReadWriteMask);
            return true;
        }

        /// <summary>
        /// A stream over a descriptor this process was handed rather than opened - the way a
        /// child of Process.spawn receives one through a redirection. IronRuby's descriptor
        /// table knows nothing about it, so IO.new(fd) could only ever answer EBADF for it.
        /// </summary>
        public static System.IO.Stream TryAdoptDescriptor(int descriptor) {
            return TryAdoptDescriptor(descriptor, false);
        }

        public static System.IO.Stream TryAdoptDescriptor(int descriptor, bool ownsDescriptor) {
            IOMode mode;
            if (!TryGetDescriptorMode(descriptor, out mode)) {
                return null;
            }
            System.IO.FileAccess access;
            switch (mode) {
                case IOMode.WriteOnly: access = System.IO.FileAccess.Write; break;
                case IOMode.ReadWrite: access = System.IO.FileAccess.ReadWrite; break;
                default: access = System.IO.FileAccess.Read; break;
            }
            try {
                // ownsDescriptor is normally false - the descriptor belongs to whoever passed
                // it in, and a finalizer closing, say, the inherited standard output would be a
                // disaster. Only #reopen, which has just taken a descriptor away from the stream
                // that owned it, asks for the new stream to own it instead.
                return new System.IO.FileStream(
                    new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)descriptor, ownsDescriptor), access, 1, false
                );
            } catch (Exception) {
                return null;
            }
        }

        /// <summary>The native descriptor of an IO, for Ruby code that needs one.</summary>
        public static int GetNativeDescriptor(RubyIO/*!*/ io) {
            return io.NativeDescriptor;
        }

        #region poll

        // struct pollfd { int fd; short events; short revents; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd {
            public int fd;
            public short events;
            public short revents;
        }

        public const short POLLIN = 0x001;
        public const short POLLOUT = 0x004;
        public const short POLLERR = 0x008;
        public const short POLLHUP = 0x010;
        public const short POLLNVAL = 0x020;

        [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
        private static extern int sys_poll([In, Out] PollFd[] fds, uint nfds, int timeout);

        /// <summary>
        /// poll(2) over the first <paramref name="count"/> descriptors, filling in their revents.
        /// Answers what poll answers - the number of descriptors with events, 0 on timeout - or -1
        /// when it failed or the platform has none (check HasPoll). EINTR reads as a timeout.
        /// </summary>
        public static int Poll(int[]/*!*/ descriptors, short[]/*!*/ events, short[]/*!*/ revents, int count, int timeoutMilliseconds) {
            if (!_hasFileControl) {
                return -1;
            }

            var fds = new PollFd[count];
            for (int i = 0; i < count; i++) {
                fds[i].fd = descriptors[i];
                fds[i].events = events[i];
            }

            int result = sys_poll(fds, (uint)count, timeoutMilliseconds);
            if (result < 0) {
                return Marshal.GetLastWin32Error() == 4 ? 0 : -1;
            }
            for (int i = 0; i < count; i++) {
                revents[i] = fds[i].revents;
            }
            return result;
        }

        /// <summary>Whether this platform has poll(2) - every Unix, and not Windows.</summary>
        public static bool HasPoll {
            get { return _hasFileControl; }
        }

        /// <summary>
        /// Blocks in one poll(2) over the given descriptors until one of them has an event, or
        /// the timeout passes. Answers false when the platform has no poll. The revents are not
        /// reported: the callers (IO.select and friends) ask each IO again afterwards, which is
        /// what decides readiness; this is only how they sleep.
        /// </summary>
        public static bool PollWait(System.Collections.Generic.IList<int>/*!*/ descriptors,
            System.Collections.Generic.IList<short>/*!*/ events, int timeoutMilliseconds) {
            if (!_hasFileControl) {
                return false;
            }

            var fds = new PollFd[descriptors.Count];
            for (int i = 0; i < fds.Length; i++) {
                fds[i].fd = descriptors[i];
                fds[i].events = events[i];
            }

            // An EINTR just ends the wait early, which the callers are ready for.
            return sys_poll(fds, (uint)fds.Length, timeoutMilliseconds) >= 0 || Marshal.GetLastWin32Error() == 4;
        }

        /// <summary>
        /// poll(2) over the given descriptors. Answers a parallel array of revents, or null when
        /// the platform has no poll. timeoutMilliseconds of -1 waits indefinitely, 0 returns at
        /// once. This is what makes IO.select a real answer rather than a guess: asking the
        /// kernel is the only way to know whether a descriptor has something waiting on it.
        /// </summary>
        // Takes plain lists so Ruby can call it with Ruby Arrays.
        public static short[] Poll(System.Collections.IList/*!*/ descriptors, System.Collections.IList/*!*/ events, int timeoutMilliseconds) {
            if (!_hasFileControl || descriptors.Count == 0) {
                return null;
            }

            var fds = new PollFd[descriptors.Count];
            for (int i = 0; i < descriptors.Count; i++) {
                fds[i].fd = Convert.ToInt32(descriptors[i]);
                fds[i].events = Convert.ToInt16(events[i]);
            }

            int result;
            do {
                result = sys_poll(fds, (uint)fds.Length, timeoutMilliseconds);
            } while (result < 0 && Marshal.GetLastWin32Error() == 4); // EINTR

            if (result < 0) {
                return null;
            }

            var revents = new short[descriptors.Count];
            for (int i = 0; i < fds.Length; i++) {
                revents[i] = fds[i].revents;
            }
            return revents;
        }

        #endregion

        #region fcntl

        // Linux values. fcntl is variadic; for F_GETFL and F_SETFL the third argument is an int.
        public const int F_GETFL = 3;
        public const int F_SETFL = 4;
        public const int O_NONBLOCK = 0x800;

        private static readonly bool _hasFileControl = System.IO.Path.DirectorySeparatorChar == '/';

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int sys_fcntl(int fd, int cmd, int arg);

        /// <summary>
        /// fcntl(2) on the underlying descriptor. Only the descriptor flags can be asked for or
        /// set this way, which is what io/nonblock needs; a platform without fcntl still gets
        /// the NotSupportedException this used to throw unconditionally.
        /// </summary>
        public virtual int FileControl(int commandId, int arg) {
            GetStream();

            if (!_hasFileControl) {
                throw new NotSupportedException();
            }

            int fd = NativeDescriptor;
            if (fd < 0) {
                throw new NotSupportedException();
            }
            int result = NativeFileControl(fd, commandId, arg);
            // The descriptor's own O_APPEND is the answer wherever the kernel has one, but a
            // stream IronRuby opened in append mode without telling the kernel (the CLR
            // positions an appending FileStream itself) still reports the flag it holds.
            if (commandId == F_GETFL && (_mode & IOMode.WriteAppends) != 0) {
                result |= IOModeNative.Append;
            }
            return result;
        }

        /// <summary>
        /// fcntl(2) on a raw descriptor, for streams (sockets) whose descriptor is not a file's.
        /// </summary>
        protected static int NativeFileControl(int fd, int commandId, int arg) {
            if (!_hasFileControl || fd < 0) {
                throw new NotSupportedException();
            }
            int result = sys_fcntl(fd, commandId, arg);
            if (result < 0) {
                throw RubyExceptions.CreateEBADF();
            }
            return result;
        }

        #endregion

        public virtual int FileControl(int commandId, byte[] arg) {
            GetStream();

            // TODO:
            throw new NotSupportedException();
        }

        public BinaryReader/*!*/ GetBinaryReader() {
            return new BinaryReader(GetReadableStream());
        }

        public BinaryWriter/*!*/ GetBinaryWriter() {
            return new BinaryWriter(GetWritableStream());
        }

        public bool IsEndOfStream() {
            return GetReadableStream().PeekByte() == -1;
        }

        // returns the number of bytes written to the stream:
        public int WriteBytes(char[]/*!*/ buffer, int index, int count) {
            byte[] bytes = ExternalEncoding.StrictEncoding.GetBytes(buffer, index, count);
            return WriteBytes(bytes, 0, bytes.Length);
        }

        // returns the number of bytes written to the stream:
        public int WriteBytes(string/*!*/ value) {
            byte[] bytes = ExternalEncoding.StrictEncoding.GetBytes(value);
            return WriteBytes(bytes, 0, bytes.Length);
        }

        public int AppendBytes(MutableString/*!*/ buffer, int count) {
            var stream = GetReadableStream();
            try {
                return stream.AppendBytes(buffer, count, PreserveEndOfLines);
            } catch (ObjectDisposedException) {
                throw RubyExceptions.CreateEBADF();
            }
        }

        /// <summary>
        /// One IO#readpartial worth of bytes: what is buffered, or a single read when nothing is.
        /// </summary>
        public int AppendAvailableBytes(MutableString/*!*/ buffer, int count) {
            var stream = GetReadableStream();
            try {
                return stream.AppendAvailableBytes(buffer, count);
            } catch (ObjectDisposedException) {
                throw RubyExceptions.CreateEBADF();
            }
        }

        public MutableString ReadLineOrParagraph(MutableString separator, int limit) {
            if (limit == 0) {
                // A limit of no bytes reads no bytes: #gets answers the empty string, at the end
                // of the stream as anywhere else. It is the readers that loop - #each_line and
                // #readlines - that refuse the limit, since they would never come back.
                return MutableString.CreateEmpty(ExternalEncoding);
            }
            var stream = GetReadableStream();
            try {
                return stream.ReadLineOrParagraph(separator, ExternalEncoding, PreserveEndOfLines, limit >= 0 ? limit : Int32.MaxValue);
            } catch (ObjectDisposedException) {
                throw RubyExceptions.CreateEBADF();
            }
        }

        public int ReadByteNormalizeEoln() {
            var stream = GetReadableStream();
            try {
                return stream.ReadByteNormalizeEoln(PreserveEndOfLines);
            } catch (ObjectDisposedException) {
                throw RubyExceptions.CreateEBADF();
            }
        }

        public int PeekByteNormalizeEoln() {
            var stream = GetReadableStream();
            try {
                return stream.PeekByteNormalizeEoln(PreserveEndOfLines);
            } catch (ObjectDisposedException) {
                throw RubyExceptions.CreateEBADF();
            }
        }

        public void PushBack(byte b) {
            GetStream().PushBack(b);            
        }

        #endregion

        public override string/*!*/ ToString() {
            return RubyUtils.ObjectToMutableString(_context, this).ToString();
        }
    }
}

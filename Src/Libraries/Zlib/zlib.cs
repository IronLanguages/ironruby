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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Zlib {

    /// <summary>
    /// The parts of libz this library is built on.
    ///
    /// Compressed output has to be byte-for-byte what CRuby produces - the specs compare exact
    /// bytes - so the compressor itself is the system's, not a reimplementation.
    ///
    /// Windows has no libz; there the same entry points run on the zlib inside .NET itself
    /// (<see cref="ClrZlib"/>), and the z_stream record below is only ever read by managed code.
    /// </summary>
    internal static class LibZ {
        private const string Lib = "libz";

        internal static readonly bool UseClrZlib = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        internal const int Z_NO_FLUSH = 0;
        internal const int Z_SYNC_FLUSH = 2;
        internal const int Z_FINISH = 4;

        internal const int Z_OK = 0;
        internal const int Z_STREAM_END = 1;
        internal const int Z_NEED_DICT = 2;
        internal const int Z_ERRNO = -1;
        internal const int Z_STREAM_ERROR = -2;
        internal const int Z_DATA_ERROR = -3;
        internal const int Z_MEM_ERROR = -4;
        internal const int Z_BUF_ERROR = -5;
        internal const int Z_VERSION_ERROR = -6;

        internal const int Z_DEFLATED = 8;

        /// <summary>
        /// z_stream, laid out the way libz declares it. The uLong fields are C's `unsigned long`,
        /// which is pointer-sized on the LP64 and ILP32 platforms this runs on.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ZStreamRec {
            public IntPtr next_in;
            public uint avail_in;
            public UIntPtr total_in;
            public IntPtr next_out;
            public uint avail_out;
            public UIntPtr total_out;
            public IntPtr msg;
            public IntPtr state;
            public IntPtr zalloc;
            public IntPtr zfree;
            public IntPtr opaque;
            public int data_type;
            public UIntPtr adler;
            public UIntPtr reserved;
        }

        [DllImport(Lib, EntryPoint = "zlibVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sys_zlibVersion();

        private static string _version;

        /// <summary>
        /// The version libz reports at run time, which is also what has to be handed to the
        /// init functions - they refuse to initialize a stream against a mismatched header.
        /// </summary>
        internal static string Version {
            get {
                if (_version == null) {
                    _version = UseClrZlib ? ClrZlib.Version : (Marshal.PtrToStringAnsi(sys_zlibVersion()) ?? "1.2.11");
                }
                return _version;
            }
        }

        internal static readonly int StreamSize = Marshal.SizeOf(typeof(ZStreamRec));

        internal static int deflateInit2_(IntPtr strm, int level, int method, int windowBits,
            int memLevel, int strategy, string/*!*/ version, int stream_size) {
            return UseClrZlib
                ? ClrZlib.DeflateInit(strm, level, method, windowBits, memLevel, strategy)
                : sys_deflateInit2_(strm, level, method, windowBits, memLevel, strategy, version, stream_size);
        }

        internal static int deflate(IntPtr strm, int flush) {
            return UseClrZlib ? ClrZlib.Run(strm, flush) : sys_deflate(strm, flush);
        }

        internal static int deflateEnd(IntPtr strm) {
            return UseClrZlib ? ClrZlib.End(strm) : sys_deflateEnd(strm);
        }

        internal static int deflateReset(IntPtr strm) {
            return UseClrZlib ? ClrZlib.Reset(strm) : sys_deflateReset(strm);
        }

        internal static int deflateParams(IntPtr strm, int level, int strategy) {
            return UseClrZlib ? ClrZlib.Params(strm, level, strategy) : sys_deflateParams(strm, level, strategy);
        }

        internal static int deflateSetDictionary(IntPtr strm, byte[]/*!*/ dictionary, uint dictLength) {
            if (UseClrZlib) {
                throw ClrZlib.Unavailable("Zlib::Deflate#set_dictionary", "deflateSetDictionary");
            }
            return sys_deflateSetDictionary(strm, dictionary, dictLength);
        }

        internal static int inflateInit2_(IntPtr strm, int windowBits, string/*!*/ version, int stream_size) {
            return UseClrZlib ? ClrZlib.InflateInit(strm, windowBits) : sys_inflateInit2_(strm, windowBits, version, stream_size);
        }

        internal static int inflate(IntPtr strm, int flush) {
            return UseClrZlib ? ClrZlib.Run(strm, flush) : sys_inflate(strm, flush);
        }

        internal static int inflateEnd(IntPtr strm) {
            return UseClrZlib ? ClrZlib.End(strm) : sys_inflateEnd(strm);
        }

        internal static int inflateReset(IntPtr strm) {
            return UseClrZlib ? ClrZlib.Reset(strm) : sys_inflateReset(strm);
        }

        internal static int inflateSetDictionary(IntPtr strm, byte[]/*!*/ dictionary, uint dictLength) {
            if (UseClrZlib) {
                throw ClrZlib.Unavailable("Zlib::Inflate#set_dictionary", "inflateSetDictionary");
            }
            return sys_inflateSetDictionary(strm, dictionary, dictLength);
        }

        internal static int inflateSyncPoint(IntPtr strm) {
            if (UseClrZlib) {
                throw ClrZlib.Unavailable("Zlib::Inflate#sync_point?", "inflateSyncPoint");
            }
            return sys_inflateSyncPoint(strm);
        }

        internal static UIntPtr adler32(UIntPtr adler, byte[] buf, uint len) {
            return UseClrZlib ? (UIntPtr)Checksums.Adler32((uint)adler, buf, 0, (int)len) : sys_adler32(adler, buf, len);
        }

        internal static UIntPtr crc32(UIntPtr crc, byte[] buf, uint len) {
            return UseClrZlib ? (UIntPtr)Checksums.Crc32((uint)crc, buf, 0, (int)len) : sys_crc32(crc, buf, len);
        }

        internal static UIntPtr adler32_combine(UIntPtr adler1, UIntPtr adler2, long len2) {
            return UseClrZlib ? (UIntPtr)Checksums.Adler32Combine((uint)adler1, (uint)adler2, len2) : sys_adler32_combine(adler1, adler2, len2);
        }

        internal static UIntPtr crc32_combine(UIntPtr crc1, UIntPtr crc2, long len2) {
            return UseClrZlib ? (UIntPtr)Checksums.Crc32Combine((uint)crc1, (uint)crc2, len2) : sys_crc32_combine(crc1, crc2, len2);
        }

        /// <summary>The 256 entries of zlib's CRC-32 table.</summary>
        internal static uint[]/*!*/ GetCrcTable() {
            if (UseClrZlib) {
                return (uint[])Checksums.CrcTable.Clone();
            }
            // get_crc_table() hands back 256 z_crc_t (uint32 since zlib 1.2.7) entries.
            var entries = new int[256];
            Marshal.Copy(sys_get_crc_table(), entries, 0, 256);
            var result = new uint[256];
            for (int i = 0; i < entries.Length; i++) {
                result[i] = unchecked((uint)entries[i]);
            }
            return result;
        }

        [DllImport(Lib, EntryPoint = "deflateInit2_", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sys_deflateInit2_(IntPtr strm, int level, int method, int windowBits,
            int memLevel, int strategy, string/*!*/ version, int stream_size);

        [DllImport(Lib, EntryPoint = "deflate", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_deflate(IntPtr strm, int flush);

        [DllImport(Lib, EntryPoint = "deflateEnd", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_deflateEnd(IntPtr strm);

        [DllImport(Lib, EntryPoint = "deflateReset", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_deflateReset(IntPtr strm);

        [DllImport(Lib, EntryPoint = "deflateParams", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_deflateParams(IntPtr strm, int level, int strategy);

        [DllImport(Lib, EntryPoint = "deflateSetDictionary", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_deflateSetDictionary(IntPtr strm, byte[]/*!*/ dictionary, uint dictLength);

        [DllImport(Lib, EntryPoint = "inflateInit2_", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sys_inflateInit2_(IntPtr strm, int windowBits, string/*!*/ version, int stream_size);

        [DllImport(Lib, EntryPoint = "inflate", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_inflate(IntPtr strm, int flush);

        [DllImport(Lib, EntryPoint = "inflateEnd", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_inflateEnd(IntPtr strm);

        [DllImport(Lib, EntryPoint = "inflateReset", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_inflateReset(IntPtr strm);

        [DllImport(Lib, EntryPoint = "inflateSetDictionary", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_inflateSetDictionary(IntPtr strm, byte[]/*!*/ dictionary, uint dictLength);

        [DllImport(Lib, EntryPoint = "inflateSyncPoint", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_inflateSyncPoint(IntPtr strm);

        [DllImport(Lib, EntryPoint = "adler32", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr sys_adler32(UIntPtr adler, byte[] buf, uint len);

        [DllImport(Lib, EntryPoint = "crc32", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr sys_crc32(UIntPtr crc, byte[] buf, uint len);

        [DllImport(Lib, EntryPoint = "adler32_combine", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr sys_adler32_combine(UIntPtr adler1, UIntPtr adler2, long len2);

        [DllImport(Lib, EntryPoint = "crc32_combine", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr sys_crc32_combine(UIntPtr crc1, UIntPtr crc2, long len2);

        [DllImport(Lib, EntryPoint = "get_crc_table", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sys_get_crc_table();
    }

    [RubyModule("Zlib")]
    public static class Zlib {

        #region Constants

        [RubyConstant("NO_FLUSH")]
        public const int NO_FLUSH = 0;

        [RubyConstant("SYNC_FLUSH")]
        public const int SYNC_FLUSH = 2;

        [RubyConstant("FULL_FLUSH")]
        public const int FULL_FLUSH = 3;

        [RubyConstant("FINISH")]
        public const int FINISH = 4;

        [RubyConstant("ZLIB_VERSION")]
        public static string ZLIB_VERSION = LibZ.Version;

        // The version of the `zlib` gem whose API this implements, not libz's own version.
        [RubyConstant("VERSION")]
        public static string VERSION = "3.2.3";

        [RubyConstant("MAXBITS")]
        public const int MAXBITS = 15;

        [RubyConstant("MAXLCODES")]
        public const int MAXLCODES = 286;

        [RubyConstant("MAXDCODES")]
        public const int MAXDCODES = 30;

        [RubyConstant("MAXCODES")]
        public const int MAXCODES = (MAXLCODES + MAXDCODES);

        [RubyConstant("FIXLCODES")]
        public const int FIXLCODES = 288;

        [RubyConstant("MAX_WBITS")]
        public const int MAX_WBITS = 15;

        [RubyConstant("MAX_MEM_LEVEL")]
        public const int MAX_MEM_LEVEL = 9;

        [RubyConstant("DEF_MEM_LEVEL")]
        public const int DEF_MEM_LEVEL = 8;

        [RubyConstant("Z_DEFLATED")]
        public const int Z_DEFLATED = 8;

        [RubyConstant("BINARY")]
        public const int BINARY = 0;

        [RubyConstant("ASCII")]
        public const int ASCII = 1;

        [RubyConstant("TEXT")]
        public const int TEXT = 1;

        [RubyConstant("UNKNOWN")]
        public const int UNKNOWN = 2;

        [RubyConstant("NO_COMPRESSION")]
        public const int NO_COMPRESSION = 0;

        [RubyConstant("BEST_SPEED")]
        public const int BEST_SPEED = 1;

        [RubyConstant("BEST_COMPRESSION")]
        public const int BEST_COMPRESSION = 9;

        [RubyConstant("DEFAULT_COMPRESSION")]
        public const int DEFAULT_COMPRESSION = -1;

        [RubyConstant("FILTERED")]
        public const int FILTERED = 1;

        [RubyConstant("HUFFMAN_ONLY")]
        public const int HUFFMAN_ONLY = 2;

        [RubyConstant("RLE")]
        public const int RLE = 3;

        [RubyConstant("FIXED")]
        public const int FIXED = 4;

        [RubyConstant("DEFAULT_STRATEGY")]
        public const int DEFAULT_STRATEGY = 0;

        [RubyConstant("OS_MSDOS")]
        public const int OS_MSDOS = 0x00;

        [RubyConstant("OS_AMIGA")]
        public const int OS_AMIGA = 0x01;

        [RubyConstant("OS_VMS")]
        public const int OS_VMS = 0x02;

        [RubyConstant("OS_UNIX")]
        public const int OS_UNIX = 0x03;

        [RubyConstant("OS_VMCMS")]
        public const int OS_VMCMS = 0x04;

        [RubyConstant("OS_ATARI")]
        public const int OS_ATARI = 0x05;

        [RubyConstant("OS_OS2")]
        public const int OS_OS2 = 0x06;

        [RubyConstant("OS_MACOS")]
        public const int OS_MACOS = 0x07;

        [RubyConstant("OS_ZSYSTEM")]
        public const int OS_ZSYSTEM = 0x08;

        [RubyConstant("OS_CPM")]
        public const int OS_CPM = 0x09;

        [RubyConstant("OS_TOPS20")]
        public const int OS_TOPS20 = 0x0a;

        [RubyConstant("OS_WIN32")]
        public const int OS_WIN32 = 0x0b;

        [RubyConstant("OS_QDOS")]
        public const int OS_QDOS = 0x0c;

        [RubyConstant("OS_RISCOS")]
        public const int OS_RISCOS = 0x0d;

        [RubyConstant("OS_UNKNOWN")]
        public const int OS_UNKNOWN = 0xff;

        [RubyConstant("OS_CODE")]
        public const int OS_CODE = OS_UNIX;

        #endregion

        #region Module functions

        // module_function in MRI: private instance methods as well as singleton ones.

        [RubyMethod("zlib_version", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("zlib_version", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ ZlibVersion(object self) {
            return MutableString.CreateAscii(LibZ.Version);
        }

        [RubyMethod("crc32", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("crc32", RubyMethodAttributes.PrivateInstance)]
        public static object GetCrc(ConversionStorage<IntegerValue>/*!*/ integerConversion, object self,
            [Optional, DefaultProtocol]MutableString str, [Optional]object initialCrc) {

            return Checksum(integerConversion, str, initialCrc, false);
        }

        [RubyMethod("adler32", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("adler32", RubyMethodAttributes.PrivateInstance)]
        public static object GetAdler(ConversionStorage<IntegerValue>/*!*/ integerConversion, object self,
            [Optional, DefaultProtocol]MutableString str, [Optional]object initialAdler) {

            return Checksum(integerConversion, str, initialAdler, true);
        }

        private static object Checksum(ConversionStorage<IntegerValue>/*!*/ integerConversion, MutableString str, object initial, bool adler) {
            uint seed;
            if (initial == null || initial is Missing) {
                // With no seed, libz's own initial value is the answer: 1 for adler32, 0 for crc32.
                seed = adler ? 1u : 0u;
            } else {
                seed = Protocols.CastToUInt32Unchecked(integerConversion, initial);
            }

            byte[] bytes = (str != null) ? str.ToByteArray() : null;
            uint length = (bytes != null) ? (uint)bytes.Length : 0;

            UIntPtr result = adler
                ? LibZ.adler32((UIntPtr)seed, bytes, length)
                : LibZ.crc32((UIntPtr)seed, bytes, length);

            return Protocols.Normalize((uint)result);
        }

        [RubyMethod("crc32_combine", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("crc32_combine", RubyMethodAttributes.PrivateInstance)]
        public static object CrcCombine(ConversionStorage<IntegerValue>/*!*/ integerConversion, object self,
            object crc1, object crc2, [DefaultProtocol]int length) {

            return Protocols.Normalize((uint)LibZ.crc32_combine(
                (UIntPtr)Protocols.CastToUInt32Unchecked(integerConversion, crc1),
                (UIntPtr)Protocols.CastToUInt32Unchecked(integerConversion, crc2),
                length));
        }

        [RubyMethod("adler32_combine", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("adler32_combine", RubyMethodAttributes.PrivateInstance)]
        public static object AdlerCombine(ConversionStorage<IntegerValue>/*!*/ integerConversion, object self,
            object adler1, object adler2, [DefaultProtocol]int length) {

            return Protocols.Normalize((uint)LibZ.adler32_combine(
                (UIntPtr)Protocols.CastToUInt32Unchecked(integerConversion, adler1),
                (UIntPtr)Protocols.CastToUInt32Unchecked(integerConversion, adler2),
                length));
        }

        [RubyMethod("crc_table", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("crc_table", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ CrcTable(object self) {
            var result = new RubyArray(256);
            foreach (uint entry in LibZ.GetCrcTable()) {
                result.Add(Protocols.Normalize(entry));
            }
            return result;
        }

        [RubyMethod("deflate", RubyMethodAttributes.PublicSingleton)]
        public static object DeflateString(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ str, [Optional]object level) {

            return Deflate.DeflateString(fixnumCast, block, null, str, level);
        }

        [RubyMethod("inflate", RubyMethodAttributes.PublicSingleton)]
        public static object InflateString(BlockParam block, RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
            return Inflate.InflateString(block, null, str);
        }

        [RubyMethod("gzip", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Gzip(ConversionStorage<int>/*!*/ fixnumCast, RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ str, [Optional]object options) {

            RubyContext context = fixnumCast.Context;

            int level = DEFAULT_COMPRESSION;
            int strategy = DEFAULT_STRATEGY;
            var hash = options as IDictionary<object, object>;
            if (hash != null) {
                object value;
                if (hash.TryGetValue(context.CreateAsciiSymbol("level"), out value) && value != null) {
                    level = Protocols.CastToFixnum(fixnumCast, value);
                }
                if (hash.TryGetValue(context.CreateAsciiSymbol("strategy"), out value) && value != null) {
                    strategy = Protocols.CastToFixnum(fixnumCast, value);
                }
            }

            // +16 asks libz to write a gzip wrapper rather than a zlib one.
            using (var z = new Deflate(level, MAX_WBITS + 16, DEF_MEM_LEVEL, strategy)) {
                z.Run(str.ToByteArray(), LibZ.Z_FINISH, null);
                return z.TakeBuffer();
            }
        }

        [RubyMethod("gunzip", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Gunzip(RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
            using (var z = new Inflate(MAX_WBITS + 16)) {
                z.Run(str.ToByteArray(), LibZ.Z_FINISH, null);
                return z.TakeBuffer();
            }
        }

        #endregion

        #region ZStream class

        [RubyClass("ZStream")]
        public class ZStream : IDisposable {
            /// <summary>
            /// MRI's ZSTREAM_AVAIL_OUT_STEP_MAX: with a block, output is handed over in chunks
            /// of exactly this size, and a shorter tail is held back until the stream ends.
            /// </summary>
            internal const int ChunkSize = 16384;

            /// <summary>z_stream, unmanaged because libz stores a pointer to it in its own state.</summary>
            private IntPtr _z;

            internal readonly bool _isInflate;
            private bool _ended;
            private bool _finished;
            private int _availOut;

            /// <summary>Input libz has not consumed yet - MRI's z->input.</summary>
            private readonly List<byte>/*!*/ _input = new List<byte>();

            /// <summary>Output produced but not handed to Ruby yet - MRI's z->buf.</summary>
            private readonly List<byte>/*!*/ _output = new List<byte>();

            /// <summary>
            /// Serializes every use of the native z_stream. MRI runs a stream under the GVL, so two
            /// Ruby threads never touch one z_stream at once there; IronRuby threads really are
            /// concurrent, and two of them in deflate() on one stream corrupt libz's state and the
            /// native heap (test_multithread_deflate killed the process with SIGSEGV), or one
            /// thread's run points next_in at a buffer the other has already unpinned.
            /// Reentrant, because a block yielded to mid-run may call back into the stream; the
            /// in-progress flag turns that into MRI's InProgressError instead.
            /// </summary>
            private readonly object/*!*/ _lock = new object();
            private bool _inProgress;

            /// <summary>MRI wraps #deflate and #inflate in a per-stream Mutex; this is its owner.</summary>
            private readonly object/*!*/ _methodLock = new object();
            private Thread _methodOwner;

            internal ZStream(bool isInflate) {
                _isInflate = isInflate;
                _z = Marshal.AllocHGlobal(LibZ.StreamSize);
                for (int i = 0; i < LibZ.StreamSize; i++) {
                    Marshal.WriteByte(_z, i, 0);
                }
            }

            ~ZStream() {
                Dispose(false);
            }

            public void Dispose() {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private void Dispose(bool disposing) {
                lock (_lock) {
                    DisposeNoLock();
                }
            }

            private void DisposeNoLock() {
                if (_z != IntPtr.Zero) {
                    if (!_ended) {
                        if (_isInflate) {
                            LibZ.inflateEnd(_z);
                        } else {
                            LibZ.deflateEnd(_z);
                        }
                        _ended = true;
                    }
                    Marshal.FreeHGlobal(_z);
                    _z = IntPtr.Zero;
                }
            }

            internal LibZ.ZStreamRec Rec {
                get {
                    lock (_lock) {
                        if (_z == IntPtr.Zero) {
                            throw new Error("stream is not ready");
                        }
                        return (LibZ.ZStreamRec)Marshal.PtrToStructure(_z, typeof(LibZ.ZStreamRec));
                    }
                }
            }

            /// <summary>
            /// Runs a native call on the stream with the stream lock held, so that it cannot race
            /// a run, an #end or a #reset on another thread.
            /// </summary>
            internal int Native(Func<IntPtr, int>/*!*/ call) {
                lock (_lock) {
                    return call(Handle);
                }
            }

            /// <summary>
            /// MRI's rb_mutex_synchronize(z->mutex, ...) around #deflate and #inflate: another
            /// thread waits its turn, and the owning thread coming back in - from the block it was
            /// given - is a recursive lock, which MRI reports as a ThreadError.
            /// </summary>
            internal T Synchronize<T>(Func<T>/*!*/ body) {
                if (_methodOwner == Thread.CurrentThread) {
                    throw new ThreadError("deadlock; recursive locking");
                }
                lock (_methodLock) {
                    _methodOwner = Thread.CurrentThread;
                    try {
                        return body();
                    } finally {
                        _methodOwner = null;
                    }
                }
            }

            private void SetRec(LibZ.ZStreamRec value) {
                Marshal.StructureToPtr(value, _z, false);
            }

            internal IntPtr Handle {
                get {
                    if (_z == IntPtr.Zero || _ended) {
                        throw new Error("stream is not ready");
                    }
                    return _z;
                }
            }

            internal bool IsFinished {
                get { return _finished; }
                set { _finished = value; }
            }

            internal bool IsEnded {
                get { return _ended; }
            }

            internal List<byte>/*!*/ Output {
                get { return _output; }
            }

            internal int PendingInput {
                get { return _input.Count; }
            }

            internal void AppendInput(byte[]/*!*/ bytes) {
                _input.AddRange(bytes);
            }

            #region running libz

            /// <summary>
            /// MRI's zstream_run. Feeds <paramref name="data"/> plus anything libz left over last
            /// time through deflate()/inflate(), yielding whole <see cref="ChunkSize"/> chunks of
            /// output to the block as they fill.
            ///
            /// Returns the result the block broke with, or null. <paramref name="broke"/> says which.
            /// </summary>
            internal object Run(byte[]/*!*/ data, int flush, BlockParam block, out bool broke) {
                lock (_lock) {
                    if (_inProgress) {
                        throw new InProgressError("zlib stream is in progress");
                    }
                    _inProgress = true;
                    try {
                        return RunNoLock(data, flush, block, out broke);
                    } finally {
                        _inProgress = false;
                    }
                }
            }

            private object RunNoLock(byte[]/*!*/ data, int flush, BlockParam block, out bool broke) {
                broke = false;
                object blockResult = null;

                byte[] pending;
                if (_input.Count > 0) {
                    pending = new byte[_input.Count + data.Length];
                    _input.CopyTo(pending, 0);
                    Array.Copy(data, 0, pending, _input.Count, data.Length);
                    _input.Clear();
                } else {
                    pending = data;
                }

                var scratch = new byte[ChunkSize];
                GCHandle inHandle = GCHandle.Alloc(pending, GCHandleType.Pinned);
                GCHandle outHandle = GCHandle.Alloc(scratch, GCHandleType.Pinned);
                int err = LibZ.Z_OK;
                int consumed = 0;
                try {
                    IntPtr inBase = inHandle.AddrOfPinnedObject();
                    IntPtr outBase = outHandle.AddrOfPinnedObject();

                    while (true) {
                        // Fetched on every pass: the block yielded to below may have closed
                        // the stream, and libz must not be handed an ended z_stream.
                        IntPtr handle = Handle;
                        var z = Rec;
                        z.next_in = inBase + consumed;
                        z.avail_in = (uint)(pending.Length - consumed);
                        z.next_out = outBase;
                        z.avail_out = (uint)ChunkSize;
                        SetRec(z);

                        err = _isInflate ? LibZ.inflate(handle, flush) : LibZ.deflate(handle, flush);

                        z = Rec;
                        int produced = ChunkSize - (int)z.avail_out;
                        for (int i = 0; i < produced; i++) {
                            _output.Add(scratch[i]);
                        }
                        consumed = pending.Length - (int)z.avail_in;
                        _availOut = (int)z.avail_out;

                        if (block != null && YieldChunks(block, ref blockResult)) {
                            broke = true;
                            break;
                        }

                        if (err == LibZ.Z_STREAM_END) {
                            _finished = true;
                            break;
                        }
                        if (err != LibZ.Z_OK && err != LibZ.Z_BUF_ERROR) {
                            break;
                        }
                        if (z.avail_out > 0) {
                            break;
                        }
                        if (z.avail_in == 0 && _isInflate && !LibZ.UseClrZlib) {
                            // inflate() answers Z_BUF_ERROR once it is out of input; deflate()
                            // can still have output pending in its own state, so only inflate
                            // gets to stop here. Not on .NET's zlib: Intel's fork in .NET 8
                            // reads all the input ahead into its bit buffer, so with the output
                            // full and nothing left to read it can still have output to give.
                            // It is asked again, and when it really is done it leaves the fresh
                            // buffer untouched, which the avail_out check above ends on.
                            break;
                        }
                    }
                } finally {
                    if (_z != IntPtr.Zero) {
                        ClearPointers();
                    }
                    inHandle.Free();
                    outHandle.Free();
                }

                for (int i = consumed; i < pending.Length; i++) {
                    _input.Add(pending[i]);
                }

                if (broke) {
                    return blockResult;
                }

                if (err != LibZ.Z_OK && err != LibZ.Z_STREAM_END) {
                    throw MakeError(err, Rec.msg);
                }

                return null;
            }

            /// <summary>Convenience for the callers that have no block and want exceptions only.</summary>
            internal void Run(byte[]/*!*/ data, int flush, BlockParam block) {
                bool broke;
                Run(data, flush, block, out broke);
            }

            /// <summary>
            /// libz remembers next_in/next_out across calls; the buffers they point at are
            /// pinned only for the duration of a run, so they have to be dropped afterwards -
            /// deflateParams() in particular will happily write through a stale next_out.
            /// </summary>
            private void ClearPointers() {
                var z = Rec;
                z.next_in = IntPtr.Zero;
                z.avail_in = 0;
                z.next_out = IntPtr.Zero;
                z.avail_out = 0;
                SetRec(z);
            }

            /// <summary>
            /// MRI's zstream_passthrough_input: once the stream has ended, whatever input libz
            /// did not take is not compressed data any more, so it becomes output verbatim.
            /// </summary>
            internal void PassthroughInput() {
                _output.AddRange(_input);
                _input.Clear();
            }

            /// <summary>
            /// Runs a libz entry point that produces output but takes no input - deflateParams().
            /// </summary>
            internal int RunWithOutputBuffer(Func<IntPtr, int>/*!*/ call) {
                lock (_lock) {
                    if (_inProgress) {
                        throw new InProgressError("zlib stream is in progress");
                    }
                    return RunWithOutputBufferNoLock(call);
                }
            }

            private int RunWithOutputBufferNoLock(Func<IntPtr, int>/*!*/ call) {
                var scratch = new byte[ChunkSize];
                GCHandle outHandle = GCHandle.Alloc(scratch, GCHandleType.Pinned);
                int err;
                try {
                    IntPtr outBase = outHandle.AddrOfPinnedObject();
                    IntPtr handle = Handle;
                    while (true) {
                        var z = Rec;
                        z.next_in = IntPtr.Zero;
                        z.avail_in = 0;
                        z.next_out = outBase;
                        z.avail_out = (uint)ChunkSize;
                        SetRec(z);

                        err = call(handle);

                        z = Rec;
                        int produced = ChunkSize - (int)z.avail_out;
                        for (int i = 0; i < produced; i++) {
                            _output.Add(scratch[i]);
                        }
                        if (err != LibZ.Z_BUF_ERROR) {
                            break;
                        }
                    }
                } finally {
                    ClearPointers();
                    outHandle.Free();
                }
                return err;
            }

            private bool YieldChunks(BlockParam/*!*/ block, ref object blockResult) {
                while (_output.Count >= ChunkSize) {
                    var chunk = MutableString.CreateBinary(_output.GetRange(0, ChunkSize), RubyEncoding.Binary);
                    _output.RemoveRange(0, ChunkSize);
                    if (block.Yield(chunk, out blockResult)) {
                        return true;
                    }
                }
                return false;
            }

            internal MutableString/*!*/ TakeBuffer() {
                var result = MutableString.CreateBinary(_output, RubyEncoding.Binary);
                _output.Clear();
                _availOut = 0;
                return result;
            }

            internal void AppendToBuffer(byte[]/*!*/ bytes) {
                _output.AddRange(bytes);
            }

            /// <summary>
            /// MRI's zstream_detach_buffer. With a block, a partial buffer is deliberately held
            /// back ("prevent tiny yields mid-stream") until the stream has ended.
            /// </summary>
            internal object DetachBuffer(BlockParam block, out bool broke) {
                broke = false;
                if (block != null && !_finished) {
                    return null;
                }

                var str = TakeBuffer();
                if (block == null) {
                    return str;
                }

                object blockResult;
                if (block.Yield(str, out blockResult)) {
                    broke = true;
                    return blockResult;
                }
                return null;
            }

            internal void End() {
                lock (_lock) {
                    if (_z == IntPtr.Zero || _ended) {
                        return;
                    }
                    if (_isInflate) {
                        LibZ.inflateEnd(_z);
                    } else {
                        LibZ.deflateEnd(_z);
                    }
                    _ended = true;
                }
            }

            internal void ResetStream() {
                lock (_lock) {
                    if (_inProgress) {
                        throw new InProgressError("zlib stream is in progress");
                    }
                    ResetStreamNoLock();
                }
            }

            private void ResetStreamNoLock() {
                int err = _isInflate ? LibZ.inflateReset(Handle) : LibZ.deflateReset(Handle);
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, Rec.msg);
                }
                _input.Clear();
                _output.Clear();
                _finished = false;
                _availOut = 0;
            }

            internal MutableString/*!*/ TakeInput() {
                var result = MutableString.CreateBinary(_input, RubyEncoding.Binary);
                _input.Clear();
                return result;
            }

            #endregion

            #region instance methods

            [RubyMethod("adler")]
            public static object Adler(ZStream/*!*/ self) {
                return Protocols.Normalize((uint)self.Rec.adler);
            }

            [RubyMethod("avail_in")]
            public static int AvailIn(ZStream/*!*/ self) {
                return self._input.Count;
            }

            [RubyMethod("avail_out")]
            public static int GetAvailOut(ZStream/*!*/ self) {
                return self._availOut;
            }

            [RubyMethod("avail_out=")]
            public static object SetAvailOut(ZStream/*!*/ self, [DefaultProtocol]int size) {
                self._availOut = size;
                return size;
            }

            [RubyMethod("finish")]
            public static object Finish(BlockParam block, ZStream/*!*/ self) {
                if (!self._finished) {
                    bool broke;
                    object result = self.Run(Utils.EmptyBytes, LibZ.Z_FINISH, block, out broke);
                    if (broke) {
                        return result;
                    }
                }

                bool detachBroke;
                return self.DetachBuffer(block, out detachBroke);
            }

            [RubyMethod("close")]
            [RubyMethod("end")]
            public static object Close(ZStream/*!*/ self) {
                self.End();
                return null;
            }

            [RubyMethod("stream_end?")]
            [RubyMethod("finished?")]
            public static bool IsFinishedP(ZStream/*!*/ self) {
                return self._finished;
            }

            [RubyMethod("closed?")]
            [RubyMethod("ended?")]
            public static bool IsClosed(ZStream/*!*/ self) {
                return self._ended;
            }

            [RubyMethod("data_type")]
            public static int DataType(ZStream/*!*/ self) {
                return self.Rec.data_type;
            }

            [RubyMethod("flush_next_in")]
            public static MutableString/*!*/ FlushNextIn(ZStream/*!*/ self) {
                return self.TakeInput();
            }

            [RubyMethod("flush_next_out")]
            public static object FlushNextOut(BlockParam block, ZStream/*!*/ self) {
                bool broke;
                return self.DetachBuffer(block, out broke);
            }

            [RubyMethod("reset")]
            public static object Reset(ZStream/*!*/ self) {
                self.ResetStream();
                return null;
            }

            [RubyMethod("total_in")]
            public static object TotalIn(ZStream/*!*/ self) {
                return Protocols.Normalize((ulong)self.Rec.total_in);
            }

            [RubyMethod("total_out")]
            public static object TotalOut(ZStream/*!*/ self) {
                return Protocols.Normalize((ulong)self.Rec.total_out);
            }

            #endregion
        }

        #endregion

        #region argument helpers

        internal static int OptionalInt(ConversionStorage<int>/*!*/ fixnumCast, object value, int defaultValue) {
            if (value == null || value is Missing) {
                return defaultValue;
            }
            return Protocols.CastToFixnum(fixnumCast, value);
        }

        internal static byte[]/*!*/ Bytes(MutableString str) {
            return (str != null) ? str.ToByteArray() : Utils.EmptyBytes;
        }

        internal static Exception/*!*/ MakeError(int err, IntPtr msgPtr) {
            string message = (msgPtr != IntPtr.Zero) ? Marshal.PtrToStringAnsi(msgPtr) : null;

            switch (err) {
                case LibZ.Z_STREAM_END: return new StreamEnd(message ?? "stream end");
                case LibZ.Z_NEED_DICT: return new NeedDict(message ?? "need dictionary");
                case LibZ.Z_STREAM_ERROR: return new StreamError(message ?? "stream error");
                case LibZ.Z_DATA_ERROR: return new DataError(message ?? "data error");
                case LibZ.Z_BUF_ERROR: return new BufError(message ?? "buffer error");
                case LibZ.Z_VERSION_ERROR: return new VersionError(message ?? "incompatible version");
                case LibZ.Z_MEM_ERROR: return new MemError(message ?? "insufficient memory");
                default: return new Error(message ?? ("unknown zlib error " + err));
            }
        }

        #endregion

        #region Inflate class

        [RubyClass("Inflate")]
        public class Inflate : ZStream {
            /// <summary>Dictionaries registered with #add_dictionary, keyed by their adler32.</summary>
            private Dictionary<uint, byte[]> _dictionaries;

            internal Inflate(int windowBits)
                : base(true) {

                int err = LibZ.inflateInit2_(Handle, windowBits, LibZ.Version, LibZ.StreamSize);
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, Rec.msg);
                }
            }

            [RubyConstructor]
            public static Inflate/*!*/ Create(ConversionStorage<int>/*!*/ fixnumCast, RubyClass/*!*/ self, [Optional]object windowBits) {
                return new Inflate(OptionalInt(fixnumCast, windowBits, MAX_WBITS));
            }

            /// <summary>MRI's do_inflate: nil means "no more input", and empty input is skipped
            /// so that inflate() cannot answer Z_BUF_ERROR for it.</summary>
            private object DoInflate(MutableString src, BlockParam block, out bool broke) {
                broke = false;
                if (src == null) {
                    return RunGuarded(Utils.EmptyBytes, LibZ.Z_FINISH, block, out broke);
                }

                byte[] bytes = src.ToByteArray();
                if (bytes.Length > 0 || PendingInput > 0) {
                    return RunGuarded(bytes, LibZ.Z_SYNC_FLUSH, block, out broke);
                }
                return null;
            }

            /// <summary>
            /// Runs, and on Z_NEED_DICT retries once with a dictionary registered by #add_dictionary,
            /// as MRI does before it gives up and raises.
            /// </summary>
            private object RunGuarded(byte[]/*!*/ data, int flush, BlockParam block, out bool broke) {
                try {
                    return Run(data, flush, block, out broke);
                } catch (NeedDict) {
                    byte[] dictionary;
                    if (_dictionaries == null || !_dictionaries.TryGetValue((uint)Rec.adler, out dictionary)) {
                        throw;
                    }
                    SetDictionary(dictionary);
                    return Run(Utils.EmptyBytes, flush, block, out broke);
                }
            }

            internal void SetDictionary(byte[]/*!*/ dictionary) {
                int err = Native(h => LibZ.inflateSetDictionary(h, dictionary, (uint)dictionary.Length));
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, Rec.msg);
                }
            }

            [RubyMethod("inflate")]
            public static object InflateMethod(BlockParam block, Inflate/*!*/ self, [DefaultProtocol]MutableString src) {
                return self.Synchronize(() => self.InflateBody(block, src));
            }

            private object InflateBody(BlockParam block, MutableString src) {
                Inflate self = this;
                bool broke;

                if (self.IsFinished) {
                    // Past the end of the stream everything is passed straight through.
                    if (src == null) {
                        return self.DetachBuffer(block, out broke);
                    }
                    self.AppendToBuffer(src.ToByteArray());
                    return MutableString.CreateBinary();
                }

                object result = self.DoInflate(src, block, out broke);
                if (broke) {
                    return result;
                }
                object dst = self.DetachBuffer(block, out broke);
                if (self.IsFinished) {
                    self.PassthroughInput();
                }
                return dst;
            }

            [RubyMethod("<<")]
            public static object Append(BlockParam block, Inflate/*!*/ self, [DefaultProtocol]MutableString src) {
                if (self.IsFinished) {
                    if (src != null) {
                        self.AppendToBuffer(src.ToByteArray());
                    }
                    return self;
                }

                bool broke;
                object result = self.DoInflate(src, block, out broke);
                if (broke) {
                    return result;
                }
                if (self.IsFinished) {
                    self.PassthroughInput();
                }
                return self;
            }

            [RubyMethod("set_dictionary")]
            public static MutableString/*!*/ SetDictionary(Inflate/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ dictionary) {
                self.SetDictionary(dictionary.ToByteArray());
                return dictionary;
            }

            [RubyMethod("add_dictionary")]
            public static MutableString/*!*/ AddDictionary(Inflate/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ dictionary) {
                byte[] bytes = dictionary.ToByteArray();
                if (self._dictionaries == null) {
                    self._dictionaries = new Dictionary<uint, byte[]>();
                }
                self._dictionaries[(uint)LibZ.adler32(LibZ.adler32(UIntPtr.Zero, null, 0), bytes, (uint)bytes.Length)] = bytes;
                return dictionary;
            }

            [RubyMethod("sync_point?")]
            public static bool IsSyncPoint(Inflate/*!*/ self) {
                int err = self.Native(LibZ.inflateSyncPoint);
                if (err == 1) {
                    return true;
                }
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, self.Rec.msg);
                }
                return false;
            }

            [RubyMethod("sync")]
            public static bool Sync(Inflate/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ src) {
                // MRI hunts for the next full-flush point by feeding the data in; without
                // inflateSync's partial-input contract the honest answer is "not found".
                self.AppendInput(src.ToByteArray());
                return false;
            }

            [RubyMethod("inflate", RubyMethodAttributes.PublicSingleton)]
            public static object InflateString(BlockParam block, RubyClass self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
                using (var z = new Inflate(MAX_WBITS)) {
                    bool broke;
                    object result = z.Run(str.ToByteArray(), LibZ.Z_SYNC_FLUSH, block, out broke);
                    if (broke) {
                        return result;
                    }
                    return z.DetachBuffer(block, out broke);
                }
            }
        }

        #endregion

        #region Deflate class

        [RubyClass("Deflate")]
        public class Deflate : ZStream {

            internal Deflate(int level, int windowBits, int memLevel, int strategy)
                : base(false) {

                int err = LibZ.deflateInit2_(Handle, level, LibZ.Z_DEFLATED, windowBits, memLevel, strategy,
                    LibZ.Version, LibZ.StreamSize);
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, Rec.msg);
                }
            }

            [RubyConstructor]
            public static Deflate/*!*/ Create(ConversionStorage<int>/*!*/ fixnumCast, RubyClass/*!*/ self,
                [Optional]object level, [Optional]object windowBits, [Optional]object memLevel, [Optional]object strategy) {

                return new Deflate(
                    OptionalInt(fixnumCast, level, DEFAULT_COMPRESSION),
                    OptionalInt(fixnumCast, windowBits, MAX_WBITS),
                    OptionalInt(fixnumCast, memLevel, DEF_MEM_LEVEL),
                    OptionalInt(fixnumCast, strategy, DEFAULT_STRATEGY)
                );
            }

            /// <summary>MRI's do_deflate: nil finishes the stream, and an empty Z_NO_FLUSH run is
            /// skipped so that deflate() cannot answer Z_BUF_ERROR for it.</summary>
            private object DoDeflate(MutableString src, int flush, BlockParam block, out bool broke) {
                broke = false;
                if (src == null) {
                    return Run(Utils.EmptyBytes, LibZ.Z_FINISH, block, out broke);
                }

                byte[] bytes = src.ToByteArray();
                if (flush != LibZ.Z_NO_FLUSH || bytes.Length > 0) {
                    return Run(bytes, flush, block, out broke);
                }
                return null;
            }

            [RubyMethod("deflate")]
            public static object DeflateMethod(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, Deflate/*!*/ self,
                [DefaultProtocol]MutableString src, [Optional]object flush) {

                int f = OptionalInt(fixnumCast, flush, LibZ.Z_NO_FLUSH);
                return self.Synchronize(() => {
                    bool broke;
                    object result = self.DoDeflate(src, f, block, out broke);
                    if (broke) {
                        return result;
                    }
                    return self.DetachBuffer(block, out broke);
                });
            }

            [RubyMethod("<<")]
            public static object Append(BlockParam block, Deflate/*!*/ self, [DefaultProtocol]MutableString src) {
                bool broke;
                object result = self.DoDeflate(src, LibZ.Z_NO_FLUSH, block, out broke);
                return broke ? result : (object)self;
            }

            [RubyMethod("flush")]
            public static object Flush(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, Deflate/*!*/ self, [Optional]object flush) {
                int f = OptionalInt(fixnumCast, flush, SYNC_FLUSH);
                bool broke;
                if (f != LibZ.Z_NO_FLUSH) {
                    object result = self.Run(Utils.EmptyBytes, f, block, out broke);
                    if (broke) {
                        return result;
                    }
                }
                return self.DetachBuffer(block, out broke);
            }

            [RubyMethod("params")]
            public static object Params(ConversionStorage<int>/*!*/ fixnumCast, Deflate/*!*/ self, object level, object strategy) {
                int newLevel = OptionalInt(fixnumCast, level, DEFAULT_COMPRESSION);
                int newStrategy = OptionalInt(fixnumCast, strategy, DEFAULT_STRATEGY);

                // Changing the level mid-stream makes libz flush what it has buffered, so this
                // needs an output buffer just as much as deflate() does.
                int err = self.RunWithOutputBuffer(handle => LibZ.deflateParams(handle, newLevel, newStrategy));

                if (err != LibZ.Z_OK) {
                    throw MakeError(err, self.Rec.msg);
                }
                return null;
            }

            [RubyMethod("set_dictionary")]
            public static MutableString/*!*/ SetDictionary(Deflate/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ dictionary) {
                byte[] bytes = dictionary.ToByteArray();
                int err = self.Native(h => LibZ.deflateSetDictionary(h, bytes, (uint)bytes.Length));
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, self.Rec.msg);
                }
                return dictionary;
            }

            [RubyMethod("deflate", RubyMethodAttributes.PublicSingleton)]
            public static object DeflateString(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, RubyClass self,
                [DefaultProtocol, NotNull]MutableString/*!*/ str, [Optional]object level) {

                using (var z = new Deflate(OptionalInt(fixnumCast, level, DEFAULT_COMPRESSION), MAX_WBITS, DEF_MEM_LEVEL, DEFAULT_STRATEGY)) {
                    bool broke;
                    object result = z.Run(str.ToByteArray(), LibZ.Z_FINISH, block, out broke);
                    if (broke) {
                        return result;
                    }
                    return z.DetachBuffer(block, out broke);
                }
            }
        }

        #endregion

        #region Exceptions

        [RubyException("Error")]
        public class Error : SystemException {
            public Error() : this(null, null) { }
            public Error(string message) : this(message, null) { }
            public Error(string message, Exception inner) : base(message ?? "Error", inner) { }
        }

        [RubyException("StreamEnd")]
        public class StreamEnd : Error {
            public StreamEnd() : this(null, null) { }
            public StreamEnd(string message) : this(message, null) { }
            public StreamEnd(string message, Exception inner) : base(message ?? "StreamEnd", inner) { }
        }

        [RubyException("NeedDict")]
        public class NeedDict : Error {
            public NeedDict() : this(null, null) { }
            public NeedDict(string message) : this(message, null) { }
            public NeedDict(string message, Exception inner) : base(message ?? "NeedDict", inner) { }
        }

        [RubyException("DataError")]
        public class DataError : Error {
            public DataError() : this(null, null) { }
            public DataError(string message) : this(message, null) { }
            public DataError(string message, Exception inner) : base(message ?? "DataError", inner) { }
        }

        [RubyException("BufError")]
        public class BufError : Error {
            public BufError() : this(null, null) { }
            public BufError(string message) : this(message, null) { }
            public BufError(string message, Exception inner) : base(message ?? "BufError", inner) { }
        }

        [RubyException("MemError")]
        public class MemError : Error {
            public MemError() : this(null, null) { }
            public MemError(string message) : this(message, null) { }
            public MemError(string message, Exception inner) : base(message ?? "MemError", inner) { }
        }

        [RubyException("VersionError")]
        public class VersionError : Error {
            public VersionError() : this(null, null) { }
            public VersionError(string message) : this(message, null) { }
            public VersionError(string message, Exception inner) : base(message ?? "VersionError", inner) { }
        }

        [RubyException("InProgressError")]
        public class InProgressError : Error {
            public InProgressError() : this(null, null) { }
            public InProgressError(string message) : this(message, null) { }
            public InProgressError(string message, Exception inner) : base(message ?? "InProgressError", inner) { }
        }

        [RubyException("StreamError")]
        public class StreamError : Error {
            public StreamError() : this(null, null) { }
            public StreamError(string message) : this(message, null) { }
            public StreamError(string message, Exception inner) : base(message ?? "StreamError", inner) { }
        }

        #endregion

    }
}

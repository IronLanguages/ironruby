/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;

namespace IronRuby.StandardLibrary.Zlib {

    /// <summary>
    /// zlib on Windows. There is no libz there, but every .NET runtime carries zlib in
    /// System.IO.Compression.Native, the one DeflateStream runs on (zlib-ng in zlib-compat mode
    /// since .NET 9, Intel's fork of zlib 1.2 in .NET 8). What it exports is narrower than libz:
    /// init, deflate/inflate and end over its own PAL_ZStream, plus crc32.
    ///
    /// This runs LibZ's z_stream-shaped record over those exports, so ZStream cannot tell the
    /// difference: next_in/avail_in/next_out/avail_out go to the PAL stream and back, total_in,
    /// total_out and adler (the running adler32 or crc32 libz keeps) are kept here. What the
    /// exports leave out - the preset dictionaries, deflateParams once data has gone in, and
    /// inflateSyncPoint - raises NotImplementedError.
    ///
    /// The record's opaque field, which libz would hand to zalloc/zfree, holds the GCHandle of
    /// the stream's <see cref="Stream"/>; nothing native ever reads the record.
    /// </summary>
    internal static class ClrZlib {
        private const string Lib = "System.IO.Compression.Native";

        /// <summary>The zlib API version behind the exports; libz's would come from zlibVersion().</summary>
        internal static string/*!*/ Version {
            get { return Environment.Version.Major >= 9 ? "1.3.1.zlib-ng" : "1.2.13"; }
        }

        /// <summary>PAL_ZStream from System.IO.Compression.Native's pal_zlib.h.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PalZStream {
            public IntPtr nextIn;
            public IntPtr nextOut;
            public IntPtr msg;
            public IntPtr internalState;
            public uint availIn;
            public uint availOut;
        }

        [DllImport(Lib, EntryPoint = "CompressionNative_DeflateInit2_", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DeflateInit2_(ref PalZStream stream, int level, int method, int windowBits, int memLevel, int strategy);

        [DllImport(Lib, EntryPoint = "CompressionNative_Deflate", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Deflate(ref PalZStream stream, int flush);

        [DllImport(Lib, EntryPoint = "CompressionNative_DeflateEnd", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DeflateEnd(ref PalZStream stream);

        [DllImport(Lib, EntryPoint = "CompressionNative_InflateInit2_", CallingConvention = CallingConvention.Cdecl)]
        private static extern int InflateInit2_(ref PalZStream stream, int windowBits);

        [DllImport(Lib, EntryPoint = "CompressionNative_Inflate", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Inflate(ref PalZStream stream, int flush);

        [DllImport(Lib, EntryPoint = "CompressionNative_InflateEnd", CallingConvention = CallingConvention.Cdecl)]
        private static extern int InflateEnd(ref PalZStream stream);

        private const int Z_BINARY = 0;
        private const int Z_TEXT = 1;
        private const int Z_UNKNOWN = 2;
        private const int Z_DEFAULT_COMPRESSION = -1;
        private const int Z_FIXED = 4;
        private const int GzipHeaderSize = 10;

        /// <summary>Which checksum libz would keep in strm->adler for the stream's wrapper.</summary>
        private enum Check { None, Adler32, Crc32, Detect }

        private sealed class Stream {
            public PalZStream Pal;
            public bool IsInflate;
            public int Level, Method, WindowBits, MemLevel, Strategy;
            public Check Check;
            public uint Sum;

            // The first bytes of the stream: a zlib header's dictionary id, which Z_NEED_DICT reports,
            // and the fixed part of a gzip header, until which libz keeps adler at 1 when it detects gzip.
            public readonly byte[]/*!*/ Header = new byte[GzipHeaderSize];
            public int HeaderLength;
            public bool GzipHeaderPending;

            // deflate's data_type: libz guesses it from the first block it flushes (detect_data_type)
            public bool DataTypeDecided, SawBinary, SawText;

            public int Init() {
                Pal = default(PalZStream);
                Sum = 0;
                HeaderLength = 0;
                GzipHeaderPending = false;
                DataTypeDecided = SawBinary = SawText = false;
                if (IsInflate) {
                    // inflateReset: strm->adler = state->wrap & 1
                    Check = (WindowBits < 0) ? Check.None : (WindowBits >= 32) ? Check.Detect : (WindowBits >= 16) ? Check.Crc32 : Check.Adler32;
                    Sum = (Check == Check.Adler32 || Check == Check.Detect) ? 1u : 0u;
                    return InflateInit2_(ref Pal, WindowBits);
                }
                // deflateReset: strm->adler = wrap == 2 ? crc32(0, NULL, 0) : adler32(0, NULL, 0)
                Check = (WindowBits < 0) ? Check.None : (WindowBits >= 16) ? Check.Crc32 : Check.Adler32;
                Sum = (Check == Check.Crc32) ? 0u : 1u;
                return DeflateInit2_(ref Pal, Level, Method, WindowBits, MemLevel, Strategy);
            }

            public int End() {
                return IsInflate ? InflateEnd(ref Pal) : DeflateEnd(ref Pal);
            }
        }

        private static LibZ.ZStreamRec Read(IntPtr strm) {
            return (LibZ.ZStreamRec)Marshal.PtrToStructure(strm, typeof(LibZ.ZStreamRec));
        }

        private static void Write(IntPtr strm, LibZ.ZStreamRec rec) {
            Marshal.StructureToPtr(rec, strm, false);
        }

        private static Stream GetStream(LibZ.ZStreamRec rec) {
            return (rec.opaque != IntPtr.Zero) ? (Stream)GCHandle.FromIntPtr(rec.opaque).Target : null;
        }

        internal static int DeflateInit(IntPtr strm, int level, int method, int windowBits, int memLevel, int strategy) {
            return Init(strm, new Stream { Level = level, Method = method, WindowBits = windowBits, MemLevel = memLevel, Strategy = strategy });
        }

        internal static int InflateInit(IntPtr strm, int windowBits) {
            return Init(strm, new Stream { IsInflate = true, WindowBits = windowBits });
        }

        private static int Init(IntPtr strm, Stream/*!*/ s) {
            int err = s.Init();
            var rec = Read(strm);
            rec.msg = s.Pal.msg;
            if (err == LibZ.Z_OK) {
                rec.opaque = GCHandle.ToIntPtr(GCHandle.Alloc(s));
                rec.adler = (UIntPtr)s.Sum;
                rec.total_in = rec.total_out = UIntPtr.Zero;
                rec.data_type = InitialDataType(s);
            } else {
                // The PAL allocated its z_stream before zlib refused the parameters, and only End
                // frees it; nothing will call End for a stream that never got a handle.
                s.End();
            }
            Write(strm, rec);
            return err;
        }

        // deflateReset sets Z_UNKNOWN; inflate leaves the zeroed field alone until it runs
        private static int InitialDataType(Stream/*!*/ s) {
            return s.IsInflate ? Z_BINARY : Z_UNKNOWN;
        }

        internal static int End(IntPtr strm) {
            var rec = Read(strm);
            Stream s = GetStream(rec);
            if (s == null) {
                return LibZ.Z_STREAM_ERROR;
            }
            int err = s.End();
            GCHandle.FromIntPtr(rec.opaque).Free();
            rec.opaque = IntPtr.Zero;
            rec.msg = IntPtr.Zero;
            Write(strm, rec);
            return err;
        }

        internal static int Reset(IntPtr strm) {
            var rec = Read(strm);
            Stream s = GetStream(rec);
            if (s == null) {
                return LibZ.Z_STREAM_ERROR;
            }
            // .NET 8 exports no inflateReset and no runtime exports deflateReset; a stream begun
            // again with the same parameters is what a reset leaves.
            s.End();
            int err = s.Init();
            rec.msg = s.Pal.msg;
            rec.adler = (UIntPtr)s.Sum;
            rec.total_in = rec.total_out = UIntPtr.Zero;
            rec.data_type = InitialDataType(s);
            Write(strm, rec);
            return err;
        }

        internal static int Run(IntPtr strm, int flush) {
            var rec = Read(strm);
            Stream s = GetStream(rec);
            if (s == null) {
                return LibZ.Z_STREAM_ERROR;
            }

            IntPtr inStart = rec.next_in, outStart = rec.next_out;
            uint availIn = rec.avail_in, availOut = rec.avail_out;
            s.Pal.nextIn = inStart;
            s.Pal.availIn = availIn;
            s.Pal.nextOut = outStart;
            s.Pal.availOut = availOut;

            int err = s.IsInflate ? Inflate(ref s.Pal, flush) : Deflate(ref s.Pal, flush);

            int consumed = (int)(availIn - s.Pal.availIn);
            int produced = (int)(availOut - s.Pal.availOut);
            rec.next_in = s.Pal.nextIn;
            rec.avail_in = s.Pal.availIn;
            rec.next_out = s.Pal.nextOut;
            rec.avail_out = s.Pal.availOut;
            rec.msg = s.Pal.msg;
            rec.total_in = (UIntPtr)((ulong)rec.total_in + (ulong)consumed);
            rec.total_out = (UIntPtr)((ulong)rec.total_out + (ulong)produced);

            if (s.IsInflate) {
                if (consumed > 0 && s.HeaderLength < s.Header.Length) {
                    int n = Math.Min(consumed, s.Header.Length - s.HeaderLength);
                    Marshal.Copy(inStart, s.Header, s.HeaderLength, n);
                    s.HeaderLength += n;
                }
                if (s.Check == Check.Detect && s.HeaderLength > 0) {
                    // libz keeps adler at 1 (the zlib start) until it has read a whole gzip header
                    s.Check = (s.Header[0] == 0x1f) ? Check.Crc32 : Check.Adler32;
                    s.GzipHeaderPending = s.Check == Check.Crc32;
                }
                if (s.GzipHeaderPending && s.HeaderLength >= GzipHeaderSize) {
                    s.GzipHeaderPending = false;
                    s.Sum = 0;
                }
                // the checksum covers what comes out of inflate and what goes into deflate
                Update(s, outStart, produced);
                if (err == LibZ.Z_NEED_DICT && s.HeaderLength >= 6) {
                    // inflate reports the dictionary it wants in strm->adler (RFC 1950's DICTID)
                    s.Sum = (uint)(s.Header[2] << 24 | s.Header[3] << 16 | s.Header[4] << 8 | s.Header[5]);
                }
            } else {
                Update(s, inStart, consumed);
                if (!s.DataTypeDecided) {
                    ScanDataType(s, inStart, consumed);
                    // libz decides when it flushes its first block; a flush here is where that
                    // happens for all but very long input, and the guess covers everything so far
                    if (flush != LibZ.Z_NO_FLUSH) {
                        s.DataTypeDecided = true;
                        rec.data_type = (!s.SawBinary && s.SawText) ? Z_TEXT : Z_BINARY;
                    }
                }
            }
            rec.adler = (UIntPtr)s.Sum;

            Write(strm, rec);
            return err;
        }

        private static void Update(Stream/*!*/ s, IntPtr data, int length) {
            if (length <= 0 || (s.Check != Check.Adler32 && s.Check != Check.Crc32)) {
                return;
            }
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
            s.Sum = (s.Check == Check.Adler32) ? Checksums.Adler32(s.Sum, bytes, 0, length) : Checksums.Crc32(s.Sum, bytes, 0, length);
        }

        /// <summary>
        /// zlib's detect_data_type: a byte from the block list (0-6, 14-25, 28-31) makes the data
        /// binary, otherwise one from the allow list (tab, line feed, carriage return, 32-255) makes
        /// it text.
        /// </summary>
        private static void ScanDataType(Stream/*!*/ s, IntPtr data, int length) {
            if (length <= 0 || s.SawBinary) {
                return;
            }
            const uint blockMask = 0xf3ffc07f;
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
            foreach (byte b in bytes) {
                if (b < 32 && (blockMask & (1u << b)) != 0) {
                    s.SawBinary = true;
                    return;
                }
                if (b >= 32 || b == 9 || b == 10 || b == 13) {
                    s.SawText = true;
                }
            }
        }

        /// <summary>
        /// deflateParams. Until anything has gone in the stream can simply begin again with the new
        /// level and strategy, which is all deflateParams does then; after that the exports have
        /// nothing that changes them. Bad values are refused before anything changes, as in libz.
        /// </summary>
        internal static int Params(IntPtr strm, int level, int strategy) {
            var rec = Read(strm);
            Stream s = GetStream(rec);
            if (s == null) {
                return LibZ.Z_STREAM_ERROR;
            }
            if (level < Z_DEFAULT_COMPRESSION || level > 9 || strategy < 0 || strategy > Z_FIXED) {
                return LibZ.Z_STREAM_ERROR;
            }
            if (NormalLevel(level) == NormalLevel(s.Level) && strategy == s.Strategy) {
                return LibZ.Z_OK;
            }
            if ((ulong)rec.total_in != 0 || (ulong)rec.total_out != 0) {
                throw Unavailable("Zlib::Deflate#params after data has been deflated", "deflateParams");
            }
            s.Level = level;
            s.Strategy = strategy;
            return Reset(strm);
        }

        // Z_DEFAULT_COMPRESSION is level 6
        private static int NormalLevel(int level) {
            return (level == Z_DEFAULT_COMPRESSION) ? 6 : level;
        }

        internal static Exception/*!*/ Unavailable(string/*!*/ what, string/*!*/ function) {
            return new NotImplementedException(what + " is not available on Windows: the zlib .NET ships there (System.IO.Compression.Native) does not export " + function);
        }
    }

    /// <summary>adler32 and crc32 as libz computes them, for the platform without libz.</summary>
    internal static class Checksums {
        private const uint AdlerBase = 65521;
        private const uint CrcPolynomial = 0xedb88320;

        internal static readonly uint[]/*!*/ CrcTable = MakeCrcTable();

        private static uint[]/*!*/ MakeCrcTable() {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++) {
                uint c = n;
                for (int k = 0; k < 8; k++) {
                    c = ((c & 1) != 0) ? CrcPolynomial ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        internal static uint Adler32(uint adler, byte[] buffer, int offset, int length) {
            if (buffer == null) {
                return 1;
            }
            uint a = adler & 0xffff, b = adler >> 16;
            while (length > 0) {
                // 5552 is the most bytes that cannot overflow b before the modulo (zlib's NMAX)
                int n = Math.Min(length, 5552);
                length -= n;
                while (n-- > 0) {
                    a += buffer[offset++];
                    b += a;
                }
                a %= AdlerBase;
                b %= AdlerBase;
            }
            return b << 16 | a;
        }

        internal static uint Crc32(uint crc, byte[] buffer, int offset, int length) {
            if (buffer == null) {
                return 0;
            }
            uint c = ~crc;
            for (int i = 0; i < length; i++) {
                c = CrcTable[(c ^ buffer[offset + i]) & 0xff] ^ (c >> 8);
            }
            return ~c;
        }

        /// <summary>zlib's adler32_combine.</summary>
        internal static uint Adler32Combine(uint adler1, uint adler2, long length2) {
            if (length2 < 0) {
                return 0xffffffff;
            }
            uint rem = (uint)(length2 % AdlerBase);
            ulong sum1 = adler1 & 0xffff;
            ulong sum2 = (rem * sum1) % AdlerBase;
            sum1 += (adler2 & 0xffff) + AdlerBase - 1;
            sum2 += ((adler1 >> 16) & 0xffff) + ((adler2 >> 16) & 0xffff) + AdlerBase - rem;
            if (sum1 >= AdlerBase) sum1 -= AdlerBase;
            if (sum1 >= AdlerBase) sum1 -= AdlerBase;
            if (sum2 >= ((ulong)AdlerBase << 1)) sum2 -= ((ulong)AdlerBase << 1);
            if (sum2 >= AdlerBase) sum2 -= AdlerBase;
            return (uint)(sum1 | (sum2 << 16));
        }

        /// <summary>zlib's crc32_combine: crc1 shifted by length2 zero bytes, then crc2.</summary>
        internal static uint Crc32Combine(uint crc1, uint crc2, long length2) {
            return MultiplyModP(PowerOfXModP(length2, 3), crc1) ^ crc2;
        }

        // a(x) * b(x) modulo the CRC polynomial, bits reflected as in zlib's multmodp
        private static uint MultiplyModP(uint a, uint b) {
            uint m = 1u << 31, p = 0;
            while (m != 0) {
                if ((a & m) != 0) {
                    p ^= b;
                    if ((a & (m - 1)) == 0) {
                        break;
                    }
                }
                m >>= 1;
                b = ((b & 1) != 0) ? (b >> 1) ^ CrcPolynomial : b >> 1;
            }
            return p;
        }

        private static readonly uint[]/*!*/ _x2n = MakeX2nTable();

        private static uint[]/*!*/ MakeX2nTable() {
            var table = new uint[32];
            uint p = 1u << 30;  // x^1
            table[0] = p;
            for (int n = 1; n < 32; n++) {
                table[n] = p = MultiplyModP(p, p);
            }
            return table;
        }

        // x^(n * 2^k) modulo the CRC polynomial, as zlib's x2nmodp
        private static uint PowerOfXModP(long n, int k) {
            uint p = 1u << 31;  // x^0
            while (n > 0) {
                if ((n & 1) != 0) {
                    p = MultiplyModP(_x2n[k & 31], p);
                }
                n >>= 1;
                k++;
            }
            return p;
        }
    }
}

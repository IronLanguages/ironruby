/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace IronRuby.Builtins {
    /// <summary>
    /// SipHash-1-3 (Aumasson and Bernstein's SipHash with one compression round and three finalization rounds) under a
    /// random key drawn once per process: the keyed hash CRuby uses for String#hash, and Python and Rust for theirs. It
    /// hashes long ASCII strings (MutableString.Content.HashAsciiBytes), where it is about 1.5 times as fast as Marvin
    /// (String.GetHashCode) over the same bytes, and like Marvin it keeps the hash unpredictable without the key, so
    /// that colliding keys cannot be prepared in advance to flood a Hash.
    /// </summary>
    internal static class SipHash {
        private static readonly ulong _key0 = NewKey();
        private static readonly ulong _key1 = NewKey();

        private static ulong NewKey() {
            Span<byte> key = stackalloc byte[sizeof(ulong)];
            RandomNumberGenerator.Fill(key);
            return BinaryPrimitives.ReadUInt64LittleEndian(key);
        }

        /// <summary>The 64-bit SipHash-1-3 of <paramref name="data"/> under this process's key, folded to 32 bits.</summary>
        internal static int Hash32(ReadOnlySpan<byte> data) {
            ulong hash = Hash13(data, _key0, _key1);
            return (int)hash ^ (int)(hash >> 32);
        }

        /// <summary>SipHash-1-3 of <paramref name="data"/> under the key (<paramref name="key0"/>, <paramref name="key1"/>), the key's and the message's bytes read little-endian.</summary>
        internal static ulong Hash13(ReadOnlySpan<byte> data, ulong key0, ulong key1) {
            ulong v0 = 0x736f6d6570736575UL ^ key0;
            ulong v1 = 0x646f72616e646f6dUL ^ key1;
            ulong v2 = 0x6c7967656e657261UL ^ key0;
            ulong v3 = 0x7465646279746573UL ^ key1;

            ref byte start = ref MemoryMarshal.GetReference(data);
            int length = data.Length;
            int end = length & ~7;
            for (int i = 0; i < end; i += sizeof(ulong)) {
                ulong m = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i));
                if (!BitConverter.IsLittleEndian) {
                    m = BinaryPrimitives.ReverseEndianness(m);
                }
                v3 ^= m;
                Round(ref v0, ref v1, ref v2, ref v3);
                v0 ^= m;
            }

            // the last 0-7 bytes, and the length's low byte in the top byte
            ulong last = (ulong)length << 56;
            for (int i = 0; i < (length & 7); i++) {
                last |= (ulong)Unsafe.Add(ref start, end + i) << (8 * i);
            }
            v3 ^= last;
            Round(ref v0, ref v1, ref v2, ref v3);
            v0 ^= last;

            v2 ^= 0xff;
            Round(ref v0, ref v1, ref v2, ref v3);
            Round(ref v0, ref v1, ref v2, ref v3);
            Round(ref v0, ref v1, ref v2, ref v3);
            return v0 ^ v1 ^ v2 ^ v3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Round(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3) {
            v0 += v1; v1 = BitOperations.RotateLeft(v1, 13); v1 ^= v0; v0 = BitOperations.RotateLeft(v0, 32);
            v2 += v3; v3 = BitOperations.RotateLeft(v3, 16); v3 ^= v2;
            v0 += v3; v3 = BitOperations.RotateLeft(v3, 21); v3 ^= v0;
            v2 += v1; v1 = BitOperations.RotateLeft(v1, 17); v1 ^= v2; v2 = BitOperations.RotateLeft(v2, 32);
        }
    }
}

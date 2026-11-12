/* ****************************************************************************
 *
 * SHA-224 and the incremental-digest shim OpenSSL::Digest runs on.
 *
 * ***************************************************************************/

using System;
using Crypto = System.Security.Cryptography;

namespace IronRuby.StandardLibrary.OpenSsl {

    /// <summary>
    /// The update/peek/reset lifecycle OpenSSL::Digest needs.  .NET's IncrementalHash
    /// is the implementation for every algorithm it has; SHA-224 has none, so the two
    /// meet behind this.
    /// </summary>
    internal abstract class IncrementalDigest : IDisposable {
        /// <summary>Appends data to the running digest.</summary>
        public abstract void Append(byte[]/*!*/ data);

        /// <summary>The digest of everything appended so far, without ending it.</summary>
        public abstract byte[]/*!*/ Peek();

        /// <summary>Discards everything appended so far.</summary>
        public abstract void Reset();

        public virtual void Dispose() { }
    }

    internal sealed class NetDigest : IncrementalDigest {
        private readonly Crypto.IncrementalHash/*!*/ _hash;

        public NetDigest(Crypto.IncrementalHash/*!*/ hash) {
            _hash = hash;
        }

        public override void Append(byte[]/*!*/ data) {
            _hash.AppendData(data);
        }

        public override byte[]/*!*/ Peek() {
            return _hash.GetCurrentHash();
        }

        public override void Reset() {
            _hash.GetHashAndReset();
        }

        public override void Dispose() {
            _hash.Dispose();
        }
    }

    /// <summary>
    /// SHA-224 (FIPS 180-4): SHA-256's compression function started from a different
    /// initial state and truncated to 28 bytes.  .NET binds neither the algorithm nor
    /// a way to change SHA-256's IV, so the compression function is written out here.
    /// The input is buffered whole, which is what OpenSSL::Digest wants anyway -- it
    /// has to be able to report the digest of a prefix without ending the digest.
    /// </summary>
    internal sealed class Sha224 : IncrementalDigest {
        private static readonly uint[]/*!*/ H0 = {
            0xc1059ed8, 0x367cd507, 0x3070dd17, 0xf70e5939,
            0xffc00b31, 0x68581511, 0x64f98fa7, 0xbefa4fa4
        };

        private static readonly uint[]/*!*/ K = {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        };

        private byte[]/*!*/ _data = new byte[0];

        public override void Append(byte[]/*!*/ data) {
            if (data.Length == 0) {
                return;
            }
            var grown = new byte[_data.Length + data.Length];
            Buffer.BlockCopy(_data, 0, grown, 0, _data.Length);
            Buffer.BlockCopy(data, 0, grown, _data.Length, data.Length);
            _data = grown;
        }

        public override void Reset() {
            _data = new byte[0];
        }

        public override byte[]/*!*/ Peek() {
            return Compute(_data);
        }

        private static uint RotR(uint x, int n) {
            return (x >> n) | (x << (32 - n));
        }

        internal static byte[]/*!*/ Compute(byte[]/*!*/ message) {
            // padding: 0x80, zeroes, then the bit length big-endian in 8 bytes
            int padded = message.Length + 1;
            padded += (56 - padded % 64 + 64) % 64;
            var block = new byte[padded + 8];
            Buffer.BlockCopy(message, 0, block, 0, message.Length);
            block[message.Length] = 0x80;
            ulong bits = (ulong)message.Length * 8;
            for (int i = 0; i < 8; i++) {
                block[block.Length - 1 - i] = (byte)(bits >> (8 * i));
            }

            var h = (uint[])H0.Clone();
            var w = new uint[64];
            for (int offset = 0; offset < block.Length; offset += 64) {
                for (int i = 0; i < 16; i++) {
                    int j = offset + i * 4;
                    w[i] = (uint)((block[j] << 24) | (block[j + 1] << 16) | (block[j + 2] << 8) | block[j + 3]);
                }
                for (int i = 16; i < 64; i++) {
                    uint s0 = RotR(w[i - 15], 7) ^ RotR(w[i - 15], 18) ^ (w[i - 15] >> 3);
                    uint s1 = RotR(w[i - 2], 17) ^ RotR(w[i - 2], 19) ^ (w[i - 2] >> 10);
                    w[i] = unchecked(w[i - 16] + s0 + w[i - 7] + s1);
                }

                uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
                for (int i = 0; i < 64; i++) {
                    uint s1 = RotR(e, 6) ^ RotR(e, 11) ^ RotR(e, 25);
                    uint ch = (e & f) ^ (~e & g);
                    uint temp1 = unchecked(hh + s1 + ch + K[i] + w[i]);
                    uint s0 = RotR(a, 2) ^ RotR(a, 13) ^ RotR(a, 22);
                    uint maj = (a & b) ^ (a & c) ^ (b & c);
                    uint temp2 = unchecked(s0 + maj);
                    hh = g; g = f; f = e;
                    e = unchecked(d + temp1);
                    d = c; c = b; b = a;
                    a = unchecked(temp1 + temp2);
                }
                unchecked {
                    h[0] += a; h[1] += b; h[2] += c; h[3] += d;
                    h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
                }
            }

            // SHA-224 is SHA-256 with the last word of the state dropped
            var result = new byte[28];
            for (int i = 0; i < 7; i++) {
                result[i * 4] = (byte)(h[i] >> 24);
                result[i * 4 + 1] = (byte)(h[i] >> 16);
                result[i * 4 + 2] = (byte)(h[i] >> 8);
                result[i * 4 + 3] = (byte)h[i];
            }
            return result;
        }
    }
}

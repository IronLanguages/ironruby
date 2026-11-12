/* ****************************************************************************
 *
 * scrypt (RFC 7914) key derivation.
 *
 * .NET has no scrypt primitive, so OpenSSL::KDF.scrypt needs one here. This is
 * a direct transcription of RFC 7914: PBKDF2-HMAC-SHA256 on the outside,
 * Salsa20/8 core -> BlockMix -> ROMix in the middle.
 *
 * ***************************************************************************/

using System;
using System.Security.Cryptography;

namespace IronRuby.StandardLibrary.OpenSsl {

    internal static class ScryptImpl {

        public static byte[]/*!*/ DeriveKey(byte[]/*!*/ password, byte[]/*!*/ salt, int N, int r, int p, int length) {
            int blockSize = 128 * r;

            // B = PBKDF2-HMAC-SHA256(P, S, 1, p * 128 * r)
            byte[] b = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * blockSize);

            uint[] block = new uint[blockSize / 4];
            uint[] scratchX = new uint[32 * r];
            uint[] scratchY = new uint[32 * r];
            uint[] v = new uint[(long)N * 32 * r <= int.MaxValue ? N * 32 * r : 0];
            if (v.Length == 0) {
                throw new OutOfMemoryException("scrypt parameters require too much memory");
            }

            for (int i = 0; i < p; i++) {
                Buffer.BlockCopy(b, i * blockSize, block, 0, blockSize);
                ROMix(block, N, r, v, scratchX, scratchY);
                Buffer.BlockCopy(block, 0, b, i * blockSize, blockSize);
            }

            return Rfc2898DeriveBytes.Pbkdf2(password, b, 1, HashAlgorithmName.SHA256, length);
        }

        private static void ROMix(uint[]/*!*/ x, int N, int r, uint[]/*!*/ v, uint[]/*!*/ t, uint[]/*!*/ y) {
            int words = 32 * r;

            for (int i = 0; i < N; i++) {
                Array.Copy(x, 0, v, i * words, words);
                BlockMix(x, y, r);
                Array.Copy(y, x, words);
            }

            for (int i = 0; i < N; i++) {
                // j = Integerify(X) mod N; X is little-endian, so the low word of the
                // last 64-byte block is the low 32 bits of the integer.
                int j = (int)(x[words - 16] & (uint)(N - 1));
                for (int k = 0; k < words; k++) {
                    t[k] = x[k] ^ v[j * words + k];
                }
                BlockMix(t, y, r);
                Array.Copy(y, x, words);
            }
        }

        private static void BlockMix(uint[]/*!*/ input, uint[]/*!*/ output, int r) {
            uint[] x = new uint[16];
            Array.Copy(input, (2 * r - 1) * 16, x, 0, 16);

            for (int i = 0; i < 2 * r; i++) {
                for (int k = 0; k < 16; k++) {
                    x[k] ^= input[i * 16 + k];
                }
                Salsa20_8(x);

                // even blocks first, then odd blocks
                int target = ((i % 2) == 0 ? i / 2 : r + i / 2) * 16;
                Array.Copy(x, 0, output, target, 16);
            }
        }

        private static uint Rotl(uint a, int b) {
            return (a << b) | (a >> (32 - b));
        }

        private static void Salsa20_8(uint[]/*!*/ b) {
            uint[] x = new uint[16];
            Array.Copy(b, x, 16);

            for (int i = 0; i < 8; i += 2) {
                // column round
                x[4] ^= Rotl(x[0] + x[12], 7); x[8] ^= Rotl(x[4] + x[0], 9);
                x[12] ^= Rotl(x[8] + x[4], 13); x[0] ^= Rotl(x[12] + x[8], 18);
                x[9] ^= Rotl(x[5] + x[1], 7); x[13] ^= Rotl(x[9] + x[5], 9);
                x[1] ^= Rotl(x[13] + x[9], 13); x[5] ^= Rotl(x[1] + x[13], 18);
                x[14] ^= Rotl(x[10] + x[6], 7); x[2] ^= Rotl(x[14] + x[10], 9);
                x[6] ^= Rotl(x[2] + x[14], 13); x[10] ^= Rotl(x[6] + x[2], 18);
                x[3] ^= Rotl(x[15] + x[11], 7); x[7] ^= Rotl(x[3] + x[15], 9);
                x[11] ^= Rotl(x[7] + x[3], 13); x[15] ^= Rotl(x[11] + x[7], 18);
                // row round
                x[1] ^= Rotl(x[0] + x[3], 7); x[2] ^= Rotl(x[1] + x[0], 9);
                x[3] ^= Rotl(x[2] + x[1], 13); x[0] ^= Rotl(x[3] + x[2], 18);
                x[6] ^= Rotl(x[5] + x[4], 7); x[7] ^= Rotl(x[6] + x[5], 9);
                x[4] ^= Rotl(x[7] + x[6], 13); x[5] ^= Rotl(x[4] + x[7], 18);
                x[11] ^= Rotl(x[10] + x[9], 7); x[8] ^= Rotl(x[11] + x[10], 9);
                x[9] ^= Rotl(x[8] + x[11], 13); x[10] ^= Rotl(x[9] + x[8], 18);
                x[12] ^= Rotl(x[15] + x[14], 7); x[13] ^= Rotl(x[12] + x[15], 9);
                x[14] ^= Rotl(x[13] + x[12], 13); x[15] ^= Rotl(x[14] + x[13], 18);
            }

            for (int i = 0; i < 16; i++) {
                b[i] += x[i];
            }
        }
    }
}

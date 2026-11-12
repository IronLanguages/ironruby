using System;
using System.Numerics;

namespace IronRuby.Runtime {
    /// <summary>
    /// Replacement for the legacy Microsoft.Scripting.Math.BigInteger(int sign, params uint[] data)
    /// constructor, removed when the DLR moved to System.Numerics.
    /// </summary>
    internal static class BigIntegerCompat {
        public static BigInteger Create(int sign, uint[]/*!*/ words) {
            // words are little-endian; trailing zero byte keeps the magnitude non-negative
            byte[] bytes = new byte[words.Length * 4 + 1];
            Buffer.BlockCopy(words, 0, bytes, 0, words.Length * 4);
            var magnitude = new BigInteger(bytes);
            return sign < 0 ? -magnitude : magnitude;
        }
    }
}

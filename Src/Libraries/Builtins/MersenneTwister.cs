/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Numerics;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// MRI's Random generator: MT19937 as random.c/mt19937.c have it, seeded the same way, so that
    /// Random.new(seed) produces the same numbers, bytes and marshal data as MRI does.
    /// Random itself is written in Ruby (ruby4.rb); this is the part that has to be fast.
    ///
    /// Every public operation holds a private lock.  MRI's generator is shared by every thread
    /// that calls Kernel#rand or Array#shuffle, and the GVL makes each draw atomic there; without
    /// the lock two threads drawing at once advanced _next past the end of the state vector, and
    /// Kernel#rand died with System::IndexOutOfRangeException.  A draw is a few nanoseconds of
    /// arithmetic, so an uncontended lock is the cheap way to keep the MRI sequence intact.
    /// </summary>
    public sealed class MersenneTwister {
        private const int N = 624;
        private const int M = 397;
        private const uint MatrixA = 0x9908b0dfU;
        private const uint UpperMask = 0x80000000U;
        private const uint LowerMask = 0x7fffffffU;

        private readonly uint[] _state = new uint[N];
        private readonly object _lock = new object();
        private int _next;
        private int _left;

        /// <summary>
        /// rand_init: the seed's absolute value as little-endian 32-bit words. A seed of one word
        /// goes to init_genrand; a longer one to init_by_array, after dropping a top word of 1
        /// (MRI's "leading-zero-guard"), so 2**32 seeds like init_by_array([0]).
        /// </summary>
        public MersenneTwister(BigInteger seed) {
            if (seed.Sign < 0) {
                seed = -seed;
            }
            byte[] bytes = seed.ToByteArray();
            int len = (bytes.Length + 3) / 4;
            uint[] key = new uint[Math.Max(len, 1)];
            for (int i = 0; i < bytes.Length; i++) {
                key[i / 4] |= (uint)bytes[i] << (8 * (i % 4));
            }
            while (len > 1 && key[len - 1] == 0) {
                len--;
            }
            if (len <= 1) {
                InitGenrand(key[0]);
            } else {
                if (key[len - 1] == 1) {
                    len--;
                }
                InitByArray(key, len);
            }
        }

        private MersenneTwister(MersenneTwister/*!*/ other) {
            Array.Copy(other._state, _state, N);
            _next = other._next;
            _left = other._left;
        }

        public MersenneTwister/*!*/ Copy() {
            lock (_lock) {
                return new MersenneTwister(this);
            }
        }

        private void InitGenrand(uint s) {
            _state[0] = s;
            for (int j = 1; j < N; j++) {
                _state[j] = unchecked(1812433253U * (_state[j - 1] ^ (_state[j - 1] >> 30)) + (uint)j);
            }
            _left = 1;
            _next = N;
        }

        private void InitByArray(uint[]/*!*/ key, int keyLength) {
            InitGenrand(19650218U);
            int i = 1, j = 0;
            for (int k = Math.Max(N, keyLength); k > 0; k--) {
                _state[i] = unchecked((_state[i] ^ ((_state[i - 1] ^ (_state[i - 1] >> 30)) * 1664525U)) + key[j] + (uint)j);
                i++; j++;
                if (i >= N) { _state[0] = _state[N - 1]; i = 1; }
                if (j >= keyLength) j = 0;
            }
            for (int k = N - 1; k > 0; k--) {
                _state[i] = unchecked((_state[i] ^ ((_state[i - 1] ^ (_state[i - 1] >> 30)) * 1566083941U)) - (uint)i);
                i++;
                if (i >= N) { _state[0] = _state[N - 1]; i = 1; }
            }
            _state[0] = 0x80000000U;
        }

        private static uint Twist(uint u, uint v) {
            return (((u & UpperMask) | (v & LowerMask)) >> 1) ^ ((v & 1U) != 0 ? MatrixA : 0U);
        }

        private void NextState() {
            _left = N;
            _next = 0;
            int p = 0;
            for (int j = N - M + 1; --j > 0; p++) {
                _state[p] = _state[p + M] ^ Twist(_state[p], _state[p + 1]);
            }
            for (int j = M; --j > 0; p++) {
                _state[p] = _state[p + M - N] ^ Twist(_state[p], _state[p + 1]);
            }
            _state[p] = _state[p + M - N] ^ Twist(_state[p], _state[0]);
        }

        public uint NextUInt32() {
            lock (_lock) {
                return Draw();
            }
        }

        /// <summary>genrand_int32 itself; the caller holds _lock.</summary>
        private uint Draw() {
            if (--_left <= 0) {
                NextState();
            }
            uint y = _state[_next++];
            y ^= (y >> 11);
            y ^= (y << 7) & 0x9d2c5680U;
            y ^= (y << 15) & 0xefc60000U;
            y ^= (y >> 18);
            return y;
        }

        /// <summary>genrand_int32, as a Ruby Integer.</summary>
        public object GenrandInt32() {
            return Protocols.Normalize(NextUInt32());
        }

        /// <summary>
        /// random_real: genrand_res53 for [0, 1), int_pair_to_real_inclusive for [0, 1].
        /// </summary>
        public double GenrandReal(bool excludeEnd) {
            uint a, b;
            lock (_lock) {
                a = Draw();
                b = Draw();
            }
            if (excludeEnd) {
                return ((a >> 5) * 67108864.0 + (b >> 6)) * (1.0 / 9007199254740992.0);
            }
            // int_pair_to_real_inclusive: the 64 bits scaled by (2**53 + 1) / 2**64, so that
            // both ends are reachable.
            UInt128 x = ((UInt128)a << 32) | b;
            UInt128 m = ((UInt128)1 << 53) | 1;
            return Math.ScaleB((double)(ulong)((x * m) >> 64), -53);
        }

        /// <summary>rb_rand_bytes_int32: each word goes out least significant byte first.</summary>
        public byte[]/*!*/ GenrandBytes(int count) {
            byte[] result = new byte[count];
            int i = 0;
            lock (_lock) {
                while (i < count) {
                    uint x = Draw();
                    for (int k = 0; k < 4 && i < count; k++) {
                        result[i++] = (byte)x;
                        x >>= 8;
                    }
                }
            }
            return result;
        }

        private static uint MakeMask(uint x) {
            x |= x >> 1;
            x |= x >> 2;
            x |= x >> 4;
            x |= x >> 8;
            x |= x >> 16;
            return x;
        }

        /// <summary>
        /// limited_rand / limited_big_rand: a uniform Integer in 0..limit, drawn a 32-bit word at a
        /// time from the most significant end and started over as soon as it exceeds limit.
        /// </summary>
        public object LimitedRand(BigInteger limit) {
            if (limit.Sign <= 0) {
                return ScriptingRuntimeHelpers.Int32ToObject(0);
            }
            byte[] bytes = limit.ToByteArray();
            int len = (bytes.Length + 3) / 4;
            uint[] lim = new uint[len];
            for (int i = 0; i < bytes.Length; i++) {
                lim[i / 4] |= (uint)bytes[i] << (8 * (i % 4));
            }
            uint[] rnd = new uint[len];
            lock (_lock) {
                FillLimited(lim, rnd, len);
            }

            BigInteger result = BigInteger.Zero;
            for (int i = len - 1; i >= 0; i--) {
                result = (result << 32) | rnd[i];
            }
            return Protocols.Normalize(result);
        }

        /// <summary>limited_rand's drawing loop; the caller holds _lock.</summary>
        private void FillLimited(uint[]/*!*/ lim, uint[]/*!*/ rnd, int len) {
        retry:
            uint mask = 0;
            bool boundary = true;
            for (int i = len - 1; i >= 0; i--) {
                uint r = 0;
                mask = mask != 0 ? 0xffffffffU : MakeMask(lim[i]);
                if (mask != 0) {
                    r = Draw() & mask;
                    if (boundary) {
                        if (lim[i] < r) {
                            goto retry;
                        }
                        if (r < lim[i]) {
                            boundary = false;
                        }
                    }
                }
                rnd[i] = r;
            }
        }

        /// <summary>The state vector as one Integer, least significant word first (rand_mt_dump).</summary>
        public BigInteger GetState() {
            lock (_lock) {
                BigInteger result = BigInteger.Zero;
                for (int i = N - 1; i >= 0; i--) {
                    result = (result << 32) | _state[i];
                }
                return result;
            }
        }

        public int Left {
            get { lock (_lock) { return _left; } }
        }

        /// <summary>rand_mt_load.</summary>
        public void SetState(BigInteger state, int left) {
            if (left < 0 || left > N) {
                throw new ArgumentOutOfRangeException("left", "wrong value");
            }
            BigInteger mask = uint.MaxValue;
            lock (_lock) {
                for (int i = 0; i < N; i++) {
                    _state[i] = (uint)(state & mask);
                    state >>= 32;
                }
                _left = left;
                _next = N - left + 1;
            }
        }

        public bool StateEquals(MersenneTwister/*!*/ other) {
            // Compare against a snapshot of +other+ taken under its own lock, so that two threads
            // comparing a == b and b == a never hold both locks at once.
            other = other.Copy();
            lock (_lock) {
                return StateEqualsLocked(other);
            }
        }

        private bool StateEqualsLocked(MersenneTwister/*!*/ other) {
            if (_left != other._left || _next != other._next) {
                return false;
            }
            for (int i = 0; i < N; i++) {
                if (_state[i] != other._state[i]) {
                    return false;
                }
            }
            return true;
        }
    }
}

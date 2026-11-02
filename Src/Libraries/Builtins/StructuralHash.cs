/* ****************************************************************************
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
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// Array#hash and Hash#hash.
    ///
    /// An Array folds the hashes of its elements one after another (order matters, and an empty
    /// array does not hash to nothing); a Hash XORs the hashes of its [key, value] pairs together
    /// (order does not matter). Every step goes through a 32-bit avalanche, so that equal values
    /// cannot cancel each other out, and the result stays a Fixnum.
    ///
    /// A structure that, directly or indirectly, contains itself hashes to one fixed value: an
    /// Array or a Hash met again while it is being hashed abandons the whole computation - out to
    /// the outermost #hash on this thread, through whatever user-defined #hash methods lie in
    /// between - which then answers <see cref="RecursiveValue"/>. That is what keeps an array that
    /// contains itself and an array that merely contains that one, which are #eql?, hashing alike.
    /// Arrays and hashes share the one stack because the two nest in each other.
    ///
    /// The element hashes come from a dynamic #hash call, so a user-defined #hash is honoured; a
    /// result that is not an Integer is converted with #to_int, as MRI's rb_hash does.
    /// </summary>
    internal static class StructuralHash {
        internal const int RecursiveValue = 0x48415348;
        private const uint ArraySeed = 0x41525259;

        // The objects being hashed on this thread, outermost first; null when no #hash is running.
        [ThreadStatic]
        private static List<object> _active;

        // A list to reuse for the next outermost #hash.
        [ThreadStatic]
        private static List<object> _spare;

        /// <summary>
        /// Unwinds to the outermost #hash when a structure is met again. Like throw/catch, it is
        /// flow control: Ruby's rescue clauses never see it, ensure clauses do run.
        /// </summary>
        private sealed class RecursionUnwinder : StackUnwinder {
            internal readonly List<object>/*!*/ Stack;

            internal RecursionUnwinder(List<object>/*!*/ stack)
                : base(null) {
                Stack = stack;
            }
        }

        internal static uint Mix32(uint value) {
            value = (value ^ (value >> 16)) * 0x45D9F3B;
            value = (value ^ (value >> 16)) * 0x45D9F3B;
            return value ^ (value >> 16);
        }

        public static int GetArrayHash(UnaryOpStorage/*!*/ hashStorage, ConversionStorage<IntegerValue>/*!*/ toInt, IList/*!*/ self) {
            var stack = _active;
            if (stack != null) {
                Enter(stack, self);
                try {
                    return (int)ArrayDigest(hashStorage, toInt, self);
                } finally {
                    stack.RemoveAt(stack.Count - 1);
                }
            }

            stack = EnterOutermost(self);
            try {
                return (int)ArrayDigest(hashStorage, toInt, self);
            } catch (RecursionUnwinder unwinder) when (unwinder.Stack == stack) {
                return RecursiveValue;
            } finally {
                LeaveOutermost(stack);
            }
        }

        public static int GetHashHash(UnaryOpStorage/*!*/ hashStorage, ConversionStorage<IntegerValue>/*!*/ toInt, Hash/*!*/ self) {
            var stack = _active;
            if (stack != null) {
                Enter(stack, self);
                try {
                    return (int)HashDigest(hashStorage, toInt, self);
                } finally {
                    stack.RemoveAt(stack.Count - 1);
                }
            }

            stack = EnterOutermost(self);
            try {
                return (int)HashDigest(hashStorage, toInt, self);
            } catch (RecursionUnwinder unwinder) when (unwinder.Stack == stack) {
                return RecursiveValue;
            } finally {
                LeaveOutermost(stack);
            }
        }

        private static void Enter(List<object>/*!*/ stack, object/*!*/ self) {
            for (int i = 0; i < stack.Count; i++) {
                if (ReferenceEquals(stack[i], self)) {
                    throw new RecursionUnwinder(stack);
                }
            }
            stack.Add(self);
        }

        private static List<object>/*!*/ EnterOutermost(object/*!*/ self) {
            var stack = _spare ?? new List<object>();
            _spare = null;
            stack.Add(self);
            _active = stack;
            return stack;
        }

        private static void LeaveOutermost(List<object>/*!*/ stack) {
            _active = null;
            stack.Clear();
            _spare = stack;
        }

        private static uint ArrayDigest(UnaryOpStorage/*!*/ hashStorage, ConversionStorage<IntegerValue>/*!*/ toInt, IList/*!*/ self) {
            uint result = Mix32(unchecked((uint)self.Count + ArraySeed));
            if (self.Count == 0) {
                return result;
            }

            var hashSite = hashStorage.GetCallSite("hash");

            // the length is read on every turn: an element's #hash may change the array
            for (int i = 0; i < self.Count; i++) {
                result = Mix32(result ^ Mix32(ElementHash(hashSite, toInt, self[i])));
            }
            return result;
        }

        private static uint HashDigest(UnaryOpStorage/*!*/ hashStorage, ConversionStorage<IntegerValue>/*!*/ toInt, Hash/*!*/ self) {
            int count = self.Count;
            uint result = Mix32((uint)count);
            if (count == 0) {
                return result;
            }

            var hashSite = hashStorage.GetCallSite("hash");

            // Walks the entries the way Hash#each does: a key's #hash may delete entries (one deleted
            // before its turn is skipped) but may not add any.
            object[] keys = new object[count];
            object[] values = new object[count];
            self.CopyKeysAndValues(keys, values);
            int stamp = self.MutationStamp;
            self.BeginIteration();
            try {
                for (int i = 0; i < count; i++) {
                    object key = keys[i];
                    object value;
                    if (self.MutationStamp == stamp) {
                        value = values[i];
                    } else if (!self.TryGetValue(key, out value)) {
                        if (self.Count < count) {
                            continue;
                        }
                        value = values[i];
                    }

                    // the hash of the pair [key, value], as Array#hash computes it
                    uint pair = Mix32(2 + ArraySeed);
                    pair = Mix32(pair ^ Mix32(ElementHash(hashSite, toInt, CustomStringDictionary.ObjToNull(key))));
                    pair = Mix32(pair ^ Mix32(ElementHash(hashSite, toInt, value)));
                    result ^= Mix32(pair);
                }
            } finally {
                self.EndIteration();
            }
            return result;
        }

        /// <summary>
        /// The low 32 bits of what the element's #hash answers. Ruby only promises an Integer there,
        /// and one wider than a Fixnum is perfectly legal.
        /// </summary>
        private static uint ElementHash(CallSite<Func<CallSite, object, object>>/*!*/ hashSite, ConversionStorage<IntegerValue>/*!*/ toInt, object item) {
            object value = hashSite.Target(hashSite, item);
            if (value is int) {
                return unchecked((uint)(int)value);
            }
            if (value is long) {
                return unchecked((uint)(long)value);
            }
            if (value is BigInteger) {
                return LowBits((BigInteger)value);
            }

            IntegerValue integer = Protocols.CastToInteger(toInt, value);
            return integer.IsFixnum ? unchecked((uint)integer.Fixnum) : LowBits(integer.Bignum);
        }

        private static uint LowBits(BigInteger value) {
            return (uint)(value & UInt32.MaxValue);
        }
    }
}

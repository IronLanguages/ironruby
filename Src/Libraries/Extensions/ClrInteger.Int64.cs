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
using System.Numerics;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    /// <summary>
    /// An Integer that does not fit in an Int32 is carried as a System.Int64 while it fits in one,
    /// and only becomes a BigInteger beyond that - which is where CRuby's Fixnum ends too, near
    /// enough. Every Integer method already works on such a value, because Int64 converts
    /// implicitly to BigInteger and the BigInteger overloads normalize their results; what this
    /// file adds is the arithmetic and comparison that must not go through BigInteger, since going
    /// there is exactly the cliff this representation exists to remove.
    ///
    /// The invariant these overloads maintain, and that the rest of the runtime relies on, is that
    /// a value has ONE representation: Int32 if it fits in an Int32, otherwise Int64 if it fits in
    /// an Int64, otherwise BigInteger. <see cref="ClrInteger.Narrow"/> and
    /// <see cref="Protocols.Normalize(BigInteger)"/> are the two funnels that establish it, so
    /// every result here leaves through one of them. #hash, #eql? and Hash keys are correct as a
    /// consequence rather than by special casing.
    /// </summary>
    public static partial class ClrInteger {
        #region helpers

        /// <summary>The sum, as an Int32 / Int64 / BigInteger, whichever the magnitude calls for.</summary>
        internal static object/*!*/ AddWide(long x, long y) {
            long r = unchecked(x + y);
            // Signed overflow iff both operands differ in sign from the result.
            if (((x ^ r) & (y ^ r)) < 0) {
                return Protocols.Normalize((BigInteger)x + y);
            }
            return Narrow(r);
        }

        internal static object/*!*/ SubtractWide(long x, long y) {
            long r = unchecked(x - y);
            // Signed overflow iff the operands differ in sign and the result differs from x.
            if (((x ^ y) & (x ^ r)) < 0) {
                return Protocols.Normalize((BigInteger)x - y);
            }
            return Narrow(r);
        }

        internal static object/*!*/ MultiplyWide(long x, long y) {
            long low;
            long high = Math.BigMul(x, y, out low);
            // The 128-bit product fits in 64 bits iff the high half is just the sign extension.
            if (high != (low >> 63)) {
                return Protocols.Normalize((BigInteger)x * y);
            }
            return Narrow(low);
        }

        internal static object/*!*/ DivideWide(long x, long y) {
            if (x == Int64.MinValue && y == -1) {
                return Protocols.Normalize(-(BigInteger)Int64.MinValue);
            }
            return Narrow(MathUtils.FloorDivideUnchecked(x, y));
        }

        internal static object/*!*/ NegateWide(long x) {
            if (x == Int64.MinValue) {
                return Protocols.Normalize(-(BigInteger)Int64.MinValue);
            }
            return Narrow(-x);
        }

        #endregion

        #region + - * / div % modulo divmod

        [RubyMethod("+")]
        public static object/*!*/ Add(long self, long other) {
            return AddWide(self, other);
        }

        [RubyMethod("+")]
        public static object/*!*/ Add(long self, int other) {
            return AddWide(self, other);
        }

        [RubyMethod("+")]
        public static object/*!*/ Add(int self, long other) {
            return AddWide(self, other);
        }

        [RubyMethod("+")]
        public static object/*!*/ Add(long self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize((BigInteger)self + other);
        }

        [RubyMethod("+")]
        public static double Add(long self, double other) {
            return (double)self + other;
        }

        [RubyMethod("-")]
        public static object/*!*/ Subtract(long self, long other) {
            return SubtractWide(self, other);
        }

        [RubyMethod("-")]
        public static object/*!*/ Subtract(long self, int other) {
            return SubtractWide(self, other);
        }

        [RubyMethod("-")]
        public static object/*!*/ Subtract(int self, long other) {
            return SubtractWide(self, other);
        }

        [RubyMethod("-")]
        public static object/*!*/ Subtract(long self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize((BigInteger)self - other);
        }

        [RubyMethod("-")]
        public static double Subtract(long self, double other) {
            return (double)self - other;
        }

        [RubyMethod("*")]
        public static object/*!*/ Multiply(long self, long other) {
            return MultiplyWide(self, other);
        }

        [RubyMethod("*")]
        public static object/*!*/ Multiply(long self, int other) {
            return MultiplyWide(self, other);
        }

        [RubyMethod("*")]
        public static object/*!*/ Multiply(int self, long other) {
            return MultiplyWide(self, other);
        }

        [RubyMethod("*")]
        public static object/*!*/ Multiply(long self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize((BigInteger)self * other);
        }

        [RubyMethod("*")]
        public static double Multiply(long self, double other) {
            return (double)self * other;
        }

        [RubyMethod("-@")]
        public static object/*!*/ Minus(long self) {
            return NegateWide(self);
        }

        [RubyMethod("/"), RubyMethod("div")]
        public static object/*!*/ Divide(long self, long other) {
            return DivideWide(self, other);
        }

        [RubyMethod("/"), RubyMethod("div")]
        public static object/*!*/ Divide(long self, int other) {
            return DivideWide(self, other);
        }

        [RubyMethod("/"), RubyMethod("div")]
        public static object/*!*/ Divide(int self, long other) {
            return DivideWide(self, other);
        }

        [RubyMethod("%"), RubyMethod("modulo")]
        public static object/*!*/ Modulo(long self, long other) {
            return Narrow(MathUtils.FloorRemainder(self, other));
        }

        [RubyMethod("%"), RubyMethod("modulo")]
        public static object/*!*/ Modulo(long self, int other) {
            return Narrow(MathUtils.FloorRemainder(self, other));
        }

        [RubyMethod("%"), RubyMethod("modulo")]
        public static object/*!*/ Modulo(int self, long other) {
            return Narrow(MathUtils.FloorRemainder(self, other));
        }

        [RubyMethod("divmod")]
        public static RubyArray/*!*/ DivMod(long self, long other) {
            return RubyOps.MakeArray2(DivideWide(self, other), Narrow(MathUtils.FloorRemainder(self, other)));
        }

        [RubyMethod("divmod")]
        public static RubyArray/*!*/ DivMod(long self, int other) {
            return DivMod(self, (long)other);
        }

        [RubyMethod("divmod")]
        public static RubyArray/*!*/ DivMod(int self, long other) {
            return DivMod((long)self, other);
        }

        #endregion

        #region abs, zero?, to_f

        [RubyMethod("abs")]
        [RubyMethod("magnitude")]
        public static object/*!*/ Abs(long self) {
            return self >= 0 ? Narrow(self) : NegateWide(self);
        }

        [RubyMethod("zero?")]
        public static bool IsZero(long self) {
            return self == 0;
        }

        [RubyMethod("to_f")]
        public static double ToFloat(long self) {
            return (double)self;
        }

        #endregion

        #region <, <=, >, >=, <=>, ==, ===

        [RubyMethod("<")]
        public static bool LessThan(long self, long other) {
            return self < other;
        }

        [RubyMethod("<")]
        public static bool LessThan(long self, int other) {
            return self < other;
        }

        [RubyMethod("<")]
        public static bool LessThan(int self, long other) {
            return self < other;
        }

        [RubyMethod("<=")]
        public static bool LessThanOrEqual(long self, long other) {
            return self <= other;
        }

        [RubyMethod("<=")]
        public static bool LessThanOrEqual(long self, int other) {
            return self <= other;
        }

        [RubyMethod("<=")]
        public static bool LessThanOrEqual(int self, long other) {
            return self <= other;
        }

        [RubyMethod(">")]
        public static bool GreaterThan(long self, long other) {
            return self > other;
        }

        [RubyMethod(">")]
        public static bool GreaterThan(long self, int other) {
            return self > other;
        }

        [RubyMethod(">")]
        public static bool GreaterThan(int self, long other) {
            return self > other;
        }

        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(long self, long other) {
            return self >= other;
        }

        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(long self, int other) {
            return self >= other;
        }

        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(int self, long other) {
            return self >= other;
        }

        [RubyMethod("<=>")]
        public static int Compare(long self, long other) {
            return self.CompareTo(other);
        }

        [RubyMethod("<=>")]
        public static int Compare(long self, int other) {
            return self.CompareTo((long)other);
        }

        [RubyMethod("<=>")]
        public static int Compare(int self, long other) {
            return ((long)self).CompareTo(other);
        }

        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(long self, long other) {
            return self == other;
        }

        // An Int32 and an Int64 never carry the same value - the narrowing funnels see to that -
        // but the comparison is still written out rather than answered false, so that a long that
        // leaked in from a CLR method returning Int64 compares by value like any other Integer.
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(long self, int other) {
            return self == other;
        }

        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(int self, long other) {
            return self == other;
        }

        #endregion
    }
}

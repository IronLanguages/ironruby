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
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using System.Numerics;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using Microsoft.Scripting.Generation;
using System.Runtime.CompilerServices;

namespace IronRuby.Builtins {
    /// <summary>
    /// Mixed-in all .NET numeric primitive types that can be widened to 32 bit signed integer 
    /// (byte, sbyte, short, ushort, int). 
    /// 
    /// TODO: we might want to specialize some of the methods to preserve the exact type if possible (like adding byte and byte).
    /// </summary>
    [RubyModule("Integer", DefineIn = typeof(IronRubyOps.Clr))]
    public static partial class ClrInteger {
        public static readonly object Zero = ScriptingRuntimeHelpers.Int32ToObject(0);
        public static readonly object One = ScriptingRuntimeHelpers.Int32ToObject(1);
        public static readonly object MinusOne = ScriptingRuntimeHelpers.Int32ToObject(-1);

        internal static object/*!*/ MinusMinValue() {
            return -(long)Int32.MinValue;
        }

        /// <summary>
        /// The canonical boxed form of an integral value: an Int32 when it fits in one, otherwise
        /// the Int64 itself. Nothing in the range of a long is ever handed out as a BigInteger, so
        /// each value has exactly one representation and #hash / #eql? need no cross-type care.
        /// </summary>
        public static object/*!*/ Narrow(long value) {
            return (value >= Int32.MinValue && value <= Int32.MaxValue) ? ScriptingRuntimeHelpers.Int32ToObject((Int32)value) : (object)value;
        }

        #region Bitwise Operators

        #region <<

        /// <summary>
        /// Returns the value after shifting to the left (right if count is negative) the value in self by other bits.
        /// (where other is Fixnum)
        /// </summary>
        /// <returns>The value after the shift</returns>
        /// <remarks>Converts to Bignum if the result cannot fit into Fixnum</remarks>
        [RubyMethod("<<")]
        public static object/*!*/ LeftShift(int self, int shift) {
            if (self == 0) {
                return Zero;
            }

            if (shift == 0) {
                return self;
            }

            if (shift < 0) {
                if (shift == Int32.MinValue) {
                    // Negating this one overflows, and the shift is further than any value
                    // reaches anyway.
                    return ShiftedOutOfRange(Math.Sign(self), false);
                } else {
                    return RightShift(self, -shift);
                }
            }
                
            // If 'self' has more than '31 - other' significant digits it will overflow:
            if (shift >= 31 || (self & ~((1 << (31 - shift)) - 1)) != 0) {
                return Protocols.Normalize(((BigInteger)self) << shift);
            }

            return self << shift;
        }

        #endregion

        #region >>

        /// <summary>
        /// Returns the value after shifting to the right (left if count is negative) the value in self by other bits.
        /// (where other is Fixnum)
        /// </summary>
        /// <returns>The value after the shift</returns>
        /// <remarks>Converts to Bignum if the result cannot fit into Fixnum</remarks>
        [RubyMethod(">>")]
        public static object/*!*/ RightShift(int self, int shift) {
            if (shift < 0) {
                if (shift == Int32.MinValue) {
                    return ShiftedOutOfRange(Math.Sign(self), true);
                } else {
                    return LeftShift(self, -shift);
                }
            } else if (shift == 0) {
                return self;
            } else if (shift >= 32) {
                return self < 0 ? MinusOne : Zero;
            } else {
                return self >> shift;
            }
        }

        /// <summary>
        /// A shift wider than any machine word still has an answer. Shifting towards the least
        /// significant end for ever leaves 0, or -1 for a negative value, since the sign bit keeps
        /// arriving; zero stays zero whichever way it goes. Only a non-zero value shifted that far
        /// the *other* way has nowhere to go, and MRI calls that a RangeError rather than trying
        /// to allocate the result.
        /// </summary>
        internal static object/*!*/ ShiftedOutOfRange(int sign, bool towardsMoreSignificantBits) {
            if (sign == 0) {
                return Zero;
            }
            if (towardsMoreSignificantBits) {
                throw RubyExceptions.CreateRangeError("shift width too big");
            }
            return sign < 0 ? MinusOne : Zero;
        }

        [RubyMethod("<<")]
        public static object/*!*/ LeftShift(int self, [NotNull]BigInteger/*!*/ shift) {
            int small;
            if (shift.AsInt32(out small)) {
                return LeftShift(self, small);
            }
            return ShiftedOutOfRange(Math.Sign(self), shift.Sign > 0);
        }

        [RubyMethod(">>")]
        public static object/*!*/ RightShift(int self, [NotNull]BigInteger/*!*/ shift) {
            int small;
            if (shift.AsInt32(out small)) {
                return RightShift(self, small);
            }
            return ShiftedOutOfRange(Math.Sign(self), shift.Sign < 0);
        }

        #endregion

        #region []

        /// <summary>
        /// Returns the value of the bit at the indexth bit position of self, where index is Fixnum
        /// </summary>
        /// <example>
        /// <code>
        ///   a = 9**15
        ///   50.downto(0) do |n|
        ///     print a[n]
        ///   end
        /// </code>
        /// produces: 
        /// <code>
        ///   000101110110100000111000011110010100111100010111001
        /// </code>
        /// </example>
        /// <returns>indexth bit in the (assumed) binary representation of self, where self[0] is the least significant bit.</returns>
        /// <remarks>Since representation is supposed to be 2s complement, we return always 1 if self is negative and index is greater than most signifcant bit in BigInteger</remarks>
        [RubyMethod("[]")]
        public static int Bit(int self, [DefaultProtocol]int index) {
            if (index < 0) {
                return 0;
            }
            if (index > 32) {
                return self < 0 ? 1 : 0;
            }
            return (self & (1 << index)) != 0 ? 1 : 0;
        }

        /// <summary>
        /// Returns the value of the bit at the indexth bit position of self, where index is Bignum
        /// </summary>
        /// <returns>
        /// 0 if index is negative or self is positive
        /// 1 otherwise
        /// </returns>
        /// <remarks>
        /// Since representation is supposed to be 2s complement and index must be extremely big,
        /// we asssume we can always return 1 if self is negative and 0 otherwise</remarks>
        [RubyMethod("[]")]
        public static int Bit(int self, [NotNull]BigInteger/*!*/ index) {
            // BigIntegers as indexes are always going to be outside the range.
            if (index.IsNegative() || self >= 0) {
                return 0;
            } else {
                return 1;
            }
        }

        #endregion

        #region ^

        /// <summary>
        /// Performs bitwise XOR on self and other
        /// </summary>
        [RubyMethod("^")]
        public static object/*!*/ BitwiseXor(int self, int other) {
            return self ^ other;
        }

        /// <summary>
        /// Performs bitwise XOR on self and other
        /// </summary>
        [RubyMethod("^")]
        public static object/*!*/ BitwiseXor(int self, [NotNull]BigInteger/*!*/ other) {
            return other ^ self;
        }

        #endregion

        #region &

        /// <summary>
        /// Performs bitwise AND on self and other, where other is Fixnum
        /// </summary>
        [RubyMethod("&")]
        public static int BitwiseAnd(int self, int other) {
            return self & other;
        }

        /// <summary>
        /// Performs bitwise AND on self and other, where other is Bignum
        /// </summary>
        [RubyMethod("&")]
        public static object/*!*/ BitwiseAnd(int self, [NotNull]BigInteger/*!*/ other) {
            BigInteger result = other & self;
            int ret;
            if (result.AsInt32(out ret)) {
                return ret;
            } else {
                return result;
            }
        }

        #endregion

        #region |

        /// <summary>
        /// Performs bitwise OR on self and other
        /// </summary>
        [RubyMethod("|")]
        public static int BitwiseOr(int self, int other) {
            return self | other;
        }

        /// <summary>
        /// Performs bitwise OR on self and other
        /// </summary>
        [RubyMethod("|")]
        public static object/*!*/ BitwiseOr(int self, [NotNull]BigInteger/*!*/ other) {
            BigInteger result = other | self;
            int ret;
            if (result.AsInt32(out ret)) {
                return ret;
            } else {
                return result;
            }
        }


        /// <summary>
        /// Anything that is not already an Integer is coerced, not converted: MRI asks the operand
        /// for #coerce and insists on getting Integers back, so a Float is a TypeError and #to_int
        /// is never called - a Rational does not get quietly truncated into a bit pattern.
        /// </summary>
        [RubyMethod("&")]
        public static object BitwiseAnd(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite,
            object/*!*/ self, object other) {
            return Protocols.CoerceAndApplyBitwise(coercionStorage, binaryOpSite, "&", self, other);
        }

        [RubyMethod("|")]
        public static object BitwiseOr(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite,
            object/*!*/ self, object other) {
            return Protocols.CoerceAndApplyBitwise(coercionStorage, binaryOpSite, "|", self, other);
        }

        [RubyMethod("^")]
        public static object Xor(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite,
            object/*!*/ self, object other) {
            return Protocols.CoerceAndApplyBitwise(coercionStorage, binaryOpSite, "^", self, other);
        }

        #endregion

        #region ~

        /// <summary>
        /// Returns the ones complement of self; a number where each bit is flipped. 
        /// </summary>
        [RubyMethod("~")]
        public static int OnesComplement(int self) {
            return ~self;
        }

        #endregion

        #endregion

        #region Arithmetic Operators
        
        #region *

        /// <summary>
        /// Returns self multiplied by other, where other is Fixnum or Bignum.
        /// </summary>
        /// <returns>
        /// Returns either Fixnum or Bignum if the result is too large for Fixnum.
        /// </returns>
        [RubyMethod("*")]
        public static object/*!*/ Multiply(int self, int other) {
            return Narrow((long)self * other);
        }

        /// <summary>
        /// Returns self multiplied by other, where other is Fixnum or Bignum.
        /// </summary>
        /// <returns>
        /// Returns either Fixnum or Bignum if the result is too large for Fixnum.
        /// </returns>
        [RubyMethod("*")]
        public static object/*!*/ Multiply(int self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize(BigInteger.Multiply(self, other));
        }

        /// <summary>
        /// Returns self multiplied by other, where other is Float.
        /// </summary>
        /// <returns>
        /// Returns a Float
        /// </returns>
        /// <remarks>
        /// Converts self to a float and multiplies the two floats directly.
        /// </remarks>
        [RubyMethod("*")]
        public static double Multiply(int self, double other) {
            return (double)self * other;
        }

        #endregion

        #region **

        /// <summary>
        /// Raises self to the other power, which may be negative.
        /// </summary>
        /// <returns>
        /// Integer (Bignum or Fixnum) if other is positive
        /// Float otherwise.
        /// </returns>
        [RubyMethod("**")]
        public static object/*!*/ Power(int self, int other) {
            if (other >= 0) {
                return ClrInteger.PowerNonNegative(self, other);
            } else if (self == 1) {
                return One;
            } else {
                return Math.Pow(self, other);
            }
        }

        /// <summary>
        /// Raises self to the other power, which may be negative or fractional.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("**")]
        public static double Power(int self, double other) {
            return Math.Pow(self, other);
        }

        /// <summary>
        /// A Bignum exponent: 0, 1 and -1 have an answer, anything else is too large to compute
        /// (an ArgumentError, as in MRI 3.4 and later).  Without this overload the binder could
        /// not choose between Bignum#** and the coercing fallback.
        /// </summary>
        [RubyMethod("**")]
        public static object Power(RubyContext/*!*/ context, int self, [NotNull]BigInteger/*!*/ other) {
            return Power(context, (BigInteger)self, other);
        }

        #endregion

        #region +

        /// <summary>
        /// Returns self added to other, where other is Fixnum.
        /// </summary>
        /// <returns>Fixnum or Bignum if result is too large for Fixnum.</returns>
        [RubyMethod("+")]
        public static object Add(int self, int other) {
            return Narrow((long)self + other);
        }

        [RubyMethod("+")]
        public static object/*!*/ Add(int self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize((BigInteger)self + other);
        }

        /// <summary>
        /// Returns self added to other, where other is Float
        /// </summary>
        /// <returns>Float</returns>
        /// <remarks>
        /// Converts self to Float and then adds the two floats directly.
        /// </remarks>
        [RubyMethod("+")]
        public static double Add(int self, double other) {
            return (double)self + other;
        }

        #endregion

        #region -

        /// <summary>
        /// Subtracts other from self (i.e. self - other), where other is Fixnum.
        /// </summary>
        /// <returns>Fixnum, or Bignum if result is too large for Fixnum.</returns>
        [RubyMethod("-")]
        public static object/*!*/ Subtract(int self, int other) {
            return Narrow((long)self - other);
        }

        [RubyMethod("-")]
        public static object/*!*/ Subtract(int self, BigInteger other) {
            return Protocols.Normalize((BigInteger)self - other);
        }

        /// <summary>
        /// Subtracts other from self (i.e. self - other), where other is Float.
        /// </summary>
        /// <returns>Float</returns>
        /// <remarks>
        /// Converts self to a double then executes the subtraction directly.
        /// </remarks>
        [RubyMethod("-")]
        public static double Subtract(int self, double other) {
            return (double)self - other;
        }

        #endregion

        #region -@

        [RubyMethod("-@")]
        public static object/*!*/ Minus(int self) {
            return self != Int32.MinValue ? -self : MinusMinValue();
        }

        #endregion

        #region /, div, fdiv, %, modulo, divmod

        /// <summary>
        /// Divides self by other, where other is a Fixnum.
        /// Aliased as / and div
        /// </summary>
        /// <returns>Fixnum, or Bignum if result is too large for Fixnum.</returns>
        /// <remarks>
        /// Since both operands are Integer, the result returned is Integer, rounded toward -Infinity.
        /// </remarks>
        [RubyMethod("/"), RubyMethod("div")]
        public static object/*!*/ Divide(int self, int other) {
            if (self == Int32.MinValue && other == -1) {
                return MinusMinValue();
            }
            return MathUtils.FloorDivideUnchecked(self, other);
        }

        [RubyMethod("fdiv", Compatibility = RubyCompatibility.Ruby19)]
        public static double FDiv([NotNull]BigInteger/*!*/ self, double other) {
            return Protocols.ToDouble(self) / other;
        }

        /// <summary>
        /// Returns self / other as a Float, where other is neither an Integer nor a Float.
        /// </summary>
        /// <remarks>
        /// Coerce and retry, like every other binary operator here.  The overload this replaces
        /// took [DefaultProtocol]int, so it answered Rational(3,2) by calling to_int on it and
        /// dividing by 1; CRuby coerces, so 1.fdiv(Rational(3,2)) is 0.666... and an argument
        /// that is not a number at all gets "String can't be coerced into Integer" rather than
        /// "no implicit conversion of String into Integer".
        /// </remarks>
        [RubyMethod("fdiv", Compatibility = RubyCompatibility.Ruby19)]
        public static object FDiv(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite,
            object/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "fdiv", self, other);
        }
        /// <summary>
        /// Returns self modulo other, where other is Fixnum.  See <see cref="FloatOps.Divmod"/> for more information.
        /// </summary>
        [RubyMethod("%"), RubyMethod("modulo")]
        public static int Modulo(int self, int other) {
            return MathUtils.FloorRemainder(self, other);
        }

        // Integer#% by a Float zero is a ZeroDivisionError, where Float#% - which the coercion
        // would otherwise end up in - answers NaN.
        [RubyMethod("%"), RubyMethod("modulo")]
        public static double Modulo(int self, double other) {
            if (other == 0.0) {
                throw new DivideByZeroException("divided by 0");
            }
            return ClrFloat.Modulo((double)self, other);
        }

        /// <summary>
        /// Returns an array containing the quotient and modulus obtained by dividing self by other.
        /// </summary>
        /// <returns>RubyArray of the form: [div, mod], where div is Integer</returns>
        /// <remarks>
        /// If q, r = x.divmod(y), then 
        ///    q = floor(float(x)/float(y))
        ///    x = q*y + r
        /// The quotient is rounded toward -infinity, as shown in the following table: 
        ///
        ///   a    |  b  |  a.divmod(b)  |   a/b   | a.modulo(b) | a.remainder(b)
        ///  ------+-----+---------------+---------+-------------+---------------
        ///   13   |  4  |   3,    1     |   3     |    1        |     1
        ///  ------+-----+---------------+---------+-------------+---------------
        ///   13   | -4  |  -4,   -3     |  -4     |   -3        |     1
        ///  ------+-----+---------------+---------+-------------+---------------
        ///  -13   |  4  |  -4,    3     |  -4     |    3        |    -1
        ///  ------+-----+---------------+---------+-------------+---------------
        ///  -13   | -4  |   3,   -1     |   3     |   -1        |    -1
        ///  ------+-----+---------------+---------+-------------+---------------
        ///   11.5 |  4  |   2,    3.5   |   2.875 |    3.5      |     3.5
        ///  ------+-----+---------------+---------+-------------+---------------
        ///   11.5 | -4  |  -3,   -0.5   |  -2.875 |   -0.5      |     3.5
        ///  ------+-----+---------------+---------+-------------+---------------
        ///  -11.5 |  4  |  -3,    0.5   |  -2.875 |    0.5      |    -3.5
        ///  ------+-----+---------------+---------+-------------+---------------
        ///  -11.5 | -4  |   2    -3.5   |   2.875 |   -3.5      |    -3.5
        /// </remarks>
        [RubyMethod("divmod")]
        public static RubyArray/*!*/ DivMod(int self, int other) {
            return RubyOps.MakeArray2(Divide(self, other), Modulo(self, other));
        }

        #endregion

        #region abs

        /// <summary>
        /// Returns the absolute value of self.
        /// </summary>
        /// <returns>Fixnum</returns>
        [RubyMethod("abs")]
        [RubyMethod("magnitude")]
        public static object/*!*/ Abs(int self) {
            return self >= 0 ? self : self != Int32.MinValue ? -self : MinusMinValue();
        }

        #endregion

        #region zero?

        /// <summary>
        /// Returns true if self is zero.
        /// </summary>
        /// <returns>True if self is zero, false otherwise.</returns>
        [RubyMethod("zero?")]
        public static bool IsZero(int self) {
            return self == 0;
        }

        [RubyMethod("zero?")]
        public static bool IsZero([NotNull]BigInteger/*!*/ self) {
            return self.IsZero();
        }

        #endregion

        #endregion

        #region Comparison Operators

        #region <

        /// <summary>
        /// Returns true if the value of self is less than other, where other is Fixnum.
        /// </summary>
        /// <returns>True or false</returns>
        [RubyMethod("<")]
        public static bool LessThan(int self, int other) {
            return self < other;
        }

        /// <summary>
        /// Returns true if the value of self is less than other, where other is not Fixnum.
        /// </summary>
        /// <returns>True or false</returns>
        /// <remarks>
        /// Self is first coerced by other and then the &lt; operator is invoked on the coerced self.
        /// </remarks>
        [RubyMethod("<")]
        public static bool LessThan(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, object/*!*/ self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, "<", self, other);
        }

        #endregion

        #region <=

        /// <summary>
        /// Returns true if the value of self is less than or equal to other, where other is Fixnum.
        /// </summary>
        /// <returns>True or false</returns>
        [RubyMethod("<=")]
        public static bool LessThanOrEqual(int self, int other) {
            return self <= other;
        }

        /// <summary>
        /// Returns true if the value of self is less than or equal to other, where other is not Fixnum.
        /// </summary>
        /// <returns>True or false</returns>
        /// <remarks>
        /// Self is first coerced by other and then the &lt;= operator is invoked on the coerced self.
        /// </remarks>
        [RubyMethod("<=")]
        public static bool LessThanOrEqual(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, object/*!*/ self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, "<=", self, other);
        }

        #endregion

        #region <=>

        /// <summary>
        /// Comparison: Returns -1, 0, or +1 depending on whether self is less than, equal to, or greater than other, where other is Fixnum.
        /// </summary>
        /// <returns>
        /// -1 if self is less than other
        /// 0 if self is equal to other
        /// +1 if self is greater than other
        /// nil if self cannot be compared to other
        /// </returns>
        [RubyMethod("<=>")]
        public static int Compare(int self, int other) {
            return self.CompareTo(other);
        }

        /// <summary>
        /// Comparison: Returns -1, 0, or +1 depending on whether self is less than, equal to, or greater than other, where other is not Fixnum.
        /// </summary>
        /// <returns>
        /// -1 if self is less than other
        /// 0 if self is equal to other
        /// +1 if self is greater than other
        /// nil if self cannot be compared to other
        /// </returns>
        /// <remarks>
        /// Self is first coerced by other and then the &lt;=&gt; operator is invoked on the coerced self.
        /// </remarks>
        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, object/*!*/ self, object other) {
            return Protocols.CoerceAndCompare(coercionStorage, comparisonStorage, self, other);
        }

        #endregion

        #region ==

        /// <summary>
        /// Test whether self is numerically equivalent to other.  (Does not require type equivalence).
        /// </summary>
        /// <returns>True if self and other are numerically equal.</returns>
        /// <remarks>
        /// Since other is Fixnum here, we just test for direct equality.
        /// </remarks>
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(int self, int other) {
            return self == other;
        }

        /// <summary>
        /// Test whether self is numerically equivalent to other.  (Does not require type equivalence).
        /// </summary>
        /// <returns>True if self and other are numerically equal.</returns>
        /// <remarks>
        /// Since other is not Fixnum, we turn the equivalence check around,
        /// i.e. call other == self
        /// </remarks>
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(BinaryOpStorage/*!*/ equals, object/*!*/ self, object other) {
            // If self == other doesn't work then try other == self
            return Protocols.IsEqual(equals, other, self);
        }

        #endregion
        
        #region >

        /// <summary>
        /// Returns true if the value of self is greater than other, where other is Fixnum.
        /// </summary>
        /// <returns>True or false</returns>
        [RubyMethod(">")]
        public static bool GreaterThan(int self, int other) {
            return self > other;
        }

        /// <summary>
        /// Returns true if the value of self is greater than other, where other is not Fixnum.
        /// </summary>
        /// <returns>True or false</returns>
        /// <remarks>
        /// Self is first coerced by other and then the &gt; operator is invoked on the coerced self.
        /// </remarks>
        [RubyMethod(">")]
        public static bool GreaterThan(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, object/*!*/ self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, ">", self, other);
        }

        #endregion

        #region >=

        /// <summary>
        /// Returns true if the value of self is greater than or equal to other, where other is Fixnum.
        /// </summary>
        /// <returns>True or false</returns>
        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(int self, int other) {
            return self >= other;
        }

        /// <summary>
        /// Returns true if the value of self is greater than or equal to other, where other is not Fixnum.
        /// </summary>
        /// <returns>True or false</returns>
        /// <remarks>
        /// Self is first coerced by other and then the &gt;= operator is invoked on the coerced self.
        /// </remarks>
        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage,
            object/*!*/ self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, ">=", self, other);
        }

        #endregion

        #endregion

        #region Conversion Methods

        #region to_f

        /// <summary>
        /// Convert self to Float.
        /// </summary>
        /// <returns>Float version of self</returns>
        [RubyMethod("to_f")]
        public static double ToFloat(int self) {
            return (double)self;
        }

        #endregion

        #region to_s

        /// <summary>
        /// Returns a string representing the value of self using base 10.
        /// </summary>
        /// <returns>MutableString</returns>
        /// <example>12345.to_s => "12345"</example>
        [RubyMethod("to_s")]
        [RubyMethod("inspect")]
        public static object ToString(object/*!*/ self) {
            return MutableString.CreateAscii(self.ToString());
        }

        #endregion

        #endregion
    }
}
 

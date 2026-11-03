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
using System.Diagnostics;
using IronRuby.Runtime;
using Microsoft.Scripting.Generation;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Globalization;

namespace IronRuby.Builtins {

    /// <summary>
    /// Mixed-in all .NET numeric primitive types that cannot be widened to 32 bit signed integer.
    /// (uint, long, ulong, BigInteger). 
    /// </summary>
    // Merged into the Integer class: Ruby 2.4 unified Fixnum and Bignum into a
    // single Integer, so the int-self and BigInteger-self operations have to live
    // in one trait type to end up as overloads of one method rather than two
    // definitions where the second silently replaces the first.
    public static partial class ClrInteger {
        #region Arithmetic Operators

        #region -@

        /// <summary>
        /// Unary minus (returns a new Bignum whose value is 0-self)
        /// </summary>
        /// <returns>0 minus self</returns>
        /// <remarks>Normalizes to a Fixnum if necessary</remarks>
        [RubyMethod("-@")]
        public static object Negate(BigInteger/*!*/ self) {
            return Protocols.Normalize(BigInteger.Negate(self));
        }

        #endregion

        #region abs

        /// <summary>
        /// Returns the absolute value of self
        /// </summary>
        /// <returns>self if self >= 0; -self if self &lt; 0</returns>
        /// <remarks>Normalizes to a Fixnum if necessary</remarks>
        [RubyMethod("abs")]
        [RubyMethod("magnitude")]
        public static object Abs(BigInteger/*!*/ self) {
            return Protocols.Normalize(self.Abs());
        }

        #endregion

        #region +

        /// <summary>
        /// Adds self and other, where other is Bignum or Fixnum
        /// </summary>
        /// <returns>self + other</returns>
        /// <remarks>Normalizes to a Fixnum if necessary</remarks>
        [RubyMethod("+")]
        public static object Add(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize(self + other);
        }

        /// <summary>
        /// Adds self and other, where other is Bignum or Fixnum
        /// </summary>
        /// <returns>self + other</returns>
        /// <remarks>Normalizes to a Fixnum if necessary</remarks>
        [RubyMethod("+")]
        public static object Add(BigInteger/*!*/ self, int other) {
            return Protocols.Normalize(self + other);
        }

        /// <summary>
        /// Adds self and other, where other is Float
        /// </summary>
        /// <returns>self + other as Float</returns>
        [RubyMethod("+")]
        public static object Add(BigInteger/*!*/ self, double other) {
            return Protocols.ToDouble(self) + other;
        }

        /// <summary>
        /// Adds self and other, where other is not a Float, Fixnum or Bignum
        /// </summary>
        /// <returns>self + other</returns>
        /// <remarks>Coerces self and other using other.coerce(self) then dynamically invokes +</remarks>
        [RubyMethod("+")]
        public static object Add(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, object/*!*/ self, object other) {
            if (IsFixnumWithComplex(coercionStorage.Context, self, other)) {
                var site = binaryOpSite.GetCallSite("+");
                return site.Target(site, other, self);
            }
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "+", self, other);
        }

        /// <summary>
        /// numeric.c's fix_plus and fix_mul hand a Complex operand straight to Complex#+ and
        /// Complex#* (with the Fixnum as the argument) instead of coercing, which keeps an exact
        /// part exact: 7 * Complex(1/2r, 3) is ((7/2)+21i), where the coerced product
        /// Complex(7, 0) * Complex(1/2r, 3) is ((7/2)+(21/1)*i). A Bignum coerces as usual.
        /// </summary>
        private static bool IsFixnumWithComplex(RubyContext/*!*/ context, object self, object other) {
            const long FixnumMax = (1L << 62) - 1, FixnumMin = -(1L << 62);
            if (other == null || !(self is int || (self is long && (long)self >= FixnumMin && (long)self <= FixnumMax))) {
                return false;
            }
            return context.GetClassOf(other).Name == "Complex";
        }

        #endregion

        #region -

        /// <summary>
        /// Subtracts other from self, where other is Bignum or Fixnum
        /// </summary>
        /// <returns>self - other</returns>
        /// <remarks>Normalizes to a Fixnum if necessary</remarks>
        [RubyMethod("-")]
        public static object Subtract(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize(self - other);
        }

        /// <summary>
        /// Subtracts other from self, where other is Bignum or Fixnum
        /// </summary>
        /// <returns>self - other</returns>
        /// <remarks>Normalizes to a Fixnum if necessary</remarks>
        [RubyMethod("-")]
        public static object Subtract(BigInteger/*!*/ self, int other) {
            return Protocols.Normalize(self - other);
        }

        /// <summary>
        /// Subtracts other from self, where other is Float
        /// </summary>
        /// <returns>self - other as Float</returns>
        [RubyMethod("-")]
        public static object Subtract(BigInteger/*!*/ self, double other) {
            return Protocols.ToDouble(self) - other;
        }

        /// <summary>
        /// Subtracts other from self, where other is not a Float, Fixnum or Bignum
        /// </summary>
        /// <returns>self - other</returns>
        /// <remarks>Coerces self and other using other.coerce(self) then dynamically invokes -</remarks>
        [RubyMethod("-")]
        public static object Subtract(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, object/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "-", self, other);
        }

        #endregion

        #region *

        /// <summary>
        /// Multiplies self by other, where other is Bignum or Fixnum
        /// </summary>
        /// <returns>self * other</returns>
        /// <remarks>Normalizes to a Fixnum if necessary</remarks>
        [RubyMethod("*")]
        public static object Multiply(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize(self * other);
        }

        /// <summary>
        /// Multiplies self by other, where other is Bignum or Fixnum
        /// </summary>
        /// <returns>self * other</returns>
        /// <remarks>Normalizes to a Fixnum if necessary</remarks>
        [RubyMethod("*")]
        public static object Multiply(BigInteger/*!*/ self, int other) {
            return Protocols.Normalize(self * other);
        }

        /// <summary>
        /// Multiplies self by other, where other is Float
        /// </summary>
        /// <returns>self * other as Float</returns>
        [RubyMethod("*")]
        public static object Multiply(BigInteger/*!*/ self, double other) {
            return Protocols.ToDouble(self) * other;
        }

        /// <summary>
        /// Multiplies self by other, where other is not a Float, Fixnum or Bignum
        /// </summary>
        /// <returns>self * other</returns>
        /// <remarks>Coerces self and other using other.coerce(self) then dynamically invokes *</remarks>
        [RubyMethod("*")]
        public static object Multiply(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, object/*!*/ self, object other) {
            if (IsFixnumWithComplex(coercionStorage.Context, self, other)) {
                var site = binaryOpSite.GetCallSite("*");
                return site.Target(site, other, self);
            }
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "*", self, other);
        }

        #endregion

        #region /, div, fdiv

        /// <summary>
        /// Divides self by other, where other is Bignum or Fixnum
        /// </summary>
        /// <returns>self / other</returns>
        /// <remarks>Uses DivMod to do the division (directly).  Normalizes to a Fixnum if necessary</remarks>
        [RubyMethod("/"), RubyMethod("div")]
        public static object Divide(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return DivMod(self, other)[0];
        }

        /// <summary>
        /// Divides self by other, where other is Bignum or Fixnum
        /// </summary>
        /// <returns>self / other</returns>
        /// <remarks>Uses DivMod to do the division (directly).  Normalizes to a Fixnum if necessary</remarks>
        [RubyMethod("/"), RubyMethod("div")]
        public static object Divide(BigInteger/*!*/ self, int other) {
            return DivMod(self, other)[0];
        }

        /// <summary>
        /// Divides self by other, where other is Float
        /// </summary>
        /// <returns>self / other as Float</returns>
        [RubyMethod("/")]
        public static object DivideOp(BigInteger/*!*/ self, double other) {
            return Protocols.ToDouble(self) / other;
        }

        /// <summary>
        /// Divides self by other, where other is Float
        /// </summary>
        /// <returns>self divided by other as Float</returns>
        [RubyMethod("div")]
        public static object Divide(BigInteger/*!*/ self, double other) {
            return DivMod(self, other)[0];
        }

        /// <summary>
        /// Divides self by other, where other is not a Float, Fixnum or Bignum
        /// </summary>
        /// <returns>self / other</returns>
        /// <remarks>Coerces self and other using other.coerce(self) then dynamically invokes /</remarks>
        [RubyMethod("/")]
        public static object Divide(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, object/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "/", self, other);
        }

        /// <summary>
        /// Divides self by other, where other is not a Float, Fixnum or Bignum
        /// </summary>
        /// <returns>self.div(other)</returns>
        /// <remarks>Coerces self and other using other.coerce(self) then dynamically invokes div</remarks>
        [RubyMethod("div")]
        public static object Div(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, object/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "div", self, other);
        }

        /// <summary>
        /// Converting each side to a double first overflows to Infinity for anything past
        /// Float::MAX, and Infinity/Infinity is NaN - so a division whose answer is an ordinary
        /// number, like (10**344).fdiv(9 * 10**342), came back NaN. Shift the numerator instead so
        /// that the quotient lands in double's range with bits to spare, and put the shift back
        /// afterwards.
        /// </summary>
        [RubyMethod("fdiv", Compatibility = RubyCompatibility.Ruby19)]
        public static double FDiv(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            if (other.IsZero()) {
                // Dividing by zero answers what a Float would: a signed infinity, or NaN for 0/0.
                return self.Sign / 0.0;
            }
            if (self.IsZero()) {
                return (other.Sign > 0) ? 0.0 : -0.0;
            }

            long magnitude = (long)self.GetBitLength() - (long)other.GetBitLength();
            if (magnitude > 1100) {
                return (self.Sign == other.Sign) ? Double.PositiveInfinity : Double.NegativeInfinity;
            }
            if (magnitude < -1100) {
                return (self.Sign == other.Sign) ? 0.0 : -0.0;
            }

            // 128 bits of quotient is far more than the 53 a double keeps. Worked out on the
            // magnitudes, with the sign put back at the end, so that the truncation the integer
            // division does is always towards zero on both sides of it.

            BigInteger numerator = BigInteger.Abs(self);
            BigInteger denominator = BigInteger.Abs(other);

            int shift = (int)(128 - magnitude);
            if (shift >= 0) {
                numerator <<= shift;
            } else {
                denominator <<= -shift;
            }

            BigInteger remainder;
            BigInteger quotient = BigInteger.DivRem(numerator, denominator, out remainder);
            if (!remainder.IsZero()) {
                // The sticky bit: the quotient was cut short, so it must not be left sitting
                // exactly on a halfway point that the rounding below would then resolve wrongly.
                quotient |= BigInteger.One;
            }

            double result = Protocols.ScaleToDouble(quotient, -shift);
            return (self.Sign == other.Sign) ? result : -result;
        }

        #endregion

        #region **

        /// <summary>
        /// Raises self to the exponent power, where exponent is Bignum.
        /// </summary>
        /// <returns>self ** exponent as Bignum/Fixnum if exponent &gt;= 0, Float otherwise</returns>
        [RubyMethod("**")]
        public static object Power(RubyContext/*!*/ context, BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ exponent) {
            if (exponent.Sign < 0) {
                return Math.Pow(Protocols.ToDouble(self), Protocols.ToDouble(exponent));
            }
            return PowerNonNegative(self, exponent);
        }

        /// <summary>
        /// Raises self to the exponent power, where exponent is Fixnum
        /// </summary>
        /// <returns>self ** exponent</returns>
        /// <remarks>
        /// Returns Bignum or Fixnum if exponent &gt;= 0.
        /// Returns Float if exponent &lt; 0
        /// </remarks>
        [RubyMethod("**")]
        public static object Power(BigInteger/*!*/ self, int exponent) {
            // BigInteger doesn't handle negative exponents.
            if (exponent < 0) {
                return Power(self, (double)exponent);
            }
            return PowerNonNegative(self, exponent);
        }

        /// <summary>
        /// The most bits MRI 3.4+ is willing to produce from Integer#** before it gives up with
        /// an ArgumentError (16 GiB). Anything past <see cref="Int32.MaxValue"/> bits is out of
        /// reach for System.Numerics.BigInteger anyway, so that is the effective ceiling here.
        /// </summary>
        private const long PowerResultBitLimit = 16L * 1024 * 1024 * 1024;

        /// <summary>
        /// self ** exponent for exponent &gt;= 0.
        ///
        /// Refuses absurd result sizes up front the way MRI 3.4+ does, rather than spending
        /// minutes and gigabytes on a value nobody can use, and shifts instead of multiplying
        /// when |self| is a power of two - BigInteger.Pow multiplies in quadratic time, which
        /// makes even a legal result like 2 ** 40_000_000 effectively a hang.
        /// </summary>
        internal static object/*!*/ PowerNonNegative(BigInteger/*!*/ self, BigInteger/*!*/ exponent) {
            Debug.Assert(exponent.Sign >= 0);

            if (exponent.IsZero) {
                return ScriptingRuntimeHelpers.Int32ToObject(1);
            }
            if (self.IsZero || self.IsOne) {
                return ScriptingRuntimeHelpers.Int32ToObject(self.IsZero ? 0 : 1);
            }
            if (self == BigInteger.MinusOne) {
                return ScriptingRuntimeHelpers.Int32ToObject(exponent.IsEven ? 1 : -1);
            }

            // |self| >= 2 from here on, so the result needs at least
            // (bitLength(|self|) - 1) * exponent + 1 bits.
            BigInteger magnitude = BigInteger.Abs(self);
            long magnitudeBits = magnitude.GetBitLength();
            BigInteger resultBits = (BigInteger)(magnitudeBits - 1) * exponent + BigInteger.One;
            if (resultBits > PowerResultBitLimit || resultBits > Int32.MaxValue) {
                throw RubyExceptions.CreateArgumentError("exponent is too large");
            }

            bool negateResult = self.Sign < 0 && !exponent.IsEven;
            int exp = (int)exponent;

            BigInteger result;
            if ((magnitude & (magnitude - BigInteger.One)).IsZero) {
                // |self| == 2 ** (magnitudeBits - 1): a shift, linear instead of quadratic
                result = BigInteger.One << (int)((magnitudeBits - 1) * exp);
            } else {
                result = BigInteger.Pow(magnitude, exp);
            }

            return Protocols.Normalize(negateResult ? -result : result);
        }

        /// <summary>
        /// Raises self to the exponent power, where exponent is Float
        /// </summary>
        /// <returns>self ** exponent as Float</returns>
        /// <remarks>Converts self to Float (directly) then calls System.Math.Pow</remarks>
        [RubyMethod("**")]
        public static object Power(BigInteger/*!*/ self, double exponent) {
            return Math.Pow(Protocols.ToDouble(self), exponent);
        }

        /// <summary>
        /// Raises self to the exponent power, where exponent is not Fixnum, Bignum or Float
        /// </summary>
        /// <returns>self ** exponent</returns>
        /// <remarks>Coerces self and other using other.coerce(self) then dynamically invokes **</remarks>
        [RubyMethod("**")]
        public static object Power(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, object/*!*/ self, object exponent) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "**", self, exponent);
        }

        #endregion

        #region modulo, %

        /// <summary>
        /// Returns self modulo other, where other is Fixnum or Bignum.
        /// </summary>
        /// <returns>self modulo other, as Fixnum or Bignum</returns>
        /// <remarks>Calls divmod directly to get the modulus.</remarks>
        [RubyMethod("%"), RubyMethod("modulo")]
        public static object Modulo(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            RubyArray result = DivMod(self, other);
            return result[1];
        }

        /// <summary>
        /// Returns self modulo other, where other is Fixnum or Bignum.
        /// </summary>
        /// <returns>self modulo other, as Fixnum or Bignum</returns>
        /// <remarks>Calls divmod directly to get the modulus.</remarks>
        [RubyMethod("%"), RubyMethod("modulo")]
        public static object Modulo(BigInteger/*!*/ self, int other) {
            RubyArray result = DivMod(self, other);
            return result[1];
        }

        /// <summary>
        /// Returns self modulo other, where other is Float.
        /// </summary>
        /// <returns>self modulo other, as Float</returns>
        /// <remarks>Calls divmod directly to get the modulus.</remarks>
        [RubyMethod("%"), RubyMethod("modulo")]
        public static object Modulo(BigInteger/*!*/ self, double other) {
            // Integer#%, unlike Float#%, does not answer NaN for a zero divisor
            if (other == 0.0) {
                throw new DivideByZeroException("divided by 0");
            }
            RubyArray result = DivMod(self, other);
            return result[1];
        }

        /// <summary>
        /// Returns self % other, where other is not Fixnum or Bignum.
        /// </summary>
        /// <returns>self % other, as Fixnum or Bignum</returns>
        /// <remarks>Coerces self and other using other.coerce(self) then dynamically invokes %</remarks>
        // #modulo is #% under another name in MRI, so both coerce and then ask for "%".
        [RubyMethod("%"), RubyMethod("modulo")]
        public static object ModuloOp(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, object/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "%", self, other);
        }

        #endregion

        #region divmod

        /// <summary>
        /// Returns an array containing the quotient and modulus obtained by dividing self by other, where other is Fixnum or Bignum.
        /// If <code>q, r = x.divmod(y)</code>, then 
        ///     <code>q = floor(float(x)/float(y))</code>
        ///     <code>x = q*y + r</code>
        /// </summary>
        /// <returns>[self div other, self modulo other] as RubyArray</returns>
        /// <remarks>Normalizes div and mod to Fixnum as necessary</remarks>
        [RubyMethod("divmod")]
        public static RubyArray DivMod(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            BigInteger mod;
            BigInteger div = BigInteger.DivRem(self, other, out mod);
            if (self.Sign != other.Sign && !mod.IsZero()) {
                div = div - 1;
                mod = mod + other;
            }
            return RubyOps.MakeArray2(Protocols.Normalize(div), Protocols.Normalize(mod));
        }

        /// <summary>
        /// Returns an array containing the quotient and modulus obtained by dividing self by other, where other is Fixnum or Bignum.
        /// If <code>q, r = x.divmod(y)</code>, then 
        ///     <code>q = floor(float(x)/float(y))</code>
        ///     <code>x = q*y + r</code>
        /// </summary>
        /// <returns>[self div other, self modulo other] as RubyArray</returns>
        /// <remarks>Normalizes div and mod to Fixnum as necessary</remarks>
        [RubyMethod("divmod")]
        public static RubyArray DivMod(BigInteger/*!*/ self, int other) {
            return DivMod(self, (BigInteger)other);
        }

        /// <summary>
        /// Returns an array containing the quotient and modulus obtained by dividing self by other, where other is Float.
        /// If <code>q, r = x.divmod(y)</code>, then 
        ///     <code>q = floor(float(x)/float(y))</code>
        ///     <code>x = q*y + r</code>
        /// </summary>
        /// <returns>[self div other, self modulo other] as RubyArray</returns>
        /// <remarks>Normalizes div to Fixnum as necessary</remarks>
        [RubyMethod("divmod")]
        public static RubyArray DivMod(BigInteger/*!*/ self, double other) {
            if (Double.IsNaN(other)) {
                throw new FloatDomainError("NaN");
            }
            if (other == 0.0) {
                throw new DivideByZeroException("divided by 0");
            }

            double selfFloat = Protocols.ToDouble(self);
            // Ruby floors the quotient and gives the remainder the sign of the divisor;
            // C#'s % takes the sign of the dividend and truncates towards zero.  Derive
            // the remainder from % rather than from div * other, which loses every
            // significant digit once self is far outside double's exact integer range.
            BigInteger div = new BigInteger(Math.Floor(selfFloat / other));
            double mod = selfFloat % other;
            if (mod != 0.0 && (mod < 0.0) != (other < 0.0)) {
                mod += other;
            }

            return RubyOps.MakeArray2(Protocols.Normalize(div), mod);
        }

        /// <summary>
        /// Returns an array containing the quotient and modulus obtained by dividing self by other, where other is not Fixnum or Bignum.
        /// If <code>q, r = x.divmod(y)</code>, then 
        ///     <code>q = floor(float(x)/float(y))</code>
        ///     <code>x = q*y + r</code>
        /// </summary>
        /// <returns>Should return [self div other, self modulo other], but the divmod implementation is free to return an arbitrary object.</returns>
        /// <remarks>Coerces self and other using other.coerce(self) then dynamically invokes divmod</remarks>
        [RubyMethod("divmod")]
        public static object DivMod(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, object/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "divmod", self, other);
        }

        #endregion

        #region remainder

        /// <summary>
        /// Returns the remainder after dividing self by other, where other is Fixnum or Bignum.
        /// </summary>
        /// <example>
        /// -1234567890987654321.remainder(13731)      #=> -6966
        /// </example>
        /// <returns>Fixnum or Bignum</returns>
        [RubyMethod("remainder")]
        public static object Remainder(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            BigInteger remainder;
            BigInteger.DivRem(self, other, out remainder);
            return Protocols.Normalize(remainder);
        }

        /// <summary>
        /// Returns the remainder after dividing self by other, where other is Fixnum or Bignum.
        /// </summary>
        /// <example>
        /// -1234567890987654321.remainder(13731)      #=> -6966
        /// </example>
        /// <returns>Fixnum or Bignum</returns>
        [RubyMethod("remainder")]
        public static object Remainder(BigInteger/*!*/ self, int other) {
            BigInteger remainder;
            BigInteger.DivRem(self, other, out remainder);
            return Protocols.Normalize(remainder);
        }

        /// <summary>
        /// Returns the remainder after dividing self by other, where other is Float.
        /// </summary>
        /// <example>
        /// -1234567890987654321.remainder(13731.24)   #=> -9906.22531493148
        /// </example>
        /// <returns>Float</returns>
        [RubyMethod("remainder")]
        public static double Remainder(BigInteger/*!*/ self, double other) {
            if (other == 0.0) {
                throw new DivideByZeroException("divided by 0");
            }
            return Protocols.ToDouble(self) % other;
        }

        /// <summary>
        /// Returns the remainder after dividing self by other, where other is not Fixnum or Bignum.
        /// </summary>
        /// <example>
        /// -1234567890987654321.remainder(13731)      #=> -6966
        /// </example>
        /// <returns>Fixnum or Bignum</returns>
        /// <remarks>Coerces self and other using other.coerce(self) then dynamically invokes remainder</remarks>
        [RubyMethod("remainder")]
        public static object Remainder(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, object/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "remainder", self, other);
        }

        #endregion

        #endregion

        #region Comparisons

        // Bignum used to inherit <, <=, > and >= from Comparable, because the Bignum
        // class carried no definitions of its own.  Integer does carry them (they came
        // from the old Fixnum class), and their catch-all overload coerces and retries,
        // which for two integers is an infinite recursion.  Define the numeric cases.
        #region <, <=, >, >=

        [RubyMethod("<")]
        public static bool LessThan(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return self < other;
        }

        [RubyMethod("<")]
        public static bool LessThan(BigInteger/*!*/ self, int other) {
            return self < (BigInteger)other;
        }

        [RubyMethod("<")]
        public static bool LessThan(RubyContext/*!*/ context, BigInteger/*!*/ self, double other) {
            int? c = CompareToFloat(self, other);
            return c.HasValue && c.Value < 0;
        }

        [RubyMethod("<=")]
        public static bool LessThanOrEqual(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return self <= other;
        }

        [RubyMethod("<=")]
        public static bool LessThanOrEqual(BigInteger/*!*/ self, int other) {
            return self <= (BigInteger)other;
        }

        [RubyMethod("<=")]
        public static bool LessThanOrEqual(RubyContext/*!*/ context, BigInteger/*!*/ self, double other) {
            int? c = CompareToFloat(self, other);
            return c.HasValue && c.Value <= 0;
        }

        [RubyMethod(">")]
        public static bool GreaterThan(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return self > other;
        }

        [RubyMethod(">")]
        public static bool GreaterThan(BigInteger/*!*/ self, int other) {
            return self > (BigInteger)other;
        }

        [RubyMethod(">")]
        public static bool GreaterThan(RubyContext/*!*/ context, BigInteger/*!*/ self, double other) {
            int? c = CompareToFloat(self, other);
            return c.HasValue && c.Value > 0;
        }

        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return self >= other;
        }

        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(BigInteger/*!*/ self, int other) {
            return self >= (BigInteger)other;
        }

        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(RubyContext/*!*/ context, BigInteger/*!*/ self, double other) {
            int? c = CompareToFloat(self, other);
            return c.HasValue && c.Value >= 0;
        }

        #endregion

        #region <=>

        /// <summary>
        /// Comparison operator, where other is Bignum or Fixnum. This is the basis for the tests in Comparable.
        /// </summary>
        /// <returns>
        /// Returns -1, 0, or +1 depending on whether self is less than, equal to, or greater than other.
        /// </returns>
        [RubyMethod("<=>")]
        public static int Compare(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return BigInteger.Compare(self, other);
        }

        /// <summary>
        /// Comparison operator, where other is Bignum or Fixnum. This is the basis for the tests in Comparable.
        /// </summary>
        /// <returns>
        /// Returns -1, 0, or +1 depending on whether self is less than, equal to, or greater than other.
        /// </returns>
        [RubyMethod("<=>")]
        public static int Compare(BigInteger/*!*/ self, int other) {
            return BigInteger.Compare(self, (BigInteger)other);
        }

        /// <summary>
        /// Comparison operator, where other is Float. This is the basis for the tests in Comparable.
        /// </summary>
        /// <returns>
        /// Returns -1, 0, or +1 depending on whether self is less than, equal to, or greater than other.
        /// </returns>
        /// <remarks>
        /// MRI's rb_integer_float_cmp: compared exactly, not by rounding self to a double first.
        /// A Bignum past Float::MAX rounds to Infinity, and Infinity &lt;=&gt; Infinity is 0, so
        /// comparing that way made every large Bignum equal to Infinity and equal to every other
        /// Bignum that rounds to the same double.
        /// </remarks>
        [RubyMethod("<=>")]
        public static object Compare(RubyContext/*!*/ context, BigInteger/*!*/ self, double other) {
            int? result = CompareToFloat(self, other);
            return result.HasValue ? (object)result.Value : null;
        }

        // rb_integer_float_cmp; null for NaN. The relational operators and == go through this
        // too (rb_integer_float_eq, rb_big_cmp): rounding self to a double first made a Bignum
        // equal to every Float within half an ulp of it.
        internal static int? CompareToFloat(BigInteger/*!*/ self, double other) {
            if (Double.IsNaN(other)) {
                return null;
            }
            if (Double.IsPositiveInfinity(other)) {
                return -1;
            }
            if (Double.IsNegativeInfinity(other)) {
                return 1;
            }

            // The whole part of other is exactly representable as a BigInteger; whatever is left
            // over is a fraction in [0, 1), so it only decides a tie between the whole parts.
            double whole = Math.Floor(other);
            int result = BigInteger.Compare(self, (BigInteger)whole);
            if (result != 0) {
                return result;
            }
            return other > whole ? -1 : 0;
        }

        /// <summary>
        /// Comparison operator, where other is not Bignum, Fixnum or Float. This is the basis for the tests in Comparable.
        /// </summary>
        /// <returns>
        /// Returns -1, 0, or +1 depending on whether self is less than, equal to, or greater than other.
        /// </returns>
        /// <remarks>
        /// Dynamically invokes &lt;=&gt;.
        /// </remarks>
        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigInteger/*!*/ self, object other) {
            return Protocols.CoerceAndCompare(coercionStorage, comparisonStorage, self, other);
        }

        #endregion

        #region ==

        /// <summary>
        /// Returns true if other has the same value as self, where other is Fixnum or Bignum.
        /// Contrast this with Bignum#eql?, which requires other to be a Bignum.
        /// </summary>
        /// <returns>true or false</returns>
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return self == other;
        }

        /// <summary>
        /// Returns true if other has the same value as self, where other is Fixnum or Bignum.
        /// Contrast this with Bignum#eql?, which requires other to be a Bignum.
        /// </summary>
        /// <returns>true or false</returns>
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(BigInteger/*!*/ self, int other) {
            return self == other;
        }

        /// <summary>
        /// Returns true if other has the same value as self, where other is Float.
        /// Contrast this with Bignum#eql?, which requires other to be a Bignum.
        /// </summary>
        /// <returns>true or false</returns>
        /// <remarks>Returns false if other is NaN.</remarks>
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(RubyContext/*!*/ context, BigInteger/*!*/ self, double other) {
            int? c = CompareToFloat(self, other);
            return c.HasValue && c.Value == 0;
        }

        #endregion

        #region eql?

        #endregion

        #endregion

        #region Bitwise Operators

        #region <<

        /// <summary>
        /// Shifts self to the left by other bits (or to the right if other is negative).
        /// </summary>
        /// <returns>self &lt;&lt; other, as Bignum or Fixnum</returns>
        /// <remarks>
        /// If self is negative we have to check for running out of bits, in which case we return -1.
        /// This is because Bignum is supposed to look like it is stored in 2s complement format.
        /// </remarks>
        [RubyMethod("<<")]
        public static object/*!*/ LeftShift(BigInteger/*!*/ self, int other) {
            BigInteger result = self << other;
            result = ShiftOverflowCheck(self, result);
            return Protocols.Normalize(result);
        }

        [RubyMethod("<<")]
        public static object/*!*/ LeftShift(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            int small;
            if (other.AsInt32(out small)) {
                return LeftShift(self, small);
            }
            return ClrInteger.ShiftedOutOfRange(self.Sign, other.Sign > 0);
        }

        /// <summary>
        /// Shifts self to the left by other bits (or to the right if other is negative).
        /// </summary>
        /// <returns>self &lt;&lt; other, as Bignum or Fixnum</returns>
        /// <remarks>other is converted to an Integer by dynamically invoking self.to_int</remarks>
        [RubyMethod("<<")]
        public static object/*!*/ LeftShift(RubyContext/*!*/ context, BigInteger/*!*/ self, [DefaultProtocol]IntegerValue other) {
            return other.IsFixnum ? LeftShift(self, other.Fixnum) : LeftShift(self, other.Bignum);
        }

        #endregion

        #region >>

        /// <summary>
        /// Shifts self to the right by other bits (or to the left if other is negative).
        /// </summary>
        /// <returns>self >> other, as Bignum or Fixnum</returns>
        /// <remarks>
        /// If self is negative we have to check for running out of bits, in which case we return -1.
        /// This is because Bignum is supposed to look like it is stored in 2s complement format.
        /// </remarks>
        [RubyMethod(">>")]
        public static object/*!*/ RightShift(BigInteger/*!*/ self, int other) {
            BigInteger result = self >> other;
            result = ShiftOverflowCheck(self, result);
            return Protocols.Normalize(result);
        }

        [RubyMethod(">>")]
        public static object/*!*/ RightShift(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            int small;
            if (other.AsInt32(out small)) {
                return RightShift(self, small);
            }
            return ClrInteger.ShiftedOutOfRange(self.Sign, other.Sign < 0);
        }

        /// <summary>
        /// Shifts self to the left by other bits (or to the right if other is negative).
        /// </summary>
        /// <returns>self >> other, as Bignum or Fixnum</returns>
        /// <remarks>other is converted to an Integer by dynamically invoking self.to_int</remarks>
        [RubyMethod(">>")]
        public static object/*!*/ RightShift(RubyContext/*!*/ context, BigInteger/*!*/ self, [DefaultProtocol]IntegerValue other) {
            return other.IsFixnum ? RightShift(self, other.Fixnum) : RightShift(self, other.Bignum);
        }

        #endregion

        #region |

        [RubyMethod("|")]
        public static object/*!*/ BitwiseOr(BigInteger/*!*/ self, int other) {
            return Protocols.Normalize(self | other);
        }

        [RubyMethod("|")]
        public static object/*!*/ BitwiseOr(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize(self | other);
        }

        /// <summary>
        /// Performs bitwise or between self and other, where other is not Fixnum or Bignum. 
        /// </summary>
        /// <remarks>other is dynamically converted to an Integer by other.to_int then | is invoked dynamically. E.g. self | (index.to_int)</remarks>
        [RubyMethod("|")]
        public static object/*!*/ BitwiseOr(RubyContext/*!*/ context, BigInteger/*!*/ self, [DefaultProtocol]IntegerValue other) {
            return other.IsFixnum ? BitwiseOr(self, other.Fixnum) : BitwiseOr(self, other.Bignum);
        }

        #endregion

        #region &

        [RubyMethod("&")]
        public static object/*!*/ And(BigInteger/*!*/ self, int other) {
            return Protocols.Normalize(self & other);
        }

        [RubyMethod("&")]
        public static object/*!*/ And(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize(self & other);
        }

        /// <summary>
        /// Performs bitwise and between self and other, where other is not Fixnum or Bignum. 
        /// </summary>
        /// <remarks>other is dynamically converted to an Integer by other.to_int then "&amp;" is invoked dynamically. E.g. self &amp; (index.to_int)</remarks>
        [RubyMethod("&")]
        public static object/*!*/ And(RubyContext/*!*/ context, BigInteger/*!*/ self, [DefaultProtocol]IntegerValue other) {
            return other.IsFixnum ? And(self, other.Fixnum) : And(self, other.Bignum);
        }

        #endregion

        #region ^

        [RubyMethod("^")]
        public static object/*!*/ Xor(BigInteger/*!*/ self, int other) {
            return Protocols.Normalize(self ^ other);
        }

        [RubyMethod("^")]
        public static object/*!*/ Xor(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return Protocols.Normalize(self ^ other);
        }

        /// <summary>
        /// Performs bitwise xor between self and other, where other is not Fixnum or Bignum. 
        /// </summary>
        /// <remarks>other is dynamically converted to an Integer by other.to_int then ^ is invoked dynamically. E.g. self ^ (index.to_int)</remarks>
        [RubyMethod("^")]
        public static object/*!*/ Xor(RubyContext/*!*/ context, BigInteger/*!*/ self, [DefaultProtocol]IntegerValue other) {
            return other.IsFixnum ? Xor(self, other.Fixnum) : Xor(self, other.Bignum);
        }

        #endregion

        #region ~

        /// <summary>
        /// Performs bitwise inversion on self.
        /// </summary>
        [RubyMethod("~")]
        public static object Invert(BigInteger/*!*/ self) {
            return Protocols.Normalize(~self);
        }

        #endregion

        #region []

        /// <summary>
        /// Returns the Bit value at the reference index, where index is Fixnum
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
        public static int Bit(BigInteger/*!*/ self, [DefaultProtocol]int index) {
            // If we are outside the range then return 0 ...
            if (index < 0) return 0;

            int bytePos = index / 8;
            int bitOffset = index % 8;
            byte[] data = self.ToByteArray();

            // ... or 1 if the index is too high and BigInteger is negative.
            if (bytePos >= data.Length) return (self.Sign > 0) ? 0 : 1;

            return (data[bytePos] & (1 << bitOffset)) != 0 ? 1 : 0;
        }

        /// <summary>
        /// Returns the Bit value at the reference index, where index is Bignum
        /// </summary>
        /// <returns>
        /// 0 if index is negative or self is positive
        /// 1 otherwise
        /// </returns>
        /// <remarks>
        /// Since representation is supposed to be 2s complement and index must be extremely big,
        /// we asssume we can always return 1 if self is negative and 0 otherwise</remarks>
        [RubyMethod("[]")]
        public static int Bit(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ index) {
            // BigIntegers as indexes are always going to be outside the range.
            if (index.IsNegative() || self.IsPositive()) return 0;
            return 1;
        }

        #endregion

        #endregion

        #region Conversion methods

        #region to_f

        /// <summary>
        /// Converts self to a Float. If self doesnt fit in a Float, the result is infinity. 
        /// </summary>
        /// <returns>self as a Float</returns>
        [RubyMethod("to_f")]
        public static double ToFloat(RubyContext/*!*/ context, BigInteger/*!*/ self) {
            return Protocols.ConvertToDouble(context, self);
        }

        #endregion

        #region to_s

        /// <summary>
        /// Returns a string containing the representation of self base 10.
        /// </summary>
        [RubyMethod("to_s")]
        [RubyMethod("inspect")]
        public static MutableString/*!*/ ToString(BigInteger/*!*/ self) {
            return MutableString.CreateAscii(self.ToString());
        }

        /// <summary>
        /// Returns a string containing the representation of self base radix (2 through 36).
        /// </summary>
        /// <param name="radix">An integer between 2 and 36 inclusive</param>
        [RubyMethod("to_s")]
        [RubyMethod("inspect")]
        public static MutableString/*!*/ ToString(BigInteger/*!*/ self, int radix) {
            if (radix < 2 || radix > 36) {
                throw RubyExceptions.CreateArgumentError("invalid radix {0}", radix);
            }

            // TODO: Can we do the ToLower in BigInteger?
            return MutableString.CreateAscii(self.ToString(radix).ToLowerInvariant());
        }

        #endregion

        #region coerce

        /// <summary>
        /// Coerces two integers to each other: [other, self].
        /// </summary>
        /// <remarks>
        /// The Bignum-era pair of coerce overloads is gone - it answered [other, self] only for
        /// another Bignum and raised "can't coerce Float to Bignum" for everything else, which
        /// was written when a Fixnum could never be a Bignum.  Numeric#coerce would be enough
        /// on its own except that it decides "are these the same kind of number?" by comparing
        /// <c>GetClassOf</c>, and an int self and a BigInteger self are only the same Ruby class
        /// once Fixnum and Bignum are unified.  This overload states that directly, so a small
        /// and a large integer coerce to integers rather than to Floats; anything else falls
        /// through to Numeric's own to_f behaviour.
        /// </remarks>
        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(BigInteger/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return RubyOps.MakeArray2(Protocols.Normalize(other), Protocols.Normalize(self));
        }

        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(ConversionStorage<double>/*!*/ tof1, ConversionStorage<double>/*!*/ tof2,
            object/*!*/ self, object other) {
            return Numeric.Coerce(tof1, tof2, self, other);
        }

        #endregion

        #endregion

        #region hash

        /// <summary>
        /// Compute a hash based on the value of self. 
        /// </summary>
        [RubyMethod("hash")]
        public static int Hash(BigInteger/*!*/ self) {
            return RubyUtils.GetIntegerHashCode(self);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Test for shift overflow on negative BigIntegers
        /// </summary>
        /// <param name="self">Value before shifting</param>
        /// <param name="result">Value after shifting</param>
        /// <returns>-1 if we overflowed, otherwise result</returns>
        /// <remarks>
        /// Negative Bignums are supposed to look like they are stored in 2s complement infinite bit string, 
        /// a negative number should always have spare 1s available for on the left hand side for right shifting.
        /// E.g. 8 == ...0001000; -8 == ...1110111, where the ... means that the left hand value is repeated indefinitely.
        /// The test here checks whether we have overflowed into the infinite 1s.
        /// [Arguably this should get factored into the BigInteger class.]
        /// </remarks>
        private static BigInteger/*!*/ ShiftOverflowCheck(BigInteger/*!*/ self, BigInteger/*!*/ result) {
            if (self.IsNegative() && result.IsZero()) {
                return -1;
            }
            return result;
        }

        #endregion
    }
}

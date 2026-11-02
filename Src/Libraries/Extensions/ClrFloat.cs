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
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Scripting.Runtime;
using System.Numerics;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Generation;

namespace IronRuby.Builtins {
    /// <summary>
    /// Mixed-in .NET floating point numeric primitive types (float, double).
    /// </summary>
    [RubyModule("Float", DefineIn = typeof(IronRubyOps.Clr))]
    public static class ClrFloat {

        #region induced_from

        /// <summary>
        /// Convert value to Float, where value is Float.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static double InducedFrom(RubyModule/*!*/ self, double value) {
            return value;
        }

        /// <summary>
        /// Convert value to Float, where value is Fixnum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static object InducedFrom(UnaryOpStorage/*!*/ tofStorage, RubyModule/*!*/ self, int value) {
            var site = tofStorage.GetCallSite("to_f");
            return site.Target(site, ScriptingRuntimeHelpers.Int32ToObject(value));
        }

        /// <summary>
        /// Convert value to Float, where value is Bignum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static object InducedFrom(UnaryOpStorage/*!*/ tofStorage, RubyModule/*!*/ self, [NotNull]BigInteger/*!*/ value) {
            var site = tofStorage.GetCallSite("to_f");
            return site.Target(site, value);
        }

        /// <summary>
        /// Convert value to Float, where value is not Float, Fixnum or Bignum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static double InducedFrom(RubyModule/*!*/ self, object value) {
            throw RubyExceptions.CreateTypeError("failed to convert {0} into Float", self.Context.GetClassDisplayName(value));
        }

        #endregion

        #region Arithmetic Operators

        #region *

        /// <summary>
        /// Returns a new float which is the product of <code>self</code> * and <code>other</code>, where <code>other</code> is Fixnum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("*")]
        public static double Multiply(double self, int other) {
            return self * (double)other;
        }

        /// <summary>
        /// Returns a new float which is the product of <code>self</code> * and <code>other</code>, where <code>other</code> is Bignum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("*")]
        public static double Multiply(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            return self * Protocols.ConvertToDouble(context, other);
        }

        /// <summary>
        /// Returns a new float which is the product of <code>self</code> * and <code>other</code>, where <code>other</code> is Float.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("*")]
        public static double Multiply(double self, double other) {
            return self * other;
        }

        /// <summary>
        /// Returns a new float which is the product of <code>self</code> * and <code>other</code>, , where <code>other</code> is not Fixnum, Bignum or Float.
        /// </summary>
        /// <returns></returns>
        [RubyMethod("*")]
        public static object Multiply(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, double self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "*", self, other);
        }

        #endregion

        #region +

        /// <summary>
        /// Returns a new float which is the sum of <code>self</code> * and <code>other</code>, where <code>other</code> is Fixnum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("+")]
        public static double Add(double self, int other) {
            return self + (double)other;
        }

        /// <summary>
        /// Returns a new float which is the sum of <code>self</code> * and <code>other</code>, where <code>other</code> is Bignum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("+")]
        public static double Add(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            return self + Protocols.ConvertToDouble(context, other);
        }

        /// <summary>
        /// Returns a new float which is the sum of <code>self</code> * and <code>other</code>, where <code>other</code> is Float.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("+")]
        public static double Add(double self, double other) {
            return self + other;
        }

        /// <summary>
        /// Returns a new float which is the sum of <code>self</code> * and <code>other</code>, , where <code>other</code> is not Fixnum, Bignum or Float.
        /// </summary>
        /// <returns></returns>
        [RubyMethod("+")]
        public static object Add(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, double self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "+", self, other);
        }

        #endregion

        #region -

        /// <summary>
        /// Returns a new float which is the difference between <code>self</code> * and <code>other</code>, where <code>other</code> is Fixnum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("-")]
        public static double Subtract(double self, int other) {
            return self - (double)other;
        }

        /// <summary>
        /// Returns a new float which is the difference between <code>self</code> * and <code>other</code>, where <code>other</code> is Bignum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("-")]
        public static double Subtract(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            return self - Protocols.ConvertToDouble(context, other);
        }

        /// <summary>
        /// Returns a new float which is the difference between <code>self</code> * and <code>other</code>, where <code>other</code> is Float.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("-")]
        public static double Subtract(double self, double other) {
            return self - other;
        }

        /// <summary>
        /// Returns a new float which is the difference between <code>self</code> * and <code>other</code>, , where <code>other</code> is not Fixnum, Bignum or Float.
        /// </summary>
        /// <returns></returns>
        [RubyMethod("-")]
        public static object Subtract(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, double self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "-", self, other);
        }

        #endregion

        #region /

        /// <summary>
        /// Returns a new float which is the result of dividing <code>self</code> * by <code>other</code>, where <code>other</code> is Fixnum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("/")]
        public static double Divide(double self, int other) {
            return self / (double)other;
        }

        /// <summary>
        /// Returns a new float which is the result of dividing <code>self</code> * by <code>other</code>, where <code>other</code> is Bignum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("/")]
        public static double Divide(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            return self / Protocols.ConvertToDouble(context, other);
        }

        /// <summary>
        /// Returns a new float which is the result of dividing <code>self</code> * by <code>other</code>, where <code>other</code> is Float.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("/")]
        public static double Divide(double self, double other) {
            return self / other;
        }

        /// <summary>
        /// Returns a new float which is the result of dividing <code>self</code> * by <code>other</code>, where <code>other</code> is not Fixnum, Bignum or Float.
        /// </summary>
        /// <returns></returns>
        [RubyMethod("/")]
        public static object Divide(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, double self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "/", self, other);
        }

        #endregion

        #region %, modulo

        /// <summary>
        /// Return the modulo after division of <code>self</code> by <code>other</code>, where <code>other</code> is Fixnum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("%"), RubyMethod("modulo")]
        public static double Modulo(double self, int other) {
            return (double)InternalDivMod(self, (double)other)[1];
        }

        /// <summary>
        /// Return the modulo after division of <code>self</code> by <code>other</code>, where <code>other</code> is Bignum.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("%"), RubyMethod("modulo")]
        public static double Modulo(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            return (double)InternalDivMod(self, Protocols.ConvertToDouble(context, other))[1];
        }

        /// <summary>
        /// Return the modulo after division of <code>self</code> by <code>other</code>, where <code>other</code> is Float.
        /// </summary>
        /// <returns>Float</returns>
        [RubyMethod("%"), RubyMethod("modulo")]
        public static double Modulo(double self, double other) {
            return (double)InternalDivMod(self, other)[1];
        }

        /// <summary>
        /// Return the modulo after division of <code>self</code> by <code>other</code>, where <code>other</code> is not Fixnum, Bignum or Float.
        /// </summary>
        /// <returns></returns>
        // #modulo is #% under another name in MRI, so both coerce and then ask for "%":
        // two entries that forwarded to different names were not the same method.
        [RubyMethod("%"), RubyMethod("modulo")]
        public static object ModuloOp(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, double self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "%", self, other);
        }

        #endregion

        #region **

        /// <summary>
        /// Raises <code>self</code> the <code>other</code> power, where other is Fixnum.
        /// </summary>
        [RubyMethod("**")]
        public static double Power(double self, int other) {
            return Math.Pow(self, (double)other);
        }

        /// <summary>
        /// Raises <code>self</code> the <code>other</code> power, where other is Bignum.
        /// </summary>
        [RubyMethod("**")]
        public static double Power(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            return Math.Pow(self, Protocols.ConvertToDouble(context, other));
        }

        /// <summary>
        /// Raises <code>self</code> the <code>other</code> power, where other is Float. A negative
        /// base to a power that is not whole leaves the real line (rb_float_pow): that Complex is
        /// __ir_pow_complex__ in rational.rb.
        /// </summary>
        [RubyMethod("**")]
        public static object Power(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ complexPower, double self, double other) {
            if (self < 0 && (double.IsNaN(other) || (!double.IsInfinity(other) && other != Math.Round(other)))) {
                var site = complexPower.GetCallSite("__ir_pow_complex__", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
                return site.Target(site, self, other);
            }
            return Math.Pow(self, other);
        }

        /// <summary>
        /// Raises <code>self</code> the <code>other</code> power, where other is not Fixnum, Bignum or
        /// Float: __ir_pow_coerce__ in rational.rb.
        /// </summary>
        [RubyMethod("**")]
        public static object Power(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ coercePower, double self, object other) {
            var site = coercePower.GetCallSite("__ir_pow_coerce__", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
            return site.Target(site, self, other);
        }

        #endregion

        #region divmod

        /// <summary>
        /// Returns an array containing the quotient and modulus obtained by dividing <i>self</i> by <i>other</i>, where other is Fixnum.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="other"></param>
        /// <returns></returns>
        /// <remarks>
        /// If <code>q, r = x.divmod(y)</code>, then
        /// <code>q = floor(float(x)/float(y))
        /// x = q*y + r</code>
        /// The quotient is rounded toward -infinity
        /// </remarks>
        [RubyMethod("divmod")]
        public static RubyArray DivMod(double self, int other) {
            return DivMod(self, (double)other);
        }

        /// <summary>
        /// Returns an array containing the quotient and modulus obtained by dividing <i>self</i> by <i>other</i>, where other is Bignum.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="other"></param>
        /// <returns></returns>
        /// <remarks>
        /// If <code>q, r = x.divmod(y)</code>, then
        /// <code>q = floor(float(x)/float(y))
        /// x = q*y + r</code>
        /// The quotient is rounded toward -infinity
        /// </remarks>
        [RubyMethod("divmod")]
        public static RubyArray DivMod(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            return DivMod(self, Protocols.ConvertToDouble(context, other));
        }

        /// <summary>
        /// Returns an array containing the quotient and modulus obtained by dividing <i>self</i> by <i>other</i>, where other is Float
        /// </summary>
        /// <param name="self"></param>
        /// <param name="other"></param>
        /// <returns></returns>
        /// <remarks>
        /// If <code>q, r = x.divmod(y)</code>, then
        /// <code>q = floor(float(x)/float(y))
        /// x = q*y + r</code>
        /// The quotient is rounded toward -infinity
        /// </remarks>
        [RubyMethod("divmod")]
        public static RubyArray DivMod(double self, double other) {
            RubyArray result = InternalDivMod(self, other);
            // Unlike modulo, divmod blows up if the quotient or modulus are not finite, so we can't put this inside InternalDivMod
            // We only need to test if the quotient is double since it should have been converted to Integer (Fixnum or Bignum) if it was OK.
            if (result[0] is double || Double.IsNaN((double)result[1])) {
                throw CreateFloatDomainError("NaN");
            }
            return result;
        }

        /// <summary>
        /// Returns an array containing the quotient and modulus obtained by dividing <i>self</i> by <i>other</i>, where other is not Fixnum, Bignum or Float.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="other"></param>
        /// <returns></returns>
        /// <remarks>
        /// If <code>q, r = x.divmod(y)</code>, then
        /// <code>q = floor(float(x)/float(y))
        /// x = q*y + r</code>
        /// The quotient is rounded toward -infinity
        /// </remarks>
        [RubyMethod("divmod")]
        public static object DivMod(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, double self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "divmod", self, other);
        }

        #endregion

        #region abs

        /// <summary>
        /// Returns the absolute value of <i>self</i>.
        /// </summary>
        /// <example>
        ///  (-34.56).abs   #=> 34.56
        ///  -34.56.abs     #=> 34.56
        /// </example>
        [RubyMethod("abs")]
        [RubyMethod("magnitude")]
        public static double Abs(double self) {
            return Math.Abs(self);
        }

        #endregion

        #endregion

        #region Conversion Methods

        #region ceil

        /// <summary>
        /// Returns the smallest <code>Integer</code> greater than or equal to <code>self</code>
        /// </summary>
        /// <example>
        /// 1.2.ceil      #=> 2
        /// 2.0.ceil      #=> 2
        /// (-1.2).ceil   #=> -1
        /// (-2.0).ceil   #=> -2
        /// </example>
        [RubyMethod("ceil")]
        public static object Ceil(double self) {
            double ceil = System.Math.Ceiling(self);
            return CastToInteger(ceil);
        }

        /// <summary>
        /// Returns self rounded up to a multiple of 10**(-ndigits), or a Float with
        /// ndigits digits after the decimal point when ndigits is positive.
        /// </summary>
        [RubyMethod("ceil")]
        public static object Ceil(ConversionStorage<IntegerValue>/*!*/ integerCast, double self, object ndigits) {
            return Scale(integerCast, self, ndigits, RoundDirection.Ceiling);
        }

        #endregion

        #region floor

        /// <summary>
        /// Returns the largest <code>Integer</code> less than or equal to <code>self</code>.
        /// </summary>
        /// <example>
        /// 1.2.floor      #=> 1
        /// 2.0.floor      #=> 2
        /// (-1.2).floor   #=> -2
        /// (-2.0).floor   #=> -2
        /// </example>
        [RubyMethod("floor")]
        public static object Floor(double self) {
            double floor = System.Math.Floor(self);
            return CastToInteger(floor);
        }

        /// <summary>
        /// Returns self rounded down to a multiple of 10**(-ndigits), or a Float with
        /// ndigits digits after the decimal point when ndigits is positive.
        /// </summary>
        [RubyMethod("floor")]
        public static object Floor(ConversionStorage<IntegerValue>/*!*/ integerCast, double self, object ndigits) {
            return Scale(integerCast, self, ndigits, RoundDirection.Flooring);
        }

        #endregion

        #region to_i, to_int, truncate

        /// <summary>
        /// Returns <code>self</code> truncated to an <code>Integer</code>.
        /// </summary>
        [RubyMethod("to_i"), RubyMethod("to_int"), RubyMethod("truncate")]
        public static object ToInt(double self) {
            if (self >= 0) {
                return Floor(self);
            } else {
                return Ceil(self);
            }
        }

        /// <summary>
        /// Returns self truncated towards zero to a multiple of 10**(-ndigits), or a Float with
        /// ndigits digits after the decimal point when ndigits is positive.
        /// </summary>
        [RubyMethod("truncate")]
        public static object Truncate(ConversionStorage<IntegerValue>/*!*/ integerCast, double self, object ndigits) {
            return Scale(integerCast, self, ndigits, RoundDirection.Truncating);
        }

        #endregion

        #region coerce

        /// <summary>
        /// Attempts to coerce other to a Float.
        /// </summary>
        /// <returns>[other, self] as Floats</returns>
        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(double self, [DefaultProtocol]double other) {
            return RubyOps.MakeArray2(other, self);
        }

        #endregion

        #region to_f

        /// <summary>
        /// Converts self to Float
        /// </summary>
        /// <remarks>
        /// As <code>self</code> is already Float, returns <code>self</code>.
        /// </remarks>
        [RubyMethod("to_f")]
        public static double ToFloat(double self) {
            return self;
        }

        #endregion

        #region round

        /// <summary>
        /// Rounds <code>self</code> to the nearest <code>Integer</code>.
        /// </summary>
        /// <remarks>
        /// This is equivalent to:
        /// <code>
        /// def round
        ///     return (self+0.5).floor if self &gt; 0.0
        ///     return (self-0.5).ceil  if self &lt; 0.0
        ///     return 0
        /// end
        /// </code>
        /// </remarks>
        /// <example>
        /// 1.5.round      #=> 2
        /// (-1.5).round   #=> -2
        /// </example>
        [RubyMethod("round")]
        public static object Round(double self) {
            return RoundToPrecision(self, 0, NumericRounding.Half.Up);
        }

        /// <summary>
        /// Rounds self to an optionally given precision, honouring the <c>half:</c> option.
        /// A positive precision yields a Float, zero or negative yields an Integer.
        /// </summary>
        [RubyMethod("round")]
        public static object Round(ConversionStorage<IntegerValue>/*!*/ integerCast, double self,
            object ndigits, [Optional]object options) {

            var opts = options as IDictionary<object, object>;
            if (opts == null && options == Missing.Value) {
                // round(half: :up) - the only argument is the options hash
                opts = ndigits as IDictionary<object, object>;
                if (opts != null) {
                    ndigits = Missing.Value;
                }
            }

            NumericRounding.Half half = NumericRounding.GetHalfOption(integerCast.Context, opts);
            int nd = (ndigits == Missing.Value) ? 0 : NumericRounding.GetNDigits(integerCast, ndigits);
            return RoundToPrecision(self, nd, half);
        }

        private static object RoundToPrecision(double self, int ndigits, NumericRounding.Half half) {
            if (self == 0.0) {
                // preserves -0.0 for a positive precision, like MRI
                return ndigits > 0 ? (object)self : ScriptingRuntimeHelpers.Int32ToObject(0);
            }

            if (ndigits > 0) {
                if (Double.IsNaN(self) || Double.IsInfinity(self)) {
                    return self;
                }
                if (NumericRounding.RoundOverflows(self, ndigits)) {
                    return self;
                }
                if (NumericRounding.RoundUnderflows(self, ndigits)) {
                    return 0.0;
                }
                if (ndigits > 14) {
                    // pow(10, ndigits) is not exact from here on; MRI rounds the exact value instead
                    return NumericRounding.RoundByRational(self, ndigits, half);
                }

                double f = System.Math.Pow(10, ndigits);
                if (Double.IsInfinity(f)) {
                    return self;
                }

                double x = NumericRounding.RoundHalf(self, f, half);
                if (Double.IsNaN(x) || Double.IsInfinity(x)) {
                    return self;
                }
                return x / f;
            }

            if (ndigits == 0) {
                return CastToInteger(NumericRounding.RoundHalf(self, 1.0, half));
            }

            // A negative precision always produces an Integer, so the exceptional values raise.
            if (Double.IsNaN(self)) {
                throw new FloatDomainError("NaN");
            }
            if (Double.IsPositiveInfinity(self)) {
                throw new FloatDomainError("Infinity");
            }
            if (Double.IsNegativeInfinity(self)) {
                throw new FloatDomainError("-Infinity");
            }

            return NumericRounding.RoundInteger(ToBigInteger(System.Math.Truncate(self)), ndigits, half);
        }

        private enum RoundDirection {
            Flooring,
            Ceiling,
            Truncating
        }

        private static object Scale(ConversionStorage<IntegerValue>/*!*/ integerCast, double self, object ndigits, RoundDirection direction) {
            int nd = NumericRounding.GetNDigits(integerCast, ndigits);

            if (self == 0.0) {
                return nd > 0 ? (object)self : ScriptingRuntimeHelpers.Int32ToObject(0);
            }

            if (nd > 0) {
                if (Double.IsNaN(self) || Double.IsInfinity(self)) {
                    return self;
                }
                if (NumericRounding.RoundOverflows(self, nd)) {
                    return self;
                }

                // truncating is flooring a positive number and ceiling a negative one (MRI's flo_truncate)
                bool flooring = (direction == RoundDirection.Flooring) || (direction == RoundDirection.Truncating && self > 0.0);

                // MRI: a value that rounds away entirely towards zero is a positive 0.0, however
                // small and negative it was; -1e-10.ceil(1) is 0.0, not -0.0.
                if (NumericRounding.RoundUnderflows(self, nd) && (flooring ? self > 0.0 : self < 0.0)) {
                    return 0.0;
                }

                double f = System.Math.Pow(10, nd);
                if (Double.IsInfinity(f)) {
                    return self;
                }

                double x = self * f;
                if (Double.IsInfinity(x)) {
                    return self;
                }

                // MRI's rb_float_floor/ceil check the neighbouring result against the original,
                // which repairs the cases where self * f landed on the wrong side of an integer.
                double result;
                if (flooring) {
                    double mul = System.Math.Floor(x);
                    result = (mul + 1) / f;
                    if (result > self) {
                        result = mul / f;
                    }
                } else {
                    double mul = System.Math.Ceiling(x);
                    result = (mul - 1) / f;
                    if (result < self) {
                        result = mul / f;
                    }
                }
                return result;
            }

            double integral;
            switch (direction) {
                case RoundDirection.Flooring: integral = System.Math.Floor(self); break;
                case RoundDirection.Ceiling: integral = System.Math.Ceiling(self); break;
                default: integral = System.Math.Truncate(self); break;
            }

            // raises FloatDomainError for NaN / +-Infinity:
            object value = CastToInteger(integral);
            if (nd == 0) {
                return value;
            }

            BigInteger big = (value is int) ? new BigInteger((int)value) : (BigInteger)value;
            switch (direction) {
                case RoundDirection.Flooring: return NumericRounding.FloorInteger(big, nd);
                case RoundDirection.Ceiling: return NumericRounding.CeilInteger(big, nd);
                default: return NumericRounding.TruncateInteger(big, nd);
            }
        }

        private static BigInteger ToBigInteger(double integralValue) {
            object value = CastToInteger(integralValue);
            return (value is int) ? new BigInteger((int)value) : (BigInteger)value;
        }

        #endregion

        #region inspect, to_s

        /// <summary>
        /// Returns a string containing a representation of self.
        /// </summary>
        /// <remarks>
        /// As well as a fixed or exponential form of the number, the call may return
        /// "<code>NaN</code>", "<code>Infinity</code>", and "<code>-Infinity</code>".
        /// </remarks>
        [RubyMethod("to_s")]
        [RubyMethod("inspect")]
        public static MutableString ToS(RubyContext/*!*/ context, double self) {
            return MutableString.CreateAscii(ToShortestString(self));
        }

        /// <summary>
        /// Ruby's Float#to_s: the shortest decimal that reads back as the same double, with a
        /// decimal point always present. Fixed notation is used while the decimal point falls in
        /// (0, DBL_DIG] or in (-4, 0]; outside that range the result is exponential with at least
        /// one fraction digit and a two-digit exponent, so 1e15 is "1.0e+15" and 1e14 is
        /// "100000000000000.0".
        /// </summary>
        internal static string/*!*/ ToShortestString(double value) {
            if (Double.IsNaN(value)) {
                return "NaN";
            }
            if (Double.IsPositiveInfinity(value)) {
                return "Infinity";
            }
            if (Double.IsNegativeInfinity(value)) {
                return "-Infinity";
            }

            bool negative = (value < 0.0) || (value == 0.0 && Double.IsNegative(value));

            // "R" round-trips through the shortest digit string on .NET Core 3.0 and later,
            // which is the same set of digits Ruby's dtoa produces in shortest mode.
            string repr = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);

            int exponent = 0;
            int e = repr.IndexOf('E');
            if (e >= 0) {
                exponent = Int32.Parse(repr.Substring(e + 1), CultureInfo.InvariantCulture);
                repr = repr.Substring(0, e);
            }

            int dot = repr.IndexOf('.');
            string digits = (dot < 0) ? repr : repr.Substring(0, dot) + repr.Substring(dot + 1);
            // Number of digits that belong to the left of the decimal point.
            int pointPosition = ((dot < 0) ? repr.Length : dot) + exponent;

            int leading = 0;
            while (leading < digits.Length && digits[leading] == '0') {
                leading++;
                pointPosition--;
            }
            digits = digits.Substring(leading).TrimEnd('0');
            if (digits.Length == 0) {
                digits = "0";
                pointPosition = 1;
            }

            StringBuilder result = new StringBuilder();
            if (negative) {
                result.Append('-');
            }

            // Fixed notation runs from just past 0.0001 up to DBL_DIG integral digits, plus one
            // extra decade when the digits reach into the fraction so that no padding zeros are
            // invented: 1536234243126633.5 stays fixed while 1.5e+15 and 1234567890123456.0 do
            // not. Derived by differential testing against CRuby over 200k random doubles.
            const int DblDig = 15;
            bool fixedForm = pointPosition > -4 &&
                (pointPosition <= DblDig || (pointPosition == DblDig + 1 && digits.Length > pointPosition));

            if (fixedForm && pointPosition > 0) {
                if (digits.Length <= pointPosition) {
                    result.Append(digits).Append('0', pointPosition - digits.Length).Append(".0");
                } else {
                    result.Append(digits, 0, pointPosition).Append('.').Append(digits, pointPosition, digits.Length - pointPosition);
                }
            } else if (fixedForm) {
                result.Append("0.").Append('0', -pointPosition).Append(digits);
            } else {
                result.Append(digits[0]).Append('.');
                if (digits.Length > 1) {
                    result.Append(digits, 1, digits.Length - 1);
                } else {
                    result.Append('0');
                }
                int power = pointPosition - 1;
                result.Append('e').Append(power < 0 ? '-' : '+');
                result.Append(Math.Abs(power).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0'));
            }

            return result.ToString();
        }

        #endregion

        #region hash

        /// <summary>
        /// Returns a hash code for <code>self</code>.
        /// </summary>
        [RubyMethod("hash")]
        public static int Hash(double self) {
            return RubyUtils.GetFloatHashCode(self);
        }

        #endregion

        #endregion

        #region Comparison Operators

        #region ==

        /// <summary>
        /// Returns <code>true</code> only if <i>other</i> has the same value as <i>self</i>, where other is Float
        /// </summary>
        /// <returns>True or False</returns>
        /// <remarks>
        /// Contrast this with <code>Float#eql?</code>, which requires <i>other</i> to be a <code>Float</code>.
        /// </remarks>
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(double self, double other) {
            return self == other;
        }

        /// <summary>
        /// Returns <code>true</code> only if <i>other</i> has the same value as <i>self</i>, where other is not a Float
        /// </summary>
        /// <returns>True or False</returns>
        /// <remarks>
        /// Contrast this with <code>Float#eql?</code>, which requires <i>other</i> to be a <code>Float</code>.
        /// Dynamically invokes other == self (i.e. swaps operands around).
        /// </remarks>
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(BinaryOpStorage/*!*/ equals, double self, object other) {
            // Call == on the right operand like Float#== does
            return Protocols.IsEqual(equals, other, self);
        }

        #endregion

        #region <=>

        /// <summary>
        /// Compares self with other, where other is Float.
        /// </summary>
        /// <returns>
        /// -1 if self is less than other
        /// 0 if self is equal to other
        /// +1 if self is greater than other
        /// nil if self is not comparable to other (for instance if either is NaN).
        /// </returns>
        /// <remarks>
        /// This is the basis for the tests in <code>Comparable</code>.
        /// </remarks>
        [RubyMethod("<=>")]
        public static object Compare(double self, double other) {
            if (Double.IsNaN(self) || Double.IsNaN(other)) {
                return null;
            }
            return self.CompareTo(other);
        }

        /// <summary>
        /// Compares self with other, where other is Fixnum.
        /// </summary>
        /// <returns>
        /// -1 if self is less than other
        /// 0 if self is equal to other
        /// +1 if self is greater than other
        /// nil if self is not comparable to other (for instance if either is NaN).
        /// </returns>
        /// <remarks>
        /// This is the basis for the tests in <code>Comparable</code>.
        /// </remarks>
        [RubyMethod("<=>")]
        public static object Compare(double self, int other) {
            if (Double.IsNaN(self)) {
                return null;
            }
            return self.CompareTo((double)other);
        }

        /// <summary>
        /// Compares self with other, where other is Bignum.
        /// </summary>
        /// <returns>
        /// -1 if self is less than other
        /// 0 if self is equal to other
        /// +1 if self is greater than other
        /// nil if self is not comparable to other (for instance if either is NaN).
        /// </returns>
        /// <remarks>
        /// This is the basis for the tests in <code>Comparable</code>.
        /// </remarks>
        [RubyMethod("<=>")]
        public static object Compare(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            // Exactly, rather than by rounding other to a double: a Bignum past Float::MAX rounds
            // to Infinity, which would make Infinity equal to it.
            object result = ClrInteger.Compare(context, other, self);
            return result is int ? -(int)result : result;
        }

        /// <summary>
        /// Compares self with other, where other is not Float, Fixnum or Bignum.
        /// </summary>
        /// <returns>
        /// -1 if self is less than other
        /// 0 if self is equal to other
        /// +1 if self is greater than other
        /// nil if self is not comparable to other (for instance if either is NaN).
        /// </returns>
        /// <remarks>
        /// This is the basis for the tests in <code>Comparable</code>.
        /// </remarks>
        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage,
            RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ infiniteStorage,
            ConversionStorage<int>/*!*/ fixnumCast, double self, object other) {

            if (Double.IsNaN(self)) {
                return null;
            }

            // MRI's flo_cmp takes this branch before coercing: when self is infinite and
            // the operand can report its own infinitude, the two are compared as infinities
            // rather than through #coerce. Date::Infinity relies on it -- Date::JULIAN is
            // -Float::INFINITY, and without this `Date::JULIAN <=> Date::Infinity.new(-1)`
            // coerces and answers -1 where MRI answers 0.
            if (Double.IsInfinity(self) && Protocols.RespondTo(respondToStorage, other, "infinite?")) {
                var site = infiniteStorage.GetCallSite("infinite?", 0);
                object infinite = site.Target(site, other);

                if (Protocols.IsTrue(infinite)) {
                    // other is infinite too; its #infinite? gives the sign.
                    int otherSign = Protocols.CastToFixnum(fixnumCast, infinite);
                    if (self > 0.0) {
                        return ScriptingRuntimeHelpers.Int32ToObject(otherSign > 0 ? 0 : +1);
                    }
                    return ScriptingRuntimeHelpers.Int32ToObject(otherSign < 0 ? 0 : -1);
                }

                // other is finite, so an infinite self is strictly greater or smaller.
                return ScriptingRuntimeHelpers.Int32ToObject(self > 0.0 ? +1 : -1);
            }

            return Protocols.CoerceAndCompare(coercionStorage, comparisonStorage, self, other);
        }

        #endregion

        #region <

        /// <summary>
        /// Returns true if self is less than other, where other is Float.
        /// </summary>
        [RubyMethod("<")]
        public static bool LessThan(double self, double other) {
            if (double.IsNaN(self) || double.IsNaN(other)) {
                return false;
            }
            return self < other;
        }

        /// <summary>
        /// Returns true if self is less than other, where other is Fixnum.
        /// </summary>
        [RubyMethod("<")]
        public static bool LessThan(double self, int other) {
            return LessThan(self, (double)other);
        }

        /// <summary>
        /// Returns true if self is less than other, where other is Bignum.
        /// </summary>
        [RubyMethod("<")]
        public static bool LessThan(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            int? c = ClrInteger.CompareToFloat(other, self);
            return c.HasValue && -c.Value < 0;
        }

        /// <summary>
        /// Returns true if self is less than other, where other is not Float, Fixnum or Bignum.
        /// </summary>
        [RubyMethod("<")]
        public static bool LessThan(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, double self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, "<", self, other);
        }

        #endregion

        #region <=

        /// <summary>
        /// Returns true if self is less than or equal to other, where other is Float.
        /// </summary>
        [RubyMethod("<=")]
        public static bool LessThanOrEqual(double self, double other) {
            if (double.IsNaN(self) || double.IsNaN(other)) {
                return false;
            }
            return self <= other;
        }

        /// <summary>
        /// Returns true if self is less than or equal to other, where other is Fixnum.
        /// </summary>
        [RubyMethod("<=")]
        public static bool LessThanOrEqual(double self, int other) {
            return LessThanOrEqual(self, (double)other);
        }

        /// <summary>
        /// Returns true if self is less than or equal to other, where other is Bignum.
        /// </summary>
        [RubyMethod("<=")]
        public static bool LessThanOrEqual(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            int? c = ClrInteger.CompareToFloat(other, self);
            return c.HasValue && -c.Value <= 0;
        }

        /// <summary>
        /// Returns true if self is less than or equal to other, where other is not Float, Fixnum or Bignum.
        /// </summary>
        [RubyMethod("<=")]
        public static bool LessThanOrEqual(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, double self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, "<=", self, other);
        }

        #endregion

        #region >

        /// <summary>
        /// Returns true if self is greater than other, where other is Float.
        /// </summary>
        [RubyMethod(">")]
        public static bool GreaterThan(double self, double other) {
            if (double.IsNaN(self) || double.IsNaN(other)) {
                return false;
            }
            return self > other;
        }

        /// <summary>
        /// Returns true if self is greater than other, where other is Fixnum.
        /// </summary>
        [RubyMethod(">")]
        public static bool GreaterThan(double self, int other) {
            return GreaterThan(self, (double)other);
        }

        /// <summary>
        /// Returns true if self is greater than other, where other is Bignum.
        /// </summary>
        [RubyMethod(">")]
        public static bool GreaterThan(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            int? c = ClrInteger.CompareToFloat(other, self);
            return c.HasValue && -c.Value > 0;
        }

        /// <summary>
        /// Returns true if self is greater than other, where other is not Float, Fixnum or Bignum.
        /// </summary>
        [RubyMethod(">")]
        public static bool GreaterThan(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, double self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, ">", self, other);
        }

        #endregion

        #region >=

        /// <summary>
        /// Returns true if self is greater than or equal to other, where other is Float.
        /// </summary>
        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(double self, double other) {
            if (double.IsNaN(self) || double.IsNaN(other)) {
                return false;
            }
            return self >= other;
        }

        /// <summary>
        /// Returns true if self is greater than or equal to other, where other is Fixnum.
        /// </summary>
        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(double self, int other) {
            return GreaterThanOrEqual(self, (double)other);
        }

        /// <summary>
        /// Returns true if self is greater than or equal to other, where other is Bignum.
        /// </summary>
        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(RubyContext/*!*/ context, double self, [NotNull]BigInteger/*!*/ other) {
            int? c = ClrInteger.CompareToFloat(other, self);
            return c.HasValue && -c.Value >= 0;
        }

        /// <summary>
        /// Returns true if self is greater than or equal to other, where other is not Float, Fixnum or Bignum.
        /// </summary>
        [RubyMethod(">=")]
        public static bool GreaterThanOrEqual(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, double self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, ">=", self, other);
        }

        #endregion

        #region finite?

        /// <summary>
        /// Returns <code>true</code> if <code>self</code> is a valid IEEE floating point number
        /// (it is not infinite, and <code>nan?</code> is <code>false</code>).
        /// </summary>
        [RubyMethod("finite?")]
        public static bool IsFinite(double self) {
            // NaN is not a finite number either, whatever its exponent looks like.
            return double.IsFinite(self);
        }

        #endregion

        #region infinite?

        /// <summary>
        /// Returns <code>nil</code>, -1, or +1 depending on whether <code>self</code> is finite, -infinity, or +infinity.
        /// </summary>
        /// <example>
        /// (0.0).infinite?        #=> nil
        /// (-1.0/0.0).infinite?   #=> -1
        /// (+1.0/0.0).infinite?   #=> 1
        /// </example>
        [RubyMethod("infinite?")]
        public static object IsInfinite(double self) {
            if (double.IsInfinity(self)) {
                return double.IsPositiveInfinity(self) ? 1 : -1;
            } else {
                return null;
            }
        }

        #endregion

        #region nan?

        /// <summary>
        /// Returns <code>true</code> if <i>self</i> is an invalid IEEE floating point number.
        /// </summary>
        /// <example>
        /// a = -1.0      #=> -1.0
        /// a.nan?        #=> false
        /// a = 0.0/0.0   #=> NaN
        /// a.nan?        #=> true
        /// </example>
        [RubyMethod("nan?")]
        public static bool IsNan(double self) {
            return double.IsNaN(self);
        }

        #endregion

        #region zero?

        /// <summary>
        /// Returns <code>true</code> if <code>self</code> is 0.0.
        /// </summary>
        [RubyMethod("zero?")]
        public static bool IsZero(double self) {
            return self.Equals(0.0);
        }

        #endregion

        #endregion

        #region Helpers

        // MRI's flodivmod. Computing the modulus as self - floor(self/other)*other loses three
        // cases that the specs check: a zero divisor is a ZeroDivisionError rather than NaN, an
        // infinite divisor leaves a finite dividend alone (0 * Infinity is NaN, not 0), and a
        // dividend of zero keeps its own sign, so -0.0 % 42 is -0.0 and not 0.0.
        private static RubyArray InternalDivMod(double self, double other) {
            double div, mod;
            if (Double.IsNaN(other)) {
                div = mod = other;
            } else if (other == 0.0) {
                throw new DivideByZeroException("divided by 0");
            } else {
                if (self == 0.0 || (Double.IsInfinity(other) && !Double.IsInfinity(self))) {
                    mod = self;
                } else {
                    // C#'s % on doubles is C's fmod: the remainder takes the dividend's sign.
                    mod = self % other;
                }

                if (Double.IsInfinity(self) && !Double.IsInfinity(other)) {
                    div = self / other;
                } else {
                    div = Math.Round((self - mod) / other, MidpointRounding.AwayFromZero);
                }

                if (other * mod < 0) {
                    mod += other;
                    div -= 1.0;
                }
            }
            object intDiv = div;
            if (!Double.IsInfinity(div) && !Double.IsNaN(div)) {
                intDiv = ToInt(div);
            }
            return RubyOps.MakeArray2(intDiv, mod);
        }

        // I did think about whether to put this into Protocols but really it is only checking the FloatDomainErrors
        // Also, it is only used in FloatOps.Floor and FloatOps.Ceil.
        // These get invoked from Protocols.ConvertToInteger anyway so it would all get a bit circular.
        private static object CastToInteger(double value) {
            try {
                if (Double.IsPositiveInfinity(value)) {
                    throw new FloatDomainError("Infinity");
                }
                if (Double.IsNegativeInfinity(value)) {
                    throw new FloatDomainError("-Infinity");
                }
                if (Double.IsNaN(value)) {
                    throw new FloatDomainError("NaN");
                }
                return System.Convert.ToInt32(value);
            } catch (OverflowException) {
                return new BigInteger(value);
            }
        }

        public static Exception CreateFloatDomainError(string message) {
            return new FloatDomainError("NaN");
        }

        public static Exception CreateFloatDomainError(string message, Exception inner) {
            return new FloatDomainError("NaN", inner);
        }

        #endregion
    }
}

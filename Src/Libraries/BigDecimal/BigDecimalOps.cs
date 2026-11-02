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
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Generation;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Globalization;

namespace IronRuby.StandardLibrary.BigDecimal {
    [RubyClass("BigDecimal", Inherits = typeof(Numeric), Extends = typeof(BigDecimal))]
    public sealed class BigDecimalOps {

        internal static readonly object BigDecimalOpsClassKey = new object();

        #region Static Fields

        internal static BigDecimal.Config GetConfig(RubyContext/*!*/ context) {
            ContractUtils.RequiresNotNull(context, "context");
            return (BigDecimal.Config)context.GetOrCreateLibraryData(BigDecimalOpsClassKey, () => new BigDecimal.Config());
        }

        #endregion

        #region Construction

        [RubyConstructor]
        public static BigDecimal/*!*/ CreateBigDecimal(RubyContext/*!*/ context, RubyClass/*!*/ self, [DefaultProtocol]MutableString/*!*/ value, [Optional]int n) {
            string str = value.ConvertToString();
            if (!BigDecimal.IsValidNumericString(str)) {
                throw RubyExceptions.CreateArgumentError("invalid value for BigDecimal(): \"{0}\"", str);
            }
            return BigDecimal.Create(GetConfig(context), str, n);
        }

        #endregion

        #region Constants

        [RubyConstant]
        public const uint BASE = BigDecimal.BASE;
        [RubyConstant]
        public const int EXCEPTION_ALL = (int)BigDecimal.OverflowExceptionModes.All;
        [RubyConstant]
        public const int EXCEPTION_INFINITY = (int)BigDecimal.OverflowExceptionModes.Infinity;
        [RubyConstant]
        public const int EXCEPTION_NaN = (int)BigDecimal.OverflowExceptionModes.NaN;
        [RubyConstant]
        public const int EXCEPTION_OVERFLOW = (int)BigDecimal.OverflowExceptionModes.Overflow;
        [RubyConstant]
        public const int EXCEPTION_UNDERFLOW = (int)BigDecimal.OverflowExceptionModes.Underflow;
        [RubyConstant]
        public const int EXCEPTION_ZERODIVIDE = (int)BigDecimal.OverflowExceptionModes.ZeroDivide;
        [RubyConstant]
        public const int ROUND_CEILING = (int)BigDecimal.RoundingModes.Ceiling;
        [RubyConstant]
        public const int ROUND_DOWN = (int)BigDecimal.RoundingModes.Down;
        [RubyConstant]
        public const int ROUND_FLOOR = (int)BigDecimal.RoundingModes.Floor;
        [RubyConstant]
        public const int ROUND_HALF_DOWN = (int)BigDecimal.RoundingModes.HalfDown;
        [RubyConstant]
        public const int ROUND_HALF_EVEN = (int)BigDecimal.RoundingModes.HalfEven;
        [RubyConstant]
        public const int ROUND_HALF_UP = (int)BigDecimal.RoundingModes.HalfUp;
        [RubyConstant]
        public const int ROUND_UP = (int)BigDecimal.RoundingModes.Up;
        [RubyConstant]
        public const int ROUND_MODE = 256;
        [RubyConstant]
        public const int SIGN_NEGATIVE_FINITE = -2;
        [RubyConstant]
        public const int SIGN_NEGATIVE_INFINITE = -3;
        [RubyConstant]
        public const int SIGN_NEGATIVE_ZERO = -1;
        [RubyConstant]
        public const int SIGN_NaN = 0;
        [RubyConstant]
        public const int SIGN_POSITIVE_FINITE = 2;
        [RubyConstant]
        public const int SIGN_POSITIVE_INFINITE = 3;
        [RubyConstant]
        public const int SIGN_POSITIVE_ZERO = 1;
        #endregion

        #region Singleton Methods

        [RubyMethod("_load", RubyMethodAttributes.PublicSingleton)]
        public static BigDecimal/*!*/ Load(RubyContext/*!*/ context, RubyClass/*!*/ self, [DefaultProtocol]MutableString/*!*/ str) {
            try {
                MutableString[] components = str.Split(new char[] { ':' }, 2, StringSplitOptions.None);
                int maxDigits = 0;
                int maxPrecision = 1;
                string digits = "";

                if (!(components[0] == null) || components[0].IsEmpty) {
                    maxDigits = int.Parse(components[0].ToString(), CultureInfo.InvariantCulture);
                }
                if (maxDigits != 0) {
                    maxPrecision = maxDigits / BigDecimal.BASE_FIG + (maxDigits % BigDecimal.BASE_FIG == 0 ? 0 : 1);
                }
                if (components.Length == 2 && components[1] != null) {
                    digits = components[1].ToString();
                }

                return BigDecimal.Create(GetConfig(context), digits, maxPrecision);
            } catch {
                throw RubyExceptions.CreateTypeError("load failed: invalid character in the marshaled string.");
            }
        }

        [RubyMethod("double_fig", RubyMethodAttributes.PublicSingleton)]
        public static int DoubleFig(RubyClass/*!*/ self) {
            return 16; // This is the number of digits (in the mantissa?) that a Double can hold.
        }

        [RubyMethod("mode", RubyMethodAttributes.PublicSingleton)]
        public static int Mode(RubyContext/*!*/ context, RubyClass/*!*/ self, int mode) {
            if (mode == ROUND_MODE) {
                return (int)GetConfig(context).RoundingMode;
            } else {
                return (int)GetConfig(context).OverflowMode & mode;
            }
        }

        [RubyMethod("mode", RubyMethodAttributes.PublicSingleton)]
        public static int Mode(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, RubyClass/*!*/ self, int mode, object value) {
            if (value == null) {
                return Mode(context, self, mode);
            }
            if (mode == ROUND_MODE) {
                var rounding = ToRoundingMode(fixnumCast, context, value);
                GetConfig(context).RoundingMode = rounding;
                return (int)rounding;
            } else {
                if (value is bool) {
                    BigDecimal.Config config = GetConfig(context);
                    if (Enum.IsDefined(typeof(BigDecimal.OverflowExceptionModes), mode)) {
                        BigDecimal.OverflowExceptionModes enumMode = (BigDecimal.OverflowExceptionModes)mode;
                        if ((bool)value) {
                            config.OverflowMode = config.OverflowMode | enumMode;
                        } else {
                            config.OverflowMode = config.OverflowMode & (BigDecimal.OverflowExceptionModes.All ^ enumMode);
                        }
                    }
                    return (int)config.OverflowMode;
                } else {
                    throw RubyExceptions.CreateTypeError("second argument must be true or false");
                }
            }
        }

        [RubyMethod("limit", RubyMethodAttributes.PublicSingleton)]
        public static int Limit(RubyContext/*!*/ context, RubyClass/*!*/ self, int n) {
            if (n < 0) {
                throw RubyExceptions.CreateArgumentError("argument must be positive");
            }
            BigDecimal.Config config = GetConfig(context);
            int limit = config.Limit;
            config.Limit = n;
            return limit;
        }

        [RubyMethod("limit", RubyMethodAttributes.PublicSingleton)]
        public static int Limit(RubyContext/*!*/ context, RubyClass/*!*/ self, [Optional]object n) {
            // BigDecimal.limit(nil) answers the limit without changing it (BigDecimal_limit).
            if (!(n is Missing) && n != null) {
                throw RubyExceptions.CreateUnexpectedTypeError(context, n, "Integer");
            }
            return GetConfig(context).Limit;
        }

        [RubyMethod("ver", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Version(RubyClass/*!*/ self) {
            return MutableString.CreateAscii("1.0.1");
        }

        /// <summary>
        /// Parses as much of the string as looks like a number and answers zero when none of
        /// it does, where Kernel#BigDecimal would raise. This is what String#to_d uses.
        /// </summary>
        [RubyMethod("interpret_loosely", RubyMethodAttributes.PublicSingleton)]
        public static BigDecimal/*!*/ InterpretLoosely(RubyContext/*!*/ context, RubyClass/*!*/ self, [DefaultProtocol]MutableString/*!*/ value) {
            return BigDecimal.Create(GetConfig(context), value.ConvertToString(), 0);
        }

        #region induced_from

        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static BigDecimal InducedFrom(RubyClass/*!*/ self, [NotNull]BigDecimal/*!*/ value) {
            return value;
        }

        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static BigDecimal InducedFrom(RubyContext/*!*/ context, RubyClass/*!*/ self, int value) {
            return BigDecimal.Create(GetConfig(context), value.ToString(CultureInfo.InvariantCulture));
        }

        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static BigDecimal InducedFrom(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]BigInteger/*!*/ value) {
            return BigDecimal.Create(GetConfig(context), value.ToString(CultureInfo.InvariantCulture));
        }

        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static BigDecimal InducedFrom(RubyClass/*!*/ self, object value) {
            throw RubyExceptions.CreateImplicitConversionError(self.Context.GetClassDisplayName(value), self.Name);
        }

        #endregion

        #endregion

        #region Instance Methods

        #region Properties

        [RubyMethod("sign")]
        public static int Sign(BigDecimal/*!*/ self) {
            return self.GetSignCode();
        }

        [RubyMethod("exponent")]
        public static int Exponent(BigDecimal/*!*/ self) {
            return self.Exponent;
        }

        /// <summary>
        /// The number of decimal digits it takes to write self out in positional notation -
        /// which is not the same as the count of significant digits: BigDecimal("1E2") has
        /// one significant digit but a precision of 3, and BigDecimal("0.001") has a
        /// precision of 3 for the leading zeros it needs. Zero and the special values are 0.
        /// (Without this, `precision` resolves to the CLR Precision property, which counts
        /// nine-digit words.)
        /// </summary>
        [RubyMethod("precision")]
        public static int Precision(BigDecimal/*!*/ self) {
            if (!BigDecimal.IsFinite(self) || BigDecimal.IsZero(self)) {
                return 0;
            }
            return self.Exponent > 0
                ? Math.Max(self.Digits, self.Exponent)
                : self.Digits - self.Exponent;
        }

        [RubyMethod("precs")]
        public static RubyArray/*!*/ Precs(BigDecimal/*!*/ self) {
            return RubyOps.MakeArray2(self.Precision * BigDecimal.BASE_FIG, self.MaxPrecision * BigDecimal.BASE_FIG);
        }

        [RubyMethod("split")]
        public static RubyArray/*!*/ Split(BigDecimal/*!*/ self) {
            return RubyOps.MakeArray4(self.Sign, MutableString.CreateAscii(self.GetFractionString()), 10, self.Exponent);
        }

        [RubyMethod("fix")]
        public static BigDecimal/*!*/ Fix(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            return BigDecimal.IntegerPart(GetConfig(context), self);
        }

        [RubyMethod("frac")]
        public static BigDecimal/*!*/ Fraction(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            return BigDecimal.FractionalPart(GetConfig(context), self);
        }

        #endregion

        #region Conversion Operations

        #region to_s

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToString(BigDecimal/*!*/ self) {
            return MutableString.CreateAscii(self.ToString());
        }

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToString(BigDecimal/*!*/ self, [DefaultProtocol]int separateAt) {
            return MutableString.CreateAscii(self.ToString(separateAt));
        }

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToString(BigDecimal/*!*/ self, [DefaultProtocol][NotNull]MutableString/*!*/ format) {
            string posSign = "";
            int separateAt = 0;

            Match m = Regex.Match(format.ConvertToString(), @"^(?<posSign>[+ ])?(?<separateAt>\d+)?(?<floatFormat>[fF])?", RegexOptions.ExplicitCapture);
            Group posSignGroup = m.Groups["posSign"];
            Group separateAtGroup = m.Groups["separateAt"];
            Group floatFormatGroup = m.Groups["floatFormat"];

            if (posSignGroup.Success) {
                posSign = m.Groups["posSign"].Value;
            }
            if (separateAtGroup.Success) {
                separateAt = Int32.Parse(m.Groups["separateAt"].Value, CultureInfo.InvariantCulture);
            }
            bool floatFormat = floatFormatGroup.Success;
            return MutableString.CreateAscii(self.ToString(separateAt, posSign, floatFormat));
        }

        #endregion

        /// <summary>
        /// Since the bigdecimal gem's 3.0, #inspect is just #to_s - the old
        /// #&lt;BigDecimal:0x...,'0.1E1',9(9)&gt; form is gone.
        /// </summary>
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            return ToString(self);
        }

        #region coerce

        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(BigDecimal/*!*/ self, BigDecimal/*!*/ other) {
            return RubyOps.MakeArray2(other, self);
        }

        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            return RubyOps.MakeArray2(BigDecimal.Create(GetConfig(context), other), self);
        }

        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return RubyOps.MakeArray2(BigDecimal.Create(GetConfig(context), other.ToString(CultureInfo.InvariantCulture)), self);
        }

        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(RubyContext/*!*/ context, BigDecimal/*!*/ self, BigInteger/*!*/ other) {
            return RubyOps.MakeArray2(BigDecimal.Create(GetConfig(context), other.ToString(CultureInfo.InvariantCulture)), self);
        }

        #endregion

        #region _dump

        [RubyMethod("_dump")]
        public static MutableString/*!*/ Dump(BigDecimal/*!*/ self, [Optional]object limit) {
            // We ignore the limit value as BigDecimal does not contain other objects.
            return MutableString.CreateMutable(RubyEncoding.Binary).
                Append(self.MaxPrecisionDigits.ToString(CultureInfo.InvariantCulture)).
                Append(':').
                Append(self.ToString()
            );
        }

        #endregion

        [RubyMethod("to_f")]
        public static double ToFloat(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            return BigDecimal.ToFloat(GetConfig(context), self);
        }

        [RubyMethod("to_i")]
        [RubyMethod("to_int")]
        public static object ToI(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            if (!BigDecimal.IsFinite(self)) {
                throw CreateSpecialValueError(self);
            }
            return BigDecimal.ToInteger(GetConfig(context), self);
        }

        [RubyMethod("hash")]
        public static int Hash(BigDecimal/*!*/ self) {
            return self.GetHashCode();
        }

        #endregion

        #region Arithmetic Operations

        #region add, +

        [RubyMethod("+")]
        [RubyMethod("add")]
        public static BigDecimal/*!*/ Add(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            return BigDecimal.Add(GetConfig(context), self, other);
        }

        [RubyMethod("+")]
        [RubyMethod("add")]
        public static BigDecimal/*!*/ Add(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Add(config, self, BigDecimal.Create(config, other));
        }

        [RubyMethod("+")]
        [RubyMethod("add")]
        public static BigDecimal/*!*/ Add(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Add(config, self, BigDecimal.Create(config, other));
        }

        [RubyMethod("+")]
        [RubyMethod("add")]
        public static object Add(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, 
            BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, opStorage, "+", self, other);
        }

        [RubyMethod("add")]
        public static BigDecimal/*!*/ Add(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other, int n) {
            return BigDecimal.Add(GetConfig(context), self, other, n);
        }

        [RubyMethod("add")]
        public static BigDecimal/*!*/ Add(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other, int n) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Add(config, self, BigDecimal.Create(config, other), n);
        }

        [RubyMethod("add")]
        public static BigDecimal/*!*/ Add(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other, int n) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Add(config, self, BigDecimal.Create(config, other), n);
        }

        [RubyMethod("add")]
        public static BigDecimal/*!*/ Add(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other, int n) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Add(config, self, BigDecimal.Create(config, other), n);
        }

        [RubyMethod("add")]
        public static object Add(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, 
            BigDecimal/*!*/ self, object other, [DefaultProtocol]int n) {
            return Protocols.CoerceAndApply(coercionStorage, opStorage, "+", self, other);
        }

        #endregion

        #region sub, -

        [RubyMethod("-")]
        [RubyMethod("sub")]
        public static BigDecimal/*!*/ Subtract(RubyContext/*!*/ context, BigDecimal/*!*/ self, BigDecimal/*!*/ other) {
            return BigDecimal.Subtract(GetConfig(context), self, other);
        }

        [RubyMethod("-")]
        [RubyMethod("sub")]
        public static BigDecimal/*!*/ Subtract(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Subtract(config, self, BigDecimal.Create(config, other));
        }

        [RubyMethod("-")]
        [RubyMethod("sub")]
        public static BigDecimal/*!*/ Subtract(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Subtract(config, self, BigDecimal.Create(config, other));
        }

        [RubyMethod("-")]
        [RubyMethod("sub")]
        public static object Subtract(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "-", self, other);
        }

        [RubyMethod("sub")]
        public static BigDecimal/*!*/ Subtract(RubyContext/*!*/ context, BigDecimal/*!*/ self, BigDecimal/*!*/ other, int n) {
            return BigDecimal.Subtract(GetConfig(context), self, other, n);
        }

        [RubyMethod("sub")]
        public static BigDecimal/*!*/ Subtract(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other, int n) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Subtract(config, self, BigDecimal.Create(config, other), n);
        }

        [RubyMethod("sub")]
        public static BigDecimal/*!*/ Subtract(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other, int n) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Subtract(config, self, BigDecimal.Create(config, other), n);
        }

        [RubyMethod("sub")]
        public static BigDecimal/*!*/ Subtract(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other, int n) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Subtract(config, self, BigDecimal.Create(config, other), n);
        }

        [RubyMethod("sub")]
        public static object Subtract(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, 
            BigDecimal/*!*/ self, object other, [DefaultProtocol]int n) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "sub", self, other);
        }

        #endregion

        #region mult, *

        [RubyMethod("*")]
        public static BigDecimal/*!*/ Multiply(RubyContext/*!*/ context, BigDecimal/*!*/ self, BigDecimal/*!*/ other) {
            return BigDecimal.Multiply(GetConfig(context), self, other);
        }

        [RubyMethod("*")]
        public static BigDecimal/*!*/ Multiply(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Multiply(config, self, BigDecimal.Create(config, other));
        }

        [RubyMethod("*")]
        public static BigDecimal/*!*/ Multiply(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Multiply(config, self, BigDecimal.Create(config, other));
        }

        [RubyMethod("*")]
        public static object Multiply(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, 
            BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "*", self, other);
        }

        [RubyMethod("mult")]
        public static BigDecimal/*!*/ Multiply(RubyContext/*!*/ context, BigDecimal/*!*/ self, BigDecimal/*!*/ other, int n) {
            return BigDecimal.Multiply(GetConfig(context), self, other, n);
        }

        [RubyMethod("mult")]
        public static BigDecimal/*!*/ Multiply(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other, int n) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Multiply(config, self, BigDecimal.Create(config, other), n);
        }

        [RubyMethod("mult")]
        public static BigDecimal/*!*/ Multiply(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other, int n) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Multiply(config, self, BigDecimal.Create(config, other), n);
        }

        [RubyMethod("mult")]
        public static BigDecimal/*!*/ Multiply(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other, int n) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.Multiply(config, self, BigDecimal.Create(config, other), n);
        }

        [RubyMethod("mult")]
        public static object Multiply(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, 
            BigDecimal/*!*/ self, object other, [DefaultProtocol]int n) {
            // TODO: converts result to BigDecimal
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "mult", self, other);
        }

        #endregion

        [RubyMethod("/")]
        [RubyMethod("quo")]
        public static BigDecimal/*!*/ Divide(RubyContext/*!*/ context, BigDecimal/*!*/ self, BigDecimal/*!*/ other) {
            BigDecimal remainder;
            return BigDecimal.Divide(GetConfig(context), self, other, 0, out remainder);
        }

        // An Integer divisor is turned into a BigDecimal, as for #+ and #*. Without these it went
        // through Integer#coerce, which answers a pair of Floats, so BigDecimal("3.14") / 2 was 1.57.
        [RubyMethod("/")]
        [RubyMethod("quo")]
        public static BigDecimal/*!*/ Divide(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            BigDecimal.Config config = GetConfig(context);
            return Divide(context, self, BigDecimal.Create(config, other));
        }

        [RubyMethod("/")]
        [RubyMethod("quo")]
        public static BigDecimal/*!*/ Divide(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            BigDecimal.Config config = GetConfig(context);
            return Divide(context, self, BigDecimal.Create(config, other));
        }

        [RubyMethod("/")]
        public static object Divide(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite,
            BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "/", self, other);
        }

        [RubyMethod("quo")]
        public static object Quotient(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite,
            BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "quo", self, other);
        }

        #region div

        /// <summary>
        /// The special-value rules MRI applies to the integer-division family (#div with no
        /// precision, #divmod). They are not the same as the ones #/ uses: a NaN operand, an
        /// infinite *dividend* and a zero divisor are all errors here, because none of them
        /// has an integer quotient. Note the order - NaN is checked before the zero divisor
        /// (BigDecimal("NaN").div(0) is a FloatDomainError, not a ZeroDivisionError) but the
        /// zero divisor is checked before an infinite dividend.
        /// An infinite *divisor* is not an error: the quotient is 0 when the signs agree and
        /// -1 when they do not, so that div * other + mod still reconstructs self.
        /// </summary>
        private static void IntegerDivMod(BigDecimal.Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y,
            out BigDecimal/*!*/ div, out BigDecimal/*!*/ mod) {

            if (BigDecimal.IsNaN(x) || BigDecimal.IsNaN(y)) {
                throw CreateSpecialValueError(BigDecimal.NaN);
            }
            if (BigDecimal.IsZero(y)) {
                throw new DivideByZeroException("divided by 0");
            }
            if (BigDecimal.IsInfinite(x)) {
                if (BigDecimal.IsInfinite(y)) {
                    throw CreateSpecialValueError(BigDecimal.NaN);
                }
                throw CreateSpecialValueError(x.Sign * y.Sign > 0
                    ? BigDecimal.PositiveInfinity
                    : BigDecimal.NegativeInfinity);
            }
            if (BigDecimal.IsInfinite(y)) {
                if (BigDecimal.IsZero(x) || x.Sign == y.Sign) {
                    div = BigDecimal.Create(config, "0");
                    mod = x;
                } else {
                    div = BigDecimal.Create(config, "-1");
                    mod = y;
                }
                return;
            }
            BigDecimal.DivMod(config, x, y, out div, out mod);
        }

        [RubyMethod("div")]
        public static object Div(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            BigDecimal.Config config = GetConfig(context);
            BigDecimal div, mod;
            IntegerDivMod(config, self, other, out div, out mod);
            // Since bigdecimal 4.0 the precision-less #div answers an Integer, not a BigDecimal.
            return BigDecimal.ToInteger(config, div);
        }

        [RubyMethod("div")]
        public static object Div(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return Div(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("div")]
        public static object Div(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return Div(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("div")]
        public static object Div(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            return Div(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("div")]
        public static object Div(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite,
            BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "div", self, other);
        }

        [RubyMethod("div")]
        public static BigDecimal/*!*/ Div(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other, [DefaultProtocol]int n) {
            if (n < 0) {
                throw RubyExceptions.CreateArgumentError("negative precision");
            }
            BigDecimal remainder;
            return BigDecimal.Divide(GetConfig(context), self, other, n, out remainder);
        }

        [RubyMethod("div")]
        public static BigDecimal/*!*/ Div(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other, [DefaultProtocol]int n) {
            return Div(context, self, BigDecimal.Create(GetConfig(context), other), n);
        }

        [RubyMethod("div")]
        public static BigDecimal/*!*/ Div(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other, [DefaultProtocol]int n) {
            return Div(context, self, BigDecimal.Create(GetConfig(context), other), n);
        }

        [RubyMethod("div")]
        public static BigDecimal/*!*/ Div(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other, [DefaultProtocol]int n) {
            return Div(context, self, BigDecimal.Create(GetConfig(context), other), n);
        }

        #endregion

        #region %, modulo

        /// <summary>
        /// MRI's #%: a NaN operand poisons the result, a zero divisor raises (even for a NaN
        /// divisor's sake we check NaN first), an infinite dividend has no remainder, and an
        /// infinite divisor leaves self alone when the signs agree - when they differ the
        /// floored-division convention forces the answer to be the divisor itself.
        /// </summary>
        private static BigDecimal/*!*/ ModuloCore(BigDecimal.Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y) {
            if (BigDecimal.IsNaN(x) || BigDecimal.IsNaN(y)) {
                return BigDecimal.NaN;
            }
            if (BigDecimal.IsZero(y)) {
                throw new DivideByZeroException("divided by 0");
            }
            if (BigDecimal.IsInfinite(x)) {
                return BigDecimal.NaN;
            }
            if (BigDecimal.IsInfinite(y)) {
                return (BigDecimal.IsZero(x) || x.Sign == y.Sign) ? x : y;
            }
            BigDecimal div, mod;
            BigDecimal.DivMod(config, x, y, out div, out mod);
            return mod;
        }

        // #modulo is not a second implementation of #% but the very same method, which is
        // what BigDecimal.instance_method(:modulo) == BigDecimal.instance_method(:%) asserts:
        // every overload below has to carry both names for the two method groups to be equal.

        [RubyMethod("%")]
        [RubyMethod("modulo")]
        public static BigDecimal/*!*/ Modulo(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            return ModuloCore(GetConfig(context), self, other);
        }

        [RubyMethod("%")]
        [RubyMethod("modulo")]
        public static BigDecimal/*!*/ Modulo(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return Modulo(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("%")]
        [RubyMethod("modulo")]
        public static BigDecimal/*!*/ Modulo(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return Modulo(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("%")]
        [RubyMethod("modulo")]
        public static BigDecimal/*!*/ Modulo(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            // A Float operand does not drag the result down to a Float: MRI keeps it a BigDecimal.
            return Modulo(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("%")]
        [RubyMethod("modulo")]
        public static object Modulo(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite,
            RubyContext/*!*/ context, BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "%", self, other);
        }

        #endregion

        [RubyMethod("**")]
        [RubyMethod("power")]
        public static BigDecimal/*!*/ Power(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return BigDecimal.Power(GetConfig(context), self, other);
        }

        [RubyMethod("+@")]
        public static BigDecimal/*!*/ Identity(BigDecimal/*!*/ self) {
            return self;
        }

        [RubyMethod("-@")]
        public static BigDecimal/*!*/ Negate(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            return BigDecimal.Negate(GetConfig(context), self);
        }

        [RubyMethod("abs")]
        public static BigDecimal/*!*/ Abs(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            return BigDecimal.Abs(GetConfig(context), self);
        }

        #region divmod

        [RubyMethod("divmod")]
        public static RubyArray/*!*/ DivMod(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            BigDecimal.Config config = GetConfig(context);
            BigDecimal div, mod;
            IntegerDivMod(config, self, other, out div, out mod);
            // Since bigdecimal 4.0 the quotient is an Integer, as it is for Integer#divmod.
            return RubyOps.MakeArray2(BigDecimal.ToInteger(config, div), mod);
        }

        [RubyMethod("divmod")]
        public static RubyArray/*!*/ DivMod(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            return DivMod(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("divmod")]
        public static RubyArray/*!*/ DivMod(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return DivMod(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("divmod")]
        public static RubyArray/*!*/ DivMod(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return DivMod(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("divmod")]
        public static object DivMod(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, 
            RubyContext/*!*/ context, BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "divmod", self, other);
        }

        #endregion

        #region remainder

        /// <summary>
        /// #remainder truncates where #% floors, so it differs from #% only when the signs
        /// disagree - and then only when the remainder is non-zero: BigDecimal("4").remainder(-2)
        /// is 0, not 2.
        /// An infinite divisor leaves self untouched here (no sign-dependent special case),
        /// but a zero divisor still raises, and NaN still wins over the zero check.
        /// </summary>
        [RubyMethod("remainder")]
        public static BigDecimal/*!*/ Remainder(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            BigDecimal.Config config = GetConfig(context);
            if (BigDecimal.IsNaN(self) || BigDecimal.IsNaN(other)) {
                return BigDecimal.NaN;
            }
            if (BigDecimal.IsZero(other)) {
                throw new DivideByZeroException("divided by 0");
            }
            if (BigDecimal.IsInfinite(self)) {
                return BigDecimal.NaN;
            }
            if (BigDecimal.IsInfinite(other)) {
                return self;
            }
            BigDecimal mod = ModuloCore(config, self, other);
            if (BigDecimal.IsZero(mod) || self.Sign == other.Sign) {
                return mod;
            }
            return BigDecimal.Subtract(config, mod, other);
        }

        [RubyMethod("remainder")]
        public static BigDecimal/*!*/ Remainder(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return Remainder(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("remainder")]
        public static BigDecimal/*!*/ Remainder(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return Remainder(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("remainder")]
        public static BigDecimal/*!*/ Remainder(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            return Remainder(context, self, BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("remainder")]
        public static object Remainder(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "remainder", self, other);
        }

        #endregion

        [RubyMethod("sqrt")]
        public static BigDecimal/*!*/ SquareRoot(RubyContext/*!*/ context, BigDecimal/*!*/ self, int n) {
            return BigDecimal.SquareRoot(GetConfig(context), self, n);
        }

        [RubyMethod("sqrt")]
        public static object SquareRoot(RubyContext/*!*/ context, BigDecimal/*!*/ self, object n) {
            throw RubyExceptions.CreateUnexpectedTypeError(context, n, "Integer");
        }

        #endregion

        #region Comparison Operations

        #region <=>
         
        [RubyMethod("<=>")]
        public static object Compare(BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            return self.CompareBigDecimal(other);
        }

        [RubyMethod("<=>")]
        public static object Compare(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("<=>")]
        public static object Compare(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("<=>")]
        public static object Compare(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            return self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndCompare(coercionStorage, comparisonStorage, self, other);
        }

        #endregion

        #region >

        private static object GreaterThenResult(int? comparisonResult) {
            // NaN makes the comparison unordered; <=> answers nil for that but the relational
            // operators are all simply false.
            return ScriptingRuntimeHelpers.BooleanToObject(comparisonResult.HasValue && comparisonResult.Value > 0);
        }

        [RubyMethod(">")]
        public static object GreaterThan(BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            return GreaterThenResult(self.CompareBigDecimal(other));
        }

        [RubyMethod(">")]
        public static object GreaterThan(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return GreaterThenResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
            
        }

        [RubyMethod(">")]
        public static object GreaterThan(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return GreaterThenResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod(">")]
        public static object GreaterThan(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            return GreaterThenResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod(">")]
        public static object GreaterThan(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, ">", self, other); 
        }

        #endregion

        #region >=

        private static object GreaterThanOrEqualResult(int? comparisonResult) {
            return ScriptingRuntimeHelpers.BooleanToObject(comparisonResult.HasValue && comparisonResult.Value >= 0);
        }

        [RubyMethod(">=")]
        public static object GreaterThanOrEqual(BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            return GreaterThanOrEqualResult(self.CompareBigDecimal(other));
        }

        [RubyMethod(">=")]
        public static object GreaterThanOrEqual(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return GreaterThanOrEqualResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod(">=")]
        public static object GreaterThanOrEqual(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return GreaterThanOrEqualResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod(">=")]
        public static object GreaterThanOrEqual(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            return GreaterThanOrEqualResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod(">=")]
        public static object GreaterThanOrEqual(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, 
            BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, ">=", self, other);
        }

        #endregion

        #region <

        private static object LessThenResult(int? comparisonResult) {
            return ScriptingRuntimeHelpers.BooleanToObject(comparisonResult.HasValue && comparisonResult.Value < 0);
        }

        [RubyMethod("<")]
        public static object LessThan(BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            return LessThenResult(self.CompareBigDecimal(other));
        }

        [RubyMethod("<")]
        public static object LessThan(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return LessThenResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod("<")]
        public static object LessThan(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return LessThenResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod("<")]
        public static object LessThan(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            return LessThenResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod("<")]
        public static object LessThan(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, 
            BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, "<", self, other);
        }

        #endregion

        #region <=

        private static object LessThanOrEqualResult(int? comparisonResult) {
            return ScriptingRuntimeHelpers.BooleanToObject(comparisonResult.HasValue && comparisonResult.Value <= 0);
        }
        
        [RubyMethod("<=")]
        public static object LessThanOrEqual(BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            return LessThanOrEqualResult(self.CompareBigDecimal(other));
        }

        [RubyMethod("<=")]
        public static object LessThanOrEqual(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return LessThanOrEqualResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod("<=")]
        public static object LessThanOrEqual(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return LessThanOrEqualResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod("<=")]
        public static object LessThanOrEqual(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            return LessThanOrEqualResult(self.CompareBigDecimal(BigDecimal.Create(GetConfig(context), other)));
        }

        [RubyMethod("<=")]
        public static object LessThanOrEqual(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, 
            BigDecimal/*!*/ self, object other) {
            return Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, "<=", self, other);
        }

        #endregion

        #region eql?, ==, ===

        [RubyMethod("eql?")]
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static object Equal(BigDecimal/*!*/ self, [NotNull]BigDecimal/*!*/ other) {
            // NaN is not equal to anything, including itself - and answers false, not nil.
            if (BigDecimal.IsNaN(self) || BigDecimal.IsNaN(other)) {
                return ScriptingRuntimeHelpers.False;
            }
            return self.Equals(other);
        }

        [RubyMethod("eql?")]
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(RubyContext/*!*/ context, BigDecimal/*!*/ self, int other) {
            return self.Equals(BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("eql?")]
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(RubyContext/*!*/ context, BigDecimal/*!*/ self, [NotNull]BigInteger/*!*/ other) {
            return self.Equals(BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("eql?")]
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equal(RubyContext/*!*/ context, BigDecimal/*!*/ self, double other) {
            return self.Equals(BigDecimal.Create(GetConfig(context), other));
        }

        [RubyMethod("eql?")] // HACK - this is actually semantically wrong but is what the BigDecimal library does.
        [RubyMethod("==")]
        [RubyMethod("===")]
        public static object Equal(BinaryOpStorage/*!*/ equals, BigDecimal/*!*/ self, object other) {
            if (other == null) {
                return ScriptingRuntimeHelpers.False;
            }
            return Protocols.IsEqual(equals, other, self);
        }

        #endregion

        #endregion

        #region Rounding Operations

        /// <summary>
        /// A rounding mode is one of the ROUND_* constants or the Symbol that names it. MRI
        /// takes both, and reports anything else as "invalid rounding mode (x)" whichever it was.
        /// </summary>
        internal static BigDecimal.RoundingModes ToRoundingMode(ConversionStorage<int>/*!*/ fixnumCast,
            RubyContext/*!*/ context, object mode) {

            if (mode == null || mode is Missing) {
                return GetConfig(context).RoundingMode;
            }

            var symbol = mode as RubySymbol;
            string name = (symbol != null) ? symbol.ToString() : null;
            if (name == null) {
                var str = mode as MutableString;
                if (str != null) {
                    name = str.ToString();
                }
            }

            if (name != null) {
                switch (name) {
                    case "up": return BigDecimal.RoundingModes.Up;
                    case "down": case "truncate": return BigDecimal.RoundingModes.Down;
                    case "half_up": case "default": return BigDecimal.RoundingModes.HalfUp;
                    case "half_down": return BigDecimal.RoundingModes.HalfDown;
                    case "half_even": case "banker": return BigDecimal.RoundingModes.HalfEven;
                    case "ceiling": case "ceil": return BigDecimal.RoundingModes.Ceiling;
                    case "floor": return BigDecimal.RoundingModes.Floor;
                }
                throw RubyExceptions.CreateArgumentError("invalid rounding mode ({0})", name);
            }

            int value = Protocols.CastToFixnum(fixnumCast, mode);
            if (value == (int)BigDecimal.RoundingModes.None || !Enum.IsDefined(typeof(BigDecimal.RoundingModes), value)) {
                throw RubyExceptions.CreateArgumentError("invalid rounding mode ({0})", value);
            }
            return (BigDecimal.RoundingModes)value;
        }

        /// <summary>
        /// These four answer an Integer when the result keeps no decimal places, and a BigDecimal
        /// otherwise - and where the answer would be an Integer, a special value raises, because
        /// there is no Integer for it to be.
        ///
        /// They disagree about where that line is, and MRI is the authority on it: #round answers
        /// an Integer for any precision &lt;= 0, so BigDecimal("123.456").round(0) is 123, while
        /// #truncate, #ceil and #floor only do so when no precision is given at all - .truncate(0)
        /// is 0.123e3.  <paramref name="integerAtZeroPrecision"/> is which of the two it is.
        /// </summary>
        private static object LimitPrecision(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context,
            BigDecimal/*!*/ self, object n, BigDecimal.RoundingModes mode, bool integerAtZeroPrecision) {

            bool digitsGiven = !(n == null || n is Missing);
            int digits = digitsGiven ? Protocols.CastToFixnum(fixnumCast, n) : 0;
            bool answersInteger = !digitsGiven || (integerAtZeroPrecision && digits <= 0);

            if (!answersInteger) {
                return BigDecimal.LimitPrecision(GetConfig(context), self, digits, mode);
            }

            if (!BigDecimal.IsFinite(self)) {
                throw CreateSpecialValueError(self);
            }
            return BigDecimal.ToInteger(GetConfig(context), BigDecimal.LimitPrecision(GetConfig(context), self, digits, mode));
        }

        internal static Exception/*!*/ CreateSpecialValueError(BigDecimal/*!*/ self) {
            string description = BigDecimal.IsNaN(self)
                ? "'NaN' (Not a Number)"
                : (self.Sign > 0 ? "'Infinity'" : "'-Infinity'");
            return new FloatDomainError(String.Format("Computation results in {0}", description));
        }

        [RubyMethod("ceil")]
        public static object Ceil(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, BigDecimal/*!*/ self,
            [Optional]object n) {

            return LimitPrecision(fixnumCast, context, self, n, BigDecimal.RoundingModes.Ceiling, false);
        }

        [RubyMethod("floor")]
        public static object Floor(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, BigDecimal/*!*/ self,
            [Optional]object n) {

            return LimitPrecision(fixnumCast, context, self, n, BigDecimal.RoundingModes.Floor, false);
        }

        [RubyMethod("round")]
        public static object Round(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, BigDecimal/*!*/ self,
            [Optional]object n, [Optional]object mode) {

            return LimitPrecision(fixnumCast, context, self, n, ToRoundingMode(fixnumCast, context, mode), true);
        }

        [RubyMethod("truncate")]
        public static object Truncate(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, BigDecimal/*!*/ self,
            [Optional]object n) {

            return LimitPrecision(fixnumCast, context, self, n, BigDecimal.RoundingModes.Down, false);
        }

        #endregion

        #region Tests

        [RubyMethod("finite?")]
        public static bool IsFinite(BigDecimal/*!*/ self) {
            return BigDecimal.IsFinite(self);
        }

        [RubyMethod("infinite?")]
        public static object IsInfinite(BigDecimal/*!*/ self) {
            if (BigDecimal.IsInfinite(self)) {
                return self.Sign;
            } else {
                return null;
            }
        }

        [RubyMethod("nan?")]
        public static bool IsNaN(BigDecimal/*!*/ self) {
            return BigDecimal.IsNaN(self);
        }

        [RubyMethod("nonzero?")]
        public static BigDecimal IsNonZero(BigDecimal/*!*/ self) {
            if (!BigDecimal.IsZero(self)) {
                return self;
            } else {
                return null;
            }
        }

        [RubyMethod("zero?")]
        public static bool IsZero(BigDecimal/*!*/ self) {
            return BigDecimal.IsZero(self);
        }

        #endregion

        #endregion
    }
}
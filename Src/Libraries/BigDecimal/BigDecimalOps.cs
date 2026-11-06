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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using IronRuby.Builtins;
using IronRuby.Runtime;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Globalization;

namespace IronRuby.StandardLibrary.BigDecimal {
    [RubyClass("BigDecimal", Inherits = typeof(Numeric), Extends = typeof(BigDecimal), Restrictions = ModuleRestrictions.NoUnderlyingType)]
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
        public static BigDecimal/*!*/ CreateBigDecimal(RubyContext/*!*/ context, RubyClass/*!*/ self, [DefaultProtocol]MutableString/*!*/ value) {
            string str = value.ConvertToString();
            if (!BigDecimal.IsValidNumericString(str)) {
                throw RubyExceptions.CreateArgumentError("invalid value for BigDecimal(): \"{0}\"", str);
            }
            return BigDecimal.Create(GetConfig(context), str);
        }

        #endregion

        #region Constants

        [RubyConstant]
        public const int BASE = (int)BigDecimal.BASE;
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

        /// <summary>
        /// "&lt;digits&gt;:&lt;value&gt;" as #_dump writes it. The digit count sizes MRI's storage and is only
        /// checked here; the value is read as Kernel#BigDecimal reads a String.
        /// </summary>
        [RubyMethod("_load", RubyMethodAttributes.PublicSingleton)]
        public static BigDecimal/*!*/ Load(RubyContext/*!*/ context, RubyClass/*!*/ self, [DefaultProtocol]MutableString/*!*/ str) {
            string text = str.ToString();
            int colon = text.IndexOf(':');
            for (int i = 0; i < (colon < 0 ? text.Length + 1 : colon); i++) {
                if (i == text.Length || text[i] < '0' || text[i] > '9') {
                    throw RubyExceptions.CreateTypeError("load failed: invalid character in the marshaled string");
                }
            }
            return CreateBigDecimal(context, self, MutableString.Create(text.Substring(colon + 1), str.Encoding));
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

        /// <summary>
        /// Parses as much of the string as looks like a number and answers zero when none of
        /// it does, where Kernel#BigDecimal would raise. This is what String#to_d uses.
        /// </summary>
        [RubyMethod("interpret_loosely", RubyMethodAttributes.PublicSingleton)]
        public static BigDecimal/*!*/ InterpretLoosely(RubyContext/*!*/ context, RubyClass/*!*/ self, [DefaultProtocol]MutableString/*!*/ value) {
            return BigDecimal.Create(GetConfig(context), value.ConvertToString());
        }

        #endregion

        #region Instance Methods

        #region Properties

        [RubyMethod("sign")]
        public static int Sign(BigDecimal/*!*/ self) {
            return self.GetSignCode();
        }

        [RubyMethod("exponent")]
        public static int Exponent(BigDecimal/*!*/ self) {
            return ZeroAwareExponent(self);
        }

        // A zero's exponent is 0 in MRI. The zeros computed here - BigDecimal("7").frac, 1 - 1 - carry
        // the PositiveZero's 1, so their #exponent and #split said 1, and BigDecimal#to_digits (util.rb)
        // made "0" * -1 of it - "negative argument" for every whole number.
        private static int ZeroAwareExponent(BigDecimal/*!*/ self) {
            return BigDecimal.IsZero(self) ? 0 : self.Exponent;
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

        [RubyMethod("split")]
        public static RubyArray/*!*/ Split(BigDecimal/*!*/ self) {
            return RubyOps.MakeArray4(self.Sign, MutableString.CreateAscii(self.GetFractionString()), 10, ZeroAwareExponent(self));
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

        // An Integer, a Float or a BigDecimal becomes a BigDecimal (a Rational too - bigdecimal.rb expands it).
        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(RubyContext/*!*/ context, BigDecimal/*!*/ self, object other) {
            BigDecimal value = ToBigDecimal(GetConfig(context), other) ?? throw RubyExceptions.MakeCoercionError(context, other, self);
            return RubyOps.MakeArray2(value, self);
        }

        #endregion

        #region _dump

        [RubyMethod("_dump")]
        public static MutableString/*!*/ Dump(BigDecimal/*!*/ self, [Optional]object limit) {
            // The limit is ignored as a BigDecimal holds no other objects. The prefix is the digits MRI's
            // words hold: 9 to a word, the words aligned at the decimal point, so "18:0.15e1" for 1.5.
            int words = 1;
            if (BigDecimal.IsFinite(self) && !BigDecimal.IsZero(self)) {
                int exponent = self.Exponent;
                int digits = self.GetFractionString().Length;
                words = FloorDiv(exponent - 1, BigDecimal.BASE_FIG) - FloorDiv(exponent - digits, BigDecimal.BASE_FIG) + 1;
            }
            return MutableString.CreateMutable(RubyEncoding.Binary).
                Append((words * BigDecimal.BASE_FIG).ToString(CultureInfo.InvariantCulture)).
                Append(':').
                Append(self.ToString()
            );
        }

        private static int FloorDiv(int a, int b) {
            return a / b - (a % b < 0 ? 1 : 0);
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

        /// <summary>
        /// The operand of an arithmetic or comparison operator as a BigDecimal: an Integer exactly, and a
        /// Float by its shortest digits - so BigDecimal("1.5") * 1.5 is 0.225e1, a BigDecimal, as in MRI
        /// rather than the Float that Float's own coercion would make it. Anything else (but a Rational,
        /// which bigdecimal.rb expands first) is null and goes through #coerce.
        /// </summary>
        private static BigDecimal ToBigDecimal(BigDecimal.Config/*!*/ config, object other) {
            switch (other) {
                case BigDecimal value: return value;
                case int value: return BigDecimal.Create(config, value.ToString(CultureInfo.InvariantCulture));
                case long value: return BigDecimal.Create(config, value.ToString(CultureInfo.InvariantCulture));
                case BigInteger value: return BigDecimal.Create(config, value.ToString(CultureInfo.InvariantCulture));
                case double value: return BigDecimal.Create(config, value);
                default: return null;
            }
        }

        private static BigDecimal/*!*/ ToBigDecimalOrError(BigDecimal.Config/*!*/ config, RubyContext/*!*/ context, BigDecimal/*!*/ self, object other) {
            return ToBigDecimal(config, other) ?? throw RubyExceptions.MakeCoercionError(context, other, self);
        }

        [RubyMethod("+")]
        public static object Add(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null ? BigDecimal.Add(config, self, y) : Protocols.CoerceAndApply(coercionStorage, opStorage, "+", self, other);
        }

        [RubyMethod("add")]
        public static object Add(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other, [DefaultProtocol]int n) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null ? BigDecimal.Add(config, self, y, n) : Protocols.CoerceAndApply(coercionStorage, opStorage, "+", self, other);
        }

        [RubyMethod("-")]
        public static object Subtract(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null ? BigDecimal.Subtract(config, self, y) : Protocols.CoerceAndApply(coercionStorage, opStorage, "-", self, other);
        }

        [RubyMethod("sub")]
        public static object Subtract(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other, [DefaultProtocol]int n) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null ? BigDecimal.Subtract(config, self, y, n) : Protocols.CoerceAndApply(coercionStorage, opStorage, "-", self, other);
        }

        [RubyMethod("*")]
        public static object Multiply(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null ? BigDecimal.Multiply(config, self, y) : Protocols.CoerceAndApply(coercionStorage, opStorage, "*", self, other);
        }

        [RubyMethod("mult")]
        public static object Multiply(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other, [DefaultProtocol]int n) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null ? BigDecimal.Multiply(config, self, y, n) : Protocols.CoerceAndApply(coercionStorage, opStorage, "*", self, other);
        }

        [RubyMethod("/")]
        public static object Divide(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null
                ? BigDecimal.DivideRounded(config, self, y, DivisionDigits(config, self, y), config.RoundingMode)
                : Protocols.CoerceAndApply(coercionStorage, opStorage, "/", self, other);
        }

        /// <summary>
        /// The significant digits of x / y in bigdecimal 4: the precision of the more precise operand plus
        /// 16 (BIGDECIMAL_DOUBLE_FIGURES), at least 32, and no more than BigDecimal.limit when one is set.
        /// </summary>
        private static int DivisionDigits(BigDecimal.Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y) {
            int digits = Math.Max(Precision(x), Precision(y)) + 16;
            if (digits < 32) {
                digits = 32;
            }
            if (config.Limit > 0 && config.Limit < digits) {
                digits = config.Limit;
            }
            return digits;
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
        public static object Div(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            if (y == null) {
                return Protocols.CoerceAndApply(coercionStorage, opStorage, "div", self, other);
            }
            IntegerDivMod(config, self, y, out BigDecimal div, out _);
            // Since bigdecimal 4.0 the precision-less #div answers an Integer, not a BigDecimal.
            return BigDecimal.ToInteger(config, div);
        }

        [RubyMethod("div")]
        public static BigDecimal/*!*/ Div(RubyContext/*!*/ context, BigDecimal/*!*/ self, object other, [DefaultProtocol]int n) {
            if (n < 0) {
                throw RubyExceptions.CreateArgumentError("negative precision");
            }
            // div(other, 0) is other / self's quotient as #/ makes it; div(other, n) has n digits
            BigDecimal.Config config = GetConfig(context);
            BigDecimal y = ToBigDecimalOrError(config, context, self, other);
            return BigDecimal.DivideRounded(config, self, y, n == 0 ? DivisionDigits(config, self, y) : n, config.RoundingMode);
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

        // #modulo is the very same method as #% (BigDecimal.instance_method(:modulo) == instance_method(:%)),
        // which bigdecimal.rb re-establishes after wrapping #%.
        [RubyMethod("%")]
        [RubyMethod("modulo")]
        public static object Modulo(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null ? ModuloCore(config, self, y) : Protocols.CoerceAndApply(coercionStorage, opStorage, "%", self, other);
        }

        #endregion

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

        [RubyMethod("divmod")]
        public static object DivMod(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            if (y == null) {
                return Protocols.CoerceAndApply(coercionStorage, opStorage, "divmod", self, other);
            }
            IntegerDivMod(config, self, y, out BigDecimal div, out BigDecimal mod);
            // Since bigdecimal 4.0 the quotient is an Integer, as it is for Integer#divmod.
            return RubyOps.MakeArray2(BigDecimal.ToInteger(config, div), mod);
        }

        /// <summary>
        /// #remainder truncates where #% floors, so it differs from #% only when the signs
        /// disagree - and then only when the remainder is non-zero: BigDecimal("4").remainder(-2)
        /// is 0, not 2.
        /// An infinite divisor leaves self untouched here (no sign-dependent special case),
        /// but a zero divisor still raises, and NaN still wins over the zero check.
        /// </summary>
        [RubyMethod("remainder")]
        public static object Remainder(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            if (y == null) {
                return Protocols.CoerceAndApply(coercionStorage, opStorage, "remainder", self, other);
            }
            if (BigDecimal.IsNaN(self) || BigDecimal.IsNaN(y)) {
                return BigDecimal.NaN;
            }
            if (BigDecimal.IsZero(y)) {
                throw new DivideByZeroException("divided by 0");
            }
            if (BigDecimal.IsInfinite(self)) {
                return BigDecimal.NaN;
            }
            if (BigDecimal.IsInfinite(y)) {
                return self;
            }
            BigDecimal mod = ModuloCore(config, self, y);
            if (BigDecimal.IsZero(mod) || self.Sign == y.Sign) {
                return mod;
            }
            return BigDecimal.Subtract(config, mod, y);
        }

        // #** and #power are bigdecimal 4's own Ruby (bigdecimal.rb); #sqrt stays here, where the root
        // is an exact BigInteger one rather than Newton's iteration on BigDecimals.
        [RubyMethod("sqrt")]
        public static BigDecimal/*!*/ SquareRoot(RubyContext/*!*/ context, BigDecimal/*!*/ self, [DefaultProtocol]int n) {
            return BigDecimal.SquareRoot(GetConfig(context), self, n);
        }

        #endregion

        #region Comparison Operations

        // NaN makes a comparison unordered: <=> answers nil for it, and the relational operators false.

        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(coercionStorage.Context), other);
            return y != null ? self.CompareBigDecimal(y) : Protocols.CoerceAndCompare(coercionStorage, comparisonStorage, self, other);
        }

        [RubyMethod(">")]
        public static object GreaterThan(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(coercionStorage.Context), other);
            return y != null ? self.CompareBigDecimal(y) > 0 : Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, ">", self, other);
        }

        [RubyMethod(">=")]
        public static object GreaterThanOrEqual(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(coercionStorage.Context), other);
            return y != null ? self.CompareBigDecimal(y) >= 0 : Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, ">=", self, other);
        }

        [RubyMethod("<")]
        public static object LessThan(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(coercionStorage.Context), other);
            return y != null ? self.CompareBigDecimal(y) < 0 : Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, "<", self, other);
        }

        [RubyMethod("<=")]
        public static object LessThanOrEqual(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(coercionStorage.Context), other);
            return y != null ? self.CompareBigDecimal(y) <= 0 : Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, "<=", self, other);
        }

        // #eql? and #=== are #== itself, aliased in bigdecimal.rb. Any other operand is compared the way
        // MRI's rb_num_coerce_cmp does - other.coerce(self), then == - and is unequal when it cannot be
        // coerced. (Asking other == self instead never ended for an operand whose == asks back.)
        [RubyMethod("==")]
        public static bool Equal(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ equals, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(equals.Context), other);
            if (y != null) {
                // NaN is not equal to anything, including itself.
                return !BigDecimal.IsNaN(self) && !BigDecimal.IsNaN(y) && self.Equals(y);
            }
            return RubyOps.IsTrue(Protocols.TryCoerceAndApply(coercionStorage, equals, "==", self, other));
        }

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

            // MRI's VpMidRound counts the rounding position from the start of the number's first 9-digit
            // word; a position before it rounds to zero in every mode but ceiling and floor - which only
            // changes ROUND_UP: BigDecimal("0.04").round(-1, :up) and 1e-10.round(1, :up) are 0 in CRuby,
            // where 0.0004.round(1, :up) is 0.1.
            if (mode == BigDecimal.RoundingModes.Up && BigDecimal.IsFinite(self) && !BigDecimal.IsZero(self)) {
                int exponent = self.Exponent;
                int wordExponent = exponent > 0 ? (exponent + IronRuby.StandardLibrary.BigDecimal.Fraction.BASE_FIG - 1) / IronRuby.StandardLibrary.BigDecimal.Fraction.BASE_FIG : -((-exponent) / IronRuby.StandardLibrary.BigDecimal.Fraction.BASE_FIG);
                if ((long)digits + (long)wordExponent * IronRuby.StandardLibrary.BigDecimal.Fraction.BASE_FIG < 0) {
                    var zero = BigDecimal.LimitPrecision(GetConfig(context), self, digits, BigDecimal.RoundingModes.Down);
                    return answersInteger ? BigDecimal.ToInteger(GetConfig(context), zero) : zero;
                }
            }

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

            // round, and round(n) for n <= 0, answer an Integer; given a rounding mode as well, round(n, mode)
            // answers a BigDecimal whatever n is, as in MRI - and so does round(half: ...), whose options
            // hash MRI counts as the first argument.
            if (n is IDictionary<object, object> nOptions) {
                return LimitPrecision(fixnumCast, context, self, 0, ToHalfRoundingMode(context, nOptions), false);
            }
            if (mode is IDictionary<object, object> options) {
                return LimitPrecision(fixnumCast, context, self, n, ToHalfRoundingMode(context, options), false);
            }
            bool modeGiven = !(mode == null || mode is Missing);
            return LimitPrecision(fixnumCast, context, self, n, ToRoundingMode(fixnumCast, context, mode), !modeGiven);
        }

        /// <summary>
        /// MRI's check_rounding_mode_option: half: :up, :down or :even (or the String), and the current
        /// mode when it is nil or not given.
        /// </summary>
        private static BigDecimal.RoundingModes ToHalfRoundingMode(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ options) {
            object half;
            if (!options.TryGetValue(context.CreateAsciiSymbol("half"), out half) || half == null) {
                return GetConfig(context).RoundingMode;
            }
            string name = (half as RubySymbol)?.ToString() ?? (half as MutableString)?.ToString();
            switch (name) {
                case "up": return BigDecimal.RoundingModes.HalfUp;
                case "down": return BigDecimal.RoundingModes.HalfDown;
                case "even": return BigDecimal.RoundingModes.HalfEven;
            }
            throw RubyExceptions.CreateArgumentError("invalid rounding mode ({0})", name ?? context.Inspect(half).ToString());
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
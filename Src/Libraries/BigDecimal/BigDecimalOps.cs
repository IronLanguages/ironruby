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
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.StandardLibrary.BigDecimal {
    /// <summary>
    /// The C extension half of the bigdecimal 4.0.1 gem; its Ruby half (power, sqrt, BigMath.log/exp,
    /// Kernel#BigDecimal's dispatch on the argument's class) is Src/StdLib/ironruby/bigdecimal.rb.
    /// </summary>
    [RubyClass("BigDecimal", Inherits = typeof(Numeric), Extends = typeof(BigDecimal), Restrictions = ModuleRestrictions.NoUnderlyingType)]
    public sealed class BigDecimalOps {

        internal static readonly object BigDecimalOpsClassKey = new object();

        internal static BigDecimal.Config GetConfig(RubyContext/*!*/ context) {
            ContractUtils.RequiresNotNull(context, "context");
            return (BigDecimal.Config)context.GetOrCreateLibraryData(BigDecimalOpsClassKey, () => new BigDecimal.Config());
        }

        #region Constants

        [RubyConstant]
        public const int BASE = BigDecimal.BASE;
        [RubyConstant]
        public const int EXCEPTION_ALL = (int)BigDecimal.ExceptionModes.All;
        [RubyConstant]
        public const int EXCEPTION_INFINITY = (int)BigDecimal.ExceptionModes.Infinity;
        [RubyConstant]
        public const int EXCEPTION_NaN = (int)BigDecimal.ExceptionModes.NaN;
        [RubyConstant]
        public const int EXCEPTION_OVERFLOW = (int)BigDecimal.ExceptionModes.Overflow;
        [RubyConstant]
        public const int EXCEPTION_UNDERFLOW = (int)BigDecimal.ExceptionModes.Underflow;
        [RubyConstant]
        public const int EXCEPTION_ZERODIVIDE = (int)BigDecimal.ExceptionModes.ZeroDivide;
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

        #region Creation

        private static string/*!*/ ToCString(MutableString/*!*/ str) {
            string value = str.ConvertToString();
            if (value.IndexOf('\0') >= 0) {
                throw RubyExceptions.CreateArgumentError("string contains null byte");
            }
            return value;
        }

        /// <summary>
        /// Kernel#BigDecimal of a String: the whole of it must be a number. An invalid one raises, or is
        /// nil when <paramref name="exception"/> is false.
        /// </summary>
        [RubyMethod("__from_string", RubyMethodAttributes.PrivateSingleton)]
        public static BigDecimal FromString(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]MutableString/*!*/ value, bool exception) {
            string str = ToCString(value);
            BigDecimal result = BigDecimal.Parse(GetConfig(context), str, true);
            if (result == null && exception) {
                throw RubyExceptions.CreateArgumentError("invalid value for BigDecimal(): \"" + str + "\"");
            }
            return result;
        }

        /// <summary>
        /// Kernel#BigDecimal of an Integer (exactly) or of a Float (to <paramref name="digits"/> digits,
        /// 0 for the shortest form that reads back as the same Float).
        /// </summary>
        [RubyMethod("__from_number", RubyMethodAttributes.PrivateSingleton)]
        public static BigDecimal/*!*/ FromNumber(RubyContext/*!*/ context, RubyClass/*!*/ self, object value, int digits) {
            BigDecimal.Config config = GetConfig(context);
            switch (value) {
                case int i: return BigDecimal.Create(config, i);
                case long l: return BigDecimal.Create(config, l);
                case BigInteger b: return BigDecimal.Create(config, b);
                case double d: return BigDecimal.Create(config, d, digits);
                default: throw RubyExceptions.CreateUnexpectedTypeError(context, value, "Integer");
            }
        }

        /// <summary>
        /// Parses as much of the string as looks like a number and answers zero when none of
        /// it does, where Kernel#BigDecimal would raise. This is what String#to_d uses.
        /// </summary>
        [RubyMethod("interpret_loosely", RubyMethodAttributes.PublicSingleton)]
        public static BigDecimal/*!*/ InterpretLoosely(RubyContext/*!*/ context, RubyClass/*!*/ self, [DefaultProtocol]MutableString/*!*/ value) {
            return BigDecimal.Parse(GetConfig(context), ToCString(value), false);
        }

        /// <summary>
        /// "&lt;digits&gt;:&lt;value&gt;" as #_dump writes it. The digit count sizes MRI's storage and is only
        /// checked here; the value is read as Kernel#BigDecimal reads a String.
        /// </summary>
        [RubyMethod("_load", RubyMethodAttributes.PublicSingleton)]
        public static BigDecimal/*!*/ Load(RubyContext/*!*/ context, RubyClass/*!*/ self, [DefaultProtocol]MutableString/*!*/ str) {
            string text = ToCString(str);
            int colon = text.IndexOf(':');
            for (int i = 0; i < (colon < 0 ? text.Length : colon); i++) {
                if (text[i] < '0' || text[i] > '9') {
                    throw RubyExceptions.CreateTypeError("load failed: invalid character in the marshaled string");
                }
            }
            string value = colon < 0 ? "" : text.Substring(colon + 1);
            BigDecimal result = BigDecimal.Parse(GetConfig(context), value, true);
            if (result == null) {
                throw RubyExceptions.CreateArgumentError("invalid value for BigDecimal(): \"" + value + "\"");
            }
            return result;
        }

        #endregion

        #region Singleton Methods

        [RubyMethod("double_fig", RubyMethodAttributes.PublicSingleton)]
        public static int DoubleFig(RubyClass/*!*/ self) {
            return BigDecimal.DoubleFig;
        }

        /// <summary>
        /// BigDecimal.mode(EXCEPTION_*[, true/false]) reads or sets exception flags and answers all of
        /// them; BigDecimal.mode(ROUND_MODE[, mode]) reads or sets the rounding mode.
        /// </summary>
        [RubyMethod("mode", RubyMethodAttributes.PublicSingleton)]
        public static int Mode(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, RubyClass/*!*/ self,
            [DefaultProtocol]int which, [Optional]object value) {

            BigDecimal.Config config = GetConfig(context);
            bool hasValue = !(value is Missing) && value != null;
            if ((which & EXCEPTION_ALL) != 0) {
                if (!hasValue) {
                    return (int)config.ExceptionMode;
                }
                if (!(value is bool)) {
                    throw RubyExceptions.CreateArgumentError("second argument must be true or false");
                }
                foreach (var flag in new[] { BigDecimal.ExceptionModes.Infinity, BigDecimal.ExceptionModes.NaN,
                    BigDecimal.ExceptionModes.Underflow, BigDecimal.ExceptionModes.ZeroDivide }) {

                    if ((which & (int)flag) != 0) {
                        config.ExceptionMode = (bool)value ? config.ExceptionMode | flag : config.ExceptionMode & ~flag;
                    }
                }
                return (int)config.ExceptionMode;
            }
            if (which == ROUND_MODE) {
                if (hasValue) {
                    config.RoundingMode = ToRoundingMode(fixnumCast, value);
                }
                return (int)config.RoundingMode;
            }
            throw RubyExceptions.CreateTypeError("first argument for BigDecimal.mode invalid");
        }

        /// <summary>BigDecimal.limit([n]) answers the limit there was; nil leaves it as it is.</summary>
        [RubyMethod("limit", RubyMethodAttributes.PublicSingleton)]
        public static int Limit(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, RubyClass/*!*/ self, [Optional]object n) {
            BigDecimal.Config config = GetConfig(context);
            int limit = config.Limit;
            if (!(n is Missing) && n != null) {
                int value = Protocols.CastToFixnum(fixnumCast, n);
                if (value < 0) {
                    throw RubyExceptions.CreateArgumentError("argument must be positive");
                }
                config.Limit = value;
            }
            return limit;
        }

        #endregion

        #region Properties

        [RubyMethod("sign")]
        public static int Sign(BigDecimal/*!*/ self) {
            return self.SignCode;
        }

        [RubyMethod("exponent")]
        public static object/*!*/ Exponent(BigDecimal/*!*/ self) {
            return ClrInteger.Narrow(self.Exponent);
        }

        [RubyMethod("precision")]
        public static object/*!*/ Precision(BigDecimal/*!*/ self) {
            return ClrInteger.Narrow(self.Precision);
        }

        [RubyMethod("scale")]
        public static object/*!*/ Scale(BigDecimal/*!*/ self) {
            return ClrInteger.Narrow(self.ScaleDigits);
        }

        [RubyMethod("precision_scale")]
        public static RubyArray/*!*/ PrecisionScale(BigDecimal/*!*/ self) {
            return RubyOps.MakeArray2(Precision(self), Scale(self));
        }

        [RubyMethod("n_significant_digits")]
        public static int SignificantDigits(BigDecimal/*!*/ self) {
            return self.DigitCount;
        }

        [RubyMethod("split")]
        public static RubyArray/*!*/ Split(BigDecimal/*!*/ self) {
            int sign = self.IsNaN ? 0 : self.Sign;
            return RubyOps.MakeArray4(sign, MutableString.CreateAscii(self.GetDigitString()), 10, Exponent(self));
        }

        [RubyMethod("fix")]
        public static BigDecimal/*!*/ Fix(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.CheckResult(config, BigDecimal.IntegerPart(config, self));
        }

        [RubyMethod("frac")]
        public static BigDecimal/*!*/ Fraction(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.CheckResult(config, BigDecimal.FractionalPart(config, self));
        }

        /// <summary>self * 10**n, exactly.</summary>
        [RubyMethod("_decimal_shift")]
        public static BigDecimal/*!*/ DecimalShift(RubyContext/*!*/ context, BigDecimal/*!*/ self, [DefaultProtocol]int n) {
            return BigDecimal.DecimalShift(GetConfig(context), self, n);
        }

        #endregion

        #region Conversion

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToString(BigDecimal/*!*/ self) {
            return MutableString.CreateAscii(self.ToString());
        }

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToString(BigDecimal/*!*/ self, [DefaultProtocol]int group) {
            if (group <= 0) {
                throw RubyExceptions.CreateArgumentError("argument must be positive");
            }
            return MutableString.CreateAscii(self.ToString(group, "", false));
        }

        /// <summary>
        /// The format is MRI's: an optional leading " " or "+" to put before a positive number, then the
        /// group width (spaces in between are skipped), and an "F" or "f" for the positional format.
        /// Anything else ends it.
        /// </summary>
        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToString(BigDecimal/*!*/ self, [NotNull]MutableString/*!*/ format) {
            string text = format.ConvertToString();
            string plusSign = "";
            int i = 0;
            if (text.Length > 0 && (text[0] == ' ' || text[0] == '+')) {
                plusSign = text[0].ToString();
                i++;
            }
            bool floatFormat = false;
            long group = 0;
            for (; i < text.Length && text[i] != '\0'; i++) {
                char c = text[i];
                if (c == ' ' || (c >= '\t' && c <= '\r')) {
                    continue;
                }
                if (c < '0' || c > '9') {
                    floatFormat = c == 'F' || c == 'f';
                    break;
                }
                group = Math.Min(group * 10 + (c - '0'), Int32.MaxValue);
            }
            return MutableString.CreateAscii(self.ToString((int)group, plusSign, floatFormat));
        }

        /// <summary>
        /// Since the bigdecimal gem's 3.0, #inspect is just #to_s - the old
        /// #&lt;BigDecimal:0x...,'0.1E1',9(9)&gt; form is gone.
        /// </summary>
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(BigDecimal/*!*/ self) {
            return ToString(self);
        }

        // An Integer, a Float or a BigDecimal becomes a BigDecimal (a Rational too - bigdecimal.rb expands it).
        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(RubyContext/*!*/ context, BigDecimal/*!*/ self, object other) {
            return RubyOps.MakeArray2(ToBigDecimalOrError(context, other), self);
        }

        [RubyMethod("_dump")]
        public static MutableString/*!*/ Dump(BigDecimal/*!*/ self, [Optional]object limit) {
            // The limit is ignored as a BigDecimal holds no other objects.
            return MutableString.CreateMutable(RubyEncoding.Binary).
                Append(self.InternalDigits.ToString(CultureInfo.InvariantCulture)).
                Append(':').
                Append(self.ToString());
        }

        [RubyMethod("to_f")]
        public static double ToFloat(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            return BigDecimal.ToFloat(GetConfig(context), self);
        }

        [RubyMethod("to_i")]
        [RubyMethod("to_int")]
        public static object/*!*/ ToInteger(BigDecimal/*!*/ self) {
            return BigDecimal.ToInteger(self);
        }

        [RubyMethod("hash")]
        public static int Hash(BigDecimal/*!*/ self) {
            return self.GetHashCode();
        }

        #endregion

        #region Arithmetic

        /// <summary>
        /// The operand of an arithmetic or comparison operator as a BigDecimal: an Integer exactly, and a
        /// Float by its shortest digits - so BigDecimal("1.5") * 1.5 is 0.225e1, a BigDecimal, as in MRI
        /// rather than the Float that Float's own coercion would make it. Anything else (but a Rational,
        /// which bigdecimal.rb expands first) is null, for the operator to go through #coerce.
        /// </summary>
        private static BigDecimal ToBigDecimal(BigDecimal.Config/*!*/ config, object other) {
            switch (other) {
                case BigDecimal value: return value;
                case int value: return BigDecimal.Create(config, value);
                case long value: return BigDecimal.Create(config, value);
                case BigInteger value: return BigDecimal.Create(config, value);
                case double value: return BigDecimal.Create(config, value, 0);
                default: return null;
            }
        }

        /// <summary>The operand of a method that does not fall back on #coerce (#add, #coerce itself).</summary>
        private static BigDecimal/*!*/ ToBigDecimalOrError(RubyContext/*!*/ context, object other) {
            BigDecimal value = ToBigDecimal(GetConfig(context), other);
            if (value != null) {
                return value;
            }
            // MRI names an immediate value by its inspect and anything else by its class
            string name = other == null || other is bool || other is RubySymbol
                ? context.Inspect(other).ToString()
                : context.GetClassOf(other).Name;
            throw RubyExceptions.CreateTypeError(name + " can't be coerced into BigDecimal");
        }

        /// <summary>MRI's check_int_precision.</summary>
        private static int CheckPrecision(int n) {
            if (n < 0) {
                throw RubyExceptions.CreateArgumentError("negative precision");
            }
            return n;
        }

        [RubyMethod("+")]
        public static object Add(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null ? BigDecimal.Add(config, self, y, 1, 0) : Protocols.CoerceAndApply(coercionStorage, opStorage, "+", self, other);
        }

        [RubyMethod("add")]
        public static BigDecimal/*!*/ Add(RubyContext/*!*/ context, BigDecimal/*!*/ self, object other, [DefaultProtocol]int n) {
            CheckPrecision(n);
            return BigDecimal.Add(GetConfig(context), self, ToBigDecimalOrError(context, other), 1, n);
        }

        [RubyMethod("-")]
        public static object Subtract(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null ? BigDecimal.Add(config, self, y, -1, 0) : Protocols.CoerceAndApply(coercionStorage, opStorage, "-", self, other);
        }

        [RubyMethod("sub")]
        public static BigDecimal/*!*/ Subtract(RubyContext/*!*/ context, BigDecimal/*!*/ self, object other, [DefaultProtocol]int n) {
            CheckPrecision(n);
            return BigDecimal.Add(GetConfig(context), self, ToBigDecimalOrError(context, other), -1, n);
        }

        [RubyMethod("*")]
        public static object Multiply(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null
                ? BigDecimal.CheckResult(config, BigDecimal.Multiply(config, self, y, 0))
                : Protocols.CoerceAndApply(coercionStorage, opStorage, "*", self, other);
        }

        [RubyMethod("mult")]
        public static BigDecimal/*!*/ Multiply(RubyContext/*!*/ context, BigDecimal/*!*/ self, object other, [DefaultProtocol]int n) {
            CheckPrecision(n);
            BigDecimal.Config config = GetConfig(context);
            return BigDecimal.CheckResult(config, BigDecimal.Multiply(config, self, ToBigDecimalOrError(context, other), n));
        }

        [RubyMethod("/")]
        public static object Divide(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            return y != null
                ? BigDecimal.Divide(config, self, y, BigDecimal.DivisionDigits(config, self, y))
                : Protocols.CoerceAndApply(coercionStorage, opStorage, "/", self, other);
        }

        /// <summary>The precision-less #div floors the quotient into an Integer (since bigdecimal 4.0).</summary>
        [RubyMethod("div")]
        public static object Div(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            if (y == null) {
                return Protocols.CoerceAndApply(coercionStorage, opStorage, "div", self, other);
            }
            BigDecimal.DivMod(config, self, y, false, out BigDecimal div, out _);
            return BigDecimal.ToInteger(BigDecimal.CheckResult(config, div));
        }

        /// <summary>div(other, n) is the quotient to n significant digits, and #/'s quotient for 0; nil is #div(other).</summary>
        [RubyMethod("div")]
        public static object Div(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, ConversionStorage<int>/*!*/ fixnumCast,
            BigDecimal/*!*/ self, object other, object digits) {

            if (digits == null) {
                return Div(coercionStorage, opStorage, self, other);
            }
            int n = CheckPrecision(Protocols.CastToFixnum(fixnumCast, digits));
            BigDecimal.Config config = GetConfig(fixnumCast.Context);
            BigDecimal y = ToBigDecimalOrError(fixnumCast.Context, other);
            return BigDecimal.Divide(config, self, y, n == 0 ? BigDecimal.DivisionDigits(config, self, y) : n);
        }

        // #modulo is the very same method as #% (BigDecimal.instance_method(:modulo) == instance_method(:%)),
        // which bigdecimal.rb re-establishes after wrapping #%.
        [RubyMethod("%")]
        [RubyMethod("modulo")]
        public static object Modulo(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            if (y == null) {
                return Protocols.CoerceAndApply(coercionStorage, opStorage, "%", self, other);
            }
            BigDecimal.DivMod(config, self, y, false, out _, out BigDecimal mod);
            return BigDecimal.CheckResult(config, mod);
        }

        /// <summary>#remainder truncates the quotient where #% floors it, so its result has self's sign.</summary>
        [RubyMethod("remainder")]
        public static object Remainder(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            if (y == null) {
                return Protocols.CoerceAndApply(coercionStorage, opStorage, "remainder", self, other);
            }
            BigDecimal.DivMod(config, self, y, true, out _, out BigDecimal mod);
            return BigDecimal.CheckResult(config, mod);
        }

        /// <summary>[floored quotient as an Integer, modulus] - the quotient of a NaN or an infinity raises.</summary>
        [RubyMethod("divmod")]
        public static object DivMod(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ opStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal.Config config = GetConfig(coercionStorage.Context);
            BigDecimal y = ToBigDecimal(config, other);
            if (y == null) {
                return Protocols.CoerceAndApply(coercionStorage, opStorage, "divmod", self, other);
            }
            BigDecimal.DivMod(config, self, y, false, out BigDecimal div, out BigDecimal mod);
            // MRI checks the modulus first: -Infinity.divmod(1) in the NaN exception mode raises for the NaN
            mod = BigDecimal.CheckResult(config, mod);
            return RubyOps.MakeArray2(BigDecimal.ToInteger(BigDecimal.CheckResult(config, div)), mod);
        }

        /// <summary>#sqrt, once bigdecimal.rb has checked the precision.</summary>
        [RubyMethod("__sqrt", RubyMethodAttributes.PrivateInstance)]
        public static BigDecimal/*!*/ SquareRoot(RubyContext/*!*/ context, BigDecimal/*!*/ self, int digits) {
            return BigDecimal.SquareRoot(GetConfig(context), self, digits);
        }

        [RubyMethod("+@")]
        public static BigDecimal/*!*/ Identity(BigDecimal/*!*/ self) {
            return self;
        }

        [RubyMethod("-@")]
        public static BigDecimal/*!*/ Negate(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            return BigDecimal.CheckResult(GetConfig(context), BigDecimal.Negate(self));
        }

        [RubyMethod("abs")]
        public static BigDecimal/*!*/ Abs(RubyContext/*!*/ context, BigDecimal/*!*/ self) {
            return BigDecimal.CheckResult(GetConfig(context), BigDecimal.Abs(self));
        }

        #endregion

        #region Comparison

        // NaN makes a comparison unordered: <=> answers nil for it, and the relational operators false.

        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(coercionStorage.Context), other);
            return y != null ? BigDecimal.Compare(self, y) : Protocols.CoerceAndCompare(coercionStorage, comparisonStorage, self, other);
        }

        [RubyMethod(">")]
        public static object GreaterThan(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(coercionStorage.Context), other);
            return y != null ? BigDecimal.Compare(self, y) > 0 : Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, ">", self, other);
        }

        [RubyMethod(">=")]
        public static object GreaterThanOrEqual(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(coercionStorage.Context), other);
            return y != null ? BigDecimal.Compare(self, y) >= 0 : Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, ">=", self, other);
        }

        [RubyMethod("<")]
        public static object LessThan(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(coercionStorage.Context), other);
            return y != null ? BigDecimal.Compare(self, y) < 0 : Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, "<", self, other);
        }

        [RubyMethod("<=")]
        public static object LessThanOrEqual(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(coercionStorage.Context), other);
            return y != null ? BigDecimal.Compare(self, y) <= 0 : Protocols.CoerceAndRelate(coercionStorage, comparisonStorage, "<=", self, other);
        }

        // #eql? and #=== are #== itself, aliased in bigdecimal.rb. Any other operand is compared the way
        // MRI's rb_num_coerce_cmp does - other.coerce(self), then == - and is unequal when it cannot be
        // coerced. (Asking other == self instead never ended for an operand whose == asks back.)
        [RubyMethod("==")]
        public static bool Equal(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ equals, BigDecimal/*!*/ self, object other) {
            BigDecimal y = ToBigDecimal(GetConfig(equals.Context), other);
            if (y != null) {
                return BigDecimal.Compare(self, y) == 0;
            }
            return RubyOps.IsTrue(Protocols.TryCoerceAndApply(coercionStorage, equals, "==", self, other));
        }

        #endregion

        #region Rounding

        private static readonly Dictionary<string, BigDecimal.RoundingModes> _roundingModeNames = new Dictionary<string, BigDecimal.RoundingModes> {
            { "up", BigDecimal.RoundingModes.Up },
            { "down", BigDecimal.RoundingModes.Down },
            { "truncate", BigDecimal.RoundingModes.Down },
            { "half_up", BigDecimal.RoundingModes.HalfUp },
            { "default", BigDecimal.RoundingModes.HalfUp },
            { "half_down", BigDecimal.RoundingModes.HalfDown },
            { "half_even", BigDecimal.RoundingModes.HalfEven },
            { "banker", BigDecimal.RoundingModes.HalfEven },
            { "ceiling", BigDecimal.RoundingModes.Ceiling },
            { "ceil", BigDecimal.RoundingModes.Ceiling },
            { "floor", BigDecimal.RoundingModes.Floor },
        };

        /// <summary>
        /// MRI's check_rounding_mode: one of the ROUND_* constants or a Symbol naming it. Anything else must
        /// convert to an Integer.
        /// </summary>
        internal static BigDecimal.RoundingModes ToRoundingMode(ConversionStorage<int>/*!*/ fixnumCast, object mode) {
            if (mode is RubySymbol symbol) {
                if (_roundingModeNames.TryGetValue(symbol.ToString(), out BigDecimal.RoundingModes result)) {
                    return result;
                }
                throw RubyExceptions.CreateArgumentError("invalid rounding mode (" + symbol.ToString() + ")");
            }
            int value = Protocols.CastToFixnum(fixnumCast, mode);
            if (value < (int)BigDecimal.RoundingModes.Up || value > (int)BigDecimal.RoundingModes.HalfEven) {
                throw RubyExceptions.CreateArgumentError("invalid rounding mode (" + value.ToString(CultureInfo.InvariantCulture) + ")");
            }
            return (BigDecimal.RoundingModes)value;
        }

        /// <summary>
        /// MRI's check_rounding_mode_option: half: :up, :down or :even (a String too, in any case), and the
        /// current mode when it is nil or not given.
        /// </summary>
        private static BigDecimal.RoundingModes ToHalfRoundingMode(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ options) {
            if (!options.TryGetValue(context.CreateAsciiSymbol("half"), out object half) || half == null) {
                return GetConfig(context).RoundingMode;
            }
            string name = (half as RubySymbol)?.ToString() ?? (half as MutableString)?.ToString();
            switch (name?.ToLowerInvariant()) {
                case "up": return BigDecimal.RoundingModes.HalfUp;
                case "down": return BigDecimal.RoundingModes.HalfDown;
                case "even": return BigDecimal.RoundingModes.HalfEven;
            }
            throw RubyExceptions.CreateArgumentError("invalid rounding mode (" + (name ?? context.Inspect(half).ToString()) + ")");
        }

        /// <summary>NUM2INT, which takes no nil.</summary>
        private static int ToDigits(ConversionStorage<int>/*!*/ fixnumCast, object n) {
            if (n == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion from nil to integer");
            }
            return Protocols.CastToFixnum(fixnumCast, n);
        }

        /// <summary>
        /// Rounds to <paramref name="digits"/> decimal places, and answers an Integer of the result when
        /// <paramref name="toInteger"/> - which a NaN or an infinity cannot be.
        /// </summary>
        private static object Round(RubyContext/*!*/ context, BigDecimal/*!*/ self, int digits, BigDecimal.RoundingModes mode, bool toInteger) {
            BigDecimal.Config config = GetConfig(context);
            BigDecimal result = BigDecimal.CheckResult(config, BigDecimal.RoundDecimalPlaces(config, self, digits, mode));
            return toInteger ? BigDecimal.ToInteger(result) : result;
        }

        /// <summary>
        /// round answers an Integer, and so does round(n) for n &lt; 1; round(n, mode) and round(half: ...)
        /// answer a BigDecimal, as do round(n) for n &gt;= 1.
        /// </summary>
        [RubyMethod("round")]
        public static object Round(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, BigDecimal/*!*/ self,
            [Optional]object n, [Optional]object mode) {

            BigDecimal.RoundingModes roundingMode = GetConfig(context).RoundingMode;
            if (n is Missing) {
                return Round(context, self, 0, roundingMode, true);
            }
            if (mode is Missing) {
                if (n is IDictionary<object, object> nOptions) {
                    return Round(context, self, 0, ToHalfRoundingMode(context, nOptions), false);
                }
                int places = ToDigits(fixnumCast, n);
                return Round(context, self, places, roundingMode, places < 1);
            }
            int digits = ToDigits(fixnumCast, n);
            roundingMode = mode is IDictionary<object, object> options
                ? ToHalfRoundingMode(context, options)
                : ToRoundingMode(fixnumCast, mode);
            return Round(context, self, digits, roundingMode, false);
        }

        [RubyMethod("truncate")]
        public static object Truncate(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, BigDecimal/*!*/ self, [Optional]object n) {
            return Round(context, self, n is Missing ? 0 : ToDigits(fixnumCast, n), BigDecimal.RoundingModes.Down, n is Missing);
        }

        [RubyMethod("floor")]
        public static object Floor(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, BigDecimal/*!*/ self, [Optional]object n) {
            return Round(context, self, n is Missing ? 0 : ToDigits(fixnumCast, n), BigDecimal.RoundingModes.Floor, n is Missing);
        }

        [RubyMethod("ceil")]
        public static object Ceil(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, BigDecimal/*!*/ self, [Optional]object n) {
            return Round(context, self, n is Missing ? 0 : ToDigits(fixnumCast, n), BigDecimal.RoundingModes.Ceiling, n is Missing);
        }

        #endregion

        #region Tests

        [RubyMethod("finite?")]
        public static bool IsFinite(BigDecimal/*!*/ self) {
            return self.IsFinite;
        }

        [RubyMethod("infinite?")]
        public static object IsInfinite(BigDecimal/*!*/ self) {
            return self.IsInfinite ? (object)self.Sign : null;
        }

        [RubyMethod("nan?")]
        public static bool IsNaN(BigDecimal/*!*/ self) {
            return self.IsNaN;
        }

        [RubyMethod("nonzero?")]
        public static BigDecimal IsNonZero(BigDecimal/*!*/ self) {
            return self.IsZero ? null : self;
        }

        [RubyMethod("zero?")]
        public static bool IsZero(BigDecimal/*!*/ self) {
            return self.IsZero;
        }

        #endregion
    }
}

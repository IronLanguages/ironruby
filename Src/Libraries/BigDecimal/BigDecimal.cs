/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * dlr@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IronRuby.Builtins;
using IronRuby.Runtime;
using System.Numerics;
using Microsoft.Scripting.Utils;

namespace IronRuby.StandardLibrary.BigDecimal {
    public class BigDecimal : IComparable<BigDecimal>, IEquatable<BigDecimal>, IRubyObjectState {

        static BigDecimal() {
            CreateSC();
            CreateSR();
        }

        /// <summary>
        /// This class holds the configuration information for the BigDecimal calculations
        /// This information needs to be shared amongst all the instances of BigDecimal
        /// but cannot be held in static variable storage since we need to scope more finely
        /// than per assembly.
        /// </summary>
        public class Config {
            public Config() {
                Limit = 0;
                RoundingMode = RoundingModes.HalfUp;
                OverflowMode = OverflowExceptionModes.None;
            }
            public int Limit { get; set; }
            public RoundingModes RoundingMode { get; set; }
            public OverflowExceptionModes OverflowMode { get; set; }
        }

        #region Instance Fields
        private readonly NumberTypes _type;
        private readonly int _sign;
        private readonly Fraction _fraction;
        private readonly int _exponent;
        private readonly int _maxPrecision;
        #endregion

        #region Private Constants

        private const string NaNString = "NaN";
        private const string InfinityString = "Infinity";
        private const string NegativeInfinityString = "-Infinity";
        private const string ZeroString = "0.0";
        private const string NegativeZeroString = "-0.0";

        #endregion

        #region Enumerations

        public enum RoundingModes {
            /// <summary>
            /// No rounding
            /// </summary>
            None = 0,
            /// <summary>
            /// Round away from zero
            /// </summary>
            Up = 1,
            /// <summary>
            /// Round toward zero
            /// </summary>
            Down = 2,
            /// <summary>
            /// Round towards "nearest neighbour" unless both neighbours are equidistant, in which case round up
            /// </summary>
            HalfUp = 3,
            /// <summary>
            /// Round towards "nearest neighbour" unless both neighbours are equidistant, in which case round down
            /// </summary>
            HalfDown = 4,
            /// <summary>
            /// Round toward positive infinity
            /// </summary>
            Ceiling = 5,
            /// <summary>
            /// Round toward negative infinity
            /// </summary>
            Floor = 6,
            /// <summary>
            /// Round towards "nearest neighbour" unless both neighbours are equidistant, in which case round to the even neighbour
            /// </summary>
            HalfEven = 7
        }

        /// <summary>
        /// Flags of when exceptions should be thrown on special values
        /// </summary>
        /// <remarks>
        /// Currently Infinity, Overflow and ZeroDivide are the same
        /// </remarks>
        [Flags]
        public enum OverflowExceptionModes {
            None = 0,
            NaN = 2,
            Infinity = 1,
            Underflow = 4,
            Overflow = 1,
            ZeroDivide = 16,
            All = 255
        }

        public enum NumberTypes {
            NaN = 0,
            Finite = 1,
            Infinite = 2
        }

        private enum BasicOperations {
            Add = 0,
            Subtract = 1,
            Multiply = 2,
            Divide = 3
        }

        #endregion

        #region Special BigDecimal Values

        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly BigDecimal One = new BigDecimal(1, Fraction.One, 1);
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly BigDecimal Half = new BigDecimal(1, Fraction.Five, 0);
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly BigDecimal NaN = new BigDecimal(NumberTypes.NaN, 0);
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly BigDecimal PositiveInfinity = new BigDecimal(NumberTypes.Infinite, 1);
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly BigDecimal NegativeInfinity = new BigDecimal(NumberTypes.Infinite, -1);
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly BigDecimal PositiveZero = new BigDecimal(1, Fraction.Zero, 1);
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly BigDecimal NegativeZero = new BigDecimal(-1, Fraction.Zero, 1);
        public const uint BASE = Fraction.BASE;
        public const int BASE_FIG = Fraction.BASE_FIG;

        #endregion

        #region Private Constructors

        public BigDecimal() : this(0, NaN) {
        }

        private BigDecimal(int sign, Fraction/*!*/ fraction, int exponent)
            : this(sign, fraction, exponent, fraction.Precision) {
        }

        private BigDecimal(int sign, Fraction/*!*/ fraction, int exponent, int maxPrecision) {
            Debug.Assert(sign == 1 || sign == -1);
            _type = NumberTypes.Finite;
            _sign = sign;
            _fraction = fraction;
            _exponent = exponent;
            // If maxPrecision is 0 then default to the precision of the fraction
            _maxPrecision = maxPrecision == 0 ? fraction.Precision : maxPrecision;
        }

        private BigDecimal(int sign, BigDecimal/*!*/ copyFrom) {
            _type = copyFrom._type;
            _sign = sign;
            _fraction = copyFrom._fraction;
            _exponent = copyFrom._exponent;
            _maxPrecision = copyFrom._maxPrecision;
        }

        private BigDecimal(int sign, BigDecimal/*!*/ copyFrom, int maxPrecision) {
            _type = copyFrom._type;
            _sign = sign;
            _fraction = new Fraction(copyFrom._fraction, maxPrecision);
            _exponent = copyFrom._exponent;
            _maxPrecision = maxPrecision;
        }

        private BigDecimal(NumberTypes type, int sign)
            : this(type, sign, 1) {
        }

        private BigDecimal(NumberTypes type, int sign, int maxPrecision) {
            Debug.Assert(type != NumberTypes.Finite);
            _type = type;
            _sign = sign;
            _fraction = Fraction.Zero;
            _exponent = 0;
            _maxPrecision = maxPrecision;
        }

        #endregion

        #region Public Properties
        public NumberTypes NumberType { get { return _type; } }
        public int Sign { get { return _sign; } }
        public int Exponent { get { return _exponent; } }
        public int Precision { get { return _fraction.Precision; } }
        public int MaxPrecision { get { return _maxPrecision; } }
        public int Digits { get { return _fraction.DigitCount; } }
        #endregion

        #region IRubyObjectState

        // A BigDecimal is immutable and, as in MRI, always frozen.
        public bool IsFrozen => true;
        public void Freeze() { }
        public bool IsTainted { get => false; set { } }
        public bool IsUntrusted { get => false; set { } }

        #endregion

        #region Create Methods

        public static BigDecimal Create(Config/*!*/config, object value) {
            Debug.Assert(!(value is double) && !(value is float)); // Need to use CultureInfo.InvariantCulture for double
            return Create(config, value.ToString(), 0);
        }

        public static BigDecimal Create(Config/*!*/config, double value) {
            return Create(config, value.ToString(CultureInfo.InvariantCulture), 0);
        }

        public static BigDecimal Create(Config/*!*/config, string value) {
            return Create(config, value, 0);
        }

        public static BigDecimal Create(Config/*!*/config, string value, int maxPrecision) {
            value = value.Trim();
            BigDecimal result = CheckSpecialCases(value);

            if (result == null) {
                Match m = Regex.Match(value, @"^(?<sign>[-+]?)(?<integer>[\d_]*)\.?(?<fraction>[\d_]*)([eEdD](?<exponent>[-+]?[\d_]+))?", RegexOptions.ExplicitCapture);

                int sign = m.Groups["sign"].Value == "-" ? -1 : 1;

                // Get the integer part of the mantissa (stripping off leading zeros and any '_' chars)
                string integerStr = (m.Groups["integer"].Value ?? "0").TrimStart('0').Replace("_", "");
                // Get the fraction part of the mantissa (stripping off following zeros and any '_' chars)
                string decimalStr = (m.Groups["fraction"].Value ?? "").TrimEnd('0').Replace("_", "");
                // Build the whole of the mantissa string
                string fractionStr = (integerStr + decimalStr);
                string strippedFractionStr = fractionStr.TrimStart('0');

                // Finally strip off any excess zeros left over
                string trimmedFractionStr = strippedFractionStr.Trim('0');

                Fraction fraction = Fraction.Create(trimmedFractionStr);

                string exponentStr = m.Groups["exponent"].Value.Replace("_", "");
                int exponent = 0;
                if (!fraction.IsZero) {
                    if (!string.IsNullOrEmpty(exponentStr)) {
                        try {
                            exponent = int.Parse(exponentStr, CultureInfo.InvariantCulture);
                        } catch (OverflowException) {
                            exponent = exponentStr.StartsWith("-", StringComparison.Ordinal) ? -1 : 1;
                            return ExponentOverflow(config, sign, exponent);
                        }
                    }
                    // Normalize exponent where mantissa > 1.0
                    exponent += integerStr.Length;
                    // Normalize exponent where mantissa < 1.0
                    exponent -= (fractionStr.Length - strippedFractionStr.Length);
                }
                result = new BigDecimal(sign, fraction, exponent, maxPrecision);
            }
            return CheckOverflowExceptions(config, result);
        }

        // A run of digits, which may be broken up by single underscores but may not start or
        // end with one: 1_2_3 is fine, 1__2 and _123 are not.
        private const string DigitRun = @"\d+(_\d+)*";

        private static readonly Regex/*!*/ _strictNumber = new Regex(
            @"^[-+]?(" + DigitRun + @"(\.(" + DigitRun + @")?)?|\.(" + DigitRun + @"))([eEdD][-+]?" + DigitRun + @")?$",
            RegexOptions.ExplicitCapture);

        // MRI tolerates one trailing underscore, but only on a bare integer and only at the
        // very end of the string: "123_" and "1_2_" parse, while "123_.45", "1.23_" and
        // "1E1_" are all rejected. Measured, not deduced - the general rule would have
        // accepted all four.
        private static readonly Regex/*!*/ _strictTrailingUnderscore = new Regex(
            @"^[-+]?" + DigitRun + "_$", RegexOptions.ExplicitCapture);

        /// <summary>
        /// Whether Kernel#BigDecimal would accept this string. The parser itself is
        /// deliberately forgiving (String#to_d wants a value out of "45.67 degrees"), so the
        /// strictness lives here rather than in Create.
        /// </summary>
        public static bool IsValidNumericString(string value) {
            if (value == null) {
                return false;
            }
            value = value.Trim();
            if (value == "NaN" || value == "Infinity" || value == "+Infinity" || value == "-Infinity") {
                return true;
            }
            return _strictNumber.IsMatch(value) || _strictTrailingUnderscore.IsMatch(value);
        }

        private static BigDecimal CheckSpecialCases(string value) {
            BigDecimal result = null;
            if (value == null) {
                result = PositiveZero;
            } else {
                value = value.Trim();
                if (value == "NaN") {
                    result = NaN;
                } else if (value.Contains("Infinity")) {
                    if (value == "+Infinity" || value == "Infinity") {
                        result = PositiveInfinity;
                    } else if (value == "-Infinity") {
                        result = NegativeInfinity;
                    } else {
                        result = PositiveZero;
                    }
                }
            }
            return result;
        }

        #endregion

        #region Special Value Tests

        public static bool IsNaN(BigDecimal x) {
            return x._type == NumberTypes.NaN;
        }

        public static bool IsInfinite(BigDecimal x) {
            return (x._type == NumberTypes.Infinite);
        }

        public static bool IsFinite(BigDecimal x) {
            return x._type == NumberTypes.Finite;
        }

        public static bool IsZero(BigDecimal/*!*/ x) {
            return IsFinite(x) && x._fraction.IsZero;
        }

        public static bool IsNonZeroFinite(BigDecimal x) {
            return IsFinite(x) && !x._fraction.IsZero;
        }

        public static bool IsPositive(BigDecimal x) {
            return x._sign == 1;
        }

        public static bool IsNegative(BigDecimal x) {
            return x._sign == -1;
        }

        public static bool IsPositiveInfinite(BigDecimal x) {
            return IsInfinite(x) && IsPositive(x);
        }

        public static bool IsNegativeInfinite(BigDecimal x) {
            return IsInfinite(x) && IsNegative(x);
        }

        public static bool IsPositiveZero(BigDecimal x) {
            return IsZero(x) && IsPositive(x);
        }

        public static bool IsNegativeZero(BigDecimal x) {
            return IsZero(x) && IsNegative(x);
        }

        public static bool IsOne(BigDecimal x) {
            return x._fraction.IsOne && x._exponent == 1;
        }

        public static bool IsPositiveOne(BigDecimal x) {
            return IsOne(x) && IsPositive(x);
        }

        #endregion

        #region Public Static Operations

        public static double ToFloat(Config config, BigDecimal/*!*/ x) {
            try {
                if (BigDecimal.IsNegativeZero(x)) {
                    return -0.0;
                }
                return Double.Parse(x.ToString(0, "", true), CultureInfo.InvariantCulture);
            } catch (OverflowException) {
                return Double.PositiveInfinity;
            }
        }

        public static object ToInteger(Config/*!*/ config, BigDecimal/*!*/ x) {
            if (IsFinite(x)) {
                BigDecimal i = IntegerPart(config, x);
                BigInteger d = new BigInteger(0);
                string digits = i._fraction.ToString();
                foreach (char c in digits) {
                    d *= 10;
                    d += (c - '0');
                }
                if (IsNegative(x)) {
                    d = BigInteger.Negate(d);
                }
                return ClrInteger.Multiply(d, new BigInteger(10).Power(i.Exponent - digits.Length));
            } else {
                return null;
            }
        }

        public static BigDecimal Abs(Config config, BigDecimal x) {
            if (IsNegative(x)) {
                return Negate(config, x);
            } else {
                return x;
            }
        }

        public static BigDecimal Negate(Config config, BigDecimal x) {
            if (IsFinite(x)) {
                return new BigDecimal(-x._sign, x._fraction, x._exponent);
            } else {
                return new BigDecimal(x.NumberType, -x.Sign);
            }
        }

        public static BigDecimal/*!*/ Add(Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y) {
            return Add(config, x, y, 0);
        }

        public static BigDecimal/*!*/ Add(Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y, int limit) {
            return InternalAdd(config, x, y, limit);
        }

        public static BigDecimal/*!*/ Subtract(Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y) {
            return Subtract(config, x, y, 0);
        }

        public static BigDecimal/*!*/ Subtract(Config config, BigDecimal x, BigDecimal y, int limit) {
            return InternalAdd(config, x, BigDecimal.Negate(config, y), limit);
        }

        public static BigDecimal Multiply(Config config, BigDecimal x, BigDecimal y) {
            return Multiply(config, x, y, 0);
        }

        public static BigDecimal Multiply(Config config, BigDecimal x, BigDecimal y, int limit) {
            BigDecimal result = CheckSpecialResult(config, x, y, BasicOperations.Multiply);
            if (result != null) {
                return result;
            }

            // Default to the global limit
            if (limit == 0) {
                limit = config.Limit;
            }

            // Calculate the number of digits if not specified
            if (limit == 0) {
                limit = x.Digits + y.Digits;
            }

            int sign = y._sign * x._sign;
            // #mult's limit counts significant digits, but LimitPrecision's counts decimal
            // places, so the multiply-by-one short cut has to convert between the two -
            // otherwise BigDecimal("1.234").mult(1, 2) keeps three digits instead of two.
            if (IsOne(x)) {
                BigDecimal scaled = new BigDecimal(sign, y._fraction, y._exponent);
                return LimitPrecision(config, scaled, limit - scaled.Exponent, config.RoundingMode);
            }
            if (IsOne(y)) {
                BigDecimal scaled = new BigDecimal(sign, x._fraction, x._exponent);
                return LimitPrecision(config, scaled, limit - scaled.Exponent, config.RoundingMode);
            }

            int exponent;
            Fraction fraction;
            try {
                checked {
                    int diffDigits1, diffDigits2;
                    fraction = Fraction.Multiply(x._fraction, y._fraction, out diffDigits1);
                    fraction = Fraction.LimitPrecision(sign, fraction, limit, config.RoundingMode, out diffDigits2);
                    exponent = x._exponent + y._exponent + diffDigits1 + diffDigits2;
                }
            } catch (OverflowException) {
                return ExponentOverflow(config, sign, x._exponent);
            }

            result = new BigDecimal(sign, fraction, exponent, limit);
            return result;
        }

        public static BigDecimal Divide(Config config, BigDecimal x, BigDecimal y, int limit, out BigDecimal remainder) {
            BigDecimal result = CheckSpecialResult(config, x, y, BasicOperations.Divide);
            if (result != null) {
                remainder = BigDecimal.PositiveZero;
                return result;
            }

            // Default to global limit if not specified
            if (limit == 0) {
                limit = config.Limit;
            }

            // Sign
            int sign = x._sign * y._sign;

            if (IsOne(y)) {
                remainder = BigDecimal.PositiveZero;
                return new BigDecimal(sign, x._fraction, x._exponent);
            }

            // Fraction
            int xOffset, rOffset, limitOffset;
            int minPrecision = (int)Math.Ceiling((double)limit / BASE_FIG) + 1;
            Fraction rFraction;
            Fraction fraction = Fraction.Divide(x._fraction, y._fraction, minPrecision, out rFraction, out xOffset, out rOffset);
            if (limit == 0) {
                limitOffset = 0;
            } else {
                fraction = Fraction.LimitPrecision(sign, fraction, limit, config.RoundingMode, out limitOffset);
            }

            // Exponent
            int exponent, rExponent;
            try {
                checked {
                    exponent = x._exponent - y._exponent + xOffset + limitOffset;
                    rExponent = x._exponent - y._exponent + rOffset;
                }
            } catch (OverflowException) {
                remainder = BigDecimal.PositiveZero;
                return ExponentOverflow(config, sign, x._exponent);
            }

            remainder = new BigDecimal(sign, rFraction, rExponent, rFraction.MaxPrecision);
            result = new BigDecimal(sign, fraction, exponent, fraction.MaxPrecision);
            return result;
        }

        /// <summary>A finite x as unscaled * 10**scale, the unscaled value carrying the sign.</summary>
        // The fraction's words hold its digits left-aligned (0.w0 w1 ... * 10**exponent, w0's first digit
        // non-zero), so the unscaled value is the words read as one base 10**9 number, 9 digits a word.
        private static void ToUnscaled(BigDecimal/*!*/ x, out System.Numerics.BigInteger unscaled, out long scale) {
            int precision = x._fraction.Precision;
            if (precision <= 2) {
                ulong value = x._fraction.GetWord(0);
                if (precision == 2) {
                    value = value * Fraction.BASE + x._fraction.GetWord(1);
                }
                unscaled = value;
            } else {
                System.Numerics.BigInteger value = System.Numerics.BigInteger.Zero;
                for (int i = 0; i < precision; i++) {
                    value = value * Fraction.BASE + x._fraction.GetWord(i);
                }
                unscaled = value;
            }
            if (x._sign < 0) {
                unscaled = -unscaled;
            }
            scale = (long)x._exponent - (long)precision * Fraction.BASE_FIG;
        }

        /// <summary>The digit count of a positive value made by ToUnscaled: 9 for each word.</summary>
        private static int UnscaledDigits(BigDecimal/*!*/ x) {
            return x._fraction.Precision * Fraction.BASE_FIG;
        }

        private static BigDecimal/*!*/ FromUnscaled(Config/*!*/ config, System.Numerics.BigInteger unscaled, long scale) {
            if (unscaled.IsZero) {
                return PositiveZero;
            }
            int sign = unscaled.Sign;
            string digits = System.Numerics.BigInteger.Abs(unscaled).ToString(CultureInfo.InvariantCulture);
            int length = digits.Length;
            while (length > 1 && digits[length - 1] == '0') {
                length--;
            }
            long exponent = scale + digits.Length;
            if (exponent > Int32.MaxValue || exponent < Int32.MinValue) {
                return Create(config, (sign < 0 ? "-" : "") + digits + "e" + scale.ToString(CultureInfo.InvariantCulture));
            }
            int wordCount = (length + Fraction.BASE_FIG - 1) / Fraction.BASE_FIG;
            var words = new uint[wordCount];
            for (int w = 0; w < wordCount; w++) {
                uint word = 0;
                for (int d = 0; d < Fraction.BASE_FIG; d++) {
                    int i = w * Fraction.BASE_FIG + d;
                    word = word * 10 + (i < length ? (uint)(digits[i] - '0') : 0);
                }
                words[w] = word;
            }
            return new BigDecimal(sign, new Fraction(words), (int)exponent);
        }

        /// <summary>
        /// x / y to <paramref name="digits"/> significant digits in <paramref name="mode"/>, as bigdecimal's
        /// BigDecimal_div2 makes it: the quotient is worked out exactly to one digit more, the remainder
        /// stands for everything beyond it (so that a quotient that is not exact never looks like a tie
        /// or a stop), and that is rounded. Fraction.Divide was asked for 2 words, so x / y had 18 digits
        /// where MRI's has max(x.precision, y.precision) + 16, and at least 32.
        /// </summary>
        public static BigDecimal/*!*/ DivideRounded(Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y, int digits, RoundingModes mode) {
            BigDecimal special = CheckSpecialResult(config, x, y, BasicOperations.Divide);
            if (special != null) {
                return special;
            }
            int sign = x._sign * y._sign;
            if (IsZero(x)) {
                return sign < 0 ? NegativeZero : PositiveZero;
            }

            System.Numerics.BigInteger a, b;
            long ea, eb;
            ToUnscaled(x, out a, out ea);
            ToUnscaled(y, out b, out eb);
            a = System.Numerics.BigInteger.Abs(a);
            b = System.Numerics.BigInteger.Abs(b);

            int aDigits = UnscaledDigits(x);
            int bDigits = UnscaledDigits(y);
            long shift = Math.Max(0, (long)digits + 2 + bDigits - aDigits);
            System.Numerics.BigInteger remainder;
            System.Numerics.BigInteger quotient = System.Numerics.BigInteger.DivRem(a * System.Numerics.BigInteger.Pow(10, (int)shift), b, out remainder);
            long scale = ea - eb - shift;
            if (!remainder.IsZero) {
                quotient = quotient * 10 + 1;
                scale--;
            }

            BigDecimal exact = FromUnscaled(config, quotient, scale);
            if (!IsFinite(exact)) {
                return sign < 0 ? NegativeInfinity : PositiveInfinity;
            }
            int offset;
            Fraction fraction = Fraction.LimitPrecision(sign, exact._fraction, digits, mode, out offset);
            return new BigDecimal(sign, fraction, exact._exponent + offset);
        }

        public static void DivMod(Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y, out BigDecimal/*!*/ div, out BigDecimal/*!*/ mod) {
            div = BigDecimal.NaN;
            mod = BigDecimal.NaN;

            // Exactly, at the exponent of the finer of the two: the quotient of an 18-digit division,
            // floored, was wrong whenever the true quotient needed more digits - BigDecimal("1e100") % 3
            // was 0.1e83.
            if (BigDecimal.IsFinite(x) && BigDecimal.IsFinite(y) && !IsZero(y)) {
                if (IsZero(x)) {
                    div = PositiveZero;
                    mod = x;
                    return;
                }
                System.Numerics.BigInteger a, b;
                long ea, eb;
                ToUnscaled(x, out a, out ea);
                ToUnscaled(y, out b, out eb);
                if (Math.Abs(ea - eb) <= 100000) {
                    long e = Math.Min(ea, eb);
                    a *= System.Numerics.BigInteger.Pow(10, (int)(ea - e));
                    b *= System.Numerics.BigInteger.Pow(10, (int)(eb - e));
                    System.Numerics.BigInteger r;
                    System.Numerics.BigInteger q = System.Numerics.BigInteger.DivRem(a, b, out r);
                    if (!r.IsZero && (r.Sign < 0) != (b.Sign < 0)) {
                        q -= 1;
                        r += b;
                    }
                    div = FromUnscaled(config, q, 0);
                    mod = FromUnscaled(config, r, e);
                    return;
                }
            }
            if (BigDecimal.IsFinite(x) && BigDecimal.IsFinite(y)) {
                BigDecimal remainder;
                BigDecimal quotient = Divide(config, x, y, 0, out remainder);
                if (BigDecimal.IsFinite(quotient)) {
                    // Calculate the div: floor(quotient)
                    div = LimitPrecision(config, quotient, 0, BigDecimal.RoundingModes.Floor);
                    // Calculate the mod: x - (div * y)
                    mod = Subtract(config, x, Multiply(config, div, y));
                }
            }
        }

        public static BigDecimal/*!*/ SquareRoot(Config/*!*/ config, BigDecimal/*!*/ x, int limit) {
            if (limit < 0) {
                throw new ArgumentException("Negative precision for sqrt");
            }
            // Special cases
            if (IsZero(x) || IsPositiveOne(x)) {
                return x;
            }
            if (IsNaN(x)) {
                throw new FloatDomainError("sqrt of 'NaN'(Not a Number)");
            }
            if (x.Sign < 0) {
                throw new FloatDomainError("sqrt of negative value");
            }
            if (!IsFinite(x)) {
                return CheckOverflowExceptions(config, x);
            }

            // bigdecimal 4's sqrt (Ruby, Newton's method) works to the precision asked for plus some and
            // rounds that to it: sqrt(n) has n digits, rounded in the current mode, and sqrt(0) has
            // n_significant_digits + 16 (BigDecimal.double_fig) - no more than BigDecimal.limit. The
            // computation this replaces kept a fixed number of words of whatever its iteration reached,
            // so BigDecimal("0.1").sqrt(10) was 0.31622776600000000000000000093469343e0.
            if (limit == 0) {
                limit = x.Digits + 16;
                if (config.Limit > 0 && config.Limit < limit) {
                    limit = config.Limit;
                }
            }
            return SquareRootRounded(config, x, limit);
        }

        private static System.Numerics.BigInteger IntegerSquareRoot(System.Numerics.BigInteger n) {
            if (n.IsZero) {
                return n;
            }
            // Newton's method from a power of two above the root
            int bits = (int)((n.GetBitLength() + 1) / 2);
            System.Numerics.BigInteger x = System.Numerics.BigInteger.One << bits;
            while (true) {
                System.Numerics.BigInteger y = (x + n / x) >> 1;
                if (y >= x) {
                    return x;
                }
                x = y;
            }
        }

        /// <summary>The square root of a finite positive x to <paramref name="digits"/> significant digits.</summary>
        private static BigDecimal/*!*/ SquareRootRounded(Config/*!*/ config, BigDecimal/*!*/ x, int digits) {
            System.Numerics.BigInteger a;
            long scale;
            ToUnscaled(x, out a, out scale);
            if ((scale & 1) != 0) {
                a *= 10;
                scale--;
            }
            int aDigits = a.ToString(CultureInfo.InvariantCulture).Length;
            long k = Math.Max(0, (long)digits + 2 - aDigits / 2 + 1);
            System.Numerics.BigInteger n = a * System.Numerics.BigInteger.Pow(10, (int)(2 * k));
            System.Numerics.BigInteger root = IntegerSquareRoot(n);
            long rootScale = scale / 2 - k;
            if (root * root != n) {
                root = root * 10 + 1;
                rootScale--;
            }
            BigDecimal exact = FromUnscaled(config, root, rootScale);
            int offset;
            Fraction fraction = Fraction.LimitPrecision(1, exact._fraction, digits, config.RoundingMode, out offset);
            return new BigDecimal(1, fraction, exact._exponent + offset);
        }

        public static BigDecimal/*!*/ FractionalPart(Config/*!*/ config, BigDecimal/*!*/ x) {
            if (IsFinite(x)) {
                if (x.Exponent > 0) {
                    if (x.Exponent < x.Digits) {
                        return new BigDecimal(x.Sign, Fraction.Create(x._fraction.ToString().Substring(x.Exponent)), 0);
                    } else {
                        return x.Sign > 0 ? BigDecimal.PositiveZero : BigDecimal.NegativeZero;
                    }
                } else {
                    return x;
                }
            }
            return x;
        }

        public static BigDecimal/*!*/ IntegerPart(Config/*!*/ config, BigDecimal/*!*/ x) {
            if (IsFinite(x)) {
                if (x.Exponent > 0) {
                    if (x.Exponent < x._fraction.DigitCount) {
                        return new BigDecimal(x.Sign, Fraction.Create(x._fraction.ToString().Substring(0, x.Exponent)), x.Exponent);
                    } else {
                        return x;
                    }
                } else {
                    return x.Sign > 0 ? BigDecimal.PositiveZero : BigDecimal.NegativeZero;
                }
            } else {
                return x;
            }
        }

        public static BigDecimal/*!*/ LimitPrecision(Config/*!*/ config, BigDecimal/*!*/ x, int limit, RoundingModes mode) {
            try {
                checked {
                    if (IsFinite(x)) {
                        int diffDigits;
                        Fraction fraction = Fraction.LimitPrecision(x._sign, x._fraction, limit + x.Exponent, mode, out diffDigits);
                        return new BigDecimal(x._sign, fraction, x._exponent + diffDigits);
                    } else {
                        return x;
                    }
                }
            } catch (OverflowException) {
                return ExponentOverflow(config, x._sign, x._exponent);
            }
        }

        #endregion

        #region Private Helpers

        private static BigDecimal InternalAdd(Config config, BigDecimal x, BigDecimal y, int limit) {
            if (limit < 0) {
                throw new ArgumentException("limit must be positive");
            }
            if (limit == 0) {
                limit = config.Limit;
            }

            // This method is used for both adding and subtracting, but the special cases are the same.
            BigDecimal result = CheckSpecialResult(config, x, y, BasicOperations.Add);
            if (result != null) {
                return result;
            }

            if (IsZero(x)) {
                if (limit == 0) {
                    return y;
                } else {
                    return LimitPrecision(config, y, limit, config.RoundingMode);
                }
            }
            if (IsZero(y)) {
                if (limit == 0) {
                    return x;
                } else {
                    return LimitPrecision(config, x, limit, config.RoundingMode);
                }
            }

            int exponent = Math.Max(x._exponent, y._exponent);
            int sign = x._sign;
            Fraction fraction;
            int exponentDiff;

            try {
                checked {
                    exponentDiff = checked(x._exponent - y._exponent);
                    int diffDigits;
                    if (x._sign == y._sign) {
                        fraction = Fraction.Add(x._fraction, y._fraction, exponentDiff, out diffDigits);
                        exponent = exponent + diffDigits;
                        sign = x._sign;
                    } else {
                        // Signs are different then we have to do a subtraction instead
                        fraction = Fraction.Subtract(x._fraction, y._fraction, exponentDiff, out diffDigits, out sign);
                        exponent = exponent + diffDigits;
                        if (sign == 0) {
                            return PositiveZero;
                        }
                        sign = x._sign * sign;
                    }
                    if (limit == 0) {
                        limit = fraction.DigitCount;
                    }
                    fraction = Fraction.LimitPrecision(sign, fraction, limit, config.RoundingMode, out diffDigits);
                    exponent = exponent + diffDigits;
                }
            } catch (OverflowException) {
                return ExponentOverflow(config, sign, x._exponent);
            }

            result = new BigDecimal(sign, fraction, exponent);
            return result;
        }

        private static BigDecimal ExponentOverflow(Config config, int sign, int exponent) {
            if (exponent > 0) {
                if ((config.OverflowMode & OverflowExceptionModes.Overflow) == OverflowExceptionModes.Overflow) {
                    throw new FloatDomainError("Exponent overflow");
                }
                // Our exponent is an Int32 where MRI's is a 64-bit value, so we run out of
                // room sooner - but the answer at the end of the road is the same one MRI
                // gives when its own exponent overflows: Infinity, not zero.
                return sign < 0 ? NegativeInfinity : PositiveInfinity;
            }
            if ((config.OverflowMode & OverflowExceptionModes.Underflow) == OverflowExceptionModes.Underflow) {
                throw new FloatDomainError("Exponent underflow");
            }
            // Underflow flushes to zero.
            return PositiveZero;
        }

        #endregion

        #region Special Comparisons

        //TODO: There is some redundancy in this table that could be removed.
        private static int? CheckSpecialComparison(BigDecimal x, BigDecimal y) {
            // Non-zero comparisons are not allowed (since these are not special cases)
            Debug.Assert(!IsNonZeroFinite(x) || !IsNonZeroFinite(y));

            // Encode the array indices
            int xSign = x._sign == -1 ? 0 : 1;
            int ySign = y._sign == -1 ? 0 : 1;
            int xZero = IsZero(x) ? 0 : 1;
            int yZero = IsZero(y) ? 0 : 1;
            int xType = (int)x._type;
            int yType = (int)y._type;
            return SC[xSign, xZero, xType, ySign, yZero, yType];
        }

        // # Generated by the following ruby code:
        // require 'bigdecimal'
        // 
        // signs = [:-, :+]
        // zeros = [true, false]
        // types = [:NaN, :Finite, :Infinite]
        // ops = [:+, :-, :*, :/]
        // 
        // def CreateBigDecimal(sign, zero, type, finiteValue) 
        //   if zero
        //     BigDecimal.new("#{sign}0.0")
        //   else
        //     if type == :NaN
        //       BigDecimal.new("NaN")
        //     elsif type == :Infinite
        //       BigDecimal.new("#{sign}Infinity")
        //     else
        //       BigDecimal.new("#{sign}#{finiteValue}")
        //     end
        //   end
        // end
        // 
        // def map(r, null)
        //    if r.sign == BigDecimal::SIGN_NaN
        //      result = 'NaN'
        //    elsif r.sign == BigDecimal::SIGN_POSITIVE_INFINITE
        //      result = 'PositiveInfinity'
        //    elsif r.sign == BigDecimal::SIGN_NEGATIVE_INFINITE
        //      result = 'NegativeInfinity'
        //    elsif r.sign == BigDecimal::SIGN_POSITIVE_ZERO
        //      result = 'PositiveZero'
        //    elsif r.sign == BigDecimal::SIGN_NEGATIVE_ZERO
        //      result = 'NegativeZero'
        //    elsif null
        //      result = 'null'
        //    else
        //      result = 'Finite'
        //    end
        // end
        // signs.each do |xSign|
        //     zeros.each do |xZero|
        //         types.each do |xType|
        //             signs.each do |ySign|
        //                 zeros.each do |yZero|
        //                     types.each do |yType|
        //                         x = CreateBigDecimal(xSign, xZero, xType, 7)
        //                         y = CreateBigDecimal(ySign, yZero, yType, 11)
        //                         cmp = (x <=> y) || "null"
        //                         puts "// #{map(x, false)} <=> #{map(y, false)} = #{cmp}"
        //                         puts "SC[#{signs.index(xSign)}, #{zeros.index(xZero)}, #{types.index(xType)}, #{signs.index(ySign)}, #{zeros.index(yZero)}, #{types.index(yType)}] = #{cmp};"
        //                     end
        //                 end
        //             end
        //         end
        //     end
        // end
        private static readonly int?[, , , , ,] SC = new int?[2, 2, 3, 2, 2, 3];

        private static void CreateSC() {
            // xSign, xZero, xType, ySign, yZero, yType
            // NegativeZero <=> NegativeZero = 0
            SC[0, 0, 0, 0, 0, 0] = 0;
            // NegativeZero <=> NegativeZero = 0
            SC[0, 0, 0, 0, 0, 1] = 0;
            // NegativeZero <=> NegativeZero = 0
            SC[0, 0, 0, 0, 0, 2] = 0;
            // NegativeZero <=> NaN = null
            SC[0, 0, 0, 0, 1, 0] = null;
            // NegativeZero <=> Finite = 1
            SC[0, 0, 0, 0, 1, 1] = 1;
            // NegativeZero <=> NegativeInfinity = 1
            SC[0, 0, 0, 0, 1, 2] = 1;
            // NegativeZero <=> PositiveZero = 0
            SC[0, 0, 0, 1, 0, 0] = 0;
            // NegativeZero <=> PositiveZero = 0
            SC[0, 0, 0, 1, 0, 1] = 0;
            // NegativeZero <=> PositiveZero = 0
            SC[0, 0, 0, 1, 0, 2] = 0;
            // NegativeZero <=> NaN = null
            SC[0, 0, 0, 1, 1, 0] = null;
            // NegativeZero <=> Finite = -1
            SC[0, 0, 0, 1, 1, 1] = -1;
            // NegativeZero <=> PositiveInfinity = -1
            SC[0, 0, 0, 1, 1, 2] = -1;
            // NegativeZero <=> NegativeZero = 0
            SC[0, 0, 1, 0, 0, 0] = 0;
            // NegativeZero <=> NegativeZero = 0
            SC[0, 0, 1, 0, 0, 1] = 0;
            // NegativeZero <=> NegativeZero = 0
            SC[0, 0, 1, 0, 0, 2] = 0;
            // NegativeZero <=> NaN = null
            SC[0, 0, 1, 0, 1, 0] = null;
            // NegativeZero <=> Finite = 1
            SC[0, 0, 1, 0, 1, 1] = 1;
            // NegativeZero <=> NegativeInfinity = 1
            SC[0, 0, 1, 0, 1, 2] = 1;
            // NegativeZero <=> PositiveZero = 0
            SC[0, 0, 1, 1, 0, 0] = 0;
            // NegativeZero <=> PositiveZero = 0
            SC[0, 0, 1, 1, 0, 1] = 0;
            // NegativeZero <=> PositiveZero = 0
            SC[0, 0, 1, 1, 0, 2] = 0;
            // NegativeZero <=> NaN = null
            SC[0, 0, 1, 1, 1, 0] = null;
            // NegativeZero <=> Finite = -1
            SC[0, 0, 1, 1, 1, 1] = -1;
            // NegativeZero <=> PositiveInfinity = -1
            SC[0, 0, 1, 1, 1, 2] = -1;
            // NegativeZero <=> NegativeZero = 0
            SC[0, 0, 2, 0, 0, 0] = 0;
            // NegativeZero <=> NegativeZero = 0
            SC[0, 0, 2, 0, 0, 1] = 0;
            // NegativeZero <=> NegativeZero = 0
            SC[0, 0, 2, 0, 0, 2] = 0;
            // NegativeZero <=> NaN = null
            SC[0, 0, 2, 0, 1, 0] = null;
            // NegativeZero <=> Finite = 1
            SC[0, 0, 2, 0, 1, 1] = 1;
            // NegativeZero <=> NegativeInfinity = 1
            SC[0, 0, 2, 0, 1, 2] = 1;
            // NegativeZero <=> PositiveZero = 0
            SC[0, 0, 2, 1, 0, 0] = 0;
            // NegativeZero <=> PositiveZero = 0
            SC[0, 0, 2, 1, 0, 1] = 0;
            // NegativeZero <=> PositiveZero = 0
            SC[0, 0, 2, 1, 0, 2] = 0;
            // NegativeZero <=> NaN = null
            SC[0, 0, 2, 1, 1, 0] = null;
            // NegativeZero <=> Finite = -1
            SC[0, 0, 2, 1, 1, 1] = -1;
            // NegativeZero <=> PositiveInfinity = -1
            SC[0, 0, 2, 1, 1, 2] = -1;
            // NaN <=> NegativeZero = null
            SC[0, 1, 0, 0, 0, 0] = null;
            // NaN <=> NegativeZero = null
            SC[0, 1, 0, 0, 0, 1] = null;
            // NaN <=> NegativeZero = null
            SC[0, 1, 0, 0, 0, 2] = null;
            // NaN <=> NaN = null
            SC[0, 1, 0, 0, 1, 0] = null;
            // NaN <=> Finite = null
            SC[0, 1, 0, 0, 1, 1] = null;
            // NaN <=> NegativeInfinity = null
            SC[0, 1, 0, 0, 1, 2] = null;
            // NaN <=> PositiveZero = null
            SC[0, 1, 0, 1, 0, 0] = null;
            // NaN <=> PositiveZero = null
            SC[0, 1, 0, 1, 0, 1] = null;
            // NaN <=> PositiveZero = null
            SC[0, 1, 0, 1, 0, 2] = null;
            // NaN <=> NaN = null
            SC[0, 1, 0, 1, 1, 0] = null;
            // NaN <=> Finite = null
            SC[0, 1, 0, 1, 1, 1] = null;
            // NaN <=> PositiveInfinity = null
            SC[0, 1, 0, 1, 1, 2] = null;
            // Finite <=> NegativeZero = -1
            SC[0, 1, 1, 0, 0, 0] = -1;
            // Finite <=> NegativeZero = -1
            SC[0, 1, 1, 0, 0, 1] = -1;
            // Finite <=> NegativeZero = -1
            SC[0, 1, 1, 0, 0, 2] = -1;
            // Finite <=> NaN = null
            SC[0, 1, 1, 0, 1, 0] = null;
            // Finite <=> Finite = 1
            SC[0, 1, 1, 0, 1, 1] = 1;
            // Finite <=> NegativeInfinity = 1
            SC[0, 1, 1, 0, 1, 2] = 1;
            // Finite <=> PositiveZero = -1
            SC[0, 1, 1, 1, 0, 0] = -1;
            // Finite <=> PositiveZero = -1
            SC[0, 1, 1, 1, 0, 1] = -1;
            // Finite <=> PositiveZero = -1
            SC[0, 1, 1, 1, 0, 2] = -1;
            // Finite <=> NaN = null
            SC[0, 1, 1, 1, 1, 0] = null;
            // Finite <=> Finite = -1
            SC[0, 1, 1, 1, 1, 1] = -1;
            // Finite <=> PositiveInfinity = -1
            SC[0, 1, 1, 1, 1, 2] = -1;
            // NegativeInfinity <=> NegativeZero = -1
            SC[0, 1, 2, 0, 0, 0] = -1;
            // NegativeInfinity <=> NegativeZero = -1
            SC[0, 1, 2, 0, 0, 1] = -1;
            // NegativeInfinity <=> NegativeZero = -1
            SC[0, 1, 2, 0, 0, 2] = -1;
            // NegativeInfinity <=> NaN = null
            SC[0, 1, 2, 0, 1, 0] = null;
            // NegativeInfinity <=> Finite = -1
            SC[0, 1, 2, 0, 1, 1] = -1;
            // NegativeInfinity <=> NegativeInfinity = 0
            SC[0, 1, 2, 0, 1, 2] = 0;
            // NegativeInfinity <=> PositiveZero = -1
            SC[0, 1, 2, 1, 0, 0] = -1;
            // NegativeInfinity <=> PositiveZero = -1
            SC[0, 1, 2, 1, 0, 1] = -1;
            // NegativeInfinity <=> PositiveZero = -1
            SC[0, 1, 2, 1, 0, 2] = -1;
            // NegativeInfinity <=> NaN = null
            SC[0, 1, 2, 1, 1, 0] = null;
            // NegativeInfinity <=> Finite = -1
            SC[0, 1, 2, 1, 1, 1] = -1;
            // NegativeInfinity <=> PositiveInfinity = -1
            SC[0, 1, 2, 1, 1, 2] = -1;
            // PositiveZero <=> NegativeZero = 0
            SC[1, 0, 0, 0, 0, 0] = 0;
            // PositiveZero <=> NegativeZero = 0
            SC[1, 0, 0, 0, 0, 1] = 0;
            // PositiveZero <=> NegativeZero = 0
            SC[1, 0, 0, 0, 0, 2] = 0;
            // PositiveZero <=> NaN = null
            SC[1, 0, 0, 0, 1, 0] = null;
            // PositiveZero <=> Finite = 1
            SC[1, 0, 0, 0, 1, 1] = 1;
            // PositiveZero <=> NegativeInfinity = 1
            SC[1, 0, 0, 0, 1, 2] = 1;
            // PositiveZero <=> PositiveZero = 0
            SC[1, 0, 0, 1, 0, 0] = 0;
            // PositiveZero <=> PositiveZero = 0
            SC[1, 0, 0, 1, 0, 1] = 0;
            // PositiveZero <=> PositiveZero = 0
            SC[1, 0, 0, 1, 0, 2] = 0;
            // PositiveZero <=> NaN = null
            SC[1, 0, 0, 1, 1, 0] = null;
            // PositiveZero <=> Finite = -1
            SC[1, 0, 0, 1, 1, 1] = -1;
            // PositiveZero <=> PositiveInfinity = -1
            SC[1, 0, 0, 1, 1, 2] = -1;
            // PositiveZero <=> NegativeZero = 0
            SC[1, 0, 1, 0, 0, 0] = 0;
            // PositiveZero <=> NegativeZero = 0
            SC[1, 0, 1, 0, 0, 1] = 0;
            // PositiveZero <=> NegativeZero = 0
            SC[1, 0, 1, 0, 0, 2] = 0;
            // PositiveZero <=> NaN = null
            SC[1, 0, 1, 0, 1, 0] = null;
            // PositiveZero <=> Finite = 1
            SC[1, 0, 1, 0, 1, 1] = 1;
            // PositiveZero <=> NegativeInfinity = 1
            SC[1, 0, 1, 0, 1, 2] = 1;
            // PositiveZero <=> PositiveZero = 0
            SC[1, 0, 1, 1, 0, 0] = 0;
            // PositiveZero <=> PositiveZero = 0
            SC[1, 0, 1, 1, 0, 1] = 0;
            // PositiveZero <=> PositiveZero = 0
            SC[1, 0, 1, 1, 0, 2] = 0;
            // PositiveZero <=> NaN = null
            SC[1, 0, 1, 1, 1, 0] = null;
            // PositiveZero <=> Finite = -1
            SC[1, 0, 1, 1, 1, 1] = -1;
            // PositiveZero <=> PositiveInfinity = -1
            SC[1, 0, 1, 1, 1, 2] = -1;
            // PositiveZero <=> NegativeZero = 0
            SC[1, 0, 2, 0, 0, 0] = 0;
            // PositiveZero <=> NegativeZero = 0
            SC[1, 0, 2, 0, 0, 1] = 0;
            // PositiveZero <=> NegativeZero = 0
            SC[1, 0, 2, 0, 0, 2] = 0;
            // PositiveZero <=> NaN = null
            SC[1, 0, 2, 0, 1, 0] = null;
            // PositiveZero <=> Finite = 1
            SC[1, 0, 2, 0, 1, 1] = 1;
            // PositiveZero <=> NegativeInfinity = 1
            SC[1, 0, 2, 0, 1, 2] = 1;
            // PositiveZero <=> PositiveZero = 0
            SC[1, 0, 2, 1, 0, 0] = 0;
            // PositiveZero <=> PositiveZero = 0
            SC[1, 0, 2, 1, 0, 1] = 0;
            // PositiveZero <=> PositiveZero = 0
            SC[1, 0, 2, 1, 0, 2] = 0;
            // PositiveZero <=> NaN = null
            SC[1, 0, 2, 1, 1, 0] = null;
            // PositiveZero <=> Finite = -1
            SC[1, 0, 2, 1, 1, 1] = -1;
            // PositiveZero <=> PositiveInfinity = -1
            SC[1, 0, 2, 1, 1, 2] = -1;
            // NaN <=> NegativeZero = null
            SC[1, 1, 0, 0, 0, 0] = null;
            // NaN <=> NegativeZero = null
            SC[1, 1, 0, 0, 0, 1] = null;
            // NaN <=> NegativeZero = null
            SC[1, 1, 0, 0, 0, 2] = null;
            // NaN <=> NaN = null
            SC[1, 1, 0, 0, 1, 0] = null;
            // NaN <=> Finite = null
            SC[1, 1, 0, 0, 1, 1] = null;
            // NaN <=> NegativeInfinity = null
            SC[1, 1, 0, 0, 1, 2] = null;
            // NaN <=> PositiveZero = null
            SC[1, 1, 0, 1, 0, 0] = null;
            // NaN <=> PositiveZero = null
            SC[1, 1, 0, 1, 0, 1] = null;
            // NaN <=> PositiveZero = null
            SC[1, 1, 0, 1, 0, 2] = null;
            // NaN <=> NaN = null
            SC[1, 1, 0, 1, 1, 0] = null;
            // NaN <=> Finite = null
            SC[1, 1, 0, 1, 1, 1] = null;
            // NaN <=> PositiveInfinity = null
            SC[1, 1, 0, 1, 1, 2] = null;
            // Finite <=> NegativeZero = 1
            SC[1, 1, 1, 0, 0, 0] = 1;
            // Finite <=> NegativeZero = 1
            SC[1, 1, 1, 0, 0, 1] = 1;
            // Finite <=> NegativeZero = 1
            SC[1, 1, 1, 0, 0, 2] = 1;
            // Finite <=> NaN = null
            SC[1, 1, 1, 0, 1, 0] = null;
            // Finite <=> Finite = 1
            SC[1, 1, 1, 0, 1, 1] = 1;
            // Finite <=> NegativeInfinity = 1
            SC[1, 1, 1, 0, 1, 2] = 1;
            // Finite <=> PositiveZero = 1
            SC[1, 1, 1, 1, 0, 0] = 1;
            // Finite <=> PositiveZero = 1
            SC[1, 1, 1, 1, 0, 1] = 1;
            // Finite <=> PositiveZero = 1
            SC[1, 1, 1, 1, 0, 2] = 1;
            // Finite <=> NaN = null
            SC[1, 1, 1, 1, 1, 0] = null;
            // Finite <=> Finite = -1
            SC[1, 1, 1, 1, 1, 1] = -1;
            // Finite <=> PositiveInfinity = -1
            SC[1, 1, 1, 1, 1, 2] = -1;
            // PositiveInfinity <=> NegativeZero = 1
            SC[1, 1, 2, 0, 0, 0] = 1;
            // PositiveInfinity <=> NegativeZero = 1
            SC[1, 1, 2, 0, 0, 1] = 1;
            // PositiveInfinity <=> NegativeZero = 1
            SC[1, 1, 2, 0, 0, 2] = 1;
            // PositiveInfinity <=> NaN = null
            SC[1, 1, 2, 0, 1, 0] = null;
            // PositiveInfinity <=> Finite = 1
            SC[1, 1, 2, 0, 1, 1] = 1;
            // PositiveInfinity <=> NegativeInfinity = 1
            SC[1, 1, 2, 0, 1, 2] = 1;
            // PositiveInfinity <=> PositiveZero = 1
            SC[1, 1, 2, 1, 0, 0] = 1;
            // PositiveInfinity <=> PositiveZero = 1
            SC[1, 1, 2, 1, 0, 1] = 1;
            // PositiveInfinity <=> PositiveZero = 1
            SC[1, 1, 2, 1, 0, 2] = 1;
            // PositiveInfinity <=> NaN = null
            SC[1, 1, 2, 1, 1, 0] = null;
            // PositiveInfinity <=> Finite = 1
            SC[1, 1, 2, 1, 1, 1] = 1;
            // PositiveInfinity <=> PositiveInfinity = 0
            SC[1, 1, 2, 1, 1, 2] = 0;
        }
        #endregion

        #region Special Results
        //TODO: Probably don't need the subtract operation since it is symetric with addition operation in terms of special results
        /// <summary>
        /// Look up special results for calculations that involve unusual values such as NaN, infinity and zero.
        /// </summary>
        /// <param name="x">The left hand side of the operation</param>
        /// <param name="y">The right hand side of the operation</param>
        /// <param name="op">The operation itself</param>
        /// <returns>The special result or null if the result is not special</returns>
        private static BigDecimal CheckSpecialResult(Config config, BigDecimal x, BigDecimal y, BasicOperations op) {
            // Encode the array indices
            int xSign = x._sign == -1 ? 0 : 1;
            int ySign = y._sign == -1 ? 0 : 1;
            int xZero = IsZero(x) ? 0 : 1;
            int yZero = IsZero(y) ? 0 : 1;
            int xType = (int)x._type;
            int yType = (int)y._type;
            int opValue = (int)op;

            // Look up the special result
            BigDecimal result = SR[xSign, xZero, xType, opValue, ySign, yZero, yType];
            if (result != null) {
                return CheckOverflowExceptions(config, result);
            }
            return null;
        }

        private static BigDecimal/*!*/ CheckOverflowExceptions(Config/*!*/ config, BigDecimal/*!*/ result) {
            if (IsNaN(result) && (config.OverflowMode & OverflowExceptionModes.NaN) == OverflowExceptionModes.NaN) {
                throw new FloatDomainError("Computation results to 'NaN'");
            } else if (IsInfinite(result) && (config.OverflowMode & OverflowExceptionModes.Infinity) == OverflowExceptionModes.Infinity) {
                throw new FloatDomainError(result.Sign > 0 ? "Computation results in 'Infinity'" : "Computation results in '-Infinity'");
            }
            return result;
        }

        /// <remarks>
        /// Generated by the following ruby code
        /// <code>
        ///   require 'bigdecimal'
        ///   
        ///   signs = [:-, :+]
        ///   zeros = [true, false]
        ///   types = [:NaN, :Finite, :Infinite]
        ///   ops = [:+, :-, :*, :/]
        ///   
        ///   def CreateBigDecimal(sign, zero, type, finiteValue) 
        ///     if zero
        ///       BigDecimal.new("#{sign}0.0")
        ///     else
        ///       if type == :NaN
        ///         BigDecimal.new("NaN")
        ///       elsif type == :Infinite
        ///         BigDecimal.new("#{sign}Infinity")
        ///       else
        ///         BigDecimal.new("#{sign}#{finiteValue}")
        ///       end
        ///     end
        ///   end
        ///   
        ///   def map(r, null)
        ///      if r.sign == BigDecimal::SIGN_NaN
        ///        result = 'NaN'
        ///      elsif r.sign == BigDecimal::SIGN_POSITIVE_INFINITE
        ///        result = 'PositiveInfinity'
        ///      elsif r.sign == BigDecimal::SIGN_NEGATIVE_INFINITE
        ///        result = 'NegativeInfinity'
        ///      elsif r.sign == BigDecimal::SIGN_POSITIVE_ZERO
        ///        result = 'PositiveZero'
        ///      elsif r.sign == BigDecimal::SIGN_NEGATIVE_ZERO
        ///        result = 'NegativeZero'
        ///      elsif null
        ///        result = 'null'
        ///      else
        ///        result = 'Finite'
        ///      end
        ///   end
        ///   
        ///   signs.each do |xSign|
        ///   zeros.each do |xZero|
        ///    types.each do |xType|
        ///      ops.each do |op|
        ///        signs.each do |ySign|
        ///          zeros.each do |yZero|
        ///            types.each do |yType|
        ///              x = CreateBigDecimal(xSign, xZero, xType, 7)
        ///              y = CreateBigDecimal(ySign, yZero, yType, 11)
        ///              r = x.send(op, y)
        ///              result = map(r, true)
        ///              puts "// #{map(x, false)} #{op} #{map(y, false)} = #{result}"
        ///              puts "SR[#{signs.index(xSign)}, #{zeros.index(xZero)}, #{types.index(xType)}, #{ops.index(op)}, #{signs.index(ySign)}, #{zeros.index(yZero)}, #{types.index(yType)}] = #{result};"
        ///            end
        ///          end
        ///        end
        ///      end
        ///    end
        ///   end
        ///   end 
        ///   </code>
        /// </remarks>
        private static readonly BigDecimal[, , , , , ,] SR = new BigDecimal[2, 2, 3, 4, 2, 2, 3];
        private static void CreateSR() {
            //[xSign, xZero, xType, Op, ySign, yZero, yType]
            // NegativeZero + NegativeZero = NegativeZero
            SR[0, 0, 0, 0, 0, 0, 0] = NegativeZero;
            // NegativeZero + NegativeZero = NegativeZero
            SR[0, 0, 0, 0, 0, 0, 1] = NegativeZero;
            // NegativeZero + NegativeZero = NegativeZero
            SR[0, 0, 0, 0, 0, 0, 2] = NegativeZero;
            // NegativeZero + NaN = NaN
            SR[0, 0, 0, 0, 0, 1, 0] = NaN;
            // NegativeZero + Finite = null
            SR[0, 0, 0, 0, 0, 1, 1] = null;
            // NegativeZero + NegativeInfinity = NegativeInfinity
            SR[0, 0, 0, 0, 0, 1, 2] = NegativeInfinity;
            // NegativeZero + PositiveZero = PositiveZero
            SR[0, 0, 0, 0, 1, 0, 0] = PositiveZero;
            // NegativeZero + PositiveZero = PositiveZero
            SR[0, 0, 0, 0, 1, 0, 1] = PositiveZero;
            // NegativeZero + PositiveZero = PositiveZero
            SR[0, 0, 0, 0, 1, 0, 2] = PositiveZero;
            // NegativeZero + NaN = NaN
            SR[0, 0, 0, 0, 1, 1, 0] = NaN;
            // NegativeZero + Finite = null
            SR[0, 0, 0, 0, 1, 1, 1] = null;
            // NegativeZero + PositiveInfinity = PositiveInfinity
            SR[0, 0, 0, 0, 1, 1, 2] = PositiveInfinity;
            // NegativeZero - NegativeZero = PositiveZero
            SR[0, 0, 0, 1, 0, 0, 0] = PositiveZero;
            // NegativeZero - NegativeZero = PositiveZero
            SR[0, 0, 0, 1, 0, 0, 1] = PositiveZero;
            // NegativeZero - NegativeZero = PositiveZero
            SR[0, 0, 0, 1, 0, 0, 2] = PositiveZero;
            // NegativeZero - NaN = NaN
            SR[0, 0, 0, 1, 0, 1, 0] = NaN;
            // NegativeZero - Finite = null
            SR[0, 0, 0, 1, 0, 1, 1] = null;
            // NegativeZero - NegativeInfinity = PositiveInfinity
            SR[0, 0, 0, 1, 0, 1, 2] = PositiveInfinity;
            // NegativeZero - PositiveZero = NegativeZero
            SR[0, 0, 0, 1, 1, 0, 0] = NegativeZero;
            // NegativeZero - PositiveZero = NegativeZero
            SR[0, 0, 0, 1, 1, 0, 1] = NegativeZero;
            // NegativeZero - PositiveZero = NegativeZero
            SR[0, 0, 0, 1, 1, 0, 2] = NegativeZero;
            // NegativeZero - NaN = NaN
            SR[0, 0, 0, 1, 1, 1, 0] = NaN;
            // NegativeZero - Finite = null
            SR[0, 0, 0, 1, 1, 1, 1] = null;
            // NegativeZero - PositiveInfinity = NegativeInfinity
            SR[0, 0, 0, 1, 1, 1, 2] = NegativeInfinity;
            // NegativeZero * NegativeZero = PositiveZero
            SR[0, 0, 0, 2, 0, 0, 0] = PositiveZero;
            // NegativeZero * NegativeZero = PositiveZero
            SR[0, 0, 0, 2, 0, 0, 1] = PositiveZero;
            // NegativeZero * NegativeZero = PositiveZero
            SR[0, 0, 0, 2, 0, 0, 2] = PositiveZero;
            // NegativeZero * NaN = NaN
            SR[0, 0, 0, 2, 0, 1, 0] = NaN;
            // NegativeZero * Finite = PositiveZero
            SR[0, 0, 0, 2, 0, 1, 1] = PositiveZero;
            // NegativeZero * NegativeInfinity = NaN
            SR[0, 0, 0, 2, 0, 1, 2] = NaN;
            // NegativeZero * PositiveZero = NegativeZero
            SR[0, 0, 0, 2, 1, 0, 0] = NegativeZero;
            // NegativeZero * PositiveZero = NegativeZero
            SR[0, 0, 0, 2, 1, 0, 1] = NegativeZero;
            // NegativeZero * PositiveZero = NegativeZero
            SR[0, 0, 0, 2, 1, 0, 2] = NegativeZero;
            // NegativeZero * NaN = NaN
            SR[0, 0, 0, 2, 1, 1, 0] = NaN;
            // NegativeZero * Finite = NegativeZero
            SR[0, 0, 0, 2, 1, 1, 1] = NegativeZero;
            // NegativeZero * PositiveInfinity = NaN
            SR[0, 0, 0, 2, 1, 1, 2] = NaN;
            // NegativeZero / NegativeZero = NaN
            SR[0, 0, 0, 3, 0, 0, 0] = NaN;
            // NegativeZero / NegativeZero = NaN
            SR[0, 0, 0, 3, 0, 0, 1] = NaN;
            // NegativeZero / NegativeZero = NaN
            SR[0, 0, 0, 3, 0, 0, 2] = NaN;
            // NegativeZero / NaN = NaN
            SR[0, 0, 0, 3, 0, 1, 0] = NaN;
            // NegativeZero / Finite = PositiveZero
            SR[0, 0, 0, 3, 0, 1, 1] = PositiveZero;
            // NegativeZero / NegativeInfinity = PositiveZero
            SR[0, 0, 0, 3, 0, 1, 2] = PositiveZero;
            // NegativeZero / PositiveZero = NaN
            SR[0, 0, 0, 3, 1, 0, 0] = NaN;
            // NegativeZero / PositiveZero = NaN
            SR[0, 0, 0, 3, 1, 0, 1] = NaN;
            // NegativeZero / PositiveZero = NaN
            SR[0, 0, 0, 3, 1, 0, 2] = NaN;
            // NegativeZero / NaN = NaN
            SR[0, 0, 0, 3, 1, 1, 0] = NaN;
            // NegativeZero / Finite = NegativeZero
            SR[0, 0, 0, 3, 1, 1, 1] = NegativeZero;
            // NegativeZero / PositiveInfinity = NegativeZero
            SR[0, 0, 0, 3, 1, 1, 2] = NegativeZero;
            // NegativeZero + NegativeZero = NegativeZero
            SR[0, 0, 1, 0, 0, 0, 0] = NegativeZero;
            // NegativeZero + NegativeZero = NegativeZero
            SR[0, 0, 1, 0, 0, 0, 1] = NegativeZero;
            // NegativeZero + NegativeZero = NegativeZero
            SR[0, 0, 1, 0, 0, 0, 2] = NegativeZero;
            // NegativeZero + NaN = NaN
            SR[0, 0, 1, 0, 0, 1, 0] = NaN;
            // NegativeZero + Finite = null
            SR[0, 0, 1, 0, 0, 1, 1] = null;
            // NegativeZero + NegativeInfinity = NegativeInfinity
            SR[0, 0, 1, 0, 0, 1, 2] = NegativeInfinity;
            // NegativeZero + PositiveZero = PositiveZero
            SR[0, 0, 1, 0, 1, 0, 0] = PositiveZero;
            // NegativeZero + PositiveZero = PositiveZero
            SR[0, 0, 1, 0, 1, 0, 1] = PositiveZero;
            // NegativeZero + PositiveZero = PositiveZero
            SR[0, 0, 1, 0, 1, 0, 2] = PositiveZero;
            // NegativeZero + NaN = NaN
            SR[0, 0, 1, 0, 1, 1, 0] = NaN;
            // NegativeZero + Finite = null
            SR[0, 0, 1, 0, 1, 1, 1] = null;
            // NegativeZero + PositiveInfinity = PositiveInfinity
            SR[0, 0, 1, 0, 1, 1, 2] = PositiveInfinity;
            // NegativeZero - NegativeZero = PositiveZero
            SR[0, 0, 1, 1, 0, 0, 0] = PositiveZero;
            // NegativeZero - NegativeZero = PositiveZero
            SR[0, 0, 1, 1, 0, 0, 1] = PositiveZero;
            // NegativeZero - NegativeZero = PositiveZero
            SR[0, 0, 1, 1, 0, 0, 2] = PositiveZero;
            // NegativeZero - NaN = NaN
            SR[0, 0, 1, 1, 0, 1, 0] = NaN;
            // NegativeZero - Finite = null
            SR[0, 0, 1, 1, 0, 1, 1] = null;
            // NegativeZero - NegativeInfinity = PositiveInfinity
            SR[0, 0, 1, 1, 0, 1, 2] = PositiveInfinity;
            // NegativeZero - PositiveZero = NegativeZero
            SR[0, 0, 1, 1, 1, 0, 0] = NegativeZero;
            // NegativeZero - PositiveZero = NegativeZero
            SR[0, 0, 1, 1, 1, 0, 1] = NegativeZero;
            // NegativeZero - PositiveZero = NegativeZero
            SR[0, 0, 1, 1, 1, 0, 2] = NegativeZero;
            // NegativeZero - NaN = NaN
            SR[0, 0, 1, 1, 1, 1, 0] = NaN;
            // NegativeZero - Finite = null
            SR[0, 0, 1, 1, 1, 1, 1] = null;
            // NegativeZero - PositiveInfinity = NegativeInfinity
            SR[0, 0, 1, 1, 1, 1, 2] = NegativeInfinity;
            // NegativeZero * NegativeZero = PositiveZero
            SR[0, 0, 1, 2, 0, 0, 0] = PositiveZero;
            // NegativeZero * NegativeZero = PositiveZero
            SR[0, 0, 1, 2, 0, 0, 1] = PositiveZero;
            // NegativeZero * NegativeZero = PositiveZero
            SR[0, 0, 1, 2, 0, 0, 2] = PositiveZero;
            // NegativeZero * NaN = NaN
            SR[0, 0, 1, 2, 0, 1, 0] = NaN;
            // NegativeZero * Finite = PositiveZero
            SR[0, 0, 1, 2, 0, 1, 1] = PositiveZero;
            // NegativeZero * NegativeInfinity = NaN
            SR[0, 0, 1, 2, 0, 1, 2] = NaN;
            // NegativeZero * PositiveZero = NegativeZero
            SR[0, 0, 1, 2, 1, 0, 0] = NegativeZero;
            // NegativeZero * PositiveZero = NegativeZero
            SR[0, 0, 1, 2, 1, 0, 1] = NegativeZero;
            // NegativeZero * PositiveZero = NegativeZero
            SR[0, 0, 1, 2, 1, 0, 2] = NegativeZero;
            // NegativeZero * NaN = NaN
            SR[0, 0, 1, 2, 1, 1, 0] = NaN;
            // NegativeZero * Finite = NegativeZero
            SR[0, 0, 1, 2, 1, 1, 1] = NegativeZero;
            // NegativeZero * PositiveInfinity = NaN
            SR[0, 0, 1, 2, 1, 1, 2] = NaN;
            // NegativeZero / NegativeZero = NaN
            SR[0, 0, 1, 3, 0, 0, 0] = NaN;
            // NegativeZero / NegativeZero = NaN
            SR[0, 0, 1, 3, 0, 0, 1] = NaN;
            // NegativeZero / NegativeZero = NaN
            SR[0, 0, 1, 3, 0, 0, 2] = NaN;
            // NegativeZero / NaN = NaN
            SR[0, 0, 1, 3, 0, 1, 0] = NaN;
            // NegativeZero / Finite = PositiveZero
            SR[0, 0, 1, 3, 0, 1, 1] = PositiveZero;
            // NegativeZero / NegativeInfinity = PositiveZero
            SR[0, 0, 1, 3, 0, 1, 2] = PositiveZero;
            // NegativeZero / PositiveZero = NaN
            SR[0, 0, 1, 3, 1, 0, 0] = NaN;
            // NegativeZero / PositiveZero = NaN
            SR[0, 0, 1, 3, 1, 0, 1] = NaN;
            // NegativeZero / PositiveZero = NaN
            SR[0, 0, 1, 3, 1, 0, 2] = NaN;
            // NegativeZero / NaN = NaN
            SR[0, 0, 1, 3, 1, 1, 0] = NaN;
            // NegativeZero / Finite = NegativeZero
            SR[0, 0, 1, 3, 1, 1, 1] = NegativeZero;
            // NegativeZero / PositiveInfinity = NegativeZero
            SR[0, 0, 1, 3, 1, 1, 2] = NegativeZero;
            // NegativeZero + NegativeZero = NegativeZero
            SR[0, 0, 2, 0, 0, 0, 0] = NegativeZero;
            // NegativeZero + NegativeZero = NegativeZero
            SR[0, 0, 2, 0, 0, 0, 1] = NegativeZero;
            // NegativeZero + NegativeZero = NegativeZero
            SR[0, 0, 2, 0, 0, 0, 2] = NegativeZero;
            // NegativeZero + NaN = NaN
            SR[0, 0, 2, 0, 0, 1, 0] = NaN;
            // NegativeZero + Finite = null
            SR[0, 0, 2, 0, 0, 1, 1] = null;
            // NegativeZero + NegativeInfinity = NegativeInfinity
            SR[0, 0, 2, 0, 0, 1, 2] = NegativeInfinity;
            // NegativeZero + PositiveZero = PositiveZero
            SR[0, 0, 2, 0, 1, 0, 0] = PositiveZero;
            // NegativeZero + PositiveZero = PositiveZero
            SR[0, 0, 2, 0, 1, 0, 1] = PositiveZero;
            // NegativeZero + PositiveZero = PositiveZero
            SR[0, 0, 2, 0, 1, 0, 2] = PositiveZero;
            // NegativeZero + NaN = NaN
            SR[0, 0, 2, 0, 1, 1, 0] = NaN;
            // NegativeZero + Finite = null
            SR[0, 0, 2, 0, 1, 1, 1] = null;
            // NegativeZero + PositiveInfinity = PositiveInfinity
            SR[0, 0, 2, 0, 1, 1, 2] = PositiveInfinity;
            // NegativeZero - NegativeZero = PositiveZero
            SR[0, 0, 2, 1, 0, 0, 0] = PositiveZero;
            // NegativeZero - NegativeZero = PositiveZero
            SR[0, 0, 2, 1, 0, 0, 1] = PositiveZero;
            // NegativeZero - NegativeZero = PositiveZero
            SR[0, 0, 2, 1, 0, 0, 2] = PositiveZero;
            // NegativeZero - NaN = NaN
            SR[0, 0, 2, 1, 0, 1, 0] = NaN;
            // NegativeZero - Finite = null
            SR[0, 0, 2, 1, 0, 1, 1] = null;
            // NegativeZero - NegativeInfinity = PositiveInfinity
            SR[0, 0, 2, 1, 0, 1, 2] = PositiveInfinity;
            // NegativeZero - PositiveZero = NegativeZero
            SR[0, 0, 2, 1, 1, 0, 0] = NegativeZero;
            // NegativeZero - PositiveZero = NegativeZero
            SR[0, 0, 2, 1, 1, 0, 1] = NegativeZero;
            // NegativeZero - PositiveZero = NegativeZero
            SR[0, 0, 2, 1, 1, 0, 2] = NegativeZero;
            // NegativeZero - NaN = NaN
            SR[0, 0, 2, 1, 1, 1, 0] = NaN;
            // NegativeZero - Finite = null
            SR[0, 0, 2, 1, 1, 1, 1] = null;
            // NegativeZero - PositiveInfinity = NegativeInfinity
            SR[0, 0, 2, 1, 1, 1, 2] = NegativeInfinity;
            // NegativeZero * NegativeZero = PositiveZero
            SR[0, 0, 2, 2, 0, 0, 0] = PositiveZero;
            // NegativeZero * NegativeZero = PositiveZero
            SR[0, 0, 2, 2, 0, 0, 1] = PositiveZero;
            // NegativeZero * NegativeZero = PositiveZero
            SR[0, 0, 2, 2, 0, 0, 2] = PositiveZero;
            // NegativeZero * NaN = NaN
            SR[0, 0, 2, 2, 0, 1, 0] = NaN;
            // NegativeZero * Finite = PositiveZero
            SR[0, 0, 2, 2, 0, 1, 1] = PositiveZero;
            // NegativeZero * NegativeInfinity = NaN
            SR[0, 0, 2, 2, 0, 1, 2] = NaN;
            // NegativeZero * PositiveZero = NegativeZero
            SR[0, 0, 2, 2, 1, 0, 0] = NegativeZero;
            // NegativeZero * PositiveZero = NegativeZero
            SR[0, 0, 2, 2, 1, 0, 1] = NegativeZero;
            // NegativeZero * PositiveZero = NegativeZero
            SR[0, 0, 2, 2, 1, 0, 2] = NegativeZero;
            // NegativeZero * NaN = NaN
            SR[0, 0, 2, 2, 1, 1, 0] = NaN;
            // NegativeZero * Finite = NegativeZero
            SR[0, 0, 2, 2, 1, 1, 1] = NegativeZero;
            // NegativeZero * PositiveInfinity = NaN
            SR[0, 0, 2, 2, 1, 1, 2] = NaN;
            // NegativeZero / NegativeZero = NaN
            SR[0, 0, 2, 3, 0, 0, 0] = NaN;
            // NegativeZero / NegativeZero = NaN
            SR[0, 0, 2, 3, 0, 0, 1] = NaN;
            // NegativeZero / NegativeZero = NaN
            SR[0, 0, 2, 3, 0, 0, 2] = NaN;
            // NegativeZero / NaN = NaN
            SR[0, 0, 2, 3, 0, 1, 0] = NaN;
            // NegativeZero / Finite = PositiveZero
            SR[0, 0, 2, 3, 0, 1, 1] = PositiveZero;
            // NegativeZero / NegativeInfinity = PositiveZero
            SR[0, 0, 2, 3, 0, 1, 2] = PositiveZero;
            // NegativeZero / PositiveZero = NaN
            SR[0, 0, 2, 3, 1, 0, 0] = NaN;
            // NegativeZero / PositiveZero = NaN
            SR[0, 0, 2, 3, 1, 0, 1] = NaN;
            // NegativeZero / PositiveZero = NaN
            SR[0, 0, 2, 3, 1, 0, 2] = NaN;
            // NegativeZero / NaN = NaN
            SR[0, 0, 2, 3, 1, 1, 0] = NaN;
            // NegativeZero / Finite = NegativeZero
            SR[0, 0, 2, 3, 1, 1, 1] = NegativeZero;
            // NegativeZero / PositiveInfinity = NegativeZero
            SR[0, 0, 2, 3, 1, 1, 2] = NegativeZero;
            // NaN + NegativeZero = NaN
            SR[0, 1, 0, 0, 0, 0, 0] = NaN;
            // NaN + NegativeZero = NaN
            SR[0, 1, 0, 0, 0, 0, 1] = NaN;
            // NaN + NegativeZero = NaN
            SR[0, 1, 0, 0, 0, 0, 2] = NaN;
            // NaN + NaN = NaN
            SR[0, 1, 0, 0, 0, 1, 0] = NaN;
            // NaN + Finite = NaN
            SR[0, 1, 0, 0, 0, 1, 1] = NaN;
            // NaN + NegativeInfinity = NaN
            SR[0, 1, 0, 0, 0, 1, 2] = NaN;
            // NaN + PositiveZero = NaN
            SR[0, 1, 0, 0, 1, 0, 0] = NaN;
            // NaN + PositiveZero = NaN
            SR[0, 1, 0, 0, 1, 0, 1] = NaN;
            // NaN + PositiveZero = NaN
            SR[0, 1, 0, 0, 1, 0, 2] = NaN;
            // NaN + NaN = NaN
            SR[0, 1, 0, 0, 1, 1, 0] = NaN;
            // NaN + Finite = NaN
            SR[0, 1, 0, 0, 1, 1, 1] = NaN;
            // NaN + PositiveInfinity = NaN
            SR[0, 1, 0, 0, 1, 1, 2] = NaN;
            // NaN - NegativeZero = NaN
            SR[0, 1, 0, 1, 0, 0, 0] = NaN;
            // NaN - NegativeZero = NaN
            SR[0, 1, 0, 1, 0, 0, 1] = NaN;
            // NaN - NegativeZero = NaN
            SR[0, 1, 0, 1, 0, 0, 2] = NaN;
            // NaN - NaN = NaN
            SR[0, 1, 0, 1, 0, 1, 0] = NaN;
            // NaN - Finite = NaN
            SR[0, 1, 0, 1, 0, 1, 1] = NaN;
            // NaN - NegativeInfinity = NaN
            SR[0, 1, 0, 1, 0, 1, 2] = NaN;
            // NaN - PositiveZero = NaN
            SR[0, 1, 0, 1, 1, 0, 0] = NaN;
            // NaN - PositiveZero = NaN
            SR[0, 1, 0, 1, 1, 0, 1] = NaN;
            // NaN - PositiveZero = NaN
            SR[0, 1, 0, 1, 1, 0, 2] = NaN;
            // NaN - NaN = NaN
            SR[0, 1, 0, 1, 1, 1, 0] = NaN;
            // NaN - Finite = NaN
            SR[0, 1, 0, 1, 1, 1, 1] = NaN;
            // NaN - PositiveInfinity = NaN
            SR[0, 1, 0, 1, 1, 1, 2] = NaN;
            // NaN * NegativeZero = NaN
            SR[0, 1, 0, 2, 0, 0, 0] = NaN;
            // NaN * NegativeZero = NaN
            SR[0, 1, 0, 2, 0, 0, 1] = NaN;
            // NaN * NegativeZero = NaN
            SR[0, 1, 0, 2, 0, 0, 2] = NaN;
            // NaN * NaN = NaN
            SR[0, 1, 0, 2, 0, 1, 0] = NaN;
            // NaN * Finite = NaN
            SR[0, 1, 0, 2, 0, 1, 1] = NaN;
            // NaN * NegativeInfinity = NaN
            SR[0, 1, 0, 2, 0, 1, 2] = NaN;
            // NaN * PositiveZero = NaN
            SR[0, 1, 0, 2, 1, 0, 0] = NaN;
            // NaN * PositiveZero = NaN
            SR[0, 1, 0, 2, 1, 0, 1] = NaN;
            // NaN * PositiveZero = NaN
            SR[0, 1, 0, 2, 1, 0, 2] = NaN;
            // NaN * NaN = NaN
            SR[0, 1, 0, 2, 1, 1, 0] = NaN;
            // NaN * Finite = NaN
            SR[0, 1, 0, 2, 1, 1, 1] = NaN;
            // NaN * PositiveInfinity = NaN
            SR[0, 1, 0, 2, 1, 1, 2] = NaN;
            // NaN / NegativeZero = NaN
            SR[0, 1, 0, 3, 0, 0, 0] = NaN;
            // NaN / NegativeZero = NaN
            SR[0, 1, 0, 3, 0, 0, 1] = NaN;
            // NaN / NegativeZero = NaN
            SR[0, 1, 0, 3, 0, 0, 2] = NaN;
            // NaN / NaN = NaN
            SR[0, 1, 0, 3, 0, 1, 0] = NaN;
            // NaN / Finite = NaN
            SR[0, 1, 0, 3, 0, 1, 1] = NaN;
            // NaN / NegativeInfinity = NaN
            SR[0, 1, 0, 3, 0, 1, 2] = NaN;
            // NaN / PositiveZero = NaN
            SR[0, 1, 0, 3, 1, 0, 0] = NaN;
            // NaN / PositiveZero = NaN
            SR[0, 1, 0, 3, 1, 0, 1] = NaN;
            // NaN / PositiveZero = NaN
            SR[0, 1, 0, 3, 1, 0, 2] = NaN;
            // NaN / NaN = NaN
            SR[0, 1, 0, 3, 1, 1, 0] = NaN;
            // NaN / Finite = NaN
            SR[0, 1, 0, 3, 1, 1, 1] = NaN;
            // NaN / PositiveInfinity = NaN
            SR[0, 1, 0, 3, 1, 1, 2] = NaN;
            // Finite + NegativeZero = null
            SR[0, 1, 1, 0, 0, 0, 0] = null;
            // Finite + NegativeZero = null
            SR[0, 1, 1, 0, 0, 0, 1] = null;
            // Finite + NegativeZero = null
            SR[0, 1, 1, 0, 0, 0, 2] = null;
            // Finite + NaN = NaN
            SR[0, 1, 1, 0, 0, 1, 0] = NaN;
            // Finite + Finite = null
            SR[0, 1, 1, 0, 0, 1, 1] = null;
            // Finite + NegativeInfinity = NegativeInfinity
            SR[0, 1, 1, 0, 0, 1, 2] = NegativeInfinity;
            // Finite + PositiveZero = null
            SR[0, 1, 1, 0, 1, 0, 0] = null;
            // Finite + PositiveZero = null
            SR[0, 1, 1, 0, 1, 0, 1] = null;
            // Finite + PositiveZero = null
            SR[0, 1, 1, 0, 1, 0, 2] = null;
            // Finite + NaN = NaN
            SR[0, 1, 1, 0, 1, 1, 0] = NaN;
            // Finite + Finite = null
            SR[0, 1, 1, 0, 1, 1, 1] = null;
            // Finite + PositiveInfinity = PositiveInfinity
            SR[0, 1, 1, 0, 1, 1, 2] = PositiveInfinity;
            // Finite - NegativeZero = null
            SR[0, 1, 1, 1, 0, 0, 0] = null;
            // Finite - NegativeZero = null
            SR[0, 1, 1, 1, 0, 0, 1] = null;
            // Finite - NegativeZero = null
            SR[0, 1, 1, 1, 0, 0, 2] = null;
            // Finite - NaN = NaN
            SR[0, 1, 1, 1, 0, 1, 0] = NaN;
            // Finite - Finite = null
            SR[0, 1, 1, 1, 0, 1, 1] = null;
            // Finite - NegativeInfinity = PositiveInfinity
            SR[0, 1, 1, 1, 0, 1, 2] = PositiveInfinity;
            // Finite - PositiveZero = null
            SR[0, 1, 1, 1, 1, 0, 0] = null;
            // Finite - PositiveZero = null
            SR[0, 1, 1, 1, 1, 0, 1] = null;
            // Finite - PositiveZero = null
            SR[0, 1, 1, 1, 1, 0, 2] = null;
            // Finite - NaN = NaN
            SR[0, 1, 1, 1, 1, 1, 0] = NaN;
            // Finite - Finite = null
            SR[0, 1, 1, 1, 1, 1, 1] = null;
            // Finite - PositiveInfinity = NegativeInfinity
            SR[0, 1, 1, 1, 1, 1, 2] = NegativeInfinity;
            // Finite * NegativeZero = PositiveZero
            SR[0, 1, 1, 2, 0, 0, 0] = PositiveZero;
            // Finite * NegativeZero = PositiveZero
            SR[0, 1, 1, 2, 0, 0, 1] = PositiveZero;
            // Finite * NegativeZero = PositiveZero
            SR[0, 1, 1, 2, 0, 0, 2] = PositiveZero;
            // Finite * NaN = NaN
            SR[0, 1, 1, 2, 0, 1, 0] = NaN;
            // Finite * Finite = null
            SR[0, 1, 1, 2, 0, 1, 1] = null;
            // Finite * NegativeInfinity = PositiveInfinity
            SR[0, 1, 1, 2, 0, 1, 2] = PositiveInfinity;
            // Finite * PositiveZero = NegativeZero
            SR[0, 1, 1, 2, 1, 0, 0] = NegativeZero;
            // Finite * PositiveZero = NegativeZero
            SR[0, 1, 1, 2, 1, 0, 1] = NegativeZero;
            // Finite * PositiveZero = NegativeZero
            SR[0, 1, 1, 2, 1, 0, 2] = NegativeZero;
            // Finite * NaN = NaN
            SR[0, 1, 1, 2, 1, 1, 0] = NaN;
            // Finite * Finite = null
            SR[0, 1, 1, 2, 1, 1, 1] = null;
            // Finite * PositiveInfinity = NegativeInfinity
            SR[0, 1, 1, 2, 1, 1, 2] = NegativeInfinity;
            // Finite / NegativeZero = PositiveInfinity
            SR[0, 1, 1, 3, 0, 0, 0] = PositiveInfinity;
            // Finite / NegativeZero = PositiveInfinity
            SR[0, 1, 1, 3, 0, 0, 1] = PositiveInfinity;
            // Finite / NegativeZero = PositiveInfinity
            SR[0, 1, 1, 3, 0, 0, 2] = PositiveInfinity;
            // Finite / NaN = NaN
            SR[0, 1, 1, 3, 0, 1, 0] = NaN;
            // Finite / Finite = null
            SR[0, 1, 1, 3, 0, 1, 1] = null;
            // Finite / NegativeInfinity = PositiveZero
            SR[0, 1, 1, 3, 0, 1, 2] = PositiveZero;
            // Finite / PositiveZero = NegativeInfinity
            SR[0, 1, 1, 3, 1, 0, 0] = NegativeInfinity;
            // Finite / PositiveZero = NegativeInfinity
            SR[0, 1, 1, 3, 1, 0, 1] = NegativeInfinity;
            // Finite / PositiveZero = NegativeInfinity
            SR[0, 1, 1, 3, 1, 0, 2] = NegativeInfinity;
            // Finite / NaN = NaN
            SR[0, 1, 1, 3, 1, 1, 0] = NaN;
            // Finite / Finite = null
            SR[0, 1, 1, 3, 1, 1, 1] = null;
            // Finite / PositiveInfinity = NegativeZero
            SR[0, 1, 1, 3, 1, 1, 2] = NegativeZero;
            // NegativeInfinity + NegativeZero = NegativeInfinity
            SR[0, 1, 2, 0, 0, 0, 0] = NegativeInfinity;
            // NegativeInfinity + NegativeZero = NegativeInfinity
            SR[0, 1, 2, 0, 0, 0, 1] = NegativeInfinity;
            // NegativeInfinity + NegativeZero = NegativeInfinity
            SR[0, 1, 2, 0, 0, 0, 2] = NegativeInfinity;
            // NegativeInfinity + NaN = NaN
            SR[0, 1, 2, 0, 0, 1, 0] = NaN;
            // NegativeInfinity + Finite = NegativeInfinity
            SR[0, 1, 2, 0, 0, 1, 1] = NegativeInfinity;
            // NegativeInfinity + NegativeInfinity = NegativeInfinity
            SR[0, 1, 2, 0, 0, 1, 2] = NegativeInfinity;
            // NegativeInfinity + PositiveZero = NegativeInfinity
            SR[0, 1, 2, 0, 1, 0, 0] = NegativeInfinity;
            // NegativeInfinity + PositiveZero = NegativeInfinity
            SR[0, 1, 2, 0, 1, 0, 1] = NegativeInfinity;
            // NegativeInfinity + PositiveZero = NegativeInfinity
            SR[0, 1, 2, 0, 1, 0, 2] = NegativeInfinity;
            // NegativeInfinity + NaN = NaN
            SR[0, 1, 2, 0, 1, 1, 0] = NaN;
            // NegativeInfinity + Finite = NegativeInfinity
            SR[0, 1, 2, 0, 1, 1, 1] = NegativeInfinity;
            // NegativeInfinity + PositiveInfinity = NaN
            SR[0, 1, 2, 0, 1, 1, 2] = NaN;
            // NegativeInfinity - NegativeZero = NegativeInfinity
            SR[0, 1, 2, 1, 0, 0, 0] = NegativeInfinity;
            // NegativeInfinity - NegativeZero = NegativeInfinity
            SR[0, 1, 2, 1, 0, 0, 1] = NegativeInfinity;
            // NegativeInfinity - NegativeZero = NegativeInfinity
            SR[0, 1, 2, 1, 0, 0, 2] = NegativeInfinity;
            // NegativeInfinity - NaN = NaN
            SR[0, 1, 2, 1, 0, 1, 0] = NaN;
            // NegativeInfinity - Finite = NegativeInfinity
            SR[0, 1, 2, 1, 0, 1, 1] = NegativeInfinity;
            // NegativeInfinity - NegativeInfinity = NaN
            SR[0, 1, 2, 1, 0, 1, 2] = NaN;
            // NegativeInfinity - PositiveZero = NegativeInfinity
            SR[0, 1, 2, 1, 1, 0, 0] = NegativeInfinity;
            // NegativeInfinity - PositiveZero = NegativeInfinity
            SR[0, 1, 2, 1, 1, 0, 1] = NegativeInfinity;
            // NegativeInfinity - PositiveZero = NegativeInfinity
            SR[0, 1, 2, 1, 1, 0, 2] = NegativeInfinity;
            // NegativeInfinity - NaN = NaN
            SR[0, 1, 2, 1, 1, 1, 0] = NaN;
            // NegativeInfinity - Finite = NegativeInfinity
            SR[0, 1, 2, 1, 1, 1, 1] = NegativeInfinity;
            // NegativeInfinity - PositiveInfinity = NegativeInfinity
            SR[0, 1, 2, 1, 1, 1, 2] = NegativeInfinity;
            // NegativeInfinity * NegativeZero = NaN
            SR[0, 1, 2, 2, 0, 0, 0] = NaN;
            // NegativeInfinity * NegativeZero = NaN
            SR[0, 1, 2, 2, 0, 0, 1] = NaN;
            // NegativeInfinity * NegativeZero = NaN
            SR[0, 1, 2, 2, 0, 0, 2] = NaN;
            // NegativeInfinity * NaN = NaN
            SR[0, 1, 2, 2, 0, 1, 0] = NaN;
            // NegativeInfinity * Finite = PositiveInfinity
            SR[0, 1, 2, 2, 0, 1, 1] = PositiveInfinity;
            // NegativeInfinity * NegativeInfinity = PositiveInfinity
            SR[0, 1, 2, 2, 0, 1, 2] = PositiveInfinity;
            // NegativeInfinity * PositiveZero = NaN
            SR[0, 1, 2, 2, 1, 0, 0] = NaN;
            // NegativeInfinity * PositiveZero = NaN
            SR[0, 1, 2, 2, 1, 0, 1] = NaN;
            // NegativeInfinity * PositiveZero = NaN
            SR[0, 1, 2, 2, 1, 0, 2] = NaN;
            // NegativeInfinity * NaN = NaN
            SR[0, 1, 2, 2, 1, 1, 0] = NaN;
            // NegativeInfinity * Finite = NegativeInfinity
            SR[0, 1, 2, 2, 1, 1, 1] = NegativeInfinity;
            // NegativeInfinity * PositiveInfinity = NegativeInfinity
            SR[0, 1, 2, 2, 1, 1, 2] = NegativeInfinity;
            // NegativeInfinity / NegativeZero = PositiveInfinity
            SR[0, 1, 2, 3, 0, 0, 0] = PositiveInfinity;
            // NegativeInfinity / NegativeZero = PositiveInfinity
            SR[0, 1, 2, 3, 0, 0, 1] = PositiveInfinity;
            // NegativeInfinity / NegativeZero = PositiveInfinity
            SR[0, 1, 2, 3, 0, 0, 2] = PositiveInfinity;
            // NegativeInfinity / NaN = NaN
            SR[0, 1, 2, 3, 0, 1, 0] = NaN;
            // NegativeInfinity / Finite = PositiveInfinity
            SR[0, 1, 2, 3, 0, 1, 1] = PositiveInfinity;
            // NegativeInfinity / NegativeInfinity = NaN
            SR[0, 1, 2, 3, 0, 1, 2] = NaN;
            // NegativeInfinity / PositiveZero = NegativeInfinity
            SR[0, 1, 2, 3, 1, 0, 0] = NegativeInfinity;
            // NegativeInfinity / PositiveZero = NegativeInfinity
            SR[0, 1, 2, 3, 1, 0, 1] = NegativeInfinity;
            // NegativeInfinity / PositiveZero = NegativeInfinity
            SR[0, 1, 2, 3, 1, 0, 2] = NegativeInfinity;
            // NegativeInfinity / NaN = NaN
            SR[0, 1, 2, 3, 1, 1, 0] = NaN;
            // NegativeInfinity / Finite = NegativeInfinity
            SR[0, 1, 2, 3, 1, 1, 1] = NegativeInfinity;
            // NegativeInfinity / PositiveInfinity = NaN
            SR[0, 1, 2, 3, 1, 1, 2] = NaN;
            // PositiveZero + NegativeZero = PositiveZero
            SR[1, 0, 0, 0, 0, 0, 0] = PositiveZero;
            // PositiveZero + NegativeZero = PositiveZero
            SR[1, 0, 0, 0, 0, 0, 1] = PositiveZero;
            // PositiveZero + NegativeZero = PositiveZero
            SR[1, 0, 0, 0, 0, 0, 2] = PositiveZero;
            // PositiveZero + NaN = NaN
            SR[1, 0, 0, 0, 0, 1, 0] = NaN;
            // PositiveZero + Finite = null
            SR[1, 0, 0, 0, 0, 1, 1] = null;
            // PositiveZero + NegativeInfinity = NegativeInfinity
            SR[1, 0, 0, 0, 0, 1, 2] = NegativeInfinity;
            // PositiveZero + PositiveZero = PositiveZero
            SR[1, 0, 0, 0, 1, 0, 0] = PositiveZero;
            // PositiveZero + PositiveZero = PositiveZero
            SR[1, 0, 0, 0, 1, 0, 1] = PositiveZero;
            // PositiveZero + PositiveZero = PositiveZero
            SR[1, 0, 0, 0, 1, 0, 2] = PositiveZero;
            // PositiveZero + NaN = NaN
            SR[1, 0, 0, 0, 1, 1, 0] = NaN;
            // PositiveZero + Finite = null
            SR[1, 0, 0, 0, 1, 1, 1] = null;
            // PositiveZero + PositiveInfinity = PositiveInfinity
            SR[1, 0, 0, 0, 1, 1, 2] = PositiveInfinity;
            // PositiveZero - NegativeZero = PositiveZero
            SR[1, 0, 0, 1, 0, 0, 0] = PositiveZero;
            // PositiveZero - NegativeZero = PositiveZero
            SR[1, 0, 0, 1, 0, 0, 1] = PositiveZero;
            // PositiveZero - NegativeZero = PositiveZero
            SR[1, 0, 0, 1, 0, 0, 2] = PositiveZero;
            // PositiveZero - NaN = NaN
            SR[1, 0, 0, 1, 0, 1, 0] = NaN;
            // PositiveZero - Finite = null
            SR[1, 0, 0, 1, 0, 1, 1] = null;
            // PositiveZero - NegativeInfinity = PositiveInfinity
            SR[1, 0, 0, 1, 0, 1, 2] = PositiveInfinity;
            // PositiveZero - PositiveZero = PositiveZero
            SR[1, 0, 0, 1, 1, 0, 0] = PositiveZero;
            // PositiveZero - PositiveZero = PositiveZero
            SR[1, 0, 0, 1, 1, 0, 1] = PositiveZero;
            // PositiveZero - PositiveZero = PositiveZero
            SR[1, 0, 0, 1, 1, 0, 2] = PositiveZero;
            // PositiveZero - NaN = NaN
            SR[1, 0, 0, 1, 1, 1, 0] = NaN;
            // PositiveZero - Finite = null
            SR[1, 0, 0, 1, 1, 1, 1] = null;
            // PositiveZero - PositiveInfinity = NegativeInfinity
            SR[1, 0, 0, 1, 1, 1, 2] = NegativeInfinity;
            // PositiveZero * NegativeZero = NegativeZero
            SR[1, 0, 0, 2, 0, 0, 0] = NegativeZero;
            // PositiveZero * NegativeZero = NegativeZero
            SR[1, 0, 0, 2, 0, 0, 1] = NegativeZero;
            // PositiveZero * NegativeZero = NegativeZero
            SR[1, 0, 0, 2, 0, 0, 2] = NegativeZero;
            // PositiveZero * NaN = NaN
            SR[1, 0, 0, 2, 0, 1, 0] = NaN;
            // PositiveZero * Finite = NegativeZero
            SR[1, 0, 0, 2, 0, 1, 1] = NegativeZero;
            // PositiveZero * NegativeInfinity = NaN
            SR[1, 0, 0, 2, 0, 1, 2] = NaN;
            // PositiveZero * PositiveZero = PositiveZero
            SR[1, 0, 0, 2, 1, 0, 0] = PositiveZero;
            // PositiveZero * PositiveZero = PositiveZero
            SR[1, 0, 0, 2, 1, 0, 1] = PositiveZero;
            // PositiveZero * PositiveZero = PositiveZero
            SR[1, 0, 0, 2, 1, 0, 2] = PositiveZero;
            // PositiveZero * NaN = NaN
            SR[1, 0, 0, 2, 1, 1, 0] = NaN;
            // PositiveZero * Finite = PositiveZero
            SR[1, 0, 0, 2, 1, 1, 1] = PositiveZero;
            // PositiveZero * PositiveInfinity = NaN
            SR[1, 0, 0, 2, 1, 1, 2] = NaN;
            // PositiveZero / NegativeZero = NaN
            SR[1, 0, 0, 3, 0, 0, 0] = NaN;
            // PositiveZero / NegativeZero = NaN
            SR[1, 0, 0, 3, 0, 0, 1] = NaN;
            // PositiveZero / NegativeZero = NaN
            SR[1, 0, 0, 3, 0, 0, 2] = NaN;
            // PositiveZero / NaN = NaN
            SR[1, 0, 0, 3, 0, 1, 0] = NaN;
            // PositiveZero / Finite = NegativeZero
            SR[1, 0, 0, 3, 0, 1, 1] = NegativeZero;
            // PositiveZero / NegativeInfinity = NegativeZero
            SR[1, 0, 0, 3, 0, 1, 2] = NegativeZero;
            // PositiveZero / PositiveZero = NaN
            SR[1, 0, 0, 3, 1, 0, 0] = NaN;
            // PositiveZero / PositiveZero = NaN
            SR[1, 0, 0, 3, 1, 0, 1] = NaN;
            // PositiveZero / PositiveZero = NaN
            SR[1, 0, 0, 3, 1, 0, 2] = NaN;
            // PositiveZero / NaN = NaN
            SR[1, 0, 0, 3, 1, 1, 0] = NaN;
            // PositiveZero / Finite = PositiveZero
            SR[1, 0, 0, 3, 1, 1, 1] = PositiveZero;
            // PositiveZero / PositiveInfinity = PositiveZero
            SR[1, 0, 0, 3, 1, 1, 2] = PositiveZero;
            // PositiveZero + NegativeZero = PositiveZero
            SR[1, 0, 1, 0, 0, 0, 0] = PositiveZero;
            // PositiveZero + NegativeZero = PositiveZero
            SR[1, 0, 1, 0, 0, 0, 1] = PositiveZero;
            // PositiveZero + NegativeZero = PositiveZero
            SR[1, 0, 1, 0, 0, 0, 2] = PositiveZero;
            // PositiveZero + NaN = NaN
            SR[1, 0, 1, 0, 0, 1, 0] = NaN;
            // PositiveZero + Finite = null
            SR[1, 0, 1, 0, 0, 1, 1] = null;
            // PositiveZero + NegativeInfinity = NegativeInfinity
            SR[1, 0, 1, 0, 0, 1, 2] = NegativeInfinity;
            // PositiveZero + PositiveZero = PositiveZero
            SR[1, 0, 1, 0, 1, 0, 0] = PositiveZero;
            // PositiveZero + PositiveZero = PositiveZero
            SR[1, 0, 1, 0, 1, 0, 1] = PositiveZero;
            // PositiveZero + PositiveZero = PositiveZero
            SR[1, 0, 1, 0, 1, 0, 2] = PositiveZero;
            // PositiveZero + NaN = NaN
            SR[1, 0, 1, 0, 1, 1, 0] = NaN;
            // PositiveZero + Finite = null
            SR[1, 0, 1, 0, 1, 1, 1] = null;
            // PositiveZero + PositiveInfinity = PositiveInfinity
            SR[1, 0, 1, 0, 1, 1, 2] = PositiveInfinity;
            // PositiveZero - NegativeZero = PositiveZero
            SR[1, 0, 1, 1, 0, 0, 0] = PositiveZero;
            // PositiveZero - NegativeZero = PositiveZero
            SR[1, 0, 1, 1, 0, 0, 1] = PositiveZero;
            // PositiveZero - NegativeZero = PositiveZero
            SR[1, 0, 1, 1, 0, 0, 2] = PositiveZero;
            // PositiveZero - NaN = NaN
            SR[1, 0, 1, 1, 0, 1, 0] = NaN;
            // PositiveZero - Finite = null
            SR[1, 0, 1, 1, 0, 1, 1] = null;
            // PositiveZero - NegativeInfinity = PositiveInfinity
            SR[1, 0, 1, 1, 0, 1, 2] = PositiveInfinity;
            // PositiveZero - PositiveZero = PositiveZero
            SR[1, 0, 1, 1, 1, 0, 0] = PositiveZero;
            // PositiveZero - PositiveZero = PositiveZero
            SR[1, 0, 1, 1, 1, 0, 1] = PositiveZero;
            // PositiveZero - PositiveZero = PositiveZero
            SR[1, 0, 1, 1, 1, 0, 2] = PositiveZero;
            // PositiveZero - NaN = NaN
            SR[1, 0, 1, 1, 1, 1, 0] = NaN;
            // PositiveZero - Finite = null
            SR[1, 0, 1, 1, 1, 1, 1] = null;
            // PositiveZero - PositiveInfinity = NegativeInfinity
            SR[1, 0, 1, 1, 1, 1, 2] = NegativeInfinity;
            // PositiveZero * NegativeZero = NegativeZero
            SR[1, 0, 1, 2, 0, 0, 0] = NegativeZero;
            // PositiveZero * NegativeZero = NegativeZero
            SR[1, 0, 1, 2, 0, 0, 1] = NegativeZero;
            // PositiveZero * NegativeZero = NegativeZero
            SR[1, 0, 1, 2, 0, 0, 2] = NegativeZero;
            // PositiveZero * NaN = NaN
            SR[1, 0, 1, 2, 0, 1, 0] = NaN;
            // PositiveZero * Finite = NegativeZero
            SR[1, 0, 1, 2, 0, 1, 1] = NegativeZero;
            // PositiveZero * NegativeInfinity = NaN
            SR[1, 0, 1, 2, 0, 1, 2] = NaN;
            // PositiveZero * PositiveZero = PositiveZero
            SR[1, 0, 1, 2, 1, 0, 0] = PositiveZero;
            // PositiveZero * PositiveZero = PositiveZero
            SR[1, 0, 1, 2, 1, 0, 1] = PositiveZero;
            // PositiveZero * PositiveZero = PositiveZero
            SR[1, 0, 1, 2, 1, 0, 2] = PositiveZero;
            // PositiveZero * NaN = NaN
            SR[1, 0, 1, 2, 1, 1, 0] = NaN;
            // PositiveZero * Finite = PositiveZero
            SR[1, 0, 1, 2, 1, 1, 1] = PositiveZero;
            // PositiveZero * PositiveInfinity = NaN
            SR[1, 0, 1, 2, 1, 1, 2] = NaN;
            // PositiveZero / NegativeZero = NaN
            SR[1, 0, 1, 3, 0, 0, 0] = NaN;
            // PositiveZero / NegativeZero = NaN
            SR[1, 0, 1, 3, 0, 0, 1] = NaN;
            // PositiveZero / NegativeZero = NaN
            SR[1, 0, 1, 3, 0, 0, 2] = NaN;
            // PositiveZero / NaN = NaN
            SR[1, 0, 1, 3, 0, 1, 0] = NaN;
            // PositiveZero / Finite = NegativeZero
            SR[1, 0, 1, 3, 0, 1, 1] = NegativeZero;
            // PositiveZero / NegativeInfinity = NegativeZero
            SR[1, 0, 1, 3, 0, 1, 2] = NegativeZero;
            // PositiveZero / PositiveZero = NaN
            SR[1, 0, 1, 3, 1, 0, 0] = NaN;
            // PositiveZero / PositiveZero = NaN
            SR[1, 0, 1, 3, 1, 0, 1] = NaN;
            // PositiveZero / PositiveZero = NaN
            SR[1, 0, 1, 3, 1, 0, 2] = NaN;
            // PositiveZero / NaN = NaN
            SR[1, 0, 1, 3, 1, 1, 0] = NaN;
            // PositiveZero / Finite = PositiveZero
            SR[1, 0, 1, 3, 1, 1, 1] = PositiveZero;
            // PositiveZero / PositiveInfinity = PositiveZero
            SR[1, 0, 1, 3, 1, 1, 2] = PositiveZero;
            // PositiveZero + NegativeZero = PositiveZero
            SR[1, 0, 2, 0, 0, 0, 0] = PositiveZero;
            // PositiveZero + NegativeZero = PositiveZero
            SR[1, 0, 2, 0, 0, 0, 1] = PositiveZero;
            // PositiveZero + NegativeZero = PositiveZero
            SR[1, 0, 2, 0, 0, 0, 2] = PositiveZero;
            // PositiveZero + NaN = NaN
            SR[1, 0, 2, 0, 0, 1, 0] = NaN;
            // PositiveZero + Finite = null
            SR[1, 0, 2, 0, 0, 1, 1] = null;
            // PositiveZero + NegativeInfinity = NegativeInfinity
            SR[1, 0, 2, 0, 0, 1, 2] = NegativeInfinity;
            // PositiveZero + PositiveZero = PositiveZero
            SR[1, 0, 2, 0, 1, 0, 0] = PositiveZero;
            // PositiveZero + PositiveZero = PositiveZero
            SR[1, 0, 2, 0, 1, 0, 1] = PositiveZero;
            // PositiveZero + PositiveZero = PositiveZero
            SR[1, 0, 2, 0, 1, 0, 2] = PositiveZero;
            // PositiveZero + NaN = NaN
            SR[1, 0, 2, 0, 1, 1, 0] = NaN;
            // PositiveZero + Finite = null
            SR[1, 0, 2, 0, 1, 1, 1] = null;
            // PositiveZero + PositiveInfinity = PositiveInfinity
            SR[1, 0, 2, 0, 1, 1, 2] = PositiveInfinity;
            // PositiveZero - NegativeZero = PositiveZero
            SR[1, 0, 2, 1, 0, 0, 0] = PositiveZero;
            // PositiveZero - NegativeZero = PositiveZero
            SR[1, 0, 2, 1, 0, 0, 1] = PositiveZero;
            // PositiveZero - NegativeZero = PositiveZero
            SR[1, 0, 2, 1, 0, 0, 2] = PositiveZero;
            // PositiveZero - NaN = NaN
            SR[1, 0, 2, 1, 0, 1, 0] = NaN;
            // PositiveZero - Finite = null
            SR[1, 0, 2, 1, 0, 1, 1] = null;
            // PositiveZero - NegativeInfinity = PositiveInfinity
            SR[1, 0, 2, 1, 0, 1, 2] = PositiveInfinity;
            // PositiveZero - PositiveZero = PositiveZero
            SR[1, 0, 2, 1, 1, 0, 0] = PositiveZero;
            // PositiveZero - PositiveZero = PositiveZero
            SR[1, 0, 2, 1, 1, 0, 1] = PositiveZero;
            // PositiveZero - PositiveZero = PositiveZero
            SR[1, 0, 2, 1, 1, 0, 2] = PositiveZero;
            // PositiveZero - NaN = NaN
            SR[1, 0, 2, 1, 1, 1, 0] = NaN;
            // PositiveZero - Finite = null
            SR[1, 0, 2, 1, 1, 1, 1] = null;
            // PositiveZero - PositiveInfinity = NegativeInfinity
            SR[1, 0, 2, 1, 1, 1, 2] = NegativeInfinity;
            // PositiveZero * NegativeZero = NegativeZero
            SR[1, 0, 2, 2, 0, 0, 0] = NegativeZero;
            // PositiveZero * NegativeZero = NegativeZero
            SR[1, 0, 2, 2, 0, 0, 1] = NegativeZero;
            // PositiveZero * NegativeZero = NegativeZero
            SR[1, 0, 2, 2, 0, 0, 2] = NegativeZero;
            // PositiveZero * NaN = NaN
            SR[1, 0, 2, 2, 0, 1, 0] = NaN;
            // PositiveZero * Finite = NegativeZero
            SR[1, 0, 2, 2, 0, 1, 1] = NegativeZero;
            // PositiveZero * NegativeInfinity = NaN
            SR[1, 0, 2, 2, 0, 1, 2] = NaN;
            // PositiveZero * PositiveZero = PositiveZero
            SR[1, 0, 2, 2, 1, 0, 0] = PositiveZero;
            // PositiveZero * PositiveZero = PositiveZero
            SR[1, 0, 2, 2, 1, 0, 1] = PositiveZero;
            // PositiveZero * PositiveZero = PositiveZero
            SR[1, 0, 2, 2, 1, 0, 2] = PositiveZero;
            // PositiveZero * NaN = NaN
            SR[1, 0, 2, 2, 1, 1, 0] = NaN;
            // PositiveZero * Finite = PositiveZero
            SR[1, 0, 2, 2, 1, 1, 1] = PositiveZero;
            // PositiveZero * PositiveInfinity = NaN
            SR[1, 0, 2, 2, 1, 1, 2] = NaN;
            // PositiveZero / NegativeZero = NaN
            SR[1, 0, 2, 3, 0, 0, 0] = NaN;
            // PositiveZero / NegativeZero = NaN
            SR[1, 0, 2, 3, 0, 0, 1] = NaN;
            // PositiveZero / NegativeZero = NaN
            SR[1, 0, 2, 3, 0, 0, 2] = NaN;
            // PositiveZero / NaN = NaN
            SR[1, 0, 2, 3, 0, 1, 0] = NaN;
            // PositiveZero / Finite = NegativeZero
            SR[1, 0, 2, 3, 0, 1, 1] = NegativeZero;
            // PositiveZero / NegativeInfinity = NegativeZero
            SR[1, 0, 2, 3, 0, 1, 2] = NegativeZero;
            // PositiveZero / PositiveZero = NaN
            SR[1, 0, 2, 3, 1, 0, 0] = NaN;
            // PositiveZero / PositiveZero = NaN
            SR[1, 0, 2, 3, 1, 0, 1] = NaN;
            // PositiveZero / PositiveZero = NaN
            SR[1, 0, 2, 3, 1, 0, 2] = NaN;
            // PositiveZero / NaN = NaN
            SR[1, 0, 2, 3, 1, 1, 0] = NaN;
            // PositiveZero / Finite = PositiveZero
            SR[1, 0, 2, 3, 1, 1, 1] = PositiveZero;
            // PositiveZero / PositiveInfinity = PositiveZero
            SR[1, 0, 2, 3, 1, 1, 2] = PositiveZero;
            // NaN + NegativeZero = NaN
            SR[1, 1, 0, 0, 0, 0, 0] = NaN;
            // NaN + NegativeZero = NaN
            SR[1, 1, 0, 0, 0, 0, 1] = NaN;
            // NaN + NegativeZero = NaN
            SR[1, 1, 0, 0, 0, 0, 2] = NaN;
            // NaN + NaN = NaN
            SR[1, 1, 0, 0, 0, 1, 0] = NaN;
            // NaN + Finite = NaN
            SR[1, 1, 0, 0, 0, 1, 1] = NaN;
            // NaN + NegativeInfinity = NaN
            SR[1, 1, 0, 0, 0, 1, 2] = NaN;
            // NaN + PositiveZero = NaN
            SR[1, 1, 0, 0, 1, 0, 0] = NaN;
            // NaN + PositiveZero = NaN
            SR[1, 1, 0, 0, 1, 0, 1] = NaN;
            // NaN + PositiveZero = NaN
            SR[1, 1, 0, 0, 1, 0, 2] = NaN;
            // NaN + NaN = NaN
            SR[1, 1, 0, 0, 1, 1, 0] = NaN;
            // NaN + Finite = NaN
            SR[1, 1, 0, 0, 1, 1, 1] = NaN;
            // NaN + PositiveInfinity = NaN
            SR[1, 1, 0, 0, 1, 1, 2] = NaN;
            // NaN - NegativeZero = NaN
            SR[1, 1, 0, 1, 0, 0, 0] = NaN;
            // NaN - NegativeZero = NaN
            SR[1, 1, 0, 1, 0, 0, 1] = NaN;
            // NaN - NegativeZero = NaN
            SR[1, 1, 0, 1, 0, 0, 2] = NaN;
            // NaN - NaN = NaN
            SR[1, 1, 0, 1, 0, 1, 0] = NaN;
            // NaN - Finite = NaN
            SR[1, 1, 0, 1, 0, 1, 1] = NaN;
            // NaN - NegativeInfinity = NaN
            SR[1, 1, 0, 1, 0, 1, 2] = NaN;
            // NaN - PositiveZero = NaN
            SR[1, 1, 0, 1, 1, 0, 0] = NaN;
            // NaN - PositiveZero = NaN
            SR[1, 1, 0, 1, 1, 0, 1] = NaN;
            // NaN - PositiveZero = NaN
            SR[1, 1, 0, 1, 1, 0, 2] = NaN;
            // NaN - NaN = NaN
            SR[1, 1, 0, 1, 1, 1, 0] = NaN;
            // NaN - Finite = NaN
            SR[1, 1, 0, 1, 1, 1, 1] = NaN;
            // NaN - PositiveInfinity = NaN
            SR[1, 1, 0, 1, 1, 1, 2] = NaN;
            // NaN * NegativeZero = NaN
            SR[1, 1, 0, 2, 0, 0, 0] = NaN;
            // NaN * NegativeZero = NaN
            SR[1, 1, 0, 2, 0, 0, 1] = NaN;
            // NaN * NegativeZero = NaN
            SR[1, 1, 0, 2, 0, 0, 2] = NaN;
            // NaN * NaN = NaN
            SR[1, 1, 0, 2, 0, 1, 0] = NaN;
            // NaN * Finite = NaN
            SR[1, 1, 0, 2, 0, 1, 1] = NaN;
            // NaN * NegativeInfinity = NaN
            SR[1, 1, 0, 2, 0, 1, 2] = NaN;
            // NaN * PositiveZero = NaN
            SR[1, 1, 0, 2, 1, 0, 0] = NaN;
            // NaN * PositiveZero = NaN
            SR[1, 1, 0, 2, 1, 0, 1] = NaN;
            // NaN * PositiveZero = NaN
            SR[1, 1, 0, 2, 1, 0, 2] = NaN;
            // NaN * NaN = NaN
            SR[1, 1, 0, 2, 1, 1, 0] = NaN;
            // NaN * Finite = NaN
            SR[1, 1, 0, 2, 1, 1, 1] = NaN;
            // NaN * PositiveInfinity = NaN
            SR[1, 1, 0, 2, 1, 1, 2] = NaN;
            // NaN / NegativeZero = NaN
            SR[1, 1, 0, 3, 0, 0, 0] = NaN;
            // NaN / NegativeZero = NaN
            SR[1, 1, 0, 3, 0, 0, 1] = NaN;
            // NaN / NegativeZero = NaN
            SR[1, 1, 0, 3, 0, 0, 2] = NaN;
            // NaN / NaN = NaN
            SR[1, 1, 0, 3, 0, 1, 0] = NaN;
            // NaN / Finite = NaN
            SR[1, 1, 0, 3, 0, 1, 1] = NaN;
            // NaN / NegativeInfinity = NaN
            SR[1, 1, 0, 3, 0, 1, 2] = NaN;
            // NaN / PositiveZero = NaN
            SR[1, 1, 0, 3, 1, 0, 0] = NaN;
            // NaN / PositiveZero = NaN
            SR[1, 1, 0, 3, 1, 0, 1] = NaN;
            // NaN / PositiveZero = NaN
            SR[1, 1, 0, 3, 1, 0, 2] = NaN;
            // NaN / NaN = NaN
            SR[1, 1, 0, 3, 1, 1, 0] = NaN;
            // NaN / Finite = NaN
            SR[1, 1, 0, 3, 1, 1, 1] = NaN;
            // NaN / PositiveInfinity = NaN
            SR[1, 1, 0, 3, 1, 1, 2] = NaN;
            // Finite + NegativeZero = null
            SR[1, 1, 1, 0, 0, 0, 0] = null;
            // Finite + NegativeZero = null
            SR[1, 1, 1, 0, 0, 0, 1] = null;
            // Finite + NegativeZero = null
            SR[1, 1, 1, 0, 0, 0, 2] = null;
            // Finite + NaN = NaN
            SR[1, 1, 1, 0, 0, 1, 0] = NaN;
            // Finite + Finite = null
            SR[1, 1, 1, 0, 0, 1, 1] = null;
            // Finite + NegativeInfinity = NegativeInfinity
            SR[1, 1, 1, 0, 0, 1, 2] = NegativeInfinity;
            // Finite + PositiveZero = null
            SR[1, 1, 1, 0, 1, 0, 0] = null;
            // Finite + PositiveZero = null
            SR[1, 1, 1, 0, 1, 0, 1] = null;
            // Finite + PositiveZero = null
            SR[1, 1, 1, 0, 1, 0, 2] = null;
            // Finite + NaN = NaN
            SR[1, 1, 1, 0, 1, 1, 0] = NaN;
            // Finite + Finite = null
            SR[1, 1, 1, 0, 1, 1, 1] = null;
            // Finite + PositiveInfinity = PositiveInfinity
            SR[1, 1, 1, 0, 1, 1, 2] = PositiveInfinity;
            // Finite - NegativeZero = null
            SR[1, 1, 1, 1, 0, 0, 0] = null;
            // Finite - NegativeZero = null
            SR[1, 1, 1, 1, 0, 0, 1] = null;
            // Finite - NegativeZero = null
            SR[1, 1, 1, 1, 0, 0, 2] = null;
            // Finite - NaN = NaN
            SR[1, 1, 1, 1, 0, 1, 0] = NaN;
            // Finite - Finite = null
            SR[1, 1, 1, 1, 0, 1, 1] = null;
            // Finite - NegativeInfinity = PositiveInfinity
            SR[1, 1, 1, 1, 0, 1, 2] = PositiveInfinity;
            // Finite - PositiveZero = null
            SR[1, 1, 1, 1, 1, 0, 0] = null;
            // Finite - PositiveZero = null
            SR[1, 1, 1, 1, 1, 0, 1] = null;
            // Finite - PositiveZero = null
            SR[1, 1, 1, 1, 1, 0, 2] = null;
            // Finite - NaN = NaN
            SR[1, 1, 1, 1, 1, 1, 0] = NaN;
            // Finite - Finite = null
            SR[1, 1, 1, 1, 1, 1, 1] = null;
            // Finite - PositiveInfinity = NegativeInfinity
            SR[1, 1, 1, 1, 1, 1, 2] = NegativeInfinity;
            // Finite * NegativeZero = NegativeZero
            SR[1, 1, 1, 2, 0, 0, 0] = NegativeZero;
            // Finite * NegativeZero = NegativeZero
            SR[1, 1, 1, 2, 0, 0, 1] = NegativeZero;
            // Finite * NegativeZero = NegativeZero
            SR[1, 1, 1, 2, 0, 0, 2] = NegativeZero;
            // Finite * NaN = NaN
            SR[1, 1, 1, 2, 0, 1, 0] = NaN;
            // Finite * Finite = null
            SR[1, 1, 1, 2, 0, 1, 1] = null;
            // Finite * NegativeInfinity = NegativeInfinity
            SR[1, 1, 1, 2, 0, 1, 2] = NegativeInfinity;
            // Finite * PositiveZero = PositiveZero
            SR[1, 1, 1, 2, 1, 0, 0] = PositiveZero;
            // Finite * PositiveZero = PositiveZero
            SR[1, 1, 1, 2, 1, 0, 1] = PositiveZero;
            // Finite * PositiveZero = PositiveZero
            SR[1, 1, 1, 2, 1, 0, 2] = PositiveZero;
            // Finite * NaN = NaN
            SR[1, 1, 1, 2, 1, 1, 0] = NaN;
            // Finite * Finite = null
            SR[1, 1, 1, 2, 1, 1, 1] = null;
            // Finite * PositiveInfinity = PositiveInfinity
            SR[1, 1, 1, 2, 1, 1, 2] = PositiveInfinity;
            // Finite / NegativeZero = NegativeInfinity
            SR[1, 1, 1, 3, 0, 0, 0] = NegativeInfinity;
            // Finite / NegativeZero = NegativeInfinity
            SR[1, 1, 1, 3, 0, 0, 1] = NegativeInfinity;
            // Finite / NegativeZero = NegativeInfinity
            SR[1, 1, 1, 3, 0, 0, 2] = NegativeInfinity;
            // Finite / NaN = NaN
            SR[1, 1, 1, 3, 0, 1, 0] = NaN;
            // Finite / Finite = null
            SR[1, 1, 1, 3, 0, 1, 1] = null;
            // Finite / NegativeInfinity = NegativeZero
            SR[1, 1, 1, 3, 0, 1, 2] = NegativeZero;
            // Finite / PositiveZero = PositiveInfinity
            SR[1, 1, 1, 3, 1, 0, 0] = PositiveInfinity;
            // Finite / PositiveZero = PositiveInfinity
            SR[1, 1, 1, 3, 1, 0, 1] = PositiveInfinity;
            // Finite / PositiveZero = PositiveInfinity
            SR[1, 1, 1, 3, 1, 0, 2] = PositiveInfinity;
            // Finite / NaN = NaN
            SR[1, 1, 1, 3, 1, 1, 0] = NaN;
            // Finite / Finite = null
            SR[1, 1, 1, 3, 1, 1, 1] = null;
            // Finite / PositiveInfinity = PositiveZero
            SR[1, 1, 1, 3, 1, 1, 2] = PositiveZero;
            // PositiveInfinity + NegativeZero = PositiveInfinity
            SR[1, 1, 2, 0, 0, 0, 0] = PositiveInfinity;
            // PositiveInfinity + NegativeZero = PositiveInfinity
            SR[1, 1, 2, 0, 0, 0, 1] = PositiveInfinity;
            // PositiveInfinity + NegativeZero = PositiveInfinity
            SR[1, 1, 2, 0, 0, 0, 2] = PositiveInfinity;
            // PositiveInfinity + NaN = NaN
            SR[1, 1, 2, 0, 0, 1, 0] = NaN;
            // PositiveInfinity + Finite = PositiveInfinity
            SR[1, 1, 2, 0, 0, 1, 1] = PositiveInfinity;
            // PositiveInfinity + NegativeInfinity = NaN
            SR[1, 1, 2, 0, 0, 1, 2] = NaN;
            // PositiveInfinity + PositiveZero = PositiveInfinity
            SR[1, 1, 2, 0, 1, 0, 0] = PositiveInfinity;
            // PositiveInfinity + PositiveZero = PositiveInfinity
            SR[1, 1, 2, 0, 1, 0, 1] = PositiveInfinity;
            // PositiveInfinity + PositiveZero = PositiveInfinity
            SR[1, 1, 2, 0, 1, 0, 2] = PositiveInfinity;
            // PositiveInfinity + NaN = NaN
            SR[1, 1, 2, 0, 1, 1, 0] = NaN;
            // PositiveInfinity + Finite = PositiveInfinity
            SR[1, 1, 2, 0, 1, 1, 1] = PositiveInfinity;
            // PositiveInfinity + PositiveInfinity = PositiveInfinity
            SR[1, 1, 2, 0, 1, 1, 2] = PositiveInfinity;
            // PositiveInfinity - NegativeZero = PositiveInfinity
            SR[1, 1, 2, 1, 0, 0, 0] = PositiveInfinity;
            // PositiveInfinity - NegativeZero = PositiveInfinity
            SR[1, 1, 2, 1, 0, 0, 1] = PositiveInfinity;
            // PositiveInfinity - NegativeZero = PositiveInfinity
            SR[1, 1, 2, 1, 0, 0, 2] = PositiveInfinity;
            // PositiveInfinity - NaN = NaN
            SR[1, 1, 2, 1, 0, 1, 0] = NaN;
            // PositiveInfinity - Finite = PositiveInfinity
            SR[1, 1, 2, 1, 0, 1, 1] = PositiveInfinity;
            // PositiveInfinity - NegativeInfinity = PositiveInfinity
            SR[1, 1, 2, 1, 0, 1, 2] = PositiveInfinity;
            // PositiveInfinity - PositiveZero = PositiveInfinity
            SR[1, 1, 2, 1, 1, 0, 0] = PositiveInfinity;
            // PositiveInfinity - PositiveZero = PositiveInfinity
            SR[1, 1, 2, 1, 1, 0, 1] = PositiveInfinity;
            // PositiveInfinity - PositiveZero = PositiveInfinity
            SR[1, 1, 2, 1, 1, 0, 2] = PositiveInfinity;
            // PositiveInfinity - NaN = NaN
            SR[1, 1, 2, 1, 1, 1, 0] = NaN;
            // PositiveInfinity - Finite = PositiveInfinity
            SR[1, 1, 2, 1, 1, 1, 1] = PositiveInfinity;
            // PositiveInfinity - PositiveInfinity = NaN
            SR[1, 1, 2, 1, 1, 1, 2] = NaN;
            // PositiveInfinity * NegativeZero = NaN
            SR[1, 1, 2, 2, 0, 0, 0] = NaN;
            // PositiveInfinity * NegativeZero = NaN
            SR[1, 1, 2, 2, 0, 0, 1] = NaN;
            // PositiveInfinity * NegativeZero = NaN
            SR[1, 1, 2, 2, 0, 0, 2] = NaN;
            // PositiveInfinity * NaN = NaN
            SR[1, 1, 2, 2, 0, 1, 0] = NaN;
            // PositiveInfinity * Finite = NegativeInfinity
            SR[1, 1, 2, 2, 0, 1, 1] = NegativeInfinity;
            // PositiveInfinity * NegativeInfinity = NegativeInfinity
            SR[1, 1, 2, 2, 0, 1, 2] = NegativeInfinity;
            // PositiveInfinity * PositiveZero = NaN
            SR[1, 1, 2, 2, 1, 0, 0] = NaN;
            // PositiveInfinity * PositiveZero = NaN
            SR[1, 1, 2, 2, 1, 0, 1] = NaN;
            // PositiveInfinity * PositiveZero = NaN
            SR[1, 1, 2, 2, 1, 0, 2] = NaN;
            // PositiveInfinity * NaN = NaN
            SR[1, 1, 2, 2, 1, 1, 0] = NaN;
            // PositiveInfinity * Finite = PositiveInfinity
            SR[1, 1, 2, 2, 1, 1, 1] = PositiveInfinity;
            // PositiveInfinity * PositiveInfinity = PositiveInfinity
            SR[1, 1, 2, 2, 1, 1, 2] = PositiveInfinity;
            // PositiveInfinity / NegativeZero = NegativeInfinity
            SR[1, 1, 2, 3, 0, 0, 0] = NegativeInfinity;
            // PositiveInfinity / NegativeZero = NegativeInfinity
            SR[1, 1, 2, 3, 0, 0, 1] = NegativeInfinity;
            // PositiveInfinity / NegativeZero = NegativeInfinity
            SR[1, 1, 2, 3, 0, 0, 2] = NegativeInfinity;
            // PositiveInfinity / NaN = NaN
            SR[1, 1, 2, 3, 0, 1, 0] = NaN;
            // PositiveInfinity / Finite = NegativeInfinity
            SR[1, 1, 2, 3, 0, 1, 1] = NegativeInfinity;
            // PositiveInfinity / NegativeInfinity = NaN
            SR[1, 1, 2, 3, 0, 1, 2] = NaN;
            // PositiveInfinity / PositiveZero = PositiveInfinity
            SR[1, 1, 2, 3, 1, 0, 0] = PositiveInfinity;
            // PositiveInfinity / PositiveZero = PositiveInfinity
            SR[1, 1, 2, 3, 1, 0, 1] = PositiveInfinity;
            // PositiveInfinity / PositiveZero = PositiveInfinity
            SR[1, 1, 2, 3, 1, 0, 2] = PositiveInfinity;
            // PositiveInfinity / NaN = NaN
            SR[1, 1, 2, 3, 1, 1, 0] = NaN;
            // PositiveInfinity / Finite = PositiveInfinity
            SR[1, 1, 2, 3, 1, 1, 1] = PositiveInfinity;
            // PositiveInfinity / PositiveInfinity = NaN
            SR[1, 1, 2, 3, 1, 1, 2] = NaN;
        }
        #endregion

        #region ToString
        public string GetFractionString() {
            if (IsFinite(this)) {
                return this._fraction.ToString();
            } else if (IsInfinite(this)) {
                return InfinityString;
            } else {
                return NaNString;
            }
        }
        public override string ToString() {
            return ToString(0);
        }
        public string ToString(int separateAt) {
            return ToString(separateAt, "", false);
        }
        public string ToString(int separateAt, string plusSign, bool floatStyleFormat) {
            if (separateAt < 0) {
                throw new ArgumentException("argument must be positive");
            }

            if (IsFinite(this) && !IsZero(this)) {
                StringBuilder sb = new StringBuilder();
                if (IsNegative(this)) {
                    sb.Append("-");
                } else {
                    sb.Append(plusSign);
                }
                if (floatStyleFormat) {
                    AppendFloatStyle(sb, separateAt);
                } else {
                    AppendEngineeringStyle(sb, separateAt);
                }
                return sb.ToString();
            }
            if (IsPositiveZero(this)) {
                return plusSign + ZeroString;
            }
            if (IsNegativeZero(this)) {
                return NegativeZeroString;
            }
            if (IsPositiveInfinite(this)) {
                return plusSign + InfinityString;
            }
            if (IsNegativeInfinite(this)) {
                return NegativeInfinityString;
            }
            return NaNString;
        }

        private void AppendFloatStyle(StringBuilder/*!*/ sb, int separateAt) {
            if (_exponent <= 0) {
                sb.Append("0.");
                AppendDigits(sb, new string('0', -_exponent) + _fraction.ToString(), 0, Digits - _exponent, separateAt);
            } else {
                int expLenDiff = _exponent - Digits;
                if (expLenDiff >= 0) {
                    AppendIntegerDigits(sb, _fraction.ToString() + new string('0', expLenDiff), 0, _exponent, separateAt);
                    sb.Append(".0");
                } else {
                    AppendIntegerDigits(sb, _fraction.ToString(), 0, _exponent, separateAt);
                    sb.Append(".");
                    AppendDigits(sb, _fraction.ToString(), _exponent, Digits - _exponent, separateAt);
                }
            }
        }

        private void AppendEngineeringStyle(StringBuilder/*!*/ sb, int separateAt) {
            sb.Append("0.");
            AppendDigits(sb, _fraction.ToString(), 0, Digits, separateAt);
            sb.Append("e");
            sb.AppendFormat("{0}", this._exponent);
        }

        /// <summary>
        /// Groups the fractional digits, which run left to right: the short group, if any,
        /// is the last one ("0.123 456 78").
        /// </summary>
        private void AppendDigits(StringBuilder/*!*/ sb, string digits, int start, int length, int separateAt) {
            int current = start;
            if (separateAt > 0) {
                while (current + separateAt < start + length) {
                    sb.Append(digits.Substring(current, separateAt));
                    current += separateAt;
                    sb.Append(" ");
                }
            }
            sb.Append(digits.Substring(current, length - (current - start)));
        }

        /// <summary>
        /// Groups the digits of the integer part, which are counted from the decimal point
        /// backwards, so the short group is the *first* one: 1000010 with a width of 5 is
        /// "10 00010", not "10000 10".
        /// </summary>
        private void AppendIntegerDigits(StringBuilder/*!*/ sb, string digits, int start, int length, int separateAt) {
            if (separateAt <= 0 || length <= separateAt) {
                sb.Append(digits.Substring(start, length));
                return;
            }
            int firstGroup = length % separateAt;
            int current = start;
            if (firstGroup != 0) {
                sb.Append(digits.Substring(current, firstGroup));
                sb.Append(" ");
                current += firstGroup;
            }
            while (current < start + length) {
                sb.Append(digits.Substring(current, separateAt));
                current += separateAt;
                if (current < start + length) {
                    sb.Append(" ");
                }
            }
        }
        #endregion

        #region Comparison
        #region IEquatable<BigDecimal> Members

        public bool Equals(BigDecimal other) {
            return CompareTo(other) == 0;
        }

        #endregion

        public int? CompareBigDecimal(BigDecimal other) {
            int? result;
            if (other == null) {
                result = null;
            } else if (!IsNonZeroFinite(this) || !IsNonZeroFinite(other)) {
                result = CheckSpecialComparison(this, other);
            } else {
                if (this._sign != other._sign) {
                    // Different signs
                    result = this._sign > other._sign ? 1 : -1;
                } else {
                    result = this._sign * (Fraction.Compare(this._fraction, other._fraction, this._exponent - other._exponent));
                }
            }
            return result;
        }

        #region IComparable<BigDecimal> Members
        public int CompareTo(BigDecimal other) {
            int? result = CompareBigDecimal(other);
            if (result.HasValue) {
                return result.Value;
            } else {
                // Must have NaNs so we map them to Double.CompareTo results
                if (IsNaN(this)) {
                    if (IsNaN(other)) {
                        // Both NaN so 
                        return 0;
                    } else {
                        // this == NaN && other != NaN
                        return -1;
                    }
                } else {
                    return 1;
                }
            }
        }
        #endregion
        #endregion

        #region Hash Code
        public int GetSignCode() {
            switch (this.NumberType) {
                case BigDecimal.NumberTypes.NaN:
                    return 0;
                case BigDecimal.NumberTypes.Infinite:
                    return this.Sign * 3;
                default:
                case BigDecimal.NumberTypes.Finite:
                    if (BigDecimal.IsZero(this)) {
                        return this.Sign;
                    } else {
                        return this.Sign * 2;
                    }
            }
        }
        public override int GetHashCode() {
            int hash = this.GetSignCode();
            // Special values have special hash codes
            if (hash == 2) {
                int fHash = _fraction.GetHashCode();
                hash = (31 * hash + fHash) ^ fHash + Exponent;
            }
            return hash;
        }
        #endregion
    }

}

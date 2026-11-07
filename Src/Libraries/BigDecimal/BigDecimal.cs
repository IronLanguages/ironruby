﻿/* ****************************************************************************
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
using System.Globalization;
using System.Numerics;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.BigDecimal {
    /// <summary>
    /// An arbitrary-precision decimal number, as the bigdecimal 4.0.1 gem defines it: NaN, an infinity,
    /// a signed zero, or sign * 0.d1d2...dn * 10**Exponent, with the digits d1...dn (d1 and dn non-zero)
    /// held as a single BigInteger. MRI keeps the digits in base 10**9 words; nothing here depends on
    /// that, except where MRI's results do (the ROUND_UP quirk of #round and #_dump's digit count),
    /// and those places work the word layout out from the exponent.
    /// </summary>
    public class BigDecimal : IRubyObjectState {

        /// <summary>
        /// BigDecimal.limit, BigDecimal.mode(ROUND_MODE) and the exception flags. MRI keeps them per
        /// thread; they are per RubyContext here.
        /// </summary>
        public sealed class Config {
            public int Limit { get; set; }
            public RoundingModes RoundingMode { get; set; } = RoundingModes.HalfUp;
            public ExceptionModes ExceptionMode { get; set; }

            internal bool Raises(ExceptionModes mode) {
                return (ExceptionMode & mode) != 0;
            }
        }

        public enum RoundingModes {
            Up = 1,
            Down = 2,
            HalfUp = 3,
            HalfDown = 4,
            Ceiling = 5,
            Floor = 6,
            HalfEven = 7
        }

        /// <summary>The EXCEPTION_* flags. OVERFLOW is the same bit as INFINITY, as in MRI.</summary>
        [Flags]
        public enum ExceptionModes {
            None = 0,
            Infinity = 1,
            Overflow = 1,
            NaN = 2,
            Underflow = 4,
            ZeroDivide = 16,
            All = 255
        }

        public enum NumberTypes {
            NaN = 0,
            Finite = 1,
            Infinite = 2
        }

        #region Representation

        private readonly NumberTypes _type;
        private readonly int _sign;
        // Positive and without trailing zeros for a non-zero finite value; zero otherwise.
        private readonly BigInteger _digits;
        private readonly int _digitCount;
        private readonly long _exponent;

        public static readonly BigDecimal NaN = new BigDecimal(NumberTypes.NaN, 1);
        public static readonly BigDecimal PositiveInfinity = new BigDecimal(NumberTypes.Infinite, 1);
        public static readonly BigDecimal NegativeInfinity = new BigDecimal(NumberTypes.Infinite, -1);
        public static readonly BigDecimal PositiveZero = new BigDecimal(NumberTypes.Finite, 1);
        public static readonly BigDecimal NegativeZero = new BigDecimal(NumberTypes.Finite, -1);

        /// <summary>MRI's word: BigDecimal::BASE is 10**9, and #_dump and #round count in its digits.</summary>
        public const int BASE_FIG = 9;
        public const int BASE = 1000000000;

        /// <summary>BIGDECIMAL_DOUBLE_FIGURES</summary>
        public const int DoubleFig = 16;

        // MRI's exponent is a word count in a signed 64-bit value: EXPONENT_MAX and EXPONENT_MIN in digits.
        private const long MaxExponent = long.MaxValue / BASE_FIG * BASE_FIG;
        private const long MinExponent = long.MinValue;

        // A subclass instance (AotSubclasses.Generated.cs) needs a parameterless constructor; nothing can
        // make one, since BigDecimal has no allocator.
        public BigDecimal() : this(NumberTypes.NaN, 1) {
        }

        private BigDecimal(NumberTypes type, int sign) {
            _type = type;
            _sign = sign;
        }

        private BigDecimal(int sign, BigInteger digits, int digitCount, long exponent) {
            Debug.Assert((sign == 1 || sign == -1) && digits.Sign > 0 && digitCount > 0);
            _type = NumberTypes.Finite;
            _sign = sign;
            _digits = digits;
            _digitCount = digitCount;
            _exponent = exponent;
        }

        public int Sign => _sign;
        public bool IsNaN => _type == NumberTypes.NaN;
        public bool IsInfinite => _type == NumberTypes.Infinite;
        public bool IsFinite => _type == NumberTypes.Finite;
        public bool IsZero => _type == NumberTypes.Finite && _digitCount == 0;
        public bool IsNonZeroFinite => _type == NumberTypes.Finite && _digitCount != 0;

        /// <summary>The decimal exponent: 0.1234e3 has 3. Zero and the special values have 0.</summary>
        public long Exponent => _exponent;

        /// <summary>The number of significant digits, without leading or trailing zeros. Zero has none.</summary>
        public int DigitCount => _digitCount;

        /// <summary>
        /// The power of ten the last digit stands for: the value is ±digits * 10**Scale. It is wider than
        /// the exponent, which can be as small as long.MinValue, and so is every scale computed from it.
        /// </summary>
        private Int128 Scale => (Int128)_exponent - _digitCount;

        /// <summary>MRI's #sign: the SIGN_* constants.</summary>
        public int SignCode => _type switch {
            NumberTypes.NaN => 0,
            NumberTypes.Infinite => 3 * _sign,
            _ => (_digitCount == 0 ? 1 : 2) * _sign,
        };

        public static BigDecimal/*!*/ Zero(int sign) => sign < 0 ? NegativeZero : PositiveZero;
        public static BigDecimal/*!*/ Infinity(int sign) => sign < 0 ? NegativeInfinity : PositiveInfinity;

        #endregion

        #region IRubyObjectState

        // A BigDecimal is immutable and, as in MRI, always frozen.
        public bool IsFrozen => true;
        public void Freeze() { }
        public bool IsTainted { get => false; set { } }
        public bool IsUntrusted { get => false; set { } }

        #endregion

        #region Digits and powers of ten

        private static readonly BigInteger[] _powers = CreatePowers(64);
        private static readonly ulong[] _ulongPowers = CreateULongPowers();

        private static BigInteger[] CreatePowers(int count) {
            var result = new BigInteger[count];
            result[0] = BigInteger.One;
            for (int i = 1; i < count; i++) {
                result[i] = result[i - 1] * 10;
            }
            return result;
        }

        private static ulong[] CreateULongPowers() {
            var result = new ulong[20];
            result[0] = 1;
            for (int i = 1; i < result.Length; i++) {
                result[i] = result[i - 1] * 10;
            }
            return result;
        }

        internal static BigInteger Pow10(Int128 n) {
            Debug.Assert(n >= 0);
            if (n < _powers.Length) {
                return _powers[(int)n];
            }
            if (n > Int32.MaxValue) {
                throw new OutOfMemoryException("failed to allocate memory");
            }
            return BigInteger.Pow(10, (int)n);
        }

        /// <summary>The number of decimal digits of a positive value.</summary>
        internal static int CountDigits(BigInteger n) {
            Debug.Assert(n.Sign > 0);
            if (n <= ulong.MaxValue) {
                ulong value = (ulong)n;
                int count = 1;
                while (count < _ulongPowers.Length && value >= _ulongPowers[count]) {
                    count++;
                }
                return count;
            }
            // 2**(bits-1) <= n < 2**bits, so n has this many digits or one more
            long bits = (long)n.GetBitLength();
            int estimate = (int)((bits - 1) * 0.30102999566398119521) + 1;
            if (n >= Pow10(estimate)) {
                return estimate + 1;
            }
            if (n < Pow10(estimate - 1)) {
                return estimate - 1;
            }
            return estimate;
        }

        /// <summary>Removes the trailing zeros of a positive value, adding one to scale for each.</summary>
        private static BigInteger StripTrailingZeros(BigInteger n, ref Int128 scale) {
            if (!n.IsEven) {
                return n;
            }
            if (n <= ulong.MaxValue) {
                ulong value = (ulong)n;
                if (value % 10 != 0) {
                    return n;
                }
                do {
                    value /= 10;
                    scale++;
                } while (value % 10 == 0);
                return value;
            }
            BigInteger remainder;
            while (true) {
                BigInteger quotient = BigInteger.DivRem(n, _powers[16], out remainder);
                if (!remainder.IsZero) {
                    break;
                }
                n = quotient;
                scale += 16;
            }
            while (true) {
                BigInteger quotient = BigInteger.DivRem(n, 10, out remainder);
                if (!remainder.IsZero) {
                    return n;
                }
                n = quotient;
                scale++;
            }
        }

        #endregion

        #region Creation

        /// <summary>
        /// sign * magnitude * 10**scale, exactly: zero is the signed zero, and an exponent beyond MRI's
        /// range overflows to an infinity or underflows to zero (raising, if the mode says so).
        /// </summary>
        internal static BigDecimal/*!*/ Create(Config/*!*/ config, int sign, BigInteger magnitude, Int128 scale) {
            Debug.Assert(magnitude.Sign >= 0);
            if (magnitude.IsZero) {
                return Zero(sign);
            }
            magnitude = StripTrailingZeros(magnitude, ref scale);
            int count = CountDigits(magnitude);
            Int128 exponent = scale + count;
            if (exponent > MaxExponent) {
                return ExponentOverflow(config, sign);
            }
            if (exponent < MinExponent) {
                return ExponentUnderflow(config, sign);
            }
            return new BigDecimal(sign, magnitude, count, (long)exponent);
        }

        /// <summary>An unscaled value carrying its own sign, times 10**scale; zero is +0.</summary>
        internal static BigDecimal/*!*/ Create(Config/*!*/ config, BigInteger value, Int128 scale) {
            return Create(config, value.Sign < 0 ? -1 : 1, BigInteger.Abs(value), scale);
        }

        internal static BigDecimal/*!*/ ExponentOverflow(Config/*!*/ config, int sign) {
            Raise(config, ExceptionModes.Overflow, "Exponent overflow");
            return Infinity(sign);
        }

        private static BigDecimal/*!*/ ExponentUnderflow(Config/*!*/ config, int sign) {
            Raise(config, ExceptionModes.Underflow, "Exponent underflow");
            return Zero(sign);
        }

        public static BigDecimal/*!*/ Create(Config/*!*/ config, int value) {
            return Create(config, (BigInteger)value, 0);
        }

        public static BigDecimal/*!*/ Create(Config/*!*/ config, long value) {
            return Create(config, (BigInteger)value, 0);
        }

        public static BigDecimal/*!*/ Create(Config/*!*/ config, BigInteger value) {
            return Create(config, value, 0);
        }

        /// <summary>
        /// A Float as MRI's rb_float_convert_to_BigDecimal makes it. With no digit count it takes the
        /// shortest digits that read back as the same Float, and keeps only the first 16 of them, unrounded
        /// (so 0.1 + 0.2 becomes 0.3). Otherwise it takes as many digits as dtoa's mode 2 gives: the Float's
        /// exact binary value rounded half to even (BigDecimal(1.25, 2) is 0.12e1) - nearly always.
        /// NaN and the infinities raise when the exception mode says so.
        /// </summary>
        public static BigDecimal/*!*/ Create(Config/*!*/ config, double value, int digits) {
            if (Double.IsNaN(value)) {
                return CheckResult(config, NaN);
            }
            if (Double.IsInfinity(value)) {
                return CheckResult(config, Infinity(value > 0 ? 1 : -1));
            }
            int sign = value < 0 || (value == 0 && Double.IsNegative(value)) ? -1 : 1;
            if (value == 0) {
                return Zero(sign);
            }

            if (digits == 0) {
                return CreateFromShortest(config, sign, Math.Abs(value));
            }

            string text = DtoaMode2(Math.Abs(value), digits, out int pointPosition);
            return Create(config, sign, BigInteger.Parse(text, CultureInfo.InvariantCulture), pointPosition - text.Length);
        }

        private static readonly double[] _tens = {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22
        };
        private static readonly double[] _bigTens = { 1e16, 1e32, 1e64, 1e128, 1e256 };

        /// <summary>
        /// The first <paramref name="ndigits"/> significant digits of d &gt; 0 as the dtoa.c in MRI's
        /// bigdecimal makes them (mode 2), trailing zeros dropped; d is 0.digits * 10**pointPosition.
        /// That is the correctly rounded value, but for one patch of Ruby's: when dtoa's quick
        /// floating-point attempt cannot tell whether the discarded part is more or less than half
        /// (it is within the attempt's error bound of half), it rounds an odd last digit up there and
        /// then, and makes the exact computation that follows leave an even one alone - whichever side
        /// of half the value really is. So BigDecimal(4.383733694592053e-09, 14), whose 15th digit is a
        /// 5 followed by 2862..., is 0.4383733694592e-8. The quick attempt is reproduced step for step.
        /// </summary>
        private static string/*!*/ DtoaMode2(double d, int ndigits, out int pointPosition) {
            long bits = BitConverter.DoubleToInt64Bits(d);
            int biasedExponent = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;
            BigInteger mantissa = biasedExponent == 0 ? fraction : fraction | (1L << 52);
            int binaryExponent = (biasedExponent == 0 ? 1 : biasedExponent) - 1075;

            // dtoa's estimate of floor(log10(d)), with d2 the mantissa scaled to [1, 2)
            double d2 = BitConverter.Int64BitsToDouble((bits & 0xFFFFFFFFFFFFFL) | 0x3FF0000000000000L);
            int i = biasedExponent - 1023;
            if (biasedExponent == 0) {
                // denormal: normalize the mantissa first
                int shift = 53 - (int)mantissa.GetBitLength();
                d2 = BitConverter.Int64BitsToDouble((((long)mantissa << shift) & 0xFFFFFFFFFFFFFL) | 0x3FF0000000000000L);
                i = -1022 - shift;
            }
            double ds = (d2 - 1.5) * 0.289529654602168 + 0.1760912590558 + i * 0.301029995663981;
            int k = (int)ds;
            if (ds < 0.0 && ds != k) {
                k--;
            }
            bool kCheck = true;
            if (k >= 0 && k <= 22) {
                if (d < _tens[k]) {
                    k--;
                }
                kCheck = false;
            }

            var s = new StringBuilder();
            bool half = false;
            int ilim = ndigits;
            if (ilim <= 14) {
                // Try to get by with floating-point arithmetic.
                double dd = d;
                int ieps = 2;
                int kq = k;
                if (k > 0) {
                    double tens = _tens[k & 0xf];
                    int j = k >> 4;
                    if ((j & 0x10) != 0) {
                        j &= 0xf;
                        dd /= _bigTens[4];
                        ieps++;
                    }
                    for (int b = 0; j != 0; j >>= 1, b++) {
                        if ((j & 1) != 0) {
                            ieps++;
                            tens *= _bigTens[b];
                        }
                    }
                    dd /= tens;
                } else if (k < 0) {
                    dd *= _tens[-k & 0xf];
                    for (int j = -k >> 4, b = 0; j != 0; j >>= 1, b++) {
                        if ((j & 1) != 0) {
                            ieps++;
                            dd *= _bigTens[b];
                        }
                    }
                }
                if (kCheck && dd < 1.0) {
                    kq--;
                    dd *= 10.0;
                    ieps++;
                }
                double eps = (ieps * dd + 7.0) * (1.0 / (1L << 52)) * _tens[ilim - 1];
                for (int n = 1; ; n++, dd *= 10.0) {
                    long digit = (long)dd;
                    dd -= digit;
                    if (dd == 0) {
                        ilim = n;
                    }
                    s.Append((char)('0' + digit));
                    if (n == ilim) {
                        if (dd > 0.5 + eps) {
                            return RoundUpDigits(s, kq, out pointPosition);
                        }
                        if (dd < 0.5 - eps) {
                            pointPosition = kq + 1;
                            return s.ToString().TrimEnd('0');
                        }
                        half = true;
                        if ((digit & 1) != 0) {
                            return RoundUpDigits(s, kq, out pointPosition);
                        }
                        break;
                    }
                }
                s.Clear();
                ilim = ndigits;
            }

            // Exactly. (dtoa's own way for a small integer rounds the same, without the patch.)
            bool smallInteger = k <= 14 && (binaryExponent >= 0 || (mantissa & ((BigInteger.One << -binaryExponent) - 1)).IsZero);
            BigInteger numerator;
            int scale;
            if (binaryExponent >= 0) {
                numerator = mantissa << binaryExponent;
                scale = 0;
            } else {
                numerator = mantissa * BigInteger.Pow(5, -binaryExponent);
                scale = binaryExponent;
            }
            int count = CountDigits(numerator);
            int drop = count - ilim;
            BigInteger quotient = numerator, remainder = BigInteger.Zero, unit = BigInteger.One;
            if (drop > 0) {
                unit = Pow10(drop);
                quotient = BigInteger.DivRem(numerator, unit, out remainder);
            }
            string digits = quotient.ToString(CultureInfo.InvariantCulture);
            int position = count + scale;
            int compared = (remainder << 1).CompareTo(unit);
            if (drop > 0 && (compared > 0 || (compared == 0 && !quotient.IsEven))) {
                s.Append(digits);
                if (smallInteger || !half) {
                    return RoundUpDigits(s, position - 1, out pointPosition);
                }
                // roundoff with the patch: the trailing 9s go, and an even digit stays as it is
                int last = s.Length - 1;
                while (last >= 0 && s[last] == '9') {
                    last--;
                }
                if (last < 0) {
                    pointPosition = position + 1;
                    return "1";
                }
                s.Length = last + 1;
                if (((s[last] - '0') & 1) != 0) {
                    s[last]++;
                }
                pointPosition = position;
                return s.ToString();
            }
            pointPosition = position;
            return digits.TrimEnd('0');
        }

        /// <summary>dtoa's bump_up: adds one to the last digit, carrying through 9s.</summary>
        private static string/*!*/ RoundUpDigits(StringBuilder/*!*/ s, int k, out int pointPosition) {
            int last = s.Length - 1;
            while (last >= 0 && s[last] == '9') {
                last--;
            }
            if (last < 0) {
                pointPosition = k + 2;
                return "1";
            }
            s.Length = last + 1;
            s[last]++;
            pointPosition = k + 1;
            return s.ToString();
        }

        private static BigDecimal/*!*/ CreateFromShortest(Config/*!*/ config, int sign, double magnitude) {
            // "R" is the shortest form that round-trips ("1.1805916207174113E+21", "0.0001", "1E-05")
            string text = magnitude.ToString("R", CultureInfo.InvariantCulture);
            int e = text.IndexOfAny(new[] { 'E', 'e' });
            long exponent = 0;
            if (e >= 0) {
                exponent = Int64.Parse(text.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                text = text.Substring(0, e);
            }
            int point = text.IndexOf('.');
            if (point >= 0) {
                exponent -= text.Length - point - 1;
                text = text.Remove(point, 1);
            }
            text = text.TrimStart('0');
            // dtoa's digits are at most 17; MRI keeps 16 and drops the rest
            if (text.Length > DoubleFig) {
                exponent += text.Length - DoubleFig;
                text = text.Substring(0, DoubleFig);
            }
            return Create(config, sign, BigInteger.Parse(text, CultureInfo.InvariantCulture), exponent);
        }

        #endregion

        #region Parsing

        private static bool IsSpace(char c) {
            return c == ' ' || (c >= '\t' && c <= '\r');
        }

        private static bool IsDigit(char c) {
            return c >= '0' && c <= '9';
        }

        /// <summary>
        /// MRI's VpAlloc. Strictly (Kernel#BigDecimal) the whole string must be a number, give or take
        /// surrounding white space, and null is returned when it is not. Loosely (String#to_d) the number
        /// ends at the first character that cannot continue it, and a string with no number is zero.
        /// Underscores may separate digits: after the first digit of the integer part (where one may also
        /// end it), and between two digits elsewhere.
        /// </summary>
        public static BigDecimal Parse(Config/*!*/ config, string/*!*/ str, bool strict) {
            int length = str.Length;
            int j = 0;
            char Peek(int k) => k < length ? str[k] : '\0';

            while (j < length && IsSpace(str[j])) {
                j++;
            }

            BigDecimal special = ParseSpecial(str, j);
            if (special != null) {
                return CheckResult(config, special);
            }

            if (Peek(j) == '#') {
                j++;
            }

            var integerDigits = new StringBuilder();
            var fractionDigits = new StringBuilder();
            var exponentDigits = new StringBuilder();
            int sign = 1;
            bool exponentNegative = false;
            bool exponentSeen = false;

            char v = Peek(j);
            if (v == '-' || v == '+') {
                sign = v == '-' ? -1 : 1;
                j++;
            }

            // integer part
            while ((v = Peek(j)) != '\0') {
                if (!strict && IsSpace(v)) {
                    v = '\0';
                    break;
                }
                if (v == '_') {
                    if (integerDigits.Length > 0) {
                        char next = Peek(j + 1);
                        if (next == '\0' || IsSpace(next) || IsDigit(next)) {
                            j++;
                            continue;
                        }
                        if (!strict) {
                            v = '\0';
                            break;
                        }
                    }
                    return Invalid(strict);
                }
                if (!IsDigit(v)) {
                    break;
                }
                integerDigits.Append(v);
                j++;
            }

            if (v != '\0') {
                // fraction part
                if (Peek(j) == '.') {
                    j++;
                    while ((v = Peek(j)) != '\0') {
                        if (!strict && IsSpace(v)) {
                            v = '\0';
                            break;
                        }
                        if (v == '_') {
                            if (fractionDigits.Length > 0 && IsDigit(Peek(j + 1))) {
                                j++;
                                continue;
                            }
                            if (!strict) {
                                v = '\0';
                                break;
                            }
                            return Invalid(strict);
                        }
                        if (!IsDigit(v)) {
                            break;
                        }
                        fractionDigits.Append(v);
                        j++;
                    }
                }

                // exponent part
                if (v != '\0') {
                    char marker = Peek(j);
                    if (marker == 'e' || marker == 'E' || marker == 'd' || marker == 'D') {
                        exponentSeen = true;
                        j++;
                        v = Peek(j);
                        if (v == '-' || v == '+') {
                            exponentNegative = v == '-';
                            j++;
                        }
                        while ((v = Peek(j)) != '\0') {
                            if (!strict && IsSpace(v)) {
                                v = '\0';
                                break;
                            }
                            if (v == '_') {
                                if (exponentDigits.Length > 0 && IsDigit(Peek(j + 1))) {
                                    j++;
                                    continue;
                                }
                                if (!strict) {
                                    v = '\0';
                                    if (exponentDigits.Length == 0) {
                                        exponentSeen = false;
                                    }
                                    break;
                                }
                                return Invalid(strict);
                            }
                            if (!IsDigit(v)) {
                                break;
                            }
                            exponentDigits.Append(v);
                            j++;
                        }
                    }
                }

                if (v != '\0') {
                    while (j < length && IsSpace(str[j])) {
                        j++;
                    }
                    if (j < length && strict) {
                        return Invalid(strict);
                    }
                }
            }

            if (strict && ((integerDigits.Length == 0 && fractionDigits.Length == 0) || (exponentSeen && exponentDigits.Length == 0))) {
                return null;
            }

            return CreateFromDigits(config, sign, integerDigits.ToString(), fractionDigits.ToString(), exponentNegative, exponentDigits.ToString());
        }

        private static BigDecimal Invalid(bool strict) {
            return strict ? null : PositiveZero;
        }

        // "Infinity", "+Infinity", "-Infinity" or "NaN", followed by nothing but white space.
        private static BigDecimal ParseSpecial(string/*!*/ str, int start) {
            foreach (var (text, value) in _specialStrings) {
                if (String.CompareOrdinal(str, start, text, 0, text.Length) != 0) {
                    continue;
                }
                int i = start + text.Length;
                while (i < str.Length && IsSpace(str[i])) {
                    i++;
                }
                if (i == str.Length) {
                    return value;
                }
            }
            return null;
        }

        private static readonly (string, BigDecimal)[] _specialStrings = {
            ("Infinity", PositiveInfinity), ("+Infinity", PositiveInfinity), ("-Infinity", NegativeInfinity), ("NaN", NaN)
        };

        /// <summary>MRI's VpCtoV: the value of integer.fraction * 10**exponent.</summary>
        private static BigDecimal/*!*/ CreateFromDigits(Config/*!*/ config, int sign, string/*!*/ integer, string/*!*/ fraction,
            bool exponentNegative, string/*!*/ exponentText) {

            string digits = (integer + fraction).TrimStart('0');
            if (digits.Length == 0) {
                return Zero(sign);
            }

            // An exponent beyond MRI's 64-bit one makes an infinity of a positive exponent and zero of a
            // negative one, whatever the digits before it: 17e-9223372036854775809 is zero.
            BigInteger exponent = exponentText.Length > 0 ? BigInteger.Parse(exponentText, CultureInfo.InvariantCulture) : BigInteger.Zero;
            if (exponentNegative) {
                exponent = -exponent;
            }
            BigInteger decimalExponent = exponent + digits.Length - fraction.Length;
            if (exponent > long.MaxValue || decimalExponent > MaxExponent) {
                Raise(config, ExceptionModes.Infinity, "exponent overflow");
                return CheckResult(config, Infinity(sign));
            }
            if (exponent < long.MinValue || decimalExponent < MinExponent) {
                return Zero(sign);
            }
            // within a string's length of the exponent, so an Int128 holds it
            Int128 scale = (Int128)(exponent - fraction.Length);
            return Create(config, sign, BigInteger.Parse(digits, CultureInfo.InvariantCulture), scale);
        }

        #endregion

        #region Exceptions

        internal static void Raise(Config/*!*/ config, ExceptionModes mode, string/*!*/ message) {
            if (config.Raises(mode)) {
                throw new FloatDomainError(message);
            }
        }

        /// <summary>MRI's CheckGetValue: a NaN or infinite result raises when the exception mode says so.</summary>
        internal static BigDecimal/*!*/ CheckResult(Config/*!*/ config, BigDecimal/*!*/ x) {
            if (x.IsNaN) {
                Raise(config, ExceptionModes.NaN, "Computation results in 'NaN' (Not a Number)");
            } else if (x.IsInfinite) {
                Raise(config, ExceptionModes.Infinity, x._sign > 0 ? "Computation results in 'Infinity'" : "Computation results in '-Infinity'");
            }
            return x;
        }

        /// <summary>
        /// The error of asking a special value for something only a finite number has (#to_i, #to_r): it
        /// raises whatever the exception mode.
        /// </summary>
        internal static Exception/*!*/ CreateSpecialValueError(BigDecimal/*!*/ x) {
            return new FloatDomainError(x.IsNaN
                ? "Computation results in 'NaN' (Not a Number)"
                : (x._sign > 0 ? "Computation results in 'Infinity'" : "Computation results in '-Infinity'"));
        }

        // MRI's VpIsDefOP reports a special result of an operation in its own words ("to", not "in").
        private static BigDecimal/*!*/ OperationNaN(Config/*!*/ config) {
            Raise(config, ExceptionModes.NaN, "Computation results to 'NaN'");
            return NaN;
        }

        private static BigDecimal/*!*/ OperationInfinity(Config/*!*/ config, BigDecimal/*!*/ result) {
            Raise(config, ExceptionModes.Infinity, result.IsInfinite && result._sign > 0
                ? "Computation results to 'Infinity'"
                : "Computation results to '-Infinity'");
            return result;
        }

        #endregion

        #region Rounding

        private static bool RoundsAway(RoundingModes mode, int sign, int comparedToHalf, bool inexact, bool odd) {
            switch (mode) {
                case RoundingModes.Up: return inexact;
                case RoundingModes.Down: return false;
                case RoundingModes.HalfUp: return comparedToHalf >= 0;
                case RoundingModes.HalfDown: return comparedToHalf > 0;
                case RoundingModes.HalfEven: return comparedToHalf > 0 || (comparedToHalf == 0 && odd);
                case RoundingModes.Ceiling: return inexact && sign > 0;
                case RoundingModes.Floor: return inexact && sign < 0;
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        /// <summary>
        /// Rounds sign * magnitude * 10**scale to a multiple of 10**position in the given mode.
        /// <paramref name="sticky"/> says that the true value is a little more than magnitude * 10**scale
        /// in magnitude - a quotient or root that did not come out exactly - which turns a tie into
        /// more than half and an exact value into an inexact one. A result of zero keeps the sign.
        /// </summary>
        internal static BigDecimal/*!*/ RoundAt(Config/*!*/ config, int sign, BigInteger magnitude, Int128 scale, Int128 position,
            RoundingModes mode, bool sticky) {

            if (position <= scale) {
                if (!sticky) {
                    return Create(config, sign, magnitude, scale);
                }
                magnitude *= Pow10(scale - position + 1);
                scale = position - 1;
            }

            Int128 drop = position - scale;
            BigInteger quotient;
            int comparedToHalf;
            bool inexact;
            if (magnitude.IsZero || drop > CountDigits(magnitude)) {
                // everything goes, and it is less than half of the last place kept
                quotient = BigInteger.Zero;
                inexact = !magnitude.IsZero || sticky;
                comparedToHalf = -1;
            } else {
                BigInteger unit = Pow10((long)drop);
                quotient = BigInteger.DivRem(magnitude, unit, out BigInteger remainder);
                inexact = !remainder.IsZero || sticky;
                comparedToHalf = (remainder << 1).CompareTo(unit);
                if (comparedToHalf == 0 && sticky) {
                    comparedToHalf = 1;
                }
            }
            if (RoundsAway(mode, sign, comparedToHalf, inexact, !quotient.IsEven)) {
                quotient += 1;
            }
            return Create(config, sign, quotient, position);
        }

        /// <summary>Rounds to <paramref name="digits"/> significant digits (MRI's VpLeftRound).</summary>
        internal static BigDecimal/*!*/ RoundToDigits(Config/*!*/ config, int sign, BigInteger magnitude, Int128 scale, int digits,
            RoundingModes mode, bool sticky) {

            if (magnitude.IsZero) {
                return Zero(sign);
            }
            return RoundAt(config, sign, magnitude, scale, scale + CountDigits(magnitude) - digits, mode, sticky);
        }

        /// <summary>A finite x rounded to <paramref name="digits"/> significant digits; others as they are.</summary>
        internal static BigDecimal/*!*/ RoundToDigits(Config/*!*/ config, BigDecimal/*!*/ x, int digits, RoundingModes mode) {
            if (!x.IsNonZeroFinite || digits <= 0 || x._digitCount <= digits) {
                return x;
            }
            return RoundAt(config, x._sign, x._digits, x.Scale, (Int128)x._exponent - digits, mode, false);
        }

        /// <summary>The global limit applied to the result of an operation (MRI's VpLimitRound).</summary>
        private static BigDecimal/*!*/ ApplyLimit(Config/*!*/ config, BigDecimal/*!*/ x, int limit) {
            return RoundToDigits(config, x, limit, config.RoundingMode);
        }

        /// <summary>
        /// #round, #truncate, #floor and #ceil: rounds to <paramref name="places"/> digits after the decimal
        /// point (before it, for a negative count). Zero and the special values are left alone.
        /// MRI's VpMidRound is exact but for one thing: in ROUND_UP mode, a position before the first of
        /// the 9-digit words the number occupies rounds to zero, so BigDecimal("0.04").round(-1, :up) is 0.
        /// </summary>
        public static BigDecimal/*!*/ RoundDecimalPlaces(Config/*!*/ config, BigDecimal/*!*/ x, long places, RoundingModes mode) {
            if (!x.IsNonZeroFinite) {
                return x;
            }
            if (mode == RoundingModes.Up && (Int128)places + WordExponent(x._exponent) * BASE_FIG < 0) {
                return Zero(x._sign);
            }
            return RoundAt(config, x._sign, x._digits, x.Scale, -(Int128)places, mode, false);
        }

        /// <summary>MRI's exponent in 9-digit words: the number is 0.[w0 w1 ...] * 10**(9 * WordExponent).</summary>
        private static long WordExponent(long exponent) {
            return exponent > 0 ? (exponent + BASE_FIG - 1) / BASE_FIG : -(-exponent / BASE_FIG);
        }

        #endregion

        #region Arithmetic

        /// <summary>
        /// x + y, or x - y for an <paramref name="operation"/> of -1, rounded to <paramref name="limit"/>
        /// significant digits, or to BigDecimal.limit for 0 (MRI's BigDecimal_addsub_with_coerce).
        /// </summary>
        public static BigDecimal/*!*/ Add(Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y, int operation, int limit) {
            if (x.IsNaN) {
                return CheckResult(config, x);
            }
            if (y.IsNaN) {
                return CheckResult(config, y);
            }
            int ySign = operation * y._sign;
            if (x.IsInfinite) {
                return y.IsInfinite && x._sign != ySign ? OperationNaN(config) : OperationInfinity(config, x);
            }
            if (y.IsInfinite) {
                return OperationInfinity(config, Infinity(ySign));
            }
            if (limit == 0) {
                limit = config.Limit;
            }
            if (x.IsZero) {
                if (y.IsZero) {
                    return Zero(x._sign < 0 && ySign < 0 ? -1 : 1);
                }
                return ApplyLimit(config, operation < 0 ? Negate(y) : y, limit);
            }
            if (y.IsZero) {
                return ApplyLimit(config, x, limit);
            }

            BigDecimal big = x, small = y;
            int bigSign = x._sign, smallSign = ySign;
            if (x._exponent < y._exponent) {
                big = y;
                small = x;
                bigSign = ySign;
                smallSign = x._sign;
            }
            BigInteger smallDigits = small._digits;
            Int128 smallScale = small.Scale;

            // An operand that lies wholly below both the other's last digit and the rounding position
            // only decides which way the rounding goes, as any smaller value of the same sign would; one
            // a long way down is replaced by a single digit, so that 1e1000000000 + 1 rounds without first
            // writing out a billion digits.
            if (limit > 0) {
                Int128 floor = Int128.Min(big.Scale, (Int128)big._exponent - limit) - 2;
                if (small._exponent < floor && floor - smallScale > 1000) {
                    smallDigits = BigInteger.One;
                    smallScale = floor - 1;
                }
            }

            Int128 scale = Int128.Min(big.Scale, smallScale);
            BigInteger sum = bigSign * big._digits * Pow10(big.Scale - scale) + smallSign * smallDigits * Pow10(smallScale - scale);
            if (sum.IsZero) {
                return PositiveZero;
            }
            BigDecimal result = Create(config, sum, scale);
            return ApplyLimit(config, result, limit);
        }

        /// <summary>
        /// x * y, rounded to <paramref name="digits"/> significant digits, or to BigDecimal.limit for 0
        /// (MRI's BigDecimal_mult_with_coerce).
        /// </summary>
        public static BigDecimal/*!*/ Multiply(Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y, int digits) {
            int sign = x._sign * y._sign;
            if (x.IsNaN || y.IsNaN) {
                return OperationNaN(config);
            }
            if (x.IsInfinite || y.IsInfinite) {
                return x.IsZero || y.IsZero ? OperationNaN(config) : OperationInfinity(config, Infinity(sign));
            }
            if (x.IsZero || y.IsZero) {
                return Zero(sign);
            }
            BigDecimal product = Create(config, sign, x._digits * y._digits, x.Scale + y.Scale);
            return RoundToDigits(config, product, digits != 0 ? digits : config.Limit, config.RoundingMode);
        }

        /// <summary>
        /// x / y to <paramref name="digits"/> significant digits in the current rounding mode, as MRI's
        /// BigDecimal_div2 makes it: the quotient rounded as if it were known exactly.
        /// </summary>
        public static BigDecimal/*!*/ Divide(Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y, int digits) {
            int sign = x._sign * y._sign;
            if (x.IsNaN || y.IsNaN) {
                return OperationNaN(config);
            }
            if (x.IsInfinite) {
                return y.IsInfinite ? OperationNaN(config) : CheckResult(config, OperationInfinity(config, Infinity(sign)));
            }
            if (y.IsInfinite) {
                // VpIsDefOP reports even this zero as an infinite result
                return OperationInfinity(config, Zero(sign));
            }
            if (y.IsZero) {
                if (x.IsZero) {
                    return OperationNaN(config);
                }
                Raise(config, ExceptionModes.ZeroDivide, "Divide by zero");
                return CheckResult(config, Infinity(sign));
            }
            if (x.IsZero) {
                return Zero(sign);
            }

            // enough digits of the quotient for one more than asked, and a remainder to say whether it is exact
            long shift = Math.Max(0, (long)digits + 2 - x._digitCount + y._digitCount);
            BigInteger quotient = BigInteger.DivRem(x._digits * Pow10(shift), y._digits, out BigInteger remainder);
            return RoundToDigits(config, sign, quotient, x.Scale - y.Scale - shift, digits, config.RoundingMode, !remainder.IsZero);
        }

        /// <summary>
        /// The digits #/ (and #div(y, 0)) divides to: the precision of the more precise operand plus 16,
        /// at least 32, and no more than BigDecimal.limit when one is set.
        /// </summary>
        public static int DivisionDigits(Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y) {
            long digits = Math.Max(x.Precision, y.Precision) + DoubleFig;
            if (digits < 2 * DoubleFig) {
                digits = 2 * DoubleFig;
            }
            if (config.Limit > 0 && config.Limit < digits) {
                digits = config.Limit;
            }
            return (int)Math.Min(digits, Int32.MaxValue);
        }

        /// <summary>
        /// MRI's BigDecimal_DoDivmod: the integral quotient and the remainder, the quotient floored
        /// (#divmod, #%) or truncated (#remainder). The quotient is a BigDecimal still; NaN and the
        /// infinities raise only when #divmod and #div make an Integer of it. A zero divisor raises.
        /// </summary>
        public static void DivMod(Config/*!*/ config, BigDecimal/*!*/ x, BigDecimal/*!*/ y, bool truncate,
            out BigDecimal/*!*/ div, out BigDecimal/*!*/ mod) {

            if (x.IsNaN || y.IsNaN || (x.IsInfinite && y.IsInfinite)) {
                div = mod = NaN;
                return;
            }
            if (y.IsZero) {
                throw new DivideByZeroException("divided by 0");
            }
            if (x.IsInfinite) {
                div = Infinity(x._sign == y._sign ? 1 : -1);
                mod = NaN;
                return;
            }
            if (x.IsZero) {
                div = PositiveZero;
                mod = x;
                return;
            }
            if (y.IsInfinite) {
                if (!truncate && x._sign != y._sign) {
                    div = Create(config, -1);
                    mod = y;
                } else {
                    div = PositiveZero;
                    mod = x;
                }
                return;
            }

            // exactly, at the exponent of the finer of the two
            Int128 scale = Int128.Min(x.Scale, y.Scale);
            BigInteger a = x._sign * x._digits * Pow10(x.Scale - scale);
            BigInteger b = y._sign * y._digits * Pow10(y.Scale - scale);
            BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
            if (!truncate && !r.IsZero && (r.Sign < 0) != (b.Sign < 0)) {
                q -= 1;
                r += b;
            }
            div = Create(config, q, 0);
            mod = Create(config, r, scale);
        }

        public static BigDecimal/*!*/ Negate(BigDecimal/*!*/ x) {
            switch (x._type) {
                case NumberTypes.NaN: return x;
                case NumberTypes.Infinite: return Infinity(-x._sign);
                default: return x.IsZero ? Zero(-x._sign) : new BigDecimal(-x._sign, x._digits, x._digitCount, x._exponent);
            }
        }

        public static BigDecimal/*!*/ Abs(BigDecimal/*!*/ x) {
            return x._sign < 0 && !x.IsNaN ? Negate(x) : x;
        }

        /// <summary>x * 10**n, exactly (MRI's _decimal_shift).</summary>
        public static BigDecimal/*!*/ DecimalShift(Config/*!*/ config, BigDecimal/*!*/ x, long n) {
            if (!x.IsNonZeroFinite || n == 0) {
                return CheckResult(config, x);
            }
            Int128 exponent = (Int128)x._exponent + n;
            if (exponent > MaxExponent) {
                return ExponentOverflow(config, x._sign);
            }
            if (exponent < MinExponent) {
                return ExponentUnderflow(config, x._sign);
            }
            return new BigDecimal(x._sign, x._digits, x._digitCount, (long)exponent);
        }

        /// <summary>
        /// bigdecimal 4.0.1's #sqrt, which the gem writes in Ruby (lib/bigdecimal.rb), step for step: Newton's
        /// iteration from the Float root, at a precision that doubles each time up to <paramref name="digits"/>
        /// + 16, then rounded to <paramref name="digits"/> in the current mode. 0 digits means
        /// n_significant_digits + 16, or BigDecimal.limit if that is less. It is not the correctly rounded
        /// root in every mode - the iteration may stop a unit below an exact one - so it is not computed as one.
        /// </summary>
        public static BigDecimal/*!*/ SquareRoot(Config/*!*/ config, BigDecimal/*!*/ x, int digits) {
            if (x.IsInfinite && x._sign > 0) {
                Raise(config, ExceptionModes.Infinity, "Computation results in 'Infinity'");
                return PositiveInfinity;
            }
            if (Compare(x, PositiveZero) < 0) {
                throw new FloatDomainError("sqrt of negative value");
            }
            if (x.IsNaN) {
                throw new FloatDomainError("sqrt of 'NaN'(Not a Number)");
            }
            if (x.IsZero) {
                return x;
            }
            if (digits == 0) {
                digits = x._digitCount + DoubleFig;
                if (config.Limit != 0) {
                    digits = Math.Min(config.Limit, digits);
                }
            }

            long shift = x._exponent >= 0 ? x._exponent / 2 : -((1 - x._exponent) / 2);
            BigDecimal a = DecimalShift(config, x, -2 * shift);
            BigDecimal y = Create(config, Math.Sqrt(ToFloat(config, a)), 0);
            BigDecimal two = Create(config, 2);
            var precisions = new System.Collections.Generic.List<int> { digits + DoubleFig };
            while (precisions[precisions.Count - 1] > DoubleFig) {
                precisions.Add(2 + precisions[precisions.Count - 1] / 2);
            }
            for (int i = precisions.Count - 1; i >= 0; i--) {
                int p = precisions[i];
                y = Divide(config, Add(config, y, Divide(config, a, y, p), 1, p), two, p);
            }
            return Multiply(config, DecimalShift(config, y, shift), Create(config, 1), digits);
        }

        /// <summary>The integer part, truncated towards zero (#fix).</summary>
        public static BigDecimal/*!*/ IntegerPart(Config/*!*/ config, BigDecimal/*!*/ x) {
            if (!x.IsNonZeroFinite || x.Scale >= 0) {
                return x;
            }
            return RoundAt(config, x._sign, x._digits, x.Scale, 0, RoundingModes.Down, false);
        }

        /// <summary>What #fix leaves out, with self's sign (#frac).</summary>
        public static BigDecimal/*!*/ FractionalPart(Config/*!*/ config, BigDecimal/*!*/ x) {
            if (!x.IsNonZeroFinite || x._exponent <= 0) {
                return x;
            }
            if (x.Scale >= 0) {
                return Zero(x._sign);
            }
            return Create(config, x._sign, x._digits % Pow10(-x.Scale), x.Scale);
        }

        #endregion

        #region Comparison

        /// <summary>x &lt;=&gt; y; null when either is NaN.</summary>
        public static int? Compare(BigDecimal/*!*/ x, BigDecimal/*!*/ y) {
            if (x.IsNaN || y.IsNaN) {
                return null;
            }
            int xRank = x.Rank, yRank = y.Rank;
            if (xRank != yRank || xRank == 0 || !x.IsFinite) {
                return xRank.CompareTo(yRank);
            }
            // two non-zero finite numbers of the same sign
            int result;
            if (x._exponent != y._exponent) {
                result = x._exponent.CompareTo(y._exponent);
            } else {
                int shift = x._digitCount - y._digitCount;
                result = shift >= 0
                    ? x._digits.CompareTo(y._digits * Pow10(shift))
                    : (x._digits * Pow10(-shift)).CompareTo(y._digits);
            }
            return x._sign * result;
        }

        // -Infinity < negative < zero (of either sign) < positive < Infinity
        private int Rank => IsZero ? 0 : (IsInfinite ? 2 : 1) * _sign;

        /// <summary>
        /// Equal numbers hash alike, whatever digits they were written with. As in MRI, the special values
        /// and the zeros hash to their sign: so +0 and -0 hash apart though they are ==.
        /// (Equals stays reference equality: IronRuby's #equal? is object.Equals for a CLR object.)
        /// </summary>
        public override int GetHashCode() {
            if (!IsNonZeroFinite) {
                return SignCode;
            }
            return HashCode.Combine(_sign, _digits, _exponent);
        }

        #endregion

        #region Precision

        /// <summary>
        /// The number of decimal digits it takes to write the number out in positional notation (MRI's
        /// #precision): BigDecimal("1e2") has one significant digit but a precision of 3, and 0.001 has a
        /// precision of 3 for the zeros after its decimal point. Zero and the special values have 0.
        /// </summary>
        public long Precision {
            get {
                if (!IsNonZeroFinite) {
                    return 0;
                }
                return _exponent > 0 ? Math.Max(_digitCount, _exponent) : _digitCount - _exponent;
            }
        }

        /// <summary>The digits after the decimal point.</summary>
        public long ScaleDigits => IsNonZeroFinite ? (long)Int128.Max(0, -Scale) : 0;

        /// <summary>
        /// The digits MRI's representation holds, 9 to a word with the words aligned at the decimal point -
        /// what #_dump writes before the colon.
        /// </summary>
        public long InternalDigits {
            get {
                if (!IsNonZeroFinite) {
                    return BASE_FIG;
                }
                Int128 words = FloorDivide((Int128)_exponent - 1, BASE_FIG) - FloorDivide(Scale, BASE_FIG) + 1;
                return (long)(words * BASE_FIG);
            }
        }

        private static Int128 FloorDivide(Int128 n, int d) {
            var (quotient, remainder) = Int128.DivRem(n, d);
            return remainder < 0 ? quotient - 1 : quotient;
        }

        #endregion

        #region Conversion

        /// <summary>
        /// MRI's #to_f: the nearest Float to the decimal value; one too big for a Float is an infinity and
        /// one too small a zero, each of which raises when the mode for it (EXCEPTION_OVERFLOW,
        /// EXCEPTION_UNDERFLOW) is set.
        /// </summary>
        public static double ToFloat(Config/*!*/ config, BigDecimal/*!*/ x) {
            switch (x._type) {
                case NumberTypes.NaN: return Double.NaN;
                case NumberTypes.Infinite: return x._sign > 0 ? Double.PositiveInfinity : Double.NegativeInfinity;
            }
            if (x.IsZero) {
                return x._sign > 0 ? 0.0 : -0.0;
            }
            double result;
            if (x._exponent > 400) {
                result = Double.PositiveInfinity;
            } else if (x._exponent < -400) {
                result = 0.0;
            } else {
                result = Double.Parse(x._digits.ToString(CultureInfo.InvariantCulture) + "E" + x.Scale.ToString(CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            if (Double.IsInfinity(result)) {
                Raise(config, ExceptionModes.Overflow, "BigDecimal to Float conversion");
            } else if (result == 0) {
                Raise(config, ExceptionModes.Underflow, "BigDecimal to Float conversion");
            }
            return x._sign > 0 ? result : -result;
        }

        /// <summary>The integer part as an Integer (#to_i); a special value raises.</summary>
        public static object/*!*/ ToInteger(BigDecimal/*!*/ x) {
            if (!x.IsFinite) {
                throw CreateSpecialValueError(x);
            }
            if (x.IsZero || x._exponent <= 0) {
                return ClrInteger.Zero;
            }
            BigInteger value = x.Scale >= 0 ? x._digits * Pow10(x.Scale) : x._digits / Pow10(-x.Scale);
            return Protocols.Normalize(x._sign < 0 ? -value : value);
        }

        #endregion

        #region ToString

        /// <summary>The digits as #split gives them: "0" for zero, "NaN" and "Infinity" for the others.</summary>
        public string/*!*/ GetDigitString() {
            switch (_type) {
                case NumberTypes.NaN: return "NaN";
                case NumberTypes.Infinite: return "Infinity";
            }
            return IsZero ? "0" : ToDecimalString(_digits, _digitCount);
        }

        // Below this many digits BigInteger.ToString is quick enough on its own.
        private const int DecimalStringLeafDigits = 2000;

        /// <summary>
        /// The <paramref name="digitCount"/> decimal digits of a positive value. BigInteger.ToString is
        /// quadratic in the number of digits - a million take half a minute - so a long value is split at
        /// 10**(DecimalStringLeafDigits * 2**k) and its halves written out separately, which is as fast as
        /// the runtime's division (a million digits in well under a second on .NET 10, half the time of
        /// ToString on .NET 8, whose division is itself quadratic).
        /// </summary>
        internal static string/*!*/ ToDecimalString(BigInteger n, int digitCount) {
            Debug.Assert(n.Sign > 0 && digitCount > 0);
            if (digitCount <= DecimalStringLeafDigits) {
                return n.ToString(CultureInfo.InvariantCulture);
            }
            // powers[k] = 10**(DecimalStringLeafDigits * 2**k), the last one at least half the digits
            var powers = new System.Collections.Generic.List<BigInteger> { Pow10(DecimalStringLeafDigits) };
            for (long d = DecimalStringLeafDigits; d * 2 < digitCount; d *= 2) {
                powers.Add(powers[powers.Count - 1] * powers[powers.Count - 1]);
            }
            var result = new char[digitCount];
            WriteDecimal(n, result, digitCount, digitCount, powers, powers.Count - 1);
            return new string(result);
        }

        // Writes n, which is less than 10**width, into buffer[end - width, end), padded with leading zeros.
        private static void WriteDecimal(BigInteger n, char[]/*!*/ buffer, int end, int width,
            System.Collections.Generic.List<BigInteger>/*!*/ powers, int level) {

            while (level >= 0 && (long)DecimalStringLeafDigits << level >= width) {
                level--;
            }
            if (level < 0) {
                string text = n.IsZero ? "" : n.ToString(CultureInfo.InvariantCulture);
                int start = end - text.Length;
                for (int i = end - width; i < start; i++) {
                    buffer[i] = '0';
                }
                text.CopyTo(0, buffer, start, text.Length);
                return;
            }
            int lowWidth = DecimalStringLeafDigits << level;
            BigInteger high = BigInteger.DivRem(n, powers[level], out BigInteger low);
            WriteDecimal(low, buffer, end, lowWidth, powers, level - 1);
            WriteDecimal(high, buffer, end - lowWidth, width - lowWidth, powers, level - 1);
        }

        public override string/*!*/ ToString() {
            return ToString(0, "", false);
        }

        // MRI's VpToSpecialString
        private string ToSpecialString(string/*!*/ plusSign) {
            switch (_type) {
                case NumberTypes.NaN: return "NaN";
                case NumberTypes.Infinite: return _sign > 0 ? plusSign + "Infinity" : "-Infinity";
            }
            if (IsZero) {
                return _sign > 0 ? plusSign + "0.0" : "-0.0";
            }
            return null;
        }

        /// <summary>
        /// #to_s: "0.12345e3", or "123.45" in the F format; <paramref name="plusSign"/> (" " or "+") goes
        /// before a positive number, and a non-zero <paramref name="group"/> puts a space between every that
        /// many digits - counted from the decimal point, so the integer part is grouped from its right.
        /// </summary>
        public string/*!*/ ToString(int group, string/*!*/ plusSign, bool floatFormat) {
            string special = ToSpecialString(plusSign);
            if (special != null) {
                return special;
            }

            var sb = new StringBuilder();
            sb.Append(_sign < 0 ? "-" : plusSign);
            string digits = ToDecimalString(_digits, _digitCount);
            if (!floatFormat) {
                sb.Append("0.");
                AppendGroupedFromLeft(sb, digits, group);
                sb.Append('e').Append(_exponent.ToString(CultureInfo.InvariantCulture));
                return sb.ToString();
            }

            if (_exponent > 0) {
                string integer = _exponent >= _digitCount
                    ? digits + new string('0', checked((int)(_exponent - _digitCount)))
                    : digits.Substring(0, (int)_exponent);
                AppendGroupedFromRight(sb, integer, group);
            } else {
                sb.Append('0');
            }
            sb.Append('.');
            if (_exponent >= _digitCount) {
                sb.Append('0');
            } else {
                string fraction = _exponent > 0
                    ? digits.Substring((int)_exponent)
                    : new string('0', checked((int)-_exponent)) + digits;
                AppendGroupedFromLeft(sb, fraction, group);
            }
            return sb.ToString();
        }

        private static void AppendGroupedFromLeft(StringBuilder/*!*/ sb, string/*!*/ digits, int group) {
            if (group <= 0) {
                sb.Append(digits);
                return;
            }
            for (int i = 0; i < digits.Length; i += group) {
                if (i > 0) {
                    sb.Append(' ');
                }
                sb.Append(digits, i, Math.Min(group, digits.Length - i));
            }
        }

        private static void AppendGroupedFromRight(StringBuilder/*!*/ sb, string/*!*/ digits, int group) {
            if (group <= 0) {
                sb.Append(digits);
                return;
            }
            int first = digits.Length % group;
            if (first == 0) {
                first = group;
            }
            sb.Append(digits, 0, Math.Min(first, digits.Length));
            for (int i = first; i < digits.Length; i += group) {
                sb.Append(' ').Append(digits, i, group);
            }
        }

        #endregion
    }
}

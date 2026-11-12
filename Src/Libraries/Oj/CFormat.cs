/* ****************************************************************************
 *
 * C printf for one double, as oj's :float_format and "%0.15g" default need it.
 *
 * .NET's numeric format strings are not C's: "G15" writes "E+15" where %g writes
 * "e+15", pads exponents differently and has no notion of the '#' flag.  This is
 * the C conversion done by hand - flags "-+ #0", width, precision and one of
 * aAeEfgG - over the double's exact decimal expansion, rounded half-to-even the
 * way glibc rounds, so that a float oj writes on CRuby comes out byte for byte the
 * same here.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Numerics;
using System.Text;

namespace IronRuby.StandardLibrary.Oj {

    internal static class CFormat {

        /// <summary>snprintf(buf, size, format, d) with a format holding at most one double directive.</summary>
        internal static string/*!*/ Format(string/*!*/ format, double d) {
            var sb = new StringBuilder();
            for (int i = 0; i < format.Length; i++) {
                char c = format[i];
                if (c != '%') {
                    sb.Append(c);
                    continue;
                }
                i++;
                if (i >= format.Length) {
                    sb.Append('%');
                    break;
                }
                if (format[i] == '%') {
                    sb.Append('%');
                    continue;
                }
                bool left = false, plus = false, space = false, alt = false, zero = false;
                for (; i < format.Length && "-+ #0".IndexOf(format[i]) >= 0; i++) {
                    switch (format[i]) {
                        case '-': left = true; break;
                        case '+': plus = true; break;
                        case ' ': space = true; break;
                        case '#': alt = true; break;
                        case '0': zero = true; break;
                    }
                }
                int width = 0;
                for (; i < format.Length && Char.IsDigit(format[i]); i++) {
                    width = width * 10 + (format[i] - '0');
                }
                int prec = -1;
                if (i < format.Length && format[i] == '.') {
                    prec = 0;
                    for (i++; i < format.Length && Char.IsDigit(format[i]); i++) {
                        prec = prec * 10 + (format[i] - '0');
                    }
                }
                if (i < format.Length && format[i] == 'l') {
                    i++;
                }
                if (i >= format.Length) {
                    break;
                }
                char conv = format[i];
                sb.Append(Convert(d, conv, left, plus, space, alt, zero, width, prec));
            }
            return sb.ToString();
        }

        private static string/*!*/ Convert(double d, char conv, bool left, bool plus, bool space, bool alt, bool zero,
            int width, int prec) {

            bool upper = Char.IsUpper(conv);
            bool neg = d < 0 || (d == 0 && 1 / d < 0) || (Double.IsNaN(d) && BitConverter.DoubleToInt64Bits(d) < 0);
            string body;

            if (Double.IsNaN(d) || Double.IsInfinity(d)) {
                body = Double.IsNaN(d) ? "nan" : "inf";
                if (upper) {
                    body = body.ToUpperInvariant();
                }
                zero = false;
            } else {
                double a = Math.Abs(d);
                switch (Char.ToLowerInvariant(conv)) {
                    case 'e': body = FormatE(a, prec < 0 ? 6 : prec, alt, upper); break;
                    case 'f': body = FormatF(a, prec < 0 ? 6 : prec, alt); break;
                    case 'g': body = FormatG(a, prec < 0 ? 6 : prec, alt, upper); break;
                    case 'a': body = FormatA(a, prec, alt, upper); break;
                    default: body = ""; break;
                }
            }

            string sign = neg ? "-" : (plus ? "+" : (space ? " " : ""));
            int len = sign.Length + body.Length;
            if (len >= width) {
                return sign + body;
            }
            int pad = width - len;
            if (left) {
                return sign + body + new string(' ', pad);
            }
            if (zero) {
                // zeros go after the sign and after a hex prefix
                if (body.StartsWith("0x", StringComparison.Ordinal) || body.StartsWith("0X", StringComparison.Ordinal)) {
                    return sign + body.Substring(0, 2) + new string('0', pad) + body.Substring(2);
                }
                return sign + new string('0', pad) + body;
            }
            return new string(' ', pad) + sign + body;
        }

        #region exact decimal expansion

        /// <summary>The exact value of a finite, non-negative double as 0.DIGITS x 10^exp; "0" for zero.</summary>
        private static void Exact(double a, out string digits, out int exp) {
            if (a == 0) {
                digits = "0";
                exp = 1;
                return;
            }
            long bits = BitConverter.DoubleToInt64Bits(a);
            int e = (int)((bits >> 52) & 0x7FF);
            long m = bits & 0xFFFFFFFFFFFFFL;
            if (e == 0) {
                e = 1;
            } else {
                m |= 1L << 52;
            }
            e -= 1075;   // a = m * 2^e

            BigInteger n;
            int pow10 = 0;
            if (e >= 0) {
                n = new BigInteger(m) << e;
            } else {
                n = new BigInteger(m) * BigInteger.Pow(5, -e);
                pow10 = e;   // a = n * 10^e
            }
            digits = n.ToString();
            exp = digits.Length + pow10;
            digits = digits.TrimEnd('0');
            if (digits.Length == 0) {
                digits = "0";
            }
        }

        /// <summary>
        /// Rounds 0.DIGITS x 10^exp to count significant digits, half to even, and answers the
        /// digits (exactly count of them, zero padded) with the exponent adjusted for a carry.
        /// count may be zero or negative, which rounds to a unit in a position above the first digit.
        /// </summary>
        private static string/*!*/ Round(string/*!*/ digits, ref int exp, int count) {
            if (count >= digits.Length) {
                return digits.PadRight(Math.Max(count, 0), '0');
            }
            if (count < 0) {
                // everything is below half a unit of the rounding position
                exp += -count;   // keep it meaning "zero at that scale"
                return "";
            }

            // decide on the digit at position count
            int next = digits[count] - '0';
            bool roundUp;
            if (next > 5) {
                roundUp = true;
            } else if (next < 5) {
                roundUp = false;
            } else {
                bool more = false;
                for (int i = count + 1; i < digits.Length; i++) {
                    if (digits[i] != '0') {
                        more = true;
                        break;
                    }
                }
                if (more) {
                    roundUp = true;
                } else {
                    int prev = count > 0 ? digits[count - 1] - '0' : 0;
                    roundUp = (prev & 1) == 1;
                }
            }

            char[] kept = digits.Substring(0, count).ToCharArray();
            if (roundUp) {
                int i = kept.Length - 1;
                while (i >= 0) {
                    if (kept[i] == '9') {
                        kept[i] = '0';
                        i--;
                    } else {
                        kept[i]++;
                        break;
                    }
                }
                if (i < 0) {
                    // carried out of the top digit
                    exp++;
                    if (kept.Length == 0) {
                        return "1";
                    }
                    var carried = new char[kept.Length];
                    carried[0] = '1';
                    for (int j = 1; j < kept.Length; j++) {
                        carried[j] = '0';
                    }
                    return new string(carried);
                }
            }
            return new string(kept);
        }

        #endregion

        private static string/*!*/ Exponent(int x, bool upper) {
            var sb = new StringBuilder();
            sb.Append(upper ? 'E' : 'e');
            sb.Append(x < 0 ? '-' : '+');
            int ax = Math.Abs(x);
            if (ax < 10) {
                sb.Append('0');
            }
            sb.Append(ax);
            return sb.ToString();
        }

        private static string/*!*/ FormatE(double a, int prec, bool alt, bool upper) {
            string digits;
            int exp;
            Exact(a, out digits, out exp);
            string kept;
            int x;
            if (a == 0) {
                kept = new string('0', prec + 1);
                x = 0;
            } else {
                kept = Round(digits, ref exp, prec + 1);
                x = exp - 1;
            }
            var sb = new StringBuilder();
            sb.Append(kept[0]);
            if (prec > 0 || alt) {
                sb.Append('.');
            }
            sb.Append(kept, 1, prec);
            sb.Append(Exponent(x, upper));
            return sb.ToString();
        }

        private static string/*!*/ FormatF(double a, int prec, bool alt) {
            string digits;
            int exp;
            Exact(a, out digits, out exp);
            string intPart;
            string fracPart;
            if (a == 0) {
                intPart = "0";
                fracPart = new string('0', prec);
            } else {
                // keep exp integer digits and prec fractional ones
                int count = exp + prec;
                int e = exp;
                string kept = Round(digits, ref e, count);
                // kept holds the digits from the top (10^(e-1)) down to 10^-prec
                int intLen = e;
                string all;
                if (kept.Length == 0) {
                    all = new string('0', prec + 1);
                    intLen = 1;
                } else if (intLen <= 0) {
                    all = new string('0', 1 - intLen) + kept;
                    intLen = 1;
                } else {
                    all = kept;
                }
                int need = intLen + prec;
                if (all.Length < need) {
                    all = all.PadRight(need, '0');
                }
                intPart = all.Substring(0, intLen);
                fracPart = all.Substring(intLen, prec);
            }
            if (prec > 0 || alt) {
                return intPart + "." + fracPart;
            }
            return intPart;
        }

        private static string/*!*/ FormatG(double a, int prec, bool alt, bool upper) {
            int p = prec == 0 ? 1 : prec;
            int x;
            if (a == 0) {
                x = 0;
            } else {
                string digits;
                int exp;
                Exact(a, out digits, out exp);
                Round(digits, ref exp, p);
                x = exp - 1;
            }

            string result;
            if (p > x && x >= -4) {
                result = FormatF(a, p - 1 - x, alt);
            } else {
                result = FormatE(a, p - 1, alt, upper);
            }
            if (!alt) {
                // drop trailing zeros of the fraction, and the point if nothing is left
                int ePos = result.IndexOfAny(new[] { 'e', 'E' });
                string mant = ePos < 0 ? result : result.Substring(0, ePos);
                string tail = ePos < 0 ? "" : result.Substring(ePos);
                if (mant.IndexOf('.') >= 0) {
                    mant = mant.TrimEnd('0');
                    if (mant.EndsWith(".", StringComparison.Ordinal)) {
                        mant = mant.Substring(0, mant.Length - 1);
                    }
                }
                result = mant + tail;
            }
            return result;
        }

        private static string/*!*/ FormatA(double a, int prec, bool alt, bool upper) {
            long bits = BitConverter.DoubleToInt64Bits(a);
            int e = (int)((bits >> 52) & 0x7FF);
            long m = bits & 0xFFFFFFFFFFFFFL;
            int lead;
            int x;
            if (a == 0) {
                lead = 0;
                x = 0;
            } else if (e == 0) {
                lead = 0;
                x = -1022;
            } else {
                lead = 1;
                x = e - 1023;
            }
            string hex = m.ToString("x13");
            if (prec < 0) {
                hex = hex.TrimEnd('0');
            } else if (prec < 13) {
                // round the 52-bit fraction to prec hex digits, half to even
                int drop = (13 - prec) * 4;
                long full = ((long)lead << 52) | m;
                long unit = 1L << drop;
                long rem = full & (unit - 1);
                long q = full >> drop;
                long half = unit >> 1;
                if (rem > half || (rem == half && (q & 1) == 1)) {
                    q++;
                }
                lead = (int)(q >> (prec * 4));
                long frac = q & ((1L << (prec * 4)) - 1);
                hex = prec == 0 ? "" : frac.ToString("x" + prec);
            } else {
                hex = hex.PadRight(prec, '0');
            }
            var sb = new StringBuilder();
            sb.Append(upper ? "0X" : "0x");
            sb.Append(lead);
            if (hex.Length > 0 || alt) {
                sb.Append('.');
            }
            sb.Append(upper ? hex.ToUpperInvariant() : hex);
            sb.Append(upper ? 'P' : 'p');
            sb.Append(x < 0 ? '-' : '+');
            sb.Append(Math.Abs(x));
            return sb.ToString();
        }
    }
}

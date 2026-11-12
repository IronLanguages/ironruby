/* ****************************************************************************
 *
 * Date for IronRuby - calendar arithmetic.
 *
 * MRI implements Date/DateTime as the "date_core" C extension.  This is a C#
 * reimplementation in the same spirit as the Digest, Zlib, StringIO, Json and
 * BigDecimal libraries here - the approach JRuby also takes for the C-backed
 * parts of the standard library.
 *
 * The representation deliberately is *not* System.DateTime: Ruby's Date is an
 * astronomical Julian Day Number plus a calendar-reform "start" value, which
 * System.DateTime cannot express (no years before 1, proleptic Gregorian only).
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Numerics;

namespace IronRuby.StandardLibrary.Date {

    /// <summary>
    /// An exact non-negative-denominator rational used for the sub-day part of a
    /// Date.  MRI is exact here (sec_fraction of Rational(1,3) stays 1/3), so a
    /// double would be wrong.
    /// </summary>
    internal struct Frac {
        internal BigInteger N;
        internal BigInteger D;

        internal Frac(BigInteger n, BigInteger d) {
            if (d.Sign == 0) {
                throw new DivideByZeroException();
            }
            if (d.Sign < 0) {
                n = -n;
                d = -d;
            }
            if (!n.IsZero) {
                BigInteger g = BigInteger.GreatestCommonDivisor(BigInteger.Abs(n), d);
                if (!g.IsOne) {
                    n /= g;
                    d /= g;
                }
            } else {
                d = BigInteger.One;
            }
            N = n;
            D = d;
        }

        internal static readonly Frac Zero = new Frac(BigInteger.Zero, BigInteger.One);

        internal bool IsZero {
            get { return N.IsZero; }
        }

        internal static Frac Add(Frac a, Frac b) {
            return new Frac(a.N * b.D + b.N * a.D, a.D * b.D);
        }

        internal static Frac Sub(Frac a, Frac b) {
            return new Frac(a.N * b.D - b.N * a.D, a.D * b.D);
        }

        internal static Frac Mul(Frac a, BigInteger k) {
            return new Frac(a.N * k, a.D);
        }

        internal static Frac FromInt(BigInteger v) {
            return new Frac(v, BigInteger.One);
        }

        /// <summary>
        /// Exact dyadic expansion of a double, i.e. what Float#to_r returns.
        /// IronRuby's Float has no #to_r, and rounding here would lose the
        /// sub-microsecond precision ruby/spec checks for.
        /// </summary>
        internal static Frac FromDouble(double value) {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) {
                throw new ArgumentOutOfRangeException("value");
            }
            long bits = BitConverter.DoubleToInt64Bits(value);
            bool negative = bits < 0;
            int exponent = (int)((bits >> 52) & 0x7FF);
            long mantissa = bits & 0xFFFFFFFFFFFFFL;
            if (exponent == 0) {
                exponent++;
            } else {
                mantissa |= 1L << 52;
            }
            exponent -= 1075;

            BigInteger n = mantissa;
            BigInteger d = BigInteger.One;
            if (exponent > 0) {
                n <<= exponent;
            } else if (exponent < 0) {
                d = BigInteger.One << (-exponent);
            }
            return new Frac(negative ? -n : n, d);
        }

        internal BigInteger Floor {
            get { return DateMath.FloorDiv(N, D); }
        }

        internal double ToDouble() {
            return (double)N / (double)D;
        }

        internal int CompareTo(Frac other) {
            return (N * other.D).CompareTo(other.N * D);
        }
    }

    internal static class DateMath {
        internal const double ITALY = 2299161.0;
        internal const double ENGLAND = 2361222.0;
        internal const double JULIAN = Double.PositiveInfinity;
        internal const double GREGORIAN = Double.NegativeInfinity;

        internal const int SecondsPerDay = 86400;

        #region floor division

        internal static long FloorDiv(long a, long b) {
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) {
                q--;
            }
            return q;
        }

        internal static long FloorMod(long a, long b) {
            long r = a % b;
            if (r != 0 && ((r < 0) != (b < 0))) {
                r += b;
            }
            return r;
        }

        internal static BigInteger FloorDiv(BigInteger a, BigInteger b) {
            BigInteger r;
            BigInteger q = BigInteger.DivRem(a, b, out r);
            if (!r.IsZero && ((a.Sign < 0) != (b.Sign < 0))) {
                q -= BigInteger.One;
            }
            return q;
        }

        internal static BigInteger FloorMod(BigInteger a, BigInteger b) {
            BigInteger r = BigInteger.Remainder(a, b);
            if (!r.IsZero && ((r.Sign < 0) != (b.Sign < 0))) {
                r += b;
            }
            return r;
        }

        #endregion

        #region civil <-> jd

        internal static long GregorianCivilToJd(long y, int m, int d) {
            long a = FloorDiv(14 - m, 12);
            long yy = y + 4800 - a;
            long mm = m + 12 * a - 3;
            return d + FloorDiv(153 * mm + 2, 5) + 365 * yy + FloorDiv(yy, 4) - FloorDiv(yy, 100) + FloorDiv(yy, 400) - 32045;
        }

        internal static long JulianCivilToJd(long y, int m, int d) {
            long a = FloorDiv(14 - m, 12);
            long yy = y + 4800 - a;
            long mm = m + 12 * a - 3;
            return d + FloorDiv(153 * mm + 2, 5) + 365 * yy + FloorDiv(yy, 4) - 32083;
        }

        internal static void GregorianJdToCivil(long jd, out long y, out int m, out int d) {
            long a = jd + 32044;
            long b = FloorDiv(4 * a + 3, 146097);
            long c = a - FloorDiv(146097 * b, 4);
            long dd = FloorDiv(4 * c + 3, 1461);
            long e = c - FloorDiv(1461 * dd, 4);
            long mm = FloorDiv(5 * e + 2, 153);
            d = (int)(e - FloorDiv(153 * mm + 2, 5) + 1);
            m = (int)(mm + 3 - 12 * FloorDiv(mm, 10));
            y = 100 * b + dd - 4800 + FloorDiv(mm, 10);
        }

        internal static void JulianJdToCivil(long jd, out long y, out int m, out int d) {
            long c = jd + 32082;
            long dd = FloorDiv(4 * c + 3, 1461);
            long e = c - FloorDiv(1461 * dd, 4);
            long mm = FloorDiv(5 * e + 2, 153);
            d = (int)(e - FloorDiv(153 * mm + 2, 5) + 1);
            m = (int)(mm + 3 - 12 * FloorDiv(mm, 10));
            y = dd - 4800 + FloorDiv(mm, 10);
        }

        /// <summary>
        /// True when the given (already computed) Julian Day falls on the Gregorian
        /// side of the calendar reform described by <paramref name="sg"/>.
        /// </summary>
        internal static bool IsGregorian(long jd, double sg) {
            return (double)jd >= sg;
        }

        /// <summary>
        /// Civil to JD honouring the calendar reform: the Gregorian value is computed
        /// first and, if it turns out to be before the reform, the date is re-read as
        /// a Julian one.  This is what MRI's c_civil_to_jd does.
        /// </summary>
        internal static long CivilToJd(long y, int m, int d, double sg) {
            long jd = GregorianCivilToJd(y, m, d);
            if ((double)jd < sg) {
                jd = JulianCivilToJd(y, m, d);
            }
            return jd;
        }

        internal static void JdToCivil(long jd, double sg, out long y, out int m, out int d) {
            if (IsGregorian(jd, sg)) {
                GregorianJdToCivil(jd, out y, out m, out d);
            } else {
                JulianJdToCivil(jd, out y, out m, out d);
            }
        }

        #endregion

        #region validation

        internal static bool ValidCivil(long y, int m, int d, double sg, out long jd) {
            jd = 0;
            if (m < 0) {
                m += 13;
            }
            if (m < 1 || m > 12) {
                return false;
            }
            if (d < 0) {
                long ldom;
                if (!FindLastDayOfMonth(y, m, sg, out ldom)) {
                    return false;
                }
                long y2; int m2, d2;
                JdToCivil(ldom + d + 1, sg, out y2, out m2, out d2);
                if (y2 != y || m2 != m) {
                    return false;
                }
                d = d2;
            }
            if (d < 1) {
                return false;
            }
            jd = CivilToJd(y, m, d, sg);
            long ry; int rm, rd;
            JdToCivil(jd, sg, out ry, out rm, out rd);
            return ry == y && rm == m && rd == d;
        }

        internal static bool FindFirstDayOfYear(long y, double sg, out long jd) {
            for (int d = 1; d <= 31; d++) {
                if (ValidCivil(y, 1, d, sg, out jd)) {
                    return true;
                }
            }
            jd = 0;
            return false;
        }

        internal static bool FindLastDayOfYear(long y, double sg, out long jd) {
            for (int d = 31; d >= 1; d--) {
                if (ValidCivil(y, 12, d, sg, out jd)) {
                    return true;
                }
            }
            jd = 0;
            return false;
        }

        internal static bool FindLastDayOfMonth(long y, int m, double sg, out long jd) {
            for (int d = 31; d >= 1; d--) {
                if (ValidCivil(y, m, d, sg, out jd)) {
                    return true;
                }
            }
            jd = 0;
            return false;
        }

        #endregion

        #region ordinal

        internal static void JdToOrdinal(long jd, double sg, out long y, out int yday) {
            long ry; int rm, rd;
            JdToCivil(jd, sg, out ry, out rm, out rd);
            long fdoy;
            if (!FindFirstDayOfYear(ry, sg, out fdoy)) {
                fdoy = CivilToJd(ry, 1, 1, sg);
            }
            y = ry;
            yday = (int)(jd - fdoy + 1);
        }

        internal static bool ValidOrdinal(long y, int d, double sg, out long jd) {
            jd = 0;
            if (d < 0) {
                long ldoy;
                if (!FindLastDayOfYear(y, sg, out ldoy)) {
                    return false;
                }
                long y2; int d2;
                JdToOrdinal(ldoy + d + 1, sg, out y2, out d2);
                if (y2 != y) {
                    return false;
                }
                d = d2;
            }
            if (d < 1) {
                return false;
            }
            long fdoy;
            if (!FindFirstDayOfYear(y, sg, out fdoy)) {
                return false;
            }
            jd = fdoy + d - 1;
            long ry; int ryday;
            JdToOrdinal(jd, sg, out ry, out ryday);
            return ry == y && ryday == d;
        }

        #endregion

        #region commercial (ISO week date)

        // JD 0 is a Monday, so jd % 7 == 0 <=> Monday and cwday == (jd mod 7) + 1.

        // Mirrors c_commercial_to_jd / c_jd_to_commercial in CRuby's ext/date/date_core.c.
        internal static long CommercialToJd(long cwyear, int cweek, int cwday, double sg) {
            long fdoy;
            if (!FindFirstDayOfYear(cwyear, sg, out fdoy)) {
                fdoy = CivilToJd(cwyear, 1, 1, sg);
            }
            long a = fdoy + 3;
            return (a - FloorMod(a, 7)) + 7L * (cweek - 1) + (cwday - 1);
        }

        internal static void JdToCommercial(long jd, double sg, out long cwyear, out int cweek, out int cwday) {
            long y; int m, d;
            JdToCivil(jd - 3, sg, out y, out m, out d);
            long week1Start = CommercialToJd(y + 1, 1, 1, sg);
            if (jd >= week1Start) {
                cwyear = y + 1;
            } else {
                week1Start = CommercialToJd(y, 1, 1, sg);
                cwyear = y;
            }
            cweek = (int)FloorDiv(jd - week1Start, 7) + 1;
            cwday = (int)FloorMod(jd + 1, 7);
            if (cwday == 0) {
                cwday = 7;
            }
        }

        internal static bool ValidCommercial(long y, int w, int d, double sg, out long jd) {
            jd = 0;
            if (d < 0) {
                d += 8;
            }
            if (d < 1 || d > 7) {
                return false;
            }
            if (w < 0) {
                long endJd = CommercialToJd(y + 1, 1, 1, sg) + 7L * w;
                long ny; int nw, nd;
                JdToCommercial(endJd, sg, out ny, out nw, out nd);
                if (ny != y) {
                    return false;
                }
                w = nw;
            }
            if (w < 1) {
                return false;
            }
            jd = CommercialToJd(y, w, d, sg);
            long ry; int rw, rd;
            JdToCommercial(jd, sg, out ry, out rw, out rd);
            return ry == y && rw == w && rd == d;
        }

        #endregion

        #region misc

        internal static int JdToWday(long jd) {
            return (int)FloorMod(jd + 1, 7);
        }

        internal static bool GregorianLeap(long y) {
            return (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;
        }

        internal static bool JulianLeap(long y) {
            return FloorMod(y, 4) == 0;
        }

        #endregion
    }
}

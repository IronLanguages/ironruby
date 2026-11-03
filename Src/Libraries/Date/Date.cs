/* ****************************************************************************
 *
 * Date for IronRuby.
 *
 * See DateMath.cs for the representation rationale.  Short version: a Date is
 *
 *   (jd, df, sf, of, sg)
 *
 * where jd is the chronological Julian Day Number of the *local* civil date,
 * df the number of whole seconds into that local day, sf an exact rational
 * sub-second remainder, of the UTC offset in seconds and sg the Julian day on
 * which the Gregorian calendar reform takes effect (Date::ITALY by default).
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Date {

    /// <summary>
    /// Date::Error, a subclass of ArgumentError (System.ArgumentException here).
    /// </summary>
    public class DateError : ArgumentException {
        public DateError(string message) : base(message) { }
        public DateError() : this("invalid date") { }
    }

    [RubyClass("Date"), Includes(typeof(Comparable))]
    public class RubyDate : RubyObject {

        #region state

        internal long _jd;
        internal int _df;
        internal Frac _sf;
        internal int _of;
        internal double _sg;

        public RubyDate(RubyClass/*!*/ cls)
            : base(cls) {
            _sf = Frac.Zero;
            _sg = DateMath.ITALY;
        }

#if FEATURE_SERIALIZATION
        protected RubyDate(SerializationInfo/*!*/ info, StreamingContext context)
            : base(info, context) {
            _sf = Frac.Zero;
            _sg = DateMath.ITALY;
        }
#endif

        protected override RubyObject/*!*/ CreateInstance() {
            return new RubyDate(ImmediateClass.NominalClass);
        }

        internal void Set(long jd, int df, Frac sf, int of, double sg) {
            _jd = jd;
            _df = df;
            _sf = sf;
            _of = of;
            _sg = sg;
        }

        /// <summary>
        /// Creates a new instance of the same Ruby class as <c>this</c>, so that
        /// arithmetic on a DateTime (or on a user subclass) keeps the class.
        /// </summary>
        internal RubyDate/*!*/ Derive(long jd, int df, Frac sf, int of, double sg) {
            var result = (RubyDate)CreateInstance();
            result.Set(jd, df, sf, of, sg);
            return result;
        }

        internal RubyDate/*!*/ Derive(long jd) {
            return Derive(jd, _df, _sf, _of, _sg);
        }

        #endregion

        #region constants

        [RubyConstant]
        public const int ITALY = 2299161;

        [RubyConstant]
        public const int ENGLAND = 2361222;

        [RubyConstant]
        public const double JULIAN = Double.PositiveInfinity;

        [RubyConstant]
        public const double GREGORIAN = Double.NegativeInfinity;

        [RubyConstant]
        public const string VERSION = "3.3.4";

        [RubyClass("Error", Extends = typeof(DateError), Inherits = typeof(ArgumentException))]
        public static class DateErrorOps {
        }

        #endregion

        #region helpers

        internal static double ToStart(object sg) {
            if (sg == null) {
                return DateMath.ITALY;
            }
            if (sg is int) {
                return (int)sg;
            }
            if (sg is double) {
                return (double)sg;
            }
            if (sg is BigInteger) {
                return (double)(BigInteger)sg;
            }
            if (sg is float) {
                return (float)sg;
            }
            if (sg is long) {
                return (long)sg;
            }
            throw RubyExceptions.CreateTypeError("expected numeric");
        }

        internal static long ToLong(object value) {
            if (value is int) {
                return (int)value;
            }
            if (value is BigInteger) {
                return (long)(BigInteger)value;
            }
            if (value is long) {
                return (long)value;
            }
            if (value is double) {
                return (long)Math.Floor((double)value);
            }
            throw RubyExceptions.CreateTypeError("expected numeric");
        }

        internal static DateError InvalidDate() {
            return new DateError("invalid date");
        }

        internal bool Julian {
            get { return !DateMath.IsGregorian(_jd, _sg); }
        }

        internal void GetCivil(out long y, out int m, out int d) {
            DateMath.JdToCivil(_jd, _sg, out y, out m, out d);
        }

        /// <summary>Astronomical Julian day (UTC), as an exact fraction.</summary>
        internal Frac Ajd {
            get {
                // jd + (df - of)/86400 + sf/86400 - 1/2
                Frac secs = Frac.Add(Frac.FromInt(_df - _of), _sf);
                Frac days = new Frac(secs.N, secs.D * DateMath.SecondsPerDay);
                Frac r = Frac.Add(Frac.FromInt(_jd), days);
                return Frac.Sub(r, new Frac(BigInteger.One, 2));
            }
        }

        internal Frac DayFraction {
            get {
                Frac secs = Frac.Add(Frac.FromInt(_df), _sf);
                return new Frac(secs.N, secs.D * DateMath.SecondsPerDay);
            }
        }

        #endregion

        #region construction

        internal static RubyDate/*!*/ Allocate(RubyClass/*!*/ cls) {
            var t = cls.GetUnderlyingSystemType();
            if (typeof(RubyDateTime).IsAssignableFrom(t)) {
                return new RubyDateTime(cls);
            }
            return new RubyDate(cls);
        }

        internal static RubyDate/*!*/ CreateCivil(RubyClass/*!*/ cls, long y, int m, int d, double sg) {
            long jd;
            if (!DateMath.ValidCivil(y, m, d, sg, out jd)) {
                throw InvalidDate();
            }
            var result = Allocate(cls);
            result.Set(jd, 0, Frac.Zero, 0, sg);
            return result;
        }

        [RubyConstructor]
        public static RubyDate/*!*/ Create(RubyClass/*!*/ self,
            [DefaultProtocol, DefaultParameterValue(-4712)]int year,
            [DefaultProtocol, DefaultParameterValue(1)]int month,
            [DefaultProtocol, DefaultParameterValue(1)]int day,
            [DefaultParameterValue(null)]object start) {
            return CreateCivil(self, year, month, day, ToStart(start));
        }

        [RubyMethod("civil", RubyMethodAttributes.PublicSingleton)]
        public static RubyDate/*!*/ Civil(RubyClass/*!*/ self,
            [DefaultProtocol, DefaultParameterValue(-4712)]int year,
            [DefaultProtocol, DefaultParameterValue(1)]int month,
            [DefaultProtocol, DefaultParameterValue(1)]int day,
            [DefaultParameterValue(null)]object start) {
            return CreateCivil(self, year, month, day, ToStart(start));
        }

        [RubyMethod("jd", RubyMethodAttributes.PublicSingleton)]
        public static RubyDate/*!*/ FromJd(RubyClass/*!*/ self, [DefaultParameterValue(null)]object jd, [DefaultParameterValue(null)]object start) {
            long j = jd == null ? 0 : ToLong(jd);
            var result = Allocate(self);
            result.Set(j, 0, Frac.Zero, 0, ToStart(start));
            return result;
        }

        [RubyMethod("ordinal", RubyMethodAttributes.PublicSingleton)]
        public static RubyDate/*!*/ Ordinal(RubyClass/*!*/ self,
            [DefaultProtocol, DefaultParameterValue(-4712)]int year,
            [DefaultProtocol, DefaultParameterValue(1)]int yday,
            [DefaultParameterValue(null)]object start) {
            double sg = ToStart(start);
            long jd;
            if (!DateMath.ValidOrdinal(year, yday, sg, out jd)) {
                throw InvalidDate();
            }
            var result = Allocate(self);
            result.Set(jd, 0, Frac.Zero, 0, sg);
            return result;
        }

        [RubyMethod("commercial", RubyMethodAttributes.PublicSingleton)]
        public static RubyDate/*!*/ Commercial(RubyClass/*!*/ self,
            [DefaultProtocol, DefaultParameterValue(-4712)]int cwyear,
            [DefaultProtocol, DefaultParameterValue(1)]int cweek,
            [DefaultProtocol, DefaultParameterValue(1)]int cwday,
            [DefaultParameterValue(null)]object start) {
            double sg = ToStart(start);
            long jd;
            if (!DateMath.ValidCommercial(cwyear, cweek, cwday, sg, out jd)) {
                throw InvalidDate();
            }
            var result = Allocate(self);
            result.Set(jd, 0, Frac.Zero, 0, sg);
            return result;
        }

        [RubyMethod("today", RubyMethodAttributes.PublicSingleton)]
        public static RubyDate/*!*/ Today(RubyClass/*!*/ self, [DefaultParameterValue(null)]object start) {
            System.DateTime now = System.DateTime.Now;
            double sg = ToStart(start);
            var result = Allocate(self);
            result.Set(DateMath.CivilToJd(now.Year, now.Month, now.Day, sg), 0, Frac.Zero, 0, sg);
            return result;
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static RubyDate/*!*/ InitializeCopy(RubyDate/*!*/ self, [NotNull]RubyDate/*!*/ other) {
            self.Set(other._jd, other._df, other._sf, other._of, other._sg);
            return self;
        }

        #endregion

        #region validation singletons

        [RubyMethod("valid_jd?", RubyMethodAttributes.PublicSingleton)]
        public static bool ValidJd(RubyClass/*!*/ self, object jd, [DefaultParameterValue(null)]object start) {
            return jd is int || jd is BigInteger || jd is long || jd is double;
        }

        [RubyMethod("valid_civil?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("valid_date?", RubyMethodAttributes.PublicSingleton)]
        public static bool ValidCivil(RubyClass/*!*/ self, [DefaultProtocol]int year, [DefaultProtocol]int month,
            [DefaultProtocol]int day, [DefaultParameterValue(null)]object start) {
            long jd;
            return DateMath.ValidCivil(year, month, day, ToStart(start), out jd);
        }

        [RubyMethod("valid_ordinal?", RubyMethodAttributes.PublicSingleton)]
        public static bool ValidOrdinal(RubyClass/*!*/ self, [DefaultProtocol]int year, [DefaultProtocol]int yday,
            [DefaultParameterValue(null)]object start) {
            long jd;
            return DateMath.ValidOrdinal(year, yday, ToStart(start), out jd);
        }

        [RubyMethod("valid_commercial?", RubyMethodAttributes.PublicSingleton)]
        public static bool ValidCommercial(RubyClass/*!*/ self, [DefaultProtocol]int cwyear, [DefaultProtocol]int cweek,
            [DefaultProtocol]int cwday, [DefaultParameterValue(null)]object start) {
            long jd;
            return DateMath.ValidCommercial(cwyear, cweek, cwday, ToStart(start), out jd);
        }

        [RubyMethod("leap?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("gregorian_leap?", RubyMethodAttributes.PublicSingleton)]
        public static bool GregorianLeapYear(RubyClass/*!*/ self, [DefaultProtocol]int year) {
            return DateMath.GregorianLeap(year);
        }

        [RubyMethod("julian_leap?", RubyMethodAttributes.PublicSingleton)]
        public static bool JulianLeapYear(RubyClass/*!*/ self, [DefaultProtocol]int year) {
            return DateMath.JulianLeap(year);
        }

        #endregion

        #region accessors

        [RubyMethod("jd")]
        public static object GetJd(RubyDate/*!*/ self) {
            return Clr.ToRubyInteger(self._jd);
        }

        [RubyMethod("mjd")]
        public static object GetMjd(RubyDate/*!*/ self) {
            return Clr.ToRubyInteger(self._jd - 2400001L);
        }

        [RubyMethod("ld")]
        public static object GetLd(RubyDate/*!*/ self) {
            return Clr.ToRubyInteger(self._jd - 2299160L);
        }

        [RubyMethod("year")]
        public static object GetYear(RubyDate/*!*/ self) {
            long y; int m, d;
            self.GetCivil(out y, out m, out d);
            return Clr.ToRubyInteger(y);
        }

        [RubyMethod("month")]
        [RubyMethod("mon")]
        public static int GetMonth(RubyDate/*!*/ self) {
            long y; int m, d;
            self.GetCivil(out y, out m, out d);
            return m;
        }

        [RubyMethod("day")]
        [RubyMethod("mday")]
        public static int GetDay(RubyDate/*!*/ self) {
            long y; int m, d;
            self.GetCivil(out y, out m, out d);
            return d;
        }

        [RubyMethod("yday")]
        public static int GetYday(RubyDate/*!*/ self) {
            long y; int yday;
            DateMath.JdToOrdinal(self._jd, self._sg, out y, out yday);
            return yday;
        }

        [RubyMethod("wday")]
        public static int GetWday(RubyDate/*!*/ self) {
            return DateMath.JdToWday(self._jd);
        }

        [RubyMethod("cwyear")]
        public static object GetCwyear(RubyDate/*!*/ self) {
            long y; int w, d;
            DateMath.JdToCommercial(self._jd, self._sg, out y, out w, out d);
            return Clr.ToRubyInteger(y);
        }

        [RubyMethod("cweek")]
        public static int GetCweek(RubyDate/*!*/ self) {
            long y; int w, d;
            DateMath.JdToCommercial(self._jd, self._sg, out y, out w, out d);
            return w;
        }

        [RubyMethod("cwday")]
        public static int GetCwday(RubyDate/*!*/ self) {
            long y; int w, d;
            DateMath.JdToCommercial(self._jd, self._sg, out y, out w, out d);
            return d;
        }

        [RubyMethod("sunday?")]
        public static bool IsSunday(RubyDate/*!*/ self) { return DateMath.JdToWday(self._jd) == 0; }
        [RubyMethod("monday?")]
        public static bool IsMonday(RubyDate/*!*/ self) { return DateMath.JdToWday(self._jd) == 1; }
        [RubyMethod("tuesday?")]
        public static bool IsTuesday(RubyDate/*!*/ self) { return DateMath.JdToWday(self._jd) == 2; }
        [RubyMethod("wednesday?")]
        public static bool IsWednesday(RubyDate/*!*/ self) { return DateMath.JdToWday(self._jd) == 3; }
        [RubyMethod("thursday?")]
        public static bool IsThursday(RubyDate/*!*/ self) { return DateMath.JdToWday(self._jd) == 4; }
        [RubyMethod("friday?")]
        public static bool IsFriday(RubyDate/*!*/ self) { return DateMath.JdToWday(self._jd) == 5; }
        [RubyMethod("saturday?")]
        public static bool IsSaturday(RubyDate/*!*/ self) { return DateMath.JdToWday(self._jd) == 6; }

        [RubyMethod("leap?")]
        public static bool IsLeap(RubyDate/*!*/ self) {
            long y; int m, d;
            self.GetCivil(out y, out m, out d);
            return self.Julian ? DateMath.JulianLeap(y) : DateMath.GregorianLeap(y);
        }

        [RubyMethod("julian?")]
        public static bool IsJulian(RubyDate/*!*/ self) {
            return self.Julian;
        }

        [RubyMethod("gregorian?")]
        public static bool IsGregorian(RubyDate/*!*/ self) {
            return !self.Julian;
        }

        [RubyMethod("infinite?")]
        public static bool IsInfinite(RubyDate/*!*/ self) {
            return false;
        }

        [RubyMethod("start")]
        public static double GetStart(RubyDate/*!*/ self) {
            return self._sg;
        }

        [RubyMethod("new_start")]
        public static RubyDate/*!*/ NewStart(RubyDate/*!*/ self, [DefaultParameterValue(null)]object start) {
            return self.Derive(self._jd, self._df, self._sf, self._of, ToStart(start));
        }

        [RubyMethod("italy")]
        public static RubyDate/*!*/ ToItaly(RubyDate/*!*/ self) {
            return self.Derive(self._jd, self._df, self._sf, self._of, DateMath.ITALY);
        }

        [RubyMethod("england")]
        public static RubyDate/*!*/ ToEngland(RubyDate/*!*/ self) {
            return self.Derive(self._jd, self._df, self._sf, self._of, DateMath.ENGLAND);
        }

        [RubyMethod("julian")]
        public static RubyDate/*!*/ ToJulian(RubyDate/*!*/ self) {
            return self.Derive(self._jd, self._df, self._sf, self._of, DateMath.JULIAN);
        }

        [RubyMethod("gregorian")]
        public static RubyDate/*!*/ ToGregorian(RubyDate/*!*/ self) {
            return self.Derive(self._jd, self._df, self._sf, self._of, DateMath.GREGORIAN);
        }

        #endregion

        #region rational-valued accessors

        internal static object MakeRational(BinaryOpStorage/*!*/ quo, Frac f) {
            var site = quo.GetCallSite("quo");
            return site.Target(site, Clr.ToRubyInteger(f.N), Clr.ToRubyInteger(f.D));
        }

        [RubyMethod("ajd")]
        public static object GetAjd(BinaryOpStorage/*!*/ quo, RubyDate/*!*/ self) {
            return MakeRational(quo, self.Ajd);
        }

        [RubyMethod("amjd")]
        public static object GetAmjd(BinaryOpStorage/*!*/ quo, RubyDate/*!*/ self) {
            // amjd = ajd - 2400000.5
            return MakeRational(quo, Frac.Sub(self.Ajd, new Frac(4800001, 2)));
        }

        [RubyMethod("day_fraction")]
        public static object GetDayFraction(BinaryOpStorage/*!*/ quo, RubyDate/*!*/ self) {
            return MakeRational(quo, self.DayFraction);
        }

        #endregion

        #region arithmetic

        internal static bool TryToFrac(UnaryOpStorage/*!*/ toR, UnaryOpStorage/*!*/ num, UnaryOpStorage/*!*/ den,
            object value, out Frac result) {

            result = Frac.Zero;
            if (value is int) {
                result = Frac.FromInt((int)value);
                return true;
            }
            if (value is BigInteger) {
                result = Frac.FromInt((BigInteger)value);
                return true;
            }
            if (value is long) {
                result = Frac.FromInt((long)value);
                return true;
            }
            if (value is double) {
                result = Frac.FromDouble((double)value);
                return true;
            }
            if (value is float) {
                result = Frac.FromDouble((float)value);
                return true;
            }
            if (value is RubyDate || value is MutableString || value is RubySymbol || value == null) {
                return false;
            }

            // MRI's k_numeric_p: only a Numeric is taken apart through #to_r. A Time has a #to_r
            // too, and `Date.today + Time.now' is a TypeError there, not a date 56 years out.
            RubyContext context = toR.Context;
            if (!context.GetClassOf(value).HasAncestor(context.GetClass(typeof(Numeric)))) {
                return false;
            }

            object r;
            try {
                var toRSite = toR.GetCallSite("to_r");
                r = toRSite.Target(toRSite, value);
            } catch (Exception) {
                return false;
            }
            if (r == null) {
                return false;
            }

            // MRI insists that #to_r answered a Rational before it takes it apart. Asking anything
            // else for its #numerator is how a Numeric whose #to_r is `self' - test_date_arith's
            // Rat - recursed through Numeric#numerator until the CLR stack overflowed and took the
            // process down.
            if (!(r is int || r is long || r is BigInteger)) {
                RubyClass cls = context.GetClassOf(r);
                if (cls.Name != "Rational") {
                    throw RubyExceptions.CreateTypeError(String.Format(
                        "wrong argument type {0} (expected Rational)", context.GetClassDisplayName(r)));
                }
            }

            var numSite = num.GetCallSite("numerator");
            var denSite = den.GetCallSite("denominator");
            object n = numSite.Target(numSite, r);
            object d = denSite.Target(denSite, r);
            BigInteger bn, bd;
            if (!TryToBigInteger(n, out bn) || !TryToBigInteger(d, out bd)) {
                return false;
            }
            result = new Frac(bn, bd);
            return true;
        }

        internal static bool TryToBigInteger(object value, out BigInteger result) {
            if (value is int) {
                result = (int)value;
                return true;
            }
            if (value is BigInteger) {
                result = (BigInteger)value;
                return true;
            }
            if (value is long) {
                result = (long)value;
                return true;
            }
            result = BigInteger.Zero;
            return false;
        }

        /// <summary>Adds a (possibly fractional) number of days.</summary>
        internal RubyDate/*!*/ AddDays(Frac days) {
            if (days.D.IsOne && _df == 0 && _sf.IsZero) {
                return Derive(_jd + (long)days.N);
            }
            // total local seconds since the epoch of _jd
            Frac secs = Frac.Add(Frac.Add(Frac.FromInt(_df), _sf), Frac.Mul(days, DateMath.SecondsPerDay));
            BigInteger daysDelta = DateMath.FloorDiv(secs.N, secs.D * DateMath.SecondsPerDay);
            Frac rem = Frac.Sub(secs, Frac.FromInt(daysDelta * DateMath.SecondsPerDay));
            BigInteger whole = rem.Floor;
            Frac sf = Frac.Sub(rem, Frac.FromInt(whole));
            return Derive(_jd + (long)daysDelta, (int)whole, sf, _of, _sg);
        }

        [RubyMethod("+")]
        public static object Add(UnaryOpStorage/*!*/ toR, UnaryOpStorage/*!*/ num, UnaryOpStorage/*!*/ den,
            RubyDate/*!*/ self, object other) {
            Frac f;
            if (!TryToFrac(toR, num, den, other, out f)) {
                throw RubyExceptions.CreateTypeError("expected numeric");
            }
            return self.AddDays(f);
        }

        [RubyMethod("-")]
        public static object Subtract(BinaryOpStorage/*!*/ quo, UnaryOpStorage/*!*/ toR, UnaryOpStorage/*!*/ num,
            UnaryOpStorage/*!*/ den, RubyDate/*!*/ self, object other) {

            var otherDate = other as RubyDate;
            if (otherDate != null) {
                return MakeRational(quo, Frac.Sub(self.Ajd, otherDate.Ajd));
            }
            Frac f;
            if (!TryToFrac(toR, num, den, other, out f)) {
                throw RubyExceptions.CreateTypeError("expected numeric");
            }
            return self.AddDays(new Frac(-f.N, f.D));
        }

        internal RubyDate/*!*/ AddMonths(long n) {
            long y; int m, d;
            GetCivil(out y, out m, out d);
            long total = y * 12L + (m - 1) + n;
            long ny = DateMath.FloorDiv(total, 12);
            int nm = (int)DateMath.FloorMod(total, 12) + 1;
            int nd = d;
            long jd;
            while (!DateMath.ValidCivil(ny, nm, nd, _sg, out jd)) {
                nd--;
                if (nd < 1) {
                    throw InvalidDate();
                }
            }
            return Derive(jd, _df, _sf, _of, _sg);
        }

        [RubyMethod(">>")]
        public static RubyDate/*!*/ AddMonths(RubyDate/*!*/ self, [DefaultProtocol]int n) {
            return self.AddMonths(n);
        }

        [RubyMethod("<<")]
        public static RubyDate/*!*/ SubtractMonths(RubyDate/*!*/ self, [DefaultProtocol]int n) {
            return self.AddMonths(-(long)n);
        }

        [RubyMethod("next_day")]
        public static RubyDate/*!*/ NextDay(RubyDate/*!*/ self, [DefaultProtocol, DefaultParameterValue(1)]int n) {
            return self.Derive(self._jd + n);
        }

        [RubyMethod("prev_day")]
        public static RubyDate/*!*/ PrevDay(RubyDate/*!*/ self, [DefaultProtocol, DefaultParameterValue(1)]int n) {
            return self.Derive(self._jd - n);
        }

        [RubyMethod("next_month")]
        public static RubyDate/*!*/ NextMonth(RubyDate/*!*/ self, [DefaultProtocol, DefaultParameterValue(1)]int n) {
            return self.AddMonths(n);
        }

        [RubyMethod("prev_month")]
        public static RubyDate/*!*/ PrevMonth(RubyDate/*!*/ self, [DefaultProtocol, DefaultParameterValue(1)]int n) {
            return self.AddMonths(-(long)n);
        }

        [RubyMethod("next_year")]
        public static RubyDate/*!*/ NextYear(RubyDate/*!*/ self, [DefaultProtocol, DefaultParameterValue(1)]int n) {
            return self.AddMonths(12L * n);
        }

        [RubyMethod("prev_year")]
        public static RubyDate/*!*/ PrevYear(RubyDate/*!*/ self, [DefaultProtocol, DefaultParameterValue(1)]int n) {
            return self.AddMonths(-12L * n);
        }

        [RubyMethod("succ")]
        [RubyMethod("next")]
        public static RubyDate/*!*/ Succ(RubyDate/*!*/ self) {
            return self.Derive(self._jd + 1);
        }

        #endregion

        #region iteration

        [RubyMethod("step")]
        public static object Step(BlockParam block, RubyDate/*!*/ self, object limit,
            [DefaultProtocol, DefaultParameterValue(1)]int step) {

            if (step == 0) {
                throw RubyExceptions.CreateArgumentError("step can't be 0");
            }
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("no block given");
            }
            var limitDate = limit as RubyDate;
            long limitJd = limitDate != null ? limitDate._jd : ToLong(limit);
            long jd = self._jd;
            while (step > 0 ? jd <= limitJd : jd >= limitJd) {
                object result;
                if (block.Yield(self.Derive(jd), out result)) {
                    return result;
                }
                jd += step;
            }
            return self;
        }

        [RubyMethod("upto")]
        public static object UpTo(BlockParam block, RubyDate/*!*/ self, object limit) {
            return Step(block, self, limit, 1);
        }

        [RubyMethod("downto")]
        public static object DownTo(BlockParam block, RubyDate/*!*/ self, object limit) {
            return Step(block, self, limit, -1);
        }

        #endregion

        #region comparison

        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage,
            BinaryOpStorage/*!*/ quo, RubyDate/*!*/ self, object other) {
            var otherDate = other as RubyDate;
            if (otherDate != null) {
                return ScriptingRuntimeHelpers.Int32ToObject(self.Ajd.CompareTo(otherDate.Ajd));
            }
            BigInteger bi;
            if (TryToBigInteger(other, out bi)) {
                return ScriptingRuntimeHelpers.Int32ToObject(self.Ajd.CompareTo(Frac.FromInt(bi)));
            }
            if (other is double) {
                double d = (double)other;
                if (Double.IsNaN(d)) {
                    return null;
                }
                return ScriptingRuntimeHelpers.Int32ToObject(self.Ajd.ToDouble().CompareTo(d));
            }
            // cmp_gen: any other Numeric (a Rational, a BigDecimal) compares with the ajd.
            RubyContext context = comparisonStorage.Context;
            if (context.IsKindOf(other, context.GetClass(typeof(IronRuby.Builtins.Numeric)))) {
                var site = comparisonStorage.GetCallSite("<=>");
                return site.Target(site, MakeRational(quo, self.Ajd), other);
            }
            // ... and it ends in rb_num_coerce_cmp: anything else is asked to
            // #coerce.  ActiveSupport::Duration answers that way, so `time_with_zone <= 1.hour`
            // (through DateTime#<=>) compares instead of failing.
            return Protocols.CoerceAndCompare(coercionStorage, comparisonStorage, self, other);
        }

        [RubyMethod("===")]
        public static object CaseCompare(RubyDate/*!*/ self, object other) {
            var otherDate = other as RubyDate;
            if (otherDate != null) {
                return ScriptingRuntimeHelpers.BooleanToObject(self._jd == otherDate._jd);
            }
            BigInteger bi;
            if (TryToBigInteger(other, out bi)) {
                return ScriptingRuntimeHelpers.BooleanToObject(bi == self._jd);
            }
            return null;
        }

        [RubyMethod("eql?")]
        public static bool Eql(RubyDate/*!*/ self, object other) {
            var otherDate = other as RubyDate;
            if (otherDate == null) {
                return false;
            }
            return self.Ajd.CompareTo(otherDate.Ajd) == 0;
        }

        [RubyMethod("hash")]
        public static int GetHash(RubyDate/*!*/ self) {
            Frac a = self.Ajd;
            return a.N.GetHashCode() ^ a.D.GetHashCode();
        }

        #endregion

        #region conversions

        [RubyMethod("to_date")]
        public static RubyDate/*!*/ ToDate(RubyContext/*!*/ context, RubyDate/*!*/ self) {
            if (self.GetType() == typeof(RubyDate)) {
                return self;
            }
            var cls = context.GetClass(typeof(RubyDate));
            var result = new RubyDate(cls);
            result.Set(self._jd, 0, Frac.Zero, 0, self._sg);
            return result;
        }

        [RubyMethod("to_datetime")]
        public static RubyDate/*!*/ ToDateTime(RubyContext/*!*/ context, RubyDate/*!*/ self) {
            if (self is RubyDateTime) {
                return self;
            }
            var cls = context.GetClass(typeof(RubyDateTime));
            var result = new RubyDateTime(cls);
            result.Set(self._jd, self._df, self._sf, self._of, self._sg);
            return result;
        }

        [RubyMethod("deconstruct_keys")]
        public static Hash/*!*/ DeconstructKeys(RubyContext/*!*/ context, RubyDate/*!*/ self, object keys) {
            var hash = new Hash(context);
            IList wanted = CheckDeconstructKeys(context, keys);
            long y; int m, d;
            self.GetCivil(out y, out m, out d);
            AddKey(context, hash, wanted, "year", Clr.ToRubyInteger(y));
            AddKey(context, hash, wanted, "month", ScriptingRuntimeHelpers.Int32ToObject(m));
            AddKey(context, hash, wanted, "day", ScriptingRuntimeHelpers.Int32ToObject(d));
            AddKey(context, hash, wanted, "yday", ScriptingRuntimeHelpers.Int32ToObject(GetYday(self)));
            AddKey(context, hash, wanted, "wday", ScriptingRuntimeHelpers.Int32ToObject(DateMath.JdToWday(self._jd)));
            return hash;
        }

        internal static IList CheckDeconstructKeys(RubyContext/*!*/ context, object keys) {
            if (keys != null && !(keys is IList)) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Array or nil)",
                    context.GetClassDisplayName(keys));
            }
            return keys as IList;
        }

        internal static void AddKey(RubyContext/*!*/ context, Hash/*!*/ hash, IList wanted, string/*!*/ name, object value) {
            RubySymbol symbol = context.CreateAsciiSymbol(name);
            if (wanted != null) {
                bool found = false;
                foreach (object k in wanted) {
                    var s = k as RubySymbol;
                    if (s != null && s.ToString() == name) {
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    return;
                }
            }
            hash[symbol] = value;
        }

        [RubyMethod("marshal_dump")]
        public static RubyArray/*!*/ MarshalDump(BinaryOpStorage/*!*/ quo, RubyDate/*!*/ self) {
            var result = new RubyArray(6);
            result.Add(ScriptingRuntimeHelpers.Int32ToObject(0));
            result.Add(Clr.ToRubyInteger(self._jd));
            result.Add(ScriptingRuntimeHelpers.Int32ToObject(self._df));
            // MRI stores the sub-second part as a count of nanoseconds
            Frac ns = Frac.Mul(self._sf, 1000000000);
            result.Add(ns.D.IsOne ? Clr.ToRubyInteger(ns.N) : MakeRational(quo, ns));
            result.Add(ScriptingRuntimeHelpers.Int32ToObject(self._of));
            result.Add(self._sg);
            return result;
        }

        [RubyMethod("marshal_load")]
        public static RubyDate/*!*/ MarshalLoad(UnaryOpStorage/*!*/ toR, UnaryOpStorage/*!*/ num, UnaryOpStorage/*!*/ den,
            RubyDate/*!*/ self, [NotNull]IList/*!*/ data) {
            if (data.Count < 6) {
                throw RubyExceptions.CreateTypeError("invalid marshalled date");
            }
            Frac sf;
            if (!TryToFrac(toR, num, den, data[3], out sf)) {
                sf = Frac.Zero;
            } else {
                sf = new Frac(sf.N, sf.D * 1000000000);
            }
            self.Set(ToLong(data[1]), (int)ToLong(data[2]), sf, (int)ToLong(data[4]), ToStart(data[5]));
            return self;
        }

        #endregion

        #region formatting

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS(RubyDate/*!*/ self) {
            return MutableString.CreateAscii(DateFormatter.Format(self, "%Y-%m-%d"));
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyDate/*!*/ self) {
            var sb = new StringBuilder();
            sb.Append("#<").Append(context.GetClassDisplayName(self)).Append(": ");
            sb.Append(DateFormatter.Format(self, self is RubyDateTime ? "%Y-%m-%dT%H:%M:%S%:z" : "%Y-%m-%d"));
            // The triple MRI prints is its own internal representation, which counts
            // the day and the seconds in *UTC* and keeps the offset beside them.  The
            // fields here are local, so they are shifted back before printing -
            // everything else about a DateTime, #jd and #hour included, is local on
            // both.
            long inspectJd = self._jd;
            int inspectDf = self._df - self._of;
            if (inspectDf < 0) {
                inspectDf += 86400;
                inspectJd -= 1;
            } else if (inspectDf >= 86400) {
                inspectDf -= 86400;
                inspectJd += 1;
            }
            sb.Append(" ((").Append(inspectJd.ToString(CultureInfo.InvariantCulture)).Append("j,");
            sb.Append(inspectDf.ToString(CultureInfo.InvariantCulture)).Append("s,");
            sb.Append(DateFormatter.NanosecondString(self._sf)).Append("n),");
            sb.Append(self._of >= 0 ? "+" : "").Append(self._of.ToString(CultureInfo.InvariantCulture)).Append("s,");
            if (Double.IsPositiveInfinity(self._sg)) {
                sb.Append("Infj");
            } else if (Double.IsNegativeInfinity(self._sg)) {
                sb.Append("-Infj");
            } else {
                sb.Append(((long)self._sg).ToString(CultureInfo.InvariantCulture)).Append('j');
            }
            sb.Append(")>");
            return MutableString.Create(sb.ToString(), RubyEncoding.UTF8);
        }

        [RubyMethod("strftime")]
        public static MutableString/*!*/ Strftime(RubyDate/*!*/ self,
            [DefaultProtocol, DefaultParameterValue(null)]MutableString format) {
            if (format == null) {
                return MutableString.CreateAscii(DateFormatter.Format(self, "%F"));
            }
            // the result carries the format string's encoding, and may well not be ASCII
            return MutableString.Create(DateFormatter.Format(self, format.ConvertToString()), format.Encoding);
        }

        [RubyMethod("asctime")]
        [RubyMethod("ctime")]
        public static MutableString/*!*/ AscTime(RubyDate/*!*/ self) {
            return MutableString.CreateAscii(DateFormatter.Format(self, "%a %b %e %H:%M:%S %Y"));
        }

        [RubyMethod("iso8601")]
        [RubyMethod("xmlschema")]
        [RubyMethod("rfc3339")]
        public static MutableString/*!*/ Iso8601(RubyDate/*!*/ self) {
            return MutableString.CreateAscii(DateFormatter.Format(self, "%Y-%m-%d"));
        }

        [RubyMethod("rfc2822")]
        [RubyMethod("rfc822")]
        public static MutableString/*!*/ Rfc2822(RubyDate/*!*/ self) {
            return MutableString.CreateAscii(DateFormatter.Format(self, "%a, %-d %b %Y %T %z"));
        }

        [RubyMethod("httpdate")]
        public static MutableString/*!*/ HttpDate(RubyDate/*!*/ self) {
            RubyDate utc = self;
            if (self._of != 0) {
                utc = self.AddDays(new Frac(-self._of, DateMath.SecondsPerDay));
                utc._of = 0;
            }
            return MutableString.CreateAscii(DateFormatter.Format(utc, "%a, %d %b %Y %T GMT"));
        }

        [RubyMethod("jisx0301")]
        public static MutableString/*!*/ Jisx0301(RubyDate/*!*/ self) {
            return MutableString.CreateAscii(DateFormatter.Jisx0301(self));
        }

        #endregion
    }

    /// <summary>Small helpers shared by the Date classes.</summary>
    internal static class Clr {
        internal static object ToRubyInteger(long value) {
            if (value >= Int32.MinValue && value <= Int32.MaxValue) {
                return ScriptingRuntimeHelpers.Int32ToObject((int)value);
            }
            return (BigInteger)value;
        }

        internal static object ToRubyInteger(BigInteger value) {
            if (value >= Int32.MinValue && value <= Int32.MaxValue) {
                return ScriptingRuntimeHelpers.Int32ToObject((int)value);
            }
            return value;
        }
    }
}

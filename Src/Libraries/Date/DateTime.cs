/* ****************************************************************************
 *
 * DateTime for IronRuby - Date plus a time of day and a UTC offset.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Date {

    [RubyClass("DateTime")]
    public class RubyDateTime : RubyDate {

        public RubyDateTime(RubyClass/*!*/ cls)
            : base(cls) {
        }

        protected RubyDateTime(SerializationInfo/*!*/ info, StreamingContext context)
            : base(info, context) {
        }

        protected override RubyObject/*!*/ CreateInstance() {
            return new RubyDateTime(ImmediateClass.NominalClass);
        }

        #region offset handling

        /// <summary>
        /// Parses the "of" argument: a zone string ("+09:00", "UTC", "Z", ...) or a
        /// numeric offset expressed in *days* (MRI uses Rational(9,24) for +09:00).
        /// Returns the offset in seconds.
        /// </summary>
        internal static int ToOffsetSeconds(ConversionStorage<double>/*!*/ toF, object of) {
            if (of == null) {
                return 0;
            }
            var str = of as MutableString;
            if (str != null) {
                int seconds;
                if (!TryParseZone(str.ConvertToString(), out seconds)) {
                    throw InvalidDate();
                }
                return seconds;
            }
            if (of is int) {
                return (int)of * DateMath.SecondsPerDay;
            }
            double days = Protocols.CastToFloat(toF, of);
            return (int)Math.Round(days * DateMath.SecondsPerDay);
        }

        internal static bool TryParseZone(string/*!*/ zone, out int seconds) {
            seconds = 0;
            if (zone.Length == 0) {
                return false;
            }
            string z = zone.Trim();
            if (z.Length == 0) {
                return false;
            }
            if (z == "Z" || z == "z" || String.Equals(z, "UTC", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(z, "GMT", StringComparison.OrdinalIgnoreCase) || String.Equals(z, "UT", StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            int sign = 1;
            int i = 0;
            if (z[0] == '+' || z[0] == '-') {
                sign = z[0] == '-' ? -1 : 1;
                i = 1;
            } else {
                int named;
                if (TryNamedZone(z, out named)) {
                    seconds = named;
                    return true;
                }
                return false;
            }

            string rest = z.Substring(i);
            int h = 0, m = 0, s = 0;
            string[] parts = rest.Split(':');
            if (parts.Length > 1) {
                if (!Int32.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out h)) {
                    return false;
                }
                if (parts.Length > 1 && parts[1].Length > 0 && !Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out m)) {
                    return false;
                }
                if (parts.Length > 2 && parts[2].Length > 0 && !Int32.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out s)) {
                    return false;
                }
            } else {
                if (rest.Length <= 2) {
                    if (!Int32.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out h)) {
                        return false;
                    }
                } else if (rest.Length <= 4) {
                    if (!Int32.TryParse(rest.Substring(0, rest.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out h) ||
                        !Int32.TryParse(rest.Substring(rest.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out m)) {
                        return false;
                    }
                } else if (rest.Length == 6) {
                    if (!Int32.TryParse(rest.Substring(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out h) ||
                        !Int32.TryParse(rest.Substring(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out m) ||
                        !Int32.TryParse(rest.Substring(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out s)) {
                        return false;
                    }
                } else {
                    return false;
                }
            }
            seconds = sign * (h * 3600 + m * 60 + s);
            return true;
        }

        private static readonly string[] _ZoneNames = {
            "UTC", "UT", "GMT", "EST", "EDT", "CST", "CDT", "MST", "MDT", "PST", "PDT",
            "A", "B", "C", "D", "E", "F", "G", "H", "I", "K", "L", "M",
            "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"
        };

        private static readonly int[] _ZoneOffsets = {
            0, 0, 0, -5, -4, -6, -5, -7, -6, -8, -7,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12,
            -1, -2, -3, -4, -5, -6, -7, -8, -9, -10, -11, -12, 0
        };

        private static bool TryNamedZone(string/*!*/ z, out int seconds) {
            for (int i = 0; i < _ZoneNames.Length; i++) {
                if (String.Equals(_ZoneNames[i], z, StringComparison.OrdinalIgnoreCase)) {
                    seconds = _ZoneOffsets[i] * 3600;
                    return true;
                }
            }
            seconds = 0;
            return false;
        }

        #endregion

        #region construction

        /// <summary>
        /// MRI's num2int_with_frac: a field may only carry a fraction if it is the
        /// last one supplied; otherwise Date::Error("invalid fraction") is raised.
        /// The fraction is carried into the time of day at <paramref name="scale"/>
        /// seconds per unit.
        /// </summary>
        private static long SplitField(UnaryOpStorage/*!*/ toR, UnaryOpStorage/*!*/ num, UnaryOpStorage/*!*/ den,
            object value, long defaultValue, bool laterFieldGiven, long scale, ref Frac carry, string/*!*/ field) {

            if (value == null) {
                return defaultValue;
            }
            Frac f;
            if (!TryToFrac(toR, num, den, value, out f)) {
                throw RubyExceptions.CreateTypeError(String.Format("invalid {0} (not numeric)", field));
            }
            // MRI's *_trunc uses floor division, so the fraction is always in [0,1).
            BigInteger whole = f.Floor;
            Frac fraction = Frac.Sub(f, Frac.FromInt(whole));
            if (!fraction.IsZero) {
                if (laterFieldGiven) {
                    throw new DateError("invalid fraction");
                }
                carry = Frac.Add(carry, Frac.Mul(fraction, scale));
            }
            return (long)whole;
        }

        internal static RubyDate/*!*/ CreateDateTime(RubyClass/*!*/ cls, long y, int m, int d,
            int hour, int minute, Frac second, int offsetSeconds, double sg) {

            long jd;
            if (!DateMath.ValidCivil(y, m, d, sg, out jd)) {
                throw InvalidDate();
            }
            if (hour < 0) {
                hour += 24;
            }
            if (minute < 0) {
                minute += 60;
            }
            BigInteger sWhole = second.Floor;
            Frac sFrac = Frac.Sub(second, Frac.FromInt(sWhole));
            int sec = (int)sWhole;
            if (sec < 0) {
                sec += 60;
            }
            if (hour == 24 && minute == 0 && sec == 0 && sFrac.IsZero) {
                // 24:00:00 is the midnight that starts the next day
                jd += 1;
                hour = 0;
            }
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || sec < 0 || sec > 59) {
                throw InvalidDate();
            }
            var result = Allocate(cls);
            result.Set(jd, hour * 3600 + minute * 60 + sec, sFrac, offsetSeconds, sg);
            return result;
        }

        [RubyConstructor]
        public static RubyDate/*!*/ Create(ConversionStorage<double>/*!*/ toF, UnaryOpStorage/*!*/ toR,
            UnaryOpStorage/*!*/ num, UnaryOpStorage/*!*/ den, RubyClass/*!*/ self,
            [DefaultParameterValue(null)]object year,
            [DefaultParameterValue(null)]object month,
            [DefaultParameterValue(null)]object day,
            [DefaultParameterValue(null)]object hour,
            [DefaultParameterValue(null)]object minute,
            [DefaultParameterValue(null)]object second,
            [DefaultParameterValue(null)]object of,
            [DefaultParameterValue(null)]object start) {

            Frac carry = Frac.Zero;
            bool haveSecond = second != null;
            bool haveMinute = minute != null || haveSecond;
            bool haveHour = hour != null || haveMinute;
            bool haveDay = day != null || haveHour;

            long y = SplitField(toR, num, den, year, -4712, day != null || haveDay || month != null, 0, ref carry, "year");
            long m = SplitField(toR, num, den, month, 1, haveDay, 0, ref carry, "month");
            long d = SplitField(toR, num, den, day, 1, haveHour, DateMath.SecondsPerDay, ref carry, "day");
            long h = SplitField(toR, num, den, hour, 0, haveMinute, 3600, ref carry, "hour");
            long min = SplitField(toR, num, den, minute, 0, haveSecond, 60, ref carry, "minute");
            long s = SplitField(toR, num, den, second, 0, false, 1, ref carry, "second");

            RubyDate result = CreateDateTime(self, y, (int)m, (int)d, (int)h, (int)min, Frac.FromInt(s),
                ToOffsetSeconds(toF, of), ToStart(start));
            if (!carry.IsZero) {
                result = result.AddDays(new Frac(carry.N, carry.D * DateMath.SecondsPerDay));
            }
            return result;
        }

        [RubyMethod("civil", RubyMethodAttributes.PublicSingleton)]
        public static RubyDate/*!*/ Civil(ConversionStorage<double>/*!*/ toF, UnaryOpStorage/*!*/ toR,
            UnaryOpStorage/*!*/ num, UnaryOpStorage/*!*/ den, RubyClass/*!*/ self,
            [DefaultParameterValue(null)]object year,
            [DefaultParameterValue(null)]object month,
            [DefaultParameterValue(null)]object day,
            [DefaultParameterValue(null)]object hour,
            [DefaultParameterValue(null)]object minute,
            [DefaultParameterValue(null)]object second,
            [DefaultParameterValue(null)]object of,
            [DefaultParameterValue(null)]object start) {

            return Create(toF, toR, num, den, self, year, month, day, hour, minute, second, of, start);
        }

        [RubyMethod("jd", RubyMethodAttributes.PublicSingleton)]
        public static RubyDate/*!*/ FromJd(ConversionStorage<double>/*!*/ toF, UnaryOpStorage/*!*/ toR,
            UnaryOpStorage/*!*/ num, UnaryOpStorage/*!*/ den, RubyClass/*!*/ self,
            [DefaultParameterValue(null)]object jd,
            [DefaultProtocol, DefaultParameterValue(0)]int hour,
            [DefaultProtocol, DefaultParameterValue(0)]int minute,
            [DefaultParameterValue(null)]object second,
            [DefaultParameterValue(null)]object of,
            [DefaultParameterValue(null)]object start) {

            Frac sec;
            if (second == null) {
                sec = Frac.Zero;
            } else if (!TryToFrac(toR, num, den, second, out sec)) {
                throw RubyExceptions.CreateTypeError("expected numeric");
            }
            long j = jd == null ? 0 : ToLong(jd);
            BigInteger sWhole = sec.Floor;
            Frac sFrac = Frac.Sub(sec, Frac.FromInt(sWhole));
            var result = Allocate(self);
            result.Set(j, hour * 3600 + minute * 60 + (int)sWhole, sFrac, ToOffsetSeconds(toF, of), ToStart(start));
            return result;
        }

        [RubyMethod("ordinal", RubyMethodAttributes.PublicSingleton)]
        public static RubyDate/*!*/ Ordinal(ConversionStorage<double>/*!*/ toF, UnaryOpStorage/*!*/ toR,
            UnaryOpStorage/*!*/ num, UnaryOpStorage/*!*/ den, RubyClass/*!*/ self,
            [DefaultProtocol, DefaultParameterValue(-4712)]int year,
            [DefaultProtocol, DefaultParameterValue(1)]int yday,
            [DefaultProtocol, DefaultParameterValue(0)]int hour,
            [DefaultProtocol, DefaultParameterValue(0)]int minute,
            [DefaultParameterValue(null)]object second,
            [DefaultParameterValue(null)]object of,
            [DefaultParameterValue(null)]object start) {

            double sg = ToStart(start);
            long jd;
            if (!DateMath.ValidOrdinal(year, yday, sg, out jd)) {
                throw InvalidDate();
            }
            Frac sec;
            if (second == null) {
                sec = Frac.Zero;
            } else if (!TryToFrac(toR, num, den, second, out sec)) {
                throw RubyExceptions.CreateTypeError("expected numeric");
            }
            BigInteger sWhole = sec.Floor;
            var result = Allocate(self);
            result.Set(jd, hour * 3600 + minute * 60 + (int)sWhole, Frac.Sub(sec, Frac.FromInt(sWhole)),
                ToOffsetSeconds(toF, of), sg);
            return result;
        }

        [RubyMethod("commercial", RubyMethodAttributes.PublicSingleton)]
        public static RubyDate/*!*/ Commercial(ConversionStorage<double>/*!*/ toF, UnaryOpStorage/*!*/ toR,
            UnaryOpStorage/*!*/ num, UnaryOpStorage/*!*/ den, RubyClass/*!*/ self,
            [DefaultProtocol, DefaultParameterValue(-4712)]int cwyear,
            [DefaultProtocol, DefaultParameterValue(1)]int cweek,
            [DefaultProtocol, DefaultParameterValue(1)]int cwday,
            [DefaultProtocol, DefaultParameterValue(0)]int hour,
            [DefaultProtocol, DefaultParameterValue(0)]int minute,
            [DefaultParameterValue(null)]object second,
            [DefaultParameterValue(null)]object of,
            [DefaultParameterValue(null)]object start) {

            double sg = ToStart(start);
            long jd;
            if (!DateMath.ValidCommercial(cwyear, cweek, cwday, sg, out jd)) {
                throw InvalidDate();
            }
            Frac sec;
            if (second == null) {
                sec = Frac.Zero;
            } else if (!TryToFrac(toR, num, den, second, out sec)) {
                throw RubyExceptions.CreateTypeError("expected numeric");
            }
            BigInteger sWhole = sec.Floor;
            var result = Allocate(self);
            result.Set(jd, hour * 3600 + minute * 60 + (int)sWhole, Frac.Sub(sec, Frac.FromInt(sWhole)),
                ToOffsetSeconds(toF, of), sg);
            return result;
        }

        [RubyMethod("now", RubyMethodAttributes.PublicSingleton)]
        public static RubyDate/*!*/ Now(RubyClass/*!*/ self, [DefaultParameterValue(null)]object start) {
            double sg = ToStart(start);
            // go through RubyTime so that Date and Time agree about the local zone
            var localNow = new RubyTime(RubyTime.GetCurrentLocalTime());
            System.DateTime now = localNow.DateTime;
            TimeSpan offset = localNow.GetCurrentZoneOffset();
            long jd = DateMath.CivilToJd(now.Year, now.Month, now.Day, sg);
            long subTicks = now.Ticks % TimeSpan.TicksPerSecond;
            var result = Allocate(self);
            result.Set(jd, now.Hour * 3600 + now.Minute * 60 + now.Second,
                new Frac(subTicks, TimeSpan.TicksPerSecond), (int)offset.TotalSeconds, sg);
            return result;
        }

        #endregion

        #region time accessors

        [RubyMethod("hour")]
        public static int GetHour(RubyDate/*!*/ self) {
            return self._df / 3600;
        }

        [RubyMethod("min")]
        [RubyMethod("minute")]
        public static int GetMinute(RubyDate/*!*/ self) {
            return (self._df % 3600) / 60;
        }

        [RubyMethod("sec")]
        [RubyMethod("second")]
        public static int GetSecond(RubyDate/*!*/ self) {
            return self._df % 60;
        }

        [RubyMethod("sec_fraction")]
        [RubyMethod("second_fraction")]
        public static object GetSecondFraction(BinaryOpStorage/*!*/ quo, RubyDate/*!*/ self) {
            return MakeRational(quo, self._sf);
        }

        [RubyMethod("offset")]
        public static object GetOffset(BinaryOpStorage/*!*/ quo, RubyDate/*!*/ self) {
            return MakeRational(quo, new Frac(self._of, DateMath.SecondsPerDay));
        }

        [RubyMethod("zone")]
        public static MutableString/*!*/ GetZone(RubyDate/*!*/ self) {
            return MutableString.CreateAscii(DateFormatter.FormatOffset(self._of, ":"));
        }

        [RubyMethod("deconstruct_keys")]
        public static Hash/*!*/ DeconstructKeys(BinaryOpStorage/*!*/ quo, RubyContext/*!*/ context,
            RubyDate/*!*/ self, object keys) {

            Hash hash = RubyDate.DeconstructKeys(context, self, keys);
            IList wanted = CheckDeconstructKeys(context, keys);
            AddKey(context, hash, wanted, "hour", ScriptingRuntimeHelpers.Int32ToObject(GetHour(self)));
            AddKey(context, hash, wanted, "min", ScriptingRuntimeHelpers.Int32ToObject(GetMinute(self)));
            AddKey(context, hash, wanted, "sec", ScriptingRuntimeHelpers.Int32ToObject(GetSecond(self)));
            AddKey(context, hash, wanted, "sec_fraction", MakeRational(quo, self._sf));
            AddKey(context, hash, wanted, "zone", GetZone(self));
            return hash;
        }

        [RubyMethod("new_offset")]
        public static RubyDate/*!*/ NewOffset(ConversionStorage<double>/*!*/ toF, RubyDate/*!*/ self, [DefaultParameterValue(null)]object of) {
            int newOf = ToOffsetSeconds(toF, of);
            RubyDate shifted = self.AddDays(new Frac(newOf - self._of, DateMath.SecondsPerDay));
            shifted._of = newOf;
            return shifted;
        }

        #endregion

        #region formatting

        [RubyMethod("to_s")]
        public static new MutableString/*!*/ ToS(RubyDate/*!*/ self) {
            return MutableString.CreateAscii(DateFormatter.Format(self, "%Y-%m-%dT%H:%M:%S%:z"));
        }

        /// <summary>DateTime#strftime defaults to the full ISO form, not Date's "%F".</summary>
        [RubyMethod("strftime")]
        public static new MutableString/*!*/ Strftime(RubyDate/*!*/ self,
            [DefaultProtocol, DefaultParameterValue(null)]MutableString format) {
            if (format == null) {
                return ToS(self);
            }
            return MutableString.Create(DateFormatter.Format(self, format.ConvertToString()), format.Encoding);
        }

        [RubyMethod("iso8601")]
        [RubyMethod("xmlschema")]
        [RubyMethod("rfc3339")]
        public static MutableString/*!*/ Iso8601(RubyDate/*!*/ self, [DefaultProtocol, DefaultParameterValue(0)]int n) {
            string fractional = n > 0 ? "." + DateFormatter.FractionDigits(self._sf, n) : "";
            return MutableString.CreateAscii(DateFormatter.Format(self, "%Y-%m-%dT%H:%M:%S") + fractional +
                DateFormatter.FormatOffset(self._of, ":"));
        }

        [RubyMethod("jisx0301")]
        public static MutableString/*!*/ Jisx0301(RubyDate/*!*/ self, [DefaultProtocol, DefaultParameterValue(0)]int n) {
            string fractional = n > 0 ? "." + DateFormatter.FractionDigits(self._sf, n) : "";
            return MutableString.CreateAscii(DateFormatter.Jisx0301(self) + "T" +
                DateFormatter.Format(self, "%H:%M:%S") + fractional + DateFormatter.FormatOffset(self._of, ":"));
        }

        #endregion
    }
}

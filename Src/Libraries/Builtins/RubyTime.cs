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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace IronRuby.Builtins {
    #region RubyTime

    public enum RubyTimeZoneKind {
        Local = 0,
        Utc = 1,
        FixedOffset = 2,
    }

    /// <summary>
    /// Ruby's Time. The instant is kept as an exact number of seconds since the epoch
    /// (whole seconds plus a Rational sub-second part) rather than as a .NET DateTime,
    /// because DateTime only has 100ns resolution while Ruby keeps arbitrary precision
    /// (Time.at(Rational(1, 3)).subsec == (1/3)). A DateTime is derived on demand for
    /// the calendar fields and for formatting.
    /// </summary>
    public class RubyTime : IComparable, IComparable<RubyTime>, IEquatable<RubyTime>, IFormattable {
        public readonly static DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc); //January 1, 1970 00:00 UTC

        internal const long TicksPerSecond = TimeSpan.TicksPerSecond;
        internal static readonly BigInteger NanosecondsPerSecond = new BigInteger(1000000000);
        internal static readonly BigInteger MicrosecondsPerSecond = new BigInteger(1000000);
        internal static readonly BigInteger TicksPerSecondBig = new BigInteger(TicksPerSecond);

        // Seconds since the epoch that the .NET DateTime range can represent.
        private const long MinRepresentableSeconds = -62135596800L; // 0001-01-01T00:00:00Z
        private const long MaxRepresentableSeconds = 253402300799L; // 9999-12-31T23:59:59Z

        #region Time Zones

        internal static RubyTimeZone/*!*/ _CurrentTimeZone;
        private static Regex _tzPattern;

        static RubyTime() {
            string tz;
            try {
                tz = Environment.GetEnvironmentVariable("TZ");
            } catch (SecurityException) {
                tz = null;
            }
            RubyTimeZone zone;
            RubyTime.TryParseTimeZone(tz, out zone);
            RubyTime._CurrentTimeZone = zone ?? RubyTimeZone.Machine();
        }

        /// <summary>
        /// Accepts both Olson database names ("Europe/Amsterdam") and the POSIX TZ form
        /// mspec's with_timezone produces ("PST8:00:00", "JST-9"). Returns false for a
        /// specification we cannot make sense of, which Ruby answers with UTC.
        /// </summary>
        public static bool TryParseTimeZone(string timeZoneEnvSpec, out RubyTimeZone timeZone) {
            if (String.IsNullOrEmpty(timeZoneEnvSpec)) {
                timeZone = RubyTimeZone.Machine();
                return true;
            }

            string id = timeZoneEnvSpec;
            if (id[0] == ':') {
                id = id.Substring(1);
            }

            timeZone = RubyTimeZone.FromId(id);
            if (timeZone != null) {
                return true;
            }

            if (_tzPattern == null) {
                // std offset [dst [offset] [,start[/time],end[/time]]]; we require the offset
                _tzPattern = new Regex(@"^\s*
                    (?<std>[^-+:,0-9\0]{3,})
                    (?<sign>[+-]?)(?<sh>[0-9]{1,2})((:(?<sm>[0-9]{1,2}))?(:(?<ss>[0-9]{1,2}))?)?
                    (?<dst>[A-Za-z]{3,})?
                    (?<doff>[+-]?[0-9]{1,2}(:[0-9]{1,2}){0,2})?
                    (,(?<start>[^,]+),(?<end>[^,]+))?
                    ",
                    RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant
                );
            }

            Match match = _tzPattern.Match(timeZoneEnvSpec);
            if (!match.Success) {
                timeZone = null;
                return false;
            }

            int h = Int32.Parse(match.Groups["sh"].Value, CultureInfo.InvariantCulture);
            int m = match.Groups["sm"].Success ? Int32.Parse(match.Groups["sm"].Value, CultureInfo.InvariantCulture) : 0;
            int sec = match.Groups["ss"].Success ? Int32.Parse(match.Groups["ss"].Value, CultureInfo.InvariantCulture) : 0;

            // The POSIX convention is reversed: the value is what must be added to local time to get UTC.
            long totalSeconds = (long)h * 3600 + m * 60 + sec;
            if (match.Groups["sign"].Value == "-") {
                totalSeconds = -totalSeconds;
            }
            totalSeconds = -totalSeconds;

            if (totalSeconds <= -24 * 3600 || totalSeconds >= 24 * 3600) {
                timeZone = null;
                return false;
            }

            if (match.Groups["dst"].Success) {
                // daylight saving time is an hour ahead of standard time unless it says otherwise
                long dstSeconds = match.Groups["doff"].Success ? -TzFile.ParsePosixTime(match.Groups["doff"].Value) : totalSeconds + 3600;
                try {
                    timeZone = RubyTimeZone.FromPosixRule(match.Groups["std"].Value, totalSeconds, match.Groups["dst"].Value, dstSeconds,
                        match.Groups["start"].Success ? match.Groups["start"].Value : null,
                        match.Groups["end"].Success ? match.Groups["end"].Value : null);
                    return true;
                } catch (FormatException) {
                } catch (ArgumentException) {
                } catch (IndexOutOfRangeException) {
                }
            }

            timeZone = RubyTimeZone.MakeFixed(totalSeconds, match.Groups["std"].Value);
            return true;
        }

        public TimeSpan GetCurrentZoneOffset() {
            return TimeSpan.FromSeconds((double)UtcOffsetSeconds);
        }

        public static string GetCurrentZoneName() {
            return _CurrentTimeZone.GetAbbreviation((long)(DateTime.UtcNow - Epoch).TotalSeconds);
        }

        public bool GetCurrentDst(RubyContext/*!*/ context) {
            return IsDaylightSavingTime;
        }

        public static DateTime ToUniversalTime(DateTime dateTime) {
            if (dateTime.Kind == DateTimeKind.Utc) {
                return dateTime;
            }
            long offset = _CurrentTimeZone.GetOffsetForWallClock(SecondsFromDateTimeTicks(dateTime.Ticks));
            return DateTime.SpecifyKind(dateTime.AddTicks(-offset * TicksPerSecond), DateTimeKind.Utc);
        }

        public static DateTime ToLocalTime(DateTime dateTime) {
            if (dateTime.Kind == DateTimeKind.Local) {
                return dateTime;
            }
            long offset = _CurrentTimeZone.GetOffset(SecondsFromDateTimeTicks(dateTime.Ticks));
            return DateTime.SpecifyKind(dateTime.AddTicks(offset * TicksPerSecond), DateTimeKind.Local);
        }

        public DateTime ToUniversalTime() {
            return UtcDateTime;
        }

        public DateTime ToLocalTime() {
            long offset = _CurrentTimeZone.GetOffset(_seconds);
            return ToDateTimeClamped(_seconds + offset, _subsec, DateTimeKind.Local);
        }

        public static DateTime GetCurrentLocalTime() {
            return ToLocalTime(DateTime.UtcNow);
        }

        #endregion

        #region State

        private long _seconds;              // whole seconds since the epoch, UTC (floor)
        private ExactNum _subsec;           // 0 <= _subsec < 1
        private RubyTimeZoneKind _zoneKind;
        private ExactNum _fixedOffset;      // seconds, only meaningful when _zoneKind == FixedOffset
        private object _zoneObject;         // user supplied timezone object, if any

        // Resolved once, when the Time first becomes a local time: Ruby keeps the zone a Time
        // was built in even if TZ changes afterwards (localtime_spec "does nothing if already
        // in a local time zone").
        private long _localOffset;
        private string _localZoneName;
        private bool _localDst;

        #endregion

        #region Construction

        public RubyTime(DateTime dateTime) {
            InitializeFromDateTime(dateTime);
        }

        // Used by derived Ruby classes.
        public RubyTime()
            : this(0, ExactNum.Zero, RubyTimeZoneKind.Local, ExactNum.Zero) {
        }

        public RubyTime(long ticks, DateTimeKind kind)
            : this(new DateTime(ticks, kind)) {
        }

        internal RubyTime(long seconds, ExactNum subsec, RubyTimeZoneKind zoneKind, ExactNum fixedOffset) {
            _seconds = seconds;
            _subsec = subsec;
            _zoneKind = zoneKind;
            _fixedOffset = fixedOffset;
            ResolveLocalZone();
        }

        private void ResolveLocalZone() {
            if (_zoneKind != RubyTimeZoneKind.Local) {
                return;
            }
            var zone = _CurrentTimeZone;
            _localOffset = zone.GetOffset(_seconds);
            _localZoneName = zone.GetAbbreviation(_seconds);
            _localDst = zone.IsDaylightSavingTime(_seconds);
        }

        internal static RubyTime/*!*/ FromExactSeconds(ExactNum secondsSinceEpoch, RubyTimeZoneKind kind, ExactNum fixedOffset) {
            BigInteger whole = secondsSinceEpoch.Floor();
            ExactNum sub = secondsSinceEpoch - ExactNum.FromInteger(whole);
            return new RubyTime(ClampSeconds(whole), sub, kind, fixedOffset);
        }

        // Seconds since the epoch are kept in a long; half its range leaves room for
        // the arithmetic on them.  MRI keeps a bignum and has no such limit.
        private static long ClampSeconds(BigInteger value) {
            if (value > new BigInteger(Int64.MaxValue / 2) || value < new BigInteger(Int64.MinValue / 2)) {
                throw RubyExceptions.CreateRangeError(String.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "time {0} out of Time range", value));
            }
            return (long)value;
        }

        private void InitializeFromDateTime(DateTime dateTime) {
            long ticks = dateTime.Ticks - Epoch.Ticks;
            long seconds = FloorDiv(ticks, TicksPerSecond);
            long rest = ticks - seconds * TicksPerSecond;

            if (dateTime.Kind == DateTimeKind.Utc) {
                _seconds = seconds;
                _zoneKind = RubyTimeZoneKind.Utc;
            } else {
                // A wall-clock reading in the current zone.
                long offset = _CurrentTimeZone.GetOffsetForWallClock(seconds);
                _seconds = seconds - offset;
                _zoneKind = RubyTimeZoneKind.Local;
            }
            _subsec = (rest == 0) ? ExactNum.Zero : ExactNum.Make(rest, TicksPerSecondBig);
            _fixedOffset = ExactNum.Zero;
            ResolveLocalZone();
        }

        internal void CopyFrom(RubyTime/*!*/ other) {
            _seconds = other._seconds;
            _subsec = other._subsec;
            _zoneKind = other._zoneKind;
            _fixedOffset = other._fixedOffset;
            _zoneObject = other._zoneObject;
            _localOffset = other._localOffset;
            _localZoneName = other._localZoneName;
            _localDst = other._localDst;
        }

        /// <summary>
        /// Keeps the zone another local Time already resolved. Time#round and friends return
        /// a Time in the receiver's zone, not in whatever TZ happens to be set now.
        /// </summary>
        internal void InheritLocalZone(RubyTime/*!*/ other) {
            if (_zoneKind == RubyTimeZoneKind.Local && other._zoneKind == RubyTimeZoneKind.Local) {
                _localOffset = other._localOffset;
                _localZoneName = other._localZoneName;
                _localDst = other._localDst;
            }
        }

        internal RubyTime/*!*/ WithZone(RubyTimeZoneKind kind, ExactNum fixedOffset, object zoneObject) {
            return new RubyTime(_seconds, _subsec, kind, fixedOffset) { _zoneObject = zoneObject };
        }

        #endregion

        #region Accessors

        internal static long FloorDiv(long value, long divisor) {
            long q = value / divisor;
            if (value % divisor != 0 && ((value < 0) != (divisor < 0))) {
                q--;
            }
            return q;
        }

        internal static long SecondsFromDateTimeTicks(long ticks) {
            return FloorDiv(ticks - Epoch.Ticks, TicksPerSecond);
        }

        internal static DateTime ToDateTimeClamped(long seconds, DateTimeKind kind) {
            return ToDateTimeClamped(seconds, ExactNum.Zero, kind);
        }

        internal static DateTime ToDateTimeClamped(long seconds, ExactNum subsec, DateTimeKind kind) {
            if (seconds < MinRepresentableSeconds) {
                return DateTime.SpecifyKind(DateTime.MinValue, kind);
            }
            if (seconds > MaxRepresentableSeconds) {
                return DateTime.SpecifyKind(DateTime.MaxValue, kind);
            }
            long ticks = Epoch.Ticks + seconds * TicksPerSecond + SubsecTicks(subsec);
            return new DateTime(ticks, kind);
        }

        internal static long SubsecTicks(ExactNum subsec) {
            if (subsec.IsZero) {
                return 0;
            }
            return (long)((subsec.Numerator * TicksPerSecondBig) / subsec.Denominator);
        }

        /// <summary>Whole seconds since the epoch (UTC), floored.</summary>
        public long Seconds {
            get { return _seconds; }
        }

        /// <summary>The exact number of seconds since the epoch, including the sub-second part.</summary>
        public ExactNum ToExactSeconds() {
            return ExactNum.FromInteger(_seconds) + _subsec;
        }

        #region Proleptic Gregorian calendar

        /// <summary>
        /// Broken-down calendar fields. Computed directly rather than through DateTime,
        /// which cannot represent year 0 (Time.new(0).year == 0 in Ruby) or years past 9999.
        /// </summary>
        public struct Fields {
            public int Year, Month, Day, Hour, Minute, Second, DayOfYear, DayOfWeek;
        }

        /// <summary>Days since 1970-01-01 for a proleptic Gregorian date (Howard Hinnant's algorithm).</summary>
        public static long DaysFromCivil(long y, int m, int d) {
            y -= (m <= 2) ? 1 : 0;
            long era = ((y >= 0) ? y : y - 399) / 400;
            long yoe = y - era * 400;                                             // [0, 399]
            long doy = (153 * (m + ((m > 2) ? -3 : 9)) + 2) / 5 + d - 1;          // [0, 365]
            long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;                     // [0, 146096]
            return era * 146097 + doe - 719468;
        }

        public static void CivilFromDays(long z, out long y, out int m, out int d) {
            z += 719468;
            long era = ((z >= 0) ? z : z - 146096) / 146097;
            long doe = z - era * 146097;                                          // [0, 146096]
            long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;     // [0, 399]
            long yy = yoe + era * 400;
            long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);                   // [0, 365]
            long mp = (5 * doy + 2) / 153;                                        // [0, 11]
            d = (int)(doy - (153 * mp + 2) / 5 + 1);                              // [1, 31]
            m = (int)(mp + ((mp < 10) ? 3 : -9));                                 // [1, 12]
            y = yy + ((m <= 2) ? 1 : 0);
        }

        /// <summary>The broken-down wall-clock fields in this Time's own zone.</summary>
        public Fields GetFields() {
            ExactNum offset = UtcOffsetExact;
            long wall = _seconds + (long)offset.Floor();

            long days = FloorDiv(wall, 86400);
            int secondOfDay = (int)(wall - days * 86400);

            long year;
            int month, day;
            CivilFromDays(days, out year, out month, out day);

            Fields result;
            result.Year = (year > Int32.MaxValue) ? Int32.MaxValue : (year < Int32.MinValue ? Int32.MinValue : (int)year);
            result.Month = month;
            result.Day = day;
            result.Hour = secondOfDay / 3600;
            result.Minute = (secondOfDay / 60) % 60;
            result.Second = secondOfDay % 60;
            result.DayOfYear = (int)(days - DaysFromCivil(year, 1, 1)) + 1;
            result.DayOfWeek = (int)(((days + 4) % 7 + 7) % 7);
            return result;
        }

        #endregion

        public ExactNum Subsec {
            get { return _subsec; }
        }

        public RubyTimeZoneKind ZoneKind {
            get { return _zoneKind; }
        }

        internal object ZoneObject {
            get { return _zoneObject; }
            set { _zoneObject = value; }
        }

        public bool IsUtc {
            get { return _zoneKind == RubyTimeZoneKind.Utc; }
        }

        public bool HasFixedOffset {
            get { return _zoneKind == RubyTimeZoneKind.FixedOffset; }
        }

        public ExactNum UtcOffsetExact {
            get {
                switch (_zoneKind) {
                    case RubyTimeZoneKind.Utc: return ExactNum.Zero;
                    case RubyTimeZoneKind.FixedOffset: return _fixedOffset;
                    default: return ExactNum.FromInteger(_localOffset);
                }
            }
        }

        public long UtcOffsetSeconds {
            get { return (long)UtcOffsetExact.Floor(); }
        }

        public bool IsDaylightSavingTime {
            get { return _zoneKind == RubyTimeZoneKind.Local && _localDst; }
        }

        /// <summary>The instant, in UTC.</summary>
        public DateTime UtcDateTime {
            get { return ToDateTimeClamped(_seconds, _subsec, DateTimeKind.Utc); }
        }

        /// <summary>The wall-clock reading in this Time's own zone.</summary>
        public DateTime WallClock {
            get {
                ExactNum offset = UtcOffsetExact;
                if (offset.IsInteger) {
                    return ToDateTimeClamped(_seconds + (long)offset.Numerator, _subsec,
                        _zoneKind == RubyTimeZoneKind.Utc ? DateTimeKind.Utc : DateTimeKind.Local);
                }
                ExactNum total = ExactNum.FromInteger(_seconds) + _subsec + offset;
                BigInteger whole = total.Floor();
                return ToDateTimeClamped((long)whole, total - ExactNum.FromInteger(whole), DateTimeKind.Local);
            }
        }

        public DateTime DateTime {
            get { return WallClock; }
            set { InitializeFromDateTime(value); }
        }

        internal void SetDateTime(DateTime value) {
            InitializeFromDateTime(value);
        }

        public long TicksSinceEpoch {
            get { return _seconds * TicksPerSecond + SubsecTicks(_subsec); }
        }

        public long Ticks {
            get { return WallClock.Ticks; }
        }

        public int Microseconds {
            get { return (int)((_subsec.Numerator * MicrosecondsPerSecond) / _subsec.Denominator); }
        }

        public int Nanoseconds {
            get { return (int)((_subsec.Numerator * NanosecondsPerSecond) / _subsec.Denominator); }
        }

        public DateTimeKind Kind {
            get { return _zoneKind == RubyTimeZoneKind.Utc ? DateTimeKind.Utc : DateTimeKind.Local; }
        }

        internal static long ToTicks(long seconds, long microseconds) {
            return seconds * 10000000 + microseconds * 10;
        }

        internal static DateTime AddSeconds(DateTime dateTime, double seconds) {
            bool isLocal = dateTime.Kind != DateTimeKind.Utc;
            if (isLocal) {
                dateTime = ToUniversalTime(dateTime);
            }

            // add in UTC to handle DST transitions correctly:
            dateTime = dateTime.AddTicks((long)(Math.Round(seconds, 6) * 10000000));

            if (isLocal) {
                dateTime = ToLocalTime(dateTime);
            }

            return dateTime;
        }

        #endregion

        #region Equality and comparison

        public override string ToString() {
            return WallClock.ToString();
        }

        public override int GetHashCode() {
            return _seconds.GetHashCode() ^ _subsec.GetHashCode();
        }

        int IComparable.CompareTo(object other) {
            return CompareTo(other as RubyTime);
        }

        public int CompareTo(RubyTime other) {
            if (other == null) {
                return -1;
            }
            int result = _seconds.CompareTo(other._seconds);
            return (result != 0) ? result : _subsec.CompareTo(other._subsec);
        }

        public static bool operator <(RubyTime x, RubyTime y) {
            return x.CompareTo(y) < 0;
        }

        public static bool operator <=(RubyTime x, RubyTime y) {
            return x.CompareTo(y) <= 0;
        }

        public static bool operator >(RubyTime x, RubyTime y) {
            return x.CompareTo(y) > 0;
        }

        public static bool operator >=(RubyTime x, RubyTime y) {
            return x.CompareTo(y) >= 0;
        }

        public static TimeSpan operator -(RubyTime x, DateTime y) {
            return x.UtcDateTime - ToUniversalTime(y);
        }

        public static TimeSpan operator -(RubyTime x, RubyTime y) {
            return TimeSpan.FromTicks(x.TicksSinceEpoch - y.TicksSinceEpoch);
        }

        public override bool Equals(object obj) {
            return Equals(obj as RubyTime);
        }

        public bool Equals(RubyTime other) {
            return CompareTo(other) == 0;
        }

        public static bool operator ==(RubyTime x, RubyTime y) {
            return ReferenceEquals(x, null) ? ReferenceEquals(y, null) : x.Equals(y);
        }

        public static bool operator !=(RubyTime x, RubyTime y) {
            return !(x == y);
        }

        public static explicit operator RubyTime(DateTime dateTime) {
            return new RubyTime(dateTime);
        }

        public static implicit operator DateTime(RubyTime time) {
            return time.WallClock;
        }

        public string ToString(string/*!*/ format, IFormatProvider/*!*/ provider) {
            return WallClock.ToString(format, provider);
        }

        #endregion

        #region Zone naming

        /// <summary>The zone abbreviation, or null for a Time with a fixed numeric offset.</summary>
        public string GetZoneName() {
            switch (_zoneKind) {
                case RubyTimeZoneKind.Utc: return "UTC";
                case RubyTimeZoneKind.FixedOffset: return null;
                default: return _localZoneName;
            }
        }

        internal string/*!*/ FormatUtcOffset() {
            return FormatUtcOffset(false, false);
        }

        internal string/*!*/ FormatUtcOffset(bool colons, bool seconds) {
            long total = UtcOffsetSeconds;
            string sign = total < 0 ? "-" : "+";
            long abs = Math.Abs(total);
            long h = abs / 3600;
            long m = (abs / 60) % 60;
            long s = abs % 60;

            if (seconds || s != 0) {
                return String.Format(CultureInfo.InvariantCulture, "{0}{1:D2}{4}{2:D2}{4}{3:D2}", sign, h, m, s, colons ? ":" : "");
            }
            return String.Format(CultureInfo.InvariantCulture, "{0}{1:D2}{3}{2:D2}", sign, h, m, colons ? ":" : "");
        }

        #endregion
    }

    #endregion
}

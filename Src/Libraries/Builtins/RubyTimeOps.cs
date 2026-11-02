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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {

    // Keep the Ruby methods separate so that we can expose CLR methods on RubyTime.
    [RubyClass("Time", Extends = typeof(RubyTime), Inherits = typeof(object))]
    [Includes(typeof(Comparable))]
    public static class RubyTimeOps {

        #region Dynamic call helpers

        private static CallSite<Func<CallSite, object, object>> _numeratorSite;
        private static CallSite<Func<CallSite, object, object>> _denominatorSite;
        private static CallSite<Func<CallSite, object, object>> _toRSite;
        private static CallSite<Func<CallSite, object, object>> _toIntSite;
        private static CallSite<Func<CallSite, object, object>> _toStrSite;
        private static CallSite<Func<CallSite, object, object>> _toISite;
        private static CallSite<Func<CallSite, object, object, object>> _localToUtcSite;
        private static CallSite<Func<CallSite, object, object, object>> _utcToLocalSite;
        private static CallSite<Func<CallSite, object, object, object>> _compareSite;
        private static CallSite<Func<CallSite, object, object, object>> _greaterSite;
        private static CallSite<Func<CallSite, object, object, object>> _lessSite;
        private static CallSite<Func<CallSite, object, object, object>> _findTimezoneSite;
        private static CallSite<Func<CallSite, object, object>> _allocateSite;
        private static CallSite<Func<CallSite, object, object, object>> _abbrSite;
        private static CallSite<Func<CallSite, object, object>> _nameSite;

        private static object Invoke(RubyContext/*!*/ context, ref CallSite<Func<CallSite, object, object>> site, string/*!*/ name, object target) {
            var s = RubyUtils.GetCallSite(ref site, context, name, 0);
            return s.Target(s, target);
        }

        private static object Invoke(RubyContext/*!*/ context, ref CallSite<Func<CallSite, object, object, object>> site, string/*!*/ name, object target, object arg) {
            var s = RubyUtils.GetCallSite(ref site, context, name, 1);
            return s.Target(s, target, arg);
        }

        private static bool RespondTo(RubyContext/*!*/ context, object obj, string/*!*/ name) {
            return context.ResolveMethod(obj, name, VisibilityContext.AllVisible).Found;
        }

        internal static object MakeRational(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, ExactNum value) {

            return KernelOps.ToRational(storage, scope,
                Protocols.Normalize(value.Numerator), Protocols.Normalize(value.Denominator));
        }

        #endregion

        #region Exact number conversion

        private static BigInteger ToBigInteger(object value) {
            if (value is int) {
                return new BigInteger((int)value);
            }
            if (value is BigInteger) {
                return (BigInteger)value;
            }
            if (value is long) {
                return new BigInteger((long)value);
            }
            if (value is double) {
                return new BigInteger((double)value);
            }
            throw RubyExceptions.CreateTypeError("can't convert to an exact number");
        }

        private static bool TryToExactSimple(object value, out ExactNum result) {
            if (value is int) {
                result = ExactNum.FromInteger((int)value);
                return true;
            }
            if (value is BigInteger) {
                result = ExactNum.FromInteger((BigInteger)value);
                return true;
            }
            if (value is long) {
                result = ExactNum.FromInteger((long)value);
                return true;
            }
            if (value is double) {
                double d = (double)value;
                if (Double.IsNaN(d) || Double.IsInfinity(d)) {
                    // MRI: a Time cannot be made from an Infinity or a NaN (FloatDomainError)
                    throw new FloatDomainError(Double.IsNaN(d) ? "NaN" : (d < 0 ? "-Infinity" : "Infinity"));
                }
                result = ExactNum.FromDouble(d);
                return true;
            }
            result = ExactNum.Zero;
            return false;
        }

        private static bool IsRational(RubyContext/*!*/ context, object value) {
            if (value == null || value is MutableString || value is RubyTime) {
                return false;
            }
            RubyClass cls = context.GetClassOf(value);
            return cls.Name == "Rational";
        }

        private static ExactNum FromRational(RubyContext/*!*/ context, object value) {
            object n = Invoke(context, ref _numeratorSite, "numerator", value);
            object d = Invoke(context, ref _denominatorSite, "denominator", value);
            return ExactNum.Make(ToBigInteger(n), ToBigInteger(d));
        }

        /// <summary>
        /// Ruby's num_exact: Integer, Rational and Float pass through; anything else must
        /// supply #to_r (plus #to_int, to reject String/Time which also define #to_r) or #to_int.
        /// </summary>
        internal static ExactNum ToExact(RubyContext/*!*/ context, object value) {
            ExactNum result;
            if (TryToExactSimple(value, out result)) {
                return result;
            }
            if (IsRational(context, value)) {
                return FromRational(context, value);
            }

            if (value != null && !(value is MutableString) && RespondTo(context, value, "to_r") && RespondTo(context, value, "to_int")) {
                object converted = Invoke(context, ref _toRSite, "to_r", value);
                if (TryToExactSimple(converted, out result)) {
                    return result;
                }
                if (IsRational(context, converted)) {
                    return FromRational(context, converted);
                }
            } else if (value != null && !(value is MutableString) && RespondTo(context, value, "to_int")) {
                object converted = Invoke(context, ref _toIntSite, "to_int", value);
                if (TryToExactSimple(converted, out result)) {
                    return result;
                }
            }

            throw RubyExceptions.CreateTypeError("can't convert {0} into an exact number", context.GetClassName(value));
        }

        /// <summary>
        /// Like ToExact, but also accepts a String, which Time.utc/local/new allow for
        /// broken-down components ("interprets all numerals as base 10").
        /// </summary>
        private static ExactNum ToExactComponent(RubyContext/*!*/ context, object value) {
            MutableString str = value as MutableString;
            if (str != null) {
                // obj2subsecx: a String is converted like Integer(), so "1.5" and "x" are errors
                return ExactNum.FromInteger(ToBigInteger(KernelOps.ToInteger((object)null, str, 10)));
            }
            return ToExact(context, value);
        }

        private static int ToIntComponent(RubyContext/*!*/ context, object value) {
            if (value is int) {
                return (int)value;
            }
            if (value is MutableString) {
                // obj2int: a String component is converted like Integer(), so "1.5" and "x" are
                // ArgumentErrors rather than 1 and 0.
                return ToInt32(KernelOps.ToInteger((object)null, (MutableString)value, 10));
            }
            ExactNum exact = ToExact(context, value);
            return ToInt32(exact.Floor());
        }

        /// <summary>NUM2INT: a value outside int is a RangeError, as MRI reports it.</summary>
        private static int ToInt32(object value) {
            if (value is int) {
                return (int)value;
            }
            BigInteger big = (value is BigInteger) ? (BigInteger)value : new BigInteger((long)value);
            return ToInt32(big);
        }

        private static int ToInt32(BigInteger value) {
            if (value > Int32.MaxValue || value < Int32.MinValue) {
                throw RubyExceptions.CreateRangeError(String.Format(CultureInfo.InvariantCulture,
                    "integer {0} too {1} to convert to 'int'", value, value.Sign < 0 ? "small" : "big"));
            }
            return (int)value;
        }

        #endregion

        #region UTC offset parsing

        private static readonly Regex/*!*/ _offsetPattern = new Regex(
            @"^(?<sign>[+-])(?<h>\d\d)(:?(?<m>\d\d)(:?(?<s>\d\d))?)?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses the utc_offset / :in argument. Returns false if the value isn't a String
        /// (the caller then treats it as a number or a timezone object).
        /// </summary>
        private static bool TryParseOffsetString(RubyContext/*!*/ context, object value, out ExactNum offset, out bool isUtc) {
            offset = ExactNum.Zero;
            isUtc = false;

            MutableString str = value as MutableString;
            if (str == null) {
                if (value == null || value is RubyTime || !RespondTo(context, value, "to_str")) {
                    return false;
                }
                str = Invoke(context, ref _toStrSite, "to_str", value) as MutableString;
                if (str == null) {
                    return false;
                }
            }

            if (!str.IsAscii() && !str.Encoding.IsAsciiIdentity) {
                throw RubyExceptions.CreateArgumentError("string contains null byte");
            }

            string s;
            try {
                s = str.ConvertToString();
            } catch (Exception) {
                throw RubyExceptions.CreateArgumentError("string contains null byte");
            }

            if (s == "UTC") {
                isUtc = true;
                return true;
            }

            if (s.Length == 1) {
                char c = s[0];
                if (c == 'Z') {
                    isUtc = true;
                    return true;
                }
                if (c >= 'A' && c <= 'I') {
                    offset = ExactNum.FromInteger((c - 'A' + 1) * 3600);
                    return true;
                }
                if (c >= 'K' && c <= 'M') {
                    offset = ExactNum.FromInteger((c - 'A') * 3600);
                    return true;
                }
                if (c >= 'N' && c <= 'Y') {
                    offset = ExactNum.FromInteger(-(c - 'N' + 1) * 3600);
                    return true;
                }
                throw InvalidUtcOffset(s);
            }

            Match m = _offsetPattern.Match(s);
            if (!m.Success) {
                throw InvalidUtcOffset(s);
            }

            int h = Int32.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            int min = m.Groups["m"].Success ? Int32.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            int sec = m.Groups["s"].Success ? Int32.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;

            if (h > 23) {
                throw RubyExceptions.CreateArgumentError("utc_offset out of range");
            }
            if (min > 59 || sec > 59) {
                throw InvalidUtcOffset(s);
            }

            long total = (long)h * 3600 + min * 60 + sec;
            if (m.Groups["sign"].Value == "-") {
                if (total == 0) {
                    isUtc = true;
                    return true;
                }
                total = -total;
            }
            offset = ExactNum.FromInteger(total);
            return true;
        }

        private static Exception InvalidUtcOffset(string value) {
            return RubyExceptions.CreateArgumentError(
                "\"+HH:MM\", \"-HH:MM\", \"UTC\" or \"A\"..\"I\",\"K\"..\"Z\" expected for utc_offset: {0}", value);
        }

        private static void CheckOffsetRange(ExactNum offset) {
            if (offset.CompareTo(ExactNum.FromInteger(86400)) >= 0 || offset.CompareTo(ExactNum.FromInteger(-86400)) <= 0) {
                throw RubyExceptions.CreateArgumentError("utc_offset out of range");
            }
        }

        /// <summary>
        /// Interprets a zone specification (nil, a numeric offset, an offset string, or a
        /// timezone object) and returns the resulting zone kind.
        /// </summary>
        private static RubyTimeZoneKind ResolveZone(RubyContext/*!*/ context, object zone, out ExactNum offset, out object zoneObject) {
            return ResolveZone(context, null, zone, out offset, out zoneObject);
        }

        private static bool IsTimezoneObject(RubyContext/*!*/ context, object zone) {
            return zone != null && (RespondTo(context, zone, "local_to_utc") || RespondTo(context, zone, "utc_to_local"));
        }

        private static RubyTimeZoneKind ResolveZone(RubyContext/*!*/ context, RubyClass owner, object zone,
            out ExactNum offset, out object zoneObject) {

            offset = ExactNum.Zero;
            zoneObject = null;

            if (zone == null || zone == Missing.Value) {
                return RubyTimeZoneKind.Local;
            }

            if (IsTimezoneObject(context, zone)) {
                zoneObject = zone;
                return RubyTimeZoneKind.FixedOffset;
            }

            // A subclass may provide .find_timezone to turn a name into a timezone object.
            bool nameLike = zone is MutableString;
            if (nameLike && owner != null && RespondTo(context, owner, "find_timezone")) {
                object found = Invoke(context, ref _findTimezoneSite, "find_timezone", owner, zone);
                if (found != null) {
                    zoneObject = found;
                    return RubyTimeZoneKind.FixedOffset;
                }
            }

            bool isUtc;
            if (TryParseOffsetString(context, zone, out offset, out isUtc)) {
                if (isUtc) {
                    offset = ExactNum.Zero;
                    return RubyTimeZoneKind.Utc;
                }
                CheckOffsetRange(offset);
                return RubyTimeZoneKind.FixedOffset;
            }

            offset = ToExact(context, zone);
            CheckOffsetRange(offset);
            return RubyTimeZoneKind.FixedOffset;
        }

        /// <summary>Extracts the epoch seconds a timezone object's protocol method returned.</summary>
        private static long ZoneResultToSeconds(RubyContext/*!*/ context, object value) {
            RubyTime time = value as RubyTime;
            if (time != null) {
                return time.Seconds;
            }
            if (value != null && RespondTo(context, value, "to_i")) {
                return (long)ToExact(context, Invoke(context, ref _toISite, "to_i", value)).Floor();
            }
            throw RubyExceptions.CreateTypeError("can't convert {0} into an exact number", context.GetClassName(value));
        }

        /// <summary>
        /// Maps an instant into a timezone object's local time, via #utc_to_local, and records
        /// the resulting offset.
        /// </summary>
        private static RubyTime/*!*/ ApplyZoneObjectToInstant(RubyContext/*!*/ context, RubyTime/*!*/ instant, object/*!*/ zoneObject) {
            if (!RespondTo(context, zoneObject, "utc_to_local")) {
                throw RubyExceptions.CreateTypeError("can't convert {0} into an exact number", context.GetClassName(zoneObject));
            }

            var utcView = new RubyTime(instant.Seconds, ExactNum.Zero, RubyTimeZoneKind.Utc, ExactNum.Zero);
            object mapped = Invoke(context, ref _utcToLocalSite, "utc_to_local", zoneObject, utcView);

            RubyTime mappedTime = mapped as RubyTime;
            long localSeconds = (mappedTime != null)
                ? mappedTime.Seconds + mappedTime.UtcOffsetSeconds
                : ZoneResultToSeconds(context, mapped);

            long offsetSeconds = localSeconds - instant.Seconds;
            if (offsetSeconds <= -86400 || offsetSeconds >= 86400) {
                throw RubyExceptions.CreateArgumentError("utc_offset out of range");
            }

            return instant.WithZone(RubyTimeZoneKind.FixedOffset, ExactNum.FromInteger(offsetSeconds), zoneObject);
        }

        #endregion

        #region Argument helpers

        private static IDictionary<object, object> ExtractOptions(ref object[]/*!*/ args) {
            if (args.Length == 0) {
                return null;
            }
            // `Time.new(..., in: zone)' is keywords; a Hash handed over as an ordinary argument
            // is a zone argument like any other, and MRI refuses to make a number of it.
            var hash = args[args.Length - 1] as Hash;
            if (hash == null || !hash.IsKeywordArguments) {
                return null;
            }
            object[] rest = new object[args.Length - 1];
            Array.Copy(args, rest, rest.Length);
            args = rest;
            return hash;
        }

        private static object GetOption(IDictionary<object, object> options, string/*!*/ name, out bool found) {
            found = false;
            if (options == null) {
                return null;
            }
            foreach (var entry in options) {
                var key = entry.Key as RubySymbol;
                if (key != null && key.ToString() == name) {
                    found = true;
                    return entry.Value;
                }
            }
            return null;
        }

        private static object GetInOption(IDictionary<object, object> options) {
            return GetInOption(options, false);
        }

        private static object GetInOption(IDictionary<object, object> options, bool allowPrecision) {
            if (options != null) {
                // in: is the only keyword Time.now and Time.at take; Time.new also takes precision:.
                foreach (var key in options.Keys) {
                    var symbol = key as RubySymbol;
                    string name = symbol != null ? symbol.ToString() : null;
                    if (name != "in" && !(allowPrecision && name == "precision")) {
                        throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", symbol != null ? ":" + name : key.ToString());
                    }
                }
            }
            bool found;
            return GetOption(options, "in", out found);
        }

        #endregion

        #region Construction

        private static RubyTime/*!*/ NowInternal(RubyContext/*!*/ context, RubyClass owner, object zone) {
            DateTime utcNow = DateTime.UtcNow;
            long ticks = utcNow.Ticks - RubyTime.Epoch.Ticks;
            long seconds = RubyTime.FloorDiv(ticks, TimeSpan.TicksPerSecond);
            long rest = ticks - seconds * TimeSpan.TicksPerSecond;
            ExactNum subsec = (rest == 0) ? ExactNum.Zero : ExactNum.Make(rest, RubyTime.TicksPerSecondBig);

            ExactNum offset;
            object zoneObject;
            RubyTimeZoneKind kind = ResolveZone(context, owner, zone, out offset, out zoneObject);
            var result = new RubyTime(seconds, subsec, kind, offset);
            if (zoneObject != null) {
                result = ApplyZoneObjectToInstant(context, result, zoneObject);
            }
            return result;
        }

        /// <summary>
        /// The singleton factories (Time.at/now/utc/local) return an instance of the receiver,
        /// which for a Ruby subclass of Time is a generated CLR subtype of RubyTime.
        /// </summary>
        private static RubyTime/*!*/ Subclass(RubyContext/*!*/ context, RubyClass self, RubyTime/*!*/ value) {
            if (self == null || self.GetUnderlyingSystemType() == typeof(RubyTime)) {
                return value;
            }
            try {
                var site = RubyUtils.GetCallSite(ref _allocateSite, context, "allocate", 0);
                RubyTime result = site.Target(site, self) as RubyTime;
                if (result != null) {
                    result.CopyFrom(value);
                    return result;
                }
            } catch (Exception) {
                // fall through: a subclass that cannot be allocated gets the base instance
            }
            return value;
        }

        [RubyConstructor]
        public static RubyTime/*!*/ Create(RubyContext/*!*/ context, RubyClass/*!*/ self) {
            return NowInternal(context, self, null);
        }

        [RubyConstructor]
        public static RubyTime/*!*/ Create(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]params object[]/*!*/ args) {
            return CreateFromComponents(context, self, args);
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyTime/*!*/ Reinitialize(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            self.CopyFrom(NowInternal(context, context.GetClassOf(self), null));
            return self;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyTime/*!*/ Reinitialize(RubyContext/*!*/ context, RubyTime/*!*/ self, [NotNull]params object[]/*!*/ args) {
            self.CopyFrom(CreateFromComponents(context, context.GetClassOf(self), args));
            return self;
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static RubyTime/*!*/ InitializeCopy(RubyTime/*!*/ self, [NotNull]RubyTime/*!*/ other) {
            self.CopyFrom(other);
            return self;
        }

        /// <summary>Time.new(year, month, day, hour, min, sec, utc_offset) / Time.new(in: zone)</summary>
        private static RubyTime/*!*/ CreateFromComponents(RubyContext/*!*/ context, RubyClass owner, object[]/*!*/ args) {
            var options = ExtractOptions(ref args);
            object inZone = GetInOption(options, true);

            if (args.Length == 0) {
                return NowInternal(context, owner, inZone);
            }

            // Ruby 3.2's Time.new("2020-12-24T15:56:17Z")
            if (args.Length == 1 && args[0] is MutableString) {
                return ParseTimeString(context, owner, (MutableString)args[0], inZone, options);
            }

            if (args.Length > 7) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0..7)", args.Length);
            }

            if (args[0] == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion of nil into Integer");
            }

            if (args.Length == 7 && inZone != null) {
                throw RubyExceptions.CreateArgumentError("timezone argument given as positional and keyword arguments");
            }
            object zone = (args.Length == 7) ? args[6] : inZone;

            ExactNum offset;
            object zoneObject;
            RubyTimeZoneKind kind = ResolveZone(context, owner, zone, out offset, out zoneObject);

            int year = ToIntComponent(context, args[0]);
            int month = (args.Length > 1) ? GetMonth(context, args[1]) : 1;
            int day = (args.Length > 2 && args[2] != null) ? ToIntComponent(context, args[2]) : 1;
            int hour = (args.Length > 3 && args[3] != null) ? ToIntComponent(context, args[3]) : 0;
            int minute = (args.Length > 4 && args[4] != null) ? ToIntComponent(context, args[4]) : 0;
            ExactNum second = (args.Length > 5 && args[5] != null) ? ToExactComponent(context, args[5]) : ExactNum.Zero;

            return AssembleTime(context, year, month, day, hour, minute, second, kind, offset, zoneObject, true);
        }

        /// <summary>
        /// Time.new with a single String argument. Every message the specs check for is
        /// allowed to be the generic "can't parse:" form, so the failure modes are not
        /// distinguished the way MRI's hand-written scanner does.
        /// </summary>
        private static RubyTime/*!*/ ParseTimeString(RubyContext/*!*/ context, RubyClass owner, MutableString/*!*/ str,
            object inZone, IDictionary<object, object> options) {

            if (!str.Encoding.IsAsciiIdentity) {
                throw RubyExceptions.CreateArgumentError("time string should have ASCII compatible encoding");
            }

            string text = str.ConvertToString();
            int year, month = 1, day = 1, hour = 0, minute = 0;
            ExactNum second = ExactNum.Zero;
            string fraction = null, zoneText = null;
            ParseTimeText(context, str, text, out year, ref month, ref day, ref hour, ref minute, ref second, ref fraction, ref zoneText);

            int precision = 9;
            bool precisionGiven;
            object precisionValue = GetOption(options, "precision", out precisionGiven);
            if (precisionGiven) {
                if (precisionValue == null) {
                    precision = -1;
                } else if (precisionValue is MutableString) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion of String into Integer");
                } else {
                    precision = ToIntComponent(context, precisionValue);
                }
            }

            if (fraction != null) {
                string digits = fraction;
                if (precision >= 0 && digits.Length > precision) {
                    digits = digits.Substring(0, precision);
                }
                if (digits.Length != 0) {
                    second = second + ExactNum.Make(BigInteger.Parse(digits, CultureInfo.InvariantCulture),
                        BigInteger.Pow(new BigInteger(10), digits.Length));
                }
            }

            object zone = zoneText != null ? MutableString.CreateAscii(zoneText) : inZone;

            ExactNum offset;
            object zoneObject;
            RubyTimeZoneKind kind = ResolveZone(context, owner, zone, out offset, out zoneObject);

            return AssembleTime(context, year, month, day, hour, minute, second, kind, offset, zoneObject, true);
        }

        private static bool IsAsciiDigit(char c) {
            return c >= '0' && c <= '9';
        }

        private static bool IsAsciiSpace(char c) {
            return c == ' ' || (c >= '\t' && c <= '\r');
        }

        /// <summary>
        /// MRI's time_init_parse: "YYYY[-MM[-DD[ HH:MM:SS[.fraction]]]] [zone]", with its messages
        /// for what is missing or malformed.
        /// </summary>
        private static void ParseTimeText(RubyContext/*!*/ context, MutableString/*!*/ str, string/*!*/ text, out int year,
            ref int month, ref int day, ref int hour, ref int minute, ref ExactNum second, ref string fraction, ref string zoneText) {

            int pos = 0, end = text.Length;
            int digits = 0;
            while (pos + digits < end && IsAsciiDigit(text[pos + digits])) {
                digits++;
            }
            if (digits == 0) {
                throw RubyExceptions.CreateArgumentError("can't parse: {0}", context.Inspect(str).ToString());
            }
            if (digits < 4) {
                throw RubyExceptions.CreateArgumentError("year must be 4 or more digits: {0}", text.Substring(pos, digits));
            }
            year = ToInt32(BigInteger.Parse(text.Substring(pos, digits), CultureInfo.InvariantCulture));
            pos += digits;

            if (pos < end && text[pos] == '-') {
                month = TwoDigits(text, ref pos, "mon");
                if (pos < end && text[pos] == '-') {
                    day = TwoDigits(text, ref pos, "mday");
                }
                if (pos < end && (text[pos] == ' ' || text[pos] == 'T')) {
                    int timePart = pos + 1;
                    hour = TwoDigits(text, ref pos, "hour");
                    if (pos < end && text[pos] == '.') {
                        throw RubyExceptions.CreateArgumentError("fraction hour is not supported: {0}", text.Substring(timePart, pos + 1 - timePart));
                    }
                    if (pos < end && text[pos] == ':') {
                        minute = TwoDigits(text, ref pos, "min");
                        if (pos < end && text[pos] == '.') {
                            throw RubyExceptions.CreateArgumentError("fraction min is not supported: {0}", text.Substring(timePart, pos + 1 - timePart));
                        }
                        if (pos < end && text[pos] == ':') {
                            second = ExactNum.FromInteger(TwoDigits(text, ref pos, "sec"));
                            if (pos < end && text[pos] == '.') {
                                pos++;
                                int start = pos;
                                while (pos < end && IsAsciiDigit(text[pos])) {
                                    pos++;
                                }
                                if (pos == start) {
                                    int shown = Math.Min(end, pos + 1);
                                    throw RubyExceptions.CreateArgumentError("subsecond expected after dot: {0}", text.Substring(timePart, shown - timePart));
                                }
                                fraction = text.Substring(start, pos - start);
                            }
                        } else {
                            // MRI shows the time part up to and including the character that was
                            // not the expected ':'
                            throw RubyExceptions.CreateArgumentError("missing sec part: {0}", text.Substring(timePart, Math.Min(end, pos + 1) - timePart));
                        }
                    } else {
                        throw RubyExceptions.CreateArgumentError("missing min part: {0}", text.Substring(timePart, Math.Min(end, pos + 1) - timePart));
                    }
                } else {
                    // a date without a time of day is not a Time
                    throw RubyExceptions.CreateArgumentError("no time information");
                }
            }

            if (pos < end && IsAsciiSpace(text[pos])) {
                while (pos < end && IsAsciiSpace(text[pos])) {
                    pos++;
                }
                // whitespace has to introduce a zone; trailing whitespace alone is a parse error
                if (pos == end) {
                    throw RubyExceptions.CreateArgumentError("can't parse: {0}", context.Inspect(str).ToString());
                }
            }
            if (pos < end) {
                int start = pos;
                while (pos < end && !IsAsciiSpace(text[pos])) {
                    pos++;
                }
                zoneText = text.Substring(start, pos - start);
                while (pos < end && IsAsciiSpace(text[pos])) {
                    pos++;
                }
                if (pos < end) {
                    throw RubyExceptions.CreateArgumentError("can't parse at: {0}", text.Substring(pos));
                }
            }
        }

        /// <summary>
        /// MRI's two_digits: exactly two digits follow the separator at text[pos], and a third
        /// digit is as wrong as a missing one.
        /// </summary>
        private static int TwoDigits(string/*!*/ text, ref int pos, string/*!*/ name) {
            char separator = text[pos];
            int start = pos + 1;
            int len = text.Length - start;
            if (len < 2 || !IsAsciiDigit(text[start]) || !IsAsciiDigit(text[start + 1]) || (len > 2 && IsAsciiDigit(text[start + 2]))) {
                string message = "two digits " + name + " is expected";
                if (separator == '-' || separator == ':') {
                    message += " after '" + separator + "'";
                }
                int shown = Math.Min(len > 10 ? 10 : len, len) + 1;
                throw RubyExceptions.CreateArgumentError(message + ": {0}", text.Substring(pos, Math.Min(shown, text.Length - pos)));
            }
            pos = start + 2;
            return (text[start] - '0') * 10 + (text[start + 1] - '0');
        }

        private static int GetMonth(RubyContext/*!*/ context, object value) {
            if (value == null) {
                return 1;
            }

            MutableString str = value as MutableString;
            if (str == null && !(value is int) && value != null && RespondTo(context, value, "to_str")) {
                str = Invoke(context, ref _toStrSite, "to_str", value) as MutableString;
            }

            if (str != null) {
                string s = str.ConvertToString();
                if (s.Length == 3) {
                    string lower = s.ToLowerInvariant();
                    for (int i = 0; i < _Months.Length; i++) {
                        if (_Months[i] == lower) {
                            return i + 1;
                        }
                    }
                }
                return ToInt32(KernelOps.ToInteger((object)null, str, 10));
            }

            return ToIntComponent(context, value);
        }

        private static string[]/*!*/ _Months = new string[12] {
            "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"
        };

        /// <summary>
        /// Builds a Time from broken-down components interpreted in the given zone.
        /// </summary>
        private static RubyTime/*!*/ AssembleTime(RubyContext/*!*/ context, int year, int month, int day, int hour, int minute,
            ExactNum second, RubyTimeZoneKind kind, ExactNum offset, object zoneObject, bool allowOverflow, bool preferDst = false) {

            // MRI uses a component-specific message for values just outside the valid band
            // and the generic "argument out of range" for anything further out.
            // (obj2ubits lets 0..15 through for the month, 0..31 for the day and the hour,
            // and the range check that follows names the field.)
            if (month < 1 || month > 12) {
                throw RubyExceptions.CreateArgumentError((month >= 0 && month <= 15) ? "mon out of range" : "argument out of range");
            }
            if (hour < 0 || hour > 24) {
                throw RubyExceptions.CreateArgumentError((hour >= 25 && hour <= 31) ? "hour out of range" : "argument out of range");
            }
            if (minute < 0 || minute > 59) {
                throw RubyExceptions.CreateArgumentError((minute >= 60 && minute <= 63) ? "min out of range" : "argument out of range");
            }
            if (second.Sign < 0) {
                throw RubyExceptions.CreateArgumentError("argument out of range");
            }
            if (second.CompareTo(ExactNum.FromInteger(61)) >= 0) {
                throw RubyExceptions.CreateArgumentError(
                    second.CompareTo(ExactNum.FromInteger(64)) < 0 ? "sec out of range" : "argument out of range");
            }
            // A day beyond the end of the month rolls into the next one (Feb 31 == Mar 3),
            // but a day outside 1..31 is an error.
            if (day < 1 || day > 31) {
                throw RubyExceptions.CreateArgumentError(day == 0 ? "mday out of range" : "argument out of range");
            }
            // 24:00:00 is the end of the day; anything past it is not a time of that day.
            if (hour == 24 && minute != 0) {
                throw RubyExceptions.CreateArgumentError("min out of range");
            }
            if (hour == 24 && second.Sign != 0) {
                throw RubyExceptions.CreateArgumentError("sec out of range");
            }

            long wallSeconds;
            try {
                wallSeconds = checked(RubyTime.DaysFromCivil(year, month, 1) * 86400
                    + (long)(day - 1) * 86400 + (long)hour * 3600 + (long)minute * 60);
            } catch (OverflowException) {
                throw RubyExceptions.CreateArgumentError("argument out of range");
            }

            BigInteger wholeSecond = second.Floor();
            ExactNum fraction = second - ExactNum.FromInteger(wholeSecond);
            wallSeconds += (long)wholeSecond;

            if (kind == RubyTimeZoneKind.FixedOffset && zoneObject != null) {
                return ApplyTimezoneObject(context, wallSeconds, fraction, zoneObject);
            }

            ExactNum wallExact = ExactNum.FromInteger(wallSeconds) + fraction;
            ExactNum utcExact;
            switch (kind) {
                case RubyTimeZoneKind.Utc:
                    utcExact = wallExact;
                    break;

                case RubyTimeZoneKind.FixedOffset:
                    utcExact = wallExact - offset;
                    break;

                default: {
                        long zoneOffset = RubyTime._CurrentTimeZone.GetOffsetForWallClock(wallSeconds, preferDst);
                        utcExact = wallExact - ExactNum.FromInteger(zoneOffset);
                        break;
                    }
            }

            var result = RubyTime.FromExactSeconds(utcExact, kind, offset);
            result.ZoneObject = zoneObject;
            return result;
        }

        private static RubyTime/*!*/ ApplyTimezoneObject(RubyContext/*!*/ context, long wallSeconds, ExactNum fraction, object/*!*/ zoneObject) {
            // Hand the zone object a UTC "Time-like" value and let it map it to a real instant.
            if (!RespondTo(context, zoneObject, "local_to_utc")) {
                throw RubyExceptions.CreateTypeError("can't convert {0} into an exact number", context.GetClassName(zoneObject));
            }

            var asUtc = new RubyTime(wallSeconds, ExactNum.Zero, RubyTimeZoneKind.Utc, ExactNum.Zero);
            long utcSeconds = ZoneResultToSeconds(context, Invoke(context, ref _localToUtcSite, "local_to_utc", zoneObject, asUtc));

            long offsetSeconds = wallSeconds - utcSeconds;
            if (offsetSeconds <= -86400 || offsetSeconds >= 86400) {
                throw RubyExceptions.CreateArgumentError("utc_offset out of range");
            }

            var result = new RubyTime(utcSeconds, fraction, RubyTimeZoneKind.FixedOffset, ExactNum.FromInteger(offsetSeconds));
            result.ZoneObject = zoneObject;
            return result;
        }

        #region at

        [RubyMethod("at", RubyMethodAttributes.PublicSingleton)]
        public static RubyTime/*!*/ At(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]params object[]/*!*/ args) {
            var options = ExtractOptions(ref args);
            object inZone = GetInOption(options);

            if (args.Length < 1 || args.Length > 3) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 1..3)", args.Length);
            }

            ExactNum offset;
            object zoneObject;
            RubyTimeZoneKind kind;
            bool zoneGiven = inZone != null;
            if (zoneGiven) {
                kind = ResolveZone(context, self, inZone, out offset, out zoneObject);
            } else {
                kind = RubyTimeZoneKind.Local;
                offset = ExactNum.Zero;
                zoneObject = null;
            }

            RubyTime other = args[0] as RubyTime;
            if (other != null && args.Length > 1) {
                throw RubyExceptions.CreateTypeError("can't convert Time into an exact number");
            }

            ExactNum seconds = (other != null) ? other.ToExactSeconds() : ToExact(context, args[0]);

            if (args.Length >= 2) {
                ExactNum sub = ToExact(context, args[1]);
                BigInteger unitDivisor = RubyTime.MicrosecondsPerSecond;

                if (args.Length == 3) {
                    var unit = args[2] as RubySymbol;
                    if (unit == null) {
                        throw RubyExceptions.CreateArgumentError("unexpected unit: {0}", context.Inspect(args[2]).ToString());
                    }
                    switch (unit.ToString()) {
                        case "millisecond": unitDivisor = new BigInteger(1000); break;
                        case "usec":
                        case "microsecond": unitDivisor = RubyTime.MicrosecondsPerSecond; break;
                        case "nsec":
                        case "nanosecond": unitDivisor = RubyTime.NanosecondsPerSecond; break;
                        default:
                            throw RubyExceptions.CreateArgumentError("unexpected unit: {0}", context.Inspect(args[2]).ToString());
                    }
                }

                seconds = seconds + sub / ExactNum.FromInteger(unitDivisor);
            }

            RubyTime instant;
            if (!zoneGiven && other != null) {
                // Time.at(time) keeps the source's zone.
                instant = new RubyTime(other.Seconds, other.Subsec, other.ZoneKind,
                    other.ZoneKind == RubyTimeZoneKind.FixedOffset ? other.UtcOffsetExact : ExactNum.Zero);
                instant.ZoneObject = other.ZoneObject;
            } else {
                instant = RubyTime.FromExactSeconds(seconds, kind, offset);
                if (zoneObject != null) {
                    instant = ApplyZoneObjectToInstant(context, instant, zoneObject);
                }
            }

            return Subclass(context, self, instant);
        }

        #endregion

        #region now

        [RubyMethod("now", RubyMethodAttributes.PublicSingleton)]
        public static RubyTime/*!*/ Now(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]params object[]/*!*/ args) {
            var options = ExtractOptions(ref args);
            if (args.Length != 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0)", args.Length);
            }
            return Subclass(context, self, NowInternal(context, self, GetInOption(options)));
        }

        #endregion

        #region local, mktime, utc, gm

        [RubyMethod("local", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("mktime", RubyMethodAttributes.PublicSingleton)]
        public static RubyTime/*!*/ CreateLocalTime(RubyContext/*!*/ context, RubyClass/*!*/ self, params object[]/*!*/ components) {
            return Subclass(context, self, CreateBrokenDownTime(context, self, components, RubyTimeZoneKind.Local));
        }

        [RubyMethod("utc", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("gm", RubyMethodAttributes.PublicSingleton)]
        public static RubyTime/*!*/ CreateGmtTime(RubyContext/*!*/ context, RubyClass/*!*/ self, params object[]/*!*/ components) {
            return Subclass(context, self, CreateBrokenDownTime(context, self, components, RubyTimeZoneKind.Utc));
        }

        private static RubyTime/*!*/ CreateBrokenDownTime(RubyContext/*!*/ context, RubyClass owner, object[]/*!*/ components, RubyTimeZoneKind kind) {
            // the isdst of the Time#to_a form picks the daylight saving reading of an hour that occurs twice
            bool preferDst = false;
            if (components.Length == 10) {
                preferDst = components[8] != null && RubyOps.IsTrue(components[8]);
                // 10 arguments in the order output by Time#to_a are permitted.
                // The last 4 are ignored. The first 6 need to be used in the reverse order.
                object[] newComponents = new object[6];
                Array.Copy(components, newComponents, 6);
                Array.Reverse(newComponents);
                components = newComponents;
            } else if (components.Length > 8 || components.Length == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 1..8)", components.Length);
            }

            int year = GetYear(context, components, 0);
            int month = (components.Length > 1) ? GetMonth(context, components[1]) : 1;
            int day = GetComponent(context, components, 2, 1);
            int hour = GetComponent(context, components, 3, 0);
            int minute = GetComponent(context, components, 4, 0);

            ExactNum second = (components.Length > 5 && components[5] != null) ? ToExactComponent(context, components[5]) : ExactNum.Zero;
            if (components.Length > 6 && components[6] != null) {
                ExactNum usec = ToExactComponent(context, components[6]);
                if (usec.Sign < 0 || usec.CompareTo(ExactNum.FromInteger(RubyTime.MicrosecondsPerSecond)) >= 0) {
                    throw RubyExceptions.CreateArgumentError("subsecx out of range");
                }
                // An explicit microsecond argument supersedes any fraction of the seconds argument.
                second = ExactNum.FromInteger(second.Floor()) + usec / ExactNum.FromInteger(RubyTime.MicrosecondsPerSecond);
            }

            return AssembleTime(context, year, month, day, hour, minute, second, kind, ExactNum.Zero, null, false, preferDst);
        }

        private static int GetComponent(RubyContext/*!*/ context, object[]/*!*/ components, int index, int defValue) {
            if (index >= components.Length || components[index] == null) {
                return defValue;
            }
            return ToIntComponent(context, components[index]);
        }

        private static int GetYear(RubyContext/*!*/ context, object[]/*!*/ components, int index) {
            if (index >= components.Length || components[index] == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion of nil into Integer");
            }
            return ToIntComponent(context, components[index]);
        }

        #endregion

        #endregion

        #region _dump, _load

        [RubyMethod("_dump", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ Dump(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]int depth) {
            DateTime value = self.UtcDateTime;
            if (value.Year < 1900 || value.Year > 2038) {
                throw RubyExceptions.CreateTypeError("unable to marshal time");
            }

            // Little Endian
            //            32            |                32                  |
            // minute:6|second:6|usec:20|1|utc:1|year:16|month:4|day:5|hour:5|
            uint dword1 = self.IsUtc ? 0xC0000000 : 0x80000000;
            dword1 |= (unchecked((uint)(value.Year - 1900)) << 14);
            dword1 |= (unchecked((uint)(value.Month - 1)) << 10);
            dword1 |= ((uint)value.Day << 5);
            dword1 |= ((uint)value.Hour);

            uint dword2 = 0;
            dword2 |= ((uint)value.Minute << 26);
            dword2 |= ((uint)value.Second << 20);
            dword2 |= ((uint)self.Microseconds);

            MemoryStream buf = new MemoryStream(8);
            RubyEncoder.Write(buf, dword1, !BitConverter.IsLittleEndian);
            RubyEncoder.Write(buf, dword2, !BitConverter.IsLittleEndian);
            var result = MutableString.CreateBinary(buf.ToArray());

            // The time's own instance variables travel on the string as well: Marshal writes the
            // string's, not the object's, once the string has any at all.
            foreach (string name in context.GetInstanceVariableNames(self)) {
                object ivarValue;
                if (context.TryGetInstanceVariable(self, name, out ivarValue)) {
                    context.SetInstanceVariable(result, name, ivarValue);
                }
            }

            // The eight bytes above hold the wall clock reading and nothing about where it was
            // read. MRI hangs the rest on the string it returns - Marshal writes the instance
            // variables of _dump's result - and reads them back in _load; without them a time
            // comes back in whatever zone the loader happens to be in.
            if (self.IsUtc) {
                context.SetInstanceVariable(result, "zone", MutableString.CreateAscii("UTC").Freeze());
            } else {
                ExactNum offset = self.UtcOffsetExact;
                if (offset.IsInteger) {
                    context.SetInstanceVariable(result, "offset", Protocols.Normalize(offset.Numerator));
                }

                if (self.ZoneObject is MutableString) {
                    // A time loaded from a dump that no find_timezone turned into an object keeps
                    // the bare name, and MRI writes the name out again as it is.
                    context.SetInstanceVariable(result, "zone", self.ZoneObject);
                } else if (self.ZoneObject != null) {
                    // A timezone object names itself. One that cannot - no #name method - takes
                    // the whole time down with it, which is what MRI does too.
                    context.SetInstanceVariable(result, "zone",
                        Invoke(context, ref _nameSite, "name", self.ZoneObject));
                } else {
                    // A time made with a numeric offset has no zone to name, and MRI writes nil.
                    // On Windows the name is the localized one ("Mitteleuropäische Zeit"), as in #zone.
                    string zoneName = self.HasFixedOffset ? null : self.GetZoneName();
                    context.SetInstanceVariable(result, "zone", zoneName == null ? null : (zoneName.IsAscii()
                        ? MutableString.CreateAscii(zoneName)
                        : MutableString.Create(zoneName, context.GetPathEncoding())).Freeze());
                }
            }

            // Anything finer than a microsecond does not fit in the eight bytes either. MRI
            // carries the remainder as a Rational number of nanoseconds.
            ExactNum nano = (self.Subsec * ExactNum.FromInteger(1000000) - ExactNum.FromInteger(self.Microseconds))
                * ExactNum.FromInteger(1000);
            if (!nano.IsZero) {
                context.SetInstanceVariable(result, "nano_num", Protocols.Normalize(nano.Numerator));
                context.SetInstanceVariable(result, "nano_den", Protocols.Normalize(nano.Denominator));
            }

            return result;
        }

        /// <summary>
        /// A time loaded from a dump that named an offset is pinned to that offset, and keeps the
        /// zone's name if the dump had one - MRI answers "AST" from a time loaded in another zone.
        /// </summary>
        private static RubyTime/*!*/ WithLoadedZone(RubyContext/*!*/ context, RubyClass/*!*/ owner, RubyTime/*!*/ time,
            bool hasOffset, BigInteger offsetSeconds, MutableString zoneName) {

            if (!hasOffset) {
                // Nothing said where it was read, so it is shown here.
                return time.WithZone(RubyTimeZoneKind.Local, ExactNum.Zero, null);
            }

            var result = time.WithZone(RubyTimeZoneKind.FixedOffset, ExactNum.FromInteger(offsetSeconds),
                zoneName != null ? zoneName.Clone().Freeze() : null);

            // A class that knows how to look a zone up by name gets the object back rather than
            // the bare name - the dump only ever carried the name. MRI rescues a StandardError
            // from the lookup and keeps the name; throw, Thread#kill and other exceptions go on.
            if (zoneName != null && RespondTo(context, owner, "find_timezone")) {
                object found;
                try {
                    found = Invoke(context, ref _findTimezoneSite, "find_timezone", owner, zoneName);
                } catch (Exception e) when (!(e is StackUnwinder) && context.IsInstanceOf(e, context.StandardErrorClass)) {
                    found = null;
                }
                if (found != null) {
                    result.ZoneObject = found;
                }
            }
            return result;
        }

        /// <summary>The instance variables a dumped Time carries that are not the time itself.</summary>
        private static readonly string[] TimeMarshalIVars = { "offset", "zone", "nano_num", "nano_den", "submicro" };

        /// <summary>
        /// Anything the dumped string carries beyond the time's own fields belongs to the object:
        /// Marshal writes an object's instance variables onto the string _dump returned when the
        /// string has none of its own, and MRI hands them back the same way.
        /// </summary>
        private static RubyTime/*!*/ CopyExtraIVars(RubyContext/*!*/ context, MutableString/*!*/ dumped, RubyTime/*!*/ result) {
            foreach (string name in context.GetInstanceVariableNames(dumped)) {
                if (Array.IndexOf(TimeMarshalIVars, name) >= 0) {
                    continue;
                }

                object value;
                if (context.TryGetInstanceVariable(dumped, name, out value)) {
                    context.SetInstanceVariable(result, name, value);
                }
            }
            return result;
        }

        private static bool TryToBigInteger(object value, out BigInteger result) {
            if (value is int) {
                result = (int)value;
                return true;
            }
            if (value is long) {
                result = (long)value;
                return true;
            }
            if (value is BigInteger) {
                result = (BigInteger)value;
                return true;
            }
            result = BigInteger.Zero;
            return false;
        }

        private static uint GetUint(byte[] data, int start) {
            Assert.NotNull(data);
            return (((((((uint)data[start + 3] << 8) + (uint)data[start + 2]) << 8) + (uint)data[start + 1]) << 8) + (uint)data[start + 0]);
        }

        [RubyMethod("_load", RubyMethodAttributes.PrivateSingleton)]
        public static RubyTime/*!*/ Load(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]MutableString/*!*/ time) {
            byte[] data = time.ConvertToBytes();
            if (data.Length != 8) {
                throw RubyExceptions.CreateTypeError("marshaled time format differ");
            }

            uint dword1 = GetUint(data, 0);
            uint dword2 = GetUint(data, 4);

            // What the eight bytes leave out travels as instance variables of this very string -
            // see _dump: where the reading was taken, and anything finer than a microsecond.
            object offsetValue, zoneValue, nanoNum, nanoDen;
            BigInteger offsetSeconds = BigInteger.Zero;
            bool hasOffset = context.TryGetInstanceVariable(time, "offset", out offsetValue)
                && TryToBigInteger(offsetValue, out offsetSeconds);
            context.TryGetInstanceVariable(time, "zone", out zoneValue);
            var zoneName = zoneValue as MutableString;
            ExactNum extraSubsec = ExactNum.Zero;
            if (context.TryGetInstanceVariable(time, "nano_num", out nanoNum)
                && context.TryGetInstanceVariable(time, "nano_den", out nanoDen)) {

                BigInteger num, den;
                if (TryToBigInteger(nanoNum, out num) && TryToBigInteger(nanoDen, out den) && !den.IsZero) {
                    // The remainder is in nanoseconds; the time itself counts seconds.
                    extraSubsec = ExactNum.Make(num, den * 1000000000);
                }
            }

            if ((data[3] & 0x80) == 0) {
                int secondsSinceEpoch = (int)dword1;
                uint microseconds = dword2;
                var subsec = ((microseconds == 0) ? ExactNum.Zero : ExactNum.Make(microseconds, RubyTime.MicrosecondsPerSecond))
                    + extraSubsec;
                return CopyExtraIVars(context, time,
                    WithLoadedZone(context, self, new RubyTime(secondsSinceEpoch, subsec, RubyTimeZoneKind.Local, ExactNum.Zero),
                        hasOffset, offsetSeconds, zoneName));
            } else {
                bool isUtc = (data[3] & 0x40) != 0;
                int year = 1900 + (int)((dword1 >> 14) & 0xffff);
                int month = 1 + (int)((dword1 >> 10) & 0x0f);
                int day = (int)((dword1 >> 5) & 0x01f);
                int hour = (int)(dword1 & 0x01f);

                int minute = (int)((dword2 >> 26) & 0x3f);
                int second = (int)((dword2 >> 20) & 0x3f);
                int usec = (int)(dword2 & 0xfffff);

                ExactNum seconds = ExactNum.FromInteger(second)
                    + ExactNum.Make(usec, RubyTime.MicrosecondsPerSecond) + extraSubsec;

                // The components are the reading in UTC whether or not the time was a UTC
                // one - the flag and the offset only say how it is to be shown again. Read
                // as local they would name a different instant, an hour or nine out.
                // Components out of range mean the bytes are not a time. Only this is guarded:
                // it runs no Ruby code, unlike the zone lookup below.
                RubyTime instant;
                try {
                    instant = AssembleTime(context, year, month, day, hour, minute, seconds,
                        RubyTimeZoneKind.Utc, ExactNum.Zero, null, false);
                } catch (Exception) {
                    throw RubyExceptions.CreateTypeError("marshaled time format differ");
                }

                return CopyExtraIVars(context, time,
                    isUtc ? instant : WithLoadedZone(context, self, instant, hasOffset, offsetSeconds, zoneName));
            }
        }

        #endregion

        #region succ, +, -, <=>, ==, eql?, hash

        // Time#succ was removed in Ruby 1.9.2. Keeping it made a Time range look iterable, so
        // (t...t).max answered t instead of raising TypeError "can't iterate from Time".
        public static RubyTime/*!*/ SuccessiveSecond(RubyTime/*!*/ self) {
            return Shift(self, ExactNum.One);
        }

        private static RubyTime/*!*/ Shift(RubyTime/*!*/ self, ExactNum delta) {
            ExactNum result = self.ToExactSeconds() + delta;
            var time = RubyTime.FromExactSeconds(result, self.ZoneKind,
                self.ZoneKind == RubyTimeZoneKind.FixedOffset ? self.UtcOffsetExact : ExactNum.Zero);
            time.ZoneObject = self.ZoneObject;
            return time;
        }

        [RubyMethod("+")]
        public static RubyTime/*!*/ AddSeconds(RubyContext/*!*/ context, RubyTime/*!*/ self, object seconds) {
            if (seconds is RubyTime) {
                throw RubyExceptions.CreateTypeError("time + time?");
            }
            try {
                return Shift(self, ToExact(context, seconds));
            } catch (OverflowException) {
                throw RubyExceptions.CreateRangeError(String.Format("time + {0} out of Time range", context.Inspect(seconds).ToString()));
            }
        }

        [RubyMethod("-")]
        public static object Subtract(RubyContext/*!*/ context, RubyTime/*!*/ self, object other) {
            RubyTime time = other as RubyTime;
            if (time != null) {
                return (self.ToExactSeconds() - time.ToExactSeconds()).ToDouble();
            }
            if (other is DateTime) {
                return (self.UtcDateTime - RubyTime.ToUniversalTime((DateTime)other)).TotalSeconds;
            }
            try {
                return Shift(self, -ToExact(context, other));
            } catch (OverflowException) {
                throw RubyExceptions.CreateRangeError(String.Format("time - {0} out of Time range", context.Inspect(other).ToString()));
            }
        }

        [RubyMethod("<=>")]
        public static object CompareTo(RubyContext/*!*/ context, RubyTime/*!*/ self, object other) {
            RubyTime time = other as RubyTime;
            if (time != null) {
                return ScriptingRuntimeHelpers.Int32ToObject(self.CompareTo(time));
            }

            // MRI asks the other object to compare itself against us and inverts the answer.
            object result = Invoke(context, ref _compareSite, "<=>", other, self);
            if (result == null) {
                return null;
            }

            object greater = Invoke(context, ref _greaterSite, ">", result, ScriptingRuntimeHelpers.Int32ToObject(0));
            if (RubyOps.IsTrue(greater)) {
                return ScriptingRuntimeHelpers.Int32ToObject(-1);
            }

            object less = Invoke(context, ref _lessSite, "<", result, ScriptingRuntimeHelpers.Int32ToObject(0));
            if (RubyOps.IsTrue(less)) {
                return ScriptingRuntimeHelpers.Int32ToObject(1);
            }

            return ScriptingRuntimeHelpers.Int32ToObject(0);
        }

        [RubyMethod("==")]
        public static bool Equal(RubyTime/*!*/ self, object other) {
            RubyTime time = other as RubyTime;
            return time != null && self.CompareTo(time) == 0;
        }

        [RubyMethod("eql?")]
        public static bool Eql(RubyTime/*!*/ self, object other) {
            RubyTime time = other as RubyTime;
            return time != null && self.Equals(time);
        }

        [RubyMethod("hash")]
        public static int GetHash(RubyTime/*!*/ self) {
            return self.GetHashCode();
        }

        #endregion

        #region utc, utc?, dst?, utc_offset, getlocal, zone

        [RubyMethod("gmtime")]
        [RubyMethod("utc")]
        public static RubyTime/*!*/ SwitchToUtc(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            if (!self.IsUtc) {
                if (context.IsObjectFrozen(self)) {
                    throw RubyExceptions.CreateObjectFrozenError(context, self);
                }
            }
            self.CopyFrom(self.WithZone(RubyTimeZoneKind.Utc, ExactNum.Zero, null));
            return self;
        }

        [RubyMethod("localtime")]
        public static RubyTime/*!*/ SwitchToLocalTime(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object zone) {
            RubyTime result = GetLocal(context, self, zone);
            if (result.ZoneKind != self.ZoneKind || result.UtcOffsetExact.CompareTo(self.UtcOffsetExact) != 0) {
                if (context.IsObjectFrozen(self)) {
                    throw RubyExceptions.CreateObjectFrozenError(context, self);
                }
            }
            self.CopyFrom(result);
            return self;
        }

        [RubyMethod("getlocal")]
        public static RubyTime/*!*/ GetLocal(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object zone) {
            if (zone == Missing.Value || zone == null) {
                var local = self.WithZone(RubyTimeZoneKind.Local, ExactNum.Zero, null);
                // Already local: keep the zone it was made in rather than re-resolving against TZ.
                local.InheritLocalZone(self);
                return local;
            }

            ExactNum offset;
            object zoneObject;
            RubyTimeZoneKind kind = ResolveZone(context, context.GetClassOf(self), zone, out offset, out zoneObject);
            if (zoneObject != null) {
                return ApplyZoneObjectToInstant(context, self, zoneObject);
            }
            return self.WithZone(kind, offset, zoneObject);
        }

        [RubyMethod("gmt?")]
        [RubyMethod("utc?")]
        public static bool IsUtc(RubyTime/*!*/ self) {
            return self.IsUtc;
        }

        [RubyMethod("dst?")]
        [RubyMethod("isdst")]
        public static bool IsDst(RubyTime/*!*/ self) {
            return self.IsDaylightSavingTime;
        }

        [RubyMethod("gmtoff")]
        [RubyMethod("utc_offset")]
        [RubyMethod("gmt_offset")]
        public static object Offset(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, RubyTime/*!*/ self) {

            ExactNum offset = self.UtcOffsetExact;
            if (offset.IsInteger) {
                return Protocols.Normalize(offset.Numerator);
            }
            return MakeRational(toRational, scope, offset);
        }

        [RubyMethod("getgm")]
        [RubyMethod("getutc")]
        public static RubyTime/*!*/ GetUTC(RubyTime/*!*/ self) {
            return self.WithZone(RubyTimeZoneKind.Utc, ExactNum.Zero, null);
        }

        [RubyMethod("zone")]
        public static object GetZone(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            if (self.ZoneObject != null) {
                return self.ZoneObject;
            }
            string name = self.GetZoneName();
            if (name == null) {
                return null;
            }
            return name.IsAscii()
                ? MutableString.CreateAscii(name)
                : MutableString.Create(name, context.GetPathEncoding());
        }

        #endregion

        #region hour, min, sec, usec, nsec, subsec, year, mon, day, yday, wday

        [RubyMethod("hour")]
        public static int Hour(RubyTime/*!*/ self) {
            return self.GetFields().Hour;
        }

        [RubyMethod("min")]
        public static int Minute(RubyTime/*!*/ self) {
            return self.GetFields().Minute;
        }

        [RubyMethod("sec")]
        public static int Second(RubyTime/*!*/ self) {
            return self.GetFields().Second;
        }

        [RubyMethod("tv_usec")]
        [RubyMethod("usec")]
        public static int GetMicroSeconds(RubyTime/*!*/ self) {
            return self.Microseconds;
        }

        [RubyMethod("tv_nsec")]
        [RubyMethod("nsec")]
        public static int GetNanoSeconds(RubyTime/*!*/ self) {
            return self.Nanoseconds;
        }

        [RubyMethod("subsec")]
        public static object GetSubsec(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, RubyTime/*!*/ self) {

            if (self.Subsec.IsZero) {
                return ScriptingRuntimeHelpers.Int32ToObject(0);
            }
            return MakeRational(toRational, scope, self.Subsec);
        }

        [RubyMethod("year")]
        public static int Year(RubyTime/*!*/ self) {
            return self.GetFields().Year;
        }

        [RubyMethod("mon")]
        [RubyMethod("month")]
        public static int Month(RubyTime/*!*/ self) {
            return self.GetFields().Month;
        }

        [RubyMethod("mday")]
        [RubyMethod("day")]
        public static int Day(RubyTime/*!*/ self) {
            return self.GetFields().Day;
        }

        [RubyMethod("yday")]
        public static int DayOfYear(RubyTime/*!*/ self) {
            return self.GetFields().DayOfYear;
        }

        [RubyMethod("wday")]
        public static int DayOfWeek(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek;
        }

        [RubyMethod("sunday?")]
        public static bool IsSunday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 0;
        }

        [RubyMethod("monday?")]
        public static bool IsMonday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 1;
        }

        [RubyMethod("tuesday?")]
        public static bool IsTuesday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 2;
        }

        [RubyMethod("wednesday?")]
        public static bool IsWednesday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 3;
        }

        [RubyMethod("thursday?")]
        public static bool IsThursday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 4;
        }

        [RubyMethod("friday?")]
        public static bool IsFriday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 5;
        }

        [RubyMethod("saturday?")]
        public static bool IsSaturday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 6;
        }

        #endregion

        #region round, floor, ceil

        [RubyMethod("round")]
        public static RubyTime/*!*/ Round(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object ndigits) {
            return RoundTo(context, self, ndigits, 0);
        }

        [RubyMethod("floor")]
        public static RubyTime/*!*/ Floor(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object ndigits) {
            return RoundTo(context, self, ndigits, -1);
        }

        [RubyMethod("ceil")]
        public static RubyTime/*!*/ Ceil(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object ndigits) {
            return RoundTo(context, self, ndigits, 1);
        }

        private static RubyTime/*!*/ RoundTo(RubyContext/*!*/ context, RubyTime/*!*/ self, object ndigits, int mode) {
            int digits;
            if (ndigits == Missing.Value) {
                digits = 0;
            } else if (ndigits == null) {
                digits = 0;
            } else if (ndigits is MutableString) {
                // NUM2INT: a String is not converted here
                throw RubyExceptions.CreateImplicitConversionError("String", "Integer");
            } else {
                digits = ToIntComponent(context, ndigits);
            }
            if (digits < 0) {
                throw RubyExceptions.CreateArgumentError("negative ndigits given");
            }

            ExactNum scale = ExactNum.FromInteger(BigInteger.Pow(new BigInteger(10), digits));
            ExactNum scaled = self.ToExactSeconds() * scale;

            BigInteger rounded;
            if (mode < 0) {
                rounded = scaled.Floor();
            } else if (mode > 0) {
                rounded = scaled.Ceiling();
            } else {
                // MRI rounds a half up (towards +Infinity): Time.at(-1.5).round is -1
                rounded = (scaled + ExactNum.Make(BigInteger.One, new BigInteger(2))).Floor();
            }

            ExactNum result = ExactNum.Make(rounded, scale.Numerator);
            var time = RubyTime.FromExactSeconds(result, self.ZoneKind,
                self.ZoneKind == RubyTimeZoneKind.FixedOffset ? self.UtcOffsetExact : ExactNum.Zero);
            time.ZoneObject = self.ZoneObject;
            time.InheritLocalZone(self);
            return time;
        }

        #endregion

        #region to_f, to_i, to_r, to_a, deconstruct_keys

        [RubyMethod("to_f")]
        public static double ToFloatSeconds(RubyTime/*!*/ self) {
            return self.ToExactSeconds().ToDouble();
        }

        [RubyMethod("tv_sec")]
        [RubyMethod("to_i")]
        public static object/*!*/ ToSeconds(RubyTime/*!*/ self) {
            return Protocols.Normalize(new BigInteger(self.Seconds));
        }

        [RubyMethod("to_r")]
        public static object ToRational(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, RubyTime/*!*/ self) {

            return MakeRational(toRational, scope, self.ToExactSeconds());
        }

        [RubyMethod("asctime")]
        [RubyMethod("ctime")]
        public static MutableString/*!*/ CTime(RubyTime/*!*/ self) {
            RubyTime.Fields t = self.GetFields();
            return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture,
                "{0} {1} {2,2} {3:D2}:{4:D2}:{5:D2} {6:D4}",
                Strftime.DayNamesAbbreviated[t.DayOfWeek], Strftime.MonthNamesAbbreviated[t.Month - 1],
                t.Day, t.Hour, t.Minute, t.Second, t.Year
            ));
        }

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToString(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            RubyTime.Fields t = self.GetFields();
            return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture,
                "{0:D4}-{1:D2}-{2:D2} {3:D2}:{4:D2}:{5:D2} {6}",
                t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second,
                self.IsUtc ? "UTC" : self.FormatUtcOffset()
            ));
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            RubyTime.Fields t = self.GetFields();
            string subsec = FormatSubsecForInspect(self.Subsec);
            return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture,
                "{0:D4}-{1:D2}-{2:D2} {3:D2}:{4:D2}:{5:D2}{6} {7}",
                t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, subsec,
                self.IsUtc ? "UTC" : self.FormatUtcOffset()
            ));
        }

        private static string/*!*/ FormatSubsecForInspect(ExactNum subsec) {
            if (subsec.IsZero) {
                return "";
            }

            // Exactly representable in nanoseconds? Then print the decimal digits.
            ExactNum scaled = subsec * ExactNum.FromInteger(RubyTime.NanosecondsPerSecond);
            if (scaled.IsInteger) {
                string digits = ((BigInteger)scaled.Numerator).ToString(CultureInfo.InvariantCulture).PadLeft(9, '0');
                digits = digits.TrimEnd('0');
                return "." + digits;
            }
            return " " + subsec.Numerator.ToString(CultureInfo.InvariantCulture) + "/" + subsec.Denominator.ToString(CultureInfo.InvariantCulture);
        }

        [RubyMethod("to_a")]
        public static RubyArray/*!*/ ToArray(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            RubyTime.Fields t = self.GetFields();
            RubyArray result = new RubyArray();
            result.Add(t.Second);
            result.Add(t.Minute);
            result.Add(t.Hour);
            result.Add(t.Day);
            result.Add(t.Month);
            result.Add(t.Year);
            result.Add(t.DayOfWeek);
            result.Add(t.DayOfYear);
            result.Add(self.IsDaylightSavingTime);
            result.Add(GetZone(context, self));
            return result;
        }

        [RubyMethod("deconstruct_keys")]
        public static Hash/*!*/ DeconstructKeys(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, RubyTime/*!*/ self, object keys) {

            RubyContext context = scope.RubyContext;
            var result = new Hash(context);

            IList list = null;
            if (keys != null) {
                list = keys as IList;
                if (list == null) {
                    throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Array or nil)",
                        context.GetClassName(keys));
                }
            }

            if (list == null) {
                foreach (string name in _deconstructKeys) {
                    result[context.CreateAsciiSymbol(name)] = GetDeconstructValue(toRational, scope, self, name);
                }
                return result;
            }

            foreach (object key in list) {
                var symbol = key as RubySymbol;
                if (symbol == null) {
                    continue;
                }
                string name = symbol.ToString();
                if (Array.IndexOf(_deconstructKeys, name) < 0) {
                    continue;
                }
                result[symbol] = GetDeconstructValue(toRational, scope, self, name);
            }
            return result;
        }

        private static readonly string[]/*!*/ _deconstructKeys = new[] {
            "year", "month", "day", "yday", "wday", "hour", "min", "sec", "subsec", "dst", "zone"
        };

        private static object GetDeconstructValue(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, RubyTime/*!*/ self, string/*!*/ name) {

            switch (name) {
                case "year": return ScriptingRuntimeHelpers.Int32ToObject(Year(self));
                case "month": return ScriptingRuntimeHelpers.Int32ToObject(Month(self));
                case "day": return ScriptingRuntimeHelpers.Int32ToObject(Day(self));
                case "yday": return ScriptingRuntimeHelpers.Int32ToObject(DayOfYear(self));
                case "wday": return ScriptingRuntimeHelpers.Int32ToObject(DayOfWeek(self));
                case "hour": return ScriptingRuntimeHelpers.Int32ToObject(Hour(self));
                case "min": return ScriptingRuntimeHelpers.Int32ToObject(Minute(self));
                case "sec": return ScriptingRuntimeHelpers.Int32ToObject(Second(self));
                case "subsec": return GetSubsec(toRational, scope, self);
                case "dst": return ScriptingRuntimeHelpers.BooleanToObject(self.IsDaylightSavingTime);
                default: return GetZone(scope.RubyContext, self);
            }
        }

        #endregion

        #region xmlschema

        [RubyMethod("xmlschema")]
        [RubyMethod("iso8601")]
        public static MutableString/*!*/ XmlSchema(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object fractionDigits) {
            int digits = (fractionDigits == Missing.Value || fractionDigits == null) ? 0 : ToIntComponent(context, fractionDigits);
            RubyTime.Fields t = self.GetFields();

            var result = new StringBuilder();
            if (t.Year < 0) {
                result.Append('-');
                result.Append((-(long)t.Year).ToString(CultureInfo.InvariantCulture).PadLeft(4, '0'));
            } else {
                result.Append(t.Year.ToString(CultureInfo.InvariantCulture).PadLeft(4, '0'));
            }
            result.AppendFormat(CultureInfo.InvariantCulture, "-{0:D2}-{1:D2}T{2:D2}:{3:D2}:{4:D2}",
                t.Month, t.Day, t.Hour, t.Minute, t.Second);

            if (digits > 0) {
                BigInteger scale = BigInteger.Pow(new BigInteger(10), digits);
                BigInteger value = (self.Subsec.Numerator * scale) / self.Subsec.Denominator;
                result.Append('.');
                result.Append(value.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'));
            }

            result.Append(self.IsUtc ? "Z" : self.FormatUtcOffset(true, false));
            return MutableString.CreateAscii(result.ToString());
        }

        #endregion

        #region strftime

        /// <summary>The name %Z should print: a timezone object's #abbr wins over the zone name.</summary>
        internal static string GetZoneAbbreviation(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            object zoneObject = self.ZoneObject;
            // A zone name loaded from a dump is what MRI prints; the time's fixed offset has none.
            var zoneName = zoneObject as MutableString;
            if (zoneName != null) {
                return zoneName.ConvertToString();
            }
            if (zoneObject != null && RespondTo(context, zoneObject, "abbr")) {
                var abbr = Invoke(context, ref _abbrSite, "abbr", zoneObject, self) as MutableString;
                if (abbr != null) {
                    return abbr.ConvertToString();
                }
            }
            return self.GetZoneName();
        }

        [RubyMethod("strftime")]
        public static MutableString/*!*/ FormatTime(RubyContext/*!*/ context, RubyTime/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ format) {

            if (!format.Encoding.IsAsciiIdentity) {
                throw RubyExceptions.CreateArgumentError("format should have ASCII compatible encoding");
            }
            MutableString result = MutableString.CreateMutable(format.Encoding);
            Strftime.Format(context, result, self, format.ConvertToString(), 0);
            return result;
        }

        #endregion
    }
}

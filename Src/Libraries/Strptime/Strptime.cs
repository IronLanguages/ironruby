/* ****************************************************************************
 *
 * IronRuby's implementation of the strptime gem's C extension.
 *
 * strptime 0.2.5 (a fast strptime/strftime engine, used by fluentd for every
 * time_format it parses) is Ruby over one C extension, strptime/strptime:
 * the classes Strptime and Strftime.  Each compiles its format into a small
 * instruction list once and runs it per call.  This is ext/strptime/strptime.c,
 * strftime.c and the parts of ruby_time.c they use, in C#, keeping what is
 * observable:
 *
 *   - which directives a format may use (anything else is "invalid format"),
 *     and that whitespace in the format skips any run of whitespace;
 *   - field widths and ranges (%H 0-23, %S 0-60, %d/%e 1-31, %m 1-12, %Y up
 *     to four digits with a sign, %y 69-99 -> 19xx, %N up to nine digits,
 *     %z as Z / +hh / +hhmm / +hh:mm), that trailing input is ignored, and
 *     that the C reads the string as NUL-terminated;
 *   - the date arithmetic: a day past the end of the month rolls over (Feb 31
 *     is Mar 3), a 60th second is the next minute, and with no %Y the fields
 *     that are given overwrite the current time from the first one given down;
 *   - the Time it makes: UTC for "Z", a fixed offset for an explicit offset
 *     (ArgumentError "utc_offset out of range" past a day), local time without
 *     one - except with no %Y, where it is fixed at the current local offset;
 *   - Strftime's quirks: %Y maps tm_year below 69 to 20xx, %z masks the hours
 *     with 15, and the output keeps the format's encoding.
 *
 * The gem's own Ruby (lib/strptime.rb, Strptime::VERSION) is not vendored: an
 * installed strptime gem loads on top of this, the way a platform gem would.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Strptime {

    /// <summary>The parts of ruby_time.c strptime.c and strftime.c use.</summary>
    internal static class CTime {
        internal struct Tm {
            public long Year;   // tm_year: years since 1900
            public int Mon;     // 0-11
            public int MDay;
            public int Hour;
            public int Min;
            public int Sec;
        }

        private static readonly int[] CommonYearYdayOffset = {
            -1, -1 + 31, -1 + 31 + 28, -1 + 31 + 28 + 31, -1 + 31 + 28 + 31 + 30,
            -1 + 31 + 28 + 31 + 30 + 31, -1 + 31 + 28 + 31 + 30 + 31 + 30,
            -1 + 31 + 28 + 31 + 30 + 31 + 30 + 31, -1 + 31 + 28 + 31 + 30 + 31 + 30 + 31 + 31,
            -1 + 31 + 28 + 31 + 30 + 31 + 30 + 31 + 31 + 30,
            -1 + 31 + 28 + 31 + 30 + 31 + 30 + 31 + 31 + 30 + 31,
            -1 + 31 + 28 + 31 + 30 + 31 + 30 + 31 + 31 + 30 + 31 + 30,
        };

        private static readonly int[] LeapYearYdayOffset = {
            -1, -1 + 31, -1 + 31 + 29, -1 + 31 + 29 + 31, -1 + 31 + 29 + 31 + 30,
            -1 + 31 + 29 + 31 + 30 + 31, -1 + 31 + 29 + 31 + 30 + 31 + 30,
            -1 + 31 + 29 + 31 + 30 + 31 + 30 + 31, -1 + 31 + 29 + 31 + 30 + 31 + 30 + 31 + 31,
            -1 + 31 + 29 + 31 + 30 + 31 + 30 + 31 + 31 + 30,
            -1 + 31 + 29 + 31 + 30 + 31 + 30 + 31 + 31 + 30 + 31,
            -1 + 31 + 29 + 31 + 30 + 31 + 30 + 31 + 31 + 30 + 31 + 30,
        };

        private static bool LeapYear(long y) {
            return ((y % 4 == 0) && (y % 100 != 0)) || (y % 400 == 0);
        }

        // DIV(n,d): floor division
        private static long Div(long n, long d) {
            return n < 0 ? -(-(n + 1) / d) - 1 : n / d;
        }

        /// <summary>timegm_noleapsecond</summary>
        internal static long TimeGm(ref Tm tm) {
            long tmYear = tm.Year;
            long yday = tm.MDay + (LeapYear(tmYear + 1900) ? LeapYearYdayOffset[tm.Mon] : CommonYearYdayOffset[tm.Mon]);
            return tm.Sec + tm.Min * 60 + tm.Hour * 3600 +
                (yday + (tmYear - 70) * 365 + Div(tmYear - 69, 4) - Div(tmYear - 1, 100) + Div(tmYear + 299, 400)) * 86400;
        }

        /// <summary>gmtime_r</summary>
        internal static Tm GmTime(long t) {
            long days = t >= 0 ? t / 86400 : -((-t + 86399) / 86400);
            int secondOfDay = (int)(t - days * 86400);
            long y;
            int m, d;
            RubyTime.CivilFromDays(days, out y, out m, out d);
            Tm tm;
            tm.Year = y - 1900;
            tm.Mon = m - 1;
            tm.MDay = d;
            tm.Hour = secondOfDay / 3600;
            tm.Min = (secondOfDay / 60) % 60;
            tm.Sec = secondOfDay % 60;
            return tm;
        }

        /// <summary>rb_timespec_now (CLOCK_REALTIME)</summary>
        internal static void Now(out long sec, out int nsec) {
            long ticks = DateTime.UtcNow.Ticks - RubyTime.Epoch.Ticks;
            sec = ticks >= 0 ? ticks / TimeSpan.TicksPerSecond : -((-ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);
            nsec = (int)(ticks - sec * TimeSpan.TicksPerSecond) * 100;
        }

        /// <summary>The local zone's offset at an instant (localtime_r's tm_gmtoff).</summary>
        internal static long LocalOffset(long t) {
            return RubyTime._CurrentTimeZone.GetOffset(t);
        }

        /// <summary>
        /// find_time_t for local time: the instant whose local wall clock reads tm.  A wall clock
        /// that occurs twice (the hour DST ends) is the first one, in daylight time; one that does
        /// not occur (the hour DST starts) is read with the offset in force before the change.
        /// </summary>
        internal static long FindLocalTime(ref Tm tm) {
            long wall = TimeGm(ref tm);
            return wall - RubyTime._CurrentTimeZone.GetOffsetForWallClock(wall, true);
        }
    }

    internal sealed class StrptimeProgram {
        // Instructions; a literal is followed by its operand, (count << 16) + offset.
        internal const int OpB = 'B', OpH = 'H', OpM = 'M', OpN = 'N', OpS = 'S', OpY = 'Y', OpD = 'd',
            OpMon = 'm', OpSpace = 'n', OpY2 = 'y', OpZ = 'z', OpLiteral = '`', OpEnd = '_';

        private static readonly byte[][] MonthNames = {
            Ascii("January"), Ascii("February"), Ascii("March"), Ascii("April"), Ascii("May"), Ascii("June"),
            Ascii("July"), Ascii("August"), Ascii("September"), Ascii("October"), Ascii("November"), Ascii("December"),
            Ascii("Jan"), Ascii("Feb"), Ascii("Mar"), Ascii("Apr"), Ascii("May"), Ascii("Jun"),
            Ascii("Jul"), Ascii("Aug"), Ascii("Sep"), Ascii("Oct"), Ascii("Nov"), Ascii("Dec"),
        };

        private static byte[] Ascii(string s) {
            return System.Text.Encoding.ASCII.GetBytes(s);
        }

        private readonly long[]/*!*/ _insns;
        private readonly byte[]/*!*/ _fmt;

        private StrptimeProgram(long[]/*!*/ insns, byte[]/*!*/ fmt) {
            _insns = insns;
            _fmt = fmt;
        }

        private static bool IsSpace(int c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';
        }

        private static bool IsDigit(int c) {
            return unchecked((uint)(c - '0')) <= 9u;
        }

        /// <summary>strptime_compile</summary>
        internal static StrptimeProgram/*!*/ Compile(byte[]/*!*/ fmt) {
            int flen = fmt.Length;
            var isns = new System.Collections.Generic.List<long>(flen + 2);
            int fi = 0;
            while (fi < flen) {
                switch (fmt[fi]) {
                    case (byte)'%': {
                            fi++;
                            int c = fi < flen ? fmt[fi] : 0;
                            switch (c) {
                                case 'B': case 'b': case 'h': isns.Add(OpB); break;
                                case 'H': isns.Add(OpH); break;
                                case 'M': isns.Add(OpM); break;
                                case 'N': isns.Add(OpN); break;
                                case 'S': isns.Add(OpS); break;
                                case 'Y': isns.Add(OpY); break;
                                case 'd': case 'e': isns.Add(OpD); break;
                                case 'm': isns.Add(OpMon); break;
                                case 'n': isns.Add(OpSpace); break;
                                case 'y': isns.Add(OpY2); break;
                                case 'z': isns.Add(OpZ); break;
                                default:
                                    throw RubyExceptions.CreateArgumentError("invalid format");
                            }
                            fi++;
                            break;
                        }
                    case (byte)' ':
                    case (byte)'\t':
                    case (byte)'\n':
                    case (byte)'\v':
                    case (byte)'\f':
                    case (byte)'\r':
                        isns.Add(OpSpace);
                        fi++;
                        break;
                    default: {
                            int p = fi;
                            while (p < flen && fmt[p] != '%' && !IsSpace(fmt[p])) {
                                p++;
                            }
                            // v = fi + (len << 16), as the C packs it
                            long v = (long)fi + ((long)(p - fi) << 16);
                            fi = p;
                            isns.Add(OpLiteral);
                            isns.Add(v);
                            break;
                        }
                }
            }
            isns.Add(OpEnd);
            return new StrptimeProgram(isns.ToArray(), fmt);
        }

        /// <summary>read_digits over a NUL-terminated string</summary>
        private static int ReadDigits(byte[]/*!*/ str, int si, out int n, int width) {
            int s = si, se = si + width, r = 0;
            for (; s < se && s < str.Length && IsDigit(str[s]); s++) {
                r *= 10;
                r += str[s] - '0';
            }
            n = r;
            return s - si;
        }

        private static int At(byte[]/*!*/ str, int i) {
            return i < str.Length ? str[i] : 0;
        }

        /// <summary>
        /// strptime_exec0.  Returns false when the string does not match; on success t, nsec and
        /// gmtoff are what the C stores (gmtoff is Int32.MaxValue for local time and
        /// Int32.MaxValue - 1 for UTC).
        /// </summary>
        internal bool Exec(byte[]/*!*/ str, out long t, out int nsecOut, out int gmtoffOut) {
            t = 0;
            nsecOut = 0;
            gmtoffOut = Int32.MaxValue;
            int si = 0;
            int slen = str.Length;
            int year = Int32.MaxValue, mon = -1, mday = -1, hour = -1, min = -1, sec = -1, nsec = 0, gmtoff = Int32.MaxValue;
            long[] insns = _insns;
            int pc = 0;
            int l;

            while (true) {
                switch ((int)insns[pc]) {
                    case OpB: {
                            bool found = false;
                            for (int i = 0; i < MonthNames.Length; i++) {
                                byte[] name = MonthNames[i];
                                if (StrNCaseEqual(name, str, si)) {
                                    si += name.Length;
                                    mon = (i % 12) + 1;
                                    found = true;
                                    break;
                                }
                            }
                            if (!found) {
                                return false;
                            }
                            pc++;
                            break;
                        }
                    case OpH:
                        l = ReadDigits(str, si, out hour, 2);
                        if (l == 0) return false;
                        si += l;
                        if (hour < 0 || hour > 23) return false;
                        pc++;
                        break;
                    case OpM:
                        l = ReadDigits(str, si, out min, 2);
                        if (l == 0) return false;
                        si += l;
                        if (min < 0 || min > 59) return false;
                        pc++;
                        break;
                    case OpN:
                        l = ReadDigits(str, si, out nsec, 9);
                        if (l == 0) return false;
                        si += l;
                        for (; l < 9; l++) {
                            nsec *= 10;
                        }
                        pc++;
                        break;
                    case OpS:
                        l = ReadDigits(str, si, out sec, 2);
                        if (l == 0) return false;
                        si += l;
                        if (sec < 0 || sec > 60) return false;
                        pc++;
                        break;
                    case OpY: {
                            int c = At(str, si);
                            if (c == '-' || c == '+') si++;
                            l = ReadDigits(str, si, out year, 4);
                            if (l == 0) return false;
                            si += l;
                            if (c == '-') year *= -1;
                            pc++;
                            break;
                        }
                    case OpD:
                        l = ReadDigits(str, si, out mday, 2);
                        if (l == 0) return false;
                        si += l;
                        if (mday < 1 || mday > 31) return false;
                        pc++;
                        break;
                    case OpMon:
                        l = ReadDigits(str, si, out mon, 2);
                        if (l == 0) return false;
                        si += l;
                        if (mon < 1 || mon > 12) return false;
                        pc++;
                        break;
                    case OpSpace:
                        for (; si < slen && IsSpace(str[si]); si++) {
                        }
                        pc++;
                        break;
                    case OpY2:
                        l = ReadDigits(str, si, out year, 2);
                        if (l == 0) return false;
                        si += l;
                        year += year < 69 ? 2000 : 1900;
                        pc++;
                        break;
                    case OpZ: {
                            int p0 = At(str, si);
                            if (p0 == 'z' || p0 == 'Z') {
                                // sic: the C does not consume the letter
                                gmtoff = Int32.MaxValue - 1;
                                pc++;
                                break;
                            }
                            if (p0 == '-' || p0 == '+') si++;
                            int r;
                            l = ReadDigits(str, si, out r, 2);
                            if (l == 0) return false;
                            si += l;
                            gmtoff = r * 60;
                            if (At(str, si) == ':') si++;
                            int len = ReadDigits(str, si, out r, 2);
                            if (len != 0) {
                                si += len;
                                gmtoff += r;
                            }
                            gmtoff *= 60;
                            if (p0 == '-') gmtoff = -gmtoff;
                            pc++;
                            break;
                        }
                    case OpLiteral: {
                            long v = insns[pc + 1];
                            int fi = (int)(v & 0xFFFF);
                            int cnt = (int)(v >> 16);
                            for (int k = 0; k < cnt; k++) {
                                if (At(str, si + k) != At(_fmt, fi + k)) {
                                    return false;
                                }
                            }
                            pc += 2;
                            si += cnt;
                            break;
                        }
                    case OpEnd:
                        return Finish(year, mon, mday, hour, min, sec, nsec, gmtoff, out t, out nsecOut, out gmtoffOut);
                    default:
                        throw new InvalidOperationException("strptime: unknown insn");
                }
            }
        }

        private static bool StrNCaseEqual(byte[]/*!*/ name, byte[]/*!*/ str, int si) {
            for (int i = 0; i < name.Length; i++) {
                int a = name[i], b = At(str, si + i);
                if (a >= 'A' && a <= 'Z') a += 32;
                if (b >= 'A' && b <= 'Z') b += 32;
                if (a != b) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>The _5f instruction: turn the fields into an instant.</summary>
        private static bool Finish(int year, int mon, int mday, int hour, int min, int sec, int nsec, int gmtoff,
            out long t, out int nsecOut, out int gmtoffOut) {

            int gmt = gmtoff >= Int32.MaxValue - 1 ? Int32.MaxValue - gmtoff : 2;
            CTime.Tm tm = default(CTime.Tm);

            if (year != Int32.MaxValue) {
                tm.Year = (long)year - 1900;
                if (mon == -1) mon = 1;
                tm.Mon = mon - 1;
                if (mday == -1) mday = 1;
                tm.MDay = mday;
                if (hour == -1) hour = 0;
                tm.Hour = hour;
                if (min == -1) min = 0;
                tm.Min = min;
                if (sec == -1) sec = 0;
                tm.Sec = sec;
            } else {
                long nowSec;
                int nowNsec;
                CTime.Now(out nowSec, out nowNsec);
                if (gmt != 0) {
                    long now = nowSec;
                    if (gmt == 2) now += gmtoff;
                    tm = CTime.GmTime(now);
                } else {
                    long off = CTime.LocalOffset(nowSec);
                    tm = CTime.GmTime(nowSec + off);
                    gmtoff = (int)off;
                }
                // the C's goto chain: the first field given and every one after it
                if (mon != -1) {
                    tm.Mon = mon - 1;
                    if (mday == -1) mday = 1;
                    goto setmday;
                }
                if (mday != -1) goto setmday;
                if (hour != -1) goto sethour;
                if (min != -1) goto setmin;
                if (sec != -1) tm.Sec = sec;
                goto done;
            setmday:
                tm.MDay = mday;
                if (hour == -1) hour = 0;
            sethour:
                tm.Hour = hour;
                if (min == -1) min = 0;
            setmin:
                tm.Min = min;
                if (sec == -1) sec = 0;
                tm.Sec = sec;
            done:
                ;
            }

            if (gmt != 0) {
                t = CTime.TimeGm(ref tm);
                if (gmt == 2) t -= gmtoff;
            } else {
                t = CTime.FindLocalTime(ref tm);
            }
            nsecOut = nsec;
            gmtoffOut = gmtoff;
            return true;
        }
    }

    internal sealed class StrftimeProgram {
        internal const int OpH = 'H', OpL = 'L', OpM = 'M', OpN = 'N', OpS = 'S', OpY = 'Y', OpD = 'd', OpB = 'b',
            OpMon = 'm', OpY2 = 'y', OpZ = 'z', OpLiteral = '`', OpEnd = '_';

        private static readonly string[] MonthNames = {
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December"
        };

        private readonly long[]/*!*/ _insns;
        private readonly byte[]/*!*/ _fmt;
        internal readonly int ResultLength;

        private StrftimeProgram(long[]/*!*/ insns, byte[]/*!*/ fmt, int resultLength) {
            _insns = insns;
            _fmt = fmt;
            ResultLength = resultLength;
        }

        /// <summary>strftime_compile</summary>
        internal static StrftimeProgram/*!*/ Compile(byte[]/*!*/ fmt) {
            int flen = fmt.Length;
            if (flen > 65535) {
                throw RubyExceptions.CreateArgumentError("too long format string (>65335)");
            }
            var isns = new System.Collections.Generic.List<long>(flen + 2);
            int fi = 0, rlen = 0;
            while (fi < flen) {
                if (fmt[fi] == '%') {
                    fi++;
                    int c = fi < flen ? fmt[fi] : 0;
                    switch (c) {
                        case 'H': rlen += 2; break;
                        case 'L': rlen += 3; break;
                        case 'M': rlen += 2; break;
                        case 'N': rlen += 9; break;
                        case 'S': rlen += 2; break;
                        case 'Y': rlen += 4; break;
                        case 'd': rlen += 2; break;
                        case 'b': rlen += 3; break;
                        case 'm': rlen += 2; break;
                        case 'y': rlen += 2; break;
                        case 'z': rlen += 5; break;
                        default:
                            throw RubyExceptions.CreateArgumentError("invalid format");
                    }
                    isns.Add(c);
                    fi++;
                } else {
                    int p = fi;
                    while (p < flen && fmt[p] != '%') {
                        p++;
                    }
                    long v = (long)fi + ((long)(p - fi) << 16);
                    rlen += p - fi;
                    fi = p;
                    isns.Add(OpLiteral);
                    isns.Add(v);
                }
            }
            isns.Add(OpEnd);
            return new StrftimeProgram(isns.ToArray(), fmt, rlen);
        }

        private static byte Digit(int v) {
            return unchecked((byte)('0' + v));
        }

        /// <summary>strftime_exec0</summary>
        internal byte[]/*!*/ Exec(long tvSec, int tvNsec, int gmtoff) {
            var result = new byte[ResultLength];
            int p = 0;
            tvSec += gmtoff;
            CTime.Tm tm = CTime.GmTime(tvSec);
            long[] insns = _insns;
            int pc = 0;
            while (true) {
                switch ((int)insns[pc]) {
                    case OpH:
                        result[p++] = Digit(tm.Hour / 10);
                        result[p++] = Digit(tm.Hour % 10);
                        pc++;
                        break;
                    case OpL: {
                            int msec = tvNsec / 1000000;
                            result[p + 2] = Digit(msec % 10);
                            msec /= 10;
                            result[p + 1] = Digit(msec % 10);
                            result[p] = Digit(msec / 10);
                            p += 3;
                            pc++;
                            break;
                        }
                    case OpM:
                        result[p++] = Digit(tm.Min / 10);
                        result[p++] = Digit(tm.Min % 10);
                        pc++;
                        break;
                    case OpN: {
                            int subsec = tvNsec;
                            for (int i = 0; i < 9; i++) {
                                result[p + 9 - i - 1] = Digit(subsec % 10);
                                subsec /= 10;
                            }
                            p += 9;
                            pc++;
                            break;
                        }
                    case OpS:
                        result[p++] = Digit(tm.Sec / 10);
                        result[p++] = Digit(tm.Sec % 10);
                        pc++;
                        break;
                    case OpY: {
                            // sic (upstream's "TODO: Y10K"): tm_year below 69 is read as 20xx
                            int y = unchecked((int)tm.Year);
                            y += y < 69 ? 2000 : 1900;
                            for (int i = 0; i < 4; i++) {
                                result[p + 3 - i] = Digit(y % 10);
                                y /= 10;
                            }
                            p += 4;
                            pc++;
                            break;
                        }
                    case OpD:
                        result[p++] = Digit(tm.MDay / 10);
                        result[p++] = Digit(tm.MDay % 10);
                        pc++;
                        break;
                    case OpB: {
                            string mon = MonthNames[tm.Mon];
                            result[p++] = (byte)mon[0];
                            result[p++] = (byte)mon[1];
                            result[p++] = (byte)mon[2];
                            pc++;
                            break;
                        }
                    case OpMon: {
                            int mon = tm.Mon + 1;
                            result[p++] = Digit(mon / 10);
                            result[p++] = Digit(mon % 10);
                            pc++;
                            break;
                        }
                    case OpY2: {
                            int y = unchecked((int)(tm.Year % 100));
                            result[p++] = Digit(y / 10);
                            result[p++] = Digit(y % 10);
                            pc++;
                            break;
                        }
                    case OpZ: {
                            int tmp = gmtoff;
                            if (gmtoff >= 0) {
                                result[p++] = (byte)'+';
                            } else {
                                result[p++] = (byte)'-';
                                tmp = -tmp;
                            }
                            tmp /= 60;
                            int h = (tmp / 60) & 15;   // "ignore too large offset"
                            int m = tmp % 60;
                            result[p++] = Digit(h / 10);
                            result[p++] = Digit(h % 10);
                            result[p++] = Digit(m / 10);
                            result[p++] = Digit(m % 10);
                            pc++;
                            break;
                        }
                    case OpLiteral: {
                            long v = insns[pc + 1];
                            int off = (int)(v & 0xFFFF);
                            int len = (int)(v >> 16);
                            Buffer.BlockCopy(_fmt, off, result, p, len);
                            p += len;
                            pc += 2;
                            break;
                        }
                    case OpEnd:
                        return result;
                    default:
                        throw new InvalidOperationException("strftime: unknown insn");
                }
            }
        }
    }

    internal static class Support {
        internal static string/*!*/ TypeName(RubyContext/*!*/ context, object obj) {
            if (obj == null) {
                return "nil";
            }
            if (obj is bool) {
                return (bool)obj ? "true" : "false";
            }
            return context.GetClassName(obj);
        }

        private sealed class Sites {
            internal readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>> Sites0 =
                new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>>();
            internal readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>> Sites1 =
                new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>>();
        }

        private static readonly object _sitesKey = new object();

        private static Sites/*!*/ GetSites(RubyContext/*!*/ context) {
            return (Sites)context.GetOrCreateLibraryData(_sitesKey, () => new Sites());
        }

        /// <summary>rb_funcall with no arguments.</summary>
        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name) {
            var site = GetSites(context).Sites0.GetOrAdd(name, n => CallSite<Func<CallSite, object, object>>.Create(
                RubyCallAction.Make(context, n, RubyCallSignature.WithImplicitSelf(0))));
            return site.Target(site, target);
        }

        /// <summary>rb_funcall with one argument.</summary>
        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name, object arg) {
            var site = GetSites(context).Sites1.GetOrAdd(name, n => CallSite<Func<CallSite, object, object, object>>.Create(
                RubyCallAction.Make(context, n, RubyCallSignature.WithImplicitSelf(1))));
            return site.Target(site, target, arg);
        }

        internal static bool RespondTo(RubyContext/*!*/ context, object target, string/*!*/ name) {
            return Protocols.IsTrue(Call(context, target, "respond_to?", context.EncodeIdentifier(name)));
        }

        /// <summary>StringValue: a String, or what #to_str makes of one.</summary>
        internal static MutableString/*!*/ StringValue(RubyContext/*!*/ context, object obj) {
            var str = obj as MutableString;
            if (str != null) {
                return str;
            }
            if (obj != null && !(obj is bool) && RespondTo(context, obj, "to_str")) {
                object converted = Call(context, obj, "to_str");
                str = converted as MutableString;
                if (str != null) {
                    return str;
                }
                throw RubyExceptions.CreateTypeError("can't convert {0} to String ({0}#to_str gives {1})",
                    TypeName(context, obj), TypeName(context, converted));
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into String", TypeName(context, obj));
        }

        internal static bool IsRational(RubyContext/*!*/ context, object value) {
            var cls = context.GetClassOf(value);
            return cls != null && cls.Name == "Rational";
        }

        /// <summary>StringValueCStr</summary>
        internal static MutableString/*!*/ StringValueCStr(RubyContext/*!*/ context, object obj, out byte[]/*!*/ bytes) {
            var str = StringValue(context, obj);
            bytes = str.ToByteArray();
            // str_null_check: a NUL *character*, which in UTF-16/32 is several zero bytes
            int minlen = Math.Max(1, str.Encoding.MinBytesPerChar);
            for (int i = 0; i + minlen <= bytes.Length; i += minlen) {
                bool nul = true;
                for (int k = 0; k < minlen; k++) {
                    if (bytes[i + k] != 0) {
                        nul = false;
                        break;
                    }
                }
                if (nul) {
                    throw RubyExceptions.CreateArgumentError("string contains null byte");
                }
            }
            return str;
        }

        /// <summary>rb_str_new_frozen</summary>
        internal static MutableString/*!*/ NewFrozen(MutableString/*!*/ str) {
            if (str.IsFrozen) {
                return str;
            }
            var copy = str.Clone();
            copy.Freeze();
            return copy;
        }
    }

    /// <summary>
    /// What a Strptime or Strftime instance holds - the C's strptime_object / strftime_object.
    /// Kept beside the object rather than in a CLR subclass of RubyObject: the gem's own Ruby
    /// (strptime/version.rb) defines the class Strptime before requiring the extension, and a
    /// library cannot replace the allocator of a class that exists already.  So both classes
    /// are ordinary Ruby classes (strptime/strptime.rb opens them before loading this) and
    /// their instances plain RubyObjects, as they would be for a class written in Ruby.
    /// </summary>
    internal sealed class CompiledFormat<TProgram> where TProgram : class {
        internal TProgram Program;
        internal MutableString Format;

        private static readonly ConditionalWeakTable<object, CompiledFormat<TProgram>> _table =
            new ConditionalWeakTable<object, CompiledFormat<TProgram>>();

        internal static CompiledFormat<TProgram> Find(object self) {
            CompiledFormat<TProgram> result;
            return _table.TryGetValue(self, out result) ? result : null;
        }

        internal static CompiledFormat<TProgram>/*!*/ GetOrCreate(object self) {
            return _table.GetValue(self, _ => new CompiledFormat<TProgram>());
        }

        /// <summary>GetStrptimeval: raises TypeError for an object initialize never ran on.</summary>
        internal static CompiledFormat<TProgram>/*!*/ Get(RubyContext/*!*/ context, object self) {
            var result = Find(self);
            if (result == null || result.Program == null) {
                throw RubyExceptions.CreateTypeError("uninitialized {0}", context.GetClassName(self));
            }
            return result;
        }

        /// <summary>strptime_init_copy / strftime_init_copy</summary>
        internal static object InitializeCopy(RubyContext/*!*/ context, object self, object other) {
            if (ReferenceEquals(self, other)) {
                return self;
            }
            if (context.GetClassOf(other) != context.GetClassOf(self)) {
                throw RubyExceptions.CreateTypeError("initialize_copy should take same class object");
            }
            var source = Get(context, other);
            var target = Find(self);
            if (target != null && target.Program != null) {
                throw RubyExceptions.CreateTypeError("already initialized {0}", context.GetClassName(self));
            }
            target = GetOrCreate(self);
            target.Program = source.Program;
            target.Format = source.Format;
            return self;
        }
    }

    /// <summary>
    /// Strptime: a parser compiled from a strptime(3)-style format.
    /// </summary>
    [RubyClass("Strptime", Inherits = typeof(object))]
    public static class StrptimeOps {

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static object Initialize(RubyContext/*!*/ context, object self, object format) {
            byte[] bytes;
            var fmt = Support.StringValueCStr(context, format, out bytes);
            var program = StrptimeProgram.Compile(bytes);
            var state = CompiledFormat<StrptimeProgram>.GetOrCreate(self);
            state.Program = program;
            state.Format = Support.NewFrozen(fmt);
            return self;
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static object InitializeCopy(RubyContext/*!*/ context, object self, object other) {
            return CompiledFormat<StrptimeProgram>.InitializeCopy(context, self, other);
        }

        private static void Run(RubyContext/*!*/ context, object self, object str,
            out long t, out int nsec, out int gmtoff) {
            var s = Support.StringValue(context, str);
            var state = CompiledFormat<StrptimeProgram>.Get(context, self);
            if (!state.Program.Exec(s.ToByteArray(), out t, out nsec, out gmtoff)) {
                throw RubyExceptions.CreateArgumentError("string doesn't match");
            }
        }

        /// <summary>exec(str) -> Time</summary>
        [RubyMethod("exec")]
        public static RubyTime/*!*/ Exec(RubyContext/*!*/ context, object self, object str) {
            long t;
            int nsec, gmtoff;
            Run(context, self, str, out t, out nsec, out gmtoff);
            return TimespecNew(t, nsec, gmtoff);
        }

        /// <summary>execi(str) -> Integer epoch</summary>
        [RubyMethod("execi")]
        public static object ExecI(RubyContext/*!*/ context, object self, object str) {
            long t;
            int nsec, gmtoff;
            Run(context, self, str, out t, out nsec, out gmtoff);
            return Protocols.Normalize(t);
        }

        [RubyMethod("source")]
        public static MutableString/*!*/ Source(RubyContext/*!*/ context, object self) {
            return CompiledFormat<StrptimeProgram>.Get(context, self).Format;
        }

        /// <summary>rb_time_timespec_new</summary>
        internal static RubyTime/*!*/ TimespecNew(long sec, int nsec, int offset) {
            ExactNum subsec = nsec == 0 ? ExactNum.Zero : ExactNum.Make(nsec, 1000000000);
            if (-86400 < offset && offset < 86400) {
                return new RubyTime(sec, subsec, RubyTimeZoneKind.FixedOffset, ExactNum.FromInteger(offset));
            }
            if (offset == Int32.MaxValue) {
                return new RubyTime(sec, subsec, RubyTimeZoneKind.Local, ExactNum.Zero);
            }
            if (offset == Int32.MaxValue - 1) {
                return new RubyTime(sec, subsec, RubyTimeZoneKind.Utc, ExactNum.Zero);
            }
            throw RubyExceptions.CreateArgumentError("utc_offset out of range");
        }
    }

    /// <summary>
    /// Strftime: a formatter compiled from a strftime(3)-style format.
    /// </summary>
    [RubyClass("Strftime", Inherits = typeof(object))]
    public static class StrftimeOps {

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static object Initialize(RubyContext/*!*/ context, object self, object format) {
            byte[] bytes;
            var fmt = Support.StringValueCStr(context, format, out bytes);
            var program = StrftimeProgram.Compile(bytes);
            var state = CompiledFormat<StrftimeProgram>.GetOrCreate(self);
            state.Program = program;
            state.Format = Support.NewFrozen(fmt);
            return self;
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static object InitializeCopy(RubyContext/*!*/ context, object self, object other) {
            return CompiledFormat<StrftimeProgram>.InitializeCopy(context, self, other);
        }

        private static MutableString/*!*/ Result(CompiledFormat<StrftimeProgram>/*!*/ state, byte[]/*!*/ bytes) {
            return MutableString.CreateBinary(bytes, state.Format.Encoding);
        }

        /// <summary>exec(time) -> String</summary>
        [RubyMethod("exec")]
        public static MutableString/*!*/ Exec(RubyContext/*!*/ context, object self, object time) {
            var t = time as RubyTime;
            if (t == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected time)", Support.TypeName(context, time));
            }
            var state = CompiledFormat<StrftimeProgram>.Get(context, self);
            return Result(state, state.Program.Exec(t.Seconds, t.Nanoseconds, unchecked((int)t.UtcOffsetSeconds)));
        }

        /// <summary>execi(epoch) -> String, formatted in UTC</summary>
        [RubyMethod("execi")]
        public static MutableString/*!*/ ExecI(RubyContext/*!*/ context, object self, object epoch) {
            var state = CompiledFormat<StrftimeProgram>.Get(context, self);
            long sec;
            int nsec;
            if (epoch is int) {
                sec = (int)epoch;
                nsec = 0;
            } else if (epoch is long) {
                sec = (long)epoch;
                nsec = 0;
            } else if (epoch is BigInteger) {
                sec = ToLong(context, epoch);
                nsec = 0;
            } else if (epoch is double) {
                double d = (double)epoch;
                sec = unchecked((long)d);
                nsec = unchecked((int)(unchecked((long)(d * 1000000000)) % 1000000000));
            } else if (Support.IsRational(context, epoch)) {
                // NUM2TIMET(epoch) and NUM2INT(epoch * 1000000000 % 1000000000)
                sec = ToLong(context, Support.Call(context, epoch, "to_int"));
                object scaled = Support.Call(context, epoch, "*", ScriptingRuntimeHelpers.Int32ToObject(1000000000));
                object m = Support.Call(context, scaled, "%", ScriptingRuntimeHelpers.Int32ToObject(1000000000));
                nsec = (int)ToLong(context, Support.Call(context, m, "to_int"));
            } else {
                // the C leaves the timespec uninitialized here; say what it was given instead
                throw RubyExceptions.CreateTypeError("can't convert {0} into an exact number", Support.TypeName(context, epoch));
            }
            return Result(state, state.Program.Exec(sec, nsec, 0));
        }

        private static long ToLong(RubyContext/*!*/ context, object value) {
            if (value is int) {
                return (int)value;
            }
            if (value is long) {
                return (long)value;
            }
            if (value is BigInteger) {
                var big = (BigInteger)value;
                if (big > long.MaxValue || big < long.MinValue) {
                    throw RubyExceptions.CreateRangeError("bignum too big to convert into 'long'");
                }
                return (long)big;
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into Integer", Support.TypeName(context, value));
        }

        [RubyMethod("source")]
        public static MutableString/*!*/ Source(RubyContext/*!*/ context, object self) {
            return CompiledFormat<StrftimeProgram>.Get(context, self).Format;
        }
    }
}

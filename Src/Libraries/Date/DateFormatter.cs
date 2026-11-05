/* ****************************************************************************
 *
 * Date#strftime for IronRuby.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Date {

    internal static class DateFormatter {

        /// <summary>
        /// Ceiling on an explicit field width.  CRuby raises Errno::ERANGE past its own
        /// buffer limit; without a cap "%1234567890Y" would try to allocate a gigabyte.
        /// </summary>
        private const long MaxWidth = 1000000;

        internal static readonly string[] DayNames = {
            "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"
        };

        internal static readonly string[] AbbrDayNames = {
            "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"
        };

        internal static readonly string[] MonthNames = {
            null, "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December"
        };

        internal static readonly string[] AbbrMonthNames = {
            null, "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
        };

        #region small helpers

        internal static string/*!*/ FormatOffset(int seconds, string separator) {
            char sign = seconds < 0 ? '-' : '+';
            int abs = Math.Abs(seconds);
            int h = abs / 3600;
            int m = (abs % 3600) / 60;
            int s = abs % 60;
            var sb = new StringBuilder();
            sb.Append(sign).Append(h.ToString("00", CultureInfo.InvariantCulture));
            if (separator == null) {
                sb.Append(m.ToString("00", CultureInfo.InvariantCulture));
            } else {
                sb.Append(separator).Append(m.ToString("00", CultureInfo.InvariantCulture));
                if (s != 0) {
                    sb.Append(separator).Append(s.ToString("00", CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// %z and its colon variants.  The hours field absorbs the requested width, with
        /// zero fill going after the sign and space fill going in front of it.  This
        /// mirrors the shared Time strftime engine (Src/Libraries/Builtins/Strftime.cs on
        /// the Time branch); Date differs from Time in one respect, namely that '-' here
        /// means "no padding" rather than RFC 3339's unknown-offset sign.
        ///
        /// colons: 0 = +hhmm, 1 = +hh:mm, 2 = +hh:mm:ss, 3 = shortest of the three.
        /// </summary>
        internal static string/*!*/ FormatOffset(int seconds, int colons, bool noPad, char padChar, int width) {
            int abs = Math.Abs(seconds);
            int h = abs / 3600;
            int m = (abs % 3600) / 60;
            int s = abs % 60;

            string separator = colons > 0 ? ":" : "";
            string tail;
            if (colons == 3) {
                // shortest form: hours only, then minutes, then seconds as needed
                tail = "";
                if (m != 0 || s != 0) {
                    tail = separator + m.ToString("00", CultureInfo.InvariantCulture);
                    if (s != 0) {
                        tail += separator + s.ToString("00", CultureInfo.InvariantCulture);
                    }
                }
            } else {
                tail = separator + m.ToString("00", CultureInfo.InvariantCulture);
                if (colons >= 2 || (colons == 0 && s != 0)) {
                    tail += separator + s.ToString("00", CultureInfo.InvariantCulture);
                }
            }

            string sign = seconds < 0 ? "-" : "+";
            string hours = h.ToString(CultureInfo.InvariantCulture);
            if (padChar != ' ') {
                // '-' drops the default two-digit hours field but an explicit width still applies
                int target = Math.Max(noPad ? 1 : 2, width - 1 - tail.Length);
                return sign + hours.PadLeft(target, '0') + tail;
            }
            // space fill goes in front of the sign; '-' drops the default minimum
            int spaceTarget = noPad ? width : Math.Max(width, 3 + tail.Length);
            return (sign + hours + tail).PadLeft(Math.Max(0, spaceTarget), ' ');
        }

        /// <summary>First <paramref name="digits"/> decimal digits of a fraction in [0,1).</summary>
        internal static string/*!*/ FractionDigits(Frac sf, int digits) {
            if (digits <= 0) {
                return "";
            }
            BigInteger scale = BigInteger.Pow(10, digits);
            BigInteger v = sf.N * scale / sf.D;
            string s = v.ToString(CultureInfo.InvariantCulture);
            if (s.Length < digits) {
                s = new string('0', digits - s.Length) + s;
            } else if (s.Length > digits) {
                s = s.Substring(0, digits);
            }
            return s;
        }

        internal static string/*!*/ NanosecondString(Frac sf) {
            if (sf.IsZero) {
                return "0";
            }
            BigInteger v = sf.N * 1000000000 / sf.D;
            return v.ToString(CultureInfo.InvariantCulture);
        }

        internal static string/*!*/ Jisx0301(RubyDate/*!*/ date) {
            long y; int m, d;
            date.GetCivil(out y, out m, out d);
            long jd = date._jd;
            // Meiji 6 (1873-01-01) is JD 2405160; before that MRI emits an ISO date.
            if (jd < 2405160) {
                return Format(date, "%Y-%m-%d");
            }
            char era;
            long baseYear;
            if (jd >= 2458605) { era = 'R'; baseYear = 2018; }        // Reiwa,  2019-05-01
            else if (jd >= 2447535) { era = 'H'; baseYear = 1988; }   // Heisei, 1989-01-08
            else if (jd >= 2424875) { era = 'S'; baseYear = 1925; }   // Showa,  1926-12-25
            else if (jd >= 2419614) { era = 'T'; baseYear = 1911; }   // Taisho, 1912-07-30
            else { era = 'M'; baseYear = 1867; }                      // Meiji
            return String.Format(CultureInfo.InvariantCulture, "{0}{1:00}.{2:00}.{3:00}", era, y - baseYear, m, d);
        }

        #endregion

        #region strftime

        internal static string/*!*/ Format(RubyDate/*!*/ date, string/*!*/ format) {
            var sb = new StringBuilder();
            Format(sb, date, format, 0);
            return sb.ToString();
        }

        private static void Format(StringBuilder/*!*/ sb, RubyDate/*!*/ date, string/*!*/ format, int depth) {
            if (depth > 8) {
                return;
            }

            long year; int month, day;
            date.GetCivil(out year, out month, out day);
            int wday = DateMath.JdToWday(date._jd);
            int hour = date._df / 3600;
            int minute = (date._df % 3600) / 60;
            int second = date._df % 60;

            int i = 0;
            while (i < format.Length) {
                char c = format[i];
                if (c != '%') {
                    sb.Append(c);
                    i++;
                    continue;
                }

                int start = i;
                i++;
                if (i >= format.Length) {
                    sb.Append('%');
                    break;
                }

                // Flags.  '-' always wins (no padding at all); between '_' and '0'
                // the last one given wins, which is what MRI's date_strftime does.
                bool noPad = false, upcase = false, changeCase = false;
                char padChar = '\0';
                while (i < format.Length) {
                    char f = format[i];
                    if (f == '-') { noPad = true; i++; }
                    else if (f == '_') { padChar = ' '; i++; }
                    else if (f == '0') { padChar = '0'; i++; }
                    else if (f == '^') { upcase = true; i++; }
                    else if (f == '#') { changeCase = true; i++; }
                    else break;
                }

                // width, then the %z colon variants - in that order, as MRI scans them
                long width = 0;
                bool hasWidth = false;
                while (i < format.Length && format[i] >= '0' && format[i] <= '9') {
                    width = Math.Min(width * 10 + (format[i] - '0'), MaxWidth + 1);
                    hasWidth = true;
                    i++;
                }

                if (hasWidth && width > MaxWidth) {
                    // CRuby raises Errno::ERANGE here; the point is not to try to
                    // allocate the string, which would take the process down.
                    throw RubyExceptions.CreateArgumentError("width too big");
                }

                int colons = 0;
                while (i < format.Length && format[i] == ':' && colons < 3) {
                    colons++;
                    i++;
                }

                if (i >= format.Length) {
                    sb.Append(format, start, format.Length - start);
                    break;
                }

                char conv = format[i];
                i++;

                string text = null;
                long numeric = 0;
                int defaultWidth = 0;
                char defaultPad = '0';
                bool isNumeric = false;
                bool isCompound = false;

                if (colons > 0 && conv != 'z') {
                    // the colon variants only exist for %z
                    sb.Append(format, start, i - start);
                    continue;
                }

                switch (conv) {
                    case '%': text = "%"; break;
                    case 'n': text = "\n"; break;
                    case 't': text = "\t"; break;

                    case 'A': text = DayNames[wday]; break;
                    case 'a': text = AbbrDayNames[wday]; break;
                    case 'B': text = MonthNames[month]; break;
                    case 'b':
                    case 'h': text = AbbrMonthNames[month]; break;

                    case 'C': isNumeric = true; numeric = DateMath.FloorDiv(year, 100); defaultWidth = 2; break;
                    case 'd': isNumeric = true; numeric = day; defaultWidth = 2; break;
                    case 'e': isNumeric = true; numeric = day; defaultWidth = 2; defaultPad = ' '; break;
                    case 'j': isNumeric = true; numeric = Yday(date); defaultWidth = 3; break;
                    case 'm': isNumeric = true; numeric = month; defaultWidth = 2; break;
                    // a negative year is four digits after its sign, as in MRI: -0001, not -001
                    case 'Y': isNumeric = true; numeric = year; defaultWidth = year < 0 ? 5 : 4; break;
                    case 'y': isNumeric = true; numeric = DateMath.FloorMod(year, 100); defaultWidth = 2; break;
                    case 'G': {
                            long cwy; int cw, cd;
                            DateMath.JdToCommercial(date._jd, date._sg, out cwy, out cw, out cd);
                            isNumeric = true; numeric = cwy; defaultWidth = cwy < 0 ? 5 : 4;
                            break;
                        }
                    case 'g': {
                            long cwy; int cw, cd;
                            DateMath.JdToCommercial(date._jd, date._sg, out cwy, out cw, out cd);
                            isNumeric = true; numeric = DateMath.FloorMod(cwy, 100); defaultWidth = 2;
                            break;
                        }
                    case 'V': {
                            long cwy; int cw, cd;
                            DateMath.JdToCommercial(date._jd, date._sg, out cwy, out cw, out cd);
                            isNumeric = true; numeric = cw; defaultWidth = 2;
                            break;
                        }
                    case 'u': {
                            long cwy; int cw, cd;
                            DateMath.JdToCommercial(date._jd, date._sg, out cwy, out cw, out cd);
                            isNumeric = true; numeric = cd; defaultWidth = 1;
                            break;
                        }
                    case 'w': isNumeric = true; numeric = wday; defaultWidth = 1; break;
                    case 'U': isNumeric = true; numeric = WeekNumber(date, 0); defaultWidth = 2; break;
                    case 'W': isNumeric = true; numeric = WeekNumber(date, 1); defaultWidth = 2; break;

                    case 'H': isNumeric = true; numeric = hour; defaultWidth = 2; break;
                    case 'k': isNumeric = true; numeric = hour; defaultWidth = 2; defaultPad = ' '; break;
                    case 'I': isNumeric = true; numeric = Hour12(hour); defaultWidth = 2; break;
                    case 'l': isNumeric = true; numeric = Hour12(hour); defaultWidth = 2; defaultPad = ' '; break;
                    case 'M': isNumeric = true; numeric = minute; defaultWidth = 2; break;
                    case 'S': isNumeric = true; numeric = second; defaultWidth = 2; break;
                    case 'L': text = FractionDigits(date._sf, hasWidth ? (int)width : 3); hasWidth = false; break;
                    case 'N': text = FractionDigits(date._sf, hasWidth ? (int)width : 9); hasWidth = false; break;
                    case 'P': text = hour < 12 ? "am" : "pm"; break;
                    case 'p': text = hour < 12 ? "AM" : "PM"; break;

                    case 's': isNumeric = true; numeric = EpochSeconds(date); defaultWidth = 1; break;
                    case 'Q': isNumeric = true; numeric = EpochSeconds(date) * 1000 + MilliPart(date); defaultWidth = 1; break;

                    // %z takes the padding flags and the width itself; %Z is %:z for Date.
                    case 'z':
                        sb.Append(FormatOffset(date._of, colons, noPad, padChar, hasWidth ? (int)width : -1));
                        continue;
                    case 'Z':
                        // for Date, %Z is plain %:z; the flags apply to it as to any text field
                        text = FormatOffset(date._of, 1, false, '\0', -1);
                        break;

                    // Compound directives: they take the width and the '_'/'0' padding and
                    // inherit '^', but not '-' and not '#'.
                    case 'c': text = Compound(date, "%a %b %e %H:%M:%S %Y", depth); isCompound = true; break;
                    case 'x':
                    case 'D': text = Compound(date, "%m/%d/%y", depth); isCompound = true; break;
                    case 'F': text = Compound(date, "%Y-%m-%d", depth); isCompound = true; break;
                    case 'X':
                    case 'T': text = Compound(date, "%H:%M:%S", depth); isCompound = true; break;
                    case 'R': text = Compound(date, "%H:%M", depth); isCompound = true; break;
                    case 'r': text = Compound(date, "%I:%M:%S %p", depth); isCompound = true; break;
                    case '+': text = Compound(date, "%a %b %e %H:%M:%S %Z %Y", depth); isCompound = true; break;
                    case 'v': text = Compound(date, "%e-%^b-%Y", depth); isCompound = true; break;

                    default:
                        sb.Append(format, start, i - start);
                        continue;
                }

                if (isNumeric) {
                    char pad = noPad ? '\0' : (padChar != '\0' ? padChar : defaultPad);
                    int w = hasWidth ? (int)width : defaultWidth;
                    text = PadNumber(numeric, w, pad);
                } else if (text != null) {
                    text = CaseFold(text, upcase, changeCase && !isCompound);
                    if (hasWidth && !noPad && text.Length < width) {
                        text = new string(padChar != '\0' ? padChar : ' ', (int)width - text.Length) + text;
                    }
                }

                sb.Append(text);
            }
        }

        private static string/*!*/ Compound(RubyDate/*!*/ date, string/*!*/ format, int depth) {
            var buffer = new StringBuilder();
            Format(buffer, date, format, depth + 1);
            return buffer.ToString();
        }

        /// <summary>
        /// '^' upcases.  '#' changes case: it upcases unless the text is already entirely
        /// upper case, in which case it downcases - it is not a per-character swap.  When
        /// both are given '#' wins (%^#p and %#^p are both "am").
        /// </summary>
        private static string/*!*/ CaseFold(string/*!*/ value, bool upcase, bool changeCase) {
            if (changeCase) {
                foreach (char c in value) {
                    if (Char.IsLower(c)) {
                        return value.ToUpperInvariant();
                    }
                }
                return value.ToLowerInvariant();
            }
            return upcase ? value.ToUpperInvariant() : value;
        }

        private static string/*!*/ PadNumber(long value, int width, char pad) {
            bool negative = value < 0;
            string digits = (negative ? -value : value).ToString(CultureInfo.InvariantCulture);
            if (pad == '\0') {
                return negative ? "-" + digits : digits;
            }
            int total = negative ? digits.Length + 1 : digits.Length;
            if (total < width) {
                if (pad == '0') {
                    digits = new string('0', width - total) + digits;
                    return negative ? "-" + digits : digits;
                }
                return new string(pad, width - total) + (negative ? "-" + digits : digits);
            }
            return negative ? "-" + digits : digits;
        }

        private static int Hour12(int hour) {
            int h = hour % 12;
            return h == 0 ? 12 : h;
        }

        private static int Yday(RubyDate/*!*/ date) {
            long y; int yday;
            DateMath.JdToOrdinal(date._jd, date._sg, out y, out yday);
            return yday;
        }

        /// <summary>
        /// %U (week starting Sunday, f = 0) / %W (week starting Monday, f = 1).
        /// Mirrors c_jd_to_weeknum in CRuby's ext/date/date_core.c.
        /// </summary>
        private static int WeekNumber(RubyDate/*!*/ date, int f) {
            long y; int m, d;
            date.GetCivil(out y, out m, out d);
            long fdoy;
            if (!DateMath.FindFirstDayOfYear(y, date._sg, out fdoy)) {
                fdoy = DateMath.CivilToJd(y, 1, 1, date._sg);
            }
            long a = fdoy + 6;
            long j = date._jd - (a - DateMath.FloorMod((a - f) + 1, 7)) + 7;
            return (int)DateMath.FloorDiv(j, 7);
        }

        private static long EpochSeconds(RubyDate/*!*/ date) {
            return (date._jd - 2440588L) * DateMath.SecondsPerDay + date._df - date._of;
        }

        private static long MilliPart(RubyDate/*!*/ date) {
            if (date._sf.IsZero) {
                return 0;
            }
            return (long)(date._sf.N * 1000 / date._sf.D);
        }

        #endregion
    }
}

/* ****************************************************************************
 *
 * Ruby's Time#strftime. Implemented directly rather than by mapping onto .NET
 * DateTime format strings: Ruby's directive set (%C %F %G %j %k %l %L %N %P %s
 * %u %V ...), its flags (- _ 0 ^ #), explicit field widths and the %z colon
 * variants have no .NET equivalent, and .NET's own escaping rules get in the way.
 *
 * ***************************************************************************/

using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    internal static class Strftime {
        internal static readonly string[]/*!*/ DayNames = new[] {
            "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"
        };

        internal static readonly string[]/*!*/ DayNamesAbbreviated = new[] {
            "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"
        };

        internal static readonly string[]/*!*/ MonthNames = new[] {
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December"
        };

        internal static readonly string[]/*!*/ MonthNamesAbbreviated = new[] {
            "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
        };

        private struct Modifiers {
            public bool SawMinus;   // '-' appeared anywhere; %z reads it as RFC 3339's unknown offset
            public char Pad;        // the last of '-' / '_' / '0'; '\0' if none was given
            public bool Upcase;
            public bool Swapcase;
            public int Colons;
            public int Width;
            public char Alt;        // 'E' or 'O': the locale modifiers, accepted on the conversions MRI accepts them on

            // '-' is sticky for text and numbers: once seen, no padding at all.
            public bool NoPadding {
                get { return SawMinus; }
            }

            /// <summary>The pad character to use, given the directive's own default.</summary>
            public char PadChar(char defaultPad) {
                return (Pad == '_') ? ' ' : (Pad == '0') ? '0' : defaultPad;
            }
        }

        private const int MaxRecursionDepth = 8;

        internal static void Format(RubyContext/*!*/ context, MutableString/*!*/ result, RubyTime/*!*/ time, string/*!*/ format, int depth) {
            if (depth > MaxRecursionDepth) {
                return;
            }

            int i = 0;
            while (i < format.Length) {
                char c = format[i];
                if (c != '%') {
                    result.Append(c);
                    i++;
                    continue;
                }

                int start = i;
                i++;
                if (i >= format.Length) {
                    // MRI: a lone % at the end, or one followed only by flags or a width, is an error
                    throw RubyExceptions.CreateArgumentError("invalid format: " + format.Substring(start));
                }

                Modifiers modifiers = new Modifiers();
                modifiers.Width = -1;
                bool reading = true;
                while (i < format.Length && reading) {
                    switch (format[i]) {
                        case '-': modifiers.SawMinus = true; modifiers.Pad = '-'; i++; break;
                        case '_': modifiers.Pad = '_'; i++; break;
                        case '0': modifiers.Pad = '0'; i++; break;
                        case '^': modifiers.Upcase = true; i++; break;
                        case '#': modifiers.Swapcase = true; i++; break;
                        default:
                            reading = false;
                            break;
                    }
                }

                int widthStart = i;
                while (i < format.Length && format[i] >= '0' && format[i] <= '9') {
                    i++;
                }
                if (i > widthStart) {
                    int width;
                    if (Int32.TryParse(format.Substring(widthStart, i - widthStart), NumberStyles.None, CultureInfo.InvariantCulture, out width)) {
                        modifiers.Width = width;
                    }
                }

                if (i >= format.Length) {
                    throw RubyExceptions.CreateArgumentError("invalid format: " + format.Substring(start));
                }

                if (format[i] == 'E' || format[i] == 'O') {
                    modifiers.Alt = format[i];
                    i++;
                }

                while (i < format.Length && format[i] == ':' && modifiers.Colons < 3) {
                    modifiers.Colons++;
                    i++;
                }

                if (i >= format.Length) {
                    result.Append(format.Substring(start));
                    return;
                }

                char directive = format[i];
                i++;

                string piece = FormatDirective(context, time, directive, modifiers, modifiers.Width, depth);
                if (piece == null) {
                    // Unknown directive: Ruby copies the whole thing through verbatim.
                    result.Append(format.Substring(start, i - start));
                } else {
                    result.Append(piece);
                }
            }
        }

        private static string FormatDirective(RubyContext/*!*/ context, RubyTime/*!*/ time, char directive, Modifiers modifiers, int width, int depth) {
            if (modifiers.Colons > 0 && directive != 'z') {
                return null;
            }
            // %E goes with the alternative-representation conversions and %O with the numeric
            // ones, as in MRI; on anything else the whole directive is copied through.
            if (modifiers.Alt == 'E' && "cCxXyY".IndexOf(directive) < 0 || modifiers.Alt == 'O' && "deHImMSuUVwWy".IndexOf(directive) < 0) {
                return null;
            }

            RubyTime.Fields t = time.GetFields();

            switch (directive) {
                case '%': return Text("%", modifiers, width);
                case 'n': return Text("\n", modifiers, width);
                case 't': return Text("\t", modifiers, width);

                case 'a': return Text(DayNamesAbbreviated[t.DayOfWeek], modifiers, width);
                case 'A': return Text(DayNames[t.DayOfWeek], modifiers, width);
                case 'h':
                case 'b': return Text(MonthNamesAbbreviated[t.Month - 1], modifiers, width);
                case 'B': return Text(MonthNames[t.Month - 1], modifiers, width);

                case 'p': return Text(t.Hour < 12 ? "AM" : "PM", modifiers, width);
                case 'P': return Text(t.Hour < 12 ? "am" : "pm", modifiers, width);

                case 'C': return Number(FloorDiv(t.Year, 100), modifiers, width, 2, '0');
                case 'd': return Number(t.Day, modifiers, width, 2, '0');
                case 'e': return Number(t.Day, modifiers, width, 2, ' ');
                case 'H': return Number(t.Hour, modifiers, width, 2, '0');
                case 'k': return Number(t.Hour, modifiers, width, 2, ' ');
                case 'I': return Number(Hour12(t.Hour), modifiers, width, 2, '0');
                case 'l': return Number(Hour12(t.Hour), modifiers, width, 2, ' ');
                case 'j': return Number(t.DayOfYear, modifiers, width, 3, '0');
                case 'm': return Number(t.Month, modifiers, width, 2, '0');
                case 'M': return Number(t.Minute, modifiers, width, 2, '0');
                case 'S': return Number(t.Second, modifiers, width, 2, '0');
                case 'u': return Number((t.DayOfWeek == 0) ? 7 : t.DayOfWeek, modifiers, width, 1, '0');
                case 'w': return Number(t.DayOfWeek, modifiers, width, 1, '0');
                case 'y': return Number(Mod(t.Year, 100), modifiers, width, 2, '0');
                // MRI pads a negative year to four digits after the sign: -0001.
                case 'Y': return Number(t.Year, modifiers, width, t.Year < 0 ? 5 : 4, '0');

                case 'G': return Number(IsoYear(t), modifiers, width, IsoYear(t) < 0 ? 5 : 4, '0');
                case 'g': return Number(Mod(IsoYear(t), 100), modifiers, width, 2, '0');
                case 'V': return Number(IsoWeek(t), modifiers, width, 2, '0');

                case 'U': return Number((t.DayOfYear - 1 + 7 - t.DayOfWeek) / 7, modifiers, width, 2, '0');
                case 'W': return Number((t.DayOfYear - 1 + 7 - ((t.DayOfWeek + 6) % 7)) / 7, modifiers, width, 2, '0');

                case 's': return Number(time.Seconds, modifiers, width, 1, '0');

                case 'L': return Fraction(time, (width < 0) ? 3 : width);
                case 'N': return Fraction(time, (width < 0) ? 9 : width);

                case 'z': return FormatOffset(time, modifiers, width);

                case 'Z': {
                        string name = RubyTimeOps.GetZoneAbbreviation(context, time);
                        return Text(name ?? "", modifiers, width);
                    }

                case 'c': return Compound(context, time, "%a %b %e %H:%M:%S %Y", modifiers, depth);
                case 'D':
                case 'x': return Compound(context, time, "%m/%d/%y", modifiers, depth);
                case 'F': return Compound(context, time, "%Y-%m-%d", modifiers, depth);
                case 'r': return Compound(context, time, "%I:%M:%S %p", modifiers, depth);
                case 'R': return Compound(context, time, "%H:%M", modifiers, depth);
                case 'T':
                case 'X': return Compound(context, time, "%H:%M:%S", modifiers, depth);
                case 'v': return Compound(context, time, "%e-%^b-%Y", modifiers, depth);

                default: return null;
            }
        }

        private static string/*!*/ Compound(RubyContext/*!*/ context, RubyTime/*!*/ time, string/*!*/ format, Modifiers modifiers, int depth) {
            MutableString buffer = MutableString.CreateMutable(RubyEncoding.Binary);
            Format(context, buffer, time, format, depth + 1);

            // The compound directives take the case and padding flags but ignore '-'.
            Modifiers inherited = new Modifiers();
            inherited.Width = modifiers.Width;
            inherited.Upcase = modifiers.Upcase;
            inherited.Pad = (modifiers.Pad == '_' || modifiers.Pad == '0') ? modifiers.Pad : '\0';
            return Text(buffer.ToString(), inherited, modifiers.Width);
        }

        /// <summary>
        /// %z. The hours field absorbs any requested width; with space padding the fill goes
        /// in front of the sign instead. A '-' flag on a UTC time asks for RFC 3339's "-0000".
        /// </summary>
        private static string/*!*/ FormatOffset(RubyTime/*!*/ time, Modifiers modifiers, int width) {
            System.Numerics.BigInteger rounded = time.UtcOffsetExact.Round();
            long total = (long)rounded;

            bool negative = total < 0 || (modifiers.SawMinus && time.IsUtc);
            long abs = Math.Abs(total);
            long h = abs / 3600;
            long m = (abs / 60) % 60;
            long sec = abs % 60;

            string separator = (modifiers.Colons > 0) ? ":" : "";
            string tail;
            if (modifiers.Colons == 3) {
                // %:::z: hours, with the minutes and seconds only when they are not zero
                tail = "";
                if (m != 0 || sec != 0) {
                    tail += ":" + m.ToString("D2", CultureInfo.InvariantCulture);
                }
                if (sec != 0) {
                    tail += ":" + sec.ToString("D2", CultureInfo.InvariantCulture);
                }
            } else {
                // %z is +hhmm and %:z +hh:mm whatever the seconds; only %::z shows them
                tail = separator + m.ToString("D2", CultureInfo.InvariantCulture);
                if (modifiers.Colons >= 2) {
                    tail += separator + sec.ToString("D2", CultureInfo.InvariantCulture);
                }
            }

            char pad = modifiers.PadChar('0');
            string sign = negative ? "-" : "+";
            string hours = h.ToString(CultureInfo.InvariantCulture);

            if (pad == '0') {
                int target = Math.Max(2, width - 1 - tail.Length);
                return sign + hours.PadLeft(target, '0') + tail;
            }
            return (sign + hours + tail).PadLeft(Math.Max(width, 3 + tail.Length), ' ');
        }

        /// <summary>ISO-8601 week-based year and week number, for %G/%g/%V.</summary>
        private static void IsoWeekDate(RubyTime.Fields t, out int year, out int week) {
            int isoWeekday = (t.DayOfWeek == 0) ? 7 : t.DayOfWeek;   // Monday = 1 .. Sunday = 7
            long days = RubyTime.DaysFromCivil(t.Year, t.Month, t.Day);
            long thursday = days + (4 - isoWeekday);                 // the Thursday of this ISO week
            long y;
            int m, d;
            RubyTime.CivilFromDays(thursday, out y, out m, out d);
            year = (int)y;
            week = (int)((thursday - RubyTime.DaysFromCivil(y, 1, 1)) / 7) + 1;
        }

        private static int IsoYear(RubyTime.Fields t) {
            int year, week;
            IsoWeekDate(t, out year, out week);
            return year;
        }

        private static int IsoWeek(RubyTime.Fields t) {
            int year, week;
            IsoWeekDate(t, out year, out week);
            return week;
        }

        private static int Hour12(int hour) {
            int result = hour % 12;
            return (result == 0) ? 12 : result;
        }

        private static int FloorDiv(int value, int divisor) {
            int q = value / divisor;
            return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? q - 1 : q;
        }

        private static int Mod(int value, int divisor) {
            int r = value % divisor;
            return (r < 0) ? r + divisor : r;
        }

        private static string/*!*/ Text(string/*!*/ value, Modifiers modifiers, int width) {
            if (modifiers.Upcase) {
                value = value.ToUpperInvariant();
            } else if (modifiers.Swapcase) {
                value = ChangeCase(value);
            }
            if (modifiers.NoPadding) {
                return value;
            }
            char pad = modifiers.PadChar(' ');
            if (width > value.Length) {
                value = value.PadLeft(width, pad);
            }
            return value;
        }

        /// <summary>
        /// The '#' flag. MRI upcases unless the text is already entirely upper case,
        /// in which case it downcases; it is not a per-character swap.
        /// </summary>
        private static string/*!*/ ChangeCase(string/*!*/ value) {
            foreach (char c in value) {
                if (Char.IsLower(c)) {
                    return value.ToUpperInvariant();
                }
            }
            return value.ToLowerInvariant();
        }

        private static string/*!*/ Number(long value, Modifiers modifiers, int width, int defaultWidth, char defaultPad) {
            string digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
            string sign = (value < 0) ? "-" : "";

            char pad = modifiers.PadChar(defaultPad);

            int target = (width >= 0) ? width : defaultWidth;
            if (modifiers.NoPadding) {
                target = 0;
            }

            int fill = target - digits.Length - sign.Length;
            if (fill <= 0) {
                return sign + digits;
            }
            if (pad == '0') {
                return sign + new string('0', fill) + digits;
            }
            return new string(pad, fill) + sign + digits;
        }

        /// <summary>Sub-second digits, e.g. %N (9 digits) and %L (3 digits).</summary>
        private static string/*!*/ Fraction(RubyTime/*!*/ time, int digits) {
            if (digits <= 0) {
                return "";
            }

            ExactNum subsec = time.Subsec;
            BigInteger scale = BigInteger.Pow(new BigInteger(10), digits);
            BigInteger value = (subsec.Numerator * scale) / subsec.Denominator;
            return value.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        }
    }
}

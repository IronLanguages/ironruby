/* ****************************************************************************
 *
 * Unicode case mapping for String#upcase/downcase/capitalize/swapcase and the
 * option set Ruby 2.4 added to them (:ascii, :fold, :turkic, :lithuanian).
 *
 * The 1:1 part of the job is .NET's: Rune.ToUpperInvariant/ToLowerInvariant.
 * What .NET cannot do, and what the tables in UnicodeCaseMapping.Data.cs
 * supply, is:
 *
 *   - the *full* mappings, where one character becomes several
 *     ("ß".upcase == "SS", "İ".downcase == "i̇"),
 *   - case *folding*, which the BCL does not expose at all
 *     ("ß".downcase(:fold) == "ss"),
 *   - *titlecase*, which #capitalize needs ("ǆ".capitalize == "ǅ"),
 *   - the handful of characters where .NET's ICU is older than the Unicode
 *     version CRuby was built against.
 *
 * ***************************************************************************/

using System;
using System.Text;

namespace IronRuby.Builtins {
    /// <summary>
    /// The options String#upcase and friends accept. Validation of which combinations Ruby
    /// allows lives in MutableStringOps; this class just obeys what it is handed.
    /// </summary>
    [Flags]
    public enum CaseMappingOptions {
        None = 0,
        /// <summary>Only a-z/A-Z move.</summary>
        Ascii = 1,
        /// <summary>Turkish/Azeri dotted and dotless I.</summary>
        Turkic = 2,
        /// <summary>Accepted, but MRI still applies plain full case mapping for it.</summary>
        Lithuanian = 4,
        /// <summary>Case folding rather than lowercasing; downcase only.</summary>
        Fold = 8,
    }

    /// <summary>Which of String's four case-mapping methods is being applied.</summary>
    public enum CaseMappingKind {
        UpCase,
        DownCase,
        SwapCase,
        Capitalize,
    }

    public static partial class UnicodeCaseMapping {
        private const int DottedCapitalI = 0x0130;  // İ

        #region public entry points

        /// <summary>
        /// Answers <paramref name="str"/> itself when the mapping changes nothing, so callers
        /// can test for "no change" - which is what the ! forms report as nil - by reference.
        /// </summary>
        public static string/*!*/ Apply(string/*!*/ str, CaseMappingKind kind, CaseMappingOptions options) {
            switch (kind) {
                case CaseMappingKind.UpCase: return UpCase(str, options);
                case CaseMappingKind.DownCase: return DownCase(str, options);
                case CaseMappingKind.SwapCase: return SwapCase(str, options);
                default: return Capitalize(str, options);
            }
        }

        public static string/*!*/ UpCase(string/*!*/ str, CaseMappingOptions options) {
            if ((options & CaseMappingOptions.Ascii) != 0) {
                return MapAscii(str, true, false);
            }
            return Map(str, options, CaseOp.Upper);
        }

        public static string/*!*/ DownCase(string/*!*/ str, CaseMappingOptions options) {
            if ((options & CaseMappingOptions.Ascii) != 0) {
                return MapAscii(str, false, false);
            }
            return Map(str, options, CaseOp.Lower);
        }

        public static string/*!*/ SwapCase(string/*!*/ str, CaseMappingOptions options) {
            if ((options & CaseMappingOptions.Ascii) != 0) {
                return MapAscii(str, false, true);
            }
            return Map(str, options, CaseOp.Swap);
        }

        /// <summary>
        /// Titlecases the first character and lowercases the rest. Note that it is the first
        /// character of the *result* that stays uppercase: "ß".capitalize is "Ss", not "SS".
        /// </summary>
        public static string/*!*/ Capitalize(string/*!*/ str, CaseMappingOptions options) {
            if (str.Length == 0) {
                return str;
            }
            if ((options & CaseMappingOptions.Ascii) != 0) {
                return CapitalizeAscii(str);
            }
            return Map(str, options, CaseOp.Title);
        }

        /// <summary>
        /// The key String#casecmp? compares by: full case folding, which is what makes
        /// "ß".casecmp?("ss") true.
        /// </summary>
        public static string/*!*/ Fold(string/*!*/ str) {
            return Map(str, CaseMappingOptions.Fold, CaseOp.Lower);
        }

        #endregion

        private enum CaseOp { Upper, Lower, Swap, Title }

        private static string/*!*/ Map(string/*!*/ str, CaseMappingOptions options, CaseOp op) {
            // Most strings are all ASCII, and over ASCII the Unicode mappings are the ASCII ones
            // (full case folding included). The general path below costs a binary search over the
            // special-casing tables and a Rune round trip per code point, which is an order of
            // magnitude more. :turkic is the one option that moves an ASCII letter out of ASCII
            // (i <-> Idot, I <-> dotless i), so it stays on the general path.
            if ((options & CaseMappingOptions.Turkic) == 0 && IsAsciiOnly(str)) {
                return op == CaseOp.Title ? CapitalizeAscii(str) : MapAscii(str, op == CaseOp.Upper, op == CaseOp.Swap);
            }

            StringBuilder result = null;
            int i = 0;
            while (i < str.Length) {
                int cp = CodePointAt(str, i, out int width);
                string mapped = MapCodePoint(cp, options, i == 0 ? op : (op == CaseOp.Title ? CaseOp.Lower : op));

                if (mapped == null) {
                    result?.Append(str, i, width);
                } else {
                    if (result == null) {
                        result = new StringBuilder(str.Length + 4);
                        result.Append(str, 0, i);
                    }
                    result.Append(mapped);
                }
                i += width;
            }
            return result == null ? str : result.ToString();
        }

        /// <summary>Answers null when the character is unchanged.</summary>
        private static string MapCodePoint(int cp, CaseMappingOptions options, CaseOp op) {
            bool turkic = (options & CaseMappingOptions.Turkic) != 0;

            switch (op) {
                case CaseOp.Upper:
                    return UpperOf(cp, turkic);

                case CaseOp.Lower:
                    return LowerOf(cp, turkic, (options & CaseMappingOptions.Fold) != 0);

                case CaseOp.Title:
                    return TitleOf(cp, turkic);

                default:
                    return SwapOf(cp, turkic);
            }
        }

        private static string UpperOf(int cp, bool turkic) {
            if (turkic && cp == 'i') {
                return "İ";
            }
            string full = Lookup(_UpperKeys, _UpperValues, cp);
            if (full != null) {
                return full;
            }
            Rune up = Rune.ToUpperInvariant(new Rune(cp));
            return up.Value == cp ? null : up.ToString();
        }

        private static string LowerOf(int cp, bool turkic, bool fold) {
            if (turkic) {
                // I and İ are different letters in Turkish: the dot is never dropped or added.
                if (cp == 'I') {
                    return "ı";
                }
                if (cp == DottedCapitalI) {
                    return "i";
                }
            }
            if (fold) {
                string folded = Lookup(_FoldKeys, _FoldValues, cp);
                if (folded != null) {
                    // Cherokee folds to *upper* case, so for its lowercase letters the fold
                    // entry is the character itself - i.e. no change.
                    return IsSame(folded, cp) ? null : folded;
                }
            }
            string full = Lookup(_LowerKeys, _LowerValues, cp);
            if (full != null) {
                return full;
            }
            Rune down = Rune.ToLowerInvariant(new Rune(cp));
            return down.Value == cp ? null : down.ToString();
        }

        private static string SwapOf(int cp, bool turkic) {
            // Titlecase (Lt) characters are swapped componentwise - "Ǆ" becomes "dŽ" - which is
            // not derivable from either direction of the character's own mapping.
            string special = Lookup(_SwapKeys, _SwapValues, cp);
            if (special != null) {
                return IsSame(special, cp) ? null : special;
            }
            // Whichever direction the character can move in. Asking "has a lowercase?" rather
            // than Rune.IsUpper matters for the cased non-letters: Ⅰ (Roman numeral one) and
            // Ⓐ are neither Lu nor Ll but both swap.
            return LowerOf(cp, turkic, false) ?? UpperOf(cp, turkic);
        }

        private static string TitleOf(int cp, bool turkic) {
            if (turkic && cp == 'i') {
                return "İ";
            }
            string title = Lookup(_TitleKeys, _TitleValues, cp);
            if (title != null) {
                // Georgian Mkhedruli has an uppercase mapping but no titlecase one, so its
                // entry is the character itself.
                return IsSame(title, cp) ? null : title;
            }

            string upper = UpperOf(cp, turkic);
            if (upper == null) {
                return null;
            }

            // A full uppercase mapping of more than one character titlecases as "first
            // character up, the rest down" - "ß" -> "SS" -> "Ss".
            int width = Char.IsSurrogatePair(upper, 0) ? 2 : 1;
            if (upper.Length == width) {
                return upper;
            }
            StringBuilder result = new StringBuilder(upper.Length);
            result.Append(upper, 0, width);
            for (int i = width; i < upper.Length; ) {
                int rest = CodePointAt(upper, i, out int w);
                result.Append(LowerOf(rest, turkic, false) ?? upper.Substring(i, w));
                i += w;
            }
            return result.ToString();
        }

        #region ASCII-only mapping

        private static string/*!*/ MapAscii(string/*!*/ str, bool upward, bool swap) {
            char[] result = null;
            for (int i = 0; i < str.Length; i++) {
                char c = str[i];
                char mapped;
                if ((swap || !upward) && c >= 'A' && c <= 'Z') {
                    mapped = (char)(c + ('a' - 'A'));
                } else if ((swap || upward) && c >= 'a' && c <= 'z') {
                    mapped = (char)(c - ('a' - 'A'));
                } else {
                    continue;
                }
                result ??= str.ToCharArray();
                result[i] = mapped;
            }
            return result == null ? str : new String(result);
        }

        private static string/*!*/ CapitalizeAscii(string/*!*/ str) {
            char[] result = null;
            for (int i = 0; i < str.Length; i++) {
                char c = str[i];
                char mapped = c;
                if (i == 0) {
                    if (c >= 'a' && c <= 'z') {
                        mapped = (char)(c - ('a' - 'A'));
                    }
                } else if (c >= 'A' && c <= 'Z') {
                    mapped = (char)(c + ('a' - 'A'));
                }
                if (mapped != c) {
                    result ??= str.ToCharArray();
                    result[i] = mapped;
                }
            }
            return result == null ? str : new String(result);
        }

        #endregion

        #region helpers

        private static bool IsAsciiOnly(string/*!*/ str) {
            for (int i = 0; i < str.Length; i++) {
                if (str[i] >= 0x80) {
                    return false;
                }
            }
            return true;
        }

        private static int CodePointAt(string/*!*/ str, int index, out int width) {
            char c = str[index];
            if (Char.IsHighSurrogate(c) && index + 1 < str.Length && Char.IsLowSurrogate(str[index + 1])) {
                width = 2;
                return Char.ConvertToUtf32(c, str[index + 1]);
            }
            width = 1;
            // A lone surrogate is not a scalar value; Rune would throw on it, so it is reported
            // as the replacement character and left alone by every mapping.
            return Char.IsSurrogate(c) ? 0xFFFD : c;
        }

        private static bool IsSame(string/*!*/ mapped, int codePoint) {
            return mapped.Length <= 2 && CodePointAt(mapped, 0, out int width) == codePoint && width == mapped.Length;
        }

        private static string Lookup(int[]/*!*/ keys, string[]/*!*/ values, int codePoint) {
            int index = Array.BinarySearch(keys, codePoint);
            return index < 0 ? null : values[index];
        }

        #endregion
    }
}

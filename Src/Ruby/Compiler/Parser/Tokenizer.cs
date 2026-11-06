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
using System.IO;
using IronRuby.Runtime;
using System.Numerics;
using Microsoft.Scripting.Utils;

namespace IronRuby.Compiler {
    /// <summary>
    /// What is left of the 1.9-era lexer now that prism is the only parser: the rules for
    /// Ruby names, digits and number literals, and the encoding magic comment, which the
    /// runtime asks about strings and symbols at run time.
    /// </summary>
    public static class Tokenizer {
        /// <summary>
        /// Converts all codepoints in range [0, 0x10ffff] to a string.
        /// Undefined for codepoint greater than 0x10ffff.
        /// </summary>
        public static string/*!*/ UnicodeCodePointToString(int codepoint) {
            if (codepoint < 0x10000) {
                // code-points [0xd800 .. 0xdfff] are not treated as invalid
                return new String((char)codepoint, 1);
            } else {
                codepoint -= 0x10000;
                return new String(new char[] { (char)((codepoint / 0x400) + 0xd800), (char)((codepoint % 0x400) + 0xdc00) });
            }
        }

        public static int ToCodePoint(int highSurrogate, int lowSurrogate) {
            return (highSurrogate - 0xd800) * 0x400 + (lowSurrogate - 0xdc00) + 0x10000;
        }

        public static bool IsHighSurrogate(int c) {
            return unchecked((uint)c - 0xd800 <= (uint)0xdbff - 0xd800);
        }

        public static bool IsLowSurrogate(int c) {
            return unchecked((uint)c - 0xdc00 <= (uint)0xdfff - 0xdc00);
        }

        public static bool IsSurrogate(int c) {
            return unchecked((uint)c - 0xd800 <= (uint)0xdfff - 0xd800);
        }

        public static bool IsDecimalDigit(int c) {
            return unchecked((uint)c - '0' <= (uint)'9' - '0');
        }

        public static int ToDigit(int c) {
            if (IsDecimalDigit(c)) {
                return c - '0';
            }

            if (IsLowerLetter(c)) {
                return c - 'a' + 10;
            }

            if (IsUpperLetter(c)) {
                return c - 'A' + 10;
            }

            return Int32.MaxValue;
        }

        // MRI 1.9 consideres all characters greater than 0x007f as identifiers.
        // Surrogate pairs are composed to a single character that is also considered an identifier.
        private const int AllowMultiByteIdentifier = 0x07f;

        public static bool IsIdentifier(int c, int multiByteIdentifier) {
            return IsIdentifierInitial(c, multiByteIdentifier) || IsDecimalDigit(c);
        }

        public static bool IsIdentifierInitial(int c, int multiByteIdentifier) {
            return IsLetter(c) || c == '_' || c > multiByteIdentifier;
        }

        public static bool IsLetter(int c) {
            return IsUpperLetter(c) || IsLowerLetter(c);
        }

        /// <summary>
        /// Ruby 2.6+: a constant may begin with any non-ASCII uppercase or titlecase letter.
        /// </summary>
        private static bool IsNonAsciiConstantInitial(string/*!*/ name, int index) {
            if (name[index] < 0x80) {
                return false;
            }
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(name, index);
            return category == System.Globalization.UnicodeCategory.UppercaseLetter
                || category == System.Globalization.UnicodeCategory.TitlecaseLetter;
        }

        public static bool IsUpperLetter(int c) {
            return unchecked((uint)c - 'A' <= (uint)'Z' - 'A');
        }

        public static bool IsLowerLetter(int c) {
            return unchecked((uint)c - 'a' <= (uint)'z' - 'a');
        }

        public static bool IsWhiteSpace(int c) {
            return IsAsciiWhiteSpace(c);
        }

        public static bool IsAsciiWhiteSpace(int c) {
            return unchecked(((uint)c - 9 <= (uint)13 - 9) || c == 32);
        }

        private static bool IsMethodNameSuffix(int c, int multiByteIdentifier) {
            return IsIdentifier(c, multiByteIdentifier) || c == '!' || c == '?' || c == '=';
        }

        private static int NextChar(string/*!*/ str, ref int i) {
            return i == str.Length ? -1 : str[i++];
        }

        public static IntegerValue ParseInteger(string/*!*/ str, int @base) {
            int i = 0;
            return ParseInteger(str, @base, ref i);
        }
        
        // @base == 0:
        //    [:whitespace:]*[+-]?(0x|0X|ob|0B|0d|0D|0o|0O)?([:base-digit:][_]?)*[:base-digit:].*
        // otherwise:
        //    [:whitespace:]*[+-]?([:base-digit:][_]?)*[:base-digit:].*
        public static IntegerValue ParseInteger(string/*!*/ str, int @base, ref int i) {
            ContractUtils.RequiresNotNull(str, "str");

            int c;
            do { c = NextChar(str, ref i); } while (IsWhiteSpace(c));

            int sign;
            if (c == '+') {
                sign = +1;
                c = NextChar(str, ref i);
            } else if (c == '-') {
                sign = -1;
                c = NextChar(str, ref i);
            } else {
                sign = +1;
            }

            // A leading zero is a digit of its own, so an underscore may follow it: "0_1" is 1.
            // After a base prefix there is no digit yet, and "0x_1" is nothing at all.
            bool afterDigit = false;

            if (c == '0') {
                c = NextChar(str, ref i);
                afterDigit = true;
                int newBase = 0;
                switch (c) {
                    case 'x':
                    case 'X': newBase = 16; break;
                    case 'b':
                    case 'B': newBase = 2; break;
                    case 'd':
                    case 'D': newBase = 10; break;
                    case 'o':
                    case 'O': newBase = 8; break;
                }

                if (newBase != 0) {
                    // no base specified -> set the base
                    // base specified -> skip prefix of that base
                    if (@base == 0 || newBase == @base) {
                        @base = newBase;
                        c = NextChar(str, ref i);
                        afterDigit = false;
                    }
                } else if (@base == 0) {
                    @base = 8;
                }
            } else if (@base == 0) {
                @base = 10;
            }

            bool underAllowed = afterDigit;
            long value = 0;
            int digitCount = 0;
            int start = i - 1;
            while (true) {
                if (c != '_') {
                    int digit = ToDigit(c);
                    if (digit < @base) {
                        if (value <= Int32.MaxValue) {
                            value = value * @base + digit;
                        }
                        digitCount++;
                    } else {
                        break;
                    }
                    underAllowed = true;
                } else if (underAllowed) {
                    underAllowed = false;
                } else {
                    break;
                }
                c = NextChar(str, ref i);
            }

            if (digitCount == 0) {
                return 0;
            }
            
            if (value <= Int32.MaxValue) {
                value *= sign;
                if (value >= Int32.MinValue && value <= Int32.MaxValue) {
                    return (int)value;
                } else {
                    return new BigInteger(value);
                }
            } else {
                return ParseBigInteger(str, start, digitCount, @base) * sign;
            }
        }

        /// <summary>
        /// The value of the <paramref name="digitCount"/> digits of <paramref name="str"/> from <paramref name="start"/> on,
        /// in the given base; underscores between them are skipped.
        /// </summary>
        internal static BigInteger ParseBigInteger(string/*!*/ str, int start, int digitCount, int @base) {
            var digits = new byte[digitCount];
            for (int i = 0, j = start; i < digitCount; j++) {
                if (str[j] != '_') {
                    digits[i++] = (byte)ToDigit(str[j]);
                }
            }

            int digitsPerUInt64 = 0;
            for (ulong power = 1, limit = UInt64.MaxValue / (ulong)@base; power <= limit; power *= (ulong)@base) {
                digitsPerUInt64++;
            }
            return ParseBigInteger(digits, @base, digitsPerUInt64);
        }

        // high * base^|low| + low: halving keeps the multiplications balanced, which BigInteger does in
        // subquadratic time, where adding one digit (or word) at a time is quadratic in the length.
        private static BigInteger ParseBigInteger(ReadOnlySpan<byte> digits, int @base, int digitsPerUInt64) {
            if (digits.Length <= digitsPerUInt64) {
                ulong value = 0;
                foreach (byte digit in digits) {
                    value = value * (ulong)@base + digit;
                }
                return value;
            }

            int lowLength = digits.Length / 2;
            return ParseBigInteger(digits[..^lowLength], @base, digitsPerUInt64) * BigInteger.Pow(@base, lowLength)
                + ParseBigInteger(digits[^lowLength..], @base, digitsPerUInt64);
        }

        // The header is matched by hand rather than with the regex ^[#].*?coding\s*[:=]\s*(?<encoding>[a-z0-9_-]+)
        // (case-insensitive): building that Regex was a few ms of every process's startup.

        // reads case insensitively, doesn't rewind the reader if the content doesn't match, doesn't read a line unless it starts with '#':
        // ([#][!].*(\r|\n|\r\n))?
        // [#].*?coding\s*[:=]\s*([a-z0-9_-]+).*(\r|\n|\r\n)
        internal static bool TryParseEncodingHeader(TextReader/*!*/ reader, out string encodingName) {
            Assert.NotNull(reader);

            encodingName = null;

            if (reader.Peek() != '#') {
                return false;
            }

            string line = reader.ReadLine();

            // skip shebang:
            if (line.Length > 1 && line[1] == '!') {
                if (reader.Peek() != '#') {
                    return false;
                }
                line = reader.ReadLine();
            }

            encodingName = MatchCodingComment(line);
            return encodingName != null;
        }

        /// <summary>
        /// The encoding name of the leftmost match of <c>coding\s*[:=]\s*([A-Za-z0-9_-]+)</c> in <paramref name="line"/>,
        /// "coding" matched ASCII-case-insensitively, or null - what the magic-comment regexes answered, without
        /// building a Regex.
        /// </summary>
        internal static string MatchCodingComment(string/*!*/ line) {
            for (int i = 0; i + 6 <= line.Length; i++) {
                if (!IsAsciiIgnoreCase(line, i, "coding")) {
                    continue;
                }
                int j = i + 6;
                while (j < line.Length && Char.IsWhiteSpace(line[j])) j++;
                if (j >= line.Length || (line[j] != ':' && line[j] != '=')) {
                    continue;
                }
                j++;
                while (j < line.Length && Char.IsWhiteSpace(line[j])) j++;
                int start = j;
                while (j < line.Length && IsCodingNameChar(line[j])) j++;
                if (j > start) {
                    return line.Substring(start, j - start);
                }
            }
            return null;
        }

        private static bool IsAsciiIgnoreCase(string/*!*/ str, int index, string/*!*/ lowerAscii) {
            for (int k = 0; k < lowerAscii.Length; k++) {
                char c = str[index + k];
                if (c >= 'A' && c <= 'Z') c = (char)(c + ('a' - 'A'));
                if (c != lowerAscii[k]) return false;
            }
            return true;
        }

        private static bool IsCodingNameChar(char c) {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
        }

        public static bool IsConstantName(string name) {
            return !String.IsNullOrEmpty(name) 
                && (IsUpperLetter(name[0]) || IsNonAsciiConstantInitial(name, 0))
                && IsVariableName(name, 1, 1, AllowMultiByteIdentifier)
                && IsIdentifier(name[name.Length - 1], AllowMultiByteIdentifier);
        }

        public static bool IsVariableName(string name) {
            return !String.IsNullOrEmpty(name)
                && IsIdentifierInitial(name[0], AllowMultiByteIdentifier)
                && IsVariableName(name, 1, 0, AllowMultiByteIdentifier);
        }

        public static bool IsMethodName(string name) {
            return !String.IsNullOrEmpty(name)
                && IsIdentifierInitial(name[0], AllowMultiByteIdentifier)
                && IsVariableName(name, 1, 1, AllowMultiByteIdentifier)
                && IsMethodNameSuffix(name[name.Length - 1], AllowMultiByteIdentifier);
        }

        public static bool IsInstanceVariableName(string name) {
            return name != null && name.Length >= 2
                && name[0] == '@'
                // the character after the sigil is an identifier *start*, so "@0" is not a name
                && !IsDecimalDigit(name[1])
                && IsVariableName(name, 1, 0, AllowMultiByteIdentifier);
        }

        public static bool IsClassVariableName(string name) {
            return name != null && name.Length >= 3
                && name[0] == '@'
                && name[1] == '@'
                && !IsDecimalDigit(name[2])
                && IsVariableName(name, 2, 0, AllowMultiByteIdentifier);
        }

        public static bool IsGlobalVariableName(string name) {
            return name != null && name.Length >= 2
                && name[0] == '$'
                && IsVariableName(name, 1, 0, AllowMultiByteIdentifier);
        }

        private static bool IsVariableName(string name, int trimStart, int trimEnd, int multiByteIdentifier) {
            for (int i = trimStart; i < name.Length - trimEnd; i++) {
                if (!IsIdentifier(name[i], multiByteIdentifier)) {
                    return false;
                }
            }

            return true;
        }

        public static bool IsOperatorName(string/*!*/ name) {
            if (name.Length <= 3) {
                switch (name) {
                    case "|":
                    case "^":
                    case "&":
                    case "<=>":
                    case "==":
                    case "===":
                    case "=~":
                    case ">":
                    case ">=":
                    case "<":
                    case "<=":
                    case "<<":
                    case ">>":
                    case "+":
                    case "-":
                    case "*":
                    case "/":
                    case "%":
                    case "**":
                    case "~":
                    case "+@":
                    case "-@":
                    case "[]":
                    case "[]=":
                    case "`":
                        return true;
                }
            }
            return false;
        }

    }
}

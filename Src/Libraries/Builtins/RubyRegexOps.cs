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
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime.Conversions;
using System.Collections.Generic;

namespace IronRuby.Builtins {
    // Not Includes(typeof(Enumerable)): MRI's Regexp is not enumerable, and pretending it is
    // gives it #to_a, which is what a splat looks for. `Regexp.union(*re)` - mustermann's
    // escape helper, so every Sinatra route - then called Enumerable#to_a on the Regexp and
    // died in #each instead of splatting to a one-element array.
    [RubyClass("Regexp", Extends = typeof(RubyRegex), Inherits = typeof(Object))]
    public static class RegexpOps {
        /// <summary>
        /// Raised when a match runs longer than `Regexp.timeout' or the pattern's own `timeout:'
        /// allows. .NET's own exception for this is the one that travels: it is thrown from deep
        /// inside the matching engine, and wrapping it at every call would only risk missing one.
        /// </summary>
        [RubyException("TimeoutError", Extends = typeof(RegexMatchTimeoutException), Inherits = typeof(RegexpError))]
        public static class TimeoutErrorOps {
            [RubyMethod("message"), RubyMethod("to_s")]
            public static MutableString/*!*/ GetMessage(RegexMatchTimeoutException/*!*/ self) {
                return MutableString.CreateAscii("regexp match timeout");
            }
        }

        #region Helpers

        internal static bool NormalizeGroupIndex(ref int index, int groupCount) {
            // Normalize index against # Groups in Match
            if (index < 0) {
                index += groupCount;
                // Cannot refer to zero using negative indices 
                if (index == 0) {
                    return false;
                }
            }

            if (index < 0 || index > groupCount) {
                return false;
            }

            return true;
        }

        #endregion

        #region constructors, compile

        [RubyConstructor]
        public static RubyRegex/*!*/ Create(RubyClass/*!*/ self, 
            [NotNull]RubyRegex/*!*/ other) {
            
            return new RubyRegex(other);
        }

        [RubyConstructor]
        public static RubyRegex/*!*/ Create(RubyClass/*!*/ self,
            [NotNull]RubyRegex/*!*/ other, int options, [Optional]object encoding) {
            return Create(self, other, (object)options, encoding);
        }

        [RubyConstructor]
        public static RubyRegex/*!*/ Create(RubyClass/*!*/ self,
            [NotNull]RubyRegex/*!*/ other, [DefaultParameterValue(null)]object options, [Optional]object encoding) {

            ReportParametersIgnoredWarning(self.Context, encoding);
            return new RubyRegex(other);
        }
                
        [RubyConstructor]
        public static RubyRegex/*!*/ Create(RubyClass/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ pattern, [Optional]object options, [DefaultProtocol, Optional]MutableString encoding) {

            // MRI rejects a pattern that is not valid in its own encoding before compiling it.
            if (pattern.Encoding != RubyEncoding.Binary && pattern.ContainsInvalidCharacters()) {
                throw new RegexpError("invalid multibyte character: /" + EscapeNonAsciiBytes(pattern) + "/");
            }
            double? timeout = TakeTimeout(self.Context, ref options);
            return new RubyRegex(pattern, MakeOptions(self.Context, options, encoding)) { Timeout = timeout };
        }

        // For an error message about bytes that do not decode: ASCII as is, the rest as \xHH.
        private static string/*!*/ EscapeNonAsciiBytes(MutableString/*!*/ str) {
            var result = new System.Text.StringBuilder();
            foreach (byte b in str.ToByteArray()) {
                if (b < 0x80) {
                    result.Append((char)b);
                } else {
                    result.Append("\\x").Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return result.ToString();
        }

        [RubyConstructor]
        public static RubyRegex/*!*/ Create(RubyClass/*!*/ self, DynamicNull pattern, [Optional]object options, [Optional]object encoding) {
            throw RubyExceptions.CreateImplicitConversionError("nil", "String");
        }

        [RubyMethod("compile", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ Compile() {
            return new RuleGenerator(RuleGenerators.InstanceConstructor);
        }

        /// <summary>
        /// MRI's rb_reg_check: a Regexp.allocate that was never initialized has no pattern, and
        /// everything that needs one - source, to_s, ==, hash, matching - is a TypeError.
        /// </summary>
        private static void RequireInitialized(RubyRegex/*!*/ self) {
            if (!self.IsInitialized) {
                throw RubyExceptions.CreateTypeError("uninitialized Regexp");
            }
        }

        [RubyMethod("timeout")]
        public static object GetTimeout(RubyRegex/*!*/ self) {
            RequireInitialized(self);
            double? timeout = self.Timeout;
            return timeout == null ? null : (object)timeout.Value;
        }

        [RubyMethod("timeout", RubyMethodAttributes.PublicSingleton)]
        public static object GetGlobalTimeout(RubyClass/*!*/ self) {
            double? timeout = RubyRegex.GlobalTimeout;
            return timeout == null ? null : (object)timeout.Value;
        }

        [RubyMethod("timeout=", RubyMethodAttributes.PublicSingleton)]
        public static object SetGlobalTimeout(RubyClass/*!*/ self, object value) {
            RubyRegex.GlobalTimeout = ToTimeout(self.Context, value);
            return value;
        }

        /// <summary>
        /// A timeout is a number of seconds, and nil means none. MRI rejects zero and negative
        /// values rather than treating them as "give up at once".
        /// </summary>
        private static double? ToTimeout(RubyContext/*!*/ context, object value) {
            if (value == null) {
                return null;
            }

            double seconds;
            if (value is double) {
                seconds = (double)value;
            } else if (value is int) {
                seconds = (int)value;
            } else if (value is long) {
                seconds = (long)value;
            } else if (value is BigInteger) {
                seconds = Protocols.ConvertToDouble(context, (BigInteger)value);
            } else {
                throw RubyExceptions.CreateTypeError("no implicit conversion to float from {0}",
                    context.GetClassDisplayName(value).ToLowerInvariant());
            }

            if (Double.IsNaN(seconds) || seconds <= 0) {
                throw RubyExceptions.CreateArgumentError("invalid timeout: {0}", context.Inspect(value).ToString());
            }
            return seconds;
        }

        /// <summary>
        /// Regexp.new's `timeout:' keyword. IronRuby has no keyword-argument slot, so it arrives
        /// as a trailing Hash in the place the flags go; only one written as keywords counts,
        /// because `Regexp.new(src, {})' is a positional argument MRI warns about instead.
        /// </summary>
        private static double? TakeTimeout(RubyContext/*!*/ context, ref object options) {
            var hash = options as Hash;
            if (hash == null || !hash.IsKeywordArguments) {
                return null;
            }

            object timeout = null;
            foreach (var entry in hash) {
                var key = entry.Key as RubySymbol;
                if (key == null || key.ToString() != "timeout") {
                    return null;
                }
                timeout = entry.Value;
            }

            options = Missing.Value;
            return ToTimeout(context, timeout);
        }

        /// <summary>
        /// A Regexp is initialized once. MRI rejects a second #initialize rather than quietly
        /// rewriting a pattern other code may already have matched with.
        /// </summary>
        private static void RequireUninitialized(RubyContext/*!*/ context, RubyRegex/*!*/ self) {
            if (context.IsObjectFrozen(self)) {
                throw RubyExceptions.CreateObjectFrozenError(context, self);
            }

            if (self.IsInitialized) {
                throw RubyExceptions.CreateTypeError("already initialized regexp");
            }
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyRegex/*!*/ Reinitialize(RubyContext/*!*/ context, RubyRegex/*!*/ self, [NotNull]RubyRegex/*!*/ other) {
            RequireUninitialized(context, self);
            RequireInitialized(other);
            self.Set(other.Pattern, other.Options);
            return self;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyRegex/*!*/ Reinitialize(RubyContext/*!*/ context, RubyRegex/*!*/ self,
            [NotNull]RubyRegex/*!*/ regex, int options, [Optional]object encoding) {

            ReportParametersIgnoredWarning(context, encoding);
            return Reinitialize(context, self, regex);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyRegex/*!*/ Reinitialize(RubyContext/*!*/ context, RubyRegex/*!*/ self,
            [NotNull]RubyRegex/*!*/ regex, [DefaultParameterValue(null)]object ignoreCase, [Optional]object encoding) {

            ReportParametersIgnoredWarning(context, encoding);
            return Reinitialize(context, self, regex);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyRegex/*!*/ Reinitialize(RubyContext/*!*/ context, RubyRegex/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ pattern, [Optional]object options, [DefaultProtocol, Optional]MutableString encoding) {

            RequireUninitialized(context, self);
            double? timeout = TakeTimeout(context, ref options);
            self.Set(pattern, MakeOptions(context, options, encoding));
            self.Timeout = timeout;
            return self;
        }

        private static void ReportParametersIgnoredWarning(RubyContext/*!*/ context, object encoding) {
            context.ReportWarning((encoding != Missing.Value) ? "flags and encoding ignored" : "flags ignored");
        }

        internal static RubyRegexOptions MakeOptions(bool ignoreCase, MutableString encoding) {
            return (ignoreCase ? RubyRegexOptions.IgnoreCase : RubyRegexOptions.NONE) | StringToRegexEncoding(encoding);
        }

        /// <summary>
        /// Regexp.new's second argument. MRI takes an Integer bit vector, a String of i/m/x flags,
        /// nil/false for none, and treats anything else as "true" after warning - notably it never
        /// calls #to_int or #to_str on it.
        /// </summary>
        internal static RubyRegexOptions MakeOptions(RubyContext/*!*/ context, object options, MutableString encoding) {
            RubyRegexOptions result;

            if (options == null || options is bool && !(bool)options || options == Missing.Value) {
                result = RubyRegexOptions.NONE;
            } else if (options is bool) {
                result = RubyRegexOptions.IgnoreCase;
            } else if (options is int) {
                result = PublicToInternalOptions((int)options);
            } else {
                var str = options as MutableString;
                if (str != null) {
                    result = ParseOptionString(str);
                } else {
                    context.ReportWarning("expected true or false as ignorecase: " + context.Inspect(options));
                    result = RubyRegexOptions.IgnoreCase;
                }
            }

            return result | StringToRegexEncoding(encoding);
        }

        private static RubyRegexOptions ParseOptionString(MutableString/*!*/ flags) {
            var result = RubyRegexOptions.NONE;
            int count = flags.GetCharCount();

            for (int i = 0; i < count; i++) {
                switch (flags.GetChar(i)) {
                    case 'i': result |= RubyRegexOptions.IgnoreCase; break;
                    case 'm': result |= RubyRegexOptions.Multiline; break;
                    case 'x': result |= RubyRegexOptions.Extended; break;
                    default:
                        throw RubyExceptions.CreateArgumentError("unknown regexp option: {0}", flags.ToString());
                }
            }

            return result;
        }

        internal static RubyRegexOptions MakeOptions(int options, MutableString encoding) {
            return PublicToInternalOptions(options) | StringToRegexEncoding(encoding);
        }

        /// <summary>
        /// Maps the bit vector Ruby exposes (IGNORECASE|EXTENDED|MULTILINE|FIXEDENCODING|NOENCODING)
        /// onto the internal flags. The two encoding bits collide with IronRuby's historical
        /// EUC/SJIS/UTF8 numbering, so they have to be translated rather than cast.
        /// </summary>
        internal static RubyRegexOptions PublicToInternalOptions(int options) {
            var result = (RubyRegexOptions)(options & (IGNORECASE | EXTENDED | MULTILINE));

            if ((options & NOENCODING) != 0) {
                result |= RubyRegexOptions.FIXED;
            }

            if ((options & FIXEDENCODING) != 0) {
                result |= RubyRegexOptions.FixedEncoding;
            }

            return result;
        }

        /// <summary>
        /// The inverse of <see cref="PublicToInternalOptions"/>: what Regexp#options returns.
        /// </summary>
        internal static int InternalToPublicOptions(RubyRegexOptions options) {
            int result = (int)(options & (RubyRegexOptions.IgnoreCase | RubyRegexOptions.Extended | RubyRegexOptions.Multiline));

            if ((options & (RubyRegexOptions.EUC | RubyRegexOptions.SJIS | RubyRegexOptions.UTF8 | RubyRegexOptions.FixedEncoding)) != 0) {
                result |= FIXEDENCODING;
            }

            if ((options & RubyRegexOptions.FIXED) != 0) {
                result |= NOENCODING;
            }

            return result;
        }

        internal static RubyRegexOptions StringToRegexEncoding(MutableString encoding) {
            if (MutableString.IsNullOrEmpty(encoding)) {
                return RubyRegexOptions.NONE;
            }

            switch (encoding.GetChar(0)) {
                case 'N':
                case 'n': return RubyRegexOptions.FIXED; 
                case 'E':
                case 'e': return RubyRegexOptions.EUC; 
                case 'S':
                case 's': return RubyRegexOptions.SJIS;
                case 'U':
                case 'u': return RubyRegexOptions.UTF8;
            }

            return RubyRegexOptions.NONE; 
        }
        
        #endregion

        [RubyConstant]
        public const int IGNORECASE = (int)RubyRegexOptions.IgnoreCase;

        [RubyConstant]
        public const int EXTENDED = (int)RubyRegexOptions.Extended;

        [RubyConstant]
        public const int MULTILINE = (int)RubyRegexOptions.Multiline;

        /// <summary>
        /// Set on a regexp whose encoding is fixed by its source or by an /u, /e or /s modifier.
        /// The value is MRI's, not RubyRegexOptions.FixedEncoding's: the two numbering schemes
        /// only agree on IGNORECASE, EXTENDED and MULTILINE.
        /// </summary>
        [RubyConstant]
        public const int FIXEDENCODING = 16;

        /// <summary>
        /// Set by the /n modifier: the regexp matches bytes rather than characters.
        /// </summary>
        [RubyConstant]
        public const int NOENCODING = 32;

        /// <summary>
        /// Returns "(?{enabled-options}-{disabled-options}:{pattern-with-forward-slash-escaped})".
        /// Doesn't escape forward slashes that are already escaped.
        /// </summary>
        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS(RubyRegex/*!*/ self) {
            // Ruby: doesn't wrap if there is a single embedded expression that evaluates to non-nil:
            // puts(/#{nil}#{/a/}#{nil}/)
            // We don't do that.

            RequireInitialized(self);
            return self.ToMutableString();
        }

        /// <summary>
        /// Returns "/{pattern-with-forward-slash-escaped}/"
        /// Doesn't escape forward slashes that are already escaped.
        /// An uninitialized Regexp shows as a plain object, "#&lt;Regexp:0x...&gt;", as in MRI.
        /// </summary>
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyRegex/*!*/ self) {
            if (!self.IsInitialized) {
                return RubyUtils.ObjectToMutableString(context, self);
            }
            return self.Inspect();
        }

        [RubyMethod("options")]
        public static int GetOptions(RubyRegex/*!*/ self) {
            if (!self.IsInitialized) {
                throw RubyExceptions.CreateTypeError("uninitialized Regexp");
            }
            return InternalToPublicOptions(self.Options);
        }

        [RubyMethod("encoding", Compatibility = RubyCompatibility.Ruby19)]
        public static RubyEncoding/*!*/ GetEncoding(RubyRegex/*!*/ self) {
            return self.Encoding;
        }

        /// <summary>
        /// True when the regexp can only match strings of one particular encoding, i.e. when an
        /// encoding modifier other than /n was given or the pattern itself isn't ASCII only.
        /// </summary>
        [RubyMethod("fixed_encoding?")]
        public static bool IsFixedEncoding(RubyRegex/*!*/ self) {
            // an uninitialized Regexp has no flags at all (MRI reads the FIXEDENCODING bit)
            return self.IsInitialized && self.IsFixedEncoding;
        }

        [RubyMethod("names")]
        public static RubyArray/*!*/ GetNames(RubyRegex/*!*/ self) {
            RequireInitialized(self);
            var result = new RubyArray();
            var seen = new List<string>();
            foreach (var name in self.GetGroupNames()) {
                if (!seen.Contains(name)) {
                    seen.Add(name);
                    result.Add(MutableString.Create(name, self.Encoding));
                }
            }
            return result;
        }

        [RubyMethod("named_captures")]
        public static Hash/*!*/ GetNamedCaptures(RubyContext/*!*/ context, RubyRegex/*!*/ self) {
            RequireInitialized(self);
            var result = new Hash(context);
            var names = self.GetGroupNames();
            for (int i = 0; i < names.Length; i++) {
                var key = MutableString.Create(names[i], self.Encoding).Freeze();
                RubyArray indices;
                object existing;
                if (result.TryGetValue(key, out existing)) {
                    indices = (RubyArray)existing;
                } else {
                    indices = new RubyArray();
                    result[key] = indices;
                }
                indices.Add(ScriptingRuntimeHelpers.Int32ToObject(i + 1));
            }
            return result;
        }

        [RubyMethod("casefold?")]
        public static bool IsCaseInsensitive(RubyRegex/*!*/ self) {
            RequireInitialized(self);
            return (self.Options & RubyRegexOptions.IgnoreCase) != 0;
        }

        /// <summary>
        /// Plain match used by the CLR callers (String#=~, String#index, ...): no block, no start
        /// offset, and it still sets $~.
        /// </summary>
        internal static MatchData Match(RubyScope/*!*/ scope, RubyRegex/*!*/ self, MutableString str) {
            return RubyRegex.SetCurrentMatchData(scope, self, str);
        }

        [RubyMethod("match")]
        public static object Match(RubyScope/*!*/ scope, [Optional]BlockParam block, RubyRegex/*!*/ self,
            [DefaultProtocol]MutableString str, [DefaultProtocol, DefaultParameterValue(0)]int start) {

            if (!self.IsInitialized) {
                throw RubyExceptions.CreateTypeError("uninitialized Regexp");
            }

            MatchData match;
            if (str == null) {
                match = null;
            } else {
                self.WarnHistoricalBinaryMatch(scope.RubyContext, str);
                match = self.MatchFromCharacter(str, start, false, true);
            }

            scope.GetInnerMostClosureScope().CurrentMatch = match;

            if (match != null && block != null) {
                object blockResult;
                block.Yield(match, out blockResult);
                return blockResult;
            }

            return match;
        }

        [RubyMethod("match")]
        public static object Match(RubyScope/*!*/ scope, [Optional]BlockParam block, RubyRegex/*!*/ self,
            [NotNull]RubySymbol/*!*/ symbol, [DefaultProtocol, DefaultParameterValue(0)]int start) {

            return Match(scope, block, self, symbol.String, start);
        }

        /// <summary>
        /// Whether the pattern matches from character <paramref name="start"/> on, as Regexp#match
        /// would, but without a MatchData or $~. \A and ^ still mean the start of the string.
        /// </summary>
        [RubyMethod("match?")]
        public static bool IsMatch(RubyRegex/*!*/ self, [DefaultProtocol]MutableString str, [DefaultProtocol, DefaultParameterValue(0)]int start) {
            if (!self.IsInitialized) {
                throw RubyExceptions.CreateTypeError("uninitialized Regexp");
            }
            return str != null && self.MatchFromCharacter(str, start, false) != null;
        }

        [RubyMethod("match?")]
        public static bool IsMatch(RubyRegex/*!*/ self, [NotNull]RubySymbol/*!*/ symbol, [DefaultProtocol, DefaultParameterValue(0)]int start) {
            return IsMatch(self, symbol.String, start);
        }

        [RubyMethod("hash")]
        public static int GetHash(RubyRegex/*!*/ self) {
            RequireInitialized(self);
            return self.GetHashCode();
        }

        [RubyMethod("=="), RubyMethod("eql?")]
        public static bool Equals(RubyRegex/*!*/ self, object other) {
            return false;
        }

        [RubyMethod("=="), RubyMethod("eql?")]
        public static bool Equals(RubyContext/*!*/ context, RubyRegex/*!*/ self, [NotNull]RubyRegex/*!*/ other) {
            if (ReferenceEquals(self, other)) {
                return true;
            }
            RequireInitialized(self);
            RequireInitialized(other);
            return self.Equals(other);
        }

        [RubyMethod("=~")]
        public static object MatchIndex(RubyScope/*!*/ scope, RubyRegex/*!*/ self, [DefaultProtocol]MutableString/*!*/ str) {
            MatchData match = RubyRegex.SetCurrentMatchData(scope, self, str);
            return (match != null) ? ScriptingRuntimeHelpers.Int32ToObject(match.CharacterIndex) : null;
        }

        [RubyMethod("=~")]
        public static object MatchIndex(RubyScope/*!*/ scope, RubyRegex/*!*/ self, [NotNull]RubySymbol/*!*/ symbol) {
            return MatchIndex(scope, self, symbol.String);
        }

        [RubyMethod("===")]
        public static bool CaseCompare(ConversionStorage<MutableString>/*!*/ stringTryCast, RubyScope/*!*/ scope, RubyRegex/*!*/ self, object obj) {
            MutableString str = RegexpOperand(stringTryCast, obj);
            if (str == null) {
                // MRI's rb_reg_eqq clears $~ for an operand that is not a String.
                scope.GetInnerMostClosureScope().CurrentMatch = null;
                return false;
            }
            return Match(scope, self, str) != null;
        }

        /// <summary>
        /// MRI's reg_operand: what a Regexp matches against is a String, or a Symbol standing
        /// for its own name. That is a special case of Regexp's own, not of the implicit String
        /// conversion - `"a" + :b` is still a TypeError. Answers null if it is neither.
        /// </summary>
        private static MutableString RegexpOperand(ConversionStorage<MutableString>/*!*/ stringTryCast, object obj) {
            var symbol = obj as RubySymbol;
            if (symbol != null) {
                return symbol.String;
            }
            return Protocols.TryCastToString(stringTryCast, obj);
        }

        [RubyMethod("~")]
        public static object ImplicitMatch(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope, RubyRegex/*!*/ self) {
            return MatchIndex(scope, self, Protocols.CastToString(stringCast, scope.GetInnerMostClosureScope().LastInputLine));
        }

        [RubyMethod("source")]
        public static MutableString/*!*/ Source(RubyRegex/*!*/ self) {
            RequireInitialized(self);
            // The source carries the regexp's own encoding, not the encoding of whatever string it
            // was built from: Regexp.new("abc") is US-ASCII even when "abc" was BINARY.
            var result = self.Pattern.Clone();
            result.ForceEncoding(self.Encoding);
            return result;
        }

        [RubyMethod("escape", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("quote", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Escape(RubyClass/*!*/ self, [NotNull]RubySymbol/*!*/ symbol) {
            return Escape(self, symbol.String);
        }

        [RubyMethod("escape", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("quote", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Escape(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
            MutableString result = RubyRegex.Escape(str).TaintBy(str);
            // rb_reg_quote: an ASCII-only string in an ASCII-compatible encoding comes back US-ASCII.
            if (str.Encoding.IsAsciiIdentity && result.IsAscii()) {
                result.ForceEncoding(RubyEncoding.Ascii);
            }
            return result;
        }

        [RubyMethod("try_convert", RubyMethodAttributes.PublicSingleton)]
        public static RubyRegex TryConvert(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ toRegexpStorage,
            RubyClass/*!*/ self, object obj) {

            var regex = obj as RubyRegex;
            if (regex != null) {
                return regex;
            }

            if (!Protocols.RespondTo(respondToStorage, obj, "to_regexp")) {
                return null;
            }

            var site = toRegexpStorage.GetCallSite("to_regexp");
            object result = site.Target(site, obj);

            regex = result as RubyRegex;
            if (regex == null) {
                var context = respondToStorage.Context;
                throw RubyExceptions.CreateTypeError("can't convert {0} into Regexp ({0}#to_regexp gives {1})",
                    context.GetClassDisplayName(obj), context.GetClassDisplayName(result)
                );
            }

            return regex;
        }

        /// <summary>
        /// Whether the pattern is free of the backtracking-only constructs (back references) that
        /// can make matching take more than linear time. Everything else IronRuby compiles is
        /// handled by a bounded automaton walk.
        /// </summary>
        [RubyMethod("linear_time?", RubyMethodAttributes.PublicSingleton)]
        public static bool IsLinearTime(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]RubyRegex/*!*/ regex, [Optional]object options) {
            if (options != Missing.Value && options != null) {
                context.ReportWarning("flags ignored");
            }
            return !RubyRegex.HasBackReference(regex.Pattern);
        }

        [RubyMethod("linear_time?", RubyMethodAttributes.PublicSingleton)]
        public static bool IsLinearTime(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ pattern,
            [Optional]object options) {

            return !RubyRegex.HasBackReference(pattern);
        }

        [RubyMethod("last_match", RubyMethodAttributes.PublicSingleton)]
        public static MatchData LastMatch(RubyScope/*!*/ scope, RubyClass/*!*/ self) {
            return scope.GetInnerMostClosureScope().CurrentMatch;
        }

        [RubyMethod("last_match", RubyMethodAttributes.PublicSingleton)]
        public static MutableString LastMatch(ConversionStorage<int>/*!*/ fixnumCast, RubyScope/*!*/ scope, RubyClass/*!*/ self,
            object groupIndex) {

            MatchData match = scope.GetInnerMostClosureScope().CurrentMatch;
            if (match == null) {
                // With no match at all MRI answers nil for any argument, without converting it.
                return null;
            }

            string name = GroupName(groupIndex);
            if (name != null) {
                if (!match.HasNamedGroup(name)) {
                    throw RubyExceptions.CreateIndexError("undefined group name reference: {0}", name);
                }
                return match.GetNamedGroupValue(name);
            }

            return match.GetNthGroupValue(Protocols.CastToFixnum(fixnumCast, groupIndex));
        }

        private static string GroupName(object groupIndex) {
            var symbol = groupIndex as RubySymbol;
            if (symbol != null) {
                return symbol.ToString();
            }

            var str = groupIndex as MutableString;
            return str != null ? str.ToString() : null;
        }

        [RubyMethod("union", RubyMethodAttributes.PublicSingleton)]
        public static RubyRegex/*!*/ Union(ConversionStorage<MutableString>/*!*/ stringCast, RespondToStorage/*!*/ respondToStorage,
            UnaryOpStorage/*!*/ toRegexpStorage, ConversionStorage<IList>/*!*/ toAry, RubyClass/*!*/ self, [NotNull]object/*!*/ obj) {

            IList list = Protocols.TryCastToArray(toAry, obj);
            if (list != null) {
                return Union(stringCast, respondToStorage, toRegexpStorage, list);
            }

            return Union(stringCast, respondToStorage, toRegexpStorage, new object[] { obj });
        }

        [RubyMethod("union", RubyMethodAttributes.PublicSingleton)]
        public static RubyRegex/*!*/ Union(ConversionStorage<MutableString>/*!*/ stringCast, RespondToStorage/*!*/ respondToStorage,
            UnaryOpStorage/*!*/ toRegexpStorage, RubyClass/*!*/ self, [NotNull]IList/*!*/ objs) {

            return Union(stringCast, respondToStorage, toRegexpStorage, objs);
        }

        [RubyMethod("union", RubyMethodAttributes.PublicSingleton)]
        public static RubyRegex/*!*/ Union(ConversionStorage<MutableString>/*!*/ stringCast, RespondToStorage/*!*/ respondToStorage,
            UnaryOpStorage/*!*/ toRegexpStorage, RubyClass/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ objs) {

            return Union(stringCast, respondToStorage, toRegexpStorage, objs);
        }

        private static RubyRegex/*!*/ Union(ConversionStorage<MutableString>/*!*/ stringCast, RespondToStorage/*!*/ respondToStorage,
            UnaryOpStorage/*!*/ toRegexpStorage, ICollection/*!*/ objs) {

            if (objs.Count == 0) {
                return new RubyRegex(MutableString.CreateAscii("(?!)"), RubyRegexOptions.NONE);
            }

            // Each part is either a Regexp, which contributes its (?opts:...) form, or a string
            // whose metacharacters are escaped. The result's encoding is negotiated first, because
            // a conflict there is an error rather than a regexp.
            var parts = new List<object>(objs.Count);
            foreach (var obj in objs) {
                var regex = obj as RubyRegex;
                if (regex == null) {
                    regex = TryConvert(respondToStorage, toRegexpStorage, null, obj);
                }
                if (regex != null) {
                    parts.Add(regex);
                } else if (objs.Count == 1 && obj is RubySymbol) {
                    parts.Add(((RubySymbol)obj).String);
                } else {
                    parts.Add(Protocols.CastToString(stringCast, obj));
                }
            }

            if (parts.Count == 1) {
                var single = parts[0] as RubyRegex;
                if (single != null) {
                    return single;
                }
            }

            var encoding = NegotiateUnionEncoding(parts);

            MutableString result = MutableString.CreateMutable(encoding);
            for (int i = 0; i < parts.Count; i++) {
                if (i > 0) {
                    result.Append('|');
                }

                var regex = parts[i] as RubyRegex;
                if (regex != null) {
                    regex.AppendTo(result);
                } else {
                    result.Append(RubyRegex.Escape((MutableString)parts[i]));
                }
            }

            return new RubyRegex(result, RubyRegexOptions.NONE);
        }

        /// <summary>
        /// MRI's rule for Regexp.union: only the parts that are not ASCII only get a say in the
        /// result's encoding, they all have to agree, and an ASCII incompatible one cannot be
        /// mixed with an ASCII only part at all.
        /// </summary>
        private static RubyEncoding/*!*/ NegotiateUnionEncoding(List<object>/*!*/ parts) {
            RubyEncoding result = null;
            bool hasAsciiOnlyPart = false;

            foreach (var part in parts) {
                RubyEncoding encoding;
                var regex = part as RubyRegex;
                if (regex != null) {
                    encoding = IsAsciiOnly(regex.Pattern) && (regex.Options & RubyRegexOptions.FixedEncoding) == 0
                        ? null : regex.Encoding;
                } else {
                    var str = (MutableString)part;
                    encoding = IsAsciiOnly(str) ? null : str.Encoding;
                }

                if (encoding == null) {
                    hasAsciiOnlyPart = true;
                } else if (result == null) {
                    result = encoding;
                } else if (result != encoding) {
                    throw RubyExceptions.CreateArgumentError("incompatible encodings: {0} and {1}",
                        result.Name, encoding.Name);
                }
            }

            if (result == null) {
                return RubyEncoding.Ascii;
            }

            if (hasAsciiOnlyPart && !result.IsAsciiIdentity) {
                throw RubyExceptions.CreateArgumentError("ASCII incompatible encoding: {0}", result.Name);
            }

            return result;
        }

        private static bool IsAsciiOnly(MutableString/*!*/ str) {
            // An ASCII incompatible encoding is never "ASCII only" however plain its characters
            // look: "a" in UTF-16LE is the two bytes 61 00.
            return str.Encoding.IsAsciiIdentity && str.IsAscii() && !RubyRegex.HasNonAsciiEscape(str);
        }
    }
}

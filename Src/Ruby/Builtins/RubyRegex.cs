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
using System.Diagnostics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Text;
using System.Text.RegularExpressions;
using IronRuby.Compiler;
using IronRuby.Runtime;
using System.Collections.Generic;

namespace IronRuby.Builtins {
    public partial class RubyRegex : IEquatable<RubyRegex>, IDuplicable, IRubyObjectState {
        // 1.9: correctly encoded, switched to characters 
        // 1.8: k-coded binary data, if _options specify encoding, or raw binary data otherwise.
        private MutableString/*!*/ _pattern;
        // HasNonAsciiEscape(_pattern) - the pattern is frozen, so it is worked out once: -1 not yet,
        // 0 none, 1 a byte escape, 2 a \u escape. Encoding and IsFixedEncoding ask on every match.
        private int _nonAsciiEscape = -1;
        private RubyRegexOptions _options;
        private bool _hasGAnchor;

        // Regexp.allocate produces a regexp with no pattern at all, which MRI reports as BINARY
        // rather than as the US-ASCII an empty pattern would give.
        private bool _initialized;

        private Regex _cachedRegex;

        // The same pattern translated for a subject that holds surrogate pairs, where '.' and \B
        // must treat a pair as the one character it is (see RegexpTransformer). A subject without
        // pairs - nearly all of them - never pays for that and matches with _cachedRegex.
        private Regex _cachedAstralRegex;
        private RubyRegexOptions _cachedAstralKCode;
        private TimeSpan _cachedAstralTimeout;

        // The same CLR regex forced to start where the search starts, for the scanning operations
        // that only ever look at the current position (StringScanner#scan, #skip, #check, #match?).
        // Cached against the regex it was derived from, so it follows _cachedRegex's own lifetime.
        private Regex _cachedAnchoredRegex;
        private Regex _cachedAnchoredSource;

        // How many matches _cachedRegex and _cachedAnchoredRegex have run while still interpreted.
        // A regex that keeps being used is rebuilt with RegexOptions.Compiled (see TierUp).
        private int _cachedRegexUses;
        private int _cachedAnchoredUses;

        // Ruby 1.8: match operations use KCODE encoding so we need to remember the one for which we have cached CLR Regex.
        private RubyRegexOptions _cachedKCode;

        /// <summary>
        /// How long a match with this pattern may run before it is abandoned, as
        /// `Regexp.new(src, timeout: seconds)' asked for. Null means the global setting applies.
        /// </summary>
        private double? _timeout;

        /// <summary>
        /// The timeout every pattern that does not carry one of its own matches under, as
        /// `Regexp.timeout=' set it. It is a property of the process in MRI too.
        /// </summary>
        private static double? _globalTimeout;

        public static double? GlobalTimeout {
            get { return _globalTimeout; }
            set { _globalTimeout = value; }
        }

        public double? Timeout {
            get { return _timeout; }
            set { _timeout = value; }
        }

        /// <summary>
        /// .NET fixes a Regex's timeout when it is constructed, so the cached one is only good
        /// while the effective timeout is the one it was built with.
        /// </summary>
        private TimeSpan _cachedTimeout;

        private TimeSpan EffectiveTimeout {
            get {
                double? seconds = _timeout ?? _globalTimeout;
                if (seconds == null || seconds.Value <= 0 || Double.IsNaN(seconds.Value)) {
                    return Regex.InfiniteMatchTimeout;
                }

                // A timeout longer than a TimeSpan can hold is no timeout at all; asking for one
                // would only throw where the matcher is built.
                if (seconds.Value >= TimeSpan.MaxValue.TotalSeconds) {
                    return Regex.InfiniteMatchTimeout;
                }
                return TimeSpan.FromSeconds(seconds.Value);
            }
        }

        private const int FrozenFlag = 1;
        private const int TaintedFlag = 2;
        private const int UntrustedFlag = 4;

        // A regexp literal is frozen by MRI, so the state has to live on the object itself rather
        // than in the context's instance data, which the literal-construction path has no access to.
        private int _flags;

        #region IRubyObjectState Members

        public bool IsFrozen {
            get { return (_flags & FrozenFlag) != 0; }
        }

        public bool IsTainted {
            get { return (_flags & TaintedFlag) != 0; }
            set { _flags = (_flags & ~TaintedFlag) | (value ? TaintedFlag : 0); }
        }

        public bool IsUntrusted {
            get { return (_flags & UntrustedFlag) != 0; }
            set { _flags = (_flags & ~UntrustedFlag) | (value ? UntrustedFlag : 0); }
        }

        public void Freeze() {
            _flags |= FrozenFlag;
        }

        #endregion

        #region Construction

        public RubyRegex() {
            _pattern = MutableString.CreateEmpty();
            _options = RubyRegexOptions.NONE;
        }

        public RubyRegex(MutableString/*!*/ pattern) 
            : this(pattern, RubyRegexOptions.NONE) {
        }

        public RubyRegex(MutableString/*!*/ pattern, RubyRegexOptions options) {
            Set(pattern, options);
        }

        public RubyRegex(RubyRegex/*!*/ regex) {
            ContractUtils.RequiresNotNull(regex, "regex");
            // Regexp.new(re), #dup and #clone: MRI's rb_reg_init_copy checks the original
            if (!regex.IsInitialized) {
                throw RubyExceptions.CreateTypeError("uninitialized Regexp");
            }
            Set(regex.Pattern, regex.Options);
        }

        public void Set(MutableString/*!*/ pattern, RubyRegexOptions options) {
            ContractUtils.RequiresNotNull(pattern, "pattern");
            _initialized = true;

            // RubyRegexOptions.Once is only used to determine how the Regexp object should be created and cached. 
            // It is not a property of the final object. /foo/ should compare equal with /foo/o.
            _options = options & ~RubyRegexOptions.Once;
            _cachedRegex = null;
            _cachedAstralRegex = null;
            _nonAsciiEscape = -1;

            RubyEncoding encoding = RubyEncoding.GetRegexEncoding(options);
            if (encoding != null) {
                _pattern = MutableString.CreateBinary(pattern.ToByteArray(), encoding ?? RubyEncoding.Binary).Freeze();
            } else {
                _pattern = pattern.PrepareForCharacterRead().Clone().Freeze();
            }
            
            TransformPattern(encoding, options & RubyRegexOptions.EncodingMask);
        }

        /// <summary>
        /// Creates a copy of the proc that has the same target, context, self object as this instance.
        /// Doesn't copy instance data.
        /// Preserves the class of the Regexp.
        /// </summary>
        protected virtual RubyRegex/*!*/ Copy() {
            return new RubyRegex(this);
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = Copy();
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        #endregion

        #region Transformation to CLR Regex

        /// <summary>
        /// MRI's rb_reg_prepare_enc. A broken string can never be matched, and a regexp that has
        /// pinned its encoding can only be matched against a string of that encoding - or, when the
        /// pinned encoding is ASCII compatible, against an ASCII-only string of any encoding.
        /// This is a regexp-versus-string rule, distinct from the string-versus-string
        /// compatibility MutableString.RequireCompatibleEncoding applies.
        /// </summary>
        private void RequireMatchableEncoding(MutableString/*!*/ input) {
            if (input.ContainsInvalidCharacters()) {
                throw RubyExceptions.CreateArgumentError("invalid byte sequence in {0}", input.Encoding.Name);
            }

            if (!IsFixedEncoding) {
                return;
            }

            var patternEncoding = Encoding;
            if (input.Encoding != patternEncoding && (!patternEncoding.IsAsciiIdentity || !input.IsAscii())) {
                throw new EncodingCompatibilityError(
                    "incompatible encoding regexp match (" + patternEncoding.Name + " regexp with " + input.Encoding.Name + " string)"
                );
            }
        }

        private Regex/*!*/ Transform(ref RubyEncoding encoding, MutableString/*!*/ input, int start, out string strInput) {
            bool astral;
            return Transform(ref encoding, input, start, out strInput, out astral);
        }

        /// <summary>
        /// <paramref name="astral"/> tells whether the subject the regex runs on holds a surrogate
        /// pair: CLR offsets then are not character offsets, and an empty match has to step over
        /// a whole pair.
        /// </summary>
        private Regex/*!*/ Transform(ref RubyEncoding encoding, MutableString/*!*/ input, int start, out string strInput, out bool astral) {
            return Transform(ref encoding, input, start, out strInput, out astral, true);
        }

        /// <summary>
        /// <paramref name="countUse"/> is false when the caller is not going to match with the
        /// regex it gets back (MatchWindow, which derives an anchored one from it), so that only
        /// the regex that actually runs works its way up to being compiled.
        /// </summary>
        private Regex/*!*/ Transform(ref RubyEncoding encoding, MutableString/*!*/ input, int start, out string strInput, out bool astral, bool countUse) {
            ContractUtils.RequiresNotNull(input, "input");

            RequireMatchableEncoding(input);

            // TODO:

            // K-coding of the current operation (the current KCODE gets preference over the KCODE regex option):
            RubyRegexOptions kc = _options & RubyRegexOptions.EncodingMask;
            if (kc != 0) {
                encoding = _pattern.Encoding;
            } else {
                kc = RubyRegexOptions.NONE;
            }

            // Convert input to a string. Force k-coding if necessary.
            if (kc != 0) {
                // Handling multi-byte K-coded characters is not entirely correct here.
                // Three cases to be considered:
                // 1) Multi-byte character is explicitly contained in the pattern: /�*/
                // 2) Subsequent escapes form a complete character: /\342\202\254*/ or /\xe2\x82\xac*/
                // 3) Subsequent escapes form an incomplete character: /[\x7f-\xff]{1,3}/
                //
                // In the first two cases we want to "group" the byte triplet so that regex operators like *, +, ? and {n,m} operate on 
                // the entire character, not just the last byte. We could unescape the bytes and replace them with complete Unicode characters.
                // Then we could encode the input using the same K-coding and we would get a match. 
                // However, case 3) requires the opposite: to match the bytes we need to encode the input using binary encoding. 
                // Using this encoding makes *+? operators operate on the last byte (encoded as UTF16 character).
                // 
                // The right solution would require the regex engine to handle multi-byte escaped characters, which it doesn't.
                //
                // TODO:
                // A correct workaround would be to wrap the byte sequence that forms a character into a non-capturing group, 
                // for example transform /\342\202\254*/ to /(?:\342\202\254)*/ and use binary encoding on both input and pattern.
                // For now, we just detect if there are any non-ascii character escapes. If so we use a binary encoding accomodating case 3), 
                // but breaking cases 1 and 2. Otherwise we encode using k-coding to make case 1 match.
                if (HasEscapedNonAsciiBytes(_pattern)) {
                    encoding = RubyEncoding.Binary;
                    kc = 0;
                }
                
                if (start == 0 && encoding == RubyEncoding.Binary && input.Encoding == RubyEncoding.Binary) {
                    // Binary in, binary out: one byte is one character either way, so forcing the
                    // coding produces exactly the string the subject already knows how to be - and
                    // MutableString keeps that one, where ForceEncoding builds a fresh copy on
                    // every match. Only binary: a wider k-coding decodes the bytes differently
                    // from the subject's own encoding, and the offsets would not line up.
                    input.PrepareForCharacterRead();
                    strInput = input.ConvertToString();
                } else {
                    strInput = ForceEncoding(input, encoding.Encoding, start);
                }
                astral = strInput != null && !encoding.IsSingleByteCharacterSet && strInput.AsSpan().IndexOfAnyInRange('\uD800', '\uDBFF') >= 0;
            } else {
                _pattern.RequireCompatibleEncoding(input);
                input.PrepareForCharacterRead();
                strInput = input.ConvertToString();
                // O(1): matching has just validated the input, which works the flags out
                astral = input.HasSurrogatePairs();
            }

            return astral ? TransformAstralPattern(encoding, kc) : TransformPattern(encoding, kc, countUse);
        }

        private Regex/*!*/ TransformAstralPattern(RubyEncoding encoding, RubyRegexOptions kc) {
            TimeSpan timeout = EffectiveTimeout;
            if (_cachedAstralRegex != null && kc == _cachedAstralKCode && timeout == _cachedAstralTimeout) {
                return _cachedAstralRegex;
            }

            // the plain translation first: it validates the pattern and sets _hasGAnchor
            Regex plain = TransformPattern(encoding, kc);
            Regex result;
            try {
                bool hasGAnchor;
                result = new Regex(RegexpTransformer.Transform(GetClrPatternSource(encoding, kc), _options, out hasGAnchor, true), plain.Options, timeout);
            } catch (Exception) {
                result = plain;
            }

            _cachedAstralTimeout = timeout;
            _cachedAstralKCode = kc;
            _cachedAstralRegex = result;
            return result;
        }

        private string/*!*/ GetClrPatternSource(RubyEncoding encoding, RubyRegexOptions kc) {
            return (kc != 0 || encoding == RubyEncoding.Binary) ? _pattern.ToString(encoding.Encoding) : _pattern.ConvertToString();
        }

        private Regex/*!*/ TransformPattern(RubyEncoding encoding, RubyRegexOptions kc) {
            return TransformPattern(encoding, kc, false);
        }

        private Regex/*!*/ TransformPattern(RubyEncoding encoding, RubyRegexOptions kc, bool countUse) {
            // We can reuse cached CLR regex if it was created for the same k-coding and the
            // same timeout:
            TimeSpan timeout = EffectiveTimeout;
            Regex cached = _cachedRegex;
            if (cached != null && kc == _cachedKCode && timeout == _cachedTimeout) {
                if (countUse && (cached.Options & RegexOptions.Compiled) == 0 && ++_cachedRegexUses >= CompileThreshold) {
                    _cachedRegex = cached = TierUp(cached);
                }
                return cached;
            }

            string pattern = GetClrPatternSource(encoding, kc);

            Regex result;
            try {
                result = new Regex(RegexpTransformer.Transform(pattern, _options, out _hasGAnchor), ToClrOptions(_options), timeout);
            } catch (Exception e) {
                throw new RegexpError(e.Message);
            }

            _cachedTimeout = timeout;
            _cachedKCode = kc;
            _cachedRegexUses = 0;
            _cachedRegex = result;
            return result;
        }

        /// <summary>
        /// Matches a regex runs before it is compiled. The CLR interprets a Regex unless told to
        /// compile it, and compiling costs a millisecond or more - about what the compiled code
        /// then saves over ten thousand matches of a typical pattern - so only a regex that has
        /// already shown it is hot pays for it: one in a loop over a large input, a tokenizer's.
        /// </summary>
        private const int CompileThreshold = 10000;

        /// <summary>
        /// The same CLR regex compiled to IL. Compiled and interpreted regexes are the same
        /// engine and the same pattern tree and match identically; only the speed differs. If it
        /// cannot be built for any reason, the interpreted one is kept.
        /// </summary>
        private static Regex/*!*/ TierUp(Regex/*!*/ regex) {
            try {
                return new Regex(regex.ToString(), regex.Options | RegexOptions.Compiled, regex.MatchTimeout);
            } catch (Exception) {
                return regex;
            }
        }

        /// <summary>
        /// Searches the pattern for hexadecimal and octal character escapes that represent a non-ASCII character.
        /// </summary>
        private static bool HasEscapedNonAsciiBytes(MutableString/*!*/ pattern) {
            int i = 0;
            int length = pattern.GetByteCount();
            while (i < length - 2) {
                int c = pattern.GetByte(i++);
                if (c == '\\') {
                    c = pattern.GetByte(i++);
                    if (c == 'x') {
                        // hexa escape:
                        int d1 = Tokenizer.ToDigit(PeekByte(pattern, length, i++));
                        if (d1 < 16) {
                            int d2 = Tokenizer.ToDigit(PeekByte(pattern, length, i++));
                            if (d2 < 16) {
                                return (d1 * 16 + d2 >= 0x80);
                            }
                        }
                    } else if (c >= '2' && c <= '7') {
                        // a backreference (\1..\9) or an octal escape:
                        int d = Tokenizer.ToDigit(PeekByte(pattern, length, i++));
                        if (d < 8) {
                            int value = Tokenizer.ToDigit(c) * 8 + d;
                            d = Tokenizer.ToDigit(PeekByte(pattern, length, i++));
                            if (d < 8) {
                                value = value * 8 + d;
                            }
                            return value >= 0x80;
                        }
                    }
                }
            }

            return false;
        }

        private static int PeekByte(MutableString/*!*/ str, int length, int i) {
            return (i < length) ? str.GetByte(i) : -1;
        }

        private static string/*!*/ ForceEncoding(MutableString/*!*/ input, Encoding/*!*/ encoding, int start) {
            int byteCount = input.GetByteCount();

            if (start < 0) {
                start += byteCount;
            }
            if (start < 0) {
                return null;
            }

            return (start <= byteCount) ? input.ToString(encoding, start, byteCount - start) : null;
        }

        #endregion

        public bool IsEmpty {
            get { return _pattern.IsEmpty; }
        }

        public RubyRegexOptions Options {
            get { return _options; }
        }

        /// <summary>
        /// False only for a Regexp produced by Regexp.allocate, which has no pattern yet.
        /// </summary>
        public bool IsInitialized {
            get { return _initialized; }
        }

        public RubyEncoding/*!*/ Encoding {
            get {
                // MRI's rb_reg_encoding. A regexp with no encoding flag whose source happens to be
                // ASCII only is US-ASCII, whatever the encoding of the file it was written in, so
                // that it can match a string in any ASCII compatible encoding.
                //
                // /n needs the extra escape check: MRI decides it on the bytes the pattern compiles
                // to, where an \xFF escape is one non-ASCII byte, while _pattern still holds the
                // four ASCII characters of the escape itself. So /ASCII/n is US-ASCII but
                // /\xc2\xa1/n is BINARY.
                if (!_initialized) {
                    return _pattern.Encoding;
                }

                // Regexp::FIXEDENCODING asks for the source's encoding to be kept, ASCII only
                // or not: that is the whole of what it says.
                if ((_options & RubyRegexOptions.FixedEncoding) != 0) {
                    return _pattern.Encoding;
                }

                var encodingOptions = _options & RubyRegexOptions.EncodingMask;
                if (encodingOptions == RubyRegexOptions.NONE && _pattern.IsAscii()) {
                    // é is six ASCII characters that compile to one UTF-8 character, so the
                    // regexp is UTF-8 even though its source is ASCII only. (A \xNN or octal escape
                    // above 7 bits without /n is "invalid multibyte escape" in MRI; we don't raise,
                    // and the pattern's own encoding is as good an answer as any.)
                    bool isUnicode;
                    if (PatternHasNonAsciiEscape(out isUnicode)) {
                        return isUnicode ? RubyEncoding.UTF8 : _pattern.Encoding;
                    }

                    return RubyEncoding.Ascii;
                }

                if (encodingOptions == RubyRegexOptions.FIXED && _pattern.IsAscii() && !PatternHasNonAsciiEscape(out _)) {
                    return RubyEncoding.Ascii;
                }

                return _pattern.Encoding;
            }
        }

        public MutableString/*!*/ Pattern {
            get { return _pattern; }
        }

        #region Pattern inspection (group names, back references)

        private static readonly string[] EmptyNames = new string[0];

        /// <summary>
        /// The names of the pattern's named groups, in the order they open, with duplicates kept so
        /// that the position in the array is the group's Ruby index - 1. When a pattern uses named
        /// groups the plain parenthesised groups don't capture, so those are exactly the capture
        /// group numbers.
        /// </summary>
        public string[]/*!*/ GetGroupNames() {
            return ScanGroupNames(_pattern);
        }

        internal static string[]/*!*/ ScanGroupNames(MutableString/*!*/ pattern) {
            List<string> names = null;
            int length = pattern.GetCharCount();
            bool inClass = false;

            for (int i = 0; i < length; i++) {
                char c = pattern.GetChar(i);

                if (c == '\\') {
                    i++;
                    continue;
                }

                if (inClass) {
                    if (c == ']') {
                        inClass = false;
                    }
                    continue;
                }

                if (c == '[') {
                    inClass = true;
                    continue;
                }

                if (c != '(' || i + 2 >= length || pattern.GetChar(i + 1) != '?') {
                    continue;
                }

                char kind = pattern.GetChar(i + 2);
                char terminator;
                if (kind == '<') {
                    // (?<= and (?<! are look-behind, not a named group
                    if (i + 3 < length) {
                        char next = pattern.GetChar(i + 3);
                        if (next == '=' || next == '!') {
                            continue;
                        }
                    }
                    terminator = '>';
                } else if (kind == '\'') {
                    terminator = '\'';
                } else {
                    continue;
                }

                int start = i + 3;
                int end = start;
                while (end < length && pattern.GetChar(end) != terminator) {
                    end++;
                }

                if (end >= length) {
                    break;
                }

                var name = new StringBuilder(end - start);
                for (int j = start; j < end; j++) {
                    name.Append(pattern.GetChar(j));
                }

                (names ?? (names = new List<string>())).Add(name.ToString());
                i = end;
            }

            return names != null ? names.ToArray() : EmptyNames;
        }

        /// <summary>
        /// Whether the pattern contains a back reference (\1..\9, \k&lt;name&gt;, \k'name').
        /// Those are the constructs that force the matcher to backtrack in a way that can take
        /// more than linear time; see Regexp.linear_time?.
        /// </summary>
        public static bool HasBackReference(MutableString/*!*/ pattern) {
            int length = pattern.GetCharCount();
            for (int i = 0; i < length - 1; i++) {
                if (pattern.GetChar(i) != '\\') {
                    continue;
                }

                char c = pattern.GetChar(i + 1);
                if (c >= '1' && c <= '9' || c == 'k') {
                    return true;
                }

                // an escaped backslash isn't the start of a back reference
                i++;
            }

            return false;
        }

        /// <summary>
        /// Whether the pattern contains an escape that denotes a character outside ASCII: \xNN or
        /// \uNNNN or \NNN (octal) with a value above 0x7f.
        ///
        /// The pattern text of such a regexp is all ASCII, so String#ascii_only? says yes, but the
        /// regexp still only matches one encoding's bytes - MRI's rb_reg_preprocess sets
        /// ARG_ENCODING_FIXED for exactly this case.  Regexp.union has to know, or a /n regexp
        /// written with byte escapes would silently lose its encoding when unioned with a plain
        /// ASCII string.
        /// </summary>
        private bool PatternHasNonAsciiEscape(out bool isUnicode) {
            int cached = _nonAsciiEscape;
            if (cached < 0) {
                cached = HasNonAsciiEscape(_pattern, out isUnicode) ? (isUnicode ? 2 : 1) : 0;
                _nonAsciiEscape = cached;
            }
            isUnicode = cached == 2;
            return cached != 0;
        }

        public static bool HasNonAsciiEscape(MutableString/*!*/ pattern) {
            bool isUnicode;
            return HasNonAsciiEscape(pattern, out isUnicode);
        }

        /// <summary>
        /// As above, and tells whether the escape that decided it was a \u one.  \u names a code
        /// point, so it compiles to UTF-8; \xNN and \NNN name a byte, so they compile to BINARY
        /// (and MRI only allows them at all under /n).
        /// </summary>
        public static bool HasNonAsciiEscape(MutableString/*!*/ pattern, out bool isUnicode) {
            isUnicode = false;
            int length = pattern.GetCharCount();
            for (int i = 0; i < length - 1; i++) {
                if (pattern.GetChar(i) != '\\') {
                    continue;
                }

                int value = -1;
                char c = pattern.GetChar(i + 1);
                if (c == 'x') {
                    // \xN and \xNN; a lone \x is a syntax error the matcher will report.
                    value = HexValue(pattern, i + 2, 2);
                } else if (c == 'u') {
                    // \uNNNN, or \u{...} whose first digit already decides it.
                    value = (i + 2 < length && pattern.GetChar(i + 2) == '{')
                        ? HexValue(pattern, i + 3, 6) : HexValue(pattern, i + 2, 4);
                } else if (c >= '0' && c <= '7') {
                    value = 0;
                    for (int j = i + 1; j < length && j < i + 4; j++) {
                        char digit = pattern.GetChar(j);
                        if (digit < '0' || digit > '7') {
                            break;
                        }
                        value = value * 8 + (digit - '0');
                    }
                }

                if (value > 0x7f) {
                    isUnicode = c == 'u';
                    return true;
                }

                // an escaped backslash isn't the start of an escape
                i++;
            }

            return false;
        }

        /// <summary>
        /// Reads up to <paramref name="maxDigits"/> hex digits starting at <paramref name="start"/>,
        /// or -1 when there isn't one.
        /// </summary>
        private static int HexValue(MutableString/*!*/ pattern, int start, int maxDigits) {
            int length = pattern.GetCharCount();
            int value = -1;
            for (int i = start; i < length && i < start + maxDigits; i++) {
                char c = pattern.GetChar(i);
                int digit;
                if (c >= '0' && c <= '9') {
                    digit = c - '0';
                } else if (c >= 'a' && c <= 'f') {
                    digit = c - 'a' + 10;
                } else if (c >= 'A' && c <= 'F') {
                    digit = c - 'A' + 10;
                } else {
                    break;
                }
                value = (value < 0 ? 0 : value * 16) + digit;
            }
            return value;
        }

        #endregion

        /// <summary>
        /// Whether the regexp can only match strings of one particular encoding: an encoding
        /// modifier other than /n was given, or the pattern itself isn't ASCII only.
        /// </summary>
        public bool IsFixedEncoding {
            get {
                if ((_options & RubyRegexOptions.FIXED) != 0) {
                    // /n on its own says nothing - /abc/n matches any ASCII compatible string. It
                    // pins the encoding only once the pattern really compiles to non-ASCII bytes,
                    // which it does for a byte escape as much as for a literal non-ASCII character.
                    bool isUnicode;
                    return !_pattern.IsAscii() || PatternHasNonAsciiEscape(out isUnicode);
                }

                return (_options & (RubyRegexOptions.EUC | RubyRegexOptions.SJIS | RubyRegexOptions.UTF8 | RubyRegexOptions.FixedEncoding)) != 0
                    || Encoding != RubyEncoding.Ascii;
            }
        }

        // The flags that make two regexps behave differently. /n is not one of them: it only says
        // the pattern's bytes are not to be reinterpreted, so // and //n are the same regexp, while
        // /abc/u and /abc/n differ because only the former pins an encoding (see IsFixedEncoding).
        private const RubyRegexOptions BehaviouralOptions =
            RubyRegexOptions.IgnoreCase | RubyRegexOptions.Extended | RubyRegexOptions.Multiline;

        public bool Equals(RubyRegex other) {
            return ReferenceEquals(this, other)
                || other != null
                && (_options & BehaviouralOptions) == (other._options & BehaviouralOptions)
                && IsFixedEncoding == other.IsFixedEncoding
                && Encoding == other.Encoding
                && PatternEquals(_pattern, other._pattern);
        }

        private static bool PatternEquals(MutableString/*!*/ x, MutableString/*!*/ y) {
            // /n keeps the pattern as binary where the same literal without it keeps the source
            // encoding, so the two hold the same characters in differently tagged strings.
            return x.Equals(y) || x.IsAscii() && y.IsAscii() && x.ToString() == y.ToString();
        }

        public override bool Equals(object other) {
            return Equals(other as RubyRegex);
        }

        public override int GetHashCode() {
            int pattern = _pattern.IsAscii() ? _pattern.ToString().GetHashCode() : _pattern.GetHashCode();
            return pattern ^ (int)(_options & BehaviouralOptions);
        }

        public static RegexOptions ToClrOptions(RubyRegexOptions options) {
            RegexOptions result = RegexOptions.Multiline | RegexOptions.CultureInvariant;

#if DEBUG
            if (RubyOptions.CompileRegexps) {
#if SILVERLIGHT || WIN8 // RegexOptions.Compiled
                throw new NotSupportedException("RegexOptions.Compiled is not supported on Silverlight");
#else
                result |= RegexOptions.Compiled;
#endif
            }
#endif
            if ((options & RubyRegexOptions.IgnoreCase) != 0) {
                result |= RegexOptions.IgnoreCase;
            }

            if ((options & RubyRegexOptions.Extended) != 0) {
                result |= RegexOptions.IgnorePatternWhitespace;
            }

            if ((options & RubyRegexOptions.Multiline) != 0) {
                result |= RegexOptions.Singleline;
            }

            return result;
        }

        #region Match, LastMatch, Matches, Split

        /// <summary>
        /// Ruby's pos argument - Regexp#match(str, pos), String#index(re, pos), #match? - is a
        /// character index, negative from the end, while Match and LastMatch take the index
        /// space the regex runs in: CLR chars, or bytes under a k-coding. Answers false when the
        /// position is outside the string.
        /// </summary>
        private bool ToSearchStart(MutableString/*!*/ input, ref int start, bool clamp) {
            if ((_options & RubyRegexOptions.EncodingMask) == 0 || input.HasByteCharacters) {
                // the regex runs on the string's own characters (or on bytes that are characters)
                if (start == 0 || input.KnowsCharIndexIsClrIndex) {
                    return true;
                }
                input.PrepareForCharacterRead();
                if (!input.HasSurrogatePairs()) {
                    return true;
                }
            }

            int length = input.GetCharacterCount();
            if (start < 0) {
                start += length;
                if (start < 0) {
                    return false;
                }
            }
            if (start > length) {
                if (!clamp) {
                    return false;
                }
                start = length;
            }

            if ((_options & RubyRegexOptions.EncodingMask) == 0 || input.HasByteCharacters) {
                start = input.ToClrIndex(start);
            } else {
                input.PrepareForCharacterRead();
                start = input.GetSlice(0, input.ToClrIndex(start)).GetByteCount();
            }
            return true;
        }

        /// <summary>
        /// Match starting at Ruby character index <paramref name="start"/> (negative counts from
        /// the end). Null if there is no match or the start is outside the string.
        /// </summary>
        public MatchData MatchFromCharacter(MutableString/*!*/ input, int start, bool freezeInput) {
            return MatchFromCharacter(input, start, freezeInput, false);
        }

        /// <summary>
        /// With <paramref name="clampToEnd"/> a start past the end searches from the end, which is
        /// what Regexp#match and String#match do (MRI's rb_str_offset stops at the end); #index
        /// and #match? answer nil resp. false instead.
        /// </summary>
        public MatchData MatchFromCharacter(MutableString/*!*/ input, int start, bool freezeInput, bool clampToEnd) {
            if (clampToEnd && start > 0) {
                int length = input.GetCharacterCount();
                if (start > length) {
                    start = length;
                }
            }
            if (!ToSearchStart(input, ref start, false)) {
                return null;
            }
            return Match(input, start, freezeInput);
        }

        /// <summary>
        /// The last match starting at or before Ruby character index <paramref name="start"/>.
        /// </summary>
        public MatchData LastMatchFromCharacter(MutableString/*!*/ input, int start) {
            if (start != Int32.MaxValue && !ToSearchStart(input, ref start, true)) {
                return null;
            }
            return LastMatch(input, start);
        }


        public MatchData Match(MutableString/*!*/ input) {
            string str;
            RubyEncoding kcode = null;
            bool astral;
            Regex regex = Transform(ref kcode, input, 0, out str, out astral);
            return MatchData.Create(astral ? MatchOutsidePairs(regex, str, 0) : regex.Match(str), input, true, str, kcode, 0, this, astral);
        }

        /// <summary>
        /// A search in a subject with surrogate pairs: .NET tries every UTF-16 position in turn,
        /// the one between the two halves of a pair included, and a match can start there - a loop
        /// over a class that takes in both halves ([^"]*, .*), \B, a lookaround. None of those
        /// are positions Ruby has, so such a match is dropped and the search goes on from the
        /// next character. (Only a match nothing before it could make gets that far.)
        /// </summary>
        private static Match/*!*/ MatchOutsidePairs(Regex/*!*/ regex, string/*!*/ str, int start) {
            while (true) {
                Match match = regex.Match(str, start);
                if (!match.Success || !IsInsidePair(str, match.Index)) {
                    return match;
                }
                start = match.Index + 1;
            }
        }

        /// <summary>
        /// The same over a window of the subject. A window's start is where \A matches, so the
        /// search cannot be restarted past the dropped match with a narrower window; it goes on
        /// with NextMatch instead, inside the same window.
        /// </summary>
        private static Match/*!*/ MatchOutsidePairs(Regex/*!*/ regex, string/*!*/ str, int start, int length) {
            Match match = regex.Match(str, start, length);
            while (match.Success && IsInsidePair(str, match.Index)) {
                match = match.NextMatch();
            }
            return match;
        }

        internal static bool IsInsidePair(string/*!*/ str, int index) {
            return index > 0 && index < str.Length && Char.IsLowSurrogate(str[index]) && Char.IsHighSurrogate(str[index - 1]);
        }

        /// <summary>
        /// Start is a number of bytes if kcode is given, otherwise it's a number of characters.
        /// </summary>
        public MatchData Match(MutableString/*!*/ input, int start, bool freezeInput) {
            string str;
            RubyEncoding kcode = null;
            bool astral;
            Regex regex = Transform(ref kcode, input, start, out str, out astral);

            Match match;
            if (kcode != null) {
                if (str == null) {
                    return null;
                }
                match = astral ? MatchOutsidePairs(regex, str, 0) : regex.Match(str, 0);
            } else {
                if (start < 0) {
                    start += str.Length;
                }
                if (start < 0 || start > str.Length) {
                    return null;
                }
                match = astral ? MatchOutsidePairs(regex, str, start) : regex.Match(str, start);
            }

            return MatchData.Create(match, input, freezeInput, str, kcode, (kcode != null) ? ((start < 0) ? start + input.GetByteCount() : start) : 0, this, astral);
        }

        /// <summary>
        /// Matches the window that starts at <paramref name="start"/> and runs to the end of the
        /// input, as if the rest of the string were the whole subject: \A, ^ and \z anchor to the
        /// window, not to the string it was cut from. That is what StringScanner needs, and taking
        /// a window costs nothing, where copying the rest of the subject out into a string of its
        /// own - which is how the scanner used to get those semantics - costs a pass over it on
        /// every single scan.
        ///
        /// With <paramref name="anchored"/> the match has to begin at the window's first character
        /// rather than anywhere in it, which is what Onigmo's onig_match does for MRI's scanning
        /// operations. Searching instead and throwing the result away when it started too late is
        /// the same answer, but it reads the whole rest of the subject to produce it.
        ///
        /// Offsets in the returned MatchData are relative to the whole input, as with #Match.
        /// Start is a number of bytes if kcode is given, otherwise a number of characters.
        /// </summary>
        public MatchData MatchWindow(MutableString/*!*/ input, int start, bool anchored, bool freezeInput) {
            // Convert the whole input, not the part from "start" on: the conversion is the
            // expensive half of a match on a long subject, and converting only the tail means
            // paying for the tail again at every position the scanner stops at.
            string str;
            RubyEncoding kcode = null;
            bool astral;
            Regex regex = Transform(ref kcode, input, 0, out str, out astral, !anchored);

            // Under a k-coding the offsets are bytes, and "start" only indexes the converted
            // string directly when that coding spells every character in one byte - which the
            // usual one here, /n, does. Anything wider still has to be cut at "start".
            int offset = 0;
            if (kcode != null && !kcode.IsSingleByteCharacterSet) {
                kcode = null;
                regex = Transform(ref kcode, input, start, out str, out astral, !anchored);
                offset = (start < 0) ? start + input.GetByteCount() : start;
                start = 0;
            }

            if (str == null) {
                return null;
            }
            if (start < 0) {
                start += str.Length;
            }
            if (start < 0 || start > str.Length) {
                return null;
            }

            if (anchored) {
                regex = AnchorAtSearchStart(regex);
            }

            Match match = astral ? MatchOutsidePairs(regex, str, start, str.Length - start) : regex.Match(str, start, str.Length - start);
            return MatchData.Create(match, input, freezeInput, str, kcode, offset, this, astral);
        }

        /// <summary>
        /// A match MatchWindowBounds found, whose MatchData has not been made yet. Most scanning
        /// operations only want to know where the match is, and building the CLR Match with its
        /// groups and the MatchData around it costs more than finding the match does.
        /// <see cref="Complete"/> makes the MatchData MatchWindow would have made, by running the
        /// same CLR regex over the same immutable text again - so a later change to the subject
        /// does not change the groups, as it does not in MRI either.
        /// </summary>
        public struct PendingMatch {
            internal RubyRegex _owner;
            internal Regex _regex;
            internal MutableString _input;
            internal string _text;
            internal RubyEncoding _kcode;
            internal int _start;
            internal MatchData _data;

            public bool IsEmpty {
                get { return _owner == null; }
            }

            public MatchData/*!*/ Complete() {
                if (_data != null) {
                    return _data;
                }
                Match match = _regex.Match(_text, _start, _text.Length - _start);
                Debug.Assert(match.Success);
                return MatchData.Create(match, _input, false, _text, _kcode, 0, _owner, false);
            }
        }

        /// <summary>
        /// MatchWindow without a frozen copy of the input, answering the byte offsets of the whole
        /// match and a <see cref="PendingMatch"/> for its groups. Where one CLR character stands
        /// for one byte of the input - a binary or single-byte k-coding, or a subject whose
        /// characters are all single bytes - the match is found without building any match
        /// objects at all; anything else goes through MatchWindow.
        /// </summary>
        public bool MatchWindowBounds(MutableString/*!*/ input, int start, bool anchored,
            out int matchStart, out int matchEnd, out PendingMatch pending) {

            matchStart = matchEnd = 0;
            pending = default(PendingMatch);

            string str;
            RubyEncoding kcode = null;
            bool astral;
            Regex regex = Transform(ref kcode, input, 0, out str, out astral, !anchored);

            bool bytesAreChars = (kcode != null) ? kcode.IsSingleByteCharacterSet : input.DetectSingleByteCharacters();
            if (astral || !bytesAreChars) {
                MatchData data = MatchWindow(input, start, anchored, false);
                if (data == null || !data.TryGetGroupByteOffsets(0, out matchStart, out matchEnd)) {
                    return false;
                }
                pending._owner = this;
                pending._data = data;
                return true;
            }

            if (str == null) {
                return false;
            }
            if (start < 0) {
                start += str.Length;
            }
            if (start < 0 || start > str.Length) {
                return false;
            }

            if (anchored) {
                regex = AnchorAtSearchStart(regex);
            }

            // A span cut at "start" is the window Match(str, start, length) searches: \A, ^, \G
            // and lookbehind all stop at its first character.
            foreach (ValueMatch match in regex.EnumerateMatches(str.AsSpan(start))) {
                int clrStart = start + match.Index;
                // MatchData's offsets: the CLR offset is the byte offset, capped at the subject
                matchStart = Math.Min(clrStart, str.Length);
                matchEnd = Math.Min(clrStart + match.Length, str.Length);
                if (kcode == null) {
                    int byteCount = input.GetByteCount();
                    matchStart = Math.Min(matchStart, byteCount);
                    matchEnd = Math.Min(matchEnd, byteCount);
                }
                pending._owner = this;
                pending._regex = regex;
                pending._input = input;
                pending._text = str;
                pending._kcode = kcode;
                pending._start = start;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The same pattern with a \G in front of it, which in a forward CLR match stands for the
        /// position the search was told to start at. The body goes in a non-capturing group so that
        /// a top-level alternation still binds inside the anchor, and group numbers do not move.
        ///
        /// Under IgnorePatternWhitespace a trailing "#" comment would otherwise swallow the closing
        /// parenthesis, so the group is closed on a line of its own - a newline that only exists in
        /// the mode that ignores it.
        ///
        /// If the wrapped pattern will not compile for some reason the original is returned, and
        /// the caller falls back to searching: slower, never wrong.
        /// </summary>
        private Regex/*!*/ AnchorAtSearchStart(Regex/*!*/ regex) {
            Regex cached = _cachedAnchoredRegex;
            if (_cachedAnchoredSource == regex && cached != null) {
                if ((cached.Options & RegexOptions.Compiled) == 0 && ++_cachedAnchoredUses >= CompileThreshold) {
                    _cachedAnchoredRegex = cached = TierUp(cached);
                }
                return cached;
            }

            Regex result;
            try {
                string body = regex.ToString();
                string pattern = ((regex.Options & RegexOptions.IgnorePatternWhitespace) != 0)
                    ? "\\G(?:" + body + "\n)"
                    : "\\G(?:" + body + ")";
                result = new Regex(pattern, regex.Options, regex.MatchTimeout);
            } catch (Exception) {
                result = regex;
            }

            _cachedAnchoredSource = regex;
            _cachedAnchoredUses = 0;
            _cachedAnchoredRegex = result;
            return result;
        }

        public MatchData LastMatch(MutableString/*!*/ input) {
            return LastMatch(input, Int32.MaxValue);
        }

        /// <summary>
        /// Finds the last match whose index is less than or equal to "start".
        /// Captures are ordered in the same way as with forward match. This is different from .NET reverse matching.
        /// Start is a number of bytes if kcode is given, otherwise it's a number of characters.
        /// </summary>
        public MatchData LastMatch(MutableString/*!*/ input, int start) {
            string str;
            RubyEncoding kcode = null;
            bool astral;
            Regex regex = Transform(ref kcode, input, 0, out str, out astral);
            Debug.Assert(str != null);

            if (kcode != null) {
                int byteCount;
                byte[] bytes = input.GetByteArray(out byteCount);

                if (start < 0) {
                    start += byteCount;
                }

                // GetCharCount returns the number of whole characters:
                start = (start >= byteCount) ? str.Length : kcode.Encoding.GetCharCount(bytes, 0, start + 1) - 1;
            } else {
                if (start < 0) {
                    start += str.Length;
                }

                if (start > str.Length) {
                    start = str.Length;
                }
            }

            if (astral && IsInsidePair(str, start)) {
                // no match can start between the halves of a pair
                start--;
            }

            Match match;
            if (_hasGAnchor) {
                match = LastMatchWithGAnchor(regex, str, start, astral);
                if (match == null) {
                    return null;
                }
            } else {
                match = LastMatch(regex, str, start, astral);
                if (match == null) {
                    return null;
                }
            }
            return MatchData.Create(match, input, true, str, kcode, 0, this, astral);
        }

        /// <summary>
        /// A backward search in MRI tries each position from "start" down to 0, and \G stands for "start"
        /// itself rather than for the position being tried. CLR's \G is always the position the match
        /// starts at, so \G becomes a lookbehind for exactly "start" characters and a candidate position
        /// only counts when the leftmost match from it starts right there.
        /// </summary>
        private static Match LastMatchWithGAnchor(Regex/*!*/ regex, string/*!*/ input, int start, bool astral) {
            string pattern = regex.ToString();
            var sb = new StringBuilder();
            for (int i = 0; i < pattern.Length; i++) {
                char c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length) {
                    if (pattern[i + 1] == 'G') {
                        sb.Append("(?<=\\A[\\s\\S]{").Append(start).Append("})");
                    } else {
                        sb.Append(c).Append(pattern[i + 1]);
                    }
                    i++;
                } else {
                    sb.Append(c);
                }
            }

            var rewritten = new Regex(sb.ToString(), regex.Options, regex.MatchTimeout);
            for (int p = start; p >= 0; p--) {
                if (astral && IsInsidePair(input, p)) {
                    continue;
                }
                Match match = rewritten.Match(input, p);
                if (match.Success && match.Index == p) {
                    return match;
                }
            }
            return null;
        }

        /// <summary>
        /// Binary searches "str" for the last match whose index is within the range [0, start].
        /// </summary>
        private static Match LastMatch(Regex/*!*/ regex, string/*!*/ input, int start, bool astral) {
            Match result = null;
            int s = 0;
            int e = start;

            while (s <= e) {
                int m = (s + e) / 2;
                Match match = astral ? MatchOutsidePairs(regex, input, m) : regex.Match(input, m);
                if (match.Success && match.Index <= e) {
                    result = match;
                    s = match.Index + 1;
                } else {
                    e = m - 1;
                }
            }

            return result;
        }

        /// <summary>
        /// Returns a collection of fresh MatchData objects.
        /// </summary>
        public IList<MatchData>/*!*/ Matches(MutableString/*!*/ input, bool inputMayMutate) {
            string str;
            RubyEncoding kcode = null;
            bool astral;
            Regex regex = Transform(ref kcode, input, 0, out str, out astral);
            IList<Match> matches;
            if (astral) {
                matches = MatchesOverPairs(regex, str);
            } else {
                // Match and NextMatch are what Regex.Matches is defined as; asking for the first
                // one directly answers "no match" - the usual outcome of a #gsub over a short
                // string - without the collection.
                Match match = regex.Match(str);
                if (!match.Success) {
                    return Array.Empty<MatchData>();
                }
                var list = new List<Match>();
                do {
                    list.Add(match);
                    match = match.NextMatch();
                } while (match.Success);
                matches = list;
            }

            var result = new MatchData[matches.Count];
            if (result.Length > 0 && inputMayMutate) {
                // clone and freeze the string once so that it can be shared by all the MatchData objects
                input = input.Clone().Freeze();
            }

            for (int i = 0; i < result.Length; i++) {
                result[i] = MatchData.Create(matches[i], input, false, str, kcode, 0, this, astral);
            }

            return result;
        }

        /// <summary>
        /// Regex.Matches, except that the search after an empty match resumes past the whole
        /// character it stopped at: .NET moves on by one UTF-16 unit, which inside a surrogate
        /// pair would find an empty match between its halves ("\u{1F600}".scan(/x*/) would be
        /// three matches, not two).
        /// </summary>
        private static List<Match>/*!*/ MatchesOverPairs(Regex/*!*/ regex, string/*!*/ str) {
            var result = new List<Match>();
            Match match = MatchOutsidePairs(regex, str, 0);
            while (match.Success) {
                result.Add(match);
                int next = match.Index + match.Length;
                if (match.Length == 0) {
                    if (next >= str.Length) {
                        break;
                    }
                    next = MutableString.NextCharacterClrIndex(str, next);
                }
                match = MatchOutsidePairs(regex, str, next);
            }
            return result;
        }

        public IList<MatchData>/*!*/ Matches(MutableString/*!*/ input) {
            return Matches(input, true);
        }

        public MutableString[]/*!*/ Split(MutableString/*!*/ input) {
            string str;
            RubyEncoding kcode = null;
            return MutableString.MakeArray(Transform(ref kcode, input, 0, out str).Split(str), kcode ?? input.Encoding);
        }
        
        public MutableString[]/*!*/ Split(MutableString/*!*/ input, int count) {
            string str;
            RubyEncoding kcode = null;
            return MutableString.MakeArray(Transform(ref kcode, input, 0, out str).Split(str, count), kcode ?? input.Encoding);
        }

        public static MatchData SetCurrentMatchData(RubyScope/*!*/ scope, RubyRegex/*!*/ regex, MutableString str) {
            if (str != null) {
                // MRI's rb_reg_search checks the regexp (rb_reg_check), a nil subject does not
                if (!regex.IsInitialized) {
                    throw RubyExceptions.CreateTypeError("uninitialized Regexp");
                }
                regex.WarnHistoricalBinaryMatch(scope.RubyContext, str);
            }
            return scope.GetInnerMostClosureScope().CurrentMatch = (str != null) ? regex.Match(str) : null;
        }

        /// <summary>
        /// MRI's rb_reg_prepare_enc: a /n regexp that has not pinned an encoding still matches a
        /// non-ASCII string of another encoding, byte by byte, and warns that it does.
        /// </summary>
        public void WarnHistoricalBinaryMatch(RubyContext/*!*/ context, MutableString/*!*/ str) {
            if ((_options & RubyRegexOptions.EncodingMask) == RubyRegexOptions.FIXED && !IsFixedEncoding &&
                str.Encoding != RubyEncoding.Binary && !str.IsAscii()) {
                context.ReportWarning(String.Format("historical binary regexp match /.../n against {0} string", str.Encoding.Name));
            }
        }

        #endregion               

        #region ToString, Inspect

        public override string/*!*/ ToString() {
            return ToMutableString().ToString();
        }

        public MutableString/*!*/ ToMutableString() {
            return AppendTo(MutableString.CreateMutable(RubyEncoding.Binary));
        }

        public MutableString/*!*/ Inspect() {
            MutableString result = MutableString.CreateMutable(RubyEncoding.Binary);
            result.Append('/');
            AppendEscapeForwardSlash(result, _pattern);
            result.Append('/');
            AppendOptionString(result, true);
            if ((_options & RubyRegexOptions.FIXED) != 0) {
                result.Append('n');
            }
            return result;
        }

        public MutableString/*!*/ AppendTo(MutableString/*!*/ result) {
            Assert.NotNull(result);

            // MRI hoists the options of a group that spans the whole pattern into the (?...) it
            // writes around it, and keeps doing so: /(?i:.)/ is "(?i-mx:.)", not "(?-mix:(?i:.))".
            MutableString pattern = _pattern;
            RubyRegexOptions options = Options;
            while (TryHoistWholePatternGroup(ref pattern, ref options)) {
            }

            result.Append("(?");
            if (AppendOptionString(result, options, true) < 3) {
                result.Append('-');
            }
            AppendOptionString(result, options, false);
            result.Append(':');
            AppendEscapeForwardSlash(result, pattern);
            result.Append(')');
            return result;
        }

        /// <summary>
        /// A pattern that is exactly one "(?opts:...)" group is the same regexp as its body under
        /// those options, and MRI writes it that way. The group has to span the whole pattern -
        /// "(?ix:foo)bar" and "(?ix:foo)(?m:bar)" both end in ')' without being one group - and it
        /// has to be an option group rather than a lookahead or a named one.
        /// </summary>
        private static bool TryHoistWholePatternGroup(ref MutableString/*!*/ pattern, ref RubyRegexOptions options) {
            int length = pattern.GetCharCount();
            if (length < 4 || pattern.GetChar(0) != '(' || pattern.GetChar(1) != '?') {
                return false;
            }
            if (FindGroupEnd(pattern, length) != length - 1) {
                return false;
            }

            RubyRegexOptions hoisted = options;
            int i = 2;
            while (i < length) {
                char c = pattern.GetChar(i);
                if (c == 'm') {
                    hoisted |= RubyRegexOptions.Multiline;
                } else if (c == 'i') {
                    hoisted |= RubyRegexOptions.IgnoreCase;
                } else if (c == 'x') {
                    hoisted |= RubyRegexOptions.Extended;
                } else {
                    break;
                }
                i++;
            }

            if (i < length && pattern.GetChar(i) == '-') {
                i++;
                while (i < length) {
                    char c = pattern.GetChar(i);
                    if (c == 'm') {
                        hoisted &= ~RubyRegexOptions.Multiline;
                    } else if (c == 'i') {
                        hoisted &= ~RubyRegexOptions.IgnoreCase;
                    } else if (c == 'x') {
                        hoisted &= ~RubyRegexOptions.Extended;
                    } else {
                        break;
                    }
                    i++;
                }
            }

            if (i >= length - 1 || pattern.GetChar(i) != ':') {
                return false;
            }

            options = hoisted;
            pattern = pattern.GetSlice(i + 1, length - i - 2);
            return true;
        }

        /// <summary>
        /// The index of the ')' closing the group the pattern opens with, or -1. A '\' escapes
        /// whatever follows it and parentheses inside a character class are literal.
        /// </summary>
        private static int FindGroupEnd(MutableString/*!*/ pattern, int length) {
            int depth = 0;
            bool inCharacterClass = false;

            for (int i = 0; i < length; i++) {
                char c = pattern.GetChar(i);
                if (c == '\\') {
                    i++;
                } else if (inCharacterClass) {
                    if (c == ']') {
                        inCharacterClass = false;
                    }
                } else if (c == '[') {
                    inCharacterClass = true;
                } else if (c == '(') {
                    depth++;
                } else if (c == ')') {
                    if (--depth == 0) {
                        return i;
                    }
                }
            }

            return -1;
        }

        private int AppendOptionString(MutableString/*!*/ result, bool enabled) {
            return AppendOptionString(result, Options, enabled);
        }

        private static int AppendOptionString(MutableString/*!*/ result, RubyRegexOptions options, bool enabled) {
            int count = 0;

            if (((options & RubyRegexOptions.Multiline) != 0) == enabled) {
                result.Append('m');
                count++;
            }

            if (((options & RubyRegexOptions.IgnoreCase) != 0) == enabled) {
                result.Append('i');
                count++;
            }

            if (((options & RubyRegexOptions.Extended) != 0) == enabled) {
                result.Append('x');
                count++;
            }

            return count;
        }

        private static int SkipToUnescapedForwardSlash(MutableString/*!*/ pattern, int patternLength, int i) {
            while (i < patternLength) {
                i = pattern.IndexOf('/', i);
                if (i <= 0) {
                    return i;
                }

                // An odd number of backslashes escapes the slash; an even number (\\/) does not,
                // so that pattern doesn't get a third backslash added to it.
                int backslashes = 0;
                for (int j = i - 1; j >= 0 && pattern.GetChar(j) == '\\'; j--) {
                    backslashes++;
                }

                if (backslashes % 2 == 0) {
                    return i;
                }

                i++;
            }
            return -1;
        }

        private static MutableString/*!*/ AppendEscapeForwardSlash(MutableString/*!*/ result, MutableString/*!*/ pattern) {
            int first = 0;
            int patternLength = pattern.GetCharCount();
            int i = SkipToUnescapedForwardSlash(pattern, patternLength, 0);
            while (i >= 0) {
                Debug.Assert(i < patternLength);
                Debug.Assert(pattern.GetChar(i) == '/');

                result.Append(pattern, first, i - first);
                result.Append('\\');
                first = i; // include forward slash in the next append
                i = SkipToUnescapedForwardSlash(pattern, patternLength, i + 1);
            }

            result.Append(pattern, first, patternLength - first);
            return result;
        }

        #endregion

        #region Escape

        private const int EndOfPattern = -1;

        /// <summary>
        /// Returns a new instance of MutableString that contains escaped content of the given string.
        /// </summary>
        public static MutableString/*!*/ Escape(MutableString/*!*/ str) {
            return str.EscapeRegularExpression();
        }

        private static int SkipNonSpecial(string/*!*/ pattern, int i, out char escaped) {
            while (i < pattern.Length) {
                char c = pattern[i];
                switch (c) {
                    case '$':
                    case '^':
                    case '|':
                    case '[':
                    case ']':
                    case '(':
                    case ')':
                    case '\\':
                    case '.':
                    case '#':
                    case '-':

                    case '{':
                    case '}':
                    case '*':
                    case '+':
                    case '?':
                    case ' ':
                        escaped = c;
                        return i;

                    case '\t':
                        escaped = 't';
                        return i;

                    case '\n':
                        escaped = 'n';
                        return i;

                    case '\r':
                        escaped = 'r';
                        return i;

                    case '\f':
                        escaped = 'f';
                        return i;

                    case '\v':
                        escaped = 'v';
                        return i;
                }
                i++;
            }

            escaped = '\0';
            return EndOfPattern;
        }

        internal static string/*!*/ Escape(string/*!*/ pattern) {
            StringBuilder sb = EscapeToStringBuilder(pattern);
            return (sb != null) ? sb.ToString() : pattern;
        }

        internal static StringBuilder EscapeToStringBuilder(string/*!*/ pattern) {
            int first = 0;
            char escaped;
            int i = SkipNonSpecial(pattern, 0, out escaped);

            if (i == EndOfPattern) {
                return null;
            }

            StringBuilder result = new StringBuilder(pattern.Length + 1);

            do {
                Debug.Assert(i < pattern.Length);
                // pattern[i] needs escape

                result.Append(pattern, first, i - first);
                result.Append('\\');
                result.Append(escaped);
                i++;

                Debug.Assert(i <= pattern.Length);

                first = i;
                i = SkipNonSpecial(pattern, i, out escaped);
            } while (i >= 0);

            result.Append(pattern, first, pattern.Length - first);
            return result;
        }

        #endregion
    }
}

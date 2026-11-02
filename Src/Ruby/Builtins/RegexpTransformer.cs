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
    /// <summary>
    /// Converts a Ruby regexp pattern to a CLR pattern
    /// </summary>
    internal sealed class RegexpTransformer {
        private readonly string/*!*/ _rubyPattern;
        private int _index;
        private StringBuilder/*!*/ _sb;
        private bool _hasGAnchor;

        // Capturing groups opened so far. Ruby resolves relative references such as \k<-1> and
        // (?(<-1>)...) against this, and reads \N as a backreference only while N is within it.
        private int _groupCount;

        // True when the pattern declares a named group anywhere. Ruby then rejects every numbered
        // backreference in the pattern, including one written before the named group.
        private readonly bool _hasNamedGroup;

        // Nesting depth of groups and of the absent operator's sub-buffer. \K rewrites everything
        // emitted so far as a lookbehind, which is only meaningful at the top level.
        private int _groupDepth;
        private int _absentDepth;

        /// <summary>
        /// Which repertoire \w \d \s and the POSIX bracket classes range over, selected by the
        /// (?a) (?d) (?u) inline modifiers. .NET has no equivalent flag, so the expansions have to
        /// differ instead. Ruby's default is (?d): \w and friends are ASCII only while the POSIX
        /// classes are Unicode aware.
        /// </summary>
        private enum CharacterClassMode {
            Default,
            Ascii,
            Unicode,
        }

        private CharacterClassMode _characterClassMode;

        // Whether '.' matches a newline here - (?m) in Ruby, RegexOptions.Singleline in .NET.
        // Tracked only to translate '.' for an astral subject; null in a copied group, whose
        // text lands wherever the copy is emitted and so cannot know.
        private bool? _dotAll;

        // Whether (?x) is in effect: whitespace and # comments are not part of the pattern.  .NET's
        // IgnorePatternWhitespace drops them too, but this parser numbers groups and copies group
        // bodies for \g<n> itself, so a "(" inside a comment counted as a group - ActiveRecord's
        // SQLite order matcher has "# ... function(one or no argument)" in it - and \g<2> then
        // called the wrong group.
        private bool _extended;

        /// <summary>
        /// A capturing group as a subexpression call or a loop copy needs it: its Ruby body and
        /// the parser state at the start of that body, so that the body can be transformed again
        /// with its nested groups getting the numbers and names they have in the original.
        /// </summary>
        private sealed class GroupDefinition {
            public string/*!*/ Source;
            public int Number;
            public string Name;
            public int Occurrence;
            public Dictionary<string, int> OccurrencesBefore;
            public CharacterClassMode Mode;
        }

        /// <summary>
        /// State shared by a transformer and every sub-transformer it spawns to copy a group.
        /// </summary>
        private sealed class SharedState {
            // Every capturing group, recorded in a prescan so that a call may precede - or sit
            // inside - the group it calls.
            public readonly Dictionary<int, GroupDefinition>/*!*/ ByNumber = new Dictionary<int, GroupDefinition>();
            public readonly Dictionary<string, GroupDefinition>/*!*/ ByName = new Dictionary<string, GroupDefinition>();

            // Keys ("name" or "#number") of the groups whose body is being emitted - parsed in
            // place or copied for a call. A call to one of them is recursion; a backreference to
            // one of them never matches, as in Onigmo.
            public readonly List<string>/*!*/ OpenGroups = new List<string>();

            // Groups some \k<name+level> refers to, and the hidden per-level groups made for them.
            public readonly HashSet<string>/*!*/ LevelReferenced = new HashSet<string>();
            public readonly Dictionary<string, int>/*!*/ LevelGroups = new Dictionary<string, int>();
            public readonly HashSet<int>/*!*/ DefinedLevelGroups = new HashSet<int>();

            public string RootPattern;
            public bool Collecting;
            public int HiddenGroupCount;
            public RegexOptions ClrOptions = RegexOptions.Multiline | RegexOptions.CultureInvariant;
            public int MaxCallDepth = DefaultMaxCallDepth;
            public int Emitted;
            // see Transform(..., astralSafe)
            public bool AstralSafe;
        }

        private sealed class ExpansionTooLargeException : Exception {
        }

        // .NET's Regex cannot recurse, so a recursive subexpression call is expanded inline this
        // many levels deep; deeper nesting fails to match. When the expansion grows too large
        // (a group calling itself more than once per level grows exponentially) the depth is
        // reduced until it fits.
        private const int DefaultMaxCallDepth = 50;
        private const int ExpansionBudget = 200000;

        // Hidden groups emitted for level-scoped backreferences are numbered from here, above any
        // group Ruby numbers, so MatchData can leave them out (see IsHiddenGroupNumber).
        internal const int HiddenGroupBase = 100000;

        private SharedState/*!*/ _state;
        private RegexOptions _clrOptions = RegexOptions.Multiline | RegexOptions.CultureInvariant;

        // Set on a sub-transformer that copies a group - for a \g<...> call or to unroll a loop:
        // the copy's groups are emitted under the numbers and names of the groups they copy.
        private bool _copyGroups;

        // How many subexpression calls deep the text being emitted is.
        private int _callDepth;

        // Backreferences and conditionals emitted so far, and empty capturing groups: a loop
        // whose body contains either can behave differently after an empty iteration (see
        // RewriteLoop).
        private int _backrefCount;
        private int _emptyCaptureCount;

        /// <summary>A quantified group, as RewriteLoop needs it.</summary>
        private sealed class LoopBody {
            public string Source;
            public int GroupCountBefore;
            public int GroupCountAfter;
            public int BackrefCountBefore;
            public int EmptyCaptureCountBefore;
            public Dictionary<string, int> OccurrencesBefore;
            public CharacterClassMode Mode;
        }

        // Onigmo's warnings about a quantifier applied to a quantifier (regparse.c set_quantifier).
        private List<string> _warnings;

        // The last quantifier as one of Onigmo's "popular" ones - ? * + ?? *? +? - or null.
        private string _lastQuantifierKind;

        private static readonly string[] PopularQuantifiers = { "?", "*", "+", "??", "*?", "+?" };

        // ReduceTypeTable[inner, outer]: 0 as is, 1 redundant, otherwise the index into ReducedQuantifiers
        private static readonly int[,] ReduceTypeTable = {
            /* '?' '*' '+' '??' '*?' '+?'   outer / inner */
            { 1, 2, 2, 4, 3, 0 },  /* '?'  */
            { 1, 1, 1, 5, 5, 1 },  /* '*'  */
            { 2, 2, 1, 0, 5, 1 },  /* '+'  */
            { 1, 3, 3, 1, 3, 3 },  /* '??' */
            { 1, 1, 1, 1, 1, 1 },  /* '*?' */
            { 0, 6, 1, 3, 3, 1 },  /* '+?' */
        };

        private static readonly string[] ReducedQuantifiers = { "", "", "*", "*?", "??", "+ and ??", "+? and ?" };

        private void CheckNestedQuantifier(string/*!*/ inner, string/*!*/ outer) {
            int i = Array.IndexOf(PopularQuantifiers, inner);
            int o = Array.IndexOf(PopularQuantifiers, outer);
            if (i < 0 || o < 0) {
                return;
            }

            int reduction = ReduceTypeTable[i, o];
            if (reduction == 0) {
                return;
            }

            if (_warnings == null) {
                _warnings = new List<string>();
            }
            _warnings.Add(reduction == 1
                ? "regular expression has redundant nested repeat operator '" + inner + "'"
                : "nested repeat operator '" + inner + "' and '" + outer + "' was replaced with '" + ReducedQuantifiers[reduction] + "' in regular expression"
            );
        }

        /// <summary>
        /// The warnings Onigmo gives while compiling the pattern, without the ": /pattern/" MRI
        /// appends. Null if there are none or the pattern does not compile.
        /// </summary>
        internal static List<string> GetWarnings(string/*!*/ rubyPattern) {
            var transformer = new RegexpTransformer(rubyPattern);
            try {
                transformer.Transform();
            } catch (Exception) {
                return null;
            }
            return transformer._warnings;
        }

        internal static string Transform(string/*!*/ rubyPattern, RubyRegexOptions options, out bool hasGAnchor) {
            return Transform(rubyPattern, options, out hasGAnchor, false);
        }

        /// <summary>
        /// With <paramref name="astralSafe"/> the translation is for a subject that holds
        /// surrogate pairs: '.' matches a pair as a whole and never half of one, and \B does not
        /// match between the halves. Without it '.' stays .NET's own, which is all a subject
        /// with no pairs needs and is what keeps the common case as fast as it was.
        /// </summary>
        internal static string Transform(string/*!*/ rubyPattern, RubyRegexOptions options, out bool hasGAnchor, bool astralSafe) {
            // TODO: surrogates (REXML uses this pattern)
            if (rubyPattern == "^[\t\n\r -\uD7FF\uE000-\uFFFD\uD800\uDC00-\uDBFF\uDFFF]*$") {
                hasGAnchor = false;
                return "^(?:[\t\n\r -\uD7FF\uE000-\uFFFD]|[\uD800-\uDBFF][\uDC00-\uDFFF])*$";
            }
            
            RegexpTransformer transformer = new RegexpTransformer(rubyPattern);
            transformer._clrOptions = RubyRegex.ToClrOptions(options);
            transformer._state.AstralSafe = astralSafe;
            var result = transformer.Transform();
            hasGAnchor = transformer._hasGAnchor;
            return result;
        }

        private RegexpTransformer(string/*!*/ rubyPattern) {
            _rubyPattern = rubyPattern;
            _groupNameCounts = CountGroupNames(rubyPattern);
            _hasNamedGroup = _groupNameCounts.Count > 0;
            _state = new SharedState();
        }

        /// <summary>
        /// A transformer for <paramref name="source"/>, the body of a group of the pattern
        /// <paramref name="parent"/> is transforming, that emits the body's groups under the
        /// numbers and names they have in the whole pattern.
        /// </summary>
        private RegexpTransformer(RegexpTransformer/*!*/ parent, string/*!*/ source, int groupCount,
            Dictionary<string, int> occurrences, CharacterClassMode mode, int callDepth) {
            _rubyPattern = source;
            _groupNameCounts = parent._groupNameCounts;
            _hasNamedGroup = parent._hasNamedGroup;
            _state = parent._state;
            _copyGroups = true;
            _groupCount = groupCount;
            if (occurrences != null) {
                _groupNameOccurrences = new Dictionary<string, int>(occurrences);
            }
            _characterClassMode = mode;
            _callDepth = callDepth;
            _extended = parent._extended;
            // a copy is never the top level of the pattern (\K)
            _groupDepth = 1;
        }

        private string/*!*/ TransformCopy(RegexpTransformer/*!*/ copy) {
            string result = copy.Transform();
            _hasGAnchor |= copy._hasGAnchor;
            _backrefCount += copy._backrefCount;
            _state.Emitted += result.Length;
            if (_state.Emitted > ExpansionBudget) {
                throw new ExpansionTooLargeException();
            }
            return result;
        }

        internal static bool IsHiddenGroupNumber(int number) {
            return number >= HiddenGroupBase;
        }

        private static string/*!*/ GroupKey(int number, string name) {
            return name ?? "#" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void OpenGroup(string/*!*/ key) {
            _state.OpenGroups.Add(key);
        }

        private void CloseGroup(string/*!*/ key) {
            _state.OpenGroups.RemoveAt(_state.OpenGroups.LastIndexOf(key));
        }

        private bool IsOpenGroup(string/*!*/ key) {
            return _state.OpenGroups.Contains(key);
        }

        /// <summary>
        /// The number of the hidden group that records what group <paramref name="key"/> captures
        /// at subexpression call level <paramref name="level"/>.
        /// </summary>
        private int GetLevelGroup(string/*!*/ key, int level) {
            string id = key + "@" + level.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int number;
            if (!_state.LevelGroups.TryGetValue(id, out number)) {
                number = AllocateHiddenGroup();
                _state.LevelGroups.Add(id, number);
            }
            return number;
        }

        private int AllocateHiddenGroup() {
            return HiddenGroupBase + _state.HiddenGroupCount++;
        }

        /// <summary>
        /// Opens the hidden per-level group of a capturing group when a level-scoped
        /// backreference refers to it. Returns whether one was opened.
        /// </summary>
        private bool OpenLevelGroup(string/*!*/ key) {
            if (_state.Collecting || !_state.LevelReferenced.Contains(key)) {
                return false;
            }
            int number = GetLevelGroup(key, _callDepth);
            _state.DefinedLevelGroups.Add(number);
            _sb.Append("(?<").Append(number).Append('>');
            return true;
        }

        // How many groups the pattern declares under each name. Ruby lets several groups share a
        // name and numbers them all; .NET would merge them into one group, so every occurrence
        // after the first is given a name of its own (see DuplicateGroupName).
        private readonly Dictionary<string, int>/*!*/ _groupNameCounts;
        private readonly Dictionary<string, int>/*!*/ _groupNameOccurrences = new Dictionary<string, int>();

        private const string DuplicateGroupNameSeparator = "__ir";

        // A name .NET would not accept - Ruby allows `(?<a+>...)' for a group only ever called
        // with \g - is spelled with its other characters as hex codes behind this prefix.
        private const string EncodedGroupNamePrefix = "__irn";

        private static string/*!*/ DuplicateGroupName(string/*!*/ name, int occurrence) {
            string clrName = IsClrGroupName(name) ? name : EncodeGroupName(name);
            return occurrence <= 1 ? clrName : clrName + DuplicateGroupNameSeparator + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool IsClrGroupName(string/*!*/ name) {
            if (name.Length == 0 || !(Char.IsLetter(name[0]) || name[0] == '_')) {
                return false;
            }
            foreach (char c in name) {
                if (!Char.IsLetterOrDigit(c) && c != '_') {
                    return false;
                }
            }
            return true;
        }

        private static string/*!*/ EncodeGroupName(string/*!*/ name) {
            var result = new StringBuilder(EncodedGroupNamePrefix);
            foreach (char c in name) {
                if (Char.IsLetterOrDigit(c)) {
                    result.Append(c);
                } else {
                    result.Append('_').Append(((int)c).ToString("x4"));
                }
            }
            return result.ToString();
        }

        private static string/*!*/ DecodeGroupName(string/*!*/ clrName) {
            if (!clrName.StartsWith(EncodedGroupNamePrefix, StringComparison.Ordinal)) {
                return clrName;
            }
            var result = new StringBuilder();
            for (int i = EncodedGroupNamePrefix.Length; i < clrName.Length; i++) {
                if (clrName[i] == '_' && i + 4 < clrName.Length) {
                    result.Append((char)Convert.ToInt32(clrName.Substring(i + 1, 4), 16));
                    i += 4;
                } else {
                    result.Append(clrName[i]);
                }
            }
            return result.ToString();
        }

        /// <summary>
        /// The Ruby name of a .NET group: the name the pattern gave it, whichever occurrence of
        /// the name it is. Null for a group .NET only knows by number.
        /// </summary>
        internal static string GetRubyGroupName(string/*!*/ clrName) {
            if (clrName.Length == 0 || Char.IsDigit(clrName[0])) {
                return null;
            }
            int separator = clrName.LastIndexOf(DuplicateGroupNameSeparator, StringComparison.Ordinal);
            if (separator > 0 && separator + DuplicateGroupNameSeparator.Length < clrName.Length) {
                for (int i = separator + DuplicateGroupNameSeparator.Length; i < clrName.Length; i++) {
                    if (!Char.IsDigit(clrName[i])) {
                        return clrName;
                    }
                }
                return DecodeGroupName(clrName.Substring(0, separator));
            }
            return DecodeGroupName(clrName);
        }

        private static Dictionary<string, int>/*!*/ CountGroupNames(string/*!*/ pattern) {
            var result = new Dictionary<string, int>();
            bool inCharacterClass = false;
            for (int i = 0; i < pattern.Length; i++) {
                char c = pattern[i];
                if (c == '\\') {
                    i++;
                } else if (inCharacterClass) {
                    inCharacterClass = c != ']';
                } else if (c == '[') {
                    inCharacterClass = true;
                } else if (c == '(' && i + 2 < pattern.Length && pattern[i + 1] == '?') {
                    char d = pattern[i + 2];
                    char terminator;
                    if (d == '\'') {
                        terminator = '\'';
                    } else if (d == '<' && i + 3 < pattern.Length && pattern[i + 3] != '=' && pattern[i + 3] != '!') {
                        terminator = '>';
                    } else {
                        continue;
                    }
                    int end = pattern.IndexOf(terminator, i + 3);
                    if (end > i + 3) {
                        string name = pattern.Substring(i + 3, end - i - 3);
                        int count;
                        result.TryGetValue(name, out count);
                        result[name] = count + 1;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Whether the pattern opens a named group anywhere. Has to be known up front: Ruby
        /// rejects (a)(?&lt;a&gt;a)\1 as well as (?&lt;a&gt;a)\1, so the decision cannot wait
        /// until the named group is reached.
        /// </summary>
        private static bool HasNamedGroup(string/*!*/ pattern) {
            bool inCharacterClass = false;
            for (int i = 0; i < pattern.Length; i++) {
                char c = pattern[i];
                if (c == '\\') {
                    i++;
                } else if (inCharacterClass) {
                    inCharacterClass = c != ']';
                } else if (c == '[') {
                    inCharacterClass = true;
                } else if (c == '(' && i + 2 < pattern.Length && pattern[i + 1] == '?') {
                    char d = pattern[i + 2];
                    if (d == '\'') {
                        return true;
                    }
                    // (?<= and (?<! are lookbehind, not a named group
                    if (d == '<' && i + 3 < pattern.Length && pattern[i + 3] != '=' && pattern[i + 3] != '!') {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// A group name that carries a level specifier - anything with a '+' or a '-' in it - can
        /// be declared but not referenced; Ruby raises when one is used in \k&lt;&gt; or in a
        /// conditional. .NET rejects such names outright, so this only has to produce the error.
        /// </summary>
        private static bool IsLevelSpecifier(string/*!*/ name) {
            return name.IndexOf('+') >= 0 || name.IndexOf('-') >= 0;
        }

        /// <summary>
        /// Parses <paramref name="text"/> as Ruby writes a group number in a reference: an
        /// optional '-' for a relative index, then decimal digits which may have leading zeros.
        /// Returns the absolute group number, or -1 when the text is not a number at all.
        /// </summary>
        private int ResolveGroupNumber(string/*!*/ text) {
            if (text.Length == 0) {
                return -1;
            }
            bool relative = text[0] == '-';
            int i = relative ? 1 : 0;
            if (i == text.Length) {
                return -1;
            }

            int value = 0;
            for (; i < text.Length; i++) {
                if (!Tokenizer.IsDecimalDigit(text[i])) {
                    return -1;
                }
                value = value * 10 + (text[i] - '0');
            }

            if (relative) {
                value = _groupCount + 1 - value;
            }
            if (value <= 0) {
                throw MakeError("invalid group name <" + text + ">");
            }
            return value;
        }

        #region Buffer Ops

        private int Peek() {
            return _index < _rubyPattern.Length ? _rubyPattern[_index] : -1;
        }

        private int Peek(int disp) {
            int i = _index + disp;
            return i < _rubyPattern.Length ? _rubyPattern[i] : -1;
        }

        private int Read() {
            return _index < _rubyPattern.Length ? _rubyPattern[_index++] : -1;
        }

        private void Back() {
            _index--;
            Debug.Assert(_index >= 0);
        }
        
        private void Skip(int n) {
            _index += n;
            Debug.Assert(_index <= _rubyPattern.Length);
        }

        private void Skip() {
            Skip(1);
        }

        private void Skip(char c) {
            Debug.Assert(Peek() == c);
            Skip();
        }

        private bool Read(int c) {
            if (Peek() == c) {
                Skip();
                return true;
            } else {
                return false;
            }
        }

        private void Append(char c) {
            _sb.Append(c);
        }

        private void AppendEscaped(int c) {
            AppendEscaped(_sb, c);
        }

        private static StringBuilder/*!*/ AppendEscaped(StringBuilder/*!*/ builder, string/*!*/ str) {
            for (int i = 0; i < str.Length; i++) {
                AppendEscaped(builder, str[i]);
            }
            return builder;
        }

        private static StringBuilder/*!*/ AppendEscaped(StringBuilder/*!*/ builder, int c) {
            if (IsMetaCharacter(c)) {
                builder.Append('\\');
            }
            builder.Append((char)c);
            return builder;
        }

        private static string/*!*/ Escape(int c) {
            return IsMetaCharacter(c) ? "\\" + (char)c : ((char)c).ToString();
        }

        private static bool IsMetaCharacter(int c) {
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
                case '\n':
                case '\r':
                case '\t':
                case ' ':
                    return true;
            }

            return false;
        }

        #endregion

        private Exception/*!*/ MakeError(string/*!*/ message) {
            // MRI quotes the offending pattern as a regexp literal, options included:
            // "invalid hex escape: /\xn/", "invalid POSIX bracket type: /[[:foo:]]/mix".
            var options = _clrOptions;
            return new RegexpError(message + ": /" + _rubyPattern + "/" +
                ((options & RegexOptions.Singleline) != 0 ? "m" : "") +
                ((options & RegexOptions.IgnoreCase) != 0 ? "i" : "") +
                ((options & RegexOptions.IgnorePatternWhitespace) != 0 ? "x" : ""));
        }

        private string/*!*/ Transform() {
            if (_copyGroups) {
                return TransformBody();
            }

            // A call may precede the group it calls, and a level-scoped backreference decides how
            // the group it refers to is emitted, so both need the whole pattern scanned first.
            bool prescan = _rubyPattern.IndexOf("\\g", StringComparison.Ordinal) >= 0
                || _rubyPattern.IndexOf("\\k", StringComparison.Ordinal) >= 0;

            for (int depth = DefaultMaxCallDepth; ; depth /= 2) {
                bool astralSafe = _state.AstralSafe;
                _state = new SharedState();
                _state.AstralSafe = astralSafe;
                _state.MaxCallDepth = depth;
                _state.ClrOptions = _clrOptions;
                _state.RootPattern = _rubyPattern;
                if (prescan) {
                    var collector = new RegexpTransformer(_rubyPattern);
                    collector._state = _state;
                    collector._extended = (_clrOptions & RegexOptions.IgnorePatternWhitespace) != 0;
                    _state.Collecting = true;
                    collector.TransformBody();
                    _state.Collecting = false;
                    _state.OpenGroups.Clear();
                }

                _index = 0;
                _groupCount = 0;
                _groupDepth = 0;
                _absentDepth = 0;
                _hasGAnchor = false;
                _warnings = null;
                _backrefCount = 0;
                _characterClassMode = CharacterClassMode.Default;
                _dotAll = (_clrOptions & RegexOptions.Singleline) != 0;
                _extended = (_clrOptions & RegexOptions.IgnorePatternWhitespace) != 0;
                _groupNameOccurrences.Clear();
                try {
                    string result = TransformBody();
                    // A level-scoped backreference may name a level no group is ever emitted at;
                    // it never matches, but .NET requires the group to exist.
                    StringBuilder undefined = null;
                    foreach (int number in _state.LevelGroups.Values) {
                        if (!_state.DefinedLevelGroups.Contains(number)) {
                            if (undefined == null) {
                                undefined = new StringBuilder(result).Append("(?:(?!)");
                            }
                            undefined.Append("(?<").Append(number).Append(">)");
                        }
                    }
                    return undefined != null ? undefined.Append(")?").ToString() : result;
                } catch (ExpansionTooLargeException) {
                    if (depth <= 1) {
                        throw MakeError("too big regular expression");
                    }
                }
            }
        }

        private string/*!*/ TransformBody() {
            _sb = new StringBuilder(_rubyPattern.Length);
            Parse(false);
            var result = _sb.ToString();
            _sb = null;
            return result;
        }

        private void Parse(bool isSubexpression) {
            int lastEntityIndex = 0;
            // The group the next quantifier would apply to, when its loop needs RewriteLoop.
            LoopBody lastEntity = null;
            // Ruby allows a quantifier to be quantified again (a***, a+?*); .NET rejects that as a
            // nested quantifier, so the whole quantified entity has to be wrapped first.
            bool lastWasQuantifier = false;
            int c;
            while (true) {
                // set by AppendCharacterSet for the token right after it only
                bool loopGuard = _loopGuardPending;
                _loopGuardPending = false;
                switch (c = Read()) {
                    case -1:
                        if (isSubexpression) {
                            throw MakeError("end pattern in group");
                        }
                        return;
                    
                    case '\\':
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        ParseEscape();
                        break;

                    case '#':
                        if (!_extended) {
                            goto default;
                        }
                        // A comment runs to the end of the line; nothing of it reaches .NET.
                        while ((c = Peek()) != -1 && c != '\n') {
                            Skip();
                        }
                        break;

                    case '?':
                    case '*':
                    case '+': {
                        string inner = lastWasQuantifier ? _lastQuantifierKind : null;
                        if (c != '?' && !lastWasQuantifier && Peek() != '+' &&
                            RewriteLoop(lastEntity, lastEntityIndex, c == '+' ? 1 : 0, -1, Peek() == '?')) {
                            _lastQuantifierKind = ((char)c).ToString() + (Peek() == '?' ? "?" : "");
                            Read('?');
                            lastWasQuantifier = true;
                            break;
                        }
                        if (lastWasQuantifier) {
                            // a*** == (?:(?:a*)*)*
                            _sb.Insert(lastEntityIndex, "(?:");
                            Append(')');
                        }
                        Append((char)c);
                        int next = Peek();
                        // `?+ *+ ++` are possessive, not a quantifier applied to a quantifier
                        string kind = (next == '+') ? null : ((char)c).ToString() + (next == '?' ? "?" : "");
                        if (inner != null && kind != null) {
                            CheckNestedQuantifier(inner, kind);
                        }
                        _lastQuantifierKind = kind;
                        // A quantifier that got wrapped is already a group, so a further
                        // quantifier can apply to it directly.
                        lastWasQuantifier = !ParsePostQuantifier(lastEntityIndex, true, false);
                        if (loopGuard) {
                            _sb.Append(CharacterSet.LoopEndGuard);
                        }
                        break;
                    }

                    case '{': {
                        bool isExactCount;
                        string inner = lastWasQuantifier ? _lastQuantifierKind : null;
                        int start = _index;
                        int min, max, end;
                        if (!lastWasQuantifier && lastEntity != null && TryReadInterval(out min, out max, out end) &&
                            (end == _rubyPattern.Length || _rubyPattern[end] != '+') &&
                            // {n}? is a quantified quantifier, not a lazy one
                            (max != min || end == _rubyPattern.Length || _rubyPattern[end] != '?') &&
                            RewriteLoop(lastEntity, lastEntityIndex, min, max, end < _rubyPattern.Length && _rubyPattern[end] == '?')) {
                            _index = end;
                            if (Peek() == '?') {
                                Skip();
                            }
                            _lastQuantifierKind = null;
                            lastWasQuantifier = true;
                            break;
                        }
                        if (ParseConstrainedQuantifier(lastWasQuantifier, lastEntityIndex, out isExactCount)) {
                            // {0,1} is Onigmo's ?, the one interval that counts as a popular quantifier here
                            int next = Peek();
                            string kind = (_rubyPattern.Substring(start, _index - start) == "0,1}")
                                ? (next == '?' ? "??" : "?")
                                : null;
                            if (inner != null && kind != null) {
                                CheckNestedQuantifier(inner, kind);
                            }
                            if (kind != null && next == '+') {
                                // {n,m}+ is not possessive in Ruby: the + quantifies again
                                CheckNestedQuantifier(kind, "+");
                                kind = null;
                            }
                            _lastQuantifierKind = kind;
                            lastWasQuantifier = !ParsePostQuantifier(lastEntityIndex, false, isExactCount);
                        } else {
                            goto default;
                        }
                        break;
                    }

                    case '(': {
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = new LoopBody {
                            GroupCountBefore = _groupCount,
                            BackrefCountBefore = _backrefCount,
                            EmptyCaptureCountBefore = _emptyCaptureCount,
                            OccurrencesBefore = _hasNamedGroup ? new Dictionary<string, int>(_groupNameOccurrences) : null,
                            Mode = _characterClassMode,
                        };
                        int entityStart = _index - 1;
                        ParseGroup();
                        lastEntity.Source = _rubyPattern.Substring(entityStart, _index - entityStart);
                        lastEntity.GroupCountAfter = _groupCount;
                        if (lastEntity.GroupCountBefore == _groupCount ||
                            (lastEntity.BackrefCountBefore == _backrefCount && lastEntity.EmptyCaptureCountBefore == _emptyCaptureCount)) {
                            // no capture that an empty iteration could set, or nothing that could
                            // observe it: .NET's loop gives the same result
                            lastEntity = null;
                        }
                        break;
                    }

                    case ')':
                        if (isSubexpression) {
                            return;
                        } else {
                            throw MakeError("unmatched close parenthesis");
                        }
                    
                    case '[':
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        bool topLevelPosixClass;
                        AppendCharacterSet(ParseCharacterGroup(false, out topLevelPosixClass), true);
                        break;

                    case '.':
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        AppendAnyCharacter();
                        break;

                    case '|':
                        Append('|');
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        break;

                    case '^':
                        // RegexOptions.Multiline is always on, and .NET's ^ then also matches the
                        // empty line it considers a trailing \n to open. Ruby has no such line:
                        // ^ matches at the start of the string and after a \n that is not the last
                        // character. "a\n\nb".scan(/^/).size is 3 in Ruby, 4 in .NET.
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        _sb.Append("(?:\\A|(?<=\\n)(?!\\z))");
                        break;

                    default:
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        if (Char.IsHighSurrogate((char)c) && Peek() >= 0xdc00 && Peek() <= 0xdfff) {
                            // A literal character above U+FFFF is a surrogate pair, and a quantifier
                            // after it applies to the whole of it, not to its trailing half.
                            int low = Read();
                            bool group = IsQuantifierNext();
                            if (group) {
                                _sb.Append("(?:");
                            }
                            Append((char)c);
                            Append((char)low);
                            if (group) {
                                _sb.Append(')');
                            }
                            break;
                        }
                        Append((char)c);
                        break;
                }
            }
        }

        private bool IsQuantifierNext() {
            int next = Peek();
            return next == '*' || next == '+' || next == '?' || next == '{';
        }

        // Onigmo's word characters - the Unicode "word" property - as a .NET entity that takes a
        // character above U+FFFF as its surrogate pair.
        private static string _astralWordCharacter;

        /// <summary>
        /// \b and \B for a subject with surrogate pairs. .NET's \b looks at single UTF-16 units, to
        /// which both halves of a pair are non-word characters: it would see a boundary around 𝒳 but
        /// none between 😀 and a letter after it, and a \B between the halves of every pair. So the
        /// boundary is spelled out with lookarounds over whole characters.
        /// </summary>
        private void AppendAstralWordBoundary(bool boundary) {
            string w = _astralWordCharacter;
            if (w == null) {
                var sb = new StringBuilder();
                CharacterSet.MakeProperty(UnicodeProperties.Find("word"), true).AppendTo(sb, true);
                _astralWordCharacter = w = sb.ToString();
            }
            if (boundary) {
                _sb.Append("(?:(?<=").Append(w).Append(")(?!").Append(w).Append(")|(?<!").Append(w).Append(")(?=").Append(w).Append("))");
            } else {
                _sb.Append("(?:(?<=").Append(w).Append(")(?=").Append(w).Append(")|(?<!").Append(w).Append(")(?!").Append(w).Append("))");
                // not between the two halves of a pair, both of which are non-word units
                _sb.Append("(?<![\\ud800-\\udbff])");
            }
        }

        /// <summary>
        /// Reads the interval of a {n,m} {n,} {,m} or {n} quantifier whose '{' has just been read,
        /// without consuming it. <paramref name="max"/> is -1 when unbounded;
        /// <paramref name="end"/> is the index just past the '}'.
        /// </summary>
        private bool TryReadInterval(out int min, out int max, out int end) {
            min = max = end = -1;
            int i = _index;
            int n = -1, m = -1;
            bool comma = false;
            while (true) {
                if (i >= _rubyPattern.Length) {
                    return false;
                }
                char c = _rubyPattern[i++];
                if (c == '}') {
                    break;
                } else if (c == ',' && !comma) {
                    comma = true;
                } else if (Tokenizer.IsDecimalDigit(c)) {
                    int value = comma ? m : n;
                    value = (value < 0 ? 0 : value) * 10 + (c - '0');
                    if (value > 100000) {
                        return false;
                    }
                    if (comma) { m = value; } else { n = value; }
                } else {
                    return false;
                }
            }
            if (n < 0 && m < 0) {
                return false;
            }
            min = n < 0 ? 0 : n;
            max = comma ? m : min;
            end = i;
            return max < 0 || max >= min;
        }

        /// <summary>
        /// Onigmo ends a loop after an iteration that matched the empty string only if the
        /// iteration left the captures as they were; .NET ends it after any empty iteration past
        /// the minimum count. It matters when a later iteration can observe the capture - through
        /// a backreference, as in (a|\2b|())* on "aaabbb", or through the capture's final value.
        /// Such a loop of a group X is rewritten so that no .NET loop has to go on past an empty
        /// iteration: iterations are grouped into units of up to K+1, X' being a copy of X whose
        /// groups keep their numbers, and only a unit that is empty as a whole ends the loop.
        /// A unit only continues while it is still empty (E: the input left is the same as at
        /// the unit start S), so each run of iterations divides into units in exactly one way.
        ///
        ///   X*      (?:S X(?:E X'(?:E X')?)?)*       (K = 2; lazily (?:S X(?:|E X'(?:|E X')))*?)
        ///   X{n,}   X{n}(?:S X'(?:E X'(?:E X')?)?)*
        ///   X{n,m}  X{n}(?:X'(?:X')?)?         (the optional iterations nested: no loop at all)
        ///
        /// K bounds how many empty iterations in a row are possible; each of them has to set
        /// another group, so it is one less than the groups in X, at most 3.
        /// Only loops of a group that can match the empty string and has a backreference or an
        /// empty capture () in its body are rewritten - the units cost more: Onigmo itself only
        /// checks the captures after an empty iteration when a backreference could observe them.
        /// </summary>
        private bool RewriteLoop(LoopBody entity, int entityIndex, int min, int max, bool lazy) {
            if (entity == null || _state.Collecting || _absentDepth != 0) {
                return false;
            }
            Debug.Assert(entity.Source != null);
            if (!CanMatchEmpty(_sb.ToString(entityIndex, _sb.Length - entityIndex))) {
                return false;
            }

            // An optional part: (?:...)? or, lazily, (?:|...) - .NET overflows its backtracking
            // stack on (?:...)?? nested in a lazy loop.
            string open = lazy ? "(?:|" : "(?:";
            string close = lazy ? ")" : ")?";
            if (max < 0) {
                int units = Math.Max(1, Math.Min(entity.GroupCountAfter - entity.GroupCountBefore - 1, 3));
                string copy = CopyLoopBody(entity);
                // A unit goes on only while it is still empty, so that a run of iterations can
                // be cut into units in just one way: otherwise a failing match would try them all.
                // The unit start is remembered as the rest of the input, in a hidden group.
                int rest = AllocateHiddenGroup();
                string start = "(?=(?<" + rest + ">[\\s\\S]*))";
                string stillEmpty = "(?=\\k<" + rest + ">\\z)";
                string more = "";
                for (int i = 0; i < units; i++) {
                    more = open + stillEmpty + copy + more + close;
                }
                // Lazily the loop of units is a counted one: .NET 10 goes on taking empty units
                // of (?:...)*? until its backtracking stack overflows - (()|a)*? and the like -
                // where (?:...){0,N}? stops as Onigmo does.
                string loop = lazy ? "){0," + (Int32.MaxValue - 1) + "}?" : ")*";
                if (min == 0) {
                    _sb.Insert(entityIndex, "(?:" + start);
                    _sb.Append(more).Append(loop);
                } else {
                    if (min > 1) {
                        _sb.Append('{').Append(min).Append('}');
                    }
                    _sb.Append("(?:").Append(start).Append(copy).Append(more).Append(loop);
                }
                return true;
            }

            int optional = max - min;
            if (optional <= 0) {
                return false;
            }
            string body = CopyLoopBody(entity);
            if ((long)body.Length * optional > ExpansionBudget / 10) {
                return false;
            }
            if (min == 0) {
                _sb.Insert(entityIndex, open);
                optional--;
            } else if (min > 1) {
                _sb.Append('{').Append(min).Append('}');
            }
            for (int i = 0; i < optional; i++) {
                _sb.Append(open).Append(body);
            }
            for (int i = 0; i < optional; i++) {
                _sb.Append(close);
            }
            if (min == 0) {
                _sb.Append(close);
            }
            return true;
        }

        /// <summary>
        /// Whether the translated group can match the empty string - when unsure, true. A
        /// backreference to a group outside of it makes .NET reject the pattern alone.
        /// </summary>
        private bool CanMatchEmpty(string/*!*/ clrPattern) {
            try {
                return new Regex("\\A(?:" + clrPattern + ")\\z", _state.ClrOptions, TimeSpan.FromSeconds(1)).IsMatch(String.Empty);
            } catch (ArgumentException) {
                return true;
            } catch (RegexMatchTimeoutException) {
                return true;
            }
        }

        private string/*!*/ CopyLoopBody(LoopBody/*!*/ entity) {
            return TransformCopy(new RegexpTransformer(this, entity.Source, entity.GroupCountBefore, entity.OccurrencesBefore, entity.Mode, _callDepth));
        }

        // {n,m}
        // {n,}
        // {,m}
        // {n}
        private bool ParseConstrainedQuantifier(bool lastWasQuantifier, int lastEntityIndex, out bool isExactCount) {
            Debug.Assert(_rubyPattern[_index - 1] == '{');
            isExactCount = false;

            int c;
            int m = -1;
            bool hasDigits = false;
            bool hasDigitsAfterComma = false;

            int i = 0;
            while (true) {
                c = Peek(i++);
                if (c == ',') {
                    if (m != -1) {
                        // not a quantifier
                        return false;
                    }
                    m = i;                    
                } else if (c == '}') {
                    break;
                } else if (!Tokenizer.IsDecimalDigit(c)) {
                    return false;
                } else {
                    hasDigits = true;
                    if (m != -1) {
                        hasDigitsAfterComma = true;
                    }
                }
            }

            // {} and {,} carry no count: Ruby and .NET both read them as literal text, so they go
            // through untouched and are not treated as a quantifier for the purposes below.
            if (hasDigits) {
                if (lastWasQuantifier) {
                    // a*{2} == (?:a*){2}
                    _sb.Insert(lastEntityIndex, "(?:");
                    Append(')');
                }
                if (m == 1 && hasDigitsAfterComma) {
                    // Ruby's {,m} means {0,m}; .NET has no such form and reads it as literal text.
                    _sb.Append("{0");
                    _sb.Append(_rubyPattern, _index, i);
                } else {
                    _sb.Append(_rubyPattern, _index - 1, i + 1);
                    isExactCount = m == -1;
                }
            } else {
                _sb.Append(_rubyPattern, _index - 1, i + 1);
            }
            _index += i;
            return true;
        }

        /// <summary>Returns true when the quantified entity was wrapped in a group.</summary>
        private bool ParsePostQuantifier(int lastEntityIndex, bool possessive, bool questionIsQuantifier) {
            int c = Peek();

            if (c == '+') {
                // nested possessive quantifiers not directly supported by Regex:
                Skip();
                _sb.Insert(lastEntityIndex, possessive ? "(?>" : "(?:");
                Append(')');
                if (!possessive) {
                    Append('+');
                }
                return true;
            } else if (c == '?') {
                Skip();
                if (questionIsQuantifier) {
                    // After an exact count Ruby reads ? as another quantifier: a{1}? == (?:a{1})?.
                    // After a range it is the ordinary laziness marker: a{1,2}? matches one 'a'.
                    _sb.Insert(lastEntityIndex, "(?:");
                    _sb.Append(")?");
                    return true;
                }
                Append('?');
            }
            return false;
        }

        //
        // (?#...)            comment
        // (?imx-imx)         option on/off
        // (?imx-imx:subexp)  option on/off for subexp
        // (?:subexp)         not captured group
        // (?=subexp)         look-ahead
        // (?!subexp)         negative look-ahead
        // (?<=subexp)        look-behind
        // (?<!subexp)        negative look-behind
        // (?>subexp)         atomic group
        //                    don't backtrack in subexp.
        // 
        // (?<name>subexp)
        // (?'name'subexp)
        //
        // (subexp)           captured group
        //
        private void ParseGroup() {
            Debug.Assert(_rubyPattern[_index - 1] == '(');
            int groupNumber = -1;
            string groupName = null;
            if (Read('?')) {
                int c = Read();
                if (c == '#') {
                    while (true) {
                        c = Read();
                        if (c == -1) {
                            throw MakeError("end pattern in group");
                        }
                        if (c == ')') {
                            break;
                        }
                    }
                    return;
                }

                if (c == '~') {
                    ParseAbsentExpression();
                    return;
                }

                if (c == '-' || c == 'i' || c == 'm' || c == 'x' || c == 'a' || c == 'd' || c == 'u') {
                    ParseGroupOptions(c);
                    return;
                }

                Append('(');
                Append('?');

                switch (c) {
                    case ':':
                        // non-captured group
                        Append(':');
                        break;

                    case '=':
                        // positive lookahead
                        Append('=');
                        break;

                    case '!':
                        // negative lookahead
                        Append('!');
                        break;

                    case '>':
                        // greedy subexpression
                        Append('>');
                        break;

                    case '<':
                        c = Read();
                        if (c == '=' || c == '!') {
                            // positive/negative lookbehind assertion
                            Append('<');
                            Append((char)c);
                        } else {
                            _groupCount++;
                            groupNumber = _groupCount;
                            groupName = ParseGroupName(c, '>', '<');
                        }
                        break;

                    case '\'':
                        _groupCount++;
                        groupNumber = _groupCount;
                        groupName = ParseGroupName(Read(), '\'', '\'');
                        break;

                    case '(': {
                        // conditional group: (?(1)yes|no), (?(<name>)...) or (?('name')...).
                        // .NET has the same construct but spells the named form without the
                        // brackets Onigmo requires around the name.
                        Append('(');
                        int delimiter = Read();
                        int closing = (delimiter == '<') ? '>' : (delimiter == '\'') ? '\'' : -1;
                        if (closing == -1) {
                            Back();
                        }

                        var condition = new StringBuilder();
                        while (true) {
                            c = Read();
                            if (c == -1) {
                                throw MakeError("end pattern in group");
                            }
                            if (c == (closing == -1 ? ')' : closing)) {
                                if (closing != -1 && Read() != ')') {
                                    throw MakeError("invalid conditional pattern");
                                }
                                break;
                            }
                            condition.Append((char)c);
                        }

                        string name = condition.ToString();
                        _backrefCount++;
                        int number = ResolveGroupNumber(name);
                        if (number >= 0) {
                            // (?(01)...), (?(<-1>)...) - .NET only understands a plain number
                            _sb.Append(number);
                        } else if (closing == -1) {
                            // Ruby requires <> or '' around a name; .NET would accept a bare one
                            throw MakeError("invalid conditional pattern");
                        } else if (IsLevelSpecifier(name)) {
                            throw MakeError("invalid group name <" + name + ">");
                        } else {
                            _sb.Append(name);
                        }
                        Append(')');
                        break;
                    }

                    default:
                        throw MakeError("undefined group option");
                }
            } else if (_hasNamedGroup) {
                // with a named group anywhere in the pattern, Ruby's plain groups do not capture
                _sb.Append("(?:");
            } else {
                _groupCount++;
                groupNumber = _groupCount;
                if (_copyGroups) {
                    // a copy's group is the same group as the one it copies
                    _sb.Append("(?<").Append(groupNumber).Append('>');
                } else {
                    _sb.Append('(');
                }
            }
            var savedMode = _characterClassMode;
            var savedDotAll = _dotAll;
            var savedExtended = _extended;
            int bodyStart = _index;
            GroupDefinition definition = null;
            bool hasLevelGroup = false;
            if (groupNumber >= 0) {
                OpenGroup("#" + groupNumber);
                if (groupName != null) {
                    OpenGroup(groupName);
                }
                hasLevelGroup = OpenLevelGroup(GroupKey(groupNumber, groupName));
                if (!_copyGroups) {
                    int occurrence = 0;
                    if (groupName != null) {
                        _groupNameOccurrences.TryGetValue(groupName, out occurrence);
                    }
                    definition = new GroupDefinition {
                        Number = groupNumber,
                        Name = groupName,
                        Occurrence = occurrence,
                        OccurrencesBefore = _hasNamedGroup ? new Dictionary<string, int>(_groupNameOccurrences) : null,
                        Mode = _characterClassMode,
                    };
                }
            }
            _groupDepth++;
            Parse(true);
            _groupDepth--;
            _characterClassMode = savedMode;
            _dotAll = savedDotAll;
            _extended = savedExtended;
            if (groupNumber >= 0) {
                CloseGroup("#" + groupNumber);
                if (groupName != null) {
                    CloseGroup(groupName);
                }
                if (hasLevelGroup) {
                    Append(')');
                }
                string body = _rubyPattern.Substring(bodyStart, _index - 1 - bodyStart);
                if (body.Length == 0) {
                    // () - a capture that is only ever set without consuming anything (see RewriteLoop)
                    _emptyCaptureCount++;
                }
                if (definition != null) {
                    // _index is now just past the ')' Parse consumed
                    definition.Source = body;
                    _state.ByNumber[groupNumber] = definition;
                    if (groupName != null && !_state.ByName.ContainsKey(groupName)) {
                        _state.ByName[groupName] = definition;
                    }
                }
            }
            Append(')');
        }

        //
        // (?imxadu-imx)          option on/off for the rest of the enclosing group
        // (?imxadu-imx:subexp)   option on/off for subexp
        //
        // a, d and u select the repertoire of \w, \d, \s and the POSIX bracket classes. .NET has
        // no such flag, so they are not emitted; they change how those classes are expanded.
        //
        private void ParseGroupOptions(int c) {
            var flags = new StringBuilder();
            var mode = _characterClassMode;
            var dotAll = _dotAll;
            var extended = _extended;
            bool isScoped = false;
            bool negative = false;

            while (true) {
                if (c == 'm') {
                    // Map (?m) to (?s) ie. RegexOptions.SingleLine
                    flags.Append('s');
                    if (dotAll != null) {
                        dotAll = !negative;
                    }
                } else if (c == 'i' || c == 'x' || c == '-') {
                    negative |= c == '-';
                    flags.Append((char)c);
                    if (c == 'x') {
                        extended = !negative;
                    }
                } else if (c == 'a') {
                    mode = CharacterClassMode.Ascii;
                } else if (c == 'd') {
                    mode = CharacterClassMode.Default;
                } else if (c == 'u') {
                    mode = CharacterClassMode.Unicode;
                } else if (c == ':') {
                    isScoped = true;
                    break;
                } else if (c == ')') {
                    break;
                } else if (c == -1) {
                    throw MakeError("end pattern in group");
                } else {
                    throw MakeError("undefined group option");
                }
                c = Read();
            }

            string options = flags.ToString();
            if (options == "-") {
                // a lone '-' turns nothing off and .NET rejects it
                options = "";
            }

            if (!isScoped) {
                _characterClassMode = mode;
                _dotAll = dotAll;
                _extended = extended;
                if (options.Length != 0) {
                    _sb.Append("(?").Append(options).Append(')');
                }
                return;
            }

            _sb.Append("(?").Append(options).Append(':');
            var savedMode = _characterClassMode;
            var savedDotAll = _dotAll;
            var savedExtended = _extended;
            _characterClassMode = mode;
            _dotAll = dotAll;
            _extended = extended;
            _groupDepth++;
            Parse(true);
            _groupDepth--;
            _characterClassMode = savedMode;
            _dotAll = savedDotAll;
            _extended = savedExtended;
            _sb.Append(')');
        }

        /// <summary>
        /// (?~E) matches the longest string that contains no match of E.
        ///
        /// .NET has no absent operator but does support variable-length lookbehind, so
        /// "consume one character provided we have not just completed an E" renders it.
        /// (?:(?!E)[\s\S])* is NOT equivalent: the lookahead sees past the intended match end
        /// and stops too early ("xfooy" would give "x" where Onigmo gives "xfo").
        ///
        /// The lookbehind alone is still not enough, because it also sees occurrences of E that
        /// began before the match started - "foo".scan(/(?~foo)/) would give ["fo", "", ""] where
        /// Onigmo gives ["fo", "o", ""]. An E of fixed length L can only be completed inside the
        /// match once L-1 characters have been consumed, so the first L-1 characters are consumed
        /// unconditionally. Both parts are greedy and consuming in the unconstrained part is never
        /// worse, so the result is still the longest match.
        ///
        /// When E has no computable fixed length the plain lookbehind form is emitted; it is exact
        /// for a match at the start of the input and conservative elsewhere.
        /// </summary>
        private void ParseAbsentExpression() {
            var outer = _sb;
            _sb = new StringBuilder();
            _absentDepth++;
            Parse(true);
            _absentDepth--;
            string absent = _sb.ToString();
            _sb = outer;

            if (absent.Length == 0) {
                // (?~) can never complete, so it matches everything remaining.
                _sb.Append("[\\s\\S]*");
                return;
            }

            int fixedLength = GetLiteralLength(absent);
            _sb.Append("(?:");
            if (fixedLength > 1) {
                _sb.Append("[\\s\\S]{0,").Append(fixedLength - 1).Append('}');
            }
            _sb.Append("(?:[\\s\\S](?<!").Append(absent).Append("))*)");
        }

        /// <summary>
        /// The number of characters a translated pattern matches when it is a plain literal
        /// sequence, or -1 when it contains anything whose length is not fixed and obvious.
        /// </summary>
        private static int GetLiteralLength(string/*!*/ pattern) {
            int length = 0;
            for (int i = 0; i < pattern.Length; i++) {
                char c = pattern[i];
                if (c == '\\') {
                    if (i + 1 == pattern.Length || !IsMetaCharacter(pattern[i + 1])) {
                        return -1;
                    }
                    i++;
                } else if (IsMetaCharacter(c)) {
                    return -1;
                }
                length++;
            }
            return length;
        }

        /// <summary>
        /// Reads a group name and emits the .NET spelling of the declaration. Returns the name.
        /// </summary>
        private string/*!*/ ParseGroupName(int c, int terminator, int opening) {
            if (c == terminator || c == -1) {
                throw MakeError("group name is empty");
            }
            var name = new StringBuilder();
            int closing;
            while (true) {
                name.Append((char)c);
                c = Read();
                if (c == terminator || c == ')') {
                    closing = c;
                    break;
                } else if (c == -1) {
                    throw MakeError("unterminated group name");
                }
            }

            if (name[0] == '-' || Tokenizer.IsDecimalDigit(name[0])) {
                throw MakeError("invalid group name <" + name + ">");
            }

            string text = name.ToString();
            int occurrence;
            _groupNameOccurrences.TryGetValue(text, out occurrence);
            _groupNameOccurrences[text] = ++occurrence;

            Append((char)opening);
            _sb.Append(DuplicateGroupName(text, occurrence));
            Append((char)closing);
            return text;
        }

        #region Escapes

        private void ParseEscape() {
            int c = Read();
            if (c == -1) {
                throw MakeError("too short escape sequence");
            }
            ParseEscape(c);
        }
        
        // escape outside of character group
        private void ParseEscape(int escape) {
            switch (escape) {
                case 'b':   // word boundary
                case 'B':   // not a word boundary
                    if (_state.AstralSafe) {
                        AppendAstralWordBoundary(escape == 'b');
                    } else {
                        Append('\\');
                        Append((char)escape);
                    }
                    break;

                case 'A':   // beginning a string
                case 'Z':   // end of string or a new line
                case 'z':   // end of string
                    Append('\\');
                    Append((char)escape);
                    break;

                // \g<n>
                // \g'n'
                // \g<-n>         
                // \g'-n'
                // \g<name>
                // \g'name'
                case 'g':
                    ParseSubexpressionCall();
                    break;
                
                case 'k':
                    ParseBackreference();
                    break;

                case 'X':
                    // An extended grapheme cluster, which .NET has no escape for. This covers what
                    // text commonly holds (UAX #29 without the Hangul and prepend rules): CRLF, a
                    // regional indicator pair (a flag), and a code point followed by combining
                    // marks, variation selectors, emoji modifiers and tags, or joined to further
                    // code points by ZWJ. Atomic, like Onigmo's.
                    _sb.Append(
                        "(?>\\r\\n|\\uD83C[\\uDDE6-\\uDDFF]\\uD83C[\\uDDE6-\\uDDFF]|" +
                        "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF])" +
                        "(?:\\p{M}|[\\uFE00-\\uFE0F\\u200C]|\\uD83C[\\uDFFB-\\uDFFF]|\\uDB40[\\uDC20-\\uDC7F]|" +
                        "\\u200D(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF]))*)"
                    );
                    break;

                case 'R':
                    // A generic line break: CRLF as a unit, or any single line terminator.
                    // Atomic so that CRLF never backtracks into matching just the CR.
                    _sb.Append("(?>\\r\\n|[\\n\\v\\f\\r\\u0085\\u2028\\u2029])");
                    break;

                case 'K':
                    // Keep: everything matched so far is excluded from the reported match. .NET
                    // has no such operator but does support variable-length lookbehind, so the
                    // pattern emitted so far becomes the lookbehind of what follows.
                    if (_absentDepth != 0 || _groupDepth != 0) {
                        throw MakeError("\\K is only supported at the top level of a pattern");
                    }
                    _sb.Insert(0, "(?<=").Append(')');
                    break;

                case 'G':   // start position
                    _hasGAnchor = true;
                    Append('\\');
                    Append((char)escape);
                    break;

                case 'u':
                    if (Peek() == '{') {
                        // \u{1234 12345 123}
                        var codepoints = new List<int>(ParseUnicodeEscapeList());
                        for (int i = 0; i < codepoints.Count; i++) {
                            // a quantifier after the list applies to its last character, all of it
                            if (i == codepoints.Count - 1 && codepoints[i] >= 0x10000 && IsQuantifierNext()) {
                                _sb.Append("(?:");
                                AppendUnicodeCodePoint(_sb, codepoints[i]);
                                _sb.Append(')');
                            } else {
                                AppendUnicodeCodePoint(_sb, codepoints[i]);
                            }
                        }
                    } else {
                        // \u1234
                        AppendUnicodeCodePoint(_sb, ParseUnicodeEscape());
                    }
                    break;
                    
                default:
                    if (Tokenizer.IsDecimalDigit(escape)) {
                        ParseNumericEscape(escape);
                        break;
                    }

                    var set = ParseCharacterEscape(escape);
                    if ((escape == 'w' || escape == 'W') && _characterClassMode == CharacterClassMode.Unicode) {
                        // Outside a class Onigmo tests a (?u)\w below U+0100 against its ISO-8859-1
                        // ctype table, which also counts the superscripts and vulgar fractions
                        // (U+00B2 U+00B3 U+00B9 U+00BC-U+00BE) as word characters.
                        var latin1Word = CharacterSet.MakeSet(_latin1WordQuirk, null);
                        set = (escape == 'w') ? set.Union(latin1Word) : set.Subtract(latin1Word);
                    }
                    AppendCharacterSet(set, false);
                    break;
            }
        }

        // \g<n>  \g'n'  \g<-n>  \g'-n'  \g<name>  \g'name'  \g<0>
        //
        // A subexpression call re-runs a group's pattern at this point. .NET has no such construct,
        // so the group's Ruby source is transformed again and spliced in here. The copy is emitted
        // under the called group's own name or number, and so are the groups nested in it, which
        // .NET permits and treats as the same groups: as in Onigmo, running the copy updates those
        // groups' captures, and the numbering of the rest of the pattern is unchanged.
        //
        // A recursive call is expanded the same way, up to SharedState.MaxCallDepth levels deep;
        // at that depth the call fails to match. Input nested deeper than that is not matched.
        private void ParseSubexpressionCall() {
            int terminator;
            int c = Read();
            if (c == '<') {
                terminator = '>';
            } else if (c == '\'') {
                terminator = '\'';
            } else {
                throw MakeError("invalid group call");
            }

            var reference = new StringBuilder();
            while (true) {
                c = Read();
                if (c == terminator) {
                    break;
                } else if (c == -1) {
                    throw MakeError("invalid group name");
                }
                reference.Append((char)c);
            }

            string name = reference.ToString();
            if (name.Length == 0) {
                throw MakeError("group name is empty");
            }

            if (_state.Collecting) {
                // the groups are not all known yet; this pass only records them
                return;
            }

            // Unlike \k<...>, a call may name a group whose name carries a '+' or a '-'.
            GroupDefinition definition;
            string key;
            if (name == "0") {
                // \g<0> calls the whole pattern
                key = "#0";
                definition = new GroupDefinition { Source = _state.RootPattern, Number = 0 };
            } else if (IsGroupNumber(name)) {
                if (_hasNamedGroup) {
                    throw MakeError("numbered backref/call is not allowed. (use name)");
                }
                int number = ResolveGroupNumber(name);
                key = "#" + number;
                if (!_state.ByNumber.TryGetValue(number, out definition)) {
                    throw MakeError("undefined group <" + name + ">");
                }
            } else {
                key = name;
                if (!_state.ByName.TryGetValue(name, out definition)) {
                    throw MakeError("undefined group name <" + name + ">");
                }
            }

            bool recursive = key == "#0" || IsOpenGroup(key);
            if (recursive && _callDepth >= _state.MaxCallDepth) {
                _sb.Append("(?!)");
                return;
            }

            int depth = _callDepth + 1;
            var copy = new RegexpTransformer(this, definition.Source, definition.Number, definition.OccurrencesBefore, definition.Mode, depth);
            string numberKey = "#" + definition.Number;
            OpenGroup(numberKey);
            if (definition.Name != null) {
                OpenGroup(definition.Name);
            }
            string body;
            try {
                body = TransformCopy(copy);
            } finally {
                CloseGroup(numberKey);
                if (definition.Name != null) {
                    CloseGroup(definition.Name);
                }
            }

            if (definition.Number == 0) {
                _sb.Append("(?:").Append(body).Append(')');
                return;
            }

            _sb.Append("(?<").Append(definition.Name != null ? DuplicateGroupName(definition.Name, definition.Occurrence) : definition.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('>');
            string levelKey = GroupKey(definition.Number, definition.Name);
            if (_state.LevelReferenced.Contains(levelKey)) {
                int level = GetLevelGroup(levelKey, depth);
                _state.DefinedLevelGroups.Add(level);
                _sb.Append("(?<").Append(level).Append('>').Append(body).Append(')');
            } else {
                _sb.Append(body);
            }
            _sb.Append(')');
        }

        private static bool IsGroupNumber(string/*!*/ text) {
            int i = (text.Length != 0 && text[0] == '-') ? 1 : 0;
            if (i == text.Length) {
                return false;
            }
            for (; i < text.Length; i++) {
                if (!Tokenizer.IsDecimalDigit(text[i])) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// \N after a backslash is a backreference, an octal escape or plain text depending on the
        /// number and on how many groups have been opened. .NET decides differently from Ruby, so
        /// the choice has to be made here.
        /// </summary>
        private void ParseNumericEscape(int firstDigit) {
            if (firstDigit == '0') {
                // A leading zero always means an octal escape: (a)\01 matches "a\x01", not "aa".
                Back();
                AppendEscaped(ParseSingleByteCharacterEscape(Read()));
                return;
            }

            int start = _index - 1;
            int value = 0;
            _index = start;
            while (Tokenizer.IsDecimalDigit(Peek())) {
                if (value < 100000) {
                    value = value * 10 + (Read() - '0');
                } else {
                    Skip();
                }
            }
            int digitCount = _index - start;

            if (value > 1000) {
                // Onigmo gives up on a number this large and matches the digits as literal text.
                AppendEscaped(_sb, _rubyPattern.Substring(start, digitCount));
                return;
            }

            if (value <= _groupCount) {
                if (_hasNamedGroup) {
                    throw MakeError("numbered backref/call is not allowed. (use name)");
                }
                AppendBackreference(value, false);
                return;
            }

            if (value >= 10) {
                // Not a group that exists, and too long to be a forward reference: Ruby re-reads
                // the digits as an octal escape, so \10 is "\b".
                _index = start;
                AppendEscaped(ParseSingleByteCharacterEscape(Read()));
                return;
            }

            // A forward reference below 10. .NET accepts these as long as the group eventually
            // appears; it reports its own error if it does not.
            if (_hasNamedGroup) {
                throw MakeError("numbered backref/call is not allowed. (use name)");
            }
            AppendBackreference(value, false);
        }

        /// <summary>
        /// A backreference to a group that has not been closed yet - \1 inside (a\1?)+ - never
        /// matches in Onigmo, even when an earlier iteration captured the group; in .NET it would
        /// match that earlier capture.
        /// </summary>
        private void AppendBackreference(int number, bool bracketed) {
            _backrefCount++;
            if (IsOpenGroup("#" + number)) {
                _sb.Append("(?!)");
            } else if (bracketed) {
                _sb.Append("\\k<").Append(number).Append('>');
            } else {
                _sb.Append('\\').Append(number);
            }
        }

        // \k<n>
        // \k'n'
        // \k<-n>         
        // \k'-n'
        // \k<m+n>
        // \k<m-n>
        // \k'm+n'
        // \k'm-n'
        // \k<name>
        // \k'name'
        // \k<name+n>
        // \k<name-n>
        // \k'name+n'
        // \k'name-n'
        private void ParseBackreference() {
            Debug.Assert(_rubyPattern[_index - 1] == 'k');
            
            int terminator;
            int c = Read();
            if (c == '<') {
                terminator = '>';                
            } else if (c == '\'') {
                terminator = '\'';
            } else {
                throw MakeError("invalid back reference");
            }

            var reference = new StringBuilder();
            while (true) {
                c = Read();
                if (c == terminator) {
                    break;
                } else if (c == -1) {
                    throw MakeError("invalid group name");
                }
                reference.Append((char)c);
            }

            string name = reference.ToString();
            if (name.Length == 0) {
                throw MakeError("group name is empty");
            }

            // \k<0> and \k<-n> that reaches past the first group are rejected by ResolveGroupNumber.
            int number = ResolveGroupNumber(name);
            if (number >= 0) {
                if (_hasNamedGroup) {
                    throw MakeError("numbered backref/call is not allowed. (use name)");
                }
                AppendBackreference(number, true);
                return;
            }

            if (IsLevelSpecifier(name)) {
                ParseLevelBackreference(name);
                return;
            }

            _backrefCount++;
            int count;
            if (_groupNameCounts.TryGetValue(name, out count) && count > 1) {
                // a name several groups share refers to whichever of them has matched
                _sb.Append("(?:");
                for (int i = count; i >= 1; i--) {
                    _sb.Append("\\k<").Append(DuplicateGroupName(name, i)).Append('>');
                    if (i > 1) {
                        _sb.Append('|');
                    }
                }
                _sb.Append(')');
                return;
            }

            if (IsOpenGroup(name)) {
                // see AppendBackreference
                _sb.Append("(?!)");
                return;
            }
            _sb.Append("\\k<").Append(name).Append('>');
        }

        /// <summary>
        /// \k<name+n> and \k<name-n> refer to what the group captured at the subexpression call
        /// level n above or below the reference's own (\k<name-0>: the same level). Each level
        /// of an expanded call is a separate copy, so a group referred to this way is wrapped,
        /// in every copy, in a hidden group of its own for that level (see OpenLevelGroup).
        /// </summary>
        private void ParseLevelBackreference(string/*!*/ reference) {
            int separator = Math.Max(reference.LastIndexOf('+'), reference.LastIndexOf('-'));
            string name = reference.Substring(0, separator);
            string levelText = reference.Substring(separator + 1);
            int offset = 0;
            bool valid = separator > 0 && levelText.Length > 0;
            foreach (char d in levelText) {
                if (!Tokenizer.IsDecimalDigit(d) || offset > 1000) {
                    valid = false;
                    break;
                }
                offset = offset * 10 + (d - '0');
            }

            string key = null;
            if (valid) {
                int number = IsGroupNumber(name) ? ResolveGroupNumber(name) : -1;
                if (number >= 0) {
                    if (_hasNamedGroup) {
                        throw MakeError("numbered backref/call is not allowed. (use name)");
                    }
                    key = "#" + number;
                } else if (_groupNameCounts.ContainsKey(name)) {
                    key = name;
                }
            }
            if (key == null) {
                throw MakeError("invalid group name <" + reference + ">");
            }

            _backrefCount++;
            _state.LevelReferenced.Add(key);
            int level = _callDepth + (reference[separator] == '+' ? offset : -offset);
            if (level < 0 || _state.Collecting) {
                _sb.Append("(?!)");
            } else {
                _sb.Append("\\k<").Append(GetLevelGroup(key, level)).Append('>');
            }
        }

        //
        // group_escape ::= 
        //     '\' '\'
        //   | '\' 'c' character
        //   | '\' 'C' '-' character
        //   | '\' 'M' '-' character
        //   | '\' 'M' '-' '\' 'C' '-' character
        //   | '\' 'p' '{' character_name '}'
        //   | '\' 'P' '{' character_name '}'
        //   | '\' 'x' hex_digit{1-2}
        //   | '\' 'u' hex_digit{4}
        //   | '\' octal{1-3}
        //   | '\' 'h'                                                 # hex digit
        //   | '\' 'H'                                                 # non-hex digit
        //   | '\' 's'                                                 # whitespace 
        //   | '\' 'S'                                                 # non-whitespace 
        //   | '\' 'w'                                                 # word character
        //   | '\' 'W'                                                 # non-word character
        //   | '\' 'd'                                                 # decimal digit 
        //   | '\' 'D'                                                 # non-decimal digit 
        //   | '\' 'b'                                                 # \u0008
        //   | '\' 't'                                                 # \u0009
        //   | '\' 'n'                                                 # \u000A
        //   | '\' 'v'                                                 # \u000B
        //   | '\' 'f'                                                 # \u000C
        //   | '\' other-character                                           
        //
        private int ParseSingleByteCharacterEscape(int escape) {
            bool hasControlModifier = false, hasMetaModifier = false;
            return ParseSingleByteCharacterEscape(escape, ref hasControlModifier, ref hasMetaModifier);
        }

        private int ParseSingleByteCharacterEscape(int escape, ref bool hasControlModifier, ref bool hasMetaModifier) {
            int c;
            switch (escape) {
                case -1: 
                    throw MakeError("too short escape sequence");

                case '\\': return '\\';
                case 'n': return '\n';
                case 't': return '\t';
                case 'r': return '\r';
                case 'f': return '\f';
                case 'v': return '\v';
                case 'a': return '\a';
                case 'e': return 27;
                case 'b': return '\b';
                
                case 'M':
                    if (!Read('-')) {
                        throw MakeError("too short meta escape");
                    }
                    if (hasMetaModifier) {
                        throw MakeError("duplicate meta escape");
                    }

                    hasMetaModifier = true;
                    c = Read();
                    if (c == -1) {
                        throw MakeError("too short escape sequence");
                    }
                    if (c == '\\') {
                        c = ParseSingleByteCharacterEscape(Read(), ref hasControlModifier, ref hasMetaModifier);
                    }
                    
                    return (c & 0xff) | 0x80;

                case 'C':
                    if (!Read('-')) {
                        throw MakeError("too short control escape");
                    }
                    goto case 'c';

                case 'c':
                    c = Read();
                    if (c == -1) {
                        throw MakeError("too short escape sequence");
                    }
                    if (hasControlModifier) {
                        throw MakeError("duplicate control escape");
                    }

                    hasControlModifier = true;
                    if (c == '\\') {
                        c = ParseSingleByteCharacterEscape(Read(), ref hasControlModifier, ref hasMetaModifier);
                    }
                    
                    return c & 0x9f;

                case 'x':
                    // hexa
                    c = Peek();
                    int d1 = Tokenizer.ToDigit(c);
                    if (d1 > 15) {
                        throw MakeError("invalid hex escape");
                    }
                    Skip();

                    c = Peek();
                    int d2 = Tokenizer.ToDigit(c);
                    if (d2 > 15) {
                        return d1;
                    }
                    Skip();

                    return (d1 << 4) | d2;
                
                default:
                    // octal
                    int o1 = Tokenizer.ToDigit(escape);
                    if (o1 > 7) {
                        return -1;
                    }

                    int o2 = Tokenizer.ToDigit(Peek());
                    if (o2 > 7) {
                        return o1;
                    }
                    Skip();

                    int o3 = Tokenizer.ToDigit(Peek());
                    if (o3 > 7) {
                        return (o1 << 3) | o2;
                    }
                    Skip();

                    return (o1 << 6) | (o2 << 3) | o3;
            }
        }

        private CharacterSet/*!*/ ParseCharacterEscape(int escape) {
            int result = ParseSingleByteCharacterEscape(escape);
            if (result != -1) {
                return CharacterSet.MakeCharacter(result, Escape(result));
            }
                    
            switch (escape) {
                case 'h':
                case 'H': 
                    // [0-9a-fA-F] whatever the mode
                    var hex = CharacterSet.MakeSet(_asciiHexDigit, "a-fA-F0-9");
                    return (escape == 'h') ? hex : hex.Complement();
                    
                case 'p':
                case 'P':
                    return ParseCharacterCategoryName(escape);

                // \w, \d and \s are ASCII only in Ruby unless (?u) is in effect, where .NET's
                // \w, \d and \s are always Unicode aware.
                case 's':
                    return MakeAsciiAware("space", _asciiSpace, "\u0020\u0009-\u000d", true);

                case 'S':
                    return MakeAsciiAware("space", _asciiSpace, "\u0020\u0009-\u000d", false);

                case 'd':
                    return MakeAsciiAware("digit", _asciiDigit, "0-9", true);

                case 'D':
                    return MakeAsciiAware("digit", _asciiDigit, "0-9", false);

                case 'w':
                    return MakeAsciiAware("word", _asciiWord, "a-zA-Z0-9_", true);

                case 'W':
                    return MakeAsciiAware("word", _asciiWord, "a-zA-Z0-9_", false);

                default:
                    // ignore backslash unless needed
                    if (escape >= 0xd800 && escape <= 0xdbff && Peek() >= 0xdc00 && Peek() <= 0xdfff) {
                        return MakeCodePointSet(Char.ConvertToUtf32((char)escape, (char)Read()));
                    }
                    return CharacterSet.MakeCharacter(escape, Escape(escape));
            }
        }

        // Set when the entity just emitted is a class loop body that needs CharacterSet.LoopEndGuard
        // after its quantifier.
        private bool _loopGuardPending;

        /// <summary>
        /// '.': .NET's own unless the subject holds surrogate pairs. Then a lone '.' is a pair or
        /// any BMP character but a surrogate - so it can neither match half a pair nor, when
        /// something after it fails, backtrack to half a pair. In a * or + loop '.' stays as it
        /// is, a one-class loop that takes both halves of a pair as two iterations, with the loop
        /// guard after it so that it cannot stop between them - the same trick as [^"]* (see
        /// AppendCharacterSet), and what keeps .* the fast loop it is.
        /// </summary>
        private void AppendAnyCharacter() {
            if (!_state.AstralSafe) {
                Append('.');
                return;
            }

            int next = Peek();
            if (next == '*' || next == '+') {
                Append('.');
                _loopGuardPending = true;
            } else if (_dotAll == true) {
                _sb.Append("(?:[\\ud800-\\udbff][\\udc00-\\udfff]|[^\\ud800-\\udfff])");
            } else if (_dotAll == false) {
                _sb.Append("(?:[\\ud800-\\udbff][\\udc00-\\udfff]|[^\\n\\ud800-\\udfff])");
            } else {
                _sb.Append("(?:[\\ud800-\\udbff][\\udc00-\\udfff]|(?![\\ud800-\\udfff]).)");
            }
        }

        /// <summary>
        /// Emits a character set as a pattern entity. A set holding every non-BMP character that
        /// a * or + follows is emitted as one class (CharacterSet.AppendLoopBodyTo), for speed.
        /// </summary>
        private void AppendCharacterSet(CharacterSet/*!*/ set, bool parenthesize) {
            int next = Peek();
            if ((next == '*' || next == '+') && set.HasAllAstral) {
                set.AppendLoopBodyTo(_sb);
                _loopGuardPending = true;
            } else {
                set.AppendTo(_sb, parenthesize);
            }
        }

        private static readonly int[] _latin1WordQuirk = new int[] { 0xb2, 0xb3, 0xb9, 0xb9, 0xbc, 0xbe };
        private static readonly int[] _asciiHexDigit = new int[] { '0', '9', 'A', 'F', 'a', 'f' };
        private static readonly int[] _asciiSpace = new int[] { 0x09, 0x0d, 0x20, 0x20 };
        private static readonly int[] _asciiDigit = new int[] { '0', '9' };
        private static readonly int[] _asciiWord = new int[] { '0', '9', 'A', 'Z', '_', '_', 'a', 'z' };

        /// <summary>
        /// \s, \d and \w: ASCII only in Ruby unless (?u) is in effect, where they are Onigmo's
        /// Space, Digit and Word properties.
        /// </summary>
        private CharacterSet/*!*/ MakeAsciiAware(string/*!*/ property, int[]/*!*/ ascii, string/*!*/ asciiText, bool positive) {
            if (_characterClassMode == CharacterClassMode.Unicode) {
                return CharacterSet.MakeProperty(UnicodeProperties.Find(property), positive);
            }
            var result = CharacterSet.MakeSet(ascii, asciiText);
            return positive ? result : result.Complement();
        }

        #endregion

        #region Unicode Codepoints

        // Peeks exactly 4 hexadecimal characters (\uFFFF).
        private int ParseUnicodeEscape() {
            int d4 = Tokenizer.ToDigit(Read());
            int d3 = Tokenizer.ToDigit(Read());
            int d2 = Tokenizer.ToDigit(Read());
            int d1 = Tokenizer.ToDigit(Read());

            if (d1 >= 16 || d2 >= 16 || d3 >= 16 || d4 >= 16) {
                throw MakeError("invalid Unicode escape");
            }

            int codepoint = (d4 << 12) | (d3 << 8) | (d2 << 4) | d1;
            if (codepoint >= 0xd800 && codepoint <= 0xdfff) {
                throw MakeError("invalid Unicode range");
            }
            return codepoint;
        }

        private IEnumerable<int>/*!*/ ParseUnicodeEscapeList() {
            Skip('{');
            while (true) {
                int codepoint = ParseUnicodeCodePoint();
                int c = Read();
                yield return codepoint;
                if (c == '}') {
                    break;
                }
                if (c != ' ') {
                    throw MakeError("invalid Unicode list");
                }
            }
        }

        // Parses [0-9A-F]{1,6}
        private int ParseUnicodeCodePoint() {
            int codepoint = 0;
            int i = 0;
            while (true) {
                int digit = Tokenizer.ToDigit(Peek());
                if (digit >= 16) {
                    break;
                }

                if (i < 7) {
                    codepoint = (codepoint << 4) | digit;
                }

                i++;
                Skip();
            }

            if (i == 0) {
                throw MakeError("invalid Unicode list");
            }

            // \u{} takes at most six hexadecimal digits; more is a range error even when the
            // leading digits are zeros, which is what MRI reports for \u{0ffffff}.
            if (i > 6) {
                throw MakeError("invalid Unicode range");
            }

            return codepoint;
        }

        private string/*!*/ UnicodeCodePointToString(int codepoint) {
            var sb = new StringBuilder(2);
            AppendUnicodeCodePoint(sb, codepoint);
            return sb.ToString();
        }

        /// <summary>
        /// A character-class member for a single codepoint. Non-BMP codepoints cannot be class
        /// members in .NET (see CharacterSet._astral), so they get their own representation.
        /// </summary>
        private CharacterSet/*!*/ MakeCodePointSet(int codepoint) {
            if (codepoint >= 0xd800 && codepoint <= 0xdfff || codepoint > 0x10ffff) {
                throw MakeError("invalid Unicode range");
            }
            return CharacterSet.MakeCharacter(codepoint, (codepoint >= 0x10000) ? null : UnicodeCodePointToString(codepoint));
        }

        private void AppendUnicodeCodePoint(StringBuilder/*!*/ builder, int codepoint) {
            if (codepoint >= 0xd800 && codepoint <= 0xdfff || codepoint > 0x10ffff) {
                throw MakeError("invalid Unicode range");
            } else if (codepoint < 0x10000) {
                AppendEscaped(builder, codepoint);
            } else {
                codepoint -= 0x10000;
                builder.Append((char)((codepoint / 0x400) + 0xd800));
                builder.Append((char)((codepoint % 0x400) + 0xdc00));
            }
        }

        #endregion

        #region Chracter Groups

        // A character class: its members as code point ranges, so that union, intersection,
        // subtraction and negation are exact - over the whole of Unicode, non-BMP members included.
        //
        // .NET's Regex matches UTF-16 code units, so a non-BMP code point cannot be a member of a
        // .NET character class: [\uD83E\uDD8A] would match either surrogate half on its own. A
        // class with such members is emitted as (?:[bmp members]|<surrogate pairs>), the pairs
        // grouped by leading surrogate, and a BMP class never matches a lone surrogate. The one
        // exception is a * or + loop over a class that holds *every* non-BMP code point - [^"]*,
        // \W+ - which stays a single .NET class loop that takes in the surrogate block, so that
        // the common negated classes are as fast as they were (see AppendLoopBodyTo).
        private sealed class CharacterSet {
            public static readonly CharacterSet Empty = new CharacterSet(CodePointRanges.Empty, false, "", -1, -1);

            private static readonly int[] BmpRange = CodePointRanges.Single(0, 0xffff);
            private static readonly int[] AstralRange = CodePointRanges.Single(0x10000, CodePointRanges.MaxCodePoint);
            private static readonly int[] SurrogateRange = CodePointRanges.Single(0xd800, 0xdfff);

            private const string SurrogateBlock = "\\ud800-\\udfff";
            private const string AnySurrogatePair = "[\\ud800-\\udbff][\\udc00-\\udfff]";

            // The members are _ranges, or every code point but those when _negated. Keeping the
            // complement symbolic lets a negated class be emitted as [\0-\uffff-[...]], which
            // .NET's IgnoreCase folds the way Onigmo does: /[^a]/i does not match "A".
            private readonly int[]/*!*/ _ranges;
            private readonly bool _negated;

            // The body of a .NET character class for _ranges, spelled as the pattern wrote it,
            // while the set is nothing but a union of BMP characters and ranges; null once it is
            // anything else. Keeps the translation of ordinary classes - [a-zA-Z\d_] - as it was.
            private readonly string _text;

            // The code point, if the set was written as a single character (it may bound a range).
            private readonly int _singleCodepoint;

            // The property table the set is (or is the complement of), for the rendering cache.
            private readonly int _table;

            private string _rendered;

            private CharacterSet(int[]/*!*/ ranges, bool negated, string text, int singleCodepoint, int table) {
                _ranges = ranges;
                _negated = negated;
                _text = text;
                _singleCodepoint = singleCodepoint;
                _table = table;
            }

            private CharacterSet(int[]/*!*/ ranges, bool negated)
                : this(ranges, negated, null, -1, -1) {
            }

            /// <summary>A single character; <paramref name="text"/> is its class-body spelling, null for a non-BMP one.</summary>
            internal static CharacterSet/*!*/ MakeCharacter(int codepoint, string text) {
                Debug.Assert((codepoint < 0x10000) == (text != null));
                return new CharacterSet(CodePointRanges.Single(codepoint, codepoint), false, text, codepoint, -1);
            }

            internal static CharacterSet/*!*/ MakeRange(int low, int high, string text) {
                return new CharacterSet(CodePointRanges.Single(low, high), false, text, -1, -1);
            }

            /// <summary>A set of explicit members; <paramref name="text"/> spells the BMP ones, if they all are.</summary>
            internal static CharacterSet/*!*/ MakeSet(int[]/*!*/ ranges, string text) {
                return new CharacterSet(ranges, false, text, -1, -1);
            }

            /// <summary>An Onigmo character property (see UnicodeProperties), or its complement.</summary>
            internal static CharacterSet/*!*/ MakeProperty(int table, bool positive) {
                return new CharacterSet(UnicodeProperties.GetRanges(table), !positive, null, -1, table);
            }

            internal bool IsSingleCharacter {
                get { return _singleCodepoint >= 0; }
            }

            internal int SingleCodepoint {
                get { return _singleCodepoint; }
            }

            /// <summary>The class-body spelling of a BMP single character.</summary>
            internal string Text {
                get { return _text; }
            }

            public bool IsEmpty {
                get { return !_negated && _ranges.Length == 0; }
            }

            internal CharacterSet/*!*/ Complement() {
                return new CharacterSet(_ranges, !_negated, _text, -1, _table);
            }

            internal CharacterSet/*!*/ Union(CharacterSet/*!*/ set) {
                if (ReferenceEquals(this, Empty)) {
                    return set;
                } else if (ReferenceEquals(set, Empty)) {
                    return this;
                }

                if (!_negated && !set._negated) {
                    return new CharacterSet(CodePointRanges.Union(_ranges, set._ranges), false,
                        (_text != null && set._text != null) ? _text + set._text : null, -1, -1);
                } else if (_negated && set._negated) {
                    // ^A or ^B == ^(A and B)
                    return new CharacterSet(CodePointRanges.Intersect(_ranges, set._ranges), true);
                } else if (_negated) {
                    // ^A or B == ^(A \ B)
                    return new CharacterSet(CodePointRanges.Subtract(_ranges, set._ranges), true);
                } else {
                    return set.Union(this);
                }
            }

            internal CharacterSet/*!*/ Intersect(CharacterSet/*!*/ set) {
                if (!_negated && !set._negated) {
                    return new CharacterSet(CodePointRanges.Intersect(_ranges, set._ranges), false);
                } else if (_negated && set._negated) {
                    // ^A and ^B == ^(A or B)
                    return new CharacterSet(CodePointRanges.Union(_ranges, set._ranges), true);
                } else if (set._negated) {
                    // A and ^B == A \ B
                    return new CharacterSet(CodePointRanges.Subtract(_ranges, set._ranges), false);
                } else {
                    return set.Intersect(this);
                }
            }

            internal CharacterSet/*!*/ Subtract(CharacterSet/*!*/ set) {
                return Intersect(set.Complement());
            }

            private static bool HasAstral(int[]/*!*/ ranges) {
                return ranges.Length != 0 && ranges[ranges.Length - 1] >= 0x10000;
            }

            /// <summary>
            /// The class body as written. A body that starts with '^' would be read as a negation,
            /// so a leading '^' is escaped: Ruby's /[^^]/ ("anything but a caret") went through
            /// as [\0-\uffff-[^]] without this.
            /// </summary>
            private void AppendText(StringBuilder/*!*/ sb) {
                if (_text.Length != 0 && _text[0] == '^') {
                    sb.Append("\\^").Append(_text, 1, _text.Length - 1);
                } else {
                    sb.Append(_text);
                }
            }

            /// <summary>
            /// Whether every non-BMP code point is a member - [^a], \W, \P{ASCII} - so that a
            /// loop over the set can be emitted as a single .NET class (see AppendLoopBodyTo).
            /// </summary>
            internal bool HasAllAstral {
                get {
                    int[] astral = CodePointRanges.Intersect(_ranges, AstralRange);
                    return _negated
                        ? astral.Length == 0
                        : astral.Length == 2 && astral[0] == 0x10000 && astral[1] == CodePointRanges.MaxCodePoint;
                }
            }

            /// <summary>
            /// The body of a * or + loop over a set that HasAllAstral, as one .NET class that takes
            /// in the whole surrogate block: a non-BMP character is then matched as two iterations.
            /// That is what keeps [^"]* a class loop - the surrogate-pair alternation AppendTo emits
            /// makes it several times slower - and it is exact as long as the loop never stops
            /// between the two halves of a pair, which the caller ensures by following the loop
            /// with LoopEndGuard. (A greedy loop over such a class cannot stop there on its own:
            /// the class has both halves.)
            /// </summary>
            internal void AppendLoopBodyTo(StringBuilder/*!*/ sb) {
                Debug.Assert(HasAllAstral);
                if (_text != null && _negated) {
                    sb.Append("[\0-\uffff");
                    if (_ranges.Length != 0) {
                        sb.Append("-[");
                        AppendText(sb);
                        sb.Append(']');
                    }
                    sb.Append(']');
                    return;
                }

                int[] bmp = CodePointRanges.Intersect(_ranges, BmpRange);
                if (_negated) {
                    bmp = CodePointRanges.Subtract(bmp, SurrogateRange);
                    sb.Append("[\0-\uffff");
                    if (bmp.Length != 0) {
                        sb.Append("-[");
                        AppendClassBody(sb, bmp);
                        sb.Append(']');
                    }
                    sb.Append(']');
                } else {
                    sb.Append('[');
                    AppendClassBody(sb, CodePointRanges.Union(bmp, SurrogateRange));
                    sb.Append(']');
                }
            }

            /// <summary>Follows a loop emitted by AppendLoopBodyTo: it may not end after a leading surrogate.</summary>
            internal const string LoopEndGuard = "(?<![\\ud800-\\udbff])";

            public StringBuilder/*!*/ AppendTo(StringBuilder/*!*/ sb, bool parenthesize) {
                if (_text != null && !HasAstral(_ranges)) {
                    if (_negated) {
                        // Any BMP character outside the class - but never a lone surrogate, which
                        // would be half of a character - or any non-BMP character, as a whole.
                        sb.Append("(?:[\0-\uffff-[");
                        AppendText(sb);
                        sb.Append(SurrogateBlock).Append("]]|").Append(AnySurrogatePair).Append(')');
                    } else if (_ranges.Length == 0) {
                        sb.Append("[a-[a]]");
                    } else if (IsSingleCharacter && !parenthesize) {
                        // Outside a class a bare '^' is the anchor, so it is escaped here too.
                        AppendText(sb);
                    } else {
                        sb.Append('[');
                        AppendText(sb);
                        sb.Append(']');
                    }
                    return sb;
                }

                if (_rendered == null) {
                    _rendered = (_table >= 0) ? GetCachedRendering(_table, _negated) : Render(_ranges, _negated);
                }
                return sb.Append(_rendered);
            }

            // Renderings of the bare properties, which are large and asked for again and again
            // (every Regexp.new of a pattern that uses one): per table, positive and negated.
            private static readonly Dictionary<int, string> _renderings = new Dictionary<int, string>();

            private static string/*!*/ GetCachedRendering(int table, bool negated) {
                int key = table * 2 + (negated ? 1 : 0);
                lock (_renderings) {
                    string result;
                    if (_renderings.TryGetValue(key, out result)) {
                        return result;
                    }
                }
                string rendered = Render(UnicodeProperties.GetRanges(table), negated);
                lock (_renderings) {
                    _renderings[key] = rendered;
                }
                return rendered;
            }

            private static string/*!*/ Render(int[]/*!*/ ranges, bool negated) {
                int[] bmp = CodePointRanges.Intersect(ranges, BmpRange);
                int[] astral = CodePointRanges.Intersect(ranges, AstralRange);
                var sb = new StringBuilder();

                if (negated) {
                    // The members are the code points *not* in ranges: the BMP ones as the
                    // complement of a class that also takes out the surrogate block (a lone
                    // surrogate would be half of a character), then the non-BMP ones.
                    bmp = CodePointRanges.Union(bmp, SurrogateRange);
                    astral = CodePointRanges.Subtract(AstralRange, astral);

                    if (astral.Length != 0) {
                        sb.Append("(?:");
                    }
                    sb.Append("[\0-\uffff-[");
                    AppendClassBody(sb, bmp);
                    sb.Append("]]");
                    if (astral.Length != 0) {
                        sb.Append('|');
                        AppendSurrogatePairs(sb, astral);
                        sb.Append(')');
                    }
                    return sb.ToString();
                }

                bmp = CodePointRanges.Subtract(bmp, SurrogateRange);
                if (astral.Length == 0) {
                    if (bmp.Length == 0) {
                        return "[a-[a]]";
                    }
                    sb.Append('[');
                    AppendClassBody(sb, bmp);
                    return sb.Append(']').ToString();
                }

                sb.Append("(?:");
                if (bmp.Length != 0) {
                    sb.Append('[');
                    AppendClassBody(sb, bmp);
                    sb.Append("]|");
                }
                AppendSurrogatePairs(sb, astral);
                return sb.Append(')').ToString();
            }

            private static void AppendClassBody(StringBuilder/*!*/ sb, int[]/*!*/ ranges) {
                for (int i = 0; i < ranges.Length; i += 2) {
                    AppendClassCharacter(sb, ranges[i]);
                    if (ranges[i + 1] != ranges[i]) {
                        if (ranges[i + 1] > ranges[i] + 1) {
                            sb.Append('-');
                        }
                        AppendClassCharacter(sb, ranges[i + 1]);
                    }
                }
            }

            private static void AppendClassCharacter(StringBuilder/*!*/ sb, int c) {
                if (c >= '0' && c <= '9' || c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z') {
                    sb.Append((char)c);
                } else {
                    sb.Append("\\u").Append(c.ToString("x4"));
                }
            }

            private static string/*!*/ Unit(int c) {
                return "\\u" + c.ToString("x4");
            }

            /// <summary>
            /// Non-BMP ranges as an alternation of surrogate pairs: [leading][trailing] per set of
            /// leading surrogates that share the same trailing ones. The branches begin with
            /// disjoint leading surrogates, so at most one of them gets past its first code unit.
            /// </summary>
            private static void AppendSurrogatePairs(StringBuilder/*!*/ sb, int[]/*!*/ ranges) {
                // trailing-surrogate ranges (0..0x3ff) per leading surrogate (0..0x3ff)
                var trails = new List<int>[0x400];
                for (int i = 0; i < ranges.Length; i += 2) {
                    int low = ranges[i] - 0x10000, high = ranges[i + 1] - 0x10000;
                    for (int lead = low >> 10; lead <= high >> 10; lead++) {
                        int from = (lead == low >> 10) ? low & 0x3ff : 0;
                        int to = (lead == high >> 10) ? high & 0x3ff : 0x3ff;
                        var list = trails[lead] ?? (trails[lead] = new List<int>());
                        list.Add(from);
                        list.Add(to);
                    }
                }

                // group the leading surrogates by their trailing class, in order of first appearance
                var order = new List<string>();
                var leads = new Dictionary<string, List<int>>();
                for (int lead = 0; lead < 0x400; lead++) {
                    if (trails[lead] == null) {
                        continue;
                    }
                    var trail = new StringBuilder();
                    var list = trails[lead];
                    for (int i = 0; i < list.Count; i += 2) {
                        trail.Append(Unit(0xdc00 + list[i]));
                        if (list[i + 1] != list[i]) {
                            if (list[i + 1] > list[i] + 1) {
                                trail.Append('-');
                            }
                            trail.Append(Unit(0xdc00 + list[i + 1]));
                        }
                    }
                    string key = (list.Count == 2 && list[0] == list[1]) ? trail.ToString() : "[" + trail + "]";
                    List<int> leadList;
                    if (!leads.TryGetValue(key, out leadList)) {
                        leads[key] = leadList = new List<int>();
                        order.Add(key);
                    }
                    leadList.Add(lead);
                }

                for (int k = 0; k < order.Count; k++) {
                    if (k > 0) {
                        sb.Append('|');
                    }
                    var leadList = leads[order[k]];
                    if (leadList.Count == 1) {
                        sb.Append(Unit(0xd800 + leadList[0]));
                    } else {
                        sb.Append('[');
                        for (int i = 0; i < leadList.Count; ) {
                            int j = i;
                            while (j + 1 < leadList.Count && leadList[j + 1] == leadList[j] + 1) {
                                j++;
                            }
                            sb.Append(Unit(0xd800 + leadList[i]));
                            if (j > i) {
                                if (j > i + 1) {
                                    sb.Append('-');
                                }
                                sb.Append(Unit(0xd800 + leadList[j]));
                            }
                            i = j + 1;
                        }
                        sb.Append(']');
                    }
                    sb.Append(order[k]);
                }
            }

            public override string/*!*/ ToString() {
                return IsEmpty ? String.Empty : AppendTo(new StringBuilder(), false).ToString();
            }
        }

        //
        // group ::= '[' negation_opt intersection ']'
        //
        // negation_opt ::= '^' | <empty>
        //
        private CharacterSet/*!*/ ParseCharacterGroup(bool nested, out bool posixClass) {
            Debug.Assert(_rubyPattern[_index - 1] == '[');

            posixClass = false;

            // [:alnum:]
            // [:^alnum:]
            // ([^:alnum:] is not one: it is the class of anything but ':', 'a', 'l', 'n', 'u' and 'm')
            if (nested) {
                var parsed = ParsePosixCharacterClass();
                if (parsed != null) {
                    posixClass = true;
                    return parsed;
                }
            }

            bool positive = !Read('^');

            var result = ParseCharacterGroupIntersections();
            if (!positive) {
                result = result.Complement();
            }
            Debug.Assert(Peek() == -1 || Peek() == ']');
            Read(']');
            return result;
        }

        // 
        // intersection ::= intersection '&' '&' union
        //                | union
        // 
        private CharacterSet/*!*/ ParseCharacterGroupIntersections() {
            CharacterSet result = null;
            int c;
            while ((c = Peek()) != -1 && c != ']') {
                // eats &&
                var set = ParseCharacterGroupUnion();

                // result = result and set
                result = (result != null) ? result.Intersect(set) : set;
            }

            if (result == null) {
                throw MakeError((c == -1) ? "premature end of char-class" : "empty char-class");
            }

            return result;
        }

        //
        // union ::= union term
        //         | term
        //
        // term ::= escape
        //        | group
        //        | posix_character_class
        //        | character '-' character
        //        | character
        //
        // posix_character_class ::= '[' negation_opt ':' posix_character_class_name ':' ']' 
        //
        private CharacterSet ParseCharacterGroupUnion() {
            CharacterSet result = CharacterSet.Empty;

            // \u{1 2 3} produces multiple characters, the first and the last might be range bounds:
            IEnumerator<int> codepoints = null;

            while (true) {
                bool mayStartRange;
                var set = ParseCharacter(ref codepoints, out mayStartRange);
                if (set == null) {
                    break;
                }

                if (codepoints == null && Read('-')) {
                    // A '-' straight after a nested character class is a literal member of the
                    // outer class, not the start of a range - MRI reads [[[:alnum:]]-_.], which is
                    // kramdown's autolink pattern, as alnum plus '-', '_' and '.'. (A '-' after
                    // \p{...} is an error there, and still is below: mayStartRange is false only
                    // for the nested-class case.)
                    if (!mayStartRange) {
                        result = result.Union(set).Union(CharacterSet.MakeCharacter('-', @"\-"));
                        continue;
                    }

                    // [a-]
                    // [a-&&b]
                    bool mayEndRange;
                    var rangeEnd = ParseCharacter(ref codepoints, out mayEndRange);
                    if (rangeEnd == null) {
                        result = result.Union(set).Union(CharacterSet.MakeCharacter('-', @"\-"));
                        break;
                    }

                    // [a-b]-z
                    // \p{L}-z
                    if (!mayStartRange || !set.IsSingleCharacter) {
                        throw MakeError("unmatched range specifier in char-class");
                    }

                    // a-[a-z]
                    // a-\p{L}
                    if (!mayEndRange || !rangeEnd.IsSingleCharacter) {
                        throw MakeError("char-class value at end of range");
                    }

                    int low = set.SingleCodepoint, high = rangeEnd.SingleCodepoint;
                    if (low > high) {
                        throw MakeError("empty range in char class");
                    }

                    // A range reaching past U+FFFF has non-BMP members, which CharacterSet emits
                    // as surrogate pairs. ActionView's token pattern is one of these:
                    // [0-9A-Za-z_\u0080-\u{10ffff}-].
                    set = CharacterSet.MakeRange(low, high, (high < 0x10000) ? set.Text + "-" + rangeEnd.Text : null);
                }

                result = result.Union(set);
            }
            return result;
        }

        private CharacterSet ParseCharacter(ref IEnumerator<int> codepoints, out bool mayStartRange) {
            if (codepoints != null) {
                mayStartRange = true;
                int current = codepoints.Current;
                if (!codepoints.MoveNext()) {
                    codepoints = null;
                }
                return MakeCodePointSet(current);
            }

            int c;
            switch (c = Read()) {
                case -1:
                    throw MakeError("premature end of char-class");

                case ']':
                    Back();
                    mayStartRange = false;
                    return null;

                case '&':
                    if (Read('&')) {
                        mayStartRange = false;
                        return null;
                    }
                    goto default;

                case '\\':
                    int escape = Read();
                    if (escape == 'u') {
                        int codepoint;
                        if (Peek() == '{') {
                            codepoints = ParseUnicodeEscapeList().GetEnumerator();
                            if (!codepoints.MoveNext()) {
                                throw MakeError("invalid Unicode list");
                            }
                            codepoint = codepoints.Current;
                            if (!codepoints.MoveNext()) {
                                codepoints = null;
                            }
                        } else {
                            codepoint = ParseUnicodeEscape();
                        }
                        mayStartRange = true;
                        return MakeCodePointSet(codepoint);
                    } else {
                        mayStartRange = true;
                        return ParseCharacterEscape(escape);
                    }

                case '[': {
                    // A POSIX class - [:alnum:] - may not be a range bound, and MRI says so;
                    // a nested class - [[:alnum:]] or [a-b] - may not either, but there MRI
                    // reads the '-' after it as an ordinary member instead of complaining.
                    // mayStartRange tells the two apart for the caller.
                    bool posixClass;
                    var group = ParseCharacterGroup(true, out posixClass);
                    mayStartRange = posixClass;
                    return group;
                }

                case '-':
                    // warning: character class has '-' without escape
                    mayStartRange = true;
                    return CharacterSet.MakeCharacter('-', @"\-");

                default:
                    mayStartRange = true;
                    if (c >= 0xd800 && c <= 0xdbff && Peek() >= 0xdc00 && Peek() <= 0xdfff) {
                        // a literal non-BMP character, written as its surrogate pair
                        return MakeCodePointSet(Char.ConvertToUtf32((char)c, (char)Read()));
                    }
                    return CharacterSet.MakeCharacter(c, ((char)c).ToString());
            }
        }

        //
        //  \p{property-name}
        //  \p{^property-name}    (negative)
        //  \P{property-name}     (negative)
        //
        // Every property Onigmo knows - the general categories (L, Lu, Letter...), the scripts
        // (Han, Hiragana, Greek, Grek...), the binary properties (Emoji, Emoji_Presentation,
        // Extended_Pictographic, Alphabetic, White_Space...), the blocks (In_Greek_and_Coptic),
        // Age=N.N, Any, Assigned and the POSIX names (Alpha, Word, Punct...) - is spelled out from
        // Onigmo's own tables (UnicodeProperties), never handed to .NET's \p{...}: that knows only
        // the general categories and some BMP blocks, of an older Unicode version, and none of it
        // outside the BMP. Onigmo matches the name ignoring case, ' ', '-' and '_'. (?a) does not
        // restrict a property - it restricts the POSIX brackets and \w, \d, \s only.
        //
        private CharacterSet/*!*/ ParseCharacterCategoryName(int escape) {
            bool positive = escape == 'p';

            int c = Peek();
            if (c != '{') {
                throw MakeError("invalid Unicode property");
            }
            Skip();

            if (Peek() == '^') {
                positive = !positive;
                Skip();
            }

            int start = _index;

            while ((c = Peek()) != '}' && c != -1) {
                Skip();
            }

            // trailing }
            if (c == -1) {
                throw MakeError("invalid character property name {}");
            }

            string name = _rubyPattern.Substring(start, _index - start);
            Skip();

            string normalized = UnicodeProperties.NormalizeName(name);
            int table = (normalized != null) ? UnicodeProperties.Find(normalized) : -1;
            if (table < 0) {
                throw MakeError("invalid character property name {" + name + "}");
            }
            return CharacterSet.MakeProperty(table, positive);
        }

        // [:xxx:] in character class
        // [^:xxx:] in character class
        private CharacterSet ParsePosixCharacterClass() {
            int i = 0;
            if (Peek(i) == ':') {
                i++;
            } else {
                return null;
            }

            bool positive = true;
            if (Peek(i) == '^') {
                positive = false;
                i++;
            }

            int start = _index + i;

            int c;
            while ((c = Peek(i)) != ':' && c != -1) {
                i++;
            }

            if (c == -1 || Peek(i + 1) != ']') {
                return null;
            }

            string name = _rubyPattern.Substring(start, _index + i - start);
            _index += i + 2;

            int table = UnicodeProperties.FindPosix(name);
            if (table < 0) {
                throw MakeError("invalid POSIX bracket type");
            }
            return MakePosixCharacterClass(table, positive);
        }

        private static readonly int[] _asciiRange = new int[] { 0, 0x7f };

        /// <summary>[[:name:]]: Onigmo's property of that name, all of Unicode unless (?a) is in effect.</summary>
        private CharacterSet/*!*/ MakePosixCharacterClass(int table, bool positive) {
            var result = CharacterSet.MakeProperty(table, true);
            if (_characterClassMode == CharacterClassMode.Ascii) {
                // (?a) restricts the POSIX classes to ASCII. A negated class still matches
                // non-ASCII: (?a)[[:^alpha:]] accepts a Hiragana character.
                result = result.Intersect(CharacterSet.MakeSet(_asciiRange, null));
            }
            return positive ? result : result.Complement();
        }

        #endregion
    }
}

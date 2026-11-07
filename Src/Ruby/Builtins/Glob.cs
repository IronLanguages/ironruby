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
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using System.IO;

namespace IronRuby.Builtins {
    public static class Glob {
        /// <summary>
        /// File::FNM_EXTGLOB. Brace expansion is unconditional in Dir.glob, so the flag only
        /// makes a difference to File.fnmatch?.
        /// </summary>
        public static int FnmExtGlob { get { return Constants.FNM_EXTGLOB; } }

        /// <summary>File::FNM_SYSCASE - see <see cref="Constants"/>.</summary>
        public static int FnmSysCase { get { return Constants.FNM_SYSCASE; } }

        // Duplicated constants from File.Constants
        internal static class Constants {
            public readonly static int FNM_CASEFOLD = 0x08;
            public readonly static int FNM_DOTMATCH = 0x04;
            public readonly static int FNM_NOESCAPE = 0x01;
            public readonly static int FNM_PATHNAME = 0x02;
            public readonly static int FNM_EXTGLOB = 0x10;

            /// <summary>
            /// File::FNM_SYSCASE is FNM_CASEFOLD on a case insensitive file system and 0
            /// elsewhere. It used to be hard-wired to FNM_CASEFOLD, which is right on Windows
            /// but made every glob and fnmatch? on Unix case insensitive, so that Dir["*.txt"]
            /// picked up FILE.TXT.
            /// </summary>
            public readonly static int FNM_SYSCASE =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                    ? 0x08 : 0;
        }

        private class CharClass {
            private readonly StringBuilder/*!*/ _chars = new StringBuilder();
            private bool _negated;
            private bool _empty = true;

            /// <summary>
            /// A leading '!' or '^' negates the set; Ruby accepts both spellings. Only '^' used
            /// to work, and only by accident - it was emitted literally and the surrounding
            /// "[" ... "]" turned it into a CLR negation - so "[!f]" matched an 'f' instead of
            /// excluding it.
            /// </summary>
            internal void Add(char c) {
                if (_empty && (c == '!' || c == '^')) {
                    _negated = true;
                    _empty = false;
                    return;
                }
                _empty = false;
                _chars.Append(c);
            }

            private static void AppendLiteral(StringBuilder/*!*/ builder, char c) {
                if (c == ']' || c == '\\' || c == '^' || c == '-' || c == '[') {
                    builder.Append('\\');
                }
                builder.Append(c);
            }

            internal string MakeString() {
                if (_chars.Length == 0) {
                    return null;
                }

                var result = new StringBuilder(_negated ? "[^" : "[");
                for (int i = 0; i < _chars.Length; i++) {
                    // "a-z" is a range; "z-a" is not - glob(7) leaves a reversed range
                    // undefined and CRuby falls back to three literal characters, while
                    // the CLR regex engine would throw.
                    if (i + 2 < _chars.Length && _chars[i + 1] == '-' && _chars[i] <= _chars[i + 2]) {
                        AppendLiteral(result, _chars[i]);
                        result.Append('-');
                        AppendLiteral(result, _chars[i + 2]);
                        i += 2;
                    } else {
                        AppendLiteral(result, _chars[i]);
                    }
                }
                result.Append(']');
                return result.ToString();
            }
        }

        private static void AppendExplicitRegexChar(StringBuilder/*!*/ builder, char c) {
            builder.Append('[');
            if (c == '^' || c == '\\') {
                builder.Append('\\');
            }
            builder.Append(c);
            builder.Append(']');
        }

        internal static string/*!*/ PatternToRegex(string/*!*/ pattern, bool pathName, bool noEscape) {
            return PatternToRegex(pattern, pathName, noEscape, false);
        }

        /// <param name="noLeadingDot">
        /// Under FNM_PATHNAME without FNM_DOTMATCH a wildcard must not match a leading '.'
        /// of *any* path segment, not just of the whole path.
        /// </param>
        internal static string/*!*/ PatternToRegex(string/*!*/ pattern, bool pathName, bool noEscape, bool noLeadingDot) {
            StringBuilder result = new StringBuilder(pattern.Length);
            result.Append("\\G");

            bool inEscape = false;
            CharClass charClass = null;

            // Where in `result` the current run of '*' started and how long the run is, but
            // only while that run begins a path segment. That is what lets "**/" be spotted
            // and widened below.
            int starRunStart = -1;
            int starRun = 0;
            bool atSegmentStart = true;

            for (int index = 0; index < pattern.Length; index++) {
                char c = pattern[index];

                if (noLeadingDot && atSegmentStart && !inEscape && !StartsWithLiteralDot(pattern, index, noEscape)) {
                    result.Append("(?![.])");
                }

                if (inEscape) {
                    if (charClass != null) {
                        charClass.Add(c);
                    } else {
                        AppendExplicitRegexChar(result, c);
                    }
                    inEscape = false;
                    continue;
                } else if (c == '\\' && !noEscape) {
                    inEscape = true;
                    continue;
                }

                if (charClass != null) {
                    if (c == ']') {
                        string set = charClass.MakeString();
                        if (set == null) {
                            // Ruby regex "[]" matches nothing
                            // CLR regex "[]" throws exception
                            return String.Empty;
                        }
                        if (pathName) {
                            // Under FNM_PATHNAME a bracket expression never matches the
                            // separator, not even when it lists or negates it.
                            result.Append("(?![/])");
                        }
                        result.Append(set);
                        charClass = null;
                    } else {
                        charClass.Add(c);
                    }
                    continue;
                }
                switch (c) {
                    case '*':
                        if (starRun == 0) {
                            starRunStart = atSegmentStart ? result.Length : -1;
                        }
                        starRun++;
                        result.Append(pathName ? "[^/]*" : ".*");
                        atSegmentStart = false;
                        continue;

                    case '?':
                        result.Append(pathName ? "[^/]" : ".");
                        break;

                    case '[':
                        charClass = new CharClass();
                        break;

                    case '/':
                        // "**/" is the one construct that crosses a separator under
                        // FNM_PATHNAME, and it matches zero segments as well:
                        // File.fnmatch?("a/**/b", "a/b", File::FNM_PATHNAME) is true.
                        if (pathName && starRun == 2 && starRunStart >= 0) {
                            result.Length = starRunStart;
                            // "**/" spans whole segments, each still subject to the
                            // leading-dot rule.
                            result.Append(noLeadingDot ? "(?:(?![.])[^/]*[/])*" : "(?:.*[/])?");
                        } else {
                            AppendExplicitRegexChar(result, c);
                        }
                        starRun = 0;
                        starRunStart = -1;
                        atSegmentStart = true;
                        continue;

                    default:
                        AppendExplicitRegexChar(result, c);
                        break;
                }
                starRun = 0;
                starRunStart = -1;
                atSegmentStart = false;
            }

            return (charClass == null) ? result.ToString() : String.Empty;
        }

        /// <summary>
        /// True when the pattern at <paramref name="index"/> is a literal '.', written
        /// plainly or escaped.
        /// </summary>
        private static bool StartsWithLiteralDot(string/*!*/ pattern, int index, bool noEscape) {
            if (pattern[index] == '.') {
                return true;
            }
            return !noEscape && pattern[index] == '\\' && index + 1 < pattern.Length && pattern[index + 1] == '.';
        }

        public static bool FnMatch(string/*!*/ pattern, string/*!*/ path, int flags) {
            return GetFnMatcher(pattern, flags).IsMatch(path);
        }

        /// <summary>
        /// A pattern of File.fnmatch? / Dir.glob translated once for a given set of flags, so that
        /// matching it against many names - every entry of a directory being globbed, or the same
        /// pattern asked about again and again - does not translate it (and look its regex up)
        /// for every name.
        /// </summary>
        private sealed class FnMatcher {
            internal readonly string/*!*/ Pattern;
            internal readonly int Flags;

            // Another matcher for the same pattern under different flags (the cache chains them).
            internal FnMatcher Next;

            // Brace alternatives under FNM_EXTGLOB: the pattern matches when any one does.
            private readonly FnMatcher[] _alternatives;

            // Null when nothing but the empty path (empty pattern) or nothing at all can match.
            // The regex is only built when a name gets as far as needing it, as before (a name
            // with a leading dot the pattern rejects never did).
            private readonly string _regexPattern;
            private readonly RegexOptions _regexOptions;
            private Regex _regex;
            private readonly bool _matchesOnlyEmpty;

            // Without FNM_PATHNAME and FNM_DOTMATCH a leading dot in the path has to be matched
            // by an explicit dot in the pattern.
            private readonly bool _rejectsLeadingDot;

            internal FnMatcher(string/*!*/ pattern, int flags) {
                Pattern = pattern;
                Flags = flags;

                if (pattern.Length == 0) {
                    _matchesOnlyEmpty = true;
                    return;
                }

                if ((flags & Constants.FNM_EXTGLOB) != 0 && pattern.IndexOf('{') >= 0) {
                    string[] groups = UngroupGlobs(pattern, (flags & Constants.FNM_NOESCAPE) != 0);
                    _alternatives = new FnMatcher[groups.Length];
                    for (int i = 0; i < groups.Length; i++) {
                        _alternatives[i] = new FnMatcher(groups[i], flags & ~Constants.FNM_EXTGLOB);
                    }
                    return;
                }

                bool pathName = ((flags & Constants.FNM_PATHNAME) != 0);
                bool noEscape = ((flags & Constants.FNM_NOESCAPE) != 0);
                bool noLeadingDot = pathName && (flags & Constants.FNM_DOTMATCH) == 0;
                string regexPattern = PatternToRegex(pattern, pathName, noEscape, noLeadingDot);
                if (regexPattern.Length == 0) {
                    return;
                }

                if (!pathName && ((flags & Constants.FNM_DOTMATCH) == 0)) {
                    // Starting dot requires an explicit dot in the pattern
                    _rejectsLeadingDot = regexPattern.Length < 4 || regexPattern[2] != '[' || regexPattern[3] != '.';
                }

                RegexOptions options = RegexOptions.None;
                if ((flags & Constants.FNM_CASEFOLD) != 0) {
                    // MRI folds case the same in every locale
                    options |= RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
                }
                _regexPattern = regexPattern;
                _regexOptions = options;
            }

            internal bool IsMatch(string/*!*/ path) {
                if (_alternatives != null) {
                    foreach (FnMatcher alternative in _alternatives) {
                        if (alternative.IsMatch(path)) {
                            return true;
                        }
                    }
                    return false;
                }

                if (_regexPattern == null) {
                    return _matchesOnlyEmpty && path.Length == 0;
                }

                if (_rejectsLeadingDot && path.Length > 0 && path[0] == '.') {
                    return false;
                }

                // a race only builds the same regex twice
                Regex regex = _regex ?? (_regex = new Regex(_regexPattern, _regexOptions));
                Match match = regex.Match(path);
                return match.Success && (match.Length == path.Length);
            }
        }

        // The matchers of the last patterns matched, by pattern; the ones for the same pattern
        // under other flags hang off FnMatcher.Next. Not the static Regex.Match cache: its key is
        // a struct, and the ConcurrentDictionary code for that key is JIT-compiled the first time
        // a process globs (rubygems does at startup).
        private static readonly Dictionary<string, FnMatcher>/*!*/ _fnMatchers = new Dictionary<string, FnMatcher>();
        private const int FnMatcherCacheSize = 32;

        private static FnMatcher/*!*/ GetFnMatcher(string/*!*/ pattern, int flags) {
            FnMatcher first;
            lock (_fnMatchers) {
                if (_fnMatchers.TryGetValue(pattern, out first)) {
                    for (FnMatcher m = first; m != null; m = m.Next) {
                        if (m.Flags == flags) {
                            return m;
                        }
                    }
                }
            }

            var matcher = new FnMatcher(pattern, flags);
            lock (_fnMatchers) {
                if (_fnMatchers.Count >= FnMatcherCacheSize) {
                    _fnMatchers.Clear();
                }
                FnMatcher existing;
                matcher.Next = _fnMatchers.TryGetValue(pattern, out existing) ? existing : null;
                _fnMatchers[pattern] = matcher;
            }
            return matcher;
        }

        private class GlobUngrouper {
            internal abstract class GlobNode {
                internal readonly GlobNode/*!*/ _parent;
                protected GlobNode(GlobNode parentNode) {
                    _parent = parentNode ?? this;
                }
                abstract internal GlobNode/*!*/ AddChar(char c);
                abstract internal GlobNode/*!*/ StartLevel();
                abstract internal GlobNode/*!*/ AddGroup();
                abstract internal GlobNode/*!*/ FinishLevel();
                abstract internal List<StringBuilder>/*!*/ Flatten();
            }

            internal class TextNode : GlobNode {
                private readonly StringBuilder/*!*/ _builder;

                internal TextNode(GlobNode/*!*/ parentNode)
                    : base(parentNode) {
                    _builder = new StringBuilder();
                }
                internal override GlobNode/*!*/ AddChar(char c) {
                    if (c != 0) {
                        _builder.Append(c);
                    }
                    return this;
                }
                internal override GlobNode/*!*/ StartLevel() {
                    return _parent.StartLevel();
                }
                internal override GlobNode/*!*/ AddGroup() {
                    return _parent.AddGroup();
                }
                internal override GlobNode/*!*/ FinishLevel() {
                    return _parent.FinishLevel();
                }
                internal override List<StringBuilder>/*!*/ Flatten() {
                    List<StringBuilder> result = new List<StringBuilder>(1);
                    result.Add(_builder);
                    return result;
                }
            }

            internal class ChoiceNode : GlobNode {
                private readonly List<SequenceNode>/*!*/ _nodes;

                internal ChoiceNode(GlobNode/*!*/ parentNode)
                    : base(parentNode) {
                    _nodes = new List<SequenceNode>();
                }
                internal override GlobNode/*!*/ AddChar(char c) {
                    SequenceNode node = new SequenceNode(this);
                    _nodes.Add(node);
                    return node.AddChar(c);
                }
                internal override GlobNode/*!*/ StartLevel() {
                    SequenceNode node = new SequenceNode(this);
                    _nodes.Add(node);
                    return node.StartLevel();
                }
                internal override GlobNode/*!*/ AddGroup() {
                    AddChar('\0');
                    return this;
                }
                internal override GlobNode/*!*/ FinishLevel() {
                    AddChar('\0');
                    return _parent;
                }
                internal override List<StringBuilder>/*!*/ Flatten() {
                    List<StringBuilder> result = new List<StringBuilder>();
                    foreach (GlobNode node in _nodes) {
                        foreach (StringBuilder builder in node.Flatten()) {
                            result.Add(builder);
                        }
                    }
                    return result;
                }
            }

            internal class SequenceNode : GlobNode {
                private readonly List<GlobNode>/*!*/ _nodes;

                internal SequenceNode(GlobNode parentNode)
                    : base(parentNode) {
                    _nodes = new List<GlobNode>();
                }

                internal override GlobNode/*!*/ AddChar(char c) {
                    TextNode node = new TextNode(this);
                    _nodes.Add(node);
                    return node.AddChar(c);
                }

                internal override GlobNode/*!*/ StartLevel() {
                    ChoiceNode node = new ChoiceNode(this);
                    _nodes.Add(node);
                    return node;
                }

                internal override GlobNode/*!*/ AddGroup() {
                    return _parent;
                }

                internal override GlobNode/*!*/ FinishLevel() {
                    return _parent._parent;
                }

                internal override List<StringBuilder>/*!*/ Flatten() {
                    List<StringBuilder> result = new List<StringBuilder>();
                    result.Add(new StringBuilder());
                    foreach (GlobNode node in _nodes) {
                        List<StringBuilder> tmp = new List<StringBuilder>();
                        List<StringBuilder> alternatives = node.Flatten();
                        // The accumulated prefixes are the *outer* loop so that the left-most
                        // group varies slowest: "a{.js,.html}{.erb,.rjs}" expands in the order
                        // a.js.erb, a.js.rjs, a.html.erb, a.html.rjs. Iterating the
                        // alternatives outermost reversed that.
                        foreach (StringBuilder sb in result) {
                            foreach (StringBuilder builder in alternatives) {
                                StringBuilder newsb = new StringBuilder(sb.ToString());
                                newsb.Append(builder.ToString());
                                tmp.Add(newsb);
                            }
                        }
                        result = tmp;
                    }
                    return result;
                }
            }

            private readonly SequenceNode/*!*/ _rootNode;
            private GlobNode/*!*/ _currentNode;
            private int _level;

            internal GlobUngrouper(int patternLength) {
                _rootNode = new SequenceNode(null);
                _currentNode = _rootNode;
                _level = 0;
            }

            internal void AddChar(char c) {
                _currentNode = _currentNode.AddChar(c);
            }

            internal void StartLevel() {
                _currentNode = _currentNode.StartLevel();
                _level++;
            }

            internal void AddGroup() {
                _currentNode = _currentNode.AddGroup();
            }

            internal void FinishLevel() {
                _currentNode = _currentNode.FinishLevel();
                _level--;
            }
            internal int Level {
                get { return _level; }
            }
            internal string[]/*!*/ Flatten() {
                if (_level != 0) {
                    return ArrayUtils.EmptyStrings;
                }
                List<StringBuilder> list = _rootNode.Flatten();
                string[] result = new string[list.Count];
                for (int i = 0; i < list.Count; i++) {
                    result[i] = list[i].ToString();
                }
                return result;
            }
        }

        private static string[] UngroupGlobs(string/*!*/ pattern, bool noEscape) {
            GlobUngrouper ungrouper = new GlobUngrouper(pattern.Length);

            bool inEscape = false;
            foreach (char c in pattern) {
                if (inEscape) {
                    // The backslash is kept even before ',', '{' and '}' so that the escape
                    // survives into PatternToRegex. Dropping it made Dir["special/\\{}/x"]
                    // look like an unbalanced '}' at brace level 0, which matches nothing.
                    ungrouper.AddChar('\\');
                    ungrouper.AddChar(c);
                    inEscape = false;
                    continue;
                } else if (c == '\\' && !noEscape) {
                    inEscape = true;
                    continue;
                }

                switch (c) {
                    case '{':
                        ungrouper.StartLevel();
                        break;

                    case ',':
                        if (ungrouper.Level < 1) {
                            ungrouper.AddChar(c);
                        } else {
                            ungrouper.AddGroup();
                        }
                        break;

                    case '}':
                        if (ungrouper.Level < 1) {
                            // A '}' with no '{' open is an ordinary character. Treating it as
                            // "matches nothing" broke Dir["special/\\{}/special"], where the
                            // '{' is escaped and so never opened a level.
                            ungrouper.AddChar(c);
                        } else {
                            ungrouper.FinishLevel();
                        }
                        break;

                    default:
                        ungrouper.AddChar(c);
                        break;
                }
            }
            return ungrouper.Flatten();
        }

        private sealed class GlobMatcher {
            private readonly PlatformAdaptationLayer/*!*/ _pal;
            private readonly string/*!*/ _pattern;
            private readonly int _flags;
            private readonly bool _dirOnly;
            private readonly bool _sort;
            private readonly string _base;
            private readonly List<string>/*!*/ _result;

            /// <summary>
            /// Number of leading characters to cut off every result. Matching always runs from
            /// a concrete base directory - "." by default, or the `base:` option - and that
            /// prefix plus its separator is not part of what Ruby returns.
            /// </summary>
            private int _stripPrefix;

            private bool NoEscapes {
                get { return ((_flags & Constants.FNM_NOESCAPE) != 0); }
            }

            internal GlobMatcher(PlatformAdaptationLayer/*!*/ pal, string/*!*/ pattern, int flags, string baseDirectory, bool sort) {
                _pal = pal;
                _pattern = pattern;
                // Dir.glob ignores FNM_CASEFOLD - MRI honours it in File.fnmatch? only - and
                // uses FNM_SYSCASE, which is 0 on a case sensitive file system.
                _flags = (flags & ~Constants.FNM_CASEFOLD) | Constants.FNM_SYSCASE;
                _base = baseDirectory;
                _sort = sort;
                _result = new List<string>();
                _dirOnly = _pattern.LastCharacter() == '/';
                _stripPrefix = 0;
            }

            internal int FindNextSeparator(int position, bool allowWildcard, out bool containsWildcard) {
                int lastSlash = -1;
                bool inEscape = false;
                containsWildcard = false;
                for (int i = position; i < _pattern.Length; i++) {
                    if (inEscape) {
                        inEscape = false;
                        continue;
                    }
                    char c = _pattern[i];
                    if (c == '\\') {
                        inEscape = true;
                        continue;
                    } else if (c == '*' || c == '?' || c == '[') {
                        if (!allowWildcard) {
                            return lastSlash + 1;
                        } else if (lastSlash >= 0) {
                            return lastSlash;
                        }
                        containsWildcard = true;
                    } else if (c == '/' || c == ':') {
                        if (containsWildcard) {
                            return i;
                        }
                        lastSlash = i;
                    }
                }
                return _pattern.Length;
            }

            private void TestPath(string path, int patternEnd, bool isLastPathSegment, bool atBase) {
                if (!isLastPathSegment) {
                    DoGlob(path, patternEnd, false, atBase);
                    return;
                }

                // The existence check has to use the full path; only the *result* has the base
                // prefix removed. Stripping first happened to work while the prefix was always
                // "./", but with `base: "sub"` it asked the file system about "x" instead of
                // "sub/x" and every match was discarded.
                // path is a real file name by now: escapes were taken out of the literal parts of
                // the pattern as they were joined on, and wildcard matches are names as listed.
                string full = path;
                string match = (_stripPrefix > 0 && full.Length >= _stripPrefix) ? full.Substring(_stripPrefix) : full;
                if (match.Length == 0) {
                    // The base directory itself is not a match, so Dir["**/"] does not lead
                    // with an empty string. The one exception is a directory-only pattern
                    // under an explicit base:, where MRI does report it - as "/", the part of
                    // the path left after the base is taken off:
                    //   Dir.glob('**/', base: "deeply/nested") == ["/", "directory/", ...]
                    if (_base == null || !_dirOnly) {
                        return;
                    }
                    match = "/";
                }

                if (_pal.DirectoryExists(full)) {
                    _result.Add(match);
                } else if (!_dirOnly && _pal.FileExists(full)) {
                    _result.Add(match);
                }
            }

            /// <summary>
            /// PlatformAdaptationLayer has no notion of links, so this goes straight to
            /// System.IO. A PAL over a virtual file system simply reports no symlinks, which
            /// is the pre-existing behaviour.
            /// </summary>
            private static bool IsSymbolicLinkToDirectory(string/*!*/ path) {
                try {
                    var info = new DirectoryInfo(path);
                    return info.Exists && info.LinkTarget != null;
                } catch (Exception) {
                    return false;
                }
            }

            /// <summary>Joins a directory and a name without doubling the separator at the root.</summary>
            private static string/*!*/ Combine(string/*!*/ directory, string/*!*/ name) {
                return directory.EndsWith("/", StringComparison.Ordinal) ? directory + name : directory + "/" + name;
            }

            private static string/*!*/ Unescape(string/*!*/ path, int start) {
                StringBuilder unescaped = new StringBuilder();
                bool inEscape = false;
                for (int i = start; i < path.Length; i++) {
                    char c = path[i];
                    if (inEscape) {
                        inEscape = false;
                    } else if (c == '\\') {
                        inEscape = true;
                        continue;
                    }
                    unescaped.Append(c);
                }

                if (inEscape) {
                    unescaped.Append('\\');
                }

                return unescaped.ToString();
            }

            internal IList<string>/*!*/ DoGlob() {
                if (_pattern.Length == 0) {
                    return ArrayUtils.EmptyStrings;
                }

                int pos = 0;
                string baseDirectory = null;
                if (_pattern[0] == '/' || _pattern.IndexOf(':') >= 0) {
                    bool containsWildcard;
                    pos = FindNextSeparator(0, false, out containsWildcard);
                    if (pos == _pattern.Length) {
                        TestPath(NoEscapes ? _pattern : Unescape(_pattern, 0), pos, true, true);
                        return _result;
                    }
                    if (pos > 0 || _pattern[0] == '/') {
                        baseDirectory = _pattern.Substring(0, pos);
                        if (!NoEscapes) {
                            baseDirectory = Unescape(baseDirectory, 0);
                        }
                    }
                }

                if (baseDirectory == null) {
                    // A relative pattern is matched against `base:` when one was given, and the
                    // base is not part of the result - "*" with base "a/b" yields "c", not
                    // "a/b/c". MRI ignores base: for an absolute pattern, which is why this
                    // only runs when the pattern gave us no base of its own.
                    baseDirectory = String.IsNullOrEmpty(_base) ? "." : _base;
                    // The entry name is appended to the base with a separator between,
                    // unless the base already ends in one - "a/" and "a" both produce
                    // "a/b", so a trailing separator leaves nothing extra to strip.
                    _stripPrefix = baseDirectory.Length +
                        (baseDirectory[baseDirectory.Length - 1] == '/' ? 0 : 1);
                }

                DoGlob(baseDirectory, pos, false, true);
                return _result;
            }

            /// <param name="atBase">
            /// True while baseDirectory is still the directory named by the pattern's literal
            /// prefix, i.e. no wildcard has been stepped through yet. Only there does "."
            /// count as an entry: Dir["a/**/*", File::FNM_DOTMATCH] yields "a/." but not
            /// "a/b/.".
            /// </param>
            internal void DoGlob(string/*!*/ baseDirectory, int position, bool isPreviousDoubleStar, bool atBase) {
                if (!_pal.DirectoryExists(baseDirectory)) {
                    return;
                }

                bool containsWildcard;
                int patternEnd = FindNextSeparator(position, true, out containsWildcard);
                bool isLastPathSegment = (patternEnd == _pattern.Length);
                string dirSegment = _pattern.Substring(position, patternEnd - position);

                if (!isLastPathSegment) {
                    patternEnd++;
                }

                if (!containsWildcard) {
                    string literal = NoEscapes ? dirSegment : Unescape(dirSegment, 0);
                    string path = Combine(baseDirectory, literal);
                    // MRI keeps repeated separators: "a//*" gives "a//b", and the literal part
                    // ends in one of them ("a/") when the next part has a wildcard.
                    if (!isLastPathSegment && literal.EndsWith("/", StringComparison.Ordinal)) {
                        path += "/";
                    }
                    TestPath(path, patternEnd, isLastPathSegment, atBase);
                    return;
                }

                // "**" only recurses when another path segment follows it. A trailing "**" is
                // just "*": Dir["dir/**"] is ["dir/f1", "dir/f2", "dir/sub"], not the whole
                // subtree. This used to be special-cased for the pattern "**" on its own.
                bool doubleStar = dirSegment.Equals("**") && !isLastPathSegment;
                if (doubleStar && !isPreviousDoubleStar) {
                    DoGlob(baseDirectory, patternEnd, true, atBase);
                }

                IEnumerable<string> entries = _pal.GetFileSystemEntries(baseDirectory, "*");
                if (_sort) {
                    // MRI sorts the entries of each directory as it walks into it rather than
                    // sorting the final list, so "{zz,aa}/*" keeps the brace alternatives in
                    // pattern order while sorting within each of them.
                    var sorted = new List<string>(entries);
                    sorted.Sort(StringComparer.Ordinal);
                    entries = sorted;
                }

                FnMatcher segmentMatcher = GetFnMatcher(dirSegment, _flags);
                foreach (string file in entries) {
                    string objectName = Path.GetFileName(file);
                    if (segmentMatcher.IsMatch(objectName)) {
                        var canon = RubyUtils.CanonicalizePath(file);
                        if (doubleStar && IsSymbolicLinkToDirectory(canon)) {
                            // A recursive "**" neither reports a symlinked directory nor walks
                            // into it - otherwise Dir["**/"] both lists "special/ln/" and
                            // reports everything below it a second time.
                            continue;
                        }
                        TestPath(canon, patternEnd, isLastPathSegment, false);
                        if (doubleStar) {
                            DoGlob(canon, position, true, false);
                        }
                    }
                }
                // "**" never matches "." as a component - that would re-glob the same
                // directory one level down and report every match twice.
                if (atBase && !doubleStar &&
                    ((_flags & Constants.FNM_DOTMATCH) != 0 || (dirSegment[0] == '.' && !isPreviousDoubleStar))) {
                    // "." is a legitimate glob result but ".." is not: Dir[".*"] is
                    // [".", ".dotfile", ...] with no "..".
                    if (segmentMatcher.IsMatch(".")) {
                        string directory = Combine(baseDirectory, ".");
                        if (isLastPathSegment && _dirOnly) {
                            directory += '/';
                        }
                        TestPath(directory, patternEnd, isLastPathSegment, false);
                    }
                }
            }
        }

        /// <summary>
        /// Collapses runs of "**" path segments. "**" followed by another "**" walks the same
        /// tree twice, which reported every match of "**/**/*.txt" four times over; MRI treats
        /// consecutive "**" segments as one.
        /// </summary>
        private static string/*!*/ CollapseDoubleStars(string/*!*/ pattern) {
            if (pattern.IndexOf("**", StringComparison.Ordinal) < 0) {
                return pattern;
            }

            var segments = pattern.Split('/');
            var kept = new List<string>(segments.Length);
            for (int i = 0; i < segments.Length; i++) {
                // A trailing "**" is a plain "*", not a recursive descent, so it never
                // collapses into the "**" before it: Dir["**/**"] is the whole tree while
                // Dir["**"] is one level.
                bool recursive = segments[i] == "**" && i < segments.Length - 1;
                if (recursive && kept.Count > 0 && kept[kept.Count - 1] == "**") {
                    continue;
                }
                kept.Add(segments[i]);
            }
            return (kept.Count == segments.Length) ? pattern : String.Join("/", kept.ToArray());
        }

        public static IEnumerable<string>/*!*/ GetMatches(PlatformAdaptationLayer/*!*/ pal, string/*!*/ pattern, int flags) {
            return GetMatches(pal, pattern, flags, null, true);
        }

        public static IEnumerable<string>/*!*/ GetMatches(PlatformAdaptationLayer/*!*/ pal, string/*!*/ pattern, int flags,
            string baseDirectory, bool sort) {

            if (pattern.Length == 0) {
                yield break;
            }
            bool noEscape = ((flags & Constants.FNM_NOESCAPE) != 0);
            string[] groups = UngroupGlobs(pattern, noEscape);
            if (groups.Length == 0) {
                yield break;
            }

            foreach (string group in groups) {
                GlobMatcher matcher = new GlobMatcher(pal, CollapseDoubleStars(group), flags, baseDirectory, sort);
                foreach (string filename in matcher.DoGlob()) {
                    yield return filename;
                }
            }
        }

        public static IEnumerable<MutableString>/*!*/ GetMatches(RubyContext/*!*/ context, MutableString/*!*/ pattern, int flags) {
            return GetMatches(context, pattern, flags, null, true);
        }

        public static IEnumerable<MutableString>/*!*/ GetMatches(RubyContext/*!*/ context, MutableString/*!*/ pattern, int flags,
            MutableString baseDirectory, bool sort) {

            string strPattern = context.DecodePath(pattern);
            string strBase = (baseDirectory != null) ? context.DecodePath(baseDirectory) : null;
            foreach (string strFileName in GetMatches(context.Platform, strPattern, flags, strBase, sort)) {
                // The results carry the pattern's encoding, not the path encoding:
                // Dir.glob("file".encode("EUC-JP")).first.encoding is EUC-JP. A name that
                // cannot be represented in it falls back to the path encoding rather than
                // raising.
                var name = MutableString.Create(strFileName, pattern.Encoding);
                if (name.ContainsInvalidCharacters()) {
                    name = context.EncodePath(strFileName);
                }
                yield return name;
            }
        }
    }
}
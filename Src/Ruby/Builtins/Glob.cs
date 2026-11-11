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
using System.IO.Enumeration;

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

        /// <summary>
        /// Walks the file system for one pattern the way MRI's glob_helper (dir.c) does: each
        /// directory is listed once, in sorted order, and every entry is matched against all the
        /// pattern components that are active at that point - more than one under "**" - before
        /// the walk goes on below it. That gives MRI's result order: "**/*" is
        /// ["a", "a/b", "a/b/c", "d"], depth first, not one level after another.
        /// </summary>
        private sealed class GlobMatcher {
            private readonly PlatformAdaptationLayer/*!*/ _pal;
            private readonly RubyContext _context;
            private readonly string/*!*/ _pattern;
            private readonly int _flags;
            private readonly bool _dirOnly;
            private readonly bool _sort;
            private readonly string _base;
            private readonly List<string>/*!*/ _result;

            // The PAL is the real file system, so it may be listed directly and asked about links.
            private readonly bool _realFileSystem;

            // ".." in a path is resolved as the kernel does - against the directory a symbolic
            // link points to - rather than cancelled out against the name before it, as .NET's
            // path normalisation does. Windows resolves ".." by name, as .NET does.
            private readonly bool _physicalDotDot;

            /// <summary>
            /// Number of leading characters to cut off every result. Matching always runs from
            /// a concrete base directory - "." by default, or the `base:` option - and that
            /// prefix plus its separator is not part of what Ruby returns.
            /// </summary>
            private int _stripPrefix;

            private bool NoEscapes {
                get { return ((_flags & Constants.FNM_NOESCAPE) != 0); }
            }

            private bool DotMatch {
                get { return ((_flags & Constants.FNM_DOTMATCH) != 0); }
            }

            internal GlobMatcher(PlatformAdaptationLayer/*!*/ pal, RubyContext context, string/*!*/ pattern, int flags, string baseDirectory, bool sort) {
                _pal = pal;
                _context = context;
                _pattern = pattern;
                // Dir.glob ignores FNM_CASEFOLD - MRI honours it in File.fnmatch? only - and
                // uses FNM_SYSCASE, which is 0 on a case sensitive file system.
                _flags = (flags & ~Constants.FNM_CASEFOLD) | Constants.FNM_SYSCASE;
                _base = baseDirectory;
                _sort = sort;
                _result = new List<string>();
                _dirOnly = _pattern.LastCharacter() == '/';
                _stripPrefix = 0;
                _realFileSystem = UsesFileSystem(pal);
                _physicalDotDot = _realFileSystem && !RubyUtils.FileSystemUsesDriveLetters;
            }

            /// <summary>
            /// The end of the pattern's literal prefix: the position after the last separator
            /// before the first wildcard or brace, or the pattern's length when it has neither.
            /// </summary>
            private int FindLiteralPrefixEnd() {
                int lastSlash = -1;
                bool inEscape = false;
                for (int i = 0; i < _pattern.Length; i++) {
                    if (inEscape) {
                        inEscape = false;
                        continue;
                    }
                    char c = _pattern[i];
                    if (c == '\\' && !NoEscapes) {
                        inEscape = true;
                    } else if (c == '*' || c == '?' || c == '[' || c == '{') {
                        return lastSlash + 1;
                    } else if (c == '/' || c == ':') {
                        lastSlash = i;
                    }
                }
                return _pattern.Length;
            }

            #region Pattern

            private enum NodeKind : byte {
                Plain,
                Magical,
                Brace,
                Recursive,
                MatchAll,
                MatchDir,
            }

            /// <summary>
            /// One component of the pattern, as glob_make_pattern splits it: a name to join on
            /// (Plain), a name to match against a listing (Magical, or Brace after "**"), "**/"
            /// (Recursive), or the end of the pattern - with or without a trailing '/'.
            /// </summary>
            private sealed class Node {
                internal NodeKind Kind;
                // The component as written, escapes and all; null for Recursive and the ends.
                internal string Text;
                // Plain: the name it stands for.
                internal string Literal;
                internal Node Next;

                private FnMatcher _matcher;
                private Node[] _single;
                internal Node[] Expansions;

                internal FnMatcher/*!*/ GetMatcher(int flags) {
                    if (_matcher == null) {
                        _matcher = GetFnMatcher(Text, Kind == NodeKind.Brace ? flags | Constants.FNM_EXTGLOB : flags);
                    }
                    return _matcher;
                }

                // A set holding just this node; most sets are one node, and this saves an array
                // for every entry that is walked on.
                internal Node[]/*!*/ Single {
                    get { return _single ?? (_single = new[] { this }); }
                }

                internal bool IsEnd {
                    get { return Kind == NodeKind.MatchAll || Kind == NodeKind.MatchDir; }
                }
            }

            // find_dirsep: the next '/' that is not inside [...] or {...} and not escaped.
            private int FindDirSeparator(string/*!*/ pattern, int start) {
                bool open = false;
                for (int i = start; i < pattern.Length; i++) {
                    char c = pattern[i];
                    switch (c) {
                        case '[':
                        case '{':
                            open = true;
                            break;
                        case ']':
                        case '}':
                            open = false;
                            break;
                        case '/':
                            if (!open) {
                                return i;
                            }
                            break;
                        case '\\':
                            if (!NoEscapes) {
                                i++;
                            }
                            break;
                    }
                }
                return pattern.Length;
            }

            // has_magic, for one component.
            private NodeKind GetComponentKind(string/*!*/ pattern, int start, int end) {
                bool magical = false;
                for (int i = start; i < end; i++) {
                    switch (pattern[i]) {
                        case '{':
                            return NodeKind.Brace;
                        case '*':
                        case '?':
                        case '[':
                            magical = true;
                            break;
                        case '\\':
                            if (!NoEscapes) {
                                i++;
                            }
                            break;
                    }
                }
                return magical ? NodeKind.Magical : NodeKind.Plain;
            }

            // glob_make_pattern
            private Node/*!*/ ParsePattern(string/*!*/ pattern, int start) {
                Node head = null, tail = null;
                bool dirsep = false;
                bool recursive = false;
                int p = start;
                while (p < pattern.Length) {
                    var node = new Node();
                    if (p + 2 < pattern.Length && pattern[p] == '*' && pattern[p + 1] == '*' && pattern[p + 2] == '/') {
                        // Consecutive "**/"s, and the separators after them, are one: "**/**/x"
                        // walks the tree once, and "**//x" is "**/x".
                        do {
                            p += 3;
                            while (p < pattern.Length && pattern[p] == '/') {
                                p++;
                            }
                        } while (p + 2 < pattern.Length && pattern[p] == '*' && pattern[p + 1] == '*' && pattern[p + 2] == '/');
                        node.Kind = NodeKind.Recursive;
                        dirsep = true;
                        recursive = true;
                    } else {
                        int m = FindDirSeparator(pattern, p);
                        NodeKind kind = GetComponentKind(pattern, p, m);
                        if (kind == NodeKind.Plain && !recursive && m < pattern.Length) {
                            // Literal components that are not the last are joined on together,
                            // "a/b/" for "a/b/*". After a "**" each one is matched in a listing.
                            int m2;
                            while ((m2 = FindDirSeparator(pattern, m + 1)) < pattern.Length &&
                                GetComponentKind(pattern, m + 1, m2) == NodeKind.Plain) {
                                m = m2;
                            }
                        }
                        node.Kind = kind;
                        node.Text = pattern.Substring(p, m - p);
                        if (kind == NodeKind.Plain) {
                            node.Literal = NoEscapes ? node.Text : Unescape(node.Text, 0);
                        }
                        if (m < pattern.Length) {
                            dirsep = true;
                            p = m + 1;
                        } else {
                            dirsep = false;
                            p = m;
                        }
                    }

                    if (head == null) {
                        head = node;
                    } else {
                        tail.Next = node;
                    }
                    tail = node;
                }

                var end = new Node { Kind = dirsep ? NodeKind.MatchDir : NodeKind.MatchAll };
                if (head == null) {
                    head = end;
                } else {
                    tail.Next = end;
                }
                return head;
            }

            // join_path_from_pattern: the rest of the pattern from a node on.
            private static string/*!*/ PatternFrom(Node/*!*/ node) {
                var result = new StringBuilder();
                bool first = true;
                for (Node p = node; p != null; p = p.Next) {
                    string str;
                    switch (p.Kind) {
                        case NodeKind.Recursive: str = "**"; break;
                        case NodeKind.MatchDir: str = ""; break;
                        case NodeKind.MatchAll: continue;
                        default: str = p.Text; break;
                    }
                    if (!first) {
                        result.Append('/');
                    }
                    result.Append(str);
                    first = false;
                }
                return result.ToString();
            }

            // The brace alternatives of the rest of the pattern from a node on, each parsed.
            private Node[]/*!*/ GetExpansions(Node/*!*/ node) {
                if (node.Expansions == null) {
                    string[] patterns = UngroupGlobs(PatternFrom(node), NoEscapes);
                    var expansions = new Node[patterns.Length];
                    for (int i = 0; i < patterns.Length; i++) {
                        expansions[i] = ParsePattern(patterns[i], 0);
                    }
                    node.Expansions = expansions;
                }
                return node.Expansions;
            }

            #endregion

            #region File system

            internal enum EntryKind : byte {
                Unknown,
                // A directory, or a symbolic link to one.
                Directory,
                // Exists (a dangling symbolic link does), but is not a directory.
                NotDirectory,
                NoEntry,
                // Resolving the path ran into a symbolic link loop.
                Loop,
            }

            /// <summary>One entry of a directory listing.</summary>
            private struct Entry {
                internal string Name;
                // Directory when the entry is one or is a symbolic link to one, as
                // DirectoryExists would answer; Unknown when the listing could not tell.
                internal EntryKind Kind;
            }

            private static readonly EnumerationOptions _listOptions = new EnumerationOptions {
                // Directory.GetFileSystemEntries' (EnumerationOptions.Compatible): every entry.
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };

            private static readonly int _eloop = RubyErrno.GetNumber("ELOOP");

            /// <summary>
            /// Lists a directory, or returns null when there is no directory to list. Like MRI's
            /// opendir, it is silent about a missing directory, a file and an unreadable directory,
            /// but raises Errno::ELOOP for a symbolic link loop.
            /// </summary>
            /// <param name="path">The path as it is reported.</param>
            /// <param name="fsPath">The path to ask the file system about.</param>
            private Entry[] ListDirectory(string/*!*/ path, string/*!*/ fsPath, EntryKind kind) {
                if (kind == EntryKind.Loop) {
                    throw MakeLoopError(path, null);
                }

                Entry[] entries;
                if (_realFileSystem) {
                    if (kind == EntryKind.NotDirectory || kind == EntryKind.Unknown && !_pal.DirectoryExists(fsPath)) {
                        // Only a symbolic link that leads nowhere can be a loop; opendir on
                        // anything else that is not a directory just fails.
                        if (!_physicalDotDot || !IsSymbolicLink(fsPath)) {
                            return null;
                        }
                    }

                    // The listing keeps what readdir says about each entry's type. Asking the
                    // file system again for every entry - FileExists, DirectoryExists, a symlink
                    // check - is most of what globbing a large tree used to cost.
                    var list = new List<Entry>();
                    try {
                        var enumerable = new FileSystemEnumerable<Entry>(
                            fsPath,
                            (ref FileSystemEntry entry) => new Entry {
                                Name = entry.FileName.ToString(),
                                Kind = GetKind(ref entry),
                            },
                            _listOptions);
                        list.AddRange(enumerable);
                    } catch (DirectoryNotFoundException) {
                        return null;
                    } catch (FileNotFoundException) {
                        return null;
                    } catch (UnauthorizedAccessException) {
                        // MRI only warns about an unreadable directory, and only when $VERBOSE
                        return null;
                    } catch (IOException e) {
                        if (_physicalDotDot && e.HResult == _eloop) {
                            throw MakeLoopError(path, e);
                        }
                        throw;
                    }
                    entries = list.ToArray();
                } else {
                    if (kind != EntryKind.Directory && !_pal.DirectoryExists(fsPath)) {
                        return null;
                    }
                    string[] paths = _pal.GetFileSystemEntries(fsPath, "*");
                    entries = new Entry[paths.Length];
                    for (int i = 0; i < paths.Length; i++) {
                        entries[i] = new Entry { Name = System.IO.Path.GetFileName(paths[i]), Kind = EntryKind.Unknown };
                    }
                }

                if (_sort) {
                    // MRI sorts the entries of each directory as it walks into it rather than
                    // sorting the final list, so "{zz,aa}/*" keeps the brace alternatives in
                    // pattern order while sorting within each of them.
                    Array.Sort(entries, (x, y) => CompareNames(x.Name, y.Name));
                }
                return entries;
            }

            /// <summary>
            /// strcmp on the UTF-8 names, which is code point order. Ordinal UTF-16 order differs
            /// from it only where a surrogate pair meets a character from U+E000 up.
            /// </summary>
            private static int CompareNames(string/*!*/ x, string/*!*/ y) {
                int i = x.AsSpan().CommonPrefixLength(y.AsSpan());
                if (i == x.Length || i == y.Length) {
                    return x.Length - y.Length;
                }
                return CodePointOrder(x[i]) - CodePointOrder(y[i]);
            }

            private static int CodePointOrder(char c) {
                return c < 0xD800 ? c : c >= 0xE000 ? c - 0x800 : c + 0x2000;
            }

            private Exception/*!*/ MakeLoopError(string/*!*/ path, IOException inner) {
                string reported = ErrorPath(path);
                if (_context != null) {
                    return RubyExceptions.CreateErrno(_context, _eloop, reported);
                }
                return inner ?? new IOException("Too many levels of symbolic links - " + reported, _eloop);
            }

            // The path MRI names in an error: without the "./" of the implicit base.
            private string/*!*/ ErrorPath(string/*!*/ path) {
                return (_base == null && _stripPrefix > 0 && path.Length > _stripPrefix) ? path.Substring(_stripPrefix) : path;
            }

            private static EntryKind GetKind(ref FileSystemEntry entry) {
                if (RubyUtils.FileSystemUsesDriveLetters) {
                    // Windows: the attributes come with the listing. A symbolic link or junction
                    // carries its own Directory attribute whether or not its target exists, so
                    // that one is left to DirectoryExists and FileExists.
                    return (entry.Attributes & FileAttributes.ReparsePoint) != 0 ? EntryKind.Unknown :
                        entry.IsDirectory ? EntryKind.Directory : EntryKind.NotDirectory;
                }
                // Unix: IsDirectory follows a symbolic link (one stat, for links only), as
                // DirectoryExists does; a dangling link is not a directory. (Attributes would
                // stat every entry, for ReadOnly.)
                return entry.IsDirectory ? EntryKind.Directory : EntryKind.NotDirectory;
            }

            // Whether the PAL's file system is the real one, so that a listing may be made
            // directly: a host's PAL that overrides how files are found or listed (a virtual
            // file system) is asked through its own methods.
            private static readonly Dictionary<Type, bool>/*!*/ _palUsesFileSystem = new Dictionary<Type, bool>();

            private static bool UsesFileSystem(PlatformAdaptationLayer/*!*/ pal) {
                Type type = pal.GetType();
                if (type == typeof(PlatformAdaptationLayer)) {
                    return true;
                }

                bool result;
                lock (_palUsesFileSystem) {
                    if (_palUsesFileSystem.TryGetValue(type, out result)) {
                        return result;
                    }
                }

                result = IsInherited(type, "FileExists", typeof(string)) &&
                    IsInherited(type, "DirectoryExists", typeof(string)) &&
                    IsInherited(type, "GetFileSystemEntries", typeof(string), typeof(string), typeof(bool), typeof(bool));

                lock (_palUsesFileSystem) {
                    _palUsesFileSystem[type] = result;
                }
                return result;
            }

            private static bool IsInherited(Type/*!*/ type, string/*!*/ name, params Type[]/*!*/ parameterTypes) {
                try {
                    var method = type.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, parameterTypes, null);
                    return method != null && method.DeclaringType == typeof(PlatformAdaptationLayer);
                } catch (Exception) {
                    return false;
                }
            }

            /// <summary>
            /// PlatformAdaptationLayer has no notion of links, so this goes straight to
            /// System.IO, and only for the real file system: a virtual one has no links.
            /// </summary>
            private static bool IsSymbolicLink(string/*!*/ path) {
                try {
                    // a trailing '/' would follow the link
                    return new FileInfo(path.Length > 1 ? path.TrimEnd('/') : path).LinkTarget != null;
                } catch (Exception) {
                    return false;
                }
            }

            /// <summary>
            /// Whether a directory "**" may walk into: a real directory, not a symbolic link to one.
            /// </summary>
            private bool IsRealDirectory(string/*!*/ fsPath, EntryKind kind) {
                if (kind == EntryKind.Unknown) {
                    // a host PAL's entry, or a Windows reparse point
                    if (!_pal.DirectoryExists(fsPath)) {
                        return false;
                    }
                } else if (kind != EntryKind.Directory) {
                    return false;
                }
                return !_realFileSystem || !IsSymbolicLink(fsPath);
            }

            private EntryKind GetPathKind(string/*!*/ fsPath) {
                if (_pal.DirectoryExists(fsPath)) {
                    return EntryKind.Directory;
                }
                // FileExists answers true for a dangling symbolic link, which MRI reports too
                return _pal.FileExists(fsPath) ? EntryKind.NotDirectory : EntryKind.NoEntry;
            }

            // Whether a path has a "." or ".." component, which .NET would resolve by name.
            private static bool HasDotSegment(string/*!*/ path) {
                int start = 0;
                while (start <= path.Length) {
                    int end = path.IndexOf('/', start);
                    if (end < 0) {
                        end = path.Length;
                    }
                    int length = end - start;
                    if (length > 0 && length <= 2 && path[start] == '.' && (length == 1 || path[start + 1] == '.')) {
                        return true;
                    }
                    start = end + 1;
                }
                return false;
            }

            private const int MaxSymbolicLinks = 40;

            /// <summary>
            /// Spells <paramref name="path"/> without "." and "..", so that .NET's normalisation of
            /// the path - which drops "x/.." and "x/." by name - cannot change what it refers to.
            /// Each ".." is taken against the directory its prefix really is: "a/link/.." is the
            /// parent of the link's target, and "file/.." and "file/." do not exist. Returns null when the path does not
            /// exist (<paramref name="loop"/> tells whether a link loop was why).
            /// </summary>
            private static string ResolveDotDot(string/*!*/ path, out bool loop) {
                loop = false;
                var rest = new Stack<string>();
                int dotDots = PushComponents(rest, path);
                string resolved = path.StartsWith("/", StringComparison.Ordinal) ? "/" : "";
                int links = 0;

                while (rest.Count > 0) {
                    string c = rest.Pop();
                    if (c.Length == 0) {
                        continue;
                    }
                    if (c == ".") {
                        // what it follows was made sure to be a directory
                        dotDots--;
                        continue;
                    }
                    if (c == "..") {
                        dotDots--;
                        resolved = ParentOf(resolved);
                        continue;
                    }

                    string candidate = resolved.Length == 0 ? c : resolved.EndsWith("/", StringComparison.Ordinal) ? resolved + c : resolved + "/" + c;
                    if (dotDots == 0) {
                        // nothing left to go up from it: the name means what .NET takes it to mean
                        resolved = candidate;
                        continue;
                    }

                    string target;
                    try {
                        target = new FileInfo(candidate).LinkTarget;
                    } catch (Exception) {
                        return null;
                    }

                    if (target != null) {
                        if (++links > MaxSymbolicLinks) {
                            loop = true;
                            return null;
                        }
                        // the target is relative to the directory holding the link: resolved
                        dotDots += PushComponents(rest, target);
                        if (target.StartsWith("/", StringComparison.Ordinal)) {
                            resolved = "/";
                        }
                        continue;
                    }

                    // a "." or ".." follows, and only a directory has those
                    if (!Directory.Exists(candidate)) {
                        return null;
                    }
                    resolved = candidate;
                }

                if (resolved.Length == 0) {
                    resolved = ".";
                }
                if (path.EndsWith("/", StringComparison.Ordinal) && !resolved.EndsWith("/", StringComparison.Ordinal)) {
                    resolved += "/";
                }
                return resolved;
            }

            private static int PushComponents(Stack<string>/*!*/ stack, string/*!*/ path) {
                string[] components = path.Split('/');
                int dotDots = 0;
                for (int i = components.Length - 1; i >= 0; i--) {
                    stack.Push(components[i]);
                    if (components[i] == ".." || components[i] == ".") {
                        dotDots++;
                    }
                }
                return dotDots;
            }

            // The parent of a path that has no symbolic links and no ".." in it - other than
            // leading ones, which go up from the current directory, which has none.
            private static string/*!*/ ParentOf(string/*!*/ resolved) {
                if (resolved == "/") {
                    return resolved;
                }
                if (resolved.Length == 0) {
                    return "..";
                }
                if (resolved == ".." || resolved.EndsWith("/..", StringComparison.Ordinal)) {
                    return resolved + "/..";
                }
                int slash = resolved.LastIndexOf('/');
                return slash < 0 ? "" : slash == 0 ? "/" : resolved.Substring(0, slash);
            }

            /// <summary>
            /// The path to ask the file system about for <paramref name="path"/>: itself, unless
            /// it has a "." or ".." that .NET would resolve differently from the kernel.
            /// </summary>
            private string ToFileSystemPath(string/*!*/ path, ref EntryKind kind) {
                if (!_physicalDotDot || !HasDotSegment(path)) {
                    return null;
                }
                bool loop;
                string resolved = ResolveDotDot(path, out loop);
                if (resolved == null) {
                    kind = loop ? EntryKind.Loop : EntryKind.NoEntry;
                }
                // "." for the implicit base, say: the path itself will do
                return resolved == path ? null : resolved;
            }

            #endregion

            private void Report(string/*!*/ full) {
                full = RubyUtils.CanonicalizePath(full);
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
                _result.Add(match);
            }

            private static string/*!*/ Join(string/*!*/ path, bool dirsep, string/*!*/ name) {
                return dirsep ? path + "/" + name : path + name;
            }

            private static string Unescape(string/*!*/ path, int start) {
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
                    pos = FindLiteralPrefixEnd();
                    if (pos == _pattern.Length) {
                        // nothing to match: the pattern names one file
                        string path = NoEscapes ? _pattern : Unescape(_pattern, 0);
                        EntryKind kind = EntryKind.Unknown;
                        string fsPath = ToFileSystemPath(path, ref kind) ?? path;
                        if (kind == EntryKind.Unknown) {
                            kind = GetPathKind(fsPath);
                        }
                        if (kind == EntryKind.Directory || kind == EntryKind.NotDirectory && !_dirOnly) {
                            Report(path);
                        }
                        return _result;
                    }
                    if (pos > 0) {
                        baseDirectory = _pattern.Substring(0, pos);
                        if (!NoEscapes) {
                            baseDirectory = Unescape(baseDirectory, 0);
                        }
                        // "/a/b/" is the path "/a/b" with names joined on after a '/' - as MRI
                        // has it, and as an error names it - but "/", "c:/" and "/a//" keep theirs.
                        if (baseDirectory.Length > 2 && baseDirectory[baseDirectory.Length - 1] == '/') {
                            char last = baseDirectory[baseDirectory.Length - 2];
                            if (last != '/' && last != ':') {
                                baseDirectory = baseDirectory.Substring(0, baseDirectory.Length - 1);
                            }
                        }
                    }
                }

                bool reportable = true;
                if (baseDirectory == null) {
                    // A relative pattern is matched against `base:` when one was given, and the
                    // base is not part of the result - "*" with base "a/b" yields "c", not
                    // "a/b/c". MRI ignores base: for an absolute pattern, which is why this
                    // only runs when the pattern gave us no base of its own.
                    reportable = !String.IsNullOrEmpty(_base);
                    baseDirectory = reportable ? _base : ".";
                    // The entry name is appended to the base with a separator between,
                    // unless the base already ends in one - "a/" and "a" both produce
                    // "a/b", so a trailing separator leaves nothing extra to strip.
                    _stripPrefix = baseDirectory.Length +
                        (baseDirectory[baseDirectory.Length - 1] == '/' ? 0 : 1);
                }

                EntryKind baseKind = EntryKind.Unknown;
                string baseFsPath = ToFileSystemPath(baseDirectory, ref baseKind);
                Node head = ParsePattern(_pattern, pos);
                Walk(baseDirectory, baseFsPath, !baseDirectory.EndsWith("/", StringComparison.Ordinal), baseKind,
                    head.Single, 1, false, reportable);
                return _result;
            }

            /// <summary>glob_helper: what the pattern nodes in <paramref name="set"/> make of <paramref name="path"/>.</summary>
            /// <param name="path">The path as reported.</param>
            /// <param name="fsPath">The path to ask the file system about, when it is not <paramref name="path"/>.</param>
            /// <param name="dirsep">Whether a '/' goes between the path and a name joined on.</param>
            /// <param name="kind">What is known about the path already.</param>
            /// <param name="skipDot">
            /// "." is listed in the first directory the walk lists, and only there, so that
            /// Dir["a/**/*", File::FNM_DOTMATCH] yields "a/." but not "a/b/.".
            /// </param>
            /// <param name="reportable">False for the implicit "." of a relative pattern, which is never a match.</param>
            private void Walk(string/*!*/ path, string fsPath, bool dirsep, EntryKind kind, Node[]/*!*/ set, int count,
                bool skipDot, bool reportable) {

                bool plain = false, magical = false, recursive = false, brace = false, matchAll = false, matchDir = false;
                for (int i = 0; i < count; i++) {
                    Node p = set[i];
                    if (p.Kind == NodeKind.Recursive) {
                        recursive = true;
                        p = p.Next;
                    }
                    switch (p.Kind) {
                        case NodeKind.Plain: plain = true; break;
                        case NodeKind.Magical: magical = true; break;
                        // After "**" a brace that stays within one name is matched in the listing
                        // (FNM_EXTGLOB) rather than expanded: "**/*.{rb,py}" walks the tree once.
                        case NodeKind.Brace: if (!recursive || p.Text.IndexOf('/') >= 0) brace = true; break;
                        case NodeKind.MatchAll: matchAll = true; break;
                        case NodeKind.MatchDir: matchDir = true; break;
                    }
                }

                if (brace) {
                    // MRI expands braces when the walk gets to them, so "*/{x,y}" gives a/x, a/y,
                    // b/x, b/y rather than every x before any y. (A pattern that starts with a brace
                    // was expanded before the walk began.)
                    foreach (Node expansion in GetExpansions(set[0])) {
                        Walk(path, fsPath, dirsep, kind, expansion.Single, 1, skipDot, reportable);
                    }
                    return;
                }

                string fs = fsPath ?? path;
                if (reportable) {
                    if ((matchAll || matchDir) && kind == EntryKind.Unknown) {
                        kind = GetPathKind(fs);
                    }
                    if (matchAll && (kind == EntryKind.Directory || kind == EntryKind.NotDirectory)) {
                        Report(path);
                    }
                    if (matchDir) {
                        if (kind != EntryKind.Directory) {
                            // MRI's stat said no (or ELOOP, which it only warns about): there is
                            // nothing to list either.
                            return;
                        }
                        Report(dirsep ? path + "/" : path);
                    }
                }

                if (kind == EntryKind.NoEntry) {
                    return;
                }

                if (magical || recursive) {
                    Entry[] entries = ListDirectory(path, fs, kind);
                    if (entries == null) {
                        return;
                    }

                    // "." comes from readdir in MRI, so it sorts with the other names; ".." never matches.
                    bool listDot = !skipDot && !(recursive && !DotMatch);
                    int dotIndex = -1;
                    if (listDot) {
                        dotIndex = 0;
                        if (_sort) {
                            while (dotIndex < entries.Length && CompareNames(entries[dotIndex].Name, ".") < 0) {
                                dotIndex++;
                            }
                        }
                    }

                    var newSet = new Node[count * 2];
                    for (int e = 0; e <= entries.Length; e++) {
                        if (e == dotIndex) {
                            WalkEntry(path, fsPath, dirsep, ".", EntryKind.Directory, set, count, newSet);
                        }
                        if (e < entries.Length) {
                            WalkEntry(path, fsPath, dirsep, entries[e].Name, entries[e].Kind, set, count, newSet);
                        }
                    }
                } else if (plain) {
                    for (int i = 0; i < count; i++) {
                        Node p = set[i];
                        if (p.Kind != NodeKind.Plain) {
                            continue;
                        }
                        // Nodes that name the same file are walked on together.
                        bool seen = false;
                        for (int j = 0; j < i; j++) {
                            if (set[j].Kind == NodeKind.Plain && set[j].Literal == p.Literal) {
                                seen = true;
                                break;
                            }
                        }
                        if (seen) {
                            continue;
                        }

                        Node[] next = p.Next.Single;
                        int nextCount = 1;
                        for (int j = i + 1; j < count; j++) {
                            if (set[j].Kind == NodeKind.Plain && set[j].Literal == p.Literal) {
                                if (nextCount == 1) {
                                    next = new Node[count];
                                    next[0] = p.Next;
                                }
                                next[nextCount++] = set[j].Next;
                            }
                        }

                        string childPath = Join(path, dirsep, p.Literal);
                        string childFsPath = null;
                        // what cannot be resolved has nothing below it that can
                        EntryKind childKind = kind == EntryKind.Loop ? EntryKind.Loop : EntryKind.Unknown;
                        if (fsPath != null || _physicalDotDot && HasDotSegment(p.Literal)) {
                            string joined = Join(fs, dirsep, p.Literal);
                            childFsPath = ToFileSystemPath(joined, ref childKind) ??
                                (childKind == EntryKind.Unknown && fsPath != null ? joined : null);
                        }
                        Walk(childPath, childFsPath, true, childKind, next, nextCount, skipDot, true);
                    }
                }
            }

            private void WalkEntry(string/*!*/ path, string fsPath, bool dirsep, string/*!*/ name, EntryKind kind,
                Node[]/*!*/ set, int count, Node[]/*!*/ newSet) {

                int dotFile = name[0] != '.' ? 0 : name.Length == 1 ? 2 : 1;
                int newCount = 0;
                string childFsPath = null;
                bool? realDirectory = null;

                for (int i = 0; i < count; i++) {
                    Node p = set[i];
                    if (p.Kind == NodeKind.Recursive) {
                        // "**" goes into directories, but not through a symbolic link, and into
                        // dot directories only with FNM_DOTMATCH - never into "." itself.
                        if (dotFile < (DotMatch ? 2 : 1) && kind != EntryKind.NotDirectory) {
                            if (realDirectory == null) {
                                childFsPath = Join(fsPath ?? path, dirsep, name);
                                realDirectory = IsRealDirectory(childFsPath, kind);
                                if (realDirectory.Value && kind == EntryKind.Unknown) {
                                    kind = EntryKind.Directory;
                                }
                            }
                            if (realDirectory.Value) {
                                AddToSet(newSet, ref newCount, p);
                            }
                        }
                        p = p.Next;
                    }
                    switch (p.Kind) {
                        case NodeKind.Plain:
                        case NodeKind.Magical:
                        case NodeKind.Brace:
                            if (p.GetMatcher(_flags).IsMatch(name)) {
                                AddToSet(newSet, ref newCount, p.Next);
                            }
                            break;
                    }
                }

                if (newCount == 0) {
                    return;
                }

                Node[] next = newCount == 1 ? newSet[0].Single : (Node[])newSet.Clone();
                Walk(Join(path, dirsep, name), fsPath != null ? (childFsPath ?? Join(fsPath, dirsep, name)) : null,
                    true, kind, next, newCount, true, true);
            }

            private static void AddToSet(Node[]/*!*/ set, ref int count, Node/*!*/ node) {
                // MRI's set can hold a node twice ("**/a/**/b" reaches "b" both ways), which
                // changes nothing but the work done.
                for (int i = 0; i < count; i++) {
                    if (set[i] == node) {
                        return;
                    }
                }
                set[count++] = node;
            }
        }

        public static IEnumerable<string>/*!*/ GetMatches(PlatformAdaptationLayer/*!*/ pal, string/*!*/ pattern, int flags) {
            return GetMatches(pal, pattern, flags, null, true);
        }

        public static IEnumerable<string>/*!*/ GetMatches(PlatformAdaptationLayer/*!*/ pal, string/*!*/ pattern, int flags,
            string baseDirectory, bool sort) {
            return GetMatches(pal, null, pattern, flags, baseDirectory, sort);
        }

        private static IEnumerable<string>/*!*/ GetMatches(PlatformAdaptationLayer/*!*/ pal, RubyContext context, string/*!*/ pattern, int flags,
            string baseDirectory, bool sort) {

            if (pattern.Length == 0) {
                yield break;
            }

            // A pattern that starts with a brace is expanded before anything is matched, as
            // ruby_glob0 does - "{/a,b}/*" has an absolute alternative. Any other brace is
            // expanded when the walk reaches it.
            string[] groups;
            if (pattern[0] == '{') {
                groups = UngroupGlobs(pattern, (flags & Constants.FNM_NOESCAPE) != 0);
            } else {
                groups = new[] { pattern };
            }

            foreach (string group in groups) {
                GlobMatcher matcher = new GlobMatcher(pal, context, group, flags, baseDirectory, sort);
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
            foreach (string strFileName in GetMatches(context.Platform, context, strPattern, flags, strBase, sort)) {
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
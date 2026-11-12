/* ****************************************************************************
 *
 * Copyright (c) 2006 Kirill Simonov
 * Copyright (c) 2011-2019 Ingy dot Net
 * Copyright (c) Microsoft Corporation.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy of
 * this software and associated documentation files (the "Software"), to deal in
 * the Software without restriction, including without limitation the rights to
 * use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
 * of the Software, and to permit persons to whom the Software is furnished to do
 * so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Text;

namespace IronRuby.StandardLibrary.Yaml {

    /// <summary>
    /// A position in the input, as libyaml reports it: the character index and the zero-based
    /// line and column.
    /// </summary>
    public struct Mark {
        public readonly int Index;
        public readonly int Line;
        public readonly int Column;

        public Mark(int index, int line, int column) {
            Index = index;
            Line = line;
            Column = column;
        }
    }

    /// <summary>
    /// A reader, scanner or parser error: libyaml's error, problem, context and marks, which is
    /// what Psych::SyntaxError is made from.
    /// </summary>
    public sealed class LibyamlException : Exception {
        public readonly string Problem;
        public readonly string Context;
        public readonly Mark ProblemMark;
        public readonly Mark ContextMark;
        public readonly int ProblemOffset;

        public LibyamlException(string problem, string context, Mark contextMark, Mark problemMark, int problemOffset)
            : base(context != null ? context + " " + problem : problem) {
            Problem = problem;
            Context = context;
            ContextMark = contextMark;
            ProblemMark = problemMark;
            ProblemOffset = problemOffset;
        }
    }

    internal enum TokenType {
        StreamStart,
        StreamEnd,
        VersionDirective,
        TagDirective,
        DocumentStart,
        DocumentEnd,
        BlockSequenceStart,
        BlockMappingStart,
        BlockEnd,
        FlowSequenceStart,
        FlowSequenceEnd,
        FlowMappingStart,
        FlowMappingEnd,
        BlockEntry,
        FlowEntry,
        Key,
        Value,
        Alias,
        Anchor,
        Tag,
        Scalar,
    }

    internal sealed class LibyamlToken {
        internal TokenType Type;
        internal Mark Start;
        internal Mark End;
        // An alias's or anchor's name, or a scalar's value.
        internal string Value;
        internal EmitterScalarStyle Style;
        // A tag's handle and suffix, or a %TAG directive's handle and prefix.
        internal string Handle;
        internal string Suffix;
        internal int Major, Minor;
        internal int Encoding;

        internal LibyamlToken(TokenType type, Mark start, Mark end) {
            Type = type;
            Start = start;
            End = end;
        }
    }

    /// <summary>
    /// libyaml 0.2.5's scanner (scanner.c) and the checks its reader (reader.c) makes, ported.
    /// The input is decoded up front into code points, so an index or a column counts
    /// characters, as libyaml's marks do.
    /// </summary>
    internal sealed class LibyamlScanner {
        private sealed class SimpleKey {
            internal bool Possible;
            internal bool Required;
            internal int TokenNumber;
            internal Mark Mark;
        }

        private readonly int[]/*!*/ _buffer;
        private readonly int _length;
        private readonly int _encoding;
        private int _pointer;

        // The first character the reader would refuse, and why; -1 when there is none.
        private readonly int _badIndex;
        private readonly string _badProblem;
        private readonly int _badOffset;

        private int _index, _line, _column;

        private bool _streamStartProduced;
        private bool _streamEndProduced;

        private readonly List<LibyamlToken>/*!*/ _tokens = new List<LibyamlToken>();
        private int _tokensHead;
        private bool _tokenAvailable;
        private int _tokensParsed;

        private int _indent;
        private readonly Stack<int>/*!*/ _indents = new Stack<int>();
        private bool _simpleKeyAllowed;
        private readonly List<SimpleKey>/*!*/ _simpleKeys = new List<SimpleKey>();
        private int _flowLevel;

        /// <param name="text">The whole input.</param>
        /// <param name="encoding">What STREAM-START reports (libyaml's yaml_encoding_t).</param>
        /// <param name="badIndex">Where the input stops being readable (-1 if it does not).</param>
        internal LibyamlScanner(string/*!*/ text, int encoding, int badIndex, string badProblem, int badOffset) {
            var buffer = new List<int>(text.Length + 1);
            int bad = -1;
            int offset = 0;
            for (int i = 0; i < text.Length; i++) {
                int c = text[i];
                if (Char.IsHighSurrogate((char)c) && i + 1 < text.Length && Char.IsLowSurrogate(text[i + 1])) {
                    c = Char.ConvertToUtf32((char)c, text[i + 1]);
                    i++;
                }
                if (bad < 0 && !IsReadable(c)) {
                    bad = buffer.Count;
                    _badProblem = (c >= 0xD800 && c <= 0xDFFF) ? "invalid Unicode character" : "control characters are not allowed";
                    _badOffset = offset;
                }
                offset += Utf8Width(c);
                buffer.Add(c);
            }
            if (badIndex >= 0 && (bad < 0 || badIndex <= bad)) {
                bad = badIndex;
                _badProblem = badProblem;
                _badOffset = badOffset;
            }
            _length = buffer.Count;
            buffer.Add(0);
            _buffer = buffer.ToArray();
            _badIndex = bad;
            _encoding = encoding;
        }

        // reader.c: what may appear in a YAML stream at all.
        private static bool IsReadable(int c) {
            return c == 0x09 || c == 0x0A || c == 0x0D
                || (c >= 0x20 && c <= 0x7E)
                || c == 0x85 || (c >= 0xA0 && c <= 0xD7FF)
                || (c >= 0xE000 && c <= 0xFFFD)
                || (c >= 0x10000 && c <= 0x10FFFF);
        }

        private static int Utf8Width(int c) {
            return c < 0x80 ? 1 : c < 0x800 ? 2 : c < 0x10000 ? 3 : 4;
        }

        /// <summary>
        /// How far the scanner has read, which is what Psych::Parser#mark answers.
        /// </summary>
        internal Mark CurrentMark {
            get { return new Mark(_index, _line, _column); }
        }

        #region Buffer

        // CACHE: the reader refuses the input once the scanner has to look at a character it
        // cannot read.
        private void Cache(int length) {
            if (_badIndex >= 0 && _pointer + length > _badIndex) {
                throw new LibyamlException(_badProblem, null, default(Mark), default(Mark), _badOffset);
            }
        }

        private int At(int offset) {
            int i = _pointer + offset;
            return (i < _length) ? _buffer[i] : 0;
        }

        private bool Check(int c) { return At(0) == c; }
        private bool CheckAt(int c, int offset) { return At(offset) == c; }

        private static bool IsAlpha(int c) {
            return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == '-';
        }

        private static bool IsDigit(int c) { return c >= '0' && c <= '9'; }
        private static bool IsHex(int c) { return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'); }
        private static int AsHex(int c) { return c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c - '0'; }
        private static bool IsBreak(int c) { return c == '\r' || c == '\n' || c == 0x85 || c == 0x2028 || c == 0x2029; }
        private static bool IsBlank(int c) { return c == ' ' || c == '\t'; }
        private static bool IsBlankZ(int c) { return IsBlank(c) || IsBreak(c) || c == 0; }
        private static bool IsBreakZ(int c) { return IsBreak(c) || c == 0; }

        // The end of the input (a NUL inside it is a character the reader refuses first).
        private bool IsZ() { return At(0) == 0; }

        private void Skip() {
            _index++;
            _column++;
            _pointer++;
        }

        private void SkipLine() {
            if (At(0) == '\r' && At(1) == '\n') {
                _index += 2;
                _column = 0;
                _line++;
                _pointer += 2;
            } else if (IsBreak(At(0))) {
                _index++;
                _column = 0;
                _line++;
                _pointer++;
            }
        }

        private void Read(StringBuilder/*!*/ sb) {
            AppendCodePoint(sb, At(0));
            _index++;
            _column++;
            _pointer++;
        }

        private void ReadLine(StringBuilder/*!*/ sb) {
            int c = At(0);
            if (c == '\r' && At(1) == '\n') {
                sb.Append('\n');
                _pointer += 2;
                _index += 2;
                _column = 0;
                _line++;
            } else if (c == '\r' || c == '\n' || c == 0x85) {
                sb.Append('\n');
                _pointer++;
                _index++;
                _column = 0;
                _line++;
            } else if (c == 0x2028 || c == 0x2029) {
                sb.Append((char)c);
                _pointer++;
                _index++;
                _column = 0;
                _line++;
            }
        }

        private static void AppendCodePoint(StringBuilder/*!*/ sb, int c) {
            if (c < 0x10000) {
                sb.Append((char)c);
            } else {
                sb.Append(Char.ConvertFromUtf32(c));
            }
        }

        private Exception/*!*/ ScannerError(string context, Mark contextMark, string/*!*/ problem) {
            return new LibyamlException(problem, context, contextMark, CurrentMark, 0);
        }

        #endregion

        #region Token queue

        internal LibyamlToken/*!*/ Peek() {
            if (!_tokenAvailable) {
                FetchMoreTokens();
            }
            return _tokens[_tokensHead];
        }

        internal void SkipToken() {
            LibyamlToken token = _tokens[_tokensHead];
            _tokenAvailable = false;
            _tokensParsed++;
            _streamEndProduced = token.Type == TokenType.StreamEnd;
            _tokens[_tokensHead] = null;
            _tokensHead++;
            if (_tokensHead > 64 && _tokensHead * 2 > _tokens.Count) {
                _tokens.RemoveRange(0, _tokensHead);
                _tokensHead = 0;
            }
        }

        private int QueueCount {
            get { return _tokens.Count - _tokensHead; }
        }

        private void Enqueue(LibyamlToken/*!*/ token) {
            _tokens.Add(token);
        }

        private void QueueInsert(int position, LibyamlToken/*!*/ token) {
            _tokens.Insert(_tokensHead + position, token);
        }

        private void FetchMoreTokens() {
            while (true) {
                bool needMoreTokens = false;

                if (QueueCount == 0) {
                    needMoreTokens = true;
                } else {
                    StaleSimpleKeys();
                    foreach (SimpleKey key in _simpleKeys) {
                        if (key.Possible && key.TokenNumber == _tokensParsed) {
                            needMoreTokens = true;
                            break;
                        }
                    }
                }

                if (!needMoreTokens) {
                    break;
                }

                FetchNextToken();
            }

            _tokenAvailable = true;
        }

        #endregion

        #region Fetchers

        private void FetchNextToken() {
            Cache(1);

            if (!_streamStartProduced) {
                FetchStreamStart();
                return;
            }

            ScanToNextToken();
            StaleSimpleKeys();
            UnrollIndent(_column);

            Cache(4);

            if (IsZ()) {
                FetchStreamEnd();
                return;
            }

            if (_column == 0 && Check('%')) {
                FetchDirective();
                return;
            }

            if (_column == 0 && CheckAt('-', 0) && CheckAt('-', 1) && CheckAt('-', 2) && IsBlankZ(At(3))) {
                FetchDocumentIndicator(TokenType.DocumentStart);
                return;
            }

            if (_column == 0 && CheckAt('.', 0) && CheckAt('.', 1) && CheckAt('.', 2) && IsBlankZ(At(3))) {
                FetchDocumentIndicator(TokenType.DocumentEnd);
                return;
            }

            int c = At(0);
            switch (c) {
                case '[': FetchFlowCollectionStart(TokenType.FlowSequenceStart); return;
                case '{': FetchFlowCollectionStart(TokenType.FlowMappingStart); return;
                case ']': FetchFlowCollectionEnd(TokenType.FlowSequenceEnd); return;
                case '}': FetchFlowCollectionEnd(TokenType.FlowMappingEnd); return;
                case ',': FetchFlowEntry(); return;
            }

            if (c == '-' && IsBlankZ(At(1))) {
                FetchBlockEntry();
                return;
            }

            if (c == '?' && (_flowLevel != 0 || IsBlankZ(At(1)))) {
                FetchKey();
                return;
            }

            if (c == ':' && (_flowLevel != 0 || IsBlankZ(At(1)))) {
                FetchValue();
                return;
            }

            switch (c) {
                case '*': FetchAnchor(TokenType.Alias); return;
                case '&': FetchAnchor(TokenType.Anchor); return;
                case '!': FetchTag(); return;
                case '|':
                    if (_flowLevel == 0) {
                        FetchBlockScalar(true);
                        return;
                    }
                    break;
                case '>':
                    if (_flowLevel == 0) {
                        FetchBlockScalar(false);
                        return;
                    }
                    break;
                case '\'': FetchFlowScalar(true); return;
                case '"': FetchFlowScalar(false); return;
            }

            // A plain scalar may start with any non-blank character except the indicators;
            // '-', and in the block context '?' and ':', may start one when a non-blank
            // follows.
            if (!(IsBlankZ(c) || c == '-' || c == '?' || c == ':' || c == ',' || c == '[' || c == ']' ||
                  c == '{' || c == '}' || c == '#' || c == '&' || c == '*' || c == '!' || c == '|' ||
                  c == '>' || c == '\'' || c == '"' || c == '%' || c == '@' || c == '`') ||
                (c == '-' && !IsBlank(At(1))) ||
                (_flowLevel == 0 && (c == '?' || c == ':') && !IsBlankZ(At(1)))) {
                FetchPlainScalar();
                return;
            }

            throw ScannerError("while scanning for the next token", CurrentMark, "found character that cannot start any token");
        }

        private void StaleSimpleKeys() {
            foreach (SimpleKey key in _simpleKeys) {
                // A simple key is limited to a single line and 1024 characters.
                if (key.Possible && (key.Mark.Line < _line || key.Mark.Index + 1024 < _index)) {
                    if (key.Required) {
                        throw ScannerError("while scanning a simple key", key.Mark, "could not find expected ':'");
                    }
                    key.Possible = false;
                }
            }
        }

        private void SaveSimpleKey() {
            bool required = _flowLevel == 0 && _indent == _column;

            if (_simpleKeyAllowed) {
                var key = new SimpleKey();
                key.Possible = true;
                key.Required = required;
                key.TokenNumber = _tokensParsed + QueueCount;
                key.Mark = CurrentMark;

                RemoveSimpleKey();

                _simpleKeys[_simpleKeys.Count - 1] = key;
            }
        }

        private void RemoveSimpleKey() {
            SimpleKey key = _simpleKeys[_simpleKeys.Count - 1];
            if (key.Possible && key.Required) {
                throw ScannerError("while scanning a simple key", key.Mark, "could not find expected ':'");
            }
            key.Possible = false;
        }

        private void IncreaseFlowLevel() {
            _simpleKeys.Add(new SimpleKey());
            _flowLevel++;
        }

        private void DecreaseFlowLevel() {
            if (_flowLevel != 0) {
                _flowLevel--;
                _simpleKeys.RemoveAt(_simpleKeys.Count - 1);
            }
        }

        private void RollIndent(int column, int number, TokenType type, Mark mark) {
            if (_flowLevel != 0) {
                return;
            }

            if (_indent < column) {
                _indents.Push(_indent);
                _indent = column;

                var token = new LibyamlToken(type, mark, mark);
                if (number == -1) {
                    Enqueue(token);
                } else {
                    QueueInsert(number - _tokensParsed, token);
                }
            }
        }

        private void UnrollIndent(int column) {
            if (_flowLevel != 0) {
                return;
            }

            while (_indent > column) {
                Mark here = CurrentMark;
                Enqueue(new LibyamlToken(TokenType.BlockEnd, here, here));
                _indent = _indents.Pop();
            }
        }

        private void FetchStreamStart() {
            _indent = -1;
            _simpleKeys.Add(new SimpleKey());
            _simpleKeyAllowed = true;
            _streamStartProduced = true;

            Mark here = CurrentMark;
            var token = new LibyamlToken(TokenType.StreamStart, here, here);
            token.Encoding = _encoding;
            Enqueue(token);
        }

        private void FetchStreamEnd() {
            // Force new line.
            if (_column != 0) {
                _column = 0;
                _line++;
            }

            UnrollIndent(-1);
            RemoveSimpleKey();
            _simpleKeyAllowed = false;

            Mark here = CurrentMark;
            Enqueue(new LibyamlToken(TokenType.StreamEnd, here, here));
        }

        private void FetchDirective() {
            UnrollIndent(-1);
            RemoveSimpleKey();
            _simpleKeyAllowed = false;
            Enqueue(ScanDirective());
        }

        private void FetchDocumentIndicator(TokenType type) {
            UnrollIndent(-1);
            RemoveSimpleKey();
            _simpleKeyAllowed = false;

            Mark start = CurrentMark;
            Skip();
            Skip();
            Skip();
            Enqueue(new LibyamlToken(type, start, CurrentMark));
        }

        private void FetchFlowCollectionStart(TokenType type) {
            SaveSimpleKey();
            IncreaseFlowLevel();
            _simpleKeyAllowed = true;

            Mark start = CurrentMark;
            Skip();
            Enqueue(new LibyamlToken(type, start, CurrentMark));
        }

        private void FetchFlowCollectionEnd(TokenType type) {
            RemoveSimpleKey();
            DecreaseFlowLevel();
            _simpleKeyAllowed = false;

            Mark start = CurrentMark;
            Skip();
            Enqueue(new LibyamlToken(type, start, CurrentMark));
        }

        private void FetchFlowEntry() {
            RemoveSimpleKey();
            _simpleKeyAllowed = true;

            Mark start = CurrentMark;
            Skip();
            Enqueue(new LibyamlToken(TokenType.FlowEntry, start, CurrentMark));
        }

        private void FetchBlockEntry() {
            if (_flowLevel == 0) {
                if (!_simpleKeyAllowed) {
                    throw ScannerError(null, CurrentMark, "block sequence entries are not allowed in this context");
                }
                RollIndent(_column, -1, TokenType.BlockSequenceStart, CurrentMark);
            } else {
                // A '-' in the flow context is an error the parser reports, where it can
                // point at the context.
            }

            RemoveSimpleKey();
            _simpleKeyAllowed = true;

            Mark start = CurrentMark;
            Skip();
            Enqueue(new LibyamlToken(TokenType.BlockEntry, start, CurrentMark));
        }

        private void FetchKey() {
            if (_flowLevel == 0) {
                if (!_simpleKeyAllowed) {
                    throw ScannerError(null, CurrentMark, "mapping keys are not allowed in this context");
                }
                RollIndent(_column, -1, TokenType.BlockMappingStart, CurrentMark);
            }

            RemoveSimpleKey();
            _simpleKeyAllowed = _flowLevel == 0;

            Mark start = CurrentMark;
            Skip();
            Enqueue(new LibyamlToken(TokenType.Key, start, CurrentMark));
        }

        private void FetchValue() {
            SimpleKey key = _simpleKeys[_simpleKeys.Count - 1];

            if (key.Possible) {
                // The KEY token (and maybe a BLOCK-MAPPING-START) goes in front of the key.
                QueueInsert(key.TokenNumber - _tokensParsed, new LibyamlToken(TokenType.Key, key.Mark, key.Mark));
                RollIndent(key.Mark.Column, key.TokenNumber, TokenType.BlockMappingStart, key.Mark);
                key.Possible = false;
                _simpleKeyAllowed = false;
            } else {
                // The ':' indicator follows a complex key.
                if (_flowLevel == 0) {
                    if (!_simpleKeyAllowed) {
                        throw ScannerError(null, CurrentMark, "mapping values are not allowed in this context");
                    }
                    RollIndent(_column, -1, TokenType.BlockMappingStart, CurrentMark);
                }
                _simpleKeyAllowed = _flowLevel == 0;
            }

            Mark start = CurrentMark;
            Skip();
            Enqueue(new LibyamlToken(TokenType.Value, start, CurrentMark));
        }

        private void FetchAnchor(TokenType type) {
            SaveSimpleKey();
            _simpleKeyAllowed = false;
            Enqueue(ScanAnchor(type));
        }

        private void FetchTag() {
            SaveSimpleKey();
            _simpleKeyAllowed = false;
            Enqueue(ScanTag());
        }

        private void FetchBlockScalar(bool literal) {
            RemoveSimpleKey();
            _simpleKeyAllowed = true;
            Enqueue(ScanBlockScalar(literal));
        }

        private void FetchFlowScalar(bool single) {
            SaveSimpleKey();
            _simpleKeyAllowed = false;
            Enqueue(ScanFlowScalar(single));
        }

        private void FetchPlainScalar() {
            SaveSimpleKey();
            _simpleKeyAllowed = false;
            Enqueue(ScanPlainScalar());
        }

        #endregion

        #region Scanners

        private void ScanToNextToken() {
            while (true) {
                // Allow the BOM mark to start a line.
                Cache(1);
                if (_column == 0 && At(0) == 0xFEFF) {
                    Skip();
                }

                // Tabs are allowed in the flow context, and in the block context but not at
                // the beginning of the line or after '-', '?', or ':' (complex value).
                Cache(1);
                while (Check(' ') || ((_flowLevel != 0 || !_simpleKeyAllowed) && Check('\t'))) {
                    Skip();
                    Cache(1);
                }

                if (Check('#')) {
                    while (!IsBreakZ(At(0))) {
                        Skip();
                        Cache(1);
                    }
                }

                if (IsBreak(At(0))) {
                    Cache(2);
                    SkipLine();
                    if (_flowLevel == 0) {
                        _simpleKeyAllowed = true;
                    }
                } else {
                    break;
                }
            }
        }

        private LibyamlToken/*!*/ ScanDirective() {
            Mark start = CurrentMark;
            Skip();

            string name = ScanDirectiveName(start);
            LibyamlToken token;

            if (name == "YAML") {
                int major, minor;
                ScanVersionDirectiveValue(start, out major, out minor);
                token = new LibyamlToken(TokenType.VersionDirective, start, CurrentMark);
                token.Major = major;
                token.Minor = minor;
            } else if (name == "TAG") {
                string handle, prefix;
                ScanTagDirectiveValue(start, out handle, out prefix);
                token = new LibyamlToken(TokenType.TagDirective, start, CurrentMark);
                token.Handle = handle;
                token.Suffix = prefix;
            } else {
                throw ScannerError("while scanning a directive", start, "found unknown directive name");
            }

            // Eat the rest of the line including any comments.
            Cache(1);
            while (IsBlank(At(0))) {
                Skip();
                Cache(1);
            }

            if (Check('#')) {
                while (!IsBreakZ(At(0))) {
                    Skip();
                    Cache(1);
                }
            }

            if (!IsBreakZ(At(0))) {
                throw ScannerError("while scanning a directive", start, "did not find expected comment or line break");
            }

            if (IsBreak(At(0))) {
                Cache(2);
                SkipLine();
            }

            return token;
        }

        private string/*!*/ ScanDirectiveName(Mark start) {
            var sb = new StringBuilder();

            Cache(1);
            while (IsAlpha(At(0))) {
                Read(sb);
                Cache(1);
            }

            if (sb.Length == 0) {
                throw ScannerError("while scanning a directive", start, "could not find expected directive name");
            }

            if (!IsBlankZ(At(0))) {
                throw ScannerError("while scanning a directive", start, "found unexpected non-alphabetical character");
            }

            return sb.ToString();
        }

        private void ScanVersionDirectiveValue(Mark start, out int major, out int minor) {
            Cache(1);
            while (IsBlank(At(0))) {
                Skip();
                Cache(1);
            }

            major = ScanVersionDirectiveNumber(start);

            if (!Check('.')) {
                throw ScannerError("while scanning a %YAML directive", start, "did not find expected digit or '.' character");
            }
            Skip();

            minor = ScanVersionDirectiveNumber(start);
        }

        private int ScanVersionDirectiveNumber(Mark start) {
            int value = 0;
            int length = 0;

            Cache(1);
            while (IsDigit(At(0))) {
                if (++length > 9) {
                    throw ScannerError("while scanning a %YAML directive", start, "found extremely long version number");
                }
                value = value * 10 + (At(0) - '0');
                Skip();
                Cache(1);
            }

            if (length == 0) {
                throw ScannerError("while scanning a %YAML directive", start, "did not find expected version number");
            }

            return value;
        }

        private void ScanTagDirectiveValue(Mark start, out string handle, out string prefix) {
            Cache(1);
            while (IsBlank(At(0))) {
                Skip();
                Cache(1);
            }

            handle = ScanTagHandle(true, start);

            Cache(1);
            if (!IsBlank(At(0))) {
                throw ScannerError("while scanning a %TAG directive", start, "did not find expected whitespace");
            }

            while (IsBlank(At(0))) {
                Skip();
                Cache(1);
            }

            prefix = ScanTagUri(true, true, null, start);

            Cache(1);
            if (!IsBlankZ(At(0))) {
                throw ScannerError("while scanning a %TAG directive", start, "did not find expected whitespace or line break");
            }
        }

        private LibyamlToken/*!*/ ScanAnchor(TokenType type) {
            var sb = new StringBuilder();
            Mark start = CurrentMark;
            Skip();

            Cache(1);
            while (IsAlpha(At(0))) {
                Read(sb);
                Cache(1);
            }

            Mark end = CurrentMark;

            // An anchor is followed by a whitespace character or one of the indicators
            // '?', ':', ',', ']', '}', '%', '@', '`'.
            int c = At(0);
            if (sb.Length == 0 || !(IsBlankZ(c) || c == '?' || c == ':' || c == ',' || c == ']' || c == '}' ||
                                     c == '%' || c == '@' || c == '`')) {
                throw ScannerError(type == TokenType.Anchor ? "while scanning an anchor" : "while scanning an alias", start,
                    "did not find expected alphabetic or numeric character");
            }

            var token = new LibyamlToken(type, start, end);
            token.Value = sb.ToString();
            return token;
        }

        private LibyamlToken/*!*/ ScanTag() {
            string handle, suffix;
            Mark start = CurrentMark;

            Cache(2);
            if (CheckAt('<', 1)) {
                // The canonical form: "!<...>".
                handle = "";
                Skip();
                Skip();

                suffix = ScanTagUri(true, false, null, start);

                if (!Check('>')) {
                    throw ScannerError("while scanning a tag", start, "did not find the expected '>'");
                }
                Skip();
            } else {
                // Either '!suffix' or '!handle!suffix'. Try a handle first.
                handle = ScanTagHandle(false, start);

                if (handle.Length > 1 && handle[0] == '!' && handle[handle.Length - 1] == '!') {
                    suffix = ScanTagUri(false, false, null, start);
                } else {
                    // It wasn't a handle after all; it is the start of the suffix.
                    suffix = ScanTagUri(false, false, handle, start);
                    handle = "!";

                    // The '!' tag: the handle is empty and the suffix is '!'.
                    if (suffix.Length == 0) {
                        handle = "";
                        suffix = "!";
                    }
                }
            }

            Cache(1);
            if (!IsBlankZ(At(0))) {
                if (_flowLevel == 0 || !Check(',')) {
                    throw ScannerError("while scanning a tag", start, "did not find expected whitespace or line break");
                }
            }

            var token = new LibyamlToken(TokenType.Tag, start, CurrentMark);
            token.Handle = handle;
            token.Suffix = suffix;
            return token;
        }

        private string/*!*/ ScanTagHandle(bool directive, Mark start) {
            var sb = new StringBuilder();

            Cache(1);
            if (!Check('!')) {
                throw ScannerError(directive ? "while scanning a tag directive" : "while scanning a tag", start,
                    "did not find expected '!'");
            }

            Read(sb);

            Cache(1);
            while (IsAlpha(At(0))) {
                Read(sb);
                Cache(1);
            }

            if (Check('!')) {
                Read(sb);
            } else {
                // Either the '!' tag or not really a handle: an error in a %TAG directive, and
                // the start of the URI in a tag.
                if (directive && !(sb.Length == 1 && sb[0] == '!')) {
                    throw ScannerError("while parsing a tag directive", start, "did not find expected '!'");
                }
            }

            return sb.ToString();
        }

        private string/*!*/ ScanTagUri(bool uriChar, bool directive, string head, Mark start) {
            int length = (head != null) ? head.Length : 0;
            var sb = new StringBuilder();

            // The head, without its leading '!'.
            if (length > 1) {
                sb.Append(head, 1, length - 1);
            }

            Cache(1);

            // The characters a URI may contain; inside a verbatim tag "<...>" the flow
            // indicators ',', '[' and ']' as well.
            while (true) {
                int c = At(0);
                if (!(IsAlpha(c) || c == ';' || c == '/' || c == '?' || c == ':' || c == '@' || c == '&' ||
                      c == '=' || c == '+' || c == '$' || c == '.' || c == '%' || c == '!' || c == '~' ||
                      c == '*' || c == '\'' || c == '(' || c == ')' ||
                      (uriChar && (c == ',' || c == '[' || c == ']')))) {
                    break;
                }

                if (c == '%') {
                    ScanUriEscapes(directive, start, sb);
                } else {
                    Read(sb);
                }
                length++;
                Cache(1);
            }

            if (length == 0) {
                throw ScannerError(directive ? "while parsing a %TAG directive" : "while parsing a tag", start,
                    "did not find expected tag URI");
            }

            return sb.ToString();
        }

        // One %-escaped UTF-8 character.
        private void ScanUriEscapes(bool directive, Mark start, StringBuilder/*!*/ sb) {
            int width = 0;
            var octets = new List<byte>(4);
            string context = directive ? "while parsing a %TAG directive" : "while parsing a tag";

            do {
                Cache(3);
                if (!(Check('%') && IsHex(At(1)) && IsHex(At(2)))) {
                    throw ScannerError(context, start, "did not find URI escaped octet");
                }

                int octet = (AsHex(At(1)) << 4) + AsHex(At(2));

                if (width == 0) {
                    width = (octet & 0x80) == 0x00 ? 1 :
                            (octet & 0xE0) == 0xC0 ? 2 :
                            (octet & 0xF0) == 0xE0 ? 3 :
                            (octet & 0xF8) == 0xF0 ? 4 : 0;
                    if (width == 0) {
                        throw ScannerError(context, start, "found an incorrect leading UTF-8 octet");
                    }
                } else if ((octet & 0xC0) != 0x80) {
                    throw ScannerError(context, start, "found an incorrect trailing UTF-8 octet");
                }

                octets.Add((byte)octet);
                Skip();
                Skip();
                Skip();
            } while (--width != 0);

            sb.Append(System.Text.Encoding.UTF8.GetString(octets.ToArray()));
        }

        private LibyamlToken/*!*/ ScanBlockScalar(bool literal) {
            var sb = new StringBuilder();
            var leadingBreak = new StringBuilder();
            var trailingBreaks = new StringBuilder();
            int chomping = 0;
            int increment = 0;
            int indent = 0;
            bool leadingBlank = false;
            bool trailingBlank;

            Mark start = CurrentMark;
            Skip();

            Cache(1);

            if (Check('+') || Check('-')) {
                chomping = Check('+') ? +1 : -1;
                Skip();

                Cache(1);
                if (IsDigit(At(0))) {
                    if (Check('0')) {
                        throw ScannerError("while scanning a block scalar", start, "found an indentation indicator equal to 0");
                    }
                    increment = At(0) - '0';
                    Skip();
                }
            } else if (IsDigit(At(0))) {
                if (Check('0')) {
                    throw ScannerError("while scanning a block scalar", start, "found an indentation indicator equal to 0");
                }
                increment = At(0) - '0';
                Skip();

                Cache(1);
                if (Check('+') || Check('-')) {
                    chomping = Check('+') ? +1 : -1;
                    Skip();
                }
            }

            Cache(1);
            while (IsBlank(At(0))) {
                Skip();
                Cache(1);
            }

            if (Check('#')) {
                while (!IsBreakZ(At(0))) {
                    Skip();
                    Cache(1);
                }
            }

            if (!IsBreakZ(At(0))) {
                throw ScannerError("while scanning a block scalar", start, "did not find expected comment or line break");
            }

            if (IsBreak(At(0))) {
                Cache(2);
                SkipLine();
            }

            Mark end = CurrentMark;

            if (increment != 0) {
                indent = _indent >= 0 ? _indent + increment : increment;
            }

            ScanBlockScalarBreaks(ref indent, trailingBreaks, start, ref end);

            Cache(1);
            while (_column == indent && !IsZ()) {
                // We are at the beginning of a non-empty line.
                trailingBlank = IsBlank(At(0));

                if (!literal && leadingBreak.Length > 0 && leadingBreak[0] == '\n' && !leadingBlank && !trailingBlank) {
                    // Fold the line break into a space, unless more breaks follow.
                    if (trailingBreaks.Length == 0) {
                        sb.Append(' ');
                    }
                    leadingBreak.Clear();
                } else {
                    sb.Append(leadingBreak);
                    leadingBreak.Clear();
                }

                sb.Append(trailingBreaks);
                trailingBreaks.Clear();

                leadingBlank = IsBlank(At(0));

                while (!IsBreakZ(At(0))) {
                    Read(sb);
                    Cache(1);
                }

                Cache(2);
                ReadLine(leadingBreak);

                ScanBlockScalarBreaks(ref indent, trailingBreaks, start, ref end);
            }

            if (chomping != -1) {
                sb.Append(leadingBreak);
            }
            if (chomping == 1) {
                sb.Append(trailingBreaks);
            }

            var token = new LibyamlToken(TokenType.Scalar, start, end);
            token.Value = sb.ToString();
            token.Style = literal ? EmitterScalarStyle.Literal : EmitterScalarStyle.Folded;
            return token;
        }

        private void ScanBlockScalarBreaks(ref int indent, StringBuilder/*!*/ breaks, Mark start, ref Mark end) {
            int maxIndent = 0;

            end = CurrentMark;

            while (true) {
                Cache(1);
                while ((indent == 0 || _column < indent) && Check(' ')) {
                    Skip();
                    Cache(1);
                }

                if (_column > maxIndent) {
                    maxIndent = _column;
                }

                if ((indent == 0 || _column < indent) && Check('\t')) {
                    throw ScannerError("while scanning a block scalar", start, "found a tab character where an indentation space is expected");
                }

                if (!IsBreak(At(0))) {
                    break;
                }

                Cache(2);
                ReadLine(breaks);
                end = CurrentMark;
            }

            if (indent == 0) {
                indent = maxIndent;
                if (indent < _indent + 1) {
                    indent = _indent + 1;
                }
                if (indent < 1) {
                    indent = 1;
                }
            }
        }

        private LibyamlToken/*!*/ ScanFlowScalar(bool single) {
            var sb = new StringBuilder();
            var leadingBreak = new StringBuilder();
            var trailingBreaks = new StringBuilder();
            var whitespaces = new StringBuilder();
            bool leadingBlanks;

            Mark start = CurrentMark;
            Skip();

            while (true) {
                Cache(4);
                if (_column == 0 &&
                    ((CheckAt('-', 0) && CheckAt('-', 1) && CheckAt('-', 2)) ||
                     (CheckAt('.', 0) && CheckAt('.', 1) && CheckAt('.', 2))) &&
                    IsBlankZ(At(3))) {
                    throw ScannerError("while scanning a quoted scalar", start, "found unexpected document indicator");
                }

                if (IsZ()) {
                    throw ScannerError("while scanning a quoted scalar", start, "found unexpected end of stream");
                }

                Cache(2);
                leadingBlanks = false;

                while (!IsBlankZ(At(0))) {
                    if (single && CheckAt('\'', 0) && CheckAt('\'', 1)) {
                        // An escaped single quote.
                        sb.Append('\'');
                        Skip();
                        Skip();
                    } else if (Check(single ? '\'' : '"')) {
                        // The right quote.
                        break;
                    } else if (!single && Check('\\') && IsBreak(At(1))) {
                        // An escaped line break.
                        Cache(3);
                        Skip();
                        SkipLine();
                        leadingBlanks = true;
                        break;
                    } else if (!single && Check('\\')) {
                        int codeLength = 0;

                        switch (At(1)) {
                            case '0': sb.Append('\0'); break;
                            case 'a': sb.Append('\x07'); break;
                            case 'b': sb.Append('\x08'); break;
                            case 't':
                            case '\t': sb.Append('\x09'); break;
                            case 'n': sb.Append('\x0A'); break;
                            case 'v': sb.Append('\x0B'); break;
                            case 'f': sb.Append('\x0C'); break;
                            case 'r': sb.Append('\x0D'); break;
                            case 'e': sb.Append('\x1B'); break;
                            case ' ': sb.Append(' '); break;
                            case '"': sb.Append('"'); break;
                            case '/': sb.Append('/'); break;
                            case '\\': sb.Append('\\'); break;
                            case 'N': sb.Append('\u0085'); break;
                            case '_': sb.Append((char)0xA0); break;
                            case 'L': sb.Append((char)0x2028); break;
                            case 'P': sb.Append((char)0x2029); break;
                            case 'x': codeLength = 2; break;
                            case 'u': codeLength = 4; break;
                            case 'U': codeLength = 8; break;
                            default:
                                throw ScannerError("while parsing a quoted scalar", start, "found unknown escape character");
                        }

                        Skip();
                        Skip();

                        if (codeLength != 0) {
                            long value = 0;

                            Cache(codeLength);
                            for (int k = 0; k < codeLength; k++) {
                                if (!IsHex(At(k))) {
                                    throw ScannerError("while parsing a quoted scalar", start, "did not find expected hexdecimal number");
                                }
                                value = (value << 4) + AsHex(At(k));
                            }

                            if ((value >= 0xD800 && value <= 0xDFFF) || value > 0x10FFFF) {
                                throw ScannerError("while parsing a quoted scalar", start, "found invalid Unicode character escape code");
                            }

                            AppendCodePoint(sb, (int)value);

                            for (int k = 0; k < codeLength; k++) {
                                Skip();
                            }
                        }
                    } else {
                        // A non-escaped non-blank character.
                        Read(sb);
                    }

                    Cache(2);
                }

                Cache(1);
                if (Check(single ? '\'' : '"')) {
                    break;
                }

                Cache(1);
                while (IsBlank(At(0)) || IsBreak(At(0))) {
                    if (IsBlank(At(0))) {
                        if (!leadingBlanks) {
                            Read(whitespaces);
                        } else {
                            Skip();
                        }
                    } else {
                        Cache(2);
                        if (!leadingBlanks) {
                            whitespaces.Clear();
                            ReadLine(leadingBreak);
                            leadingBlanks = true;
                        } else {
                            ReadLine(trailingBreaks);
                        }
                    }
                    Cache(1);
                }

                if (leadingBlanks) {
                    if (leadingBreak.Length > 0 && leadingBreak[0] == '\n') {
                        if (trailingBreaks.Length == 0) {
                            sb.Append(' ');
                        } else {
                            sb.Append(trailingBreaks);
                            trailingBreaks.Clear();
                        }
                        leadingBreak.Clear();
                    } else {
                        sb.Append(leadingBreak);
                        sb.Append(trailingBreaks);
                        leadingBreak.Clear();
                        trailingBreaks.Clear();
                    }
                } else {
                    sb.Append(whitespaces);
                    whitespaces.Clear();
                }
            }

            // The right quote.
            Skip();

            var token = new LibyamlToken(TokenType.Scalar, start, CurrentMark);
            token.Value = sb.ToString();
            token.Style = single ? EmitterScalarStyle.SingleQuoted : EmitterScalarStyle.DoubleQuoted;
            return token;
        }

        private LibyamlToken/*!*/ ScanPlainScalar() {
            var sb = new StringBuilder();
            var leadingBreak = new StringBuilder();
            var trailingBreaks = new StringBuilder();
            var whitespaces = new StringBuilder();
            bool leadingBlanks = false;
            int indent = _indent + 1;

            Mark start = CurrentMark, end = CurrentMark;

            while (true) {
                Cache(4);
                if (_column == 0 &&
                    ((CheckAt('-', 0) && CheckAt('-', 1) && CheckAt('-', 2)) ||
                     (CheckAt('.', 0) && CheckAt('.', 1) && CheckAt('.', 2))) &&
                    IsBlankZ(At(3))) {
                    break;
                }

                if (Check('#')) {
                    break;
                }

                while (!IsBlankZ(At(0))) {
                    // "x:" followed by one of ',?[]{}' in the flow context.
                    if (_flowLevel != 0 && Check(':') &&
                        (CheckAt(',', 1) || CheckAt('?', 1) || CheckAt('[', 1) || CheckAt(']', 1) ||
                         CheckAt('{', 1) || CheckAt('}', 1))) {
                        throw ScannerError("while scanning a plain scalar", start, "found unexpected ':'");
                    }

                    // Indicators that may end a plain scalar.
                    if ((Check(':') && IsBlankZ(At(1))) ||
                        (_flowLevel != 0 && (Check(',') || Check('[') || Check(']') || Check('{') || Check('}')))) {
                        break;
                    }

                    if (leadingBlanks || whitespaces.Length > 0) {
                        if (leadingBlanks) {
                            if (leadingBreak.Length > 0 && leadingBreak[0] == '\n') {
                                if (trailingBreaks.Length == 0) {
                                    sb.Append(' ');
                                } else {
                                    sb.Append(trailingBreaks);
                                    trailingBreaks.Clear();
                                }
                                leadingBreak.Clear();
                            } else {
                                sb.Append(leadingBreak);
                                sb.Append(trailingBreaks);
                                leadingBreak.Clear();
                                trailingBreaks.Clear();
                            }
                            leadingBlanks = false;
                        } else {
                            sb.Append(whitespaces);
                            whitespaces.Clear();
                        }
                    }

                    Read(sb);
                    end = CurrentMark;
                    Cache(2);
                }

                if (!(IsBlank(At(0)) || IsBreak(At(0)))) {
                    break;
                }

                Cache(1);
                while (IsBlank(At(0)) || IsBreak(At(0))) {
                    if (IsBlank(At(0))) {
                        // Tabs that abuse indentation.
                        if (leadingBlanks && _column < indent && Check('\t')) {
                            throw ScannerError("while scanning a plain scalar", start, "found a tab character that violates indentation");
                        }

                        if (!leadingBlanks) {
                            Read(whitespaces);
                        } else {
                            Skip();
                        }
                    } else {
                        Cache(2);
                        if (!leadingBlanks) {
                            whitespaces.Clear();
                            ReadLine(leadingBreak);
                            leadingBlanks = true;
                        } else {
                            ReadLine(trailingBreaks);
                        }
                    }
                    Cache(1);
                }

                if (_flowLevel == 0 && _column < indent) {
                    break;
                }
            }

            var token = new LibyamlToken(TokenType.Scalar, start, end);
            token.Value = sb.ToString();
            token.Style = EmitterScalarStyle.Plain;

            if (leadingBlanks) {
                _simpleKeyAllowed = true;
            }

            return token;
        }

        #endregion
    }
}

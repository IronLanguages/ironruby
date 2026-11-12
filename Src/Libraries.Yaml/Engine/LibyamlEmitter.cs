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

    public enum EmitterEventKind {
        StreamStart,
        StreamEnd,
        DocumentStart,
        DocumentEnd,
        Alias,
        Scalar,
        SequenceStart,
        SequenceEnd,
        MappingStart,
        MappingEnd,
    }

    // libyaml's yaml_scalar_style_t and yaml_sequence_style_t / yaml_mapping_style_t, which are
    // also Psych::Nodes::Scalar's and Psych::Nodes::Sequence's style constants.
    public enum EmitterScalarStyle { Any = 0, Plain = 1, SingleQuoted = 2, DoubleQuoted = 3, Literal = 4, Folded = 5 }
    public enum EmitterCollectionStyle { Any = 0, Block = 1, Flow = 2 }

    /// <summary>
    /// One event, as LibyamlParser produces and LibyamlEmitter consumes it: libyaml's
    /// yaml_event_t, with the fields each kind uses.
    /// </summary>
    public sealed class LibyamlEvent {
        public EmitterEventKind Kind;
        public Mark Start;
        public Mark End;
        public string Anchor;
        public string Tag;
        public string Value;
        public bool PlainImplicit;
        public bool QuotedImplicit;
        // For a document start or end, and a collection start: libyaml's "implicit".
        public bool Implicit;
        public EmitterScalarStyle ScalarStyle;
        public EmitterCollectionStyle CollectionStyle;
        public int Encoding;
        // A document start's %YAML directive, or null.
        public Version Version;
        // A document start's %TAG directives: handle, prefix.
        public List<KeyValuePair<string, string>> TagDirectives;
    }

    /// <summary>
    /// An emitter error; Psych raises it as a RuntimeError with the problem as the message.
    /// </summary>
    public sealed class EmitterException : Exception {
        public EmitterException(string/*!*/ problem)
            : base(problem) {
        }
    }

    /// <summary>
    /// libyaml 0.2.5's emitter (emitter.c), ported. Psych::Emitter on CRuby is this code, so a
    /// tree written through it comes out byte for byte as CRuby's Psych writes it. Strings are
    /// handled as code points, so a column counts characters, as libyaml's does.
    /// </summary>
    public sealed class LibyamlEmitter {
        private enum State {
            StreamStart,
            FirstDocumentStart,
            DocumentStart,
            DocumentContent,
            DocumentEnd,
            FlowSequenceFirstItem,
            FlowSequenceItem,
            FlowMappingFirstKey,
            FlowMappingKey,
            FlowMappingSimpleValue,
            FlowMappingValue,
            BlockSequenceFirstItem,
            BlockSequenceItem,
            BlockMappingFirstKey,
            BlockMappingKey,
            BlockMappingSimpleValue,
            BlockMappingValue,
            End,
        }

        private readonly StringBuilder/*!*/ _output = new StringBuilder();

        private bool _canonical;
        private int _bestIndent;
        private int _bestWidth;
        private readonly bool _unicode = true;
        private int _encoding;

        private readonly Stack<State>/*!*/ _states = new Stack<State>();
        private State _state = State.StreamStart;
        private readonly List<LibyamlEvent>/*!*/ _events = new List<LibyamlEvent>();
        private readonly Stack<int>/*!*/ _indents = new Stack<int>();
        private readonly List<KeyValuePair<string, string>>/*!*/ _tagDirectives = new List<KeyValuePair<string, string>>();

        private int _indent;
        private int _flowLevel;
        private bool _rootContext, _sequenceContext, _mappingContext, _simpleKeyContext;
        private int _line, _column;
        private bool _whitespace, _indention;
        private int _openEnded;

        // What yaml_emitter_analyze_event found out about the event at the head of the queue.
        private string _anchor;
        private bool _alias;
        private string _tagHandle;
        private string _tagSuffix;
        private int[] _scalar;
        private bool _multiline;
        private bool _flowPlainAllowed, _blockPlainAllowed, _singleQuotedAllowed, _blockAllowed;
        private EmitterScalarStyle _style;

        /// <param name="indent">yaml_emitter_set_indent</param>
        /// <param name="width">yaml_emitter_set_width: negative is unlimited, 0 the default</param>
        public LibyamlEmitter(int indent, int width, bool canonical) {
            _bestIndent = (1 < indent && indent < 10) ? indent : 2;
            _bestWidth = (width >= 0) ? width : -1;
            _canonical = canonical;
        }

        /// <summary>
        /// The text written so far and not yet taken.
        /// </summary>
        public bool HasOutput {
            get { return _output.Length > 0; }
        }

        public string/*!*/ TakeOutput() {
            string result = _output.ToString();
            _output.Clear();
            return result;
        }

        /// <summary>
        /// The encoding the stream start asked for (libyaml's yaml_encoding_t).
        /// </summary>
        public int Encoding {
            get { return _encoding; }
        }

        private static Exception/*!*/ Error(string/*!*/ problem) {
            return new EmitterException(problem);
        }

        #region Emit

        public void Emit(LibyamlEvent/*!*/ e) {
            _events.Add(e);
            while (!NeedMoreEvents()) {
                AnalyzeEvent(_events[0]);
                StateMachine(_events[0]);
                _events.RemoveAt(0);
            }
        }

        // We accumulate extra
        //  - 1 event for DOCUMENT-START
        //  - 2 events for SEQUENCE-START
        //  - 3 events for MAPPING-START
        private bool NeedMoreEvents() {
            if (_events.Count == 0) {
                return true;
            }

            int accumulate;
            switch (_events[0].Kind) {
                case EmitterEventKind.DocumentStart: accumulate = 1; break;
                case EmitterEventKind.SequenceStart: accumulate = 2; break;
                case EmitterEventKind.MappingStart: accumulate = 3; break;
                default: return false;
            }

            if (_events.Count > accumulate) {
                return false;
            }

            int level = 0;
            foreach (LibyamlEvent e in _events) {
                switch (e.Kind) {
                    case EmitterEventKind.StreamStart:
                    case EmitterEventKind.DocumentStart:
                    case EmitterEventKind.SequenceStart:
                    case EmitterEventKind.MappingStart:
                        level += 1;
                        break;
                    case EmitterEventKind.StreamEnd:
                    case EmitterEventKind.DocumentEnd:
                    case EmitterEventKind.SequenceEnd:
                    case EmitterEventKind.MappingEnd:
                        level -= 1;
                        break;
                }
                if (level == 0) {
                    return false;
                }
            }

            return true;
        }

        private void AppendTagDirective(string/*!*/ handle, string/*!*/ prefix, bool allowDuplicates) {
            foreach (var directive in _tagDirectives) {
                if (directive.Key == handle) {
                    if (allowDuplicates) {
                        return;
                    }
                    throw Error("duplicate %TAG directive");
                }
            }
            _tagDirectives.Add(new KeyValuePair<string, string>(handle, prefix));
        }

        private void IncreaseIndent(bool flow, bool indentless) {
            _indents.Push(_indent);

            if (_indent < 0) {
                _indent = flow ? _bestIndent : 0;
            } else if (!indentless) {
                _indent += _bestIndent;
            }
        }

        private void StateMachine(LibyamlEvent/*!*/ e) {
            switch (_state) {
                case State.StreamStart: EmitStreamStart(e); break;
                case State.FirstDocumentStart: EmitDocumentStart(e, true); break;
                case State.DocumentStart: EmitDocumentStart(e, false); break;
                case State.DocumentContent: EmitDocumentContent(e); break;
                case State.DocumentEnd: EmitDocumentEnd(e); break;
                case State.FlowSequenceFirstItem: EmitFlowSequenceItem(e, true); break;
                case State.FlowSequenceItem: EmitFlowSequenceItem(e, false); break;
                case State.FlowMappingFirstKey: EmitFlowMappingKey(e, true); break;
                case State.FlowMappingKey: EmitFlowMappingKey(e, false); break;
                case State.FlowMappingSimpleValue: EmitFlowMappingValue(e, true); break;
                case State.FlowMappingValue: EmitFlowMappingValue(e, false); break;
                case State.BlockSequenceFirstItem: EmitBlockSequenceItem(e, true); break;
                case State.BlockSequenceItem: EmitBlockSequenceItem(e, false); break;
                case State.BlockMappingFirstKey: EmitBlockMappingKey(e, true); break;
                case State.BlockMappingKey: EmitBlockMappingKey(e, false); break;
                case State.BlockMappingSimpleValue: EmitBlockMappingValue(e, true); break;
                case State.BlockMappingValue: EmitBlockMappingValue(e, false); break;
                case State.End: throw Error("expected nothing after STREAM-END");
            }
        }

        private void EmitStreamStart(LibyamlEvent/*!*/ e) {
            _openEnded = 0;
            if (e.Kind != EmitterEventKind.StreamStart) {
                throw Error("expected STREAM-START");
            }

            _encoding = e.Encoding;
            if (_encoding == 0) {
                _encoding = 1;
            }

            if (_bestIndent < 2 || _bestIndent > 9) {
                _bestIndent = 2;
            }

            if (_bestWidth >= 0 && _bestWidth <= _bestIndent * 2) {
                _bestWidth = 80;
            }

            if (_bestWidth < 0) {
                _bestWidth = Int32.MaxValue;
            }

            _indent = -1;
            _line = 0;
            _column = 0;
            _whitespace = true;
            _indention = true;

            if (_encoding != 1) {
                // The byte order mark; the caller encodes the text as _encoding asks.
                _output.Append((char)0xFEFF);
            }

            _state = State.FirstDocumentStart;
        }

        private void EmitDocumentStart(LibyamlEvent/*!*/ e, bool first) {
            if (e.Kind == EmitterEventKind.DocumentStart) {
                bool hasDirectives = e.TagDirectives != null && e.TagDirectives.Count > 0;

                if (e.Version != null) {
                    AnalyzeVersionDirective(e.Version);
                }

                if (hasDirectives) {
                    foreach (var directive in e.TagDirectives) {
                        AnalyzeTagDirective(directive.Key, directive.Value);
                        AppendTagDirective(directive.Key, directive.Value, false);
                    }
                }

                AppendTagDirective("!", "!", true);
                AppendTagDirective("!!", "tag:yaml.org,2002:", true);

                bool @implicit = e.Implicit;
                if (!first || _canonical) {
                    @implicit = false;
                }

                if ((e.Version != null || hasDirectives) && _openEnded != 0) {
                    WriteIndicator("...", true, false, false);
                    WriteIndent();
                }
                _openEnded = 0;

                if (e.Version != null) {
                    @implicit = false;
                    WriteIndicator("%YAML", true, false, false);
                    WriteIndicator(e.Version.Minor == 1 ? "1.1" : "1.2", true, false, false);
                    WriteIndent();
                }

                if (hasDirectives) {
                    @implicit = false;
                    foreach (var directive in e.TagDirectives) {
                        WriteIndicator("%TAG", true, false, false);
                        WriteTagHandle(directive.Key);
                        WriteTagContent(directive.Value, true);
                        WriteIndent();
                    }
                }

                if (!@implicit) {
                    WriteIndent();
                    WriteIndicator("---", true, false, false);
                    if (_canonical) {
                        WriteIndent();
                    }
                }

                _state = State.DocumentContent;
                _openEnded = 0;
                return;
            }

            if (e.Kind == EmitterEventKind.StreamEnd) {
                // This can happen if a block scalar with trailing empty lines is at the end of
                // the stream.
                if (_openEnded == 2) {
                    WriteIndicator("...", true, false, false);
                    _openEnded = 0;
                    WriteIndent();
                }
                _state = State.End;
                return;
            }

            throw Error("expected DOCUMENT-START or STREAM-END");
        }

        private void EmitDocumentContent(LibyamlEvent/*!*/ e) {
            _states.Push(State.DocumentEnd);
            EmitNode(e, true, false, false, false);
        }

        private void EmitDocumentEnd(LibyamlEvent/*!*/ e) {
            if (e.Kind != EmitterEventKind.DocumentEnd) {
                throw Error("expected DOCUMENT-END");
            }

            WriteIndent();
            if (!e.Implicit) {
                WriteIndicator("...", true, false, false);
                _openEnded = 0;
                WriteIndent();
            } else if (_openEnded == 0) {
                _openEnded = 1;
            }

            _state = State.DocumentStart;
            _tagDirectives.Clear();
        }

        private void EmitFlowSequenceItem(LibyamlEvent/*!*/ e, bool first) {
            if (first) {
                WriteIndicator("[", true, true, false);
                IncreaseIndent(true, false);
                _flowLevel++;
            }

            if (e.Kind == EmitterEventKind.SequenceEnd) {
                _flowLevel--;
                _indent = _indents.Pop();
                if (_canonical && !first) {
                    WriteIndicator(",", false, false, false);
                    WriteIndent();
                }
                WriteIndicator("]", false, false, false);
                _state = _states.Pop();
                return;
            }

            if (!first) {
                WriteIndicator(",", false, false, false);
            }

            if (_canonical || _column > _bestWidth) {
                WriteIndent();
            }
            _states.Push(State.FlowSequenceItem);
            EmitNode(e, false, true, false, false);
        }

        private void EmitFlowMappingKey(LibyamlEvent/*!*/ e, bool first) {
            if (first) {
                WriteIndicator("{", true, true, false);
                IncreaseIndent(true, false);
                _flowLevel++;
            }

            if (e.Kind == EmitterEventKind.MappingEnd) {
                _flowLevel--;
                _indent = _indents.Pop();
                if (_canonical && !first) {
                    WriteIndicator(",", false, false, false);
                    WriteIndent();
                }
                WriteIndicator("}", false, false, false);
                _state = _states.Pop();
                return;
            }

            if (!first) {
                WriteIndicator(",", false, false, false);
            }
            if (_canonical || _column > _bestWidth) {
                WriteIndent();
            }

            if (!_canonical && CheckSimpleKey()) {
                _states.Push(State.FlowMappingSimpleValue);
                EmitNode(e, false, false, true, true);
            } else {
                WriteIndicator("?", true, false, false);
                _states.Push(State.FlowMappingValue);
                EmitNode(e, false, false, true, false);
            }
        }

        private void EmitFlowMappingValue(LibyamlEvent/*!*/ e, bool simple) {
            if (simple) {
                WriteIndicator(":", false, false, false);
            } else {
                if (_canonical || _column > _bestWidth) {
                    WriteIndent();
                }
                WriteIndicator(":", true, false, false);
            }
            _states.Push(State.FlowMappingKey);
            EmitNode(e, false, false, true, false);
        }

        private void EmitBlockSequenceItem(LibyamlEvent/*!*/ e, bool first) {
            if (first) {
                IncreaseIndent(false, _mappingContext && !_indention);
            }

            if (e.Kind == EmitterEventKind.SequenceEnd) {
                _indent = _indents.Pop();
                _state = _states.Pop();
                return;
            }

            WriteIndent();
            WriteIndicator("-", true, false, true);
            _states.Push(State.BlockSequenceItem);
            EmitNode(e, false, true, false, false);
        }

        private void EmitBlockMappingKey(LibyamlEvent/*!*/ e, bool first) {
            if (first) {
                IncreaseIndent(false, false);
            }

            if (e.Kind == EmitterEventKind.MappingEnd) {
                _indent = _indents.Pop();
                _state = _states.Pop();
                return;
            }

            WriteIndent();

            if (CheckSimpleKey()) {
                _states.Push(State.BlockMappingSimpleValue);
                EmitNode(e, false, false, true, true);
            } else {
                WriteIndicator("?", true, false, true);
                _states.Push(State.BlockMappingValue);
                EmitNode(e, false, false, true, false);
            }
        }

        private void EmitBlockMappingValue(LibyamlEvent/*!*/ e, bool simple) {
            if (simple) {
                WriteIndicator(":", false, false, false);
            } else {
                WriteIndent();
                WriteIndicator(":", true, false, true);
            }
            _states.Push(State.BlockMappingKey);
            EmitNode(e, false, false, true, false);
        }

        private void EmitNode(LibyamlEvent/*!*/ e, bool root, bool sequence, bool mapping, bool simpleKey) {
            _rootContext = root;
            _sequenceContext = sequence;
            _mappingContext = mapping;
            _simpleKeyContext = simpleKey;

            switch (e.Kind) {
                case EmitterEventKind.Alias: EmitAlias(); break;
                case EmitterEventKind.Scalar: EmitScalar(e); break;
                case EmitterEventKind.SequenceStart: EmitSequenceStart(e); break;
                case EmitterEventKind.MappingStart: EmitMappingStart(e); break;
                default: throw Error("expected SCALAR, SEQUENCE-START, MAPPING-START, or ALIAS");
            }
        }

        private void EmitAlias() {
            ProcessAnchor();
            if (_simpleKeyContext) {
                Put(' ');
            }
            _state = _states.Pop();
        }

        private void EmitScalar(LibyamlEvent/*!*/ e) {
            SelectScalarStyle(e);
            ProcessAnchor();
            ProcessTag();
            IncreaseIndent(true, false);
            ProcessScalar();
            _indent = _indents.Pop();
            _state = _states.Pop();
        }

        private void EmitSequenceStart(LibyamlEvent/*!*/ e) {
            ProcessAnchor();
            ProcessTag();

            if (_flowLevel != 0 || _canonical || e.CollectionStyle == EmitterCollectionStyle.Flow || CheckEmptySequence()) {
                _state = State.FlowSequenceFirstItem;
            } else {
                _state = State.BlockSequenceFirstItem;
            }
        }

        private void EmitMappingStart(LibyamlEvent/*!*/ e) {
            ProcessAnchor();
            ProcessTag();

            if (_flowLevel != 0 || _canonical || e.CollectionStyle == EmitterCollectionStyle.Flow || CheckEmptyMapping()) {
                _state = State.FlowMappingFirstKey;
            } else {
                _state = State.BlockMappingFirstKey;
            }
        }

        #endregion

        #region Checkers

        private bool CheckEmptySequence() {
            return _events.Count >= 2
                && _events[0].Kind == EmitterEventKind.SequenceStart
                && _events[1].Kind == EmitterEventKind.SequenceEnd;
        }

        private bool CheckEmptyMapping() {
            return _events.Count >= 2
                && _events[0].Kind == EmitterEventKind.MappingStart
                && _events[1].Kind == EmitterEventKind.MappingEnd;
        }

        private bool CheckSimpleKey() {
            LibyamlEvent e = _events[0];
            int length = 0;

            switch (e.Kind) {
                case EmitterEventKind.Alias:
                    length += Length(_anchor);
                    break;

                case EmitterEventKind.Scalar:
                    if (_multiline) {
                        return false;
                    }
                    length += Length(_anchor) + Length(_tagHandle) + Length(_tagSuffix) + _scalar.Length;
                    break;

                case EmitterEventKind.SequenceStart:
                    if (!CheckEmptySequence()) {
                        return false;
                    }
                    length += Length(_anchor) + Length(_tagHandle) + Length(_tagSuffix);
                    break;

                case EmitterEventKind.MappingStart:
                    if (!CheckEmptyMapping()) {
                        return false;
                    }
                    length += Length(_anchor) + Length(_tagHandle) + Length(_tagSuffix);
                    break;

                default:
                    return false;
            }

            return length <= 128;
        }

        // libyaml measures these in bytes.
        private static int Length(string str) {
            return (str == null) ? 0 : System.Text.Encoding.UTF8.GetByteCount(str);
        }

        private void SelectScalarStyle(LibyamlEvent/*!*/ e) {
            EmitterScalarStyle style = e.ScalarStyle;
            bool noTag = _tagHandle == null && _tagSuffix == null;

            if (noTag && !e.PlainImplicit && !e.QuotedImplicit) {
                throw Error("neither tag nor implicit flags are specified");
            }

            if (style == EmitterScalarStyle.Any) {
                style = EmitterScalarStyle.Plain;
            }

            if (_canonical) {
                style = EmitterScalarStyle.DoubleQuoted;
            }

            if (_simpleKeyContext && _multiline) {
                style = EmitterScalarStyle.DoubleQuoted;
            }

            if (style == EmitterScalarStyle.Plain) {
                if ((_flowLevel != 0 && !_flowPlainAllowed) || (_flowLevel == 0 && !_blockPlainAllowed)) {
                    style = EmitterScalarStyle.SingleQuoted;
                }
                if (_scalar.Length == 0 && (_flowLevel != 0 || _simpleKeyContext)) {
                    style = EmitterScalarStyle.SingleQuoted;
                }
                if (noTag && !e.PlainImplicit) {
                    style = EmitterScalarStyle.SingleQuoted;
                }
            }

            if (style == EmitterScalarStyle.SingleQuoted) {
                if (!_singleQuotedAllowed) {
                    style = EmitterScalarStyle.DoubleQuoted;
                }
            }

            if (style == EmitterScalarStyle.Literal || style == EmitterScalarStyle.Folded) {
                if (!_blockAllowed || _flowLevel != 0 || _simpleKeyContext) {
                    style = EmitterScalarStyle.DoubleQuoted;
                }
            }

            if (noTag && !e.QuotedImplicit && style != EmitterScalarStyle.Plain) {
                _tagHandle = "!";
            }

            _style = style;
        }

        #endregion

        #region Processors

        private void ProcessAnchor() {
            if (_anchor == null) {
                return;
            }
            WriteIndicator(_alias ? "*" : "&", true, false, false);
            WriteAnchor(_anchor);
        }

        private void ProcessTag() {
            if (_tagHandle == null && _tagSuffix == null) {
                return;
            }

            if (_tagHandle != null) {
                WriteTagHandle(_tagHandle);
                if (_tagSuffix != null) {
                    WriteTagContent(_tagSuffix, false);
                }
            } else {
                WriteIndicator("!<", true, false, false);
                WriteTagContent(_tagSuffix, false);
                WriteIndicator(">", false, false, false);
            }
        }

        private void ProcessScalar() {
            switch (_style) {
                case EmitterScalarStyle.Plain: WritePlainScalar(_scalar, !_simpleKeyContext); break;
                case EmitterScalarStyle.SingleQuoted: WriteSingleQuotedScalar(_scalar, !_simpleKeyContext); break;
                case EmitterScalarStyle.DoubleQuoted: WriteDoubleQuotedScalar(_scalar, !_simpleKeyContext); break;
                case EmitterScalarStyle.Literal: WriteLiteralScalar(_scalar); break;
                case EmitterScalarStyle.Folded: WriteFoldedScalar(_scalar); break;
            }
        }

        #endregion

        #region Analyzers

        private static void AnalyzeVersionDirective(Version/*!*/ version) {
            if (version.Major != 1 || (version.Minor != 1 && version.Minor != 2)) {
                throw Error("incompatible %YAML directive");
            }
        }

        private static void AnalyzeTagDirective(string handle, string prefix) {
            if (String.IsNullOrEmpty(handle)) {
                throw Error("tag handle must not be empty");
            }
            if (handle[0] != '!') {
                throw Error("tag handle must start with '!'");
            }
            if (handle[handle.Length - 1] != '!') {
                throw Error("tag handle must end with '!'");
            }
            for (int i = 1; i < handle.Length - 1; i++) {
                if (!IsAlpha(handle[i])) {
                    throw Error("tag handle must contain alphanumerical characters only");
                }
            }
            if (String.IsNullOrEmpty(prefix)) {
                throw Error("tag prefix must not be empty");
            }
        }

        private void AnalyzeAnchor(string/*!*/ anchor, bool alias) {
            if (anchor.Length == 0) {
                throw Error(alias ? "alias value must not be empty" : "anchor value must not be empty");
            }
            foreach (char c in anchor) {
                if (!IsAlpha(c)) {
                    throw Error(alias ?
                        "alias value must contain alphanumerical characters only" :
                        "anchor value must contain alphanumerical characters only");
                }
            }
            _anchor = anchor;
            _alias = alias;
        }

        private void AnalyzeTag(string/*!*/ tag) {
            if (tag.Length == 0) {
                throw Error("tag value must not be empty");
            }

            foreach (var directive in _tagDirectives) {
                string prefix = directive.Value;
                if (prefix.Length < tag.Length && tag.StartsWith(prefix, StringComparison.Ordinal)) {
                    _tagHandle = directive.Key;
                    _tagSuffix = tag.Substring(prefix.Length);
                    return;
                }
            }

            _tagSuffix = tag;
        }

        private void AnalyzeScalar(string/*!*/ value) {
            int[] s = ToCodePoints(value);
            _scalar = s;

            if (s.Length == 0) {
                _multiline = false;
                _flowPlainAllowed = false;
                _blockPlainAllowed = true;
                _singleQuotedAllowed = true;
                _blockAllowed = false;
                return;
            }

            bool blockIndicators = false;
            bool flowIndicators = false;
            bool lineBreaks = false;
            bool specialCharacters = false;

            bool leadingSpace = false;
            bool leadingBreak = false;
            bool trailingSpace = false;
            bool trailingBreak = false;
            bool breakSpace = false;
            bool spaceBreak = false;

            bool previousSpace = false;
            bool previousBreak = false;

            if ((At(s, 0) == '-' && At(s, 1) == '-' && At(s, 2) == '-') ||
                (At(s, 0) == '.' && At(s, 1) == '.' && At(s, 2) == '.')) {
                blockIndicators = true;
                flowIndicators = true;
            }

            bool precededByWhitespace = true;
            bool followedByWhitespace = IsBlankZ(At(s, 1));

            for (int i = 0; i < s.Length; i++) {
                int c = s[i];
                if (i == 0) {
                    if (c == '#' || c == ',' || c == '[' || c == ']' || c == '{' || c == '}' ||
                        c == '&' || c == '*' || c == '!' || c == '|' || c == '>' || c == '\'' ||
                        c == '"' || c == '%' || c == '@' || c == '`') {
                        flowIndicators = true;
                        blockIndicators = true;
                    }

                    if (c == '?' || c == ':') {
                        flowIndicators = true;
                        if (followedByWhitespace) {
                            blockIndicators = true;
                        }
                    }

                    if (c == '-' && followedByWhitespace) {
                        flowIndicators = true;
                        blockIndicators = true;
                    }
                } else {
                    if (c == ',' || c == '?' || c == '[' || c == ']' || c == '{' || c == '}') {
                        flowIndicators = true;
                    }

                    if (c == ':') {
                        flowIndicators = true;
                        if (followedByWhitespace) {
                            blockIndicators = true;
                        }
                    }

                    if (c == '#' && precededByWhitespace) {
                        flowIndicators = true;
                        blockIndicators = true;
                    }
                }

                if (!IsPrintable(c) || (!IsAscii(c) && !_unicode)) {
                    specialCharacters = true;
                }

                if (IsBreak(c)) {
                    lineBreaks = true;
                }

                if (c == ' ') {
                    if (i == 0) {
                        leadingSpace = true;
                    }
                    if (i == s.Length - 1) {
                        trailingSpace = true;
                    }
                    if (previousBreak) {
                        breakSpace = true;
                    }
                    previousSpace = true;
                    previousBreak = false;
                } else if (IsBreak(c)) {
                    if (i == 0) {
                        leadingBreak = true;
                    }
                    if (i == s.Length - 1) {
                        trailingBreak = true;
                    }
                    if (previousSpace) {
                        spaceBreak = true;
                    }
                    previousSpace = false;
                    previousBreak = true;
                } else {
                    previousSpace = false;
                    previousBreak = false;
                }

                precededByWhitespace = IsBlankZ(c);
                if (i + 1 < s.Length) {
                    followedByWhitespace = IsBlankZ(At(s, i + 2));
                }
            }

            _multiline = lineBreaks;

            _flowPlainAllowed = true;
            _blockPlainAllowed = true;
            _singleQuotedAllowed = true;
            _blockAllowed = true;

            if (leadingSpace || leadingBreak || trailingSpace || trailingBreak) {
                _flowPlainAllowed = false;
                _blockPlainAllowed = false;
            }

            if (trailingSpace) {
                _blockAllowed = false;
            }

            if (breakSpace) {
                _flowPlainAllowed = false;
                _blockPlainAllowed = false;
                _singleQuotedAllowed = false;
            }

            if (spaceBreak || specialCharacters) {
                _flowPlainAllowed = false;
                _blockPlainAllowed = false;
                _singleQuotedAllowed = false;
                _blockAllowed = false;
            }

            if (lineBreaks) {
                _flowPlainAllowed = false;
                _blockPlainAllowed = false;
            }

            if (flowIndicators) {
                _flowPlainAllowed = false;
            }

            if (blockIndicators) {
                _blockPlainAllowed = false;
            }
        }

        private void AnalyzeEvent(LibyamlEvent/*!*/ e) {
            _anchor = null;
            _alias = false;
            _tagHandle = null;
            _tagSuffix = null;
            _scalar = null;

            switch (e.Kind) {
                case EmitterEventKind.Alias:
                    AnalyzeAnchor(e.Anchor ?? String.Empty, true);
                    break;

                case EmitterEventKind.Scalar:
                    if (e.Anchor != null) {
                        AnalyzeAnchor(e.Anchor, false);
                    }
                    if (e.Tag != null && (_canonical || (!e.PlainImplicit && !e.QuotedImplicit))) {
                        AnalyzeTag(e.Tag);
                    }
                    AnalyzeScalar(e.Value ?? String.Empty);
                    break;

                case EmitterEventKind.SequenceStart:
                case EmitterEventKind.MappingStart:
                    if (e.Anchor != null) {
                        AnalyzeAnchor(e.Anchor, false);
                    }
                    if (e.Tag != null && (_canonical || !e.Implicit)) {
                        AnalyzeTag(e.Tag);
                    }
                    break;
            }
        }

        #endregion

        #region Writers

        private void Put(char c) {
            _output.Append(c);
            _column++;
        }

        private void PutBreak() {
            _output.Append('\n');
            _column = 0;
            _line++;
        }

        private void Write(int c) {
            AppendCodePoint(c);
            _column++;
        }

        private void WriteBreak(int c) {
            if (c == '\n') {
                PutBreak();
            } else {
                AppendCodePoint(c);
                _column = 0;
                _line++;
            }
        }

        private void AppendCodePoint(int c) {
            if (c < 0x10000) {
                _output.Append((char)c);
            } else {
                _output.Append(Char.ConvertFromUtf32(c));
            }
        }

        private void WriteIndent() {
            int indent = (_indent >= 0) ? _indent : 0;

            if (!_indention || _column > indent || (_column == indent && !_whitespace)) {
                PutBreak();
            }

            while (_column < indent) {
                Put(' ');
            }

            _whitespace = true;
            _indention = true;
        }

        private void WriteIndicator(string/*!*/ indicator, bool needWhitespace, bool isWhitespace, bool isIndention) {
            if (needWhitespace && !_whitespace) {
                Put(' ');
            }

            foreach (char c in indicator) {
                Write(c);
            }

            _whitespace = isWhitespace;
            _indention = _indention && isIndention;
        }

        private void WriteAnchor(string/*!*/ value) {
            foreach (int c in ToCodePoints(value)) {
                Write(c);
            }
            _whitespace = false;
            _indention = false;
        }

        private void WriteTagHandle(string/*!*/ value) {
            if (!_whitespace) {
                Put(' ');
            }
            foreach (int c in ToCodePoints(value)) {
                Write(c);
            }
            _whitespace = false;
            _indention = false;
        }

        private void WriteTagContent(string/*!*/ value, bool needWhitespace) {
            if (needWhitespace && !_whitespace) {
                Put(' ');
            }

            foreach (int c in ToCodePoints(value)) {
                if (IsAlpha(c) || c == ';' || c == '/' || c == '?' || c == ':' || c == '@' || c == '&' ||
                    c == '=' || c == '+' || c == '$' || c == ',' || c == '_' || c == '.' || c == '~' ||
                    c == '*' || c == '\'' || c == '(' || c == ')' || c == '[' || c == ']') {
                    Write(c);
                } else {
                    // Every byte of the character's UTF-8, %-escaped.
                    foreach (byte b in System.Text.Encoding.UTF8.GetBytes(Char.ConvertFromUtf32(c))) {
                        Put('%');
                        Put(HexDigit(b >> 4));
                        Put(HexDigit(b & 0x0F));
                    }
                }
            }

            _whitespace = false;
            _indention = false;
        }

        private void WritePlainScalar(int[]/*!*/ s, bool allowBreaks) {
            bool spaces = false;
            bool breaks = false;

            // Avoid trailing spaces for empty values in block mode.
            if (!_whitespace && (s.Length != 0 || _flowLevel != 0)) {
                Put(' ');
            }

            for (int i = 0; i < s.Length; i++) {
                int c = s[i];
                if (c == ' ') {
                    if (allowBreaks && !spaces && _column > _bestWidth && At(s, i + 1) != ' ') {
                        WriteIndent();
                    } else {
                        Write(c);
                    }
                    spaces = true;
                } else if (IsBreak(c)) {
                    if (!breaks && c == '\n') {
                        PutBreak();
                    }
                    WriteBreak(c);
                    _indention = true;
                    breaks = true;
                } else {
                    if (breaks) {
                        WriteIndent();
                    }
                    Write(c);
                    _indention = false;
                    spaces = false;
                    breaks = false;
                }
            }

            _whitespace = false;
            _indention = false;
        }

        private void WriteSingleQuotedScalar(int[]/*!*/ s, bool allowBreaks) {
            bool spaces = false;
            bool breaks = false;

            WriteIndicator("'", true, false, false);

            for (int i = 0; i < s.Length; i++) {
                int c = s[i];
                if (c == ' ') {
                    if (allowBreaks && !spaces && _column > _bestWidth && i != 0 && i != s.Length - 1 && At(s, i + 1) != ' ') {
                        WriteIndent();
                    } else {
                        Write(c);
                    }
                    spaces = true;
                } else if (IsBreak(c)) {
                    if (!breaks && c == '\n') {
                        PutBreak();
                    }
                    WriteBreak(c);
                    _indention = true;
                    breaks = true;
                } else {
                    if (breaks) {
                        WriteIndent();
                    }
                    if (c == '\'') {
                        Put('\'');
                    }
                    Write(c);
                    _indention = false;
                    spaces = false;
                    breaks = false;
                }
            }

            if (breaks) {
                WriteIndent();
            }

            WriteIndicator("'", false, false, false);

            _whitespace = false;
            _indention = false;
        }

        private void WriteDoubleQuotedScalar(int[]/*!*/ s, bool allowBreaks) {
            bool spaces = false;

            WriteIndicator("\"", true, false, false);

            for (int i = 0; i < s.Length; i++) {
                int c = s[i];
                if (!IsPrintable(c) || (!_unicode && !IsAscii(c)) || c == 0xFEFF || IsBreak(c) || c == '"' || c == '\\') {
                    Put('\\');

                    switch (c) {
                        case 0x00: Put('0'); break;
                        case 0x07: Put('a'); break;
                        case 0x08: Put('b'); break;
                        case 0x09: Put('t'); break;
                        case 0x0A: Put('n'); break;
                        case 0x0B: Put('v'); break;
                        case 0x0C: Put('f'); break;
                        case 0x0D: Put('r'); break;
                        case 0x1B: Put('e'); break;
                        case 0x22: Put('"'); break;
                        case 0x5C: Put('\\'); break;
                        case 0x85: Put('N'); break;
                        case 0xA0: Put('_'); break;
                        case 0x2028: Put('L'); break;
                        case 0x2029: Put('P'); break;
                        default:
                            int width;
                            if (c <= 0xFF) {
                                Put('x');
                                width = 2;
                            } else if (c <= 0xFFFF) {
                                Put('u');
                                width = 4;
                            } else {
                                Put('U');
                                width = 8;
                            }
                            for (int k = (width - 1) * 4; k >= 0; k -= 4) {
                                Put(HexDigit((c >> k) & 0x0F));
                            }
                            break;
                    }
                    spaces = false;
                } else if (c == ' ') {
                    if (allowBreaks && !spaces && _column > _bestWidth && i != 0 && i != s.Length - 1) {
                        WriteIndent();
                        if (At(s, i + 1) == ' ') {
                            Put('\\');
                        }
                    } else {
                        Write(c);
                    }
                    spaces = true;
                } else {
                    Write(c);
                    spaces = false;
                }
            }

            WriteIndicator("\"", false, false, false);

            _whitespace = false;
            _indention = false;
        }

        private void WriteBlockScalarHints(int[]/*!*/ s) {
            string chompHint = null;

            if (s.Length > 0 && (s[0] == ' ' || IsBreak(s[0]))) {
                WriteIndicator(((char)('0' + _bestIndent)).ToString(), false, false, false);
            }

            _openEnded = 0;

            if (s.Length == 0) {
                chompHint = "-";
            } else if (!IsBreak(s[s.Length - 1])) {
                chompHint = "-";
            } else if (s.Length == 1) {
                chompHint = "+";
                _openEnded = 2;
            } else if (IsBreak(s[s.Length - 2])) {
                chompHint = "+";
                _openEnded = 2;
            }

            if (chompHint != null) {
                WriteIndicator(chompHint, false, false, false);
            }
        }

        private void WriteLiteralScalar(int[]/*!*/ s) {
            bool breaks = true;

            WriteIndicator("|", true, false, false);
            WriteBlockScalarHints(s);
            PutBreak();
            _indention = true;
            _whitespace = true;

            foreach (int c in s) {
                if (IsBreak(c)) {
                    WriteBreak(c);
                    _indention = true;
                    breaks = true;
                } else {
                    if (breaks) {
                        WriteIndent();
                    }
                    Write(c);
                    _indention = false;
                    breaks = false;
                }
            }
        }

        private void WriteFoldedScalar(int[]/*!*/ s) {
            bool breaks = true;
            bool leadingSpaces = true;

            WriteIndicator(">", true, false, false);
            WriteBlockScalarHints(s);
            PutBreak();
            _indention = true;
            _whitespace = true;

            for (int i = 0; i < s.Length; i++) {
                int c = s[i];
                if (IsBreak(c)) {
                    if (!breaks && !leadingSpaces && c == '\n') {
                        int k = 0;
                        while (IsBreak(At(s, i + k))) {
                            k++;
                        }
                        if (!IsBlankZ(At(s, i + k))) {
                            PutBreak();
                        }
                    }
                    WriteBreak(c);
                    _indention = true;
                    breaks = true;
                } else {
                    if (breaks) {
                        WriteIndent();
                        leadingSpaces = IsBlank(c);
                    }
                    if (!breaks && c == ' ' && At(s, i + 1) != ' ' && _column > _bestWidth) {
                        WriteIndent();
                    } else {
                        Write(c);
                    }
                    _indention = false;
                    breaks = false;
                }
            }
        }

        #endregion

        #region Characters

        private static int[]/*!*/ ToCodePoints(string/*!*/ str) {
            var result = new List<int>(str.Length);
            for (int i = 0; i < str.Length; i++) {
                char c = str[i];
                if (Char.IsHighSurrogate(c) && i + 1 < str.Length && Char.IsLowSurrogate(str[i + 1])) {
                    result.Add(Char.ConvertToUtf32(c, str[i + 1]));
                    i++;
                } else {
                    result.Add(c);
                }
            }
            return result.ToArray();
        }

        // The character at +index+, or NUL past the end (libyaml's strings are NUL-terminated).
        private static int At(int[]/*!*/ s, int index) {
            return (index < s.Length) ? s[index] : 0;
        }

        private static char HexDigit(int value) {
            return (char)(value < 10 ? '0' + value : 'A' + value - 10);
        }

        private static bool IsAlpha(int c) {
            return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == '-';
        }

        private static bool IsAscii(int c) {
            return c <= 0x7F;
        }

        // libyaml's IS_PRINTABLE, which is a test on UTF-8 bytes: #x0A, #x20-#x7E, #xA0-#xD7FF
        // and #xE000-#xFFFD without the byte order mark. Nothing beyond the BMP counts.
        private static bool IsPrintable(int c) {
            return c == 0x0A
                || (c >= 0x20 && c <= 0x7E)
                || (c >= 0xA0 && c <= 0xD7FF)
                || (c >= 0xE000 && c <= 0xFFFD && c != 0xFEFF);
        }

        private static bool IsBreak(int c) {
            return c == '\r' || c == '\n' || c == 0x85 || c == 0x2028 || c == 0x2029;
        }

        private static bool IsBlank(int c) {
            return c == ' ' || c == '\t';
        }

        private static bool IsBlankZ(int c) {
            return IsBlank(c) || IsBreak(c) || c == 0;
        }

        #endregion
    }
}

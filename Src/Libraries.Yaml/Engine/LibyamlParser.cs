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

namespace IronRuby.StandardLibrary.Yaml {

    /// <summary>
    /// libyaml 0.2.5's parser (parser.c), ported: the scanner's tokens in, events out.
    /// </summary>
    internal sealed class LibyamlParser {
        private enum State {
            StreamStart,
            ImplicitDocumentStart,
            DocumentStart,
            DocumentContent,
            DocumentEnd,
            BlockNode,
            BlockNodeOrIndentlessSequence,
            FlowNode,
            BlockSequenceFirstEntry,
            BlockSequenceEntry,
            IndentlessSequenceEntry,
            BlockMappingFirstKey,
            BlockMappingKey,
            BlockMappingValue,
            FlowSequenceFirstEntry,
            FlowSequenceEntry,
            FlowSequenceEntryMappingKey,
            FlowSequenceEntryMappingValue,
            FlowSequenceEntryMappingEnd,
            FlowMappingFirstKey,
            FlowMappingKey,
            FlowMappingValue,
            FlowMappingEmptyValue,
            End,
        }

        private readonly LibyamlScanner/*!*/ _scanner;
        private State _state = State.StreamStart;
        private readonly Stack<State>/*!*/ _states = new Stack<State>();
        private readonly Stack<Mark>/*!*/ _marks = new Stack<Mark>();
        private readonly List<KeyValuePair<string, string>>/*!*/ _tagDirectives = new List<KeyValuePair<string, string>>();
        private bool _done;

        internal LibyamlParser(LibyamlScanner/*!*/ scanner) {
            _scanner = scanner;
        }

        internal LibyamlScanner/*!*/ Scanner {
            get { return _scanner; }
        }

        /// <summary>
        /// The next event, or null after STREAM-END.
        /// </summary>
        internal LibyamlEvent Parse() {
            if (_done || _state == State.End) {
                return null;
            }
            LibyamlEvent e = StateMachine();
            if (e.Kind == EmitterEventKind.StreamEnd) {
                _done = true;
            }
            return e;
        }

        private static Exception/*!*/ Error(string/*!*/ problem, Mark problemMark) {
            return new LibyamlException(problem, null, default(Mark), problemMark, 0);
        }

        private static Exception/*!*/ Error(string/*!*/ context, Mark contextMark, string/*!*/ problem, Mark problemMark) {
            return new LibyamlException(problem, context, contextMark, problemMark, 0);
        }

        private LibyamlToken/*!*/ PeekToken() {
            return _scanner.Peek();
        }

        private void SkipToken() {
            _scanner.SkipToken();
        }

        private static LibyamlEvent/*!*/ Event(EmitterEventKind kind, Mark start, Mark end) {
            var e = new LibyamlEvent();
            e.Kind = kind;
            e.Start = start;
            e.End = end;
            return e;
        }

        private LibyamlEvent/*!*/ StateMachine() {
            switch (_state) {
                case State.StreamStart: return ParseStreamStart();
                case State.ImplicitDocumentStart: return ParseDocumentStart(true);
                case State.DocumentStart: return ParseDocumentStart(false);
                case State.DocumentContent: return ParseDocumentContent();
                case State.DocumentEnd: return ParseDocumentEnd();
                case State.BlockNode: return ParseNode(true, false);
                case State.BlockNodeOrIndentlessSequence: return ParseNode(true, true);
                case State.FlowNode: return ParseNode(false, false);
                case State.BlockSequenceFirstEntry: return ParseBlockSequenceEntry(true);
                case State.BlockSequenceEntry: return ParseBlockSequenceEntry(false);
                case State.IndentlessSequenceEntry: return ParseIndentlessSequenceEntry();
                case State.BlockMappingFirstKey: return ParseBlockMappingKey(true);
                case State.BlockMappingKey: return ParseBlockMappingKey(false);
                case State.BlockMappingValue: return ParseBlockMappingValue();
                case State.FlowSequenceFirstEntry: return ParseFlowSequenceEntry(true);
                case State.FlowSequenceEntry: return ParseFlowSequenceEntry(false);
                case State.FlowSequenceEntryMappingKey: return ParseFlowSequenceEntryMappingKey();
                case State.FlowSequenceEntryMappingValue: return ParseFlowSequenceEntryMappingValue();
                case State.FlowSequenceEntryMappingEnd: return ParseFlowSequenceEntryMappingEnd();
                case State.FlowMappingFirstKey: return ParseFlowMappingKey(true);
                case State.FlowMappingKey: return ParseFlowMappingKey(false);
                case State.FlowMappingValue: return ParseFlowMappingValue(false);
                case State.FlowMappingEmptyValue: return ParseFlowMappingValue(true);
                default: throw new InvalidOperationException("invalid parser state");
            }
        }

        private LibyamlEvent/*!*/ ParseStreamStart() {
            LibyamlToken token = PeekToken();

            if (token.Type != TokenType.StreamStart) {
                throw Error("did not find expected <stream-start>", token.Start);
            }

            _state = State.ImplicitDocumentStart;
            LibyamlEvent e = Event(EmitterEventKind.StreamStart, token.Start, token.Start);
            e.Encoding = token.Encoding;
            SkipToken();
            return e;
        }

        private LibyamlEvent/*!*/ ParseDocumentStart(bool @implicit) {
            LibyamlToken token = PeekToken();

            // Extra document end indicators.
            if (!@implicit) {
                while (token.Type == TokenType.DocumentEnd) {
                    SkipToken();
                    token = PeekToken();
                }
            }

            if (@implicit && token.Type != TokenType.VersionDirective && token.Type != TokenType.TagDirective &&
                token.Type != TokenType.DocumentStart && token.Type != TokenType.StreamEnd) {
                // An implicit document.
                Version version;
                List<KeyValuePair<string, string>> directives;
                ProcessDirectives(out version, out directives);
                _states.Push(State.DocumentEnd);
                _state = State.BlockNode;
                LibyamlEvent e = Event(EmitterEventKind.DocumentStart, token.Start, token.Start);
                e.Implicit = true;
                return e;
            } else if (token.Type != TokenType.StreamEnd) {
                // An explicit document.
                Mark start = token.Start;
                Version version;
                List<KeyValuePair<string, string>> directives;
                ProcessDirectives(out version, out directives);
                token = PeekToken();
                if (token.Type != TokenType.DocumentStart) {
                    throw Error("did not find expected <document start>", token.Start);
                }
                _states.Push(State.DocumentEnd);
                _state = State.DocumentContent;
                LibyamlEvent e = Event(EmitterEventKind.DocumentStart, start, token.End);
                e.Version = version;
                e.TagDirectives = directives;
                e.Implicit = false;
                SkipToken();
                return e;
            } else {
                // The stream end.
                _state = State.End;
                LibyamlEvent e = Event(EmitterEventKind.StreamEnd, token.Start, token.End);
                SkipToken();
                return e;
            }
        }

        private LibyamlEvent/*!*/ ParseDocumentContent() {
            LibyamlToken token = PeekToken();

            if (token.Type == TokenType.VersionDirective || token.Type == TokenType.TagDirective ||
                token.Type == TokenType.DocumentStart || token.Type == TokenType.DocumentEnd ||
                token.Type == TokenType.StreamEnd) {
                _state = _states.Pop();
                return EmptyScalar(token.Start);
            }

            return ParseNode(true, false);
        }

        private LibyamlEvent/*!*/ ParseDocumentEnd() {
            LibyamlToken token = PeekToken();
            Mark start = token.Start, end = token.Start;
            bool @implicit = true;

            if (token.Type == TokenType.DocumentEnd) {
                end = token.End;
                SkipToken();
                @implicit = false;
            }

            _tagDirectives.Clear();

            _state = State.DocumentStart;
            LibyamlEvent e = Event(EmitterEventKind.DocumentEnd, start, end);
            e.Implicit = @implicit;
            return e;
        }

        private LibyamlEvent/*!*/ ParseNode(bool block, bool indentlessSequence) {
            LibyamlToken token = PeekToken();
            string anchor = null;
            string tagHandle = null;
            string tagSuffix = null;
            string tag = null;
            Mark start, end, tagMark = default(Mark);

            if (token.Type == TokenType.Alias) {
                _state = _states.Pop();
                LibyamlEvent alias = Event(EmitterEventKind.Alias, token.Start, token.End);
                alias.Anchor = token.Value;
                SkipToken();
                return alias;
            }

            start = end = token.Start;

            if (token.Type == TokenType.Anchor) {
                anchor = token.Value;
                start = token.Start;
                end = token.End;
                SkipToken();
                token = PeekToken();
                if (token.Type == TokenType.Tag) {
                    tagHandle = token.Handle;
                    tagSuffix = token.Suffix;
                    tagMark = token.Start;
                    end = token.End;
                    SkipToken();
                    token = PeekToken();
                }
            } else if (token.Type == TokenType.Tag) {
                tagHandle = token.Handle;
                tagSuffix = token.Suffix;
                start = tagMark = token.Start;
                end = token.End;
                SkipToken();
                token = PeekToken();
                if (token.Type == TokenType.Anchor) {
                    anchor = token.Value;
                    end = token.End;
                    SkipToken();
                    token = PeekToken();
                }
            }

            if (tagHandle != null) {
                if (tagHandle.Length == 0) {
                    tag = tagSuffix;
                } else {
                    foreach (var directive in _tagDirectives) {
                        if (directive.Key == tagHandle) {
                            tag = directive.Value + tagSuffix;
                            break;
                        }
                    }
                    if (tag == null) {
                        throw Error("while parsing a node", start, "found undefined tag handle", tagMark);
                    }
                }
            }

            bool @implicit = String.IsNullOrEmpty(tag);

            if (indentlessSequence && token.Type == TokenType.BlockEntry) {
                end = token.End;
                _state = State.IndentlessSequenceEntry;
                return CollectionStart(EmitterEventKind.SequenceStart, anchor, tag, @implicit, EmitterCollectionStyle.Block, start, end);
            }

            if (token.Type == TokenType.Scalar) {
                bool plainImplicit = false;
                bool quotedImplicit = false;
                end = token.End;
                if ((token.Style == EmitterScalarStyle.Plain && tag == null) || tag == "!") {
                    plainImplicit = true;
                } else if (tag == null) {
                    quotedImplicit = true;
                }
                _state = _states.Pop();
                LibyamlEvent e = Event(EmitterEventKind.Scalar, start, end);
                e.Anchor = anchor;
                e.Tag = tag;
                e.Value = token.Value;
                e.PlainImplicit = plainImplicit;
                e.QuotedImplicit = quotedImplicit;
                e.ScalarStyle = token.Style;
                SkipToken();
                return e;
            }

            if (token.Type == TokenType.FlowSequenceStart) {
                end = token.End;
                _state = State.FlowSequenceFirstEntry;
                return CollectionStart(EmitterEventKind.SequenceStart, anchor, tag, @implicit, EmitterCollectionStyle.Flow, start, end);
            }

            if (token.Type == TokenType.FlowMappingStart) {
                end = token.End;
                _state = State.FlowMappingFirstKey;
                return CollectionStart(EmitterEventKind.MappingStart, anchor, tag, @implicit, EmitterCollectionStyle.Flow, start, end);
            }

            if (block && token.Type == TokenType.BlockSequenceStart) {
                end = token.End;
                _state = State.BlockSequenceFirstEntry;
                return CollectionStart(EmitterEventKind.SequenceStart, anchor, tag, @implicit, EmitterCollectionStyle.Block, start, end);
            }

            if (block && token.Type == TokenType.BlockMappingStart) {
                end = token.End;
                _state = State.BlockMappingFirstKey;
                return CollectionStart(EmitterEventKind.MappingStart, anchor, tag, @implicit, EmitterCollectionStyle.Block, start, end);
            }

            if (anchor != null || tag != null) {
                // Properties with no content: an empty plain scalar carries them.
                _state = _states.Pop();
                LibyamlEvent e = Event(EmitterEventKind.Scalar, start, end);
                e.Anchor = anchor;
                e.Tag = tag;
                e.Value = "";
                e.PlainImplicit = @implicit;
                e.QuotedImplicit = false;
                e.ScalarStyle = EmitterScalarStyle.Plain;
                return e;
            }

            throw Error(block ? "while parsing a block node" : "while parsing a flow node", start,
                "did not find expected node content", token.Start);
        }

        private static LibyamlEvent/*!*/ CollectionStart(EmitterEventKind kind, string anchor, string tag, bool @implicit,
            EmitterCollectionStyle style, Mark start, Mark end) {
            LibyamlEvent e = Event(kind, start, end);
            e.Anchor = anchor;
            e.Tag = tag;
            e.Implicit = @implicit;
            e.CollectionStyle = style;
            return e;
        }

        private LibyamlEvent/*!*/ ParseBlockSequenceEntry(bool first) {
            LibyamlToken token;

            if (first) {
                token = PeekToken();
                _marks.Push(token.Start);
                SkipToken();
            }

            token = PeekToken();

            if (token.Type == TokenType.BlockEntry) {
                Mark mark = token.End;
                SkipToken();
                token = PeekToken();
                if (token.Type != TokenType.BlockEntry && token.Type != TokenType.BlockEnd) {
                    _states.Push(State.BlockSequenceEntry);
                    return ParseNode(true, false);
                }
                _state = State.BlockSequenceEntry;
                return EmptyScalar(mark);
            }

            if (token.Type == TokenType.BlockEnd) {
                _state = _states.Pop();
                _marks.Pop();
                LibyamlEvent e = Event(EmitterEventKind.SequenceEnd, token.Start, token.End);
                SkipToken();
                return e;
            }

            throw Error("while parsing a block collection", _marks.Pop(), "did not find expected '-' indicator", token.Start);
        }

        private LibyamlEvent/*!*/ ParseIndentlessSequenceEntry() {
            LibyamlToken token = PeekToken();

            if (token.Type == TokenType.BlockEntry) {
                Mark mark = token.End;
                SkipToken();
                token = PeekToken();
                if (token.Type != TokenType.BlockEntry && token.Type != TokenType.Key &&
                    token.Type != TokenType.Value && token.Type != TokenType.BlockEnd) {
                    _states.Push(State.IndentlessSequenceEntry);
                    return ParseNode(true, false);
                }
                _state = State.IndentlessSequenceEntry;
                return EmptyScalar(mark);
            }

            _state = _states.Pop();
            return Event(EmitterEventKind.SequenceEnd, token.Start, token.Start);
        }

        private LibyamlEvent/*!*/ ParseBlockMappingKey(bool first) {
            LibyamlToken token;

            if (first) {
                token = PeekToken();
                _marks.Push(token.Start);
                SkipToken();
            }

            token = PeekToken();

            if (token.Type == TokenType.Key) {
                Mark mark = token.End;
                SkipToken();
                token = PeekToken();
                if (token.Type != TokenType.Key && token.Type != TokenType.Value && token.Type != TokenType.BlockEnd) {
                    _states.Push(State.BlockMappingValue);
                    return ParseNode(true, true);
                }
                _state = State.BlockMappingValue;
                return EmptyScalar(mark);
            }

            if (token.Type == TokenType.BlockEnd) {
                _state = _states.Pop();
                _marks.Pop();
                LibyamlEvent e = Event(EmitterEventKind.MappingEnd, token.Start, token.End);
                SkipToken();
                return e;
            }

            throw Error("while parsing a block mapping", _marks.Pop(), "did not find expected key", token.Start);
        }

        private LibyamlEvent/*!*/ ParseBlockMappingValue() {
            LibyamlToken token = PeekToken();

            if (token.Type == TokenType.Value) {
                Mark mark = token.End;
                SkipToken();
                token = PeekToken();
                if (token.Type != TokenType.Key && token.Type != TokenType.Value && token.Type != TokenType.BlockEnd) {
                    _states.Push(State.BlockMappingKey);
                    return ParseNode(true, true);
                }
                _state = State.BlockMappingKey;
                return EmptyScalar(mark);
            }

            _state = State.BlockMappingKey;
            return EmptyScalar(token.Start);
        }

        private LibyamlEvent/*!*/ ParseFlowSequenceEntry(bool first) {
            LibyamlToken token;

            if (first) {
                token = PeekToken();
                _marks.Push(token.Start);
                SkipToken();
            }

            token = PeekToken();

            if (token.Type != TokenType.FlowSequenceEnd) {
                if (!first) {
                    if (token.Type == TokenType.FlowEntry) {
                        SkipToken();
                        token = PeekToken();
                    } else {
                        throw Error("while parsing a flow sequence", _marks.Pop(), "did not find expected ',' or ']'", token.Start);
                    }
                }

                if (token.Type == TokenType.Key) {
                    _state = State.FlowSequenceEntryMappingKey;
                    LibyamlEvent e = CollectionStart(EmitterEventKind.MappingStart, null, null, true, EmitterCollectionStyle.Flow,
                        token.Start, token.End);
                    SkipToken();
                    return e;
                }

                if (token.Type != TokenType.FlowSequenceEnd) {
                    _states.Push(State.FlowSequenceEntry);
                    return ParseNode(false, false);
                }
            }

            _state = _states.Pop();
            _marks.Pop();
            LibyamlEvent end = Event(EmitterEventKind.SequenceEnd, token.Start, token.End);
            SkipToken();
            return end;
        }

        private LibyamlEvent/*!*/ ParseFlowSequenceEntryMappingKey() {
            LibyamlToken token = PeekToken();

            if (token.Type != TokenType.Value && token.Type != TokenType.FlowEntry && token.Type != TokenType.FlowSequenceEnd) {
                _states.Push(State.FlowSequenceEntryMappingValue);
                return ParseNode(false, false);
            }

            Mark mark = token.End;
            SkipToken();
            _state = State.FlowSequenceEntryMappingValue;
            return EmptyScalar(mark);
        }

        private LibyamlEvent/*!*/ ParseFlowSequenceEntryMappingValue() {
            LibyamlToken token = PeekToken();

            if (token.Type == TokenType.Value) {
                SkipToken();
                token = PeekToken();
                if (token.Type != TokenType.FlowEntry && token.Type != TokenType.FlowSequenceEnd) {
                    _states.Push(State.FlowSequenceEntryMappingEnd);
                    return ParseNode(false, false);
                }
            }

            _state = State.FlowSequenceEntryMappingEnd;
            return EmptyScalar(token.Start);
        }

        private LibyamlEvent/*!*/ ParseFlowSequenceEntryMappingEnd() {
            LibyamlToken token = PeekToken();
            _state = State.FlowSequenceEntry;
            return Event(EmitterEventKind.MappingEnd, token.Start, token.Start);
        }

        private LibyamlEvent/*!*/ ParseFlowMappingKey(bool first) {
            LibyamlToken token;

            if (first) {
                token = PeekToken();
                _marks.Push(token.Start);
                SkipToken();
            }

            token = PeekToken();

            if (token.Type != TokenType.FlowMappingEnd) {
                if (!first) {
                    if (token.Type == TokenType.FlowEntry) {
                        SkipToken();
                        token = PeekToken();
                    } else {
                        throw Error("while parsing a flow mapping", _marks.Pop(), "did not find expected ',' or '}'", token.Start);
                    }
                }

                if (token.Type == TokenType.Key) {
                    SkipToken();
                    token = PeekToken();
                    if (token.Type != TokenType.Value && token.Type != TokenType.FlowEntry && token.Type != TokenType.FlowMappingEnd) {
                        _states.Push(State.FlowMappingValue);
                        return ParseNode(false, false);
                    }
                    _state = State.FlowMappingValue;
                    return EmptyScalar(token.Start);
                }

                if (token.Type != TokenType.FlowMappingEnd) {
                    _states.Push(State.FlowMappingEmptyValue);
                    return ParseNode(false, false);
                }
            }

            _state = _states.Pop();
            _marks.Pop();
            LibyamlEvent end = Event(EmitterEventKind.MappingEnd, token.Start, token.End);
            SkipToken();
            return end;
        }

        private LibyamlEvent/*!*/ ParseFlowMappingValue(bool empty) {
            LibyamlToken token = PeekToken();

            if (empty) {
                _state = State.FlowMappingKey;
                return EmptyScalar(token.Start);
            }

            if (token.Type == TokenType.Value) {
                SkipToken();
                token = PeekToken();
                if (token.Type != TokenType.FlowEntry && token.Type != TokenType.FlowMappingEnd) {
                    _states.Push(State.FlowMappingKey);
                    return ParseNode(false, false);
                }
            }

            _state = State.FlowMappingKey;
            return EmptyScalar(token.Start);
        }

        private static LibyamlEvent/*!*/ EmptyScalar(Mark mark) {
            LibyamlEvent e = Event(EmitterEventKind.Scalar, mark, mark);
            e.Value = "";
            e.PlainImplicit = true;
            e.QuotedImplicit = false;
            e.ScalarStyle = EmitterScalarStyle.Plain;
            return e;
        }

        private void ProcessDirectives(out Version version, out List<KeyValuePair<string, string>> directives) {
            version = null;
            directives = null;

            LibyamlToken token = PeekToken();

            while (token.Type == TokenType.VersionDirective || token.Type == TokenType.TagDirective) {
                if (token.Type == TokenType.VersionDirective) {
                    if (version != null) {
                        throw Error("found duplicate %YAML directive", token.Start);
                    }
                    if (token.Major != 1 || (token.Minor != 1 && token.Minor != 2)) {
                        throw Error("found incompatible YAML document", token.Start);
                    }
                    version = new Version(token.Major, token.Minor);
                } else {
                    AppendTagDirective(token.Handle, token.Suffix, false, token.Start);
                    if (directives == null) {
                        directives = new List<KeyValuePair<string, string>>();
                    }
                    directives.Add(new KeyValuePair<string, string>(token.Handle, token.Suffix));
                }

                SkipToken();
                token = PeekToken();
            }

            AppendTagDirective("!", "!", true, token.Start);
            AppendTagDirective("!!", "tag:yaml.org,2002:", true, token.Start);
        }

        private void AppendTagDirective(string/*!*/ handle, string/*!*/ prefix, bool allowDuplicates, Mark mark) {
            foreach (var directive in _tagDirectives) {
                if (directive.Key == handle) {
                    if (allowDuplicates) {
                        return;
                    }
                    throw Error("found duplicate %TAG directive", mark);
                }
            }
            _tagDirectives.Add(new KeyValuePair<string, string>(handle, prefix));
        }
    }
}

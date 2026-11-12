/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using Microsoft.Scripting.Utils;

namespace IronRuby.Runtime {

    [Flags]
    public enum CoverageModes {
        // Coverage.start without arguments: MRI's "compatible" mode, lines reported as bare arrays
        Legacy = 0,
        Lines = 0x1,
        Branches = 0x2,
        Methods = 0x4,
        OneshotLines = 0x8,
        Eval = 0x10,
    }

    /// <summary>
    /// The state behind the Coverage library (MRI's rb_get_coverages): set up by Coverage.setup,
    /// counting while resumed. Line coverage piggybacks on the TracePoint :line hooks - code
    /// compiled while coverage is set up counts into a <see cref="LineCoverage"/> at each hook.
    /// Branch and method coverage are not implemented.
    /// </summary>
    public sealed class CoverageState {
        /// <summary>
        /// Read by compiled code before counting.
        /// </summary>
        public bool Resumed;

        private readonly CoverageModes _modes;
        private readonly Dictionary<string, LineCoverage>/*!*/ _files = new Dictionary<string, LineCoverage>(StringComparer.Ordinal);
        private readonly List<LineCoverage>/*!*/ _order = new List<LineCoverage>();

        public CoverageState(CoverageModes modes) {
            _modes = modes;
        }

        public CoverageModes Modes {
            get { return _modes; }
        }

        public List<LineCoverage>/*!*/ GetFiles() {
            lock (_files) {
                return new List<LineCoverage>(_order);
            }
        }

        /// <summary>
        /// The counters code compiled from <paramref name="ast"/> counts into, or null if it is not
        /// measured. Like MRI, a file is measured when it is loaded; eval'd code only in :eval mode,
        /// counting into the file it claims to come from (a new entry if that is not measured).
        /// </summary>
        internal LineCoverage GetCoverage(SourceUnitTree/*!*/ ast, string path, bool isEval, int firstLine, string/*!*/ code) {
            // MRI does not measure an eval not given a file name
            if (path == null || isEval && ((_modes & CoverageModes.Eval) == 0 || path.StartsWith("(eval", StringComparison.Ordinal))) {
                return null;
            }

            LineCoverage result;
            lock (_files) {
                LineCoverage existing;
                if (!_files.TryGetValue(path, out existing)) {
                    result = new LineCoverage(this, path, Math.Max(firstLine - 1, 0) + CountLines(code));
                    _files[path] = result;
                    _order.Add(result);
                } else if (isEval) {
                    result = existing;
                } else {
                    // a file loaded again starts over (MRI replaces the entry, which keeps its place)
                    result = new LineCoverage(this, path, CountLines(code));
                    _files[path] = result;
                    _order[_order.IndexOf(existing)] = result;
                }
            }

            new LineCollector(result).Walk(ast);
            return result;
        }

        private static int CountLines(string/*!*/ code) {
            int count = 0;
            foreach (char c in code) {
                if (c == '\n') {
                    count++;
                }
            }
            if (code.Length > 0 && code[code.Length - 1] != '\n') {
                count++;
            }
            return count;
        }

        /// <summary>
        /// Coverage.result(clear: true): the counters go back to zero, the lines stay.
        /// </summary>
        public void Clear() {
            foreach (var file in GetFiles()) {
                file.Clear();
            }
        }

        /// <summary>
        /// Marks the lines of a tree that get a :line hook (see AstGenerator.TraceLine) as lines
        /// the coverage reports - method bodies too, which are only compiled when first called.
        /// </summary>
        private sealed class LineCollector : Walker {
            private readonly LineCoverage/*!*/ _coverage;

            public LineCollector(LineCoverage/*!*/ coverage) {
                _coverage = coverage;
            }

            // the default walk does not go into the block of a call
            protected internal override void Walk(MethodCall/*!*/ node) {
                base.Walk(node);
                if (node.Block != null) {
                    node.Block.Walk(this);
                }
            }

            protected internal override void Walk(SuperCall/*!*/ node) {
                base.Walk(node);
                if (node.Block != null) {
                    node.Block.Walk(this);
                }
            }

            protected internal override void Walk(ParallelAssignmentExpression/*!*/ node) {
                base.Walk(node);
                foreach (var value in node.Right) {
                    value.Walk(this);
                }
            }

            protected internal override void Walk(ElseIfClause/*!*/ node) {
                if (node.IsElsif) {
                    _coverage.MarkLine(node.Location.Start.Line);
                }
                base.Walk(node);
            }

            protected internal override void VisitStatements(Statements/*!*/ statements) {
                for (int i = 0; i < statements.Count; i++) {
                    if (!AstGenerator.IsTransparentStatement(statements[i])) {
                        _coverage.MarkLine(statements.GetStartLine(i));
                    }
                }
            }
        }
    }

    /// <summary>
    /// One method definition of a measured file, as :methods coverage reports it: MRI's key is
    /// [class, name, start line, start column, end line, end column] and its value the number of
    /// calls. Recorded when the `def' runs (so a method that is never called is still reported,
    /// with 0), counted in the body's prologue.
    /// </summary>
    public sealed class MethodCoverage {
        public readonly CoverageState/*!*/ State;
        public readonly RubyModule/*!*/ Owner;
        public readonly string/*!*/ Name;
        public readonly int StartLine;
        public readonly int StartColumn;
        public readonly int EndLine;
        public readonly int EndColumn;

        /// <summary>
        /// A one-element array so that compiled code can increment it in place, as for lines.
        /// </summary>
        public readonly int[]/*!*/ Counts = new int[1];

        /// <summary>
        /// One call, counted by the method body's prologue (see MethodDefinition.TransformBody).
        /// </summary>
        public void Count() {
            if (State.Resumed) {
                Counts[0]++;
            }
        }

        internal MethodCoverage(CoverageState/*!*/ state, RubyModule/*!*/ owner, string/*!*/ name,
            int startLine, int startColumn, int endLine, int endColumn) {
            Assert.NotNull(state, owner, name);
            State = state;
            Owner = owner;
            Name = name;
            StartLine = startLine;
            StartColumn = startColumn;
            EndLine = endLine;
            EndColumn = endColumn;
        }
    }

    public sealed class LineCoverage {
        private const int NotALine = -1;

        public readonly CoverageState/*!*/ State;
        public readonly string/*!*/ Path;

        /// <summary>
        /// Execution count per line (index = line - 1); -1 for lines without code.
        /// Incremented by compiled code.
        /// </summary>
        public readonly int[]/*!*/ Counts;

        // oneshot_lines: lines already handed out by a clearing Coverage.result
        private bool[] _reported;

        // The methods defined in this file while it was measured, by the `def' they came from: one
        // entry per module the same `def' was run for (Class.new { def m; end } in a loop).
        private readonly Dictionary<object, List<MethodCoverage>>/*!*/ _methods =
            new Dictionary<object, List<MethodCoverage>>(ReferenceEqualityComparer.Instance);
        private readonly List<MethodCoverage>/*!*/ _methodOrder = new List<MethodCoverage>();

        internal LineCoverage(CoverageState/*!*/ state, string/*!*/ path, int lineCount) {
            Assert.NotNull(state, path);
            State = state;
            Path = path;
            Counts = new int[lineCount];
            for (int i = 0; i < Counts.Length; i++) {
                Counts[i] = NotALine;
            }
        }

        internal void MarkLine(int line) {
            if (line > 0 && line <= Counts.Length && Counts[line - 1] == NotALine) {
                Counts[line - 1] = 0;
            }
        }

        internal bool IsCounted(int line) {
            return line > 0 && line <= Counts.Length;
        }

        /// <summary>
        /// Counts per line, null for lines without code.
        /// </summary>
        public int?[]/*!*/ GetLines() {
            var result = new int?[Counts.Length];
            for (int i = 0; i < result.Length; i++) {
                int count = Counts[i];
                result[i] = count == NotALine ? (int?)null : count;
            }
            return result;
        }

        /// <summary>
        /// The lines run at least once and not reported by an earlier clearing result.
        /// </summary>
        public List<int>/*!*/ GetOneshotLines() {
            var result = new List<int>();
            for (int i = 0; i < Counts.Length; i++) {
                if (Counts[i] > 0 && (_reported == null || !_reported[i])) {
                    result.Add(i + 1);
                }
            }
            return result;
        }

        /// <summary>
        /// Records a method definition, or answers the record a previous run of the same `def' for
        /// the same module made. Only called when :methods coverage is measured.
        /// </summary>
        internal MethodCoverage/*!*/ AddMethod(object/*!*/ definition, RubyModule/*!*/ owner, string/*!*/ name,
            int startLine, int startColumn, int endLine, int endColumn) {

            lock (_methods) {
                List<MethodCoverage> definitions;
                if (!_methods.TryGetValue(definition, out definitions)) {
                    _methods[definition] = definitions = new List<MethodCoverage>();
                }
                foreach (var existing in definitions) {
                    if (ReferenceEquals(existing.Owner, owner)) {
                        return existing;
                    }
                }

                var result = new MethodCoverage(State, owner, name, startLine, startColumn, endLine, endColumn);
                definitions.Add(result);
                _methodOrder.Add(result);
                return result;
            }
        }

        /// <summary>
        /// The counters the body compiled for <paramref name="owner"/> counts into, or null if the
        /// `def' was not recorded (:methods coverage off, or the definition never ran). A body is
        /// compiled once per module it is defined for, so the owner picks the record; a singleton
        /// or module-function copy, whose module is not the one the definition recorded, falls
        /// back to the first record of the same `def'.
        /// </summary>
        internal MethodCoverage FindMethod(object/*!*/ definition, RubyModule/*!*/ owner) {
            lock (_methods) {
                List<MethodCoverage> definitions;
                if (!_methods.TryGetValue(definition, out definitions) || definitions.Count == 0) {
                    return null;
                }
                foreach (var existing in definitions) {
                    if (ReferenceEquals(existing.Owner, owner)) {
                        return existing;
                    }
                }
                return definitions[0];
            }
        }

        public List<MethodCoverage>/*!*/ GetMethods() {
            lock (_methods) {
                return new List<MethodCoverage>(_methodOrder);
            }
        }

        internal void Clear() {
            foreach (var method in GetMethods()) {
                method.Counts[0] = 0;
            }
            for (int i = 0; i < Counts.Length; i++) {
                if (Counts[i] > 0) {
                    if ((State.Modes & CoverageModes.OneshotLines) != 0) {
                        if (_reported == null) {
                            _reported = new bool[Counts.Length];
                        }
                        _reported[i] = true;
                    }
                    Counts[i] = 0;
                }
            }
        }
    }
}

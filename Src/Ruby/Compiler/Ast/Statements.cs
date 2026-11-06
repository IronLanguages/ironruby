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

using MSA = System.Linq.Expressions;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using System;

namespace IronRuby.Compiler.Ast {
    using Ast = Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    
    public sealed class Statements : IEnumerable<Expression> {
        private static readonly Statements _Empty = new Statements();

        internal static Statements/*!*/ Empty {
            get {
                Debug.Assert(_Empty.Count == 0);
                return _Empty;
            }
        }

        private Expression[] _statements;
        private int _count;

        // The line each statement starts on (for TracePoint :line), where the statement's own
        // location does not say: a local variable read is the variable's node, located where the
        // variable was defined. NoLine marks a statement the compiler made up.
        private int[] _lines;
        private const int Unset = Int32.MinValue;
        public const int NoLine = -1;

        public Statements() {
        }

        public int Count {
            get { return _count; }
        }

        public Statements(Expression/*!*/ statement) {
            Assert.NotNull(statement);
            AddFirst(statement);
        }

        private void AddFirst(Expression/*!*/ statement) {
            _statements = new Expression[] { statement };
            _count = 1;
        }

        public Expression/*!*/ Add(Expression/*!*/ statement) {
            Assert.NotNull(statement);
            if (_count == 0) {
                AddFirst(statement);
            } else {
                if (_count == _statements.Length) {
                    Array.Resize(ref _statements, 2 * _count);
                }
                _statements[_count] = statement;
                _count++;
            }
            return statement;
        }

        /// <summary>
        /// Adds a statement that starts on the given line.
        /// </summary>
        public Expression/*!*/ Add(Expression/*!*/ statement, int line) {
            Add(statement);
            if (line != statement.Location.Start.Line) {
                int oldLength = (_lines != null) ? _lines.Length : 0;
                if (oldLength < _statements.Length) {
                    Array.Resize(ref _lines, _statements.Length);
                    for (int i = oldLength; i < _lines.Length; i++) {
                        _lines[i] = Unset;
                    }
                }
                _lines[_count - 1] = line;
            }
            return statement;
        }

        public Expression/*!*/ this[int index] {
            get { return _statements[index]; }
        }

        public int GetStartLine(int index) {
            if (_lines != null && index < _lines.Length && _lines[index] != Unset) {
                return _lines[index];
            }
            return _statements[index].Location.Start.Line;
        }

        public Expression/*!*/ First {
            get { return _statements[0]; }
        }

        public Expression/*!*/ Last {
            get { return _statements[_count - 1]; }
        }

        public IEnumerable<Expression>/*!*/ AllButLast {
            get {
                for (int i = 0; i < _count - 1; i++) {
                    yield return _statements[i];
                }
            }
        }

        public IEnumerator<Expression>/*!*/ GetEnumerator() {
            for (int i = 0; i < _count; i++) {
                yield return _statements[i];
            }
        }

        System.Collections.IEnumerator/*!*/ System.Collections.IEnumerable.GetEnumerator() {
            return GetEnumerator();
        }
    }
}

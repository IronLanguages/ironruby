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

#if FEATURE_CORE_DLR
using MSA = System.Linq.Expressions;
#else
using MSA = Microsoft.Scripting.Ast;
#endif

using System;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Interpreter;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    // TracePoint hooks in compiled code. When no TracePoint listens for the event they cost one
    // instruction in the interpreter (a guard that jumps over the hook) and a static field test
    // once compiled.

    /// <summary>
    /// The :line event, before a statement that starts a new line.
    /// </summary>
    internal sealed class TraceLineExpression : ReducibleEmptyExpression, IInstructionProvider {
        private readonly MSA.Expression/*!*/ _scope;
        private readonly string _path;
        private readonly int _line;
        private readonly LineCoverage _coverage;

        public TraceLineExpression(MSA.Expression/*!*/ scope, string path, int line, LineCoverage coverage) {
            _scope = scope;
            _path = path;
            _line = line;
            _coverage = coverage;
        }

        public void AddInstructions(LightCompiler/*!*/ compiler) {
            if (_coverage != null) {
                compiler.Instructions.Emit(new CountLineInstruction(_coverage, _line - 1));
            }

            var guard = new TraceGuardInstruction(TraceEvents.Line);
            compiler.Instructions.Emit(guard);
            int start = compiler.Instructions.Count;
            compiler.Compile(_scope);
            compiler.Instructions.Emit(new TraceLineInstruction(_path, _line));
            guard.Skip = compiler.Instructions.Count - start;
        }

        public override MSA.Expression/*!*/ Reduce() {
            var trace = AstUtils.IfThen(
                TraceGuardInstruction.MakeTest(TraceEvents.Line),
                Methods.TraceLineEvent.OpCall(AstUtils.Convert(_scope, typeof(RubyScope)), AstUtils.Constant(_path, typeof(string)), AstUtils.Constant(_line))
            );

            if (_coverage == null) {
                return trace;
            }

            return Ast.Block(
                AstUtils.IfThen(
                    Ast.Field(AstUtils.Constant(_coverage.State), typeof(CoverageState).GetField("Resumed")),
                    Ast.PreIncrementAssign(Ast.ArrayAccess(AstUtils.Constant(_coverage.Counts), AstUtils.Constant(_line - 1)))
                ),
                trace
            );
        }

        protected override MSA.Expression/*!*/ VisitChildren(MSA.ExpressionVisitor/*!*/ visitor) {
            var scope = visitor.Visit(_scope);
            return scope == _scope ? this : new TraceLineExpression(scope, _path, _line, _coverage);
        }

        /// <summary>
        /// Counts the line for the Coverage library while the measurement runs.
        /// </summary>
        private sealed class CountLineInstruction : Instruction {
            private readonly LineCoverage/*!*/ _coverage;
            private readonly int _index;

            internal CountLineInstruction(LineCoverage/*!*/ coverage, int index) {
                _coverage = coverage;
                _index = index;
            }

            public override int Run(InterpretedFrame/*!*/ frame) {
                if (_coverage.State.Resumed) {
                    _coverage.Counts[_index]++;
                }
                return +1;
            }

            public override string InstructionName {
                get { return "Ruby:CountLine"; }
            }
        }

        private sealed class TraceLineInstruction : Instruction {
            private readonly string _path;
            private readonly int _line;

            internal TraceLineInstruction(string path, int line) {
                _path = path;
                _line = line;
            }

            public override int ConsumedStack { get { return 1; } }

            public override int Run(InterpretedFrame/*!*/ frame) {
                RubyOps.TraceLineEvent((RubyScope)frame.Pop(), _path, _line);
                return +1;
            }

            public override string InstructionName {
                get { return "Ruby:TraceLine"; }
            }
        }
    }

    /// <summary>
    /// The :return, :b_return and :end events: the value of a method, block or class body passes
    /// through it on its way out.
    /// </summary>
    internal sealed class TraceReturnExpression : MSA.Expression, IInstructionProvider {
        private readonly MSA.Expression/*!*/ _scope;
        private readonly MSA.Expression/*!*/ _value;
        private readonly TraceEvents _events;
        private readonly string _path;
        private readonly int _line;

        internal MSA.Expression/*!*/ Scope { get { return _scope; } }
        internal MSA.Expression/*!*/ Value { get { return _value; } }

        /// <summary>The same hook with other operands (see MethodDefinition.LightFrameRewriter).</summary>
        internal TraceReturnExpression/*!*/ Update(MSA.Expression/*!*/ scope, MSA.Expression/*!*/ value) {
            return scope == _scope && value == _value ? this : new TraceReturnExpression(scope, value, _events, _path, _line);
        }

        public TraceReturnExpression(MSA.Expression/*!*/ scope, MSA.Expression/*!*/ value, TraceEvents events, string path, int line) {
            _scope = scope;
            _value = AstUtils.Convert(value, typeof(object));
            _events = events;
            _path = path;
            _line = line;
        }

        public sealed override MSA.ExpressionType NodeType {
            get { return MSA.ExpressionType.Extension; }
        }

        public sealed override Type/*!*/ Type {
            get { return typeof(object); }
        }

        public override bool CanReduce {
            get { return true; }
        }

        public void AddInstructions(LightCompiler/*!*/ compiler) {
            compiler.Compile(_value);
            var guard = new TraceGuardInstruction(_events);
            compiler.Instructions.Emit(guard);
            int start = compiler.Instructions.Count;
            compiler.Compile(_scope);
            compiler.Instructions.Emit(new TraceReturnInstruction(_path, _line));
            guard.Skip = compiler.Instructions.Count - start;
        }

        public override MSA.Expression/*!*/ Reduce() {
            var result = Ast.Variable(typeof(object), "#result");
            return Ast.Block(new[] { result },
                Ast.Assign(result, _value),
                AstUtils.IfThen(
                    TraceGuardInstruction.MakeTest(_events),
                    Methods.TraceReturnEvent.OpCall(AstUtils.Convert(_scope, typeof(RubyScope)), result, AstUtils.Constant(_path, typeof(string)), AstUtils.Constant(_line))
                ),
                result
            );
        }

        protected override MSA.Expression/*!*/ VisitChildren(MSA.ExpressionVisitor/*!*/ visitor) {
            var scope = visitor.Visit(_scope);
            var value = visitor.Visit(_value);
            return scope == _scope && value == _value ? this : new TraceReturnExpression(scope, value, _events, _path, _line);
        }

        private sealed class TraceReturnInstruction : Instruction {
            private readonly string _path;
            private readonly int _line;

            internal TraceReturnInstruction(string path, int line) {
                _path = path;
                _line = line;
            }

            // pops the scope, leaves the value underneath it on the stack
            public override int ConsumedStack { get { return 1; } }

            public override int Run(InterpretedFrame/*!*/ frame) {
                var scope = (RubyScope)frame.Pop();
                RubyOps.TraceReturnEvent(scope, frame.Peek(), _path, _line);
                return +1;
            }

            public override string InstructionName {
                get { return "Ruby:TraceReturn"; }
            }
        }
    }

    /// <summary>
    /// Jumps over the hook that follows it unless a TracePoint listens for one of the events.
    /// </summary>
    internal sealed class TraceGuardInstruction : Instruction {
        private readonly int _events;
        internal int Skip;

        internal TraceGuardInstruction(TraceEvents events) {
            _events = (int)events;
        }

        internal static MSA.Expression/*!*/ MakeTest(TraceEvents events) {
            return Ast.NotEqual(
                Ast.And(Ast.Field(null, typeof(TracePoint).GetField("ActiveEvents")), AstUtils.Constant((int)events)),
                AstUtils.Constant(0)
            );
        }

        public override int Run(InterpretedFrame/*!*/ frame) {
            return (TracePoint.ActiveEvents & _events) != 0 ? +1 : 1 + Skip;
        }

        public override string InstructionName {
            get { return "Ruby:TraceGuard"; }
        }
    }
}

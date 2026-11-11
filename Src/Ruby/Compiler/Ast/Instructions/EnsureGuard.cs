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

using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Interpreter;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    /// <summary>
    /// The guarded statements of a body that has an ensure clause and no rescue or else clause (see Body.TransformExceptionHandling).
    ///
    /// The interpreter, which has no exception filters, runs <see cref="Lower"/>: a catch of every exception CanRescue takes (a
    /// DLR filter, which the DLR lowers to a catch and a throw again of whatever the filter turns down), after which the ensure
    /// clause, in the finally block around this, throws the exception again. Each such catch and throw is a full CLR exception
    /// dispatch of its own, for every ensure the exception goes through on its way out. Compiled, the statements are guarded by a
    /// real exception filter, RubyOps.PassEnsure, which lets an exception go through when the catch would have nothing left to
    /// do for it, and a fault block that sets $! for the ensure clause as the exception leaves; the ensure clause then runs from
    /// the finally block while the exception passes. An exception the filter does not let through is caught as before.
    /// </summary>
    internal sealed class EnsureGuardExpression : MSA.Expression, IInstructionProvider {
        private static readonly MethodInfo/*!*/ _PassEnsure = typeof(RubyOps).GetMethod(nameof(RubyOps.PassEnsure));
        private static readonly MethodInfo/*!*/ _EnterPassedEnsure = typeof(RubyOps).GetMethod(nameof(RubyOps.EnterPassedEnsure));
        private static readonly MethodInfo/*!*/ _CanRescue = typeof(RubyOps).GetMethod(nameof(RubyOps.CanRescue));
        private static readonly MethodInfo/*!*/ _Capture = typeof(ExceptionDispatchInfo).GetMethod(nameof(ExceptionDispatchInfo.Capture));
        private static readonly MethodInfo/*!*/ _Throw = typeof(ExceptionDispatchInfo).GetMethod(nameof(ExceptionDispatchInfo.Throw), Type.EmptyTypes);

        private readonly MSA.Expression/*!*/ _body;
        private readonly MSA.Expression/*!*/ _scope;
        private readonly MSA.Expression/*!*/ _thrown;
        private readonly MSA.Expression/*!*/ _rethrow;

        /// <param name="body">The guarded statements, of type void.</param>
        /// <param name="scope">The current RubyScope.</param>
        /// <param name="thrown">The body's "exception thrown" flag, set when CanRescue takes the exception.</param>
        /// <param name="rethrow">The body's "rethrow" flag, set when CanRescue takes the exception: the ensure clause throws it again.</param>
        internal EnsureGuardExpression(MSA.Expression/*!*/ body, MSA.Expression/*!*/ scope, MSA.Expression/*!*/ thrown, MSA.Expression/*!*/ rethrow) {
            _body = body;
            _scope = scope;
            _thrown = thrown;
            _rethrow = rethrow;
        }

        /// <summary>
        /// The guard as a DLR filter on CanRescue, which the DLR lowers to a catch: for the interpreter.
        /// </summary>
        internal MSA.Expression/*!*/ Lower() {
            MSA.ParameterExpression e;
            return AstUtils.Try(
                _body
            ).Filter(e = Ast.Parameter(typeof(Exception), "#e"),
                Methods.CanRescue.OpCall(_scope, e),

                Ast.Assign(_thrown, AstUtils.Constant(true)),
                Ast.Assign(_rethrow, AstUtils.Constant(true)),
                AstUtils.Empty()
            );
        }

        public override MSA.ExpressionType NodeType {
            get { return MSA.ExpressionType.Extension; }
        }

        public override Type/*!*/ Type {
            get { return typeof(void); }
        }

        public override bool CanReduce {
            get { return true; }
        }

        public void AddInstructions(LightCompiler/*!*/ compiler) {
            compiler.Compile(Lower());
        }

        //   passing = null; pending = null;
        //   try {
        //       try {
        //           body
        //       } catch (Exception e) when ((passing = RubyOps.PassEnsure(scope, e)) == null) {
        //           if (RubyOps.CanRescue(scope, e)) { thrown = rethrow = true } else { pending = ExceptionDispatchInfo.Capture(e) }
        //       }
        //       if (pending != null) pending.Throw();
        //   } fault {
        //       RubyOps.EnterPassedEnsure(scope, passing);
        //   }
        //
        // A filter that let one exception through has seen every exception that leaves the inner try after it: one that the
        // statements throw, or a finally block of theirs on the way out, passes the filter itself before it leaves. Whatever leaves
        // the catch block was caught, after the filter set passing to null. So the fault always gets the exception that is leaving.
        public override MSA.Expression/*!*/ Reduce() {
            var passing = Ast.Variable(typeof(Exception), "#passing");
            var pending = Ast.Variable(typeof(ExceptionDispatchInfo), "#pending");
            var e = Ast.Variable(typeof(Exception), "#e");

            return Ast.Block(new[] { passing, pending },
                Ast.Assign(passing, Ast.Constant(null, typeof(Exception))),
                Ast.Assign(pending, Ast.Constant(null, typeof(ExceptionDispatchInfo))),
                Ast.TryFault(
                    Ast.Block(
                        Ast.TryCatch(
                            _body,
                            Ast.Catch(e,
                                Ast.IfThenElse(
                                    Ast.Call(_CanRescue, _scope, e),
                                    Ast.Block(Ast.Assign(_thrown, Ast.Constant(true)), Ast.Assign(_rethrow, Ast.Constant(true))),
                                    Ast.Assign(pending, Ast.Call(_Capture, e))
                                ),
                                Ast.Equal(
                                    Ast.Assign(passing, Ast.Call(_PassEnsure, _scope, e)),
                                    Ast.Constant(null, typeof(Exception))
                                )
                            )
                        ),
                        Ast.IfThen(
                            Ast.NotEqual(pending, Ast.Constant(null, typeof(ExceptionDispatchInfo))),
                            Ast.Call(pending, _Throw)
                        )
                    ),
                    Ast.Call(_EnterPassedEnsure, _scope, passing)
                )
            );
        }

        protected override MSA.Expression/*!*/ VisitChildren(MSA.ExpressionVisitor/*!*/ visitor) {
            var body = visitor.Visit(_body);
            var scope = visitor.Visit(_scope);
            var thrown = visitor.Visit(_thrown);
            var rethrow = visitor.Visit(_rethrow);
            if (body == _body && scope == _scope && thrown == _thrown && rethrow == _rethrow) {
                return this;
            }
            return new EnsureGuardExpression(body, scope, thrown, rethrow);
        }
    }
}

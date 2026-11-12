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
using System.Collections.Generic;
using Microsoft.Scripting;
using Microsoft.Scripting.Ast;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    // begin/class/def/module/class << {expression}
    //   statements
    // rescue-clauses 
    // else
    //   statements
    // ensure
    //   statements
    // end
    public partial class Body : Expression {
        private static readonly System.Reflection.MethodInfo/*!*/ _ExposeException = typeof(RubyOps).GetMethod(nameof(RubyOps.ExposeException));
        private static readonly System.Reflection.MethodInfo/*!*/ _CanRescueDeferTrace = typeof(RubyOps).GetMethod(nameof(RubyOps.CanRescueDeferTrace),
            new[] { typeof(RubyScope), typeof(Exception), typeof(int) });
        private static readonly System.Reflection.MethodInfo/*!*/ _RescuePassUnwinder = typeof(RubyOps).GetMethod(nameof(RubyOps.RescuePassUnwinder));
        private static readonly System.Reflection.MethodInfo/*!*/ _RescuePassMayTest = typeof(RubyOps).GetMethod(nameof(RubyOps.RescuePassMayTest));
        private static readonly System.Reflection.MethodInfo/*!*/ _RescueSiteCannotMatch = typeof(RubyOps).GetMethod(nameof(RubyOps.RescueSiteCannotMatch));
        private static readonly System.Reflection.MethodInfo/*!*/ _RescueDefaultCannotMatch = typeof(RubyOps).GetMethod(nameof(RubyOps.RescueDefaultCannotMatch));
        private static readonly System.Reflection.MethodInfo/*!*/ _RescuePassCommit = typeof(RubyOps).GetMethod(nameof(RubyOps.RescuePassCommit));
        private static readonly System.Reflection.MethodInfo/*!*/ _CanRescueAfterClauses = typeof(RubyOps).GetMethod(nameof(RubyOps.CanRescueAfterClauses));
        private static readonly System.Reflection.MethodInfo/*!*/ _SetCauseLeavingEnsure = typeof(RubyOps).GetMethod(nameof(RubyOps.SetCauseLeavingEnsure));
        private static readonly System.Reflection.MethodInfo/*!*/ _SetCauseLeavingInterpretedEnsure = typeof(RubyOps).GetMethod(nameof(RubyOps.SetCauseLeavingInterpretedEnsure));

        /// <summary>
        /// Whether an ensure clause may jump out of itself - return, break, next, redo, retry - which the DLR makes a catch of
        /// every exception around the frame's statements and a `throw' of it again after the clause (FlowControlRewriter): that
        /// starts the exception's CLR trace over, so the trace no longer has the frames it came out of. A jump of a block or
        /// method defined in the clause is a jump of that lambda's own, or an exception.
        /// </summary>
        private sealed class EnsureJumpFinder : Walker {
            private bool _found;

            internal static bool MayJump(Statements/*!*/ statements) {
                var finder = new EnsureJumpFinder();
                foreach (var statement in statements) {
                    statement.Walk(finder);
                }
                return finder._found;
            }

            public override bool Enter(ReturnStatement/*!*/ node) { _found = true; return false; }
            public override bool Enter(BreakStatement/*!*/ node) { _found = true; return false; }
            public override bool Enter(NextStatement/*!*/ node) { _found = true; return false; }
            public override bool Enter(RedoStatement/*!*/ node) { _found = true; return false; }
            public override bool Enter(RetryStatement/*!*/ node) { _found = true; return false; }
            public override bool Enter(BlockDefinition/*!*/ node) { return false; }
            public override bool Enter(MethodDefinition/*!*/ node) { return false; }
        }

        /// <summary>
        /// Whether the transformed ensure clause has a jump (a GotoExpression) of its own: what the DLR's FlowControlRewriter turns
        /// into the catch and `throw saved' EnsureJumpFinder is about, wherever it comes from - a call with a block (literal or
        /// passed) or a yield or a zsuper returns through a label after it, which a break from the block jumps to. A jump within the
        /// clause counts too, which is the safe side. Not those of the lambdas defined in it; extension nodes are looked into as the
        /// rewriter sees them, reduced.
        /// </summary>
        private sealed class GotoFinder : MSA.ExpressionVisitor {
            private bool _found;

            internal static bool Has(MSA.Expression/*!*/ node) {
                var finder = new GotoFinder();
                finder.Visit(node);
                return finder._found;
            }

            public override MSA.Expression Visit(MSA.Expression node) {
                return _found ? node : base.Visit(node);
            }

            protected override MSA.Expression/*!*/ VisitGoto(MSA.GotoExpression/*!*/ node) {
                _found = true;
                return node;
            }

            protected override MSA.Expression/*!*/ VisitLambda<T>(MSA.Expression<T>/*!*/ node) {
                return node;
            }

            protected override MSA.Expression/*!*/ VisitExtension(MSA.Expression/*!*/ node) {
                return node.CanReduce ? Visit(node.Reduce()) : node;
            }
        }

        // RescuePassUnwinder(scope, e) || RescuePassMayTest(scope, e) && <each class of each clause cannot match> && RescuePassCommit(scope, e)
        private static MSA.Expression/*!*/ MakeRescuePass(MSA.Expression/*!*/ scope, MSA.Expression/*!*/ exception, ConstantSiteCache[][]/*!*/ clauses) {
            MSA.Expression test = Ast.Call(_RescuePassMayTest, scope, exception);
            foreach (var clause in clauses) {
                if (clause.Length == 0) {
                    test = Ast.AndAlso(test, Ast.Call(_RescueDefaultCannotMatch, scope, exception));
                }
                foreach (var site in clause) {
                    test = Ast.AndAlso(test, Ast.Call(_RescueSiteCannotMatch, scope, exception, AstUtils.Constant(site)));
                }
            }
            return Ast.OrElse(Ast.Call(_RescuePassUnwinder, scope, exception), Ast.AndAlso(test, Ast.Call(_RescuePassCommit, scope, exception)));
        }

        private readonly Statements/*!*/ _statements;
        private readonly List<RescueClause> _rescueClauses;	// optional
        private readonly Statements _elseStatements;	// optional
        private readonly Statements _ensureStatements;	// optional

        // TODO: readonly
        public Statements/*!*/ Statements { get { return _statements; } }
        public List<RescueClause> RescueClauses { get { return _rescueClauses; } }
        public Statements ElseStatements { get { return _elseStatements; } }
        public Statements EnsureStatements { get { return _ensureStatements; } }

        /// <summary>
        /// Whether the statements can be guarded by the rescue clauses: there are both.
        /// </summary>
        internal bool HasRescue {
            get { return _statements.Count > 0 && _rescueClauses != null; }
        }

        /// <summary>
        /// Set by the block whose whole body this is, which then has no safe point at its entry
        /// (RubyOps.CreateBlockScopeSafePointInBody): the first thing the rescued statements do is
        /// the safe point - on a `retry` too, which makes the retry loop interruptible as well.
        /// </summary>
        internal bool SafePointFirst { get; set; }

        private bool HasExceptionHandling {
            get {
                return (_statements.Count > 0 && _rescueClauses != null) || _elseStatements != null || _ensureStatements != null;
            }
        }

        public Body(Statements/*!*/ statements, List<RescueClause> rescueClauses, Statements elseStatements,
            Statements ensureStatements, SourceSpan location)
            : base(location) {

            Assert.NotNull(statements);

            _statements = statements;
            _rescueClauses = rescueClauses;
            _elseStatements = elseStatements;
            _ensureStatements = ensureStatements;
        }

        internal override MSA.Expression/*!*/ Transform(AstGenerator/*!*/ gen) {
            // do not mark a sequence point wrapping the entire block:
            return TransformRead(gen);
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            Assert.NotNull(gen);

            if (HasExceptionHandling) {
                MSA.Expression resultVariable = gen.CurrentScope.DefineHiddenVariable("#block-result", typeof(object));

                return Ast.Block(
                    TransformExceptionHandling(gen, ResultOperation.Store(resultVariable)),
                    resultVariable
                );

            } else {
                return gen.TransformStatementsToExpression(_statements);
            }
        }

        internal override MSA.Expression/*!*/ TransformResult(AstGenerator/*!*/ gen, ResultOperation resultOperation) {
            Assert.NotNull(gen);

            if (HasExceptionHandling) {
                return TransformExceptionHandling(gen, resultOperation);
            } else {
                return gen.TransformStatements(_statements, resultOperation);
            }
        }

        private MSA.Expression/*!*/ TransformExceptionHandling(AstGenerator/*!*/ gen, ResultOperation resultOperation) {
            Assert.NotNull(gen);

            MSA.Expression exceptionThrownVariable = gen.CurrentScope.DefineHiddenVariable("#exception-thrown", typeof(bool));
            MSA.Expression exceptionRethrowVariable = gen.CurrentScope.DefineHiddenVariable("#exception-rethrow", typeof(bool));
            MSA.Expression retryingVariable = gen.CurrentScope.DefineHiddenVariable("#retrying", typeof(bool));
            MSA.Expression oldExceptionVariable = gen.CurrentScope.DefineHiddenVariable("#old-exception", typeof(Exception));

            MSA.ParameterExpression unwinder, exceptionVariable;
            MSA.Expression transformedBody;
            MSA.Expression transformedEnsure;
            MSA.Expression transformedElse;

            // $! when the body was entered: what it is again once the ensure clause is left by a jump (see below)
            MSA.Expression entryExceptionVariable = null;
            if (_ensureStatements != null) {
                entryExceptionVariable = gen.CurrentScope.DefineHiddenVariable("#entry-exception", typeof(Exception));
                MSA.Expression ensureDoneVariable = gen.CurrentScope.DefineHiddenVariable("#ensure-done", typeof(bool));
                MSA.ParameterExpression leaving = Ast.Parameter(typeof(Exception), "#e");

                transformedEnsure = Ast.Block(
                    // ensure (an exception to be thrown on gets its backtrace completed first: a jump out of the clause ends it):
                    Ast.Assign(oldExceptionVariable, Methods.GetCurrentException.OpCall(gen.CurrentScopeVariable)),
                    AstUtils.IfThen(exceptionRethrowVariable, Ast.Call(_ExposeException, oldExceptionVariable)),
                    Ast.Assign(ensureDoneVariable, AstUtils.Constant(false)),
                    AstUtils.Try(
                        // An exception the clause raises has the exception the clause runs for as its cause, which the
                        // filter decides before the finally below restores $! (RubyOps.SetCauseLeavingEnsure).
                        new RubyFilterExpression(
                            Ast.Block(TransformEnsureStatements(gen), AstUtils.Empty()),
                            leaving,
                            Ast.Call(_SetCauseLeavingInterpretedEnsure, gen.CurrentScopeVariable, leaving),
                            AstUtils.Empty(),
                            Ast.Not(Ast.Call(_SetCauseLeavingEnsure, gen.CurrentScopeVariable, leaving))
                        ),
                        Ast.Assign(ensureDoneVariable, AstUtils.Constant(true))
                    ).Finally(
                        // Left by a jump - return, break, next, throw, a break from a block it yields to - the clause ends
                        // the exception it ran for, and $! is again what it was before the begin, as in MRI (whose $! is the
                        // errinfo of the innermost rescue or ensure frame still running). Without this the swallowed
                        // exception stayed in $!, and became the cause of the next exception raised.
                        AstUtils.Unless(ensureDoneVariable,
                            Methods.SetCurrentException.OpCall(gen.CurrentScopeVariable, entryExceptionVariable)
                        )
                    ),
                    Methods.SetCurrentException.OpCall(gen.CurrentScopeVariable, oldExceptionVariable),

                    // rethrow:
                    AstUtils.IfThen(
                        Ast.AndAlso(
                            exceptionRethrowVariable,
                            Ast.NotEqual(oldExceptionVariable, AstUtils.Constant(null))
                        ),
                        Ast.Throw(Ast.Call(_ExposeException, oldExceptionVariable))
                    )
                );
            } else {
                // rethrow:
                transformedEnsure = AstUtils.IfThen(
                    Ast.AndAlso(
                        exceptionRethrowVariable,
                        Ast.NotEqual(
                            Ast.Assign(oldExceptionVariable, Methods.GetCurrentException.OpCall(gen.CurrentScopeVariable)),
                            AstUtils.Constant(null, typeof(Exception)))
                        ),
                    Ast.Throw(Ast.Call(_ExposeException, oldExceptionVariable))
                );
            }

            if (_elseStatements != null) {
                transformedElse = gen.TransformStatements(_elseStatements, resultOperation);
            } else {
                transformedElse = AstUtils.Empty();
            }

            // body should do return, but else-clause is present => we cannot do return from the guarded statements: 
            // (the value of the last expression in the body cannot be the last executed expression statement => we can ignore it):
            transformedBody = gen.TransformStatements(_statements, (_elseStatements != null) ? ResultOperation.Ignore : resultOperation);
            if (SafePointFirst) {
                transformedBody = Ast.Block(gen.AddDebugInfo(Ast.Call(RubyUtils.SafePointMethod), _statements.First.Location), transformedBody);
            }

            MSA.Expression enterRescue = null, leaveRescue = null;
            var retryLabel = Ast.Label("retry");
            ConstantSiteCache[][] rescueSites = null;

            // The exception the rescue clauses ran for, once they are left: the cause of an exception they raise that has none
            // yet (one C# threw), which the handler around them decides after the finally below has restored $!.
            MSA.Expression handledVariable = (_rescueClauses != null) ?
                gen.CurrentScope.DefineHiddenVariable("#handled-exception", typeof(Exception)) : null;

            // make rescue clause:
            MSA.Expression transformedRescue;
            if (_rescueClauses != null) {
                // outer-most EH blocks sets and clears runtime flag RuntimeFlowControl.InTryRescue:
                if (gen.CurrentRescue == null) {
                    enterRescue = Methods.EnterRescue.OpCall(gen.CurrentScopeVariable);
                    leaveRescue = Methods.LeaveRescue.OpCall(gen.CurrentScopeVariable);
                } else {
                    enterRescue = leaveRescue = AstUtils.Empty();
                }

                gen.EnterRescueClause(retryingVariable, retryLabel);

                var handlers = new IfStatementTest[_rescueClauses.Count];
                var clauseSites = new ConstantSiteCache[_rescueClauses.Count][];
                for (int i = 0; i < handlers.Length; i++) {
                    ConstantSiteCache[] sites;
                    handlers[i] = _rescueClauses[i].Transform(gen, resultOperation, out sites);
                    if (sites == null) {
                        clauseSites = null;
                    } else if (clauseSites != null) {
                        clauseSites[i] = sites;
                    }
                }

                // Compiled, an exception none of the rescue clauses can match goes through (RubyOps.RescuePassMayTest); not with an
                // ensure clause, which would need $! set and runs Ruby code before an outer frame's rescue clauses are matched.
                if (clauseSites != null && _ensureStatements == null) {
                    rescueSites = clauseSites;
                }

                transformedRescue =
                    AstUtils.Try(
                        enterRescue,
                        AstUtils.If(handlers, Ast.Assign(exceptionRethrowVariable, AstUtils.Constant(true)))
                    ).Filter(unwinder = Ast.Parameter(typeof(EvalUnwinder), "#u"),
                        Ast.Equal(Ast.Field(unwinder, EvalUnwinder.ReasonField), AstUtils.Constant(BlockReturnReason.Retry)),

                        Ast.Block(
                            Ast.Assign(retryingVariable, AstUtils.Constant(true)),
                            Ast.Continue(retryLabel),
                            AstUtils.Empty()
                        )
                    );
                

                gen.LeaveRescueClause();

            } else {
                transformedRescue = Ast.Assign(exceptionRethrowVariable, AstUtils.Constant(true));
            }

            if (_elseStatements != null) {
                transformedElse = AstUtils.Unless(exceptionThrownVariable, transformedElse);
            }

            // The rescue clauses - matching them as well as running them - come after the handler, not in it: on
            // CoreCLR a catch runs on top of the frames the exception came out of, which are popped only when the
            // catch is left, so a rescue body ran with no more stack than the raise had. After a deep recursion's
            // SystemStackError it had none, and the first call in the rescue raised another (#128). MRI's rescue
            // runs once the frames are gone. Without rescue clauses the handler only takes note.
            MSA.Expression guarded;
            MSA.ParameterExpression passingVariable = null;
            // the index of the frame's record, which the walk of the stack below it waits for (RubyExceptionData.TryDeferCatchSite)
            var frameDepth = gen.CurrentCompiledFrameDepth;
            if (rescueSites != null) {
                // The exception the filter lets through, which the filter around the else clause lets through as well. Reset once
                // the statements are left any other way: an exception thrown later in the frame is not the one that went through.
                passingVariable = gen.CurrentScope.DefineHiddenVariable("#passing", typeof(Exception));
                exceptionVariable = Ast.Parameter(typeof(Exception), "#e");
                guarded = AstUtils.Try(
                    new RubyFilterExpression(
                        Ast.Block(transformedBody, AstUtils.Empty()),
                        exceptionVariable,
                        Ast.Call(_CanRescueDeferTrace, gen.CurrentScopeVariable, exceptionVariable, frameDepth),
                        Ast.Block(Ast.Assign(exceptionThrownVariable, AstUtils.Constant(true)), AstUtils.Empty()),
                        Ast.Block(
                            Ast.Assign(passingVariable, Ast.Condition(
                                MakeRescuePass(gen.CurrentScopeVariable, exceptionVariable, rescueSites),
                                exceptionVariable,
                                AstUtils.Constant(null, typeof(Exception))
                            )),
                            Ast.NotEqual(passingVariable, AstUtils.Constant(null, typeof(Exception)))
                        )
                    ),
                    Ast.Assign(passingVariable, AstUtils.Constant(null, typeof(Exception))),
                    AstUtils.IfThen(exceptionThrownVariable, transformedRescue),
                    AstUtils.Empty()
                ).Finally(
                    Ast.Assign(handledVariable, Ast.Condition(exceptionThrownVariable,
                        Methods.GetCurrentException.OpCall(gen.CurrentScopeVariable), AstUtils.Constant(null, typeof(Exception)))),
                    // restore previous exception if the current one has been handled:
                    AstUtils.Unless(exceptionRethrowVariable,
                        Methods.SetCurrentException.OpCall(gen.CurrentScopeVariable, oldExceptionVariable)
                    ),
                    leaveRescue
                );
            } else if (_rescueClauses != null) {
                guarded = AstUtils.Try(
                    AstUtils.Try(
                        Ast.Block(transformedBody, AstUtils.Empty())
                    ).Filter(exceptionVariable = Ast.Parameter(typeof(Exception), "#e"),
                        Ast.Call(_CanRescueDeferTrace, gen.CurrentScopeVariable, exceptionVariable, frameDepth),

                        Ast.Assign(exceptionThrownVariable, AstUtils.Constant(true)),
                        AstUtils.Empty()
                    ),
                    AstUtils.IfThen(exceptionThrownVariable, transformedRescue),
                    AstUtils.Empty()
                ).Finally(
                    Ast.Assign(handledVariable, Ast.Condition(exceptionThrownVariable,
                        Methods.GetCurrentException.OpCall(gen.CurrentScopeVariable), AstUtils.Constant(null, typeof(Exception)))),
                    // restore previous exception if the current one has been handled:
                    AstUtils.Unless(exceptionRethrowVariable,
                        Methods.SetCurrentException.OpCall(gen.CurrentScopeVariable, oldExceptionVariable)
                    ),
                    leaveRescue
                );
            } else if (_elseStatements == null) {
                // ensure only: compiled, an exception that the handler would only take note of and throw again goes through, and
                // one whose backtrace is still to be taken may go through as well where nothing in the frame throws it again
                // (RubyOps.PassEnsure)
                guarded = new EnsureGuardExpression(
                    Ast.Block(transformedBody, AstUtils.Empty()), gen.CurrentScopeVariable, exceptionThrownVariable, exceptionRethrowVariable,
                    (EnsureJumpFinder.MayJump(_ensureStatements) || GotoFinder.Has(transformedEnsure)) ? AstUtils.Constant(-1) : gen.CurrentCompiledFrameDepth
                );
            } else {
                guarded = AstUtils.Try(
                    Ast.Block(transformedBody, AstUtils.Empty())
                ).Filter(exceptionVariable = Ast.Parameter(typeof(Exception), "#e"),
                    Methods.CanRescue.OpCall(gen.CurrentScopeVariable, exceptionVariable),

                    Ast.Assign(exceptionThrownVariable, AstUtils.Constant(true)),
                    transformedRescue,
                    AstUtils.Empty()
                );
            }

            var protectedBody = Ast.Block(
                Ast.Assign(exceptionThrownVariable, AstUtils.Constant(false)),
                Ast.Assign(exceptionRethrowVariable, AstUtils.Constant(false)),
                Ast.Assign(retryingVariable, AstUtils.Constant(false)),

                // save exception (old_$! is not used unless there is a rescue clause):
                (_rescueClauses == null) ? (MSA.Expression)AstUtils.Empty() :
                    Ast.Assign(oldExceptionVariable, Methods.GetCurrentException.OpCall(gen.CurrentScopeVariable)),

                guarded,

                // unless (exception_thrown) do <else-statements> end
                transformedElse,
                AstUtils.Empty()
            );

            MSA.Expression saveEntryException = (entryExceptionVariable != null) ?
                (MSA.Expression)Ast.Assign(entryExceptionVariable, Methods.GetCurrentException.OpCall(gen.CurrentScopeVariable)) :
                AstUtils.Empty();

            if (passingVariable != null) {
                // the exception the rescue clauses let through is not caught here either (see above)
                exceptionVariable = Ast.Parameter(typeof(Exception), "#e");
                return Ast.Block(
                    saveEntryException,
                    Ast.Label(retryLabel),
                    AstUtils.Try(
                        Ast.Assign(passingVariable, AstUtils.Constant(null, typeof(Exception))),
                        new RubyFilterExpression(
                            protectedBody,
                            exceptionVariable,
                            Ast.Call(_CanRescueAfterClauses, gen.CurrentScopeVariable, exceptionVariable, handledVariable),
                            Ast.Block(Ast.Assign(exceptionRethrowVariable, AstUtils.Constant(true)), AstUtils.Empty()),
                            Ast.ReferenceEqual(passingVariable, exceptionVariable)
                        )
                    ).FinallyWithJumps(
                        AstUtils.Unless(retryingVariable,
                            AstUtils.Unless(Ast.Property(null, typeof(RubyUtils), "IsReclaimingFiber"), transformedEnsure)
                        )
                    )
                );
            }

            var result = Ast.Block(
                saveEntryException,
                Ast.Label(retryLabel),
                AstUtils.Try(
                    protectedBody
                ).FilterIf((_rescueClauses != null || _elseStatements != null),
                    exceptionVariable = Ast.Parameter(typeof(Exception), "#e"), 
                    (handledVariable != null) ?
                        Ast.Call(_CanRescueAfterClauses, gen.CurrentScopeVariable, exceptionVariable, handledVariable) :
                        Methods.CanRescue.OpCall(gen.CurrentScopeVariable, exceptionVariable),

                    Ast.Assign(exceptionRethrowVariable, AstUtils.Constant(true)),
                    AstUtils.Empty()
                ).FinallyWithJumps(
                    AstUtils.Unless(retryingVariable,
                        // Reclaiming a discarded fiber drops Ruby ensure bodies, just as freeing
                        // a native fiber stack does. CLR cleanup finally blocks still execute.
                        AstUtils.Unless(Ast.Property(null, typeof(RubyUtils), "IsReclaimingFiber"), transformedEnsure)
                    )
                )
            );

            return result;
        }

        // the ensure clause's statements, whose lines say so (SourceLineMarker.InEnsure)
        private MSA.Expression/*!*/ TransformEnsureStatements(AstGenerator/*!*/ gen) {
            var scope = gen.CurrentScope;
            scope.EnsureDepth++;
            try {
                return gen.TransformStatements(_ensureStatements, ResultOperation.Ignore);
            } finally {
                scope.EnsureDepth--;
            }
        }

        internal override Expression/*!*/ ToCondition(LexicalScope/*!*/ currentScope) {
            // propagates 'in condition' property if we have a single element:
            if (_statements != null && _statements.Count == 1 && !HasExceptionHandling) {
                return _statements.First.ToCondition(currentScope);
            }

            return this;
        }
    }
}

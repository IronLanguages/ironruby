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

using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using IronRuby.Runtime.Osr;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstBlock = Microsoft.Scripting.Ast.BlockBuilder;

    // pre-test:
    //   while <expression> do <statements> end
    //   until <expression> do <statements> end
    // post-test:
    //   <statement> while <expression>             
    //   <statement> until <expression>
    //   <block-expression> while <expression>   
    //   <block-expression> until <expression>
    public partial class WhileLoopExpression : Expression {
        private readonly Expression _condition;
        private readonly Statements _statements;		// optional
        private readonly bool _isWhileLoop; // while or until
        private readonly bool _isPostTest;  // do-while or while-do 

        public Expression Condition {
            get { return _condition; }
        }

        public Statements Statements {
            get { return _statements; }
        }

        public bool IsWhileLoop {
            get { return _isWhileLoop; }
        }

        public bool IsPostTest {
            get { return _isPostTest; }
        }

        public WhileLoopExpression(Expression/*!*/ condition, bool isWhileLoop, bool isPostTest, Statements/*!*/ statements, SourceSpan location)
            : base(location) {

            ContractUtils.RequiresNotNull(condition, "condition");
            ContractUtils.RequiresNotNull(statements, "statements");

            _condition = condition;
            _isWhileLoop = isWhileLoop;
            _isPostTest = isPostTest;
            _statements = statements;
        }

        // see Ruby Language.doc/Runtime/Control Flow Implementation/While-Until
        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            MSA.Expression resultVariable = gen.CurrentScope.DefineHiddenVariable("#loop-result", typeof(object));
            MSA.Expression redoVariable = gen.CurrentScope.DefineHiddenVariable("#skip-condition", typeof(bool));
            MSA.ParameterExpression unwinder;

            bool isInnerLoop = gen.CurrentLoop != null;

            // -X:OSR: the loop counts its own back edges, and when it has taken enough of them
            // it asks its site for a type-specialized copy of itself and finishes there. The
            // locals it would carry over are in the enclosing scope's tuple already, so there
            // is nothing to materialize: the copy is handed the same tuples and picks up at the
            // top of the next iteration.
            bool osr = gen.CanReplaceLoops;
            MSA.Expression osrCountdown = null, osrResult = null;
            OsrLoopSite osrSite = null;
            if (osr) {
                osrCountdown = gen.CurrentScope.DefineHiddenVariable("#osr-n", typeof(long));
                osrResult = gen.CurrentScope.DefineHiddenVariable("#osr-result", typeof(object));
                osrSite = new OsrLoopSite(gen.SourcePath + ":" + Location.Start.Line);
                osrSite.LoopAst = this;
                osrSite.Context = gen.Context;
                osrSite.ScopeDepth = gen.CurrentScope.LexicalDepth;
            }

            MSA.LabelTarget breakLabel = Ast.Label();
            MSA.LabelTarget continueLabel = Ast.Label();

            gen.EnterLoop(redoVariable, resultVariable, breakLabel, continueLabel);
            MSA.Expression transformedBody = gen.TransformStatements(_statements, ResultOperation.Ignore);
            MSA.Expression transformedCondition = _condition.TransformCondition(gen, true);
            gen.LeaveLoop();

            MSA.Expression conditionPositiveStmt, conditionNegativeStmt;
            if (_isWhileLoop) {
                conditionPositiveStmt = AstUtils.Empty();
                conditionNegativeStmt = Ast.Break(breakLabel);
            } else {
                conditionPositiveStmt = Ast.Break(breakLabel);
                conditionNegativeStmt = AstUtils.Empty();
            }

            // The tuples the specialized copy addresses the loop's locals through: this scope's
            // own, and one per outer scope the body reached into. Read after the body has been
            // transformed, which is what creates the closure variables.
            MSA.Expression osrArguments = null;
            if (osr) {
                var tupleArgIndex = new Dictionary<int, int>();
                var tupleArguments = new List<MSA.Expression>();
                foreach (var entry in gen.CurrentScope.TupleVariablesByDepth) {
                    if (!tupleArgIndex.ContainsKey(entry.Key)) {
                        tupleArgIndex.Add(entry.Key, tupleArguments.Count);
                        tupleArguments.Add(AstUtils.Box(entry.Value));
                    }
                }
                osrSite.TupleArgIndex = tupleArgIndex;
                // and self, which the instance variables the loop reads belong to
                osrSite.SelfArgIndex = tupleArguments.Count;
                tupleArguments.Add(AstUtils.Box(gen.CurrentSelfVariable));
                // and the scope, whose refinements the copy's operators are resolved under
                osrSite.ScopeArgIndex = tupleArguments.Count;
                tupleArguments.Add(AstUtils.Convert(gen.CurrentScopeVariable, typeof(object)));
                osrArguments = Ast.NewArrayInit(typeof(object), tupleArguments);
            }

            // The back-edge test, at the top of every iteration: two compares and a decrement of
            // a local. Once the site has given up, its countdown is Int64.MaxValue and the test
            // costs exactly that and nothing more, for the whole life of the program.
            //
            // A pending redo must not trip it: the specialized copy starts at the condition, and
            // a redo is precisely the state in which the condition has to be skipped.
            MSA.Expression backEdge = osr ? (MSA.Expression)AstUtils.IfThen(
                Ast.AndAlso(
                    Ast.AndAlso(
                        Ast.GreaterThan(osrCountdown, AstUtils.Constant(0L)),
                        Ast.Not(redoVariable)
                    ),
                    Ast.Equal(Ast.Assign(osrCountdown, Ast.Add(osrCountdown, AstUtils.Constant(-1L))), AstUtils.Constant(0L))
                ),
                Ast.Block(
                    Ast.Assign(osrResult, Ast.Call(Ast.Constant(osrSite, typeof(OsrLoopSite)), OsrLoopSite.RunMethod, osrArguments)),
                    AstUtils.If(
                        Ast.Not(Ast.ReferenceEqual(osrResult, Ast.Constant(OsrLoopSite.Retry, typeof(object)))),
                        Ast.Assign(resultVariable, osrResult),
                        Ast.Break(breakLabel),
                        AstUtils.Empty()
                    ).Else(
                        // The copy handed the loop back. Take the site's budget again rather
                        // than run out the rest of this entry generically: without this the
                        // back edge fires exactly once per entry, so a loop that deopts in its
                        // first thousandth - an accumulator crossing 2^31, say - is never looked
                        // at again, and the specialization is worth nothing on exactly the shape
                        // it was meant for.
                        Ast.Assign(osrCountdown, Ast.Field(Ast.Constant(osrSite, typeof(OsrLoopSite)), OsrLoopSite.CountdownField)),
                        AstUtils.Empty()
                    ),
                    AstUtils.Empty()
                )
            ) : AstUtils.Empty();

            // make the loop first:
            MSA.Expression loop = new AstBlock {
                gen.ClearDebugInfo(),
                Ast.Assign(redoVariable, AstUtils.Constant(_isPostTest)),

                AstFactory.Infinite(breakLabel, continueLabel,
                    // The interrupt check on the back edge, so that Thread#raise, Thread#kill and
                    // Timeout reach a thread spinning in this loop (RubyUtils.SafePoint: one
                    // volatile load and a branch while nothing is pending). It carries the loop's
                    // own line, which is where MRI reports an exception raised at a back edge;
                    // without it the frame would show the cleared sequence point (0xFEEFEE).
                    gen.AddDebugInfo(Ast.Call(RubyUtils.SafePointMethod), Location),
                    backEdge,
                    AstUtils.Try(

                        AstUtils.If(redoVariable, 
                            Ast.Assign(redoVariable, AstUtils.Constant(false))
                        ).ElseIf(transformedCondition,
                            conditionPositiveStmt
                        ).Else(
                            conditionNegativeStmt
                        ),

                        transformedBody,
                        AstUtils.Empty()

                    ).Catch(unwinder = Ast.Parameter(typeof(BlockUnwinder), "#u"), 
                        // redo = u.IsRedo
                        Ast.Assign(redoVariable, Ast.Field(unwinder, BlockUnwinder.IsRedoField)),
                        AstUtils.Empty()

                    ).Filter(unwinder = Ast.Parameter(typeof(EvalUnwinder), "#u"), 
                        Ast.Equal(Ast.Field(unwinder, EvalUnwinder.ReasonField), AstFactory.BlockReturnReasonBreak),

                        // result = unwinder.ReturnValue
                        Ast.Assign(resultVariable, Ast.Field(unwinder, EvalUnwinder.ReturnValueField)),
                        Ast.Break(breakLabel)
                    )
                ),
                gen.ClearDebugInfo(),
                AstUtils.Empty(),
            };

            // wrap it to try finally that updates RFC state:
            if (!isInnerLoop) {
                loop = AstUtils.Try(
                    Methods.EnterLoop.OpCall(gen.CurrentScopeVariable),
                    loop
                ).Finally(
                    Methods.LeaveLoop.OpCall(gen.CurrentScopeVariable)
                );
            }

            if (!osr) {
                return Ast.Block(loop, resultVariable);
            }

            // The budget this entry gets, and, on the way out, what it did not spend - so that a
            // loop entered many times for a few iterations each still adds up to a compilation.
            var site = Ast.Constant(osrSite, typeof(OsrLoopSite));
            return Ast.Block(
                Ast.Assign(osrCountdown, Ast.Field(site, OsrLoopSite.CountdownField)),
                loop,
                AstUtils.IfThen(
                    Ast.AndAlso(
                        Ast.Field(site, OsrLoopSite.CountingField),
                        Ast.LessThan(osrCountdown, Ast.Field(site, OsrLoopSite.CountdownField))
                    ),
                    Ast.Assign(Ast.Field(site, OsrLoopSite.CountdownField), osrCountdown)
                ),
                resultVariable
            );
        }

        internal override MSA.Expression/*!*/ Transform(AstGenerator/*!*/ gen) {
            // do not mark a sequence point wrapping the entire node:
            return TransformRead(gen);
        }        
    }
}

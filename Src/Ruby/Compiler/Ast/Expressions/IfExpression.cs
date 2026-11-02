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

using System.Collections.Generic;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    public partial class IfExpression : Expression {
        private Expression/*!*/ _condition;
        private Statements _body;
        private List<ElseIfClause> _elseIfClauses;

        public Expression/*!*/ Condition {
            get { return _condition; }
        }

        public Statements Body {
            get { return _body; }
        }

        public List<ElseIfClause> ElseIfClauses {
            get { return _elseIfClauses; }
        }

        public IfExpression(Expression/*!*/ condition, Statements/*!*/ body, List<ElseIfClause>/*!*/ elseIfClauses, SourceSpan location)
            : base(location) {
            ContractUtils.RequiresNotNull(body, "body");
            ContractUtils.RequiresNotNull(condition, "condition");
            ContractUtils.RequiresNotNull(elseIfClauses, "elseIfClauses");

            // all but the last clause should have non-null conditions:
            for (int i = 0; i < elseIfClauses.Count - 1; i++) {
                if (elseIfClauses[i].Condition == null) {
                    throw ExceptionUtils.MakeArgumentItemNullException(i, "elseIfClauses");
                }
            }

            _condition = condition;
            _body = body;
            _elseIfClauses = elseIfClauses;
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {

            MSA.Expression result;

            // the :line hooks of elsif conditions are claimed in source order, before the clauses
            // are transformed (last first)
            var traces = new MSA.Expression[_elseIfClauses.Count];
            for (int j = 0; j < _elseIfClauses.Count; j++) {
                if (_elseIfClauses[j].IsElsif) {
                    traces[j] = gen.TraceLine(_elseIfClauses[j].Location.Start.Line);
                }
            }

            int i = _elseIfClauses.Count - 1;

            if (i >= 0 && _elseIfClauses[i].Condition == null) {
                // ... else body end
                result = gen.TransformStatementsToExpression(_elseIfClauses[i].Statements);
                i--;
            } else {
                // no else clause => the result of the if-expression is nil:
                result = AstUtils.Constant(null);
            }

            while (i >= 0) {
                var clause = _elseIfClauses[i];
                var condition = clause.Condition.TransformCondition(gen, true);
                if (traces[i] != null) {
                    condition = MSA.Expression.Block(traces[i], condition);
                }
                if (clause.IsElsif) {
                    // a statement on the elsif's line has no event of its own
                    gen.SetLastTracedLine(clause.Location.Start.Line);
                }

                // emit: else (if (condition) body else result)
                result = AstFactory.Condition(
                    condition,
                    gen.TransformStatementsToExpression(clause.Statements),
                    result
                );
                i--;
            }

            // if (condition) body else result
            return AstFactory.Condition(
                _condition.TransformCondition(gen, true),
                gen.TransformStatementsToExpression(_body),
                result
            );
        }

        internal override MSA.Expression/*!*/ Transform(AstGenerator/*!*/ gen) {
            // do not mark a sequence point wrapping the entire condition:
            return TransformRead(gen);
        }
    }
}

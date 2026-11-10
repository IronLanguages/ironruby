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

using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Ast;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    // rescue types, *type-array, types, *type-array, ... 
    //   statements
    // rescue types, *type-array, types, *type-array, ... => target
    //   statements
    public partial class RescueClause : Node {
        private readonly Expression/*!*/[]/*!*/ _types;  // might be empty, might include SplattedArgument expressions
        private readonly LeftValue _target;		         // optional
        private readonly Statements _statements;         // optional

        public Expression/*!*/[]/*!*/ Types {
            get { return _types; }
        }

        public LeftValue Target {
            get { return _target; }
        }

        public Statements Statements {
            get { return _statements; }
        }
        
        public RescueClause(Expression/*!*/[]/*!*/ types, LeftValue target, Statements statements, SourceSpan location)
            : base(location) {
            Assert.NotNullItems(types);
            _types = types;
            _target = target;
            _statements = statements;
        }

        //
        // rescue stmts                     ... if (StandardError === $!) { stmts; } 
        // rescue <types> stmts             ... if (type1 === $! || type2 === $! ...) { stmts; }
        // rescue <types> => <lvalue> stmts ... if (type1 === $! || type2 === $! ...) { <lvalue> = $!; stmts; }
        // 
        internal IfStatementTest/*!*/ Transform(AstGenerator/*!*/ gen, ResultOperation resultOperation) {
            Assert.NotNull(gen);
            
            var comparisonSiteStorage = Ast.Constant(new BinaryOpStorage(gen.Context));
            MSA.Expression condition;
            if (_types.Length != 0) {
                // CompareException(type[0]) || ... || CompareException(type[n]): as in MRI, each type is evaluated
                // and compared before the next one is evaluated, and the ones after a match are not evaluated at all.
                condition = MakeCompareException(gen, comparisonSiteStorage, _types[0].TransformRead(gen), _types[0] is SplattedArgument);
                for (int i = 1; i < _types.Length; i++) {
                    condition = Ast.OrElse(condition, MakeCompareException(gen, comparisonSiteStorage, _types[i].TransformRead(gen), _types[i] is SplattedArgument));
                }
            } else {
                condition = Methods.CompareDefaultException.OpCall(comparisonSiteStorage, gen.CurrentScopeVariable);
            }

            return AstUtils.IfCondition(condition,
                gen.TransformStatements(
                    // <lvalue> = e;
                    (_target != null) ? _target.TransformWrite(gen, Ast.Call(typeof(RubyOps).GetMethod(nameof(RubyOps.GetCurrentExceptionForRescueTarget)), gen.CurrentScopeVariable)) : null,

                    // body:
                    _statements,

                    resultOperation
                )
            );
        }

        private MSA.Expression/*!*/ MakeCompareException(AstGenerator/*!*/ gen, MSA.Expression/*!*/ comparisonSiteStorage, MSA.Expression/*!*/ expression, bool isSplatted) {
            if (isSplatted) {
                return Methods.CompareSplattedExceptions.OpCall(comparisonSiteStorage, gen.CurrentScopeVariable, expression);
            } else {
                return Methods.CompareException.OpCall(comparisonSiteStorage, gen.CurrentScopeVariable, AstUtils.Box(expression));
            }
        }
    }
}


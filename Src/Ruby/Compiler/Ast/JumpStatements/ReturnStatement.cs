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

using System.Reflection;
using Microsoft.Scripting;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    
    public partial class ReturnStatement : JumpStatement {
        public ReturnStatement(Arguments arguments, SourceSpan location)
            : base(arguments, location) {
        }

        // see Ruby Language.doc/Runtime/Control Flow Implementation/Return
        internal override MSA.Expression/*!*/ TransformJump(AstGenerator/*!*/ gen) {
            MSA.Expression transformedReturnValue = TransformReturnValue(gen);

            // eval:
            if (gen.CompilerOptions.IsEval) {
                return gen.Return(Methods.EvalReturn.OpCall(gen.CurrentScopeVariable, AstUtils.Box(transformedReturnValue)));
            }

            // block:
            if (gen.CurrentBlock != null) {
                for (var block = gen.CurrentBlock.ParentBlock; block != null; block = block.ParentBlock) {
                    block.HasNestedReturn = true;
                }
                return gen.Return(Methods.BlockReturn.OpCall(gen.CurrentBlock.BfcVariable, AstUtils.Box(transformedReturnValue)));
            }

            // method:
            var method = gen.CurrentMethod;
            if (gen.Traceable && method.MethodName != null && method != gen.TopLevelScope && gen.GetEnclosingModuleFrame() == null) {
                // TracePoint :return reports the line of the `return', not that of `end'.
                if (method.ExplicitReturnLabel == null) {
                    method.ExplicitReturnLabel = Ast.Label(typeof(object));
                }
                return Ast.Return(method.ExplicitReturnLabel, new TraceReturnExpression(
                    gen.CurrentScopeVariable, AstUtils.Box(transformedReturnValue), TraceEvents.Return, gen.SourcePath, Location.Start.Line
                ));
            }

            return gen.Return(transformedReturnValue);
        }

        internal static MSA.Expression/*!*/ Propagate(AstGenerator/*!*/ gen, MSA.Expression/*!*/ resultVariable) {
            // eval:
            if (gen.CompilerOptions.IsEval) {
                return Methods.EvalPropagateReturn.OpCall(resultVariable);
            }

            // block:
            if (gen.CurrentBlock != null) {
                return Methods.BlockPropagateReturn.OpCall(
                    gen.CurrentBlock.BfcVariable,
                    resultVariable
                );
            }

            // method:
            return Methods.MethodPropagateReturn.OpCall(
                gen.CurrentScopeVariable,
                gen.MakeMethodBlockParameterRead(),
                Ast.Convert(resultVariable, typeof(BlockReturnResult))
            );
        }
    }
}

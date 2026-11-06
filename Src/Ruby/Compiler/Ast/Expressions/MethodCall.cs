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

using System.Diagnostics;
using System.Dynamic;
using Microsoft.Scripting;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstBlock = Microsoft.Scripting.Ast.BlockBuilder;
    
    /// <summary>
    /// target.method_id(args)
    /// </summary>
    public partial class MethodCall : CallExpression {
        private string/*!*/ _methodName;
        private readonly Expression _target;

        public string/*!*/ MethodName {
            get { return _methodName; }
        }

        public Expression Target {
            get { return _target; }
        }

        /// <summary>
        /// A bare identifier - no receiver, arguments or parentheses - that is not a local variable.
        /// </summary>
        public bool IsVariableCall { get; set; }

        public MethodCall(Expression target, string/*!*/ methodName, Arguments args, SourceSpan location)
            : this(target, methodName, args, null, location) {
        }
    
        public MethodCall(Expression target, string/*!*/ methodName, Arguments args, Block block, SourceSpan location)
            : base(args, block, location) {
            Assert.NotEmpty(methodName);

            _methodName = methodName;
            _target = target;
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            var literal = _target as StringLiteral;
            if (literal != null && _methodName == "freeze" && (Arguments == null || Arguments.IsEmpty) && Block == null) {
                return literal.TransformReadFrozen(gen);
            }

            var array = _target as ArrayConstructor;
            if (array != null && (_methodName == "min" || _methodName == "max") && (Arguments == null || Arguments.IsEmpty) && Block == null) {
                int splattedCount;
                if (array.Arguments.Expressions.Length == 2 && array.Arguments.IndexOfSplatted(out splattedCount) < 0) {
                    return TransformArrayLiteralExtreme(gen, array.Arguments.Expressions[0], array.Arguments.Expressions[1]);
                }
            }

            MSA.Expression transformedTarget;
            bool hasImplicitSelf;
            if (_target != null) {
                transformedTarget = _target.TransformRead(gen);
                // `self.foo` may call a private method, just like a receiverless call does
                hasImplicitSelf = _target.NodeType == NodeTypes.SelfReference;
            } else {
                transformedTarget = gen.CurrentSelfVariable;
                hasImplicitSelf = true;
            }
            return TransformRead(this, gen, hasImplicitSelf, _methodName, transformedTarget, Arguments, Block, null, null);
        }

        // `[a, b].min' and `[a, b].max', the commonest way Ruby spells the smaller (larger) of two
        // values: answered without building the array while Array#min (#max) is the library's own
        // method (ArrayExtremeSite), made as written otherwise. The elements are evaluated first
        // either way, as the array literal would.
        private MSA.Expression/*!*/ TransformArrayLiteralExtreme(AstGenerator/*!*/ gen, Expression/*!*/ first, Expression/*!*/ second) {
            var item1 = gen.CurrentScope.DefineHiddenVariable("#item1", typeof(object));
            var item2 = gen.CurrentScope.DefineHiddenVariable("#item2", typeof(object));
            var result = gen.CurrentScope.DefineHiddenVariable("#extreme", typeof(object));

            var call = TransformRead(this, gen, false, _methodName, Methods.MakeArray2.OpCall(item1, item2), null, null, null, null);
            var site = new ArrayExtremeSite(_methodName == "max");

            return Ast.Block(
                Ast.Assign(item1, AstUtils.Box(first.TransformRead(gen))),
                Ast.Assign(item2, AstUtils.Box(second.TransformRead(gen))),
                Ast.Assign(result, Methods.ArrayLiteralExtreme.OpCall(gen.CurrentScopeVariable, item1, item2, AstUtils.Constant(site))),
                Ast.Condition(Methods.IsArrayExtremeNotHandled.OpCall(result), AstUtils.Box(call), result, typeof(object))
            );
        }

        // arguments: complex arguments (expressions, maplets, splat, block) 
        // singleArgument: siple argument (complex are not used)
        // assignmentRhsArgument: rhs of the assignment: target.method=(rhs)
        internal static MSA.Expression/*!*/ TransformRead(Expression/*!*/ node, AstGenerator/*!*/ gen, bool hasImplicitSelf, 
            string/*!*/ methodName, MSA.Expression/*!*/ transformedTarget,
            Arguments arguments, Block block, MSA.Expression singleArgument, MSA.Expression assignmentRhsArgument) {

            Debug.Assert(assignmentRhsArgument == null || block == null, "Block not allowed in assignment");
            Debug.Assert(singleArgument == null || arguments == null && assignmentRhsArgument == null);
            Assert.NotNull(gen, transformedTarget);
            Assert.NotEmpty(methodName);

            // -X:JIT: `m(x, k: v)' and `r.m(x, k: v)' may go straight into m's specialized body (Runtime/Jit/JitKeywordCall.cs)
            if (block == null && singleArgument == null && assignmentRhsArgument == null && node is MethodCall) {
                var direct = IronRuby.Runtime.Jit.JitKeywordCall.TryTransform(gen, methodName, transformedTarget, arguments, hasImplicitSelf);
                if (direct != null) {
                    return direct;
                }
            }

            // Pass args in this order:
            // 1. instance
            // 2. block (if present)
            // 3. passed args: normal args, maplets, array
            // 4. RHS of assignment (if present)

            MSA.Expression blockArgVariable;
            MSA.Expression transformedBlock;

            if (block != null) {
                blockArgVariable = gen.CurrentScope.DefineHiddenVariable("#block-def", typeof(Proc));
                transformedBlock = block.Transform(gen);
            } else {
                blockArgVariable = transformedBlock = null;
            }

            var siteBuilder = new CallSiteBuilder(gen, transformedTarget, blockArgVariable);

            // `recv.m(args, &blk)': MRI evaluates the receiver and the arguments - expanding a
            // splat into a fresh array - before the block argument, which is computed first below
            // for a literal block's sake (retry). So they are evaluated into temporaries up front.
            MSA.Expression hoisted = null;
            if (block != null && !block.IsDefinition && singleArgument == null && assignmentRhsArgument == null) {
                var scratch = new CallSiteBuilder(gen, transformedTarget, blockArgVariable);
                if (arguments != null) {
                    arguments.TransformToCall(gen, scratch);
                }

                var assignments = new AstBlock();
                var targetTemp = gen.CurrentScope.DefineHiddenVariable("#target", typeof(object));
                assignments.Add(Ast.Assign(targetTemp, AstUtils.Box(transformedTarget)));

                var ordered = new CallSiteBuilder(gen, targetTemp, blockArgVariable);
                int index = 0;
                foreach (var argument in scratch) {
                    // skip the scope, the instance and the block
                    if (index++ < 3) {
                        continue;
                    }
                    var temp = gen.CurrentScope.DefineHiddenVariable("#arg", argument.Type);
                    assignments.Add(Ast.Assign(temp, argument));
                    ordered.Add(temp);
                }
                if (scratch.SplattedArgument != null) {
                    var temp = gen.CurrentScope.DefineHiddenVariable("#splat", typeof(RubyArray));
                    assignments.Add(Ast.Assign(temp,
                        Ast.New(typeof(RubyArray).GetConstructor(new[] { typeof(System.Collections.IList) }),
                            AstUtils.Convert(scratch.SplattedArgument, typeof(System.Collections.IList)))
                    ));
                    ordered.SplattedArgument = temp;
                }
                hoisted = assignments;
                arguments = null;
                siteBuilder = ordered;
            }

            if (arguments != null) {
                arguments.TransformToCall(gen, siteBuilder);
            } else if (singleArgument != null) {
                siteBuilder.Add(singleArgument);
            }

            MSA.Expression rhsVariable = null;
            if (assignmentRhsArgument != null) {
                rhsVariable = gen.CurrentScope.DefineHiddenVariable("#rhs", assignmentRhsArgument.Type);
                siteBuilder.RhsArgument = Ast.Assign(rhsVariable, assignmentRhsArgument);
            }

            var call = node as MethodCall;
            siteBuilder.IsVariableCall = call != null && call.IsVariableCall;
            var dynamicSite = siteBuilder.MakeCallAction(methodName, hasImplicitSelf);
            MSA.Expression result = gen.DebugMark(dynamicSite, methodName);

            if (block != null) {
                result = gen.DebugMark(MakeCallWithBlockRetryable(gen, result, blockArgVariable, transformedBlock, block.IsDefinition, hoisted),
                    "#RB: method call with a block ('" + methodName + "')");
            }

            if (assignmentRhsArgument != null) {
                result = Ast.Block(result, rhsVariable);
            }

            return result;
        }

        internal static MSA.Expression/*!*/ MakeCallWithBlockRetryable(AstGenerator/*!*/ gen, MSA.Expression/*!*/ invoke,
            MSA.Expression blockArgVariable, MSA.Expression transformedBlock, bool isBlockDefinition) {
            return MakeCallWithBlockRetryable(gen, invoke, blockArgVariable, transformedBlock, isBlockDefinition, null);
        }

        internal static MSA.Expression/*!*/ MakeCallWithBlockRetryable(AstGenerator/*!*/ gen, MSA.Expression/*!*/ invoke,
            MSA.Expression blockArgVariable, MSA.Expression transformedBlock, bool isBlockDefinition, MSA.Expression argumentEvaluation) {
            Assert.NotNull(invoke);
            Debug.Assert((blockArgVariable == null) == (transformedBlock == null));

            // see Ruby Language.doc/Control Flow Implementation/Method Call With a Block
            MSA.Expression resultVariable = gen.CurrentScope.DefineHiddenVariable("#method-result", typeof(object));
            MSA.ParameterExpression evalUnwinder;

            MSA.LabelTarget retryLabel = Ast.Label("retry");

            var result = new AstBlock {
                argumentEvaluation,
                Ast.Assign(blockArgVariable, Ast.Convert(transformedBlock, blockArgVariable.Type)),

                Ast.Label(retryLabel),

                (isBlockDefinition) ? Methods.InitializeBlock.OpCall(blockArgVariable) : null,

                AstUtils.Try(
                    Ast.Assign(resultVariable, invoke)
                ).Catch(evalUnwinder = Ast.Parameter(typeof(EvalUnwinder), "#u"),
                    Ast.Assign(
                        resultVariable, 
                        Ast.Field(evalUnwinder, EvalUnwinder.ReturnValueField)
                    )
                ),

                Ast.IfThen(Ast.TypeEqual(resultVariable, typeof(BlockReturnResult)),
                    Ast.IfThenElse(Methods.IsRetrySingleton.OpCall(resultVariable),
                        // retry:
                        AstUtils.IfThenElse(Ast.Equal(gen.MakeMethodBlockParameterRead(), blockArgVariable),
                            RetryStatement.TransformRetry(gen),
                            Ast.Goto(retryLabel)
                        ),
                        // return:
                        gen.Return(ReturnStatement.Propagate(gen, resultVariable))
                    )
                ),

                resultVariable
            };

            return result;
        }

        internal override MSA.Expression TransformDefinedCondition(AstGenerator/*!*/ gen) {
            // MRI doesn't evaluate the arguments 
            MSA.Expression result = AstUtils.LightDynamic(
                RubyCallAction.Make(gen.Context, _methodName, RubyCallSignature.IsDefined(_target == null)), 
                typeof(bool),
                gen.CurrentScopeVariable,
                (_target != null) ? AstUtils.Box(_target.TransformRead(gen)) : gen.CurrentSelfVariable
            );

            return (_target != null) ? gen.TryCatchAny(result, AstFactory.False) : result;
        }

        internal override string/*!*/ GetNodeName(AstGenerator/*!*/ gen) {
            return "method";
        }
    }
}

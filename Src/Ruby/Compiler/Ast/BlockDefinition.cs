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
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstExpressions = ReadOnlyCollectionBuilder<MSA.Expression>;
    using AstParameters = ReadOnlyCollectionBuilder<MSA.ParameterExpression>;

    public partial class BlockDefinition : Block {
        //	{ |args| body }

        // self, block flow control:
        internal const int HiddenParameterCount = BlockDispatcher.HiddenParameterCount;

        // if the block has more parameters object[] is used:
        internal const int MaxBlockArity = BlockDispatcher.MaxBlockArity;

        private readonly Parameters/*!*/ _parameters;
        private readonly Statements/*!*/ _body;
        private readonly LexicalScope/*!*/ _definedScope;

        public Parameters/*!*/ Parameters {
            get { return _parameters; }
        }

        public Statements/*!*/ Body {
            get { return _body; }
        }

        public sealed override bool IsDefinition { get { return true; } }

        // The number of parameters excluding splat and proc:
        private int ParameterCount {
            get {
                return _parameters.Mandatory.Length + _parameters.Optional.Length;
            }
        }

        private bool HasFormalParametersInArray {
            get {
                return ParameterCount > MaxBlockArity || HasUnsplatParameter || HasProcParameter || HasPostParameters;
            }
        }

        // Mandatory parameters written after the optional/rest ones, as in |a, *b, c|.
        // A trailing Placeholder is not one of those: it is how `|a,|` spells "swallow the rest",
        // so it binds like a rest parameter rather than like a post parameter.
        private int PostParameterCount {
            get {
                int count = 0;
                for (int i = _parameters.LeadingMandatoryCount; i < _parameters.Mandatory.Length; i++) {
                    if (!(_parameters.Mandatory[i] is Placeholder)) {
                        count++;
                    }
                }
                return count;
            }
        }

        private bool HasPostParameters {
            get { return PostParameterCount > 0; }
        }

        private bool HasUnsplatParameter {
            get {
                return _parameters.Unsplat != null;
            }
        }

        private bool HasProcParameter {
            get {
                return _parameters.Block != null;
            }
        }

        public BlockDefinition(LexicalScope/*!*/ definedScope, Parameters parameters, Statements/*!*/ body, SourceSpan location)
            : base(location) {
            Assert.NotNull(definedScope, body);

            _definedScope = definedScope;
            _body = body;
            _parameters = parameters ?? Parameters.Empty; 
        }

        private AstParameters/*!*/ DefineParameters(out MSA.ParameterExpression/*!*/ selfVariable, out MSA.ParameterExpression/*!*/ blockParamVariable) {
            // Block signature:
            // <proc>, <self>, <leading-mandatory>, <trailing-mandatory>, <optional>, &<block>, *<unsplat>
            // or
            // <proc>, <self>, object[] { <leading-mandatory>, <trailing-mandatory>, <optional>, &<block> } *<unsplat>,
            var parameters = new AstParameters(
                HiddenParameterCount +
                (HasFormalParametersInArray ? 1 : ParameterCount) +
                (HasUnsplatParameter ? 1 : 0) +
                (HasProcParameter ? 1 : 0)
            );

            // hidden parameters:
            // #proc must be the first one - it is used as instance target for method invocation:
            parameters.Add(blockParamVariable = Ast.Parameter(typeof(BlockParam), "#bp"));
            parameters.Add(selfVariable = Ast.Parameter(typeof(object), "#self"));

            if (HasFormalParametersInArray) {
                parameters.Add(Ast.Parameter(typeof(object[]), "#parameters"));
            } else {
                for (int i = 0; i < ParameterCount; i++) {
                    parameters.Add(Ast.Parameter(typeof(object), "#" + i));
                }
            }

            if (HasUnsplatParameter) {
                parameters.Add(Ast.Parameter(typeof(RubyArray), "#array"));
            }

            if (HasProcParameter) {
                parameters.Add(Ast.Parameter(typeof(Proc), "#proc"));
            }

            return parameters;
        }

        private ScopeBuilder/*!*/ DefineLocals(ScopeBuilder/*!*/ parentBuilder) {
            return new ScopeBuilder(_definedScope.AllocateClosureSlotsForLocals(0), parentBuilder, _definedScope);
        }

        internal override MSA.Expression/*!*/ Transform(AstGenerator/*!*/ gen) {
            return Transform(gen, false);
        }

        internal MSA.Expression/*!*/ Transform(AstGenerator/*!*/ gen, bool isLambda) {
            ScopeBuilder scope = DefineLocals(gen.CurrentScope);

            // define hidden parameters and RHS-placeholders (#1..#n will be used as RHS of a parallel assignment):
            MSA.ParameterExpression blockParameter, selfParameter;
            var parameters = DefineParameters(out selfParameter, out blockParameter);

            MSA.ParameterExpression scopeVariable = scope.DefineHiddenVariable("#scope", typeof(RubyBlockScope));
            MSA.LabelTarget redoLabel = Ast.Label();

            gen.EnterBlockDefinition(
                scope,
                blockParameter,
                selfParameter,
                scopeVariable,
                redoLabel
            );

            MSA.Expression paramInit = MakeParametersInitialization(gen, parameters);
            MSA.ParameterExpression blockUnwinder, filterVariable;

            MSA.Expression traceCall, traceReturn;
			if (gen.TraceEnabled) {
                int firstStatementLine = _body.Count > 0 ? _body.First.Location.Start.Line : Location.End.Line;
                int lastStatementLine = _body.Count > 0 ? _body.Last.Location.End.Line : Location.End.Line;

                traceCall = Methods.TraceBlockCall.OpCall(scopeVariable, blockParameter, gen.SourcePathConstant, AstUtils.Constant(firstStatementLine));
                traceReturn = Methods.TraceBlockReturn.OpCall(scopeVariable, blockParameter, gen.SourcePathConstant, AstUtils.Constant(lastStatementLine));
            } else {
                traceCall = traceReturn = Ast.Empty();
            }

            MSA.Expression statements = gen.TransformStatements(_body, ResultOperation.Return);
            bool hasNestedReturn = gen.CurrentBlock.HasNestedReturn;

            // A body that never uses its scope - { }, { |x| x } - has no use for the RubyBlockScope
            // every invocation would allocate, except to hand to the TracePoint hooks, which only
            // evaluate their scope while a TracePoint listens: there it is made on demand.
            MSA.Expression lazyScope = null, filterScope = scopeVariable;
            if (!hasNestedReturn && !gen.TraceEnabled && !gen.CompilerOptions.IsEval && !scope.HasClosures && IsScopeFreeBody()) {
                var names = scope.GetVariableNamesExpression();
                Func<MSA.Expression> makeScope = () => Methods.CreateBlockScopeLazy.OpCall(
                    scope.LocalsTupleVariable, names, blockParameter, selfParameter, EnterInterpretedFrameExpression.Instance
                );
                var lazy = Ast.Coalesce(scopeVariable, Ast.Assign(scopeVariable, makeScope()));
                var rewriter = new LazyScopeRewriter(scopeVariable, lazy);
                var lazyParamInit = rewriter.Visit(paramInit);
                var lazyStatements = rewriter.Visit(statements);
                if (!rewriter.NeedsScope) {
                    paramInit = lazyParamInit;
                    statements = lazyStatements;
                    lazyScope = lazy;
                    filterScope = Ast.Coalesce(scopeVariable, makeScope());
                }
            }

            var bodyTry = AstUtils.Try(
                statements
            ).Catch(blockUnwinder = Ast.Parameter(typeof(BlockUnwinder), "#u"),
                // redo:
                AstUtils.IfThen(Ast.Field(blockUnwinder, BlockUnwinder.IsRedoField), Ast.Goto(redoLabel)),

                // next:
                gen.Return(Ast.Field(blockUnwinder, BlockUnwinder.ReturnValueField))
            );

            MSA.Expression enterNestedReturn, leaveNestedReturn;
            if (hasNestedReturn) {
                // a return in a nested block leaves this block if it runs as a lambda, however it was invoked:
                MSA.ParameterExpression lambdaUnwinder;
                bodyTry = bodyTry.Filter(lambdaUnwinder = Ast.Parameter(typeof(Exception), "#lu"),
                    Methods.IsLambdaScopeUnwinderTarget.OpCall(scopeVariable, lambdaUnwinder),
                    gen.Return(Methods.GetLambdaUnwinderReturnValue.OpCall(lambdaUnwinder))
                );
                enterNestedReturn = Methods.EnterLambdaReturnScope.OpCall(scopeVariable);
                leaveNestedReturn = Methods.LeaveLambdaReturnScope.OpCall(scopeVariable);
            } else {
                enterNestedReturn = leaveNestedReturn = Ast.Empty();
            }

            MSA.Expression body = AstUtils.Try(
                enterNestedReturn,
                paramInit,
                traceCall,
                Ast.Label(redoLabel),
                bodyTry
            ).Filter(filterVariable = Ast.Parameter(typeof(Exception), "#e"),
                Methods.FilterBlockException.OpCall(filterScope, filterVariable)
            ).Finally(
                AstUtils.Try(traceReturn).Finally(
                    leaveNestedReturn,
                    Methods.ReleaseScopeFrame.OpCall(scopeVariable),
                    Ast.Empty()
                )
            );

            // A body that needs its scope but defines no closure over it can reuse the scope of the
            // previous call of the block, unless something captured that (see RubyScope.IsCaptured).
            // A nested block literal is a Proc over the scope on every call: nothing to reuse.
            bool reuseScope = lazyScope == null && !hasNestedReturn && !gen.CurrentBlock.HasNestedBlock &&
                !gen.TraceEnabled && !gen.CompilerOptions.IsEval;

            body = gen.Traceable
                ? new TraceReturnExpression(lazyScope ?? scopeVariable, gen.AddReturnTarget(body), TraceEvents.BReturn | TraceEvents.Return, gen.SourcePath, Location.End.Line)
                : gen.AddReturnTarget(body);

            if (reuseScope) {
                // the body stays in statement position: as a call argument it would have to be spilled
                var result = scope.DefineHiddenVariable("#result", typeof(object));
                bool keepLocals = scope.HasReusableLocals;
                MSA.Expression leave = Methods.LeaveReusableBlockScope.OpCall(scopeVariable, AstUtils.Constant(keepLocals));
                if (keepLocals) {
                    leave = Ast.IfThen(leave, scope.MakeClearLocals());
                }
                body = Ast.Block(
                    Ast.Assign(result, AstUtils.Convert(body, typeof(object))),
                    leave,
                    result
                );
            }

            MSA.Expression enterScope;
            if (reuseScope) {
                // the scope to reuse is taken first, its tuple from it (see RubyOps.TakeReusableBlockScope)
                var reusableScope = scope.DefineHiddenVariable("#reusable", typeof(RubyBlockScope));
                enterScope = Methods.CreateReusableBlockScope.OpCall(new AstExpressions {
                    scope.MakeReusableLocalsStorage(blockParameter, reusableScope),
                    scope.GetVariableNamesExpression(),
                    blockParameter,
                    selfParameter,
                    EnterInterpretedFrameExpression.Instance,
                    reusableScope
                });
            } else {
                enterScope = (lazyScope != null ? Methods.CreateBlockScopeIfTraced : Methods.CreateBlockScope).OpCall(new AstExpressions {
                    scope.MakeLocalsStorage(),
                    scope.GetVariableNamesExpression(),
                    blockParameter, 
                    selfParameter,
                    EnterInterpretedFrameExpression.Instance
                });
            }

            body = scope.CreateScope(
                scopeVariable,
                enterScope,
                body
            );

            gen.LeaveBlockDefinition();

            int parameterCount = ParameterCount;
            var attributes = _parameters.GetBlockSignatureAttributes();

            var blockDispatcher = BlockDispatcher.Create(parameterCount, PostParameterCount, attributes, gen.SourcePath, Location.Start.Line);
            blockDispatcher.ParameterSignature = _parameters.Signature;

            var dispatcher = Ast.Constant(blockDispatcher, typeof(BlockDispatcher));

            return Ast.Coalesce(
                (isLambda ? Methods.InstantiateLambda : Methods.InstantiateBlock).OpCall(gen.CurrentScopeVariable, gen.CurrentSelfVariable, dispatcher),
                (isLambda ? Methods.DefineLambda : Methods.DefineBlock).OpCall(gen.CurrentScopeVariable, gen.CurrentSelfVariable, dispatcher,
                    BlockDispatcher.CreateLambda(
                        body,
                        RubyStackTraceBuilder.EncodeMethodName(BlockFrameLabel(gen), gen.SourcePath, Location, gen.DebugMode),
                        parameters,
                        parameterCount,
                        PostParameterCount,
                        attributes
                    )
                )
            );
        }

        // MRI labels a block frame "block in <enclosing>", and counts the nesting once there
        // is more than one level. This runs after LeaveBlockDefinition, so the generator's
        // block chain is already the enclosing one and this block is the extra level.
        private static string/*!*/ BlockFrameLabel(AstGenerator/*!*/ gen) {
            string enclosing;
            AstGenerator.BlockScope outermost;
            int levels = 1;
            var module = gen.GetEnclosingModuleFrame();
            if (module != null) {
                enclosing = module.FrameLabel;
                outermost = module.OuterBlock;
            } else if (gen.CompilerOptions.EvalFrameBaseLabel != null && gen.CurrentMethod == gen.TopLevelScope) {
                // a block in an eval counts its levels from the frame the eval runs in
                enclosing = gen.CompilerOptions.EvalFrameBaseLabel;
                levels += gen.CompilerOptions.EvalFrameBlockLevels;
                outermost = null;
            } else {
                enclosing = gen.CurrentMethod.FrameLabel ?? gen.CurrentMethod.MethodName ?? gen.TopLevelFrameLabel;
                outermost = null;
            }

            for (var block = gen.CurrentBlock; block != null && block != outermost; block = block.ParentBlock) {
                levels++;
            }

            return AstGenerator.FormatBlockLabel(enclosing, levels);
        }

        /// <summary>
        /// Nothing in the parameters or the body needs the block's scope at runtime: plain
        /// parameters, and statements that only read and assign the block's own locals, literals and
        /// self. Every call site takes the scope, and so do constants, instance and global variables,
        /// so this is the call-free body: { }, { |x| x }, { |a, b| a }.
        /// The transformed body is still checked (LazyScopeRewriter); this is the cheap filter.
        /// </summary>
        private bool IsScopeFreeBody() {
            if (_parameters.Optional.Length > 0 || _parameters.Unsplat != null || _parameters.Block != null || HasPostParameters) {
                return false;
            }
            var signature = _parameters.Signature;
            if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords)) {
                return false;
            }
            foreach (var parameter in _parameters.Mandatory) {
                if (!(parameter is LocalVariable) && !(parameter is Placeholder)) {
                    return false;
                }
            }
            foreach (var statement in _body) {
                if (!IsScopeFree(statement)) {
                    return false;
                }
            }
            return true;
        }

        private static bool IsScopeFree(Expression/*!*/ node) {
            if (node is LocalVariable || node is Literal || node is SelfReference) {
                return true;
            }
            var assignment = node as SimpleAssignmentExpression;
            return assignment != null && assignment.Operation == null && assignment.Left is LocalVariable && IsScopeFree(assignment.Right);
        }

        /// <summary>
        /// Points the TracePoint hooks of a scope-free body at the scope made on demand. Any other use
        /// of the scope, or a node it cannot look into, means the body needs its scope after all.
        /// </summary>
        private sealed class LazyScopeRewriter : MSA.ExpressionVisitor {
            private readonly MSA.ParameterExpression/*!*/ _scope;
            private readonly MSA.Expression/*!*/ _lazyScope;
            private int _inTraceHook;

            internal bool NeedsScope;

            internal LazyScopeRewriter(MSA.ParameterExpression/*!*/ scope, MSA.Expression/*!*/ lazyScope) {
                _scope = scope;
                _lazyScope = lazyScope;
            }

            protected override MSA.Expression/*!*/ VisitParameter(MSA.ParameterExpression/*!*/ node) {
                if (node == _scope) {
                    if (_inTraceHook > 0) {
                        return _lazyScope;
                    }
                    NeedsScope = true;
                }
                return node;
            }

            protected override MSA.Expression/*!*/ VisitExtension(MSA.Expression/*!*/ node) {
                if (node is TraceLineExpression) {
                    _inTraceHook++;
                    try {
                        return base.VisitExtension(node);
                    } finally {
                        _inTraceHook--;
                    }
                }
                NeedsScope = true;
                return node;
            }
        }

        private MSA.Expression/*!*/ GetParameterAccess(AstParameters/*!*/ parameters, MSA.Expression paramsArray, int i) {
            return (paramsArray != null) ? (MSA.Expression)Ast.ArrayAccess(paramsArray, AstUtils.Constant(i)) : parameters[HiddenParameterCount + i];
        }

        private MSA.Expression/*!*/ MakeParametersInitialization(AstGenerator/*!*/ gen, AstParameters/*!*/ parameters) {
            var result = new AstExpressions(
                ParameterCount + 
                (_parameters.Optional.Length > 0 ? 1 : 0) + 
                (_parameters.Unsplat != null ? 1 : 0) +
                (_parameters.Block != null ? 1 : 0) +
                1
             );

            // TODO: we can skip parameters that are locals (need to be defined as parameters, not as #n):

            var paramsArray = HasFormalParametersInArray ? parameters[HiddenParameterCount] : null;

            int parameterIndex = 0;
            for (int i = 0; i < _parameters.Mandatory.Length; i++) {
                result.Add(_parameters.Mandatory[i].TransformWrite(gen, GetParameterAccess(parameters, paramsArray, parameterIndex)));
                parameterIndex++;
            }

            if (_parameters.Optional.Length > 0) {
                for (int i = 0; i < _parameters.Optional.Length; i++) {
                    result.Add(_parameters.Optional[i].Left.TransformWrite(gen, GetParameterAccess(parameters, paramsArray, parameterIndex)));
                    parameterIndex++;
                }
                result.Add(_parameters.TransformOptionalsInitialization(gen));
            }

            if (_parameters.Unsplat != null) {
                // the last parameter is unsplat:
                result.Add(_parameters.Unsplat.TransformWrite(gen, parameters[parameters.Count - (_parameters.Block != null ? 2 : 1)]));
            }

            if (_parameters.Block != null) {
                result.Add(_parameters.Block.TransformWrite(gen, parameters[parameters.Count - 1]));
                parameterIndex++;
            }

            result.Add(AstUtils.Empty());
            return Ast.Block(result);
        }
    }
}

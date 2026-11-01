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
using System.Dynamic;
using Microsoft.Scripting;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    /// <summary>
    /// super(args)
    /// super
    /// 
    /// The former case passes the arguments explicitly.
    /// The latter passes all of the arguments that were passed to the current method (including the block, if any)
    /// 
    /// Also works from a method defined using define_method.
    /// </summary>
    public partial class SuperCall : CallExpression {
        // True for `super` without an argument list, even when the front end synthesized one
        // from the parameters (which it does for a signature with keyword parameters).
        private readonly bool _argumentsAreImplicit;

        /// <summary>
        /// All non-block arguments are passed implicitly and this node has to read them off the
        /// enclosing method's parameter list itself.
        /// </summary>
        public bool HasImplicitArguments {
            get { return Arguments == null; }
        }

        public SuperCall(Arguments args, Block block, SourceSpan location)
            : this(args, block, location, args == null) {
        }

        public SuperCall(Arguments args, Block block, SourceSpan location, bool argumentsAreImplicit)
            : base(args, block, location) {
            _argumentsAreImplicit = argumentsAreImplicit;
        }

        internal override MSA.Expression/*!*/ TransformRead(AstGenerator/*!*/ gen) {
            // variable assigned to the transformed block in MakeCallWithBlockRetryable:
            MSA.Expression blockArgVariable = gen.CurrentScope.DefineHiddenVariable("#super-call-block", typeof(Proc));

            MSA.Expression invoke;
            KeywordArgumentSplat keywordSplat = TrailingKeywordSplat();
            if (keywordSplat != null) {
                invoke = TransformKeywordSplatCall(gen, blockArgVariable, keywordSplat);
            } else {
                invoke = TransformCall(gen, blockArgVariable);
            }

            // block:
            MSA.Expression transformedBlock;
            if (Block != null) {
                transformedBlock = Block.Transform(gen);
            } else {
                transformedBlock = gen.MakeMethodBlockParameterRead();
            }
            
            return gen.DebugMark(
                MethodCall.MakeCallWithBlockRetryable(gen,
                    invoke,
                    blockArgVariable, 
                    transformedBlock,
                    Block != null && Block.IsDefinition
                ),
                "#RB: super call ('" + gen.CurrentMethod.MethodName + "')"
            );
        }

        private MSA.Expression/*!*/ TransformCall(AstGenerator/*!*/ gen, MSA.Expression/*!*/ blockArgVariable) {
            // invoke super member action:
            var siteBuilder = new CallSiteBuilder(gen, gen.CurrentSelfVariable, blockArgVariable);

            // arguments:
            if (HasImplicitArguments) {
                // MRI 1.8: If a block is called via define_method stub its parameters are used here.
                // MRI 1.9: This scenario is not supported.
                // We don't support this either. Otherwise we would need to emit super call with dynamic parameters if gen.CurrentBlock != null.

                if (gen.CurrentMethod.Parameters != null) {
                    gen.CurrentMethod.Parameters.TransformForSuperCall(gen, siteBuilder);
                } else if (gen.CompilerOptions.TopLevelParameterNames != null) {
                    bool hasUnsplat = gen.CompilerOptions.TopLevelHasUnsplatParameter;
                    string[] names = gen.CompilerOptions.TopLevelParameterNames;

                    // dynamic lookup:
                    for (int i = 0; i < names.Length - (hasUnsplat ? 1 : 0); i++) {
                        siteBuilder.Add(Methods.GetLocalVariable.OpCall(gen.CurrentScopeVariable, AstUtils.Constant(names[i])));
                    }
                    if (hasUnsplat) {
                        siteBuilder.SplattedArgument =
                            Methods.GetLocalVariable.OpCall(gen.CurrentScopeVariable, AstUtils.Constant(names[names.Length - 1]));
                    }
                } else {
                    // this means we are not inside any method scope -> an exception will be thrown at the call site
                }
            } else {
                Arguments.TransformToCall(gen, siteBuilder);
            }

            return siteBuilder.MakeSuperCallAction(gen.CurrentFrame.UniqueId, _argumentsAreImplicit);
        }

        /// <summary>
        /// The `**h` the argument list ends with, if it does: `super(a, **h)`, and the parameterless
        /// `super` of a method with **rest (PrismAstBridge.ZSuperArguments).
        /// </summary>
        private KeywordArgumentSplat TrailingKeywordSplat() {
            if (HasImplicitArguments) {
                return null;
            }
            var expressions = Arguments.Expressions;
            return expressions.Length > 0 ? expressions[expressions.Length - 1] as KeywordArgumentSplat : null;
        }

        /// <summary>
        /// `super(a, **h)`: h passes nothing at all when it is empty, and a copy of itself otherwise,
        /// which the splatted call site of an ordinary call does with a one-or-zero element array
        /// (see KeywordArgumentSplat) - one more allocation, and a splatted call, per call. Here the
        /// hash is computed first and one of two plain call sites takes it:
        ///
        ///   #a0 = a ; #kw = &lt;h, copied, or null&gt;
        ///   #kw == null ? super(#a0) : super(#a0, #kw)
        ///
        /// The positional arguments are evaluated into temporaries before h, in source order. A
        /// parameterless `super`'s are reads of the method's own locals, which h cannot change, so
        /// those are read where they are passed.
        /// </summary>
        private MSA.Expression/*!*/ TransformKeywordSplatCall(AstGenerator/*!*/ gen, MSA.Expression/*!*/ blockArgVariable,
            KeywordArgumentSplat/*!*/ keywordSplat) {

            var expressions = Arguments.Expressions;
            int count = expressions.Length - 1;
            var temps = new List<MSA.ParameterExpression>();
            var body = new List<MSA.Expression>();
            var values = new MSA.Expression[count];
            var splatted = new bool[count];
            for (int i = 0; i < count; i++) {
                var expression = expressions[i];
                splatted[i] = expression is SplattedArgument;
                if (splatted[i]) {
                    var temp = Ast.Variable(typeof(IList), "#super-splat" + i);
                    temps.Add(temp);
                    body.Add(Ast.Assign(temp, AstUtils.Convert(expression.TransformRead(gen), typeof(IList))));
                    values[i] = temp;
                } else if (_argumentsAreImplicit && expression is LocalVariable) {
                    values[i] = AstUtils.Box(expression.TransformRead(gen));
                } else {
                    var temp = Ast.Variable(typeof(object), "#super-arg" + i);
                    temps.Add(temp);
                    body.Add(Ast.Assign(temp, AstUtils.Box(expression.TransformRead(gen))));
                    values[i] = temp;
                }
            }

            return MakeKeywordsCall(gen, blockArgVariable, temps, body, values, splatted, keywordSplat.TransformToArgument(gen));
        }

        /// <summary>
        /// <paramref name="body"/>, then `#kw = keywordsValue` and `#kw == null ? super(values) : super(values, #kw)`.
        /// </summary>
        private MSA.Expression/*!*/ MakeKeywordsCall(AstGenerator/*!*/ gen, MSA.Expression/*!*/ blockArgVariable,
            List<MSA.ParameterExpression>/*!*/ temps, List<MSA.Expression>/*!*/ body, MSA.Expression/*!*/[]/*!*/ values,
            bool[]/*!*/ splatted, MSA.Expression/*!*/ keywordsValue) {

            var keywords = Ast.Variable(typeof(Hash), "#super-keywords");
            temps.Add(keywords);
            body.Add(Ast.Assign(keywords, keywordsValue));

            var without = new CallSiteBuilder(gen, gen.CurrentSelfVariable, blockArgVariable);
            AddArguments(without, values, splatted, null);
            var with = new CallSiteBuilder(gen, gen.CurrentSelfVariable, blockArgVariable);
            AddArguments(with, values, splatted, keywords);

            body.Add(Ast.Condition(
                Ast.ReferenceEqual(keywords, AstUtils.Constant(null)),
                AstUtils.Convert(without.MakeSuperCallAction(gen.CurrentFrame.UniqueId, _argumentsAreImplicit), typeof(object)),
                AstUtils.Convert(with.MakeSuperCallAction(gen.CurrentFrame.UniqueId, _argumentsAreImplicit), typeof(object)),
                typeof(object)
            ));
            return Ast.Block(typeof(object), temps, body);
        }

        /// <summary>
        /// Positional <paramref name="values"/>, then <paramref name="keywords"/> if not null. A call
        /// site splats at the end of its argument list only, so from the first splatted value on they
        /// all go into one array, as Arguments does it.
        /// </summary>
        private static void AddArguments(CallSiteBuilder/*!*/ siteBuilder, MSA.Expression/*!*/[]/*!*/ values, bool[]/*!*/ splatted,
            MSA.Expression keywords) {

            int first = System.Array.IndexOf(splatted, true);
            if (first < 0) {
                foreach (var value in values) {
                    siteBuilder.Add(value);
                }
                if (keywords != null) {
                    siteBuilder.Add(keywords);
                }
                return;
            }

            for (int i = 0; i < first; i++) {
                siteBuilder.Add(values[i]);
            }
            if (first == values.Length - 1) {
                // `super(a, *rest)`, `super(a, *rest, **kw)`: the usual shape
                siteBuilder.SplattedArgument = (keywords == null) ? values[first]
                    : Methods.SplatAppendItem.OpCall(values[first], AstUtils.Convert(keywords, typeof(object)));
                return;
            }

            MSA.Expression array = Methods.MakeArray0.OpCall();
            for (int i = first; i < values.Length; i++) {
                array = splatted[i]
                    ? Methods.AddRange.OpCall(array, values[i])
                    : Methods.AddItem.OpCall(array, values[i]);
            }
            if (keywords != null) {
                array = Methods.AddItem.OpCall(array, AstUtils.Convert(keywords, typeof(object)));
            }
            siteBuilder.SplattedArgument = array;
        }

        internal override MSA.Expression/*!*/ TransformDefinedCondition(AstGenerator/*!*/ gen) {
            // MRI doesn't evaluate the arguments 
            return AstUtils.LightDynamic(
                SuperCallAction.Make(gen.Context, RubyCallSignature.IsDefined(true), gen.CurrentFrame.UniqueId),
                typeof(bool),
                gen.CurrentScopeVariable,
                gen.CurrentSelfVariable
            );
        }

        internal override string/*!*/ GetNodeName(AstGenerator/*!*/ gen) {
            return "super";
        }
    }
}

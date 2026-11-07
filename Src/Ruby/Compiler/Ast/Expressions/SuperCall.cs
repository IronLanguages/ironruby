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

using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
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

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            // variable assigned to the transformed block in MakeCallWithBlockRetryable:
            MSA.Expression blockArgVariable = gen.CurrentScope.DefineHiddenVariable("#super-call-block", typeof(Proc));

            MSA.Expression invoke;
            Arguments arguments = Arguments ?? EvalImplicitArguments(gen);
            KeywordArgumentSplat keywordSplat = TrailingKeywordSplat(arguments);
            if (keywordSplat != null) {
                invoke = TransformKeywordSplatCall(gen, blockArgVariable, arguments, keywordSplat);
            } else {
                invoke = TransformCall(gen, blockArgVariable, arguments);
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

        private MSA.Expression/*!*/ TransformCall(AstGenerator/*!*/ gen, MSA.Expression/*!*/ blockArgVariable, Arguments arguments) {
            // invoke super member action:
            var siteBuilder = new CallSiteBuilder(gen, gen.CurrentSelfVariable, blockArgVariable);

            // arguments:
            if (arguments == null) {
                // The parameters of the enclosing def. A block's are never passed: in a block that
                // define_method made a method of, the super call site raises (as MRI does).

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
                arguments.TransformToCall(gen, siteBuilder);
            }

            return siteBuilder.MakeSuperCallAction(gen.CurrentFrame.UniqueId, _argumentsAreImplicit);
        }

        /// <summary>
        /// The `**h` the argument list ends with, if it does: `super(a, **h)`, and the parameterless
        /// `super` of a method with **rest (PrismAstBridge.ZSuperArguments).
        /// </summary>
        private static KeywordArgumentSplat TrailingKeywordSplat(Arguments arguments) {
            if (arguments == null) {
                return null;
            }
            var expressions = arguments.Expressions;
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
            Arguments/*!*/ arguments, KeywordArgumentSplat/*!*/ keywordSplat) {

            var expressions = arguments.Expressions;
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

        #region Parameterless super in eval

        // The names PrismAstBridge gives the hidden locals of a method with keyword parameters: the
        // keywords as they arrived (LowerKeywords, LowerGeneralParameters, RefuseKeywords), the one
        // splat the positionals arrive in when they are not all mandatory (LowerGeneralParameters),
        // a copy of that kept for `super` when they cannot all be read back by name, and an
        // anonymous **.
        private const string KeywordsLocal = "?kw?";
        private const string ArgsLocal = "?args?";
        private const string SuperArgsLocal = "?superargs?";
        private const string AnonymousKeywordRestLocal = "?kwrest?";

        // The declared signatures of the methods with keyword parameters, by the array of local
        // names their scopes are made with, of which each compilation of a method body has its own.
        private static readonly ConditionalWeakTable<string[], RubyParameterSignature> _keywordSignatures =
            new ConditionalWeakTable<string[], RubyParameterSignature>();

        /// <summary>
        /// Records the signature of a method body compiled with these local names if it has keyword
        /// parameters (or `**nil`). Their lowering hides them, and all positionals but mandatory ones,
        /// from the parameter list a parameterless `super` in an eval'd string otherwise reads its
        /// arguments off (RubyMethodScope.GetVisibleParameterNames).
        /// </summary>
        internal static void RecordKeywordSignature(string[] variableNames, RubyParameterSignature signature) {
            if (variableNames != null && signature != null && System.Array.IndexOf(variableNames, KeywordsLocal) >= 0) {
                _keywordSignatures.AddOrUpdate(variableNames, signature);
            }
        }

        /// <summary>
        /// The signature RecordKeywordSignature recorded for the local names of a method scope, or null.
        /// </summary>
        internal static RubyParameterSignature GetKeywordSignature(string/*!*/[]/*!*/ variableNames) {
            RubyParameterSignature signature;
            return _keywordSignatures.TryGetValue(variableNames, out signature) ? signature : null;
        }

        /// <summary>
        /// `eval("super")` in a method with keyword parameters: the arguments a parameterless `super` in
        /// the method itself passes (PrismAstBridge.ZSuperArguments), rebuilt from the declared signature
        /// with each parameter read by name - its current value, as in MRI. Null for any other call.
        /// </summary>
        private Arguments EvalImplicitArguments(AstGenerator/*!*/ gen) {
            RubyParameterSignature signature;
            string[] visibleParameters;
            if (gen.CurrentMethod.Parameters != null ||
                (signature = gen.CompilerOptions.TopLevelParameterSignature) == null ||
                (visibleParameters = gen.CompilerOptions.TopLevelParameterNames) == null) {
                return null;
            }

            var span = Location;
            var arguments = new List<Expression>();
            bool positionalsSplatted = visibleParameters.Length == 1 && visibleParameters[0] == ArgsLocal;
            if (!positionalsSplatted) {
                // mandatory positionals, then ?kw?: these read back by name, destructured ones included
                for (int i = 0; i < visibleParameters.Length - 1; i++) {
                    arguments.Add(ReadLocal(visibleParameters[i], span));
                }
            } else if (gen.CompilerOptions.LocalNames != null && gen.CompilerOptions.LocalNames.Contains(SuperArgsLocal)) {
                // the arguments as the method was called with them, keywords included
                arguments.Add(new SplattedArgument(ReadLocal(SuperArgsLocal, span)));
                return new Arguments(arguments.ToArray());
            }

            var keywords = new List<Maplet>();
            Expression keywordRest = null;
            foreach (var parameter in signature.Parameters) {
                switch (parameter.Kind) {
                    case "req":
                    case "opt":
                        if (positionalsSplatted) {
                            arguments.Add(ReadLocal(parameter.Name, span));
                        }
                        break;
                    case "rest":
                        arguments.Add(new SplattedArgument(ReadLocal(parameter.Name, span)));
                        break;
                    case "keyreq":
                    case "key":
                        keywords.Add(new Maplet(new SymbolLiteral(parameter.Name, gen.Encoding, span), ReadLocal(parameter.Name, span), span));
                        break;
                    case "keyrest":
                        keywordRest = ReadLocal(parameter.Name == "**" ? AnonymousKeywordRestLocal : parameter.Name, span);
                        break;
                }
            }

            if (keywords.Count > 0) {
                Expression hash = new HashConstructor(keywords.ToArray(), true, span);
                if (keywordRest != null) {
                    // rest first, and it has to be a Hash still (see PrismAstBridge.ZSuperArguments)
                    hash = KeywordSplatMerge.HashOnly(
                        KeywordSplatMerge.HashOnly(new HashConstructor(new Maplet[0], true, span), keywordRest, span), hash, span);
                }
                arguments.Add(hash);
            } else if (keywordRest != null) {
                var converted = new MethodCall(new HashConstructor(new Maplet[0], true, span), "merge",
                    new Arguments(new OrExpression(keywordRest, new HashConstructor(new Maplet[0], span), span)), span);
                arguments.Add(KeywordArgumentSplat.PassingOn(keywordRest, converted, ReadLocal(KeywordsLocal, span)));
            }
            return new Arguments(arguments.ToArray());
        }

        // a local of the method the eval runs in, looked up by name
        private static LocalVariable/*!*/ ReadLocal(string/*!*/ name, SourceSpan span) {
            return new LocalVariable(name, span, -1);
        }

        #endregion

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

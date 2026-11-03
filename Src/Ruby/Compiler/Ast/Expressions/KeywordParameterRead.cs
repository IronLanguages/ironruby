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

using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    /// <summary>
    /// The parts of a keyword-parameter prologue (PrismAstBridge.BindKeywordsFrom) that read the
    /// hash a keyword call passed: whether it matches the declared keywords, the value of one
    /// keyword parameter, and the **rest parameter. Each one is a single runtime call (see
    /// <see cref="IronRuby.Runtime.RubyOps.KeywordArgumentsMatch"/>) where the prologue used to
    /// make a handful of dynamic Hash calls per keyword.
    /// </summary>
    public partial class KeywordParameterRead : Expression {
        private enum ReadKind {
            Match,
            Value,
            Rest
        }

        private readonly ReadKind _kind;
        // the hidden local holding the keyword hash
        private readonly Expression/*!*/ _keywords;
        private readonly string/*!*/[]/*!*/ _names;
        private readonly RubyEncoding/*!*/ _encoding;
        // Match: how many of the names are required; Value: where the keyword is declared
        private readonly int _requiredCount;
        private readonly bool _acceptsRest;
        private readonly Expression _defaultValue;

        public Expression/*!*/ Keywords {
            get { return _keywords; }
        }

        public Expression DefaultValue {
            get { return _defaultValue; }
        }

        // What the method JIT (Runtime/Jit/JitKeywords.cs) needs to recognize a keyword prologue.
        internal bool IsMatch { get { return _kind == ReadKind.Match; } }
        internal bool IsValue { get { return _kind == ReadKind.Value; } }
        internal string/*!*/[]/*!*/ Names { get { return _names; } }
        internal RubyEncoding/*!*/ Encoding { get { return _encoding; } }
        // Match: how many of the names are required; Value: where the keyword is declared
        internal int RequiredCountOrPosition { get { return _requiredCount; } }
        internal bool AcceptsRest { get { return _acceptsRest; } }

        private KeywordParameterRead(ReadKind kind, Expression/*!*/ keywords, string/*!*/[]/*!*/ names, RubyEncoding/*!*/ encoding,
            int requiredCount, bool acceptsRest, Expression defaultValue, SourceSpan location)
            : base(location) {
            Assert.NotNull(keywords, names, encoding);
            _kind = kind;
            _keywords = keywords;
            _names = names;
            _encoding = encoding;
            _requiredCount = requiredCount;
            _acceptsRest = acceptsRest;
            _defaultValue = defaultValue;
        }

        /// <summary>
        /// True when the hash holds the first <paramref name="requiredCount"/> of
        /// <paramref name="names"/> and, unless <paramref name="acceptsRest"/>, no other key.
        /// </summary>
        public static KeywordParameterRead/*!*/ Match(Expression/*!*/ keywords, string/*!*/[]/*!*/ names, int requiredCount,
            bool acceptsRest, RubyEncoding/*!*/ encoding, SourceSpan location) {
            return new KeywordParameterRead(ReadKind.Match, keywords, names, encoding, requiredCount, acceptsRest, null, location);
        }

        /// <summary>
        /// The value the call passed for keyword <paramref name="name"/>, the method's keyword number
        /// <paramref name="position"/>. If it passed none, <paramref name="defaultValue"/> is evaluated
        /// instead, or nil if there is none.
        /// </summary>
        public static KeywordParameterRead/*!*/ Value(Expression/*!*/ keywords, string/*!*/ name, int position, Expression defaultValue,
            RubyEncoding/*!*/ encoding, SourceSpan location) {
            return new KeywordParameterRead(ReadKind.Value, keywords, new[] { name }, encoding, position, false, defaultValue, location);
        }

        /// <summary>
        /// A copy of the hash without the declared <paramref name="names"/>: the **rest parameter.
        /// </summary>
        public static KeywordParameterRead/*!*/ Rest(Expression/*!*/ keywords, string/*!*/[]/*!*/ names,
            RubyEncoding/*!*/ encoding, SourceSpan location) {
            return new KeywordParameterRead(ReadKind.Rest, keywords, names, encoding, 0, true, null, location);
        }

        private RubySymbol/*!*/[]/*!*/ GetSymbols(AstGenerator/*!*/ gen) {
            var result = new RubySymbol[_names.Length];
            for (int i = 0; i < result.Length; i++) {
                result[i] = gen.Context.CreateSymbol(_names[i], _encoding);
            }
            return result;
        }

        private MSA.Expression/*!*/ TransformMatch(AstGenerator/*!*/ gen) {
            return Methods.KeywordArgumentsMatch.OpCall(
                AstUtils.Box(_keywords.TransformRead(gen)),
                AstUtils.Constant(GetSymbols(gen)),
                AstUtils.Constant(_requiredCount),
                AstUtils.Constant(_acceptsRest)
            );
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            switch (_kind) {
                case ReadKind.Match:
                    return AstUtils.Box(TransformMatch(gen));

                case ReadKind.Rest:
                    return Methods.MakeKeywordRest.OpCall(AstUtils.Box(_keywords.TransformRead(gen)), AstUtils.Constant(GetSymbols(gen)));

                default:
                    // value = GetKeywordArgument(kw, :name); value == DefaultArgument ? <default> : value
                    var value = gen.CurrentScope.DefineHiddenVariable("#keyword", typeof(object));
                    return Ast.Condition(
                        Ast.Equal(
                            Ast.Assign(value, Methods.GetKeywordArgument.OpCall(
                                AstUtils.Box(_keywords.TransformRead(gen)),
                                AstUtils.Constant(gen.Context.CreateSymbol(_names[0], _encoding)),
                                AstUtils.Constant(_requiredCount)
                            )),
                            Ast.Field(null, Fields.DefaultArgument)
                        ),
                        (_defaultValue != null) ? AstUtils.Box(_defaultValue.TransformRead(gen)) : AstUtils.Constant(null, typeof(object)),
                        value
                    );
            }
        }

        internal override MSA.Expression/*!*/ TransformReadBoolean(AstGenerator/*!*/ gen, bool positive) {
            if (_kind == ReadKind.Match) {
                var match = TransformMatch(gen);
                return positive ? match : Ast.Not(match);
            }
            return base.TransformReadBoolean(gen, positive);
        }
    }
}

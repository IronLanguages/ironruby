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

using System.Collections.Generic;
using IronRuby.Builtins;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    /// <summary>
    /// A call-site keyword-argument hash that was written with at least one `**` splat, e.g.
    /// `f(1, **opts)` or `f(**a, **b)`.
    ///
    /// IronRuby models keyword arguments as a trailing positional Hash, which is Ruby 2
    /// behaviour and is right for `f(x: 1)`.  It is wrong for `**`: Ruby 3 drops the argument
    /// altogether when the splatted hash turns out to be empty, so `f(1, **{})` calls `f(1)`.
    /// Emptiness is only known at run time, so this produces a one-or-zero element array in
    /// place of the usual `to_a` splat conversion and lets the ordinary splatting machinery
    /// in <see cref="Arguments"/> flatten it away.
    ///
    /// A plain `f(1, {})` is *not* built this way and keeps passing the hash.
    ///
    /// Deriving from <see cref="SplattedArgument"/> (rather than wrapping one) is what makes
    /// Arguments.IndexOfSplatted see it, so no new NodeTypes entry is needed.
    /// </summary>
    public partial class KeywordArgumentSplat : SplattedArgument {
        // `f(**h)` alone: Argument stores h in a temporary, and this is the hash to splat instead
        // when h is neither a Hash nor nil - `{}.merge(temporary)`, which applies #to_hash.
        private readonly Expression _converted;

        // The hidden keyword parameter of the method whose parameterless `super' this splats the
        // **rest of (see PassingOn).
        private readonly LocalVariable _keywords;

        public KeywordArgumentSplat(Expression/*!*/ hash)
            : base(hash) {
        }

        /// <summary>
        /// `f(**h)`: <paramref name="hash"/> evaluates h (and keeps it for <paramref name="converted"/>),
        /// which is splatted as it is when it is a Hash or nil - a copy is made either way - and
        /// through <paramref name="converted"/> otherwise. Building `{}.merge(h)` for every call only
        /// to copy the result again cost a Ruby-level Hash#merge per call.
        /// </summary>
        public KeywordArgumentSplat(Expression/*!*/ hash, Expression/*!*/ converted)
            : base(hash) {
            _converted = converted;
        }

        private KeywordArgumentSplat(Expression/*!*/ hash, Expression/*!*/ converted, LocalVariable/*!*/ keywords)
            : this(hash, converted) {
            _keywords = keywords;
        }

        /// <summary>
        /// The `**rest' of `super(a, **rest)', which a parameterless `super' in `def m(a, **rest)' is
        /// (PrismAstBridge.ZSuperArguments): while rest holds exactly what the call passed, the hash the
        /// call passed it in - <paramref name="keywords"/>, the hidden parameter - goes to the parent
        /// instead of a copy (RubyOps.PassOnKeywordRest), as the forwarded hidden parameter always did.
        /// The method lets go of it once it has passed it on: the parameter is set to rest, so that
        /// another `super' (a loop, a second one), or an eval'd one, passes a copy of rest instead.
        /// </summary>
        internal static KeywordArgumentSplat/*!*/ PassingOn(Expression/*!*/ hash, Expression/*!*/ converted, LocalVariable/*!*/ keywords) {
            return new KeywordArgumentSplat(hash, converted, keywords);
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            if (_converted != null) {
                return MSA.Expression.Coalesce(
                    Methods.SplatKeywordHashIfHash.OpCall(gen.CurrentScopeVariable, AstUtils.Box(Argument.TransformRead(gen))),
                    Methods.SplatKeywordHash.OpCall(gen.CurrentScopeVariable, AstUtils.Box(_converted.TransformRead(gen)))
                );
            }
            return Methods.SplatKeywordHash.OpCall(gen.CurrentScopeVariable, AstUtils.Box(Argument.TransformRead(gen)));
        }

        /// <summary>
        /// The hash this splat passes as the one argument it is, typed <see cref="Hash"/>: null when it
        /// passes nothing at all. For a call that is compiled as two plain call sites, one with the
        /// hash and one without (SuperCall), instead of one splatting what TransformRead makes.
        /// </summary>
        internal MSA.Expression/*!*/ TransformToArgument(AstGenerator/*!*/ gen) {
            if (_keywords != null) {
                var rest = Ast.Variable(typeof(object), "#kwrest");
                var given = Ast.Variable(typeof(object), "#kwgiven");
                var passed = Ast.Variable(typeof(Hash), "#kwpassed");
                return Ast.Block(typeof(Hash), new[] { rest, given, passed },
                    Ast.Assign(rest, AstUtils.Box(Argument.TransformRead(gen))),
                    Ast.Assign(given, AstUtils.Box(_keywords.TransformRead(gen))),
                    Ast.Assign(passed, Ast.Condition(
                        Ast.OrElse(
                            Ast.ReferenceEqual(rest, AstUtils.Constant(null)),
                            Ast.TypeIs(rest, typeof(IDictionary<object, object>))
                        ),
                        Methods.PassOnKeywordRest.OpCall(gen.CurrentScopeVariable, rest, given),
                        Methods.KeywordSplatArgument.OpCall(gen.CurrentScopeVariable, AstUtils.Box(_converted.TransformRead(gen))),
                        typeof(Hash)
                    )),
                    Ast.IfThen(
                        Ast.AndAlso(
                            Ast.ReferenceNotEqual(passed, AstUtils.Constant(null)),
                            Ast.ReferenceEqual(passed, given)
                        ),
                        _keywords.TransformWrite(gen, rest)
                    ),
                    passed
                );
            }
            if (_converted != null) {
                var value = Ast.Variable(typeof(object), "#kwsplat");
                return Ast.Block(typeof(Hash), new[] { value },
                    Ast.Assign(value, AstUtils.Box(Argument.TransformRead(gen))),
                    Ast.Condition(
                        Ast.OrElse(
                            Ast.ReferenceEqual(value, AstUtils.Constant(null)),
                            Ast.TypeIs(value, typeof(IDictionary<object, object>))
                        ),
                        Methods.KeywordSplatArgument.OpCall(gen.CurrentScopeVariable, value),
                        Methods.KeywordSplatArgument.OpCall(gen.CurrentScopeVariable, AstUtils.Box(_converted.TransformRead(gen))),
                        typeof(Hash)
                    )
                );
            }
            return Methods.KeywordSplatArgument.OpCall(gen.CurrentScopeVariable, AstUtils.Box(Argument.TransformRead(gen)));
        }
    }
}

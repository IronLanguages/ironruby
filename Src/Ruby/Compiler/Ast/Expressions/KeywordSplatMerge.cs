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

using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    /// <summary>
    /// One `**h` of keyword arguments that also has `k: v` pairs or other splats, e.g. `f(k: v, **h)`:
    /// h's entries go into the keyword hash built so far (see RubyOps.MergeKeywordSplat) when h is
    /// a Hash or nil. Anything else takes <see cref="Converted"/> - `hash.merge(h || {})`, which
    /// applies #to_hash - as every `**h` used to, at the price of a Ruby-level Hash#merge.
    ///
    /// Without Converted (<see cref="HashOnly"/>) h has to be a Hash, and anything else - nil too -
    /// raises TypeError, as MRI's parameterless super does for `def m(k: 1, **rest)` once the body
    /// has set rest to something else.
    /// </summary>
    public partial class KeywordSplatMerge : Expression {
        // `temp = <hash so far>` and `temp2 = <h>`, the temporaries Converted reads
        private readonly Expression/*!*/ _hash;
        private readonly Expression/*!*/ _splatted;
        private readonly Expression _converted;

        public Expression/*!*/ Hash {
            get { return _hash; }
        }

        public Expression/*!*/ Splatted {
            get { return _splatted; }
        }

        /// <summary>Null for <see cref="HashOnly"/>.</summary>
        public Expression Converted {
            get { return _converted; }
        }

        public KeywordSplatMerge(Expression/*!*/ hash, Expression/*!*/ splatted, Expression/*!*/ converted, SourceSpan location)
            : base(location) {
            Assert.NotNull(hash, splatted, converted);
            _hash = hash;
            _splatted = splatted;
            _converted = converted;
        }

        private KeywordSplatMerge(Expression/*!*/ hash, Expression/*!*/ splatted, SourceSpan location)
            : base(location) {
            Assert.NotNull(hash, splatted);
            _hash = hash;
            _splatted = splatted;
        }

        /// <summary>
        /// <paramref name="splatted"/>'s entries into <paramref name="hash"/>, the keyword hash being built,
        /// where splatted must be a Hash: neither is read twice, so neither needs a temporary.
        /// </summary>
        internal static KeywordSplatMerge/*!*/ HashOnly(Expression/*!*/ hash, Expression/*!*/ splatted, SourceSpan location) {
            return new KeywordSplatMerge(hash, splatted, location);
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            if (_converted == null) {
                return Methods.MergeKeywordHash.OpCall(gen.CurrentScopeVariable,
                    AstUtils.Box(_hash.TransformRead(gen)), AstUtils.Box(_splatted.TransformRead(gen)));
            }
            return Ast.Coalesce(
                Methods.MergeKeywordSplat.OpCall(gen.CurrentScopeVariable,
                    AstUtils.Box(_hash.TransformRead(gen)), AstUtils.Box(_splatted.TransformRead(gen))),
                AstUtils.Box(_converted.TransformRead(gen))
            );
        }
    }
}

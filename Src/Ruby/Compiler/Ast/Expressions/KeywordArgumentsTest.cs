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
    /// <summary>
    /// True iff the value is the trailing hash a call site built out of keyword syntax, as
    /// opposed to an ordinary Hash that was passed positionally. Keyword arguments have no slot
    /// of their own in this calling convention, so the parameter-binding prologue of a method
    /// with keyword parameters uses this to tell `m(a: 1)` from `m({a: 1})`.
    /// </summary>
    public partial class KeywordArgumentsTest : Expression {
        private readonly Expression/*!*/ _value;

        public Expression/*!*/ Value {
            get { return _value; }
        }

        public KeywordArgumentsTest(Expression/*!*/ value, SourceSpan location)
            : base(location) {
            Assert.NotNull(value);
            _value = value;
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            return AstUtils.Box(Methods.IsKeywordArgumentsHash.OpCall(AstUtils.Box(_value.TransformRead(gen))));
        }

        // The test is only ever a condition (the separation checks of a keyword prologue, which run on
        // every call): the Boolean as it is, rather than boxed into a new object for IsTrue to unbox.
        internal override MSA.Expression/*!*/ TransformReadBoolean(AstGenerator/*!*/ gen, bool positive) {
            var test = Methods.IsKeywordArgumentsHash.OpCall(AstUtils.Box(_value.TransformRead(gen)));
            return positive ? test : MSA.Expression.Not(test);
        }
    }
}

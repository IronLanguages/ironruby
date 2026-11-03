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

namespace IronRuby.Compiler.Ast {
    /// <summary>
    /// Binds a destructured method parameter: `def m((a, b))` is compiled as a plain hidden
    /// parameter plus this, which spreads the argument over the compound l-value.
    ///
    /// It is not a <see cref="ParallelAssignmentExpression"/> with a single r-value, because
    /// `(a) = x` there means `a = [x]` (the shape a one-element parenthesized l-value has when
    /// it is written at the top level of a multiple assignment). A parameter always spreads,
    /// so `def m((a)); a; end` called with [1, 2] binds a to 1 - the same semantics a nested
    /// compound l-value has, which is what the base l-value write implements.
    /// </summary>
    public partial class DestructuringAssignmentExpression : ParallelAssignmentExpression {
        public DestructuringAssignmentExpression(CompoundLeftValue/*!*/ lhs, Expression/*!*/ value, SourceSpan location)
            : base(lhs, new Expression[] { value }, location) {
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            return Left.TransformWrite(gen, null, Right[0].TransformRead(gen));
        }
    }
}

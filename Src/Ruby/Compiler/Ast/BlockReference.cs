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
using IronRuby.Builtins;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    
    public partial class BlockReference : Block {
        private readonly Expression/*!*/ _expression;

        public sealed override bool IsDefinition { get { return false; } }

        public Expression/*!*/ Expression {
            get { return _expression; }
        }

        public BlockReference(Expression/*!*/ expression, SourceSpan location)
            : base(location) {
            Assert.NotNull(expression);

            _expression = expression;
        }

        internal override MSA.Expression/*!*/ Transform(AstGenerator/*!*/ gen) {
            Assert.NotNull(gen);
            // &:sym and a refined #to_proc have to see the refinements active here, which the conversion site
            // cannot - see RubyOps.TryConvertBlockWithRefinements
            var value = Ast.Variable(typeof(object), "#block-arg");
            return Ast.Block(new[] { value },
                Ast.Assign(value, AstUtils.Box(_expression.TransformRead(gen))),
                Ast.Coalesce(
                    Methods.TryConvertBlockWithRefinements.OpCall(gen.CurrentScopeVariable, value),
                    AstUtils.LightDynamic(ConvertToProcAction.Make(gen.Context), typeof(Proc), value)
                )
            );
        }
    }
}

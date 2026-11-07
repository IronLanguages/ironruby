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

    public partial class AttributeAccess : LeftValue {
        // qualifier::name =

        private Expression/*!*/ _qualifier;
        private string/*!*/ _name;

        public Expression/*!*/ Qualifier {
            get { return _qualifier; }
        }

        public string/*!*/ Name {
            get { return _name; }
        }

        public AttributeAccess(Expression/*!*/ qualifier, string/*!*/ name, SourceSpan location)
            : base(location) {
            Assert.NotNull(qualifier, name);

            _name = name + "=";
            _qualifier = qualifier;
        }

        internal override string/*!*/ GetNodeName(AstGenerator/*!*/ gen) {
            return "assignment";
        }

        internal override MSA.Expression TransformTargetRead(AstGenerator/*!*/ gen) {
            return _qualifier.TransformRead(gen);
        }

        internal override MSA.Expression/*!*/ TransformRead(AstGenerator/*!*/ gen, MSA.Expression targetValue, bool tryRead) {
            throw Assert.Unreachable;
        }

        /// <summary>
        /// `recv&amp;.name = value` as a target (rescue =&gt; recv&amp;.name, for recv&amp;.name in ...):
        /// nothing is assigned when recv is nil.
        /// </summary>
        public bool IsSafeNavigation { get; set; }

        internal override MSA.Expression/*!*/ TransformWrite(AstGenerator/*!*/ gen, MSA.Expression/*!*/ targetValue, MSA.Expression/*!*/ rightValue) {
            Assert.NotNull(gen, targetValue, rightValue);
            if (IsSafeNavigation) {
                var target = gen.CurrentScope.DefineHiddenVariable("#safe-target", typeof(object));
                return Ast.Condition(
                    Ast.Equal(Ast.Assign(target, AstUtils.Box(targetValue)), AstUtils.Constant(null)),
                    AstUtils.Constant(null, typeof(object)),
                    AstUtils.Box(MethodCall.TransformRead(this, gen, false, _name, target, null, null, null, rightValue))
                );
            }
            return MethodCall.TransformRead(this, gen, _qualifier is SelfReference, _name, targetValue, null, null, null, rightValue);
        }
    }
}

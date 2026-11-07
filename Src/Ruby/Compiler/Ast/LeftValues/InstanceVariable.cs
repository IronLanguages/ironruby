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

using System.Diagnostics;
using Microsoft.Scripting;
using IronRuby.Runtime;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {

    public partial class InstanceVariable : Variable {
        public InstanceVariable(string/*!*/ name, SourceSpan location)
            : base(name, location) {
            Debug.Assert(name.StartsWith("@") || name.StartsWith(RubyUtils.GetHiddenInstanceVariableName("@"), System.StringComparison.Ordinal));
        }

        internal override MSA.Expression/*!*/ TransformReadVariable(AstGenerator/*!*/ gen, bool tryRead) {
            // each read and each write gets an inline cache of its own (IronRuby.Runtime.InstanceVariableSite)
            return Methods.GetInstanceVariable.OpCall(gen.CurrentScopeVariable, gen.CurrentSelfVariable, AstUtils.Constant(new InstanceVariableSite(Name, gen.IsCoreLibrary)));
        }

        internal override MSA.Expression/*!*/ TransformWriteVariable(AstGenerator/*!*/ gen, MSA.Expression/*!*/ rightValue) {
            return Methods.SetInstanceVariable.OpCall(gen.CurrentSelfVariable, AstUtils.Box(rightValue), gen.CurrentScopeVariable, AstUtils.Constant(new InstanceVariableSite(Name, gen.IsCoreLibrary)));
        }

        internal override MSA.Expression TransformDefinedCondition(AstGenerator/*!*/ gen) {
            return Methods.IsDefinedInstanceVariable.OpCall(gen.CurrentScopeVariable, gen.CurrentSelfVariable, AstUtils.Constant(Name));
        }

        internal override string/*!*/ GetNodeName(AstGenerator/*!*/ gen) {
            return "instance-variable";
        }
    }
}

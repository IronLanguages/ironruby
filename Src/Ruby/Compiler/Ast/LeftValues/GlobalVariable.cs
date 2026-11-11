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

namespace IronRuby.Compiler.Ast {
    using AstUtils = Microsoft.Scripting.Ast.Utils;

    public partial class GlobalVariable : Variable {

        public string/*!*/ FullName {
            get { return "$" + Name; }
        }

        public GlobalVariable(string/*!*/ name, SourceSpan location)
            : base(name, location) {
            Debug.Assert(name.ToString() == "$" || !name.ToString().StartsWith("$"));
        }

        internal override MSA.Expression/*!*/ TransformReadVariable(AstGenerator/*!*/ gen, bool tryRead) {
            if (gen.SavingToDisk) {
                return (tryRead ? Methods.GetGlobalVariable : Methods.ReadGlobalVariable).OpCall(gen.CurrentScopeVariable, TransformName(gen));
            }
            return (tryRead ? Methods.GetCachedGlobalVariable : Methods.ReadCachedGlobalVariable).OpCall(
                gen.CurrentScopeVariable, AstUtils.Constant(new IronRuby.Runtime.GlobalVariableSite(Name, gen.IsCoreLibrary))
            );
        }

        internal override MSA.Expression/*!*/ TransformWriteVariable(AstGenerator/*!*/ gen, MSA.Expression/*!*/ rightValue) {
            if (gen.SavingToDisk) {
                return Methods.SetGlobalVariable.OpCall(AstUtils.Box(rightValue), gen.CurrentScopeVariable, TransformName(gen));
            }
            return Methods.SetCachedGlobalVariable.OpCall(
                AstUtils.Box(rightValue), gen.CurrentScopeVariable, AstUtils.Constant(new IronRuby.Runtime.GlobalVariableSite(Name, gen.IsCoreLibrary))
            );
        }

        internal override MSA.Expression TransformDefinedCondition(AstGenerator/*!*/ gen) {
            return Methods.IsDefinedGlobalVariable.OpCall(gen.CurrentScopeVariable, TransformName(gen));
        }

        internal override string/*!*/ GetNodeName(AstGenerator/*!*/ gen) {
            return "global-variable";
        }
    }
}

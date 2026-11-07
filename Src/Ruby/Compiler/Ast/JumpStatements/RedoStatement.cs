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
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    
    public partial class RedoStatement : JumpStatement {
        public RedoStatement(SourceSpan location)
            : base(null, location) {
        }

        // see Docs/Ruby Language.docx, Runtime/Control Flow Implementation/Redo
        // a class body inside the loop or block has to be left first
        private static MSA.Expression/*!*/ LoopRedo(AstGenerator/*!*/ gen, MSA.Expression value) {
            var module = gen.GetModuleBodyToEscape(true);
            if (module != null) {
                return module.Escape(AstUtils.Constant(null), LoopRedo);
            }
            return Ast.Block(
                Ast.Assign(gen.CurrentLoop.RedoVariable, AstUtils.Constant(true)),
                Ast.Continue(gen.CurrentLoop.ContinueLabel),
                AstUtils.Empty()
            );
        }

        private static MSA.Expression/*!*/ BlockRedo(AstGenerator/*!*/ gen, MSA.Expression value) {
            var module = gen.GetModuleBodyToEscape(false);
            if (module != null) {
                return module.Escape(AstUtils.Constant(null), BlockRedo);
            }
            return Ast.Continue(gen.CurrentBlock.RedoLabel);
        }

        internal override MSA.Expression/*!*/ TransformJump(AstGenerator/*!*/ gen) {

            // eval:
            if (gen.CompilerOptions.IsEval) {
                return Methods.EvalRedo.OpCall(gen.CurrentScopeVariable);
            }

            // loop:
            if (gen.CurrentLoop != null) {
                return LoopRedo(gen, null);
            }

            // block:
            if (gen.CurrentBlock != null) {
                return BlockRedo(gen, null);
            }

            // method:
            return Methods.MethodRedo.OpCall(gen.CurrentScopeVariable);
        }
    }
}

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

using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Scripting.Interpreter;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    /// <summary>
    /// <c>try { body } filter (e: condition) { handler }</c>, the DLR filter IronRuby's exception handling is built of, which the DLR
    /// lowers to a catch of every exception: the condition runs in the catch, after the frames the exception left are gone, and
    /// an exception it turns down is thrown again - a full CLR exception dispatch of its own. The interpreter, which has no
    /// exception filters, runs that (<see cref="Lower"/>).
    ///
    /// Compiled, a real CLR filter, <see cref="_pass"/>, is asked first, in the CLR's first pass: true lets the exception go through
    /// without being caught at all. Otherwise it is caught, and the condition and the handler run as before. The pass expression
    /// must run no Ruby code and must say true only where the catch would have thrown the exception again with nothing to tell
    /// (see Body.TransformExceptionHandling, RubyOps.RescuePassMayTest).
    /// </summary>
    internal sealed class RubyFilterExpression : MSA.Expression, IInstructionProvider {
        private static readonly MethodInfo/*!*/ _Capture = typeof(ExceptionDispatchInfo).GetMethod(nameof(ExceptionDispatchInfo.Capture));
        private static readonly MethodInfo/*!*/ _Throw = typeof(ExceptionDispatchInfo).GetMethod(nameof(ExceptionDispatchInfo.Throw), Type.EmptyTypes);

        private readonly MSA.Expression/*!*/ _body;
        private readonly MSA.ParameterExpression/*!*/ _exception;
        private readonly MSA.Expression/*!*/ _condition;
        private readonly MSA.Expression/*!*/ _handler;
        private readonly MSA.Expression/*!*/ _pass;

        /// <param name="body">The guarded code, of type void.</param>
        /// <param name="exception">The exception variable, of type Exception, which the other expressions read.</param>
        /// <param name="condition">The DLR filter's condition: whether the handler takes the exception.</param>
        /// <param name="handler">The handler, of type void.</param>
        /// <param name="pass">Compiled only, in the CLR's first pass: whether the exception goes through without being caught.</param>
        internal RubyFilterExpression(MSA.Expression/*!*/ body, MSA.ParameterExpression/*!*/ exception, MSA.Expression/*!*/ condition,
            MSA.Expression/*!*/ handler, MSA.Expression/*!*/ pass) {
            _body = body;
            _exception = exception;
            _condition = condition;
            _handler = handler;
            _pass = pass;
        }

        public override MSA.ExpressionType NodeType {
            get { return MSA.ExpressionType.Extension; }
        }

        public override Type/*!*/ Type {
            get { return typeof(void); }
        }

        public override bool CanReduce {
            get { return true; }
        }

        internal MSA.Expression/*!*/ Lower() {
            return AstUtils.Try(_body).Filter(_exception, _condition, _handler);
        }

        public void AddInstructions(LightCompiler/*!*/ compiler) {
            compiler.Compile(Lower());
        }

        //   pending = null;
        //   try {
        //       body
        //   } catch (Exception e) when (!pass) {
        //       if (condition) { handler } else { pending = ExceptionDispatchInfo.Capture(e) }
        //   }
        //   if (pending != null) pending.Throw();
        public override MSA.Expression/*!*/ Reduce() {
            var pending = Ast.Variable(typeof(ExceptionDispatchInfo), "#pending");
            return Ast.Block(new[] { pending },
                Ast.Assign(pending, Ast.Constant(null, typeof(ExceptionDispatchInfo))),
                Ast.TryCatch(
                    _body,
                    Ast.Catch(_exception,
                        Ast.IfThenElse(_condition, _handler, Ast.Block(Ast.Assign(pending, Ast.Call(_Capture, _exception)), Ast.Empty())),
                        Ast.Not(_pass)
                    )
                ),
                Ast.IfThen(
                    Ast.NotEqual(pending, Ast.Constant(null, typeof(ExceptionDispatchInfo))),
                    Ast.Call(pending, _Throw)
                )
            );
        }

        protected override MSA.Expression/*!*/ VisitChildren(MSA.ExpressionVisitor/*!*/ visitor) {
            var body = visitor.Visit(_body);
            var exception = (MSA.ParameterExpression)visitor.Visit(_exception);
            var condition = visitor.Visit(_condition);
            var handler = visitor.Visit(_handler);
            var pass = visitor.Visit(_pass);
            if (body == _body && exception == _exception && condition == _condition && handler == _handler && pass == _pass) {
                return this;
            }
            return new RubyFilterExpression(body, exception, condition, handler, pass);
        }
    }
}

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

using Microsoft.Scripting;
using Microsoft.Scripting.Utils;

namespace IronRuby.Compiler.Ast {

    public abstract class DefinitionExpression : Expression {
        private readonly LexicalScope/*!*/ _definedScope;
        private volatile Body _body;
        // A body the front end builds when it is first asked for (see MethodDefinition), or null.
        private System.Func<Body> _bodyFactory;

        public LexicalScope/*!*/ DefinedScope {
            get {
                return _definedScope;
            }
        }

        public Body/*!*/ Body {
            get {
                return _body ?? BuildBody();
            }
        }

        private Body/*!*/ BuildBody() {
            lock (this) {
                if (_body == null) {
                    var body = _bodyFactory();
                    ContractUtils.RequiresNotNull(body, "body");
                    _body = body;
                    _bodyFactory = null;
                }
                return _body;
            }
        }

        protected DefinitionExpression(LexicalScope/*!*/ definedScope, Body/*!*/ body, SourceSpan location) 
            : base(location) {
            ContractUtils.RequiresNotNull(definedScope, "definedScope");
            ContractUtils.RequiresNotNull(body, "body");

            _definedScope = definedScope;
            _body = body;
        }

        protected DefinitionExpression(LexicalScope/*!*/ definedScope, System.Func<Body>/*!*/ bodyFactory, SourceSpan location)
            : base(location) {
            ContractUtils.RequiresNotNull(definedScope, "definedScope");
            ContractUtils.RequiresNotNull(bodyFactory, "bodyFactory");

            _definedScope = definedScope;
            _bodyFactory = bodyFactory;
        }
    }
}

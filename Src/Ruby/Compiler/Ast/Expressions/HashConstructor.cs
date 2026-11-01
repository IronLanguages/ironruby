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

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = Expression;

    public partial class HashConstructor : Expression {
        // { key1 => value1, key2 => value2, ... }
        private readonly Maplet/*!*/[]/*!*/ _maplets;

        public Maplet/*!*/[]/*!*/ Maplets {
            get { return _maplets; }
        }

        // True for the hash a call site builds out of keyword syntax - `f(a: 1)` - as opposed to
        // a hash literal - `f({a: 1})`. Ruby 3 keeps the two apart, and the flag on the resulting
        // hash is how the callee tells which one it got.
        private readonly bool _isKeywordArguments;

        public bool IsKeywordArguments {
            get { return _isKeywordArguments; }
        }

        // The empty default of a method's or block's hidden keyword parameter (see OmittedKeywords).
        private readonly bool _isOmittedKeywords;

        public HashConstructor(Maplet/*!*/[]/*!*/ maplets, SourceSpan location)
            : this(maplets, false, location) {
        }

        public HashConstructor(Maplet/*!*/[]/*!*/ maplets, bool isKeywordArguments, SourceSpan location)
            : base(location) {
            Assert.NotNullItems(maplets);
            _maplets = maplets;
            _isKeywordArguments = isKeywordArguments;
        }

        private HashConstructor(SourceSpan location)
            : this(new Maplet[0], true, location) {
            _isOmittedKeywords = true;
        }

        /// <summary>
        /// The default of the hidden keyword parameter a method or block with keyword parameters is
        /// lowered to: the call passed no keywords. An empty keyword hash that only that one call of
        /// the method ever holds, which a **rest parameter may therefore take over instead of copying
        /// (RubyOps.MakeOmittedKeywordArguments, RubyOps.MakeKeywordRest).
        /// </summary>
        internal static HashConstructor/*!*/ OmittedKeywords(SourceSpan location) {
            return new HashConstructor(location);
        }

        internal override MSA.Expression/*!*/ TransformRead(AstGenerator/*!*/ gen) {
            if (_isKeywordArguments && _maplets.Length <= 3 && HasSymbolLiteralKeys()) {
                return TransformKeywordArguments(gen);
            }
            var items = gen.TransformMapletsToExpressions(_maplets);
            return _isKeywordArguments ? gen.MakeKeywordArgumentsHashOpCall(items) : gen.MakeHashOpCall(items);
        }

        private bool HasSymbolLiteralKeys() {
            foreach (var maplet in _maplets) {
                if (!(maplet.Key is SymbolLiteral)) {
                    return false;
                }
            }
            return true;
        }

        // `f(a: x, b: y)`: the keyword hash is built by a helper of its own arity (keys and values
        // still evaluated in order), see RubyOps.MakeKeywordArgumentsHash2.
        private MSA.Expression/*!*/ TransformKeywordArguments(AstGenerator/*!*/ gen) {
            var args = new ReadOnlyCollectionBuilder<MSA.Expression>(1 + 2 * _maplets.Length);
            args.Add(gen.CurrentScopeVariable);
            foreach (var maplet in _maplets) {
                args.Add(AstUtils.Box(maplet.Key.TransformRead(gen)));
                args.Add(AstUtils.Box(maplet.Value.TransformRead(gen)));
            }
            switch (_maplets.Length) {
                // the default of the hidden keyword parameter: no keywords passed
                case 0:
                    return _isOmittedKeywords
                        ? Methods.MakeOmittedKeywordArguments.OpCall(gen.CurrentScopeVariable)
                        : Methods.MakeKeywordArgumentsHash0.OpCall(gen.CurrentScopeVariable);
                case 1: return Methods.MakeKeywordArgumentsHash1.OpCall(args);
                case 2: return Methods.MakeKeywordArgumentsHash2.OpCall(args);
                default: return Methods.MakeKeywordArgumentsHash3.OpCall(args);
            }
        }
    }
}

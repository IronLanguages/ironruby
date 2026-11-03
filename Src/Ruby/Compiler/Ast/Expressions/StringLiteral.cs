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

using System.Diagnostics;
using Microsoft.Scripting;
using IronRuby.Builtins;
using System.Runtime.CompilerServices;
using System.Globalization;

namespace IronRuby.Compiler.Ast {
    using AstUtils = Microsoft.Scripting.Ast.Utils;

    /// <summary>
    /// Represents a string literal.
    /// </summary>
    /// <summary>
    /// What a string literal's source said about frozen_string_literal. Chilled is the state of
    /// a file that said nothing: mutable, but a mutation is worth a deprecation warning.
    /// </summary>
    public enum StringLiteralMutability {
        Mutable,
        Chilled,
        Frozen,
    }

    public partial class StringLiteral : Expression {
        // string or byte[]
        private readonly object/*!*/ _value;

        // TODO: we can save memory if we subclass StringLiteral (EncodedStringLiteral, EncodedSymbolLiteral) for _encoding != __ENCODING__
        private readonly RubyEncoding/*!*/ _encoding;

        private readonly StringLiteralMutability _mutability;

        // A synthesised string - a defined? category, __FILE__, an error message - is an ordinary
        // mutable one; only something the user actually wrote is frozen or chilled.
        internal StringLiteral(object/*!*/ value, RubyEncoding/*!*/ encoding, SourceSpan location)
            : this(value, encoding, StringLiteralMutability.Mutable, location) {
        }

        internal StringLiteral(object/*!*/ value, RubyEncoding/*!*/ encoding, StringLiteralMutability mutability, SourceSpan location) 
            : base(location) {
            Debug.Assert(value is string || value is byte[]);
            Debug.Assert(encoding != null);
            _value = value;
            _encoding = encoding;
            _mutability = mutability;
        }

        public StringLiteralMutability Mutability {
            get { return _mutability; }
        }

        public object/*!*/ Value {
            get { return _value; }
        }

        public RubyEncoding/*!*/ Encoding {
            get { return _encoding; }
        }

        public MutableString/*!*/ GetMutableString() {
            string str = _value as string;
            if (str != null) {
                return MutableString.Create(str, _encoding);
            } else {
                return MutableString.CreateBinary((byte[])_value, _encoding);
            }
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            string site = GetDebugSite(gen);
            switch (_mutability) {
                case StringLiteralMutability.Frozen: return TransformFrozen(_value, _encoding, site);
                case StringLiteralMutability.Chilled: return TransformChilled(_value, _encoding, site);
                default: return Transform(_value, _encoding);
            }
        }

        // Under --debug-frozen-string-literal every literal remembers where it was written,
        // so that a FrozenError or a chilled-mutation warning can name the place.
        private string GetDebugSite(AstGenerator/*!*/ gen) {
            return gen.Context.RubyOptions.DebugFrozenStringLiteral
                ? gen.SourcePath + ":" + Location.Start.Line.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>
        /// `"literal".freeze': MRI compiles this to the deduplicated frozen string (opt_str_freeze),
        /// whatever the file says about frozen_string_literal, so every evaluation answers the same
        /// object - the one -"literal" answers too.
        /// </summary>
        internal MSA.Expression/*!*/ TransformReadFrozen(AstGenerator/*!*/ gen) {
            return TransformFrozen(_value, _encoding, GetDebugSite(gen));
        }

        /// <summary>
        /// A frozen literal is created once and then handed out again on every evaluation, so the
        /// StrongBox belongs to this one literal in the program - the same shape the regexp
        /// literals use for their cache.
        /// </summary>
        internal static MSA.Expression/*!*/ TransformFrozen(object/*!*/ value, RubyEncoding/*!*/ encoding, string site) {
            var cache = AstUtils.Constant(new StrongBox<MutableString>(null));
            if (site == null) {
                return (value is string)
                    ? Methods.CreateFrozenMutableStringL.OpCall(AstUtils.Constant(value), encoding.Expression, cache)
                    : Methods.CreateFrozenMutableStringB.OpCall(AstUtils.Constant(value), encoding.Expression, cache);
            }

            return (value is string)
                ? Methods.CreateFrozenMutableStringLDebug.OpCall(AstUtils.Constant(value), encoding.Expression, cache, AstUtils.Constant(site))
                : Methods.CreateFrozenMutableStringBDebug.OpCall(AstUtils.Constant(value), encoding.Expression, cache, AstUtils.Constant(site));
        }

        /// <summary>
        /// A literal in a file that said nothing about frozen_string_literal. Still a fresh
        /// mutable string every time, so there is no cache; it only carries the bit that makes
        /// the first mutation warn.
        /// </summary>
        internal static MSA.Expression/*!*/ TransformChilled(object/*!*/ value, RubyEncoding/*!*/ encoding, string site) {
            if (site == null) {
                return (value is string)
                    ? Methods.CreateChilledMutableStringL.OpCall(AstUtils.Constant(value), encoding.Expression)
                    : Methods.CreateChilledMutableStringB.OpCall(AstUtils.Constant(value), encoding.Expression);
            }

            return (value is string)
                ? Methods.CreateChilledMutableStringLDebug.OpCall(AstUtils.Constant(value), encoding.Expression, AstUtils.Constant(site))
                : Methods.CreateChilledMutableStringBDebug.OpCall(AstUtils.Constant(value), encoding.Expression, AstUtils.Constant(site));
        }

        internal static MSA.Expression/*!*/ Transform(object/*!*/ value, RubyEncoding/*!*/ encoding) {
            if (value is string) {
                return Methods.CreateMutableStringL.OpCall(AstUtils.Constant(value), encoding.Expression);
            } else {
                return Methods.CreateMutableStringB.OpCall(AstUtils.Constant(value), encoding.Expression);
            }
        }
    }
}

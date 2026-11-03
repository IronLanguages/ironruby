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
using System.Linq.Expressions;
#else
using Microsoft.Scripting.Ast;
#endif

using System;
using System.Collections;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Generation;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Diagnostics;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime.Conversions {
    using Ast = Expression;
    using System.Collections.Generic;

    public enum CompositeConversion {
        ToFixnumToStr,
        ToStrToFixnum,
        ToIntToI,
        ToAryToInt,
        ToPathToStr,
        ToHashToStr,
    }

    public sealed class CompositeConversionAction : RubyConversionAction {
        private readonly CompositeConversion _conversion;
        private readonly ProtocolConversionAction[]/*!*/ _conversions;
        private readonly Type/*!*/ _resultType;

        private CompositeConversionAction(CompositeConversion conversion, Type/*!*/ resultType, params ProtocolConversionAction[]/*!*/ conversions)
            : base(conversions[0].Context) {
            Assert.NotNullItems(conversions);
            Assert.NotEmpty(conversions);

            _conversions = conversions;
            _conversion = conversion;
            _resultType = resultType;
        }

        public override Type/*!*/ ReturnType {
            get { return _resultType; }
        }

        /// <summary>
        /// The context's one binder for the conversion (<see cref="RubyMetaBinderFactory.CompositeConversion"/>), as every
        /// other conversion has: its sites then share the rules it binds rather than each binding its own for the same
        /// receiver class, and a storage's GetSite(Make(...)) allocates nothing once the site exists.
        /// </summary>
        public static CompositeConversionAction/*!*/ Make(RubyContext/*!*/ context, CompositeConversion/*!*/ conversion) {
            // A new call site then finds the rules its binder has bound for other sites; with an action of its own -
            // Make used to create one per call - every site bound each rule anew (30 binds at `ir -e 1' for ToPathToStr
            // alone, about a quarter of a millisecond each on the R2R host). A null context (MakeShared, shared
            // components) gets the shared factory's one action.
            var factory = (context != null) ? context.MetaBinderFactory : RubyMetaBinderFactory.Shared;
            return factory.CompositeConversion(conversion);
        }

        /// <summary>
        /// A new action - for <see cref="RubyMetaBinderFactory"/>, which keeps one per runtime. Everyone else calls Make.
        /// A null <paramref name="context"/> makes the shared action (<see cref="MakeShared"/>), of shared components.
        /// </summary>
        internal static CompositeConversionAction/*!*/ Create(RubyContext context, CompositeConversion conversion) {
            switch (conversion) {
                case CompositeConversion.ToFixnumToStr:
                    return new CompositeConversionAction(conversion,
                        typeof(Union<int, MutableString>), ConvertToFixnumAction.Make(context), ConvertToStrAction.Make(context)
                    );

                case CompositeConversion.ToStrToFixnum:
                    return new CompositeConversionAction(conversion,
                        typeof(Union<MutableString, int>), ConvertToStrAction.Make(context), ConvertToFixnumAction.Make(context)
                    );

                case CompositeConversion.ToIntToI:
                    return new CompositeConversionAction(conversion,
                        typeof(IntegerValue), ConvertToIntAction.Make(context), ConvertToIAction.Make(context)
                    );

                case CompositeConversion.ToAryToInt:
                    return new CompositeConversionAction(conversion,
                        typeof(Union<IList, int>), ConvertToArrayAction.Make(context), ConvertToFixnumAction.Make(context)
                    );

                case CompositeConversion.ToPathToStr:
                    return new CompositeConversionAction(conversion,
                        typeof(MutableString), ConvertToPathAction.Make(context), ConvertToStrAction.Make(context)
                    );

                case CompositeConversion.ToHashToStr:
                    return new CompositeConversionAction(conversion,
                        typeof(Union<IDictionary<object, object>, MutableString>), ConvertToHashAction.Make(context), ConvertToStrAction.Make(context)
                    );

                default:
                    throw Assert.Unreachable;
            }
        }

        [Emitted]
        public static CompositeConversionAction/*!*/ MakeShared(CompositeConversion conversion) {
            return Make(null, conversion);
        }

        public override Expression/*!*/ CreateExpression() {
            return Methods.GetMethod(GetType(), "MakeShared").OpCall(Ast.Constant(_conversion));
        }

        protected override bool Build(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, bool defaultFallback) {
            Debug.Assert(defaultFallback, "custom fallback not supported");
            ProtocolConversionAction.BuildConversion(metaBuilder, args, _resultType, _conversions);
            return true;
        }

        public override string/*!*/ ToString() {
            return _conversion.ToString() + (Context != null ? " @" + Context.RuntimeId.ToString() : null);
        }
    }
}

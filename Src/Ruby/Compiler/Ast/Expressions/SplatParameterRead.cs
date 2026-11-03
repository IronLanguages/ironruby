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

using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    /// <summary>
    /// The steps of the prologue PrismAstBridge.LowerGeneralParameters emits to take apart the
    /// single splat a signature mixing keywords with optional, rest or post parameters is lowered
    /// to. Each is one runtime call (see <see cref="IronRuby.Runtime.RubyOps.ShiftSplatArgument"/>
    /// and its neighbours) where the prologue used to make one or more dynamic Array calls.
    /// </summary>
    public partial class SplatParameterRead : Expression {
        private enum ReadKind {
            Copy,
            PopKeywords,
            IsAutoSplat,
            AutoSplat,
            CheckArity,
            Shift,
            ShiftOptional,
            ShiftRest
        }

        private readonly ReadKind _kind;
        // the hidden local holding the splat
        private readonly Expression/*!*/ _arguments;
        private readonly int _count1, _count2;
        private readonly Expression _defaultValue;

        public Expression/*!*/ Arguments {
            get { return _arguments; }
        }

        public Expression DefaultValue {
            get { return _defaultValue; }
        }

        private SplatParameterRead(ReadKind kind, Expression/*!*/ arguments, int count1, int count2, Expression defaultValue, SourceSpan location)
            : base(location) {
            Assert.NotNull(arguments);
            _kind = kind;
            _arguments = arguments;
            _count1 = count1;
            _count2 = count2;
            _defaultValue = defaultValue;
        }

        /// <summary>A copy of the arguments as they arrived (for a parameterless `super`).</summary>
        public static SplatParameterRead/*!*/ Copy(Expression/*!*/ arguments, SourceSpan location) {
            return new SplatParameterRead(ReadKind.Copy, arguments, 0, 0, null, location);
        }

        /// <summary>The keyword hash taken off the end of the arguments, or an empty hash.</summary>
        public static SplatParameterRead/*!*/ PopKeywords(Expression/*!*/ arguments, SourceSpan location) {
            return new SplatParameterRead(ReadKind.PopKeywords, arguments, 0, 0, null, location);
        }

        /// <summary>True if the arguments are a single Array, which a block auto-splats.</summary>
        public static SplatParameterRead/*!*/ IsAutoSplat(Expression/*!*/ arguments, SourceSpan location) {
            return new SplatParameterRead(ReadKind.IsAutoSplat, arguments, 0, 0, null, location);
        }

        /// <summary>A copy of the single Array argument a block auto-splats.</summary>
        public static SplatParameterRead/*!*/ AutoSplat(Expression/*!*/ arguments, SourceSpan location) {
            return new SplatParameterRead(ReadKind.AutoSplat, arguments, 0, 0, null, location);
        }

        /// <summary>Raises ArgumentError unless there are minimum..maximum arguments (maximum &lt; 0: no cap).</summary>
        public static SplatParameterRead/*!*/ CheckArity(Expression/*!*/ arguments, int minimum, int maximum, SourceSpan location) {
            return new SplatParameterRead(ReadKind.CheckArity, arguments, minimum, maximum, null, location);
        }

        /// <summary>The next argument, nil if none is left.</summary>
        public static SplatParameterRead/*!*/ Shift(Expression/*!*/ arguments, SourceSpan location) {
            return new SplatParameterRead(ReadKind.Shift, arguments, 0, 0, null, location);
        }

        /// <summary>
        /// The next argument if the <paramref name="postCount"/> post parameters leave one,
        /// otherwise the value of <paramref name="defaultValue"/>.
        /// </summary>
        public static SplatParameterRead/*!*/ ShiftOptional(Expression/*!*/ arguments, int postCount, Expression/*!*/ defaultValue, SourceSpan location) {
            Assert.NotNull(defaultValue);
            return new SplatParameterRead(ReadKind.ShiftOptional, arguments, postCount, 0, defaultValue, location);
        }

        /// <summary>The arguments the <paramref name="postCount"/> post parameters leave, as an Array.</summary>
        public static SplatParameterRead/*!*/ ShiftRest(Expression/*!*/ arguments, int postCount, SourceSpan location) {
            return new SplatParameterRead(ReadKind.ShiftRest, arguments, postCount, 0, null, location);
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            var arguments = AstUtils.Box(_arguments.TransformRead(gen));
            switch (_kind) {
                case ReadKind.Copy:
                    return Methods.CopySplatArguments.OpCall(arguments);

                case ReadKind.PopKeywords:
                    return Methods.PopKeywordArguments.OpCall(gen.CurrentScopeVariable, arguments);

                case ReadKind.IsAutoSplat:
                    return AstUtils.Box(Methods.IsAutoSplatArgument.OpCall(arguments));

                case ReadKind.AutoSplat:
                    return Methods.AutoSplatArgument.OpCall(arguments);

                case ReadKind.CheckArity:
                    return Ast.Block(
                        Methods.CheckSplatArity.OpCall(arguments, AstUtils.Constant(_count1), AstUtils.Constant(_count2)),
                        AstUtils.Constant(null, typeof(object))
                    );

                case ReadKind.Shift:
                    return Methods.ShiftSplatArgument.OpCall(arguments);

                case ReadKind.ShiftRest:
                    return Methods.ShiftRestSplatArguments.OpCall(arguments, AstUtils.Constant(_count1));

                default:
                    // value = ShiftOptionalSplatArgument(args, postCount); value == DefaultArgument ? <default> : value
                    var value = gen.CurrentScope.DefineHiddenVariable("#optional", typeof(object));
                    return Ast.Condition(
                        Ast.Equal(
                            Ast.Assign(value, Methods.ShiftOptionalSplatArgument.OpCall(arguments, AstUtils.Constant(_count1))),
                            Ast.Field(null, Fields.DefaultArgument)
                        ),
                        AstUtils.Box(_defaultValue.TransformRead(gen)),
                        value
                    );
            }
        }

        internal override MSA.Expression/*!*/ TransformReadBoolean(AstGenerator/*!*/ gen, bool positive) {
            if (_kind == ReadKind.IsAutoSplat) {
                var test = Methods.IsAutoSplatArgument.OpCall(AstUtils.Box(_arguments.TransformRead(gen)));
                return positive ? test : Ast.Not(test);
            }
            return base.TransformReadBoolean(gen, positive);
        }
    }
}

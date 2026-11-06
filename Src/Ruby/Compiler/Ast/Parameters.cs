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
using System.Collections;
using System.Diagnostics;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;

    // <leading-mandatory>, <optional>, *<array>, <trailing-mandatory>, &block
    public partial class Parameters : Node {
        internal static readonly Parameters/*!*/ Empty = new Parameters(null, 0, null, null, null, SourceSpan.None) {
            Signature = RubyParameterSignature.Empty
        };

        // all mandatory parameters:
        private readonly LeftValue/*!*/[]/*!*/ _mandatory;

        // the number of leading mandatory parameters:
        private readonly int _leadingMandatoryCount;

        private readonly SimpleAssignmentExpression/*!*/[]/*!*/ _optional;
        private readonly LeftValue _unsplat;
        private readonly LocalVariable _block;

        // the parameter list as it was written, before keywords were lowered onto positionals;
        // null when the front end did not record one (the 1.9 parser never does)
        private RubyParameterSignature _signature;

        /// <summary>
        /// What #parameters and #arity should report, which is the declaration rather than the
        /// lowered parameter list above. Null if unknown.
        /// </summary>
        public RubyParameterSignature Signature {
            get { return _signature; }
            set { _signature = value; }
        }

        // Set when the signature was lowered to a single splat whose contents a prologue
        // unpacks by hand (keywords mixed with optional/rest/post positionals). The
        // prologue empties the splat, so a parameterless `super` has to read the copy of
        // the original argument list the prologue kept aside instead of the parameters.
        private LeftValue _superSplat;

        internal LeftValue SuperSplat {
            get { return _superSplat; }
            set { _superSplat = value; }
        }

        public LeftValue/*!*/[]/*!*/ Mandatory {
            get { return _mandatory; }
        }

        public int LeadingMandatoryCount {
            get { return _leadingMandatoryCount; }
        }

        public SimpleAssignmentExpression/*!*/[]/*!*/ Optional {
            get { return _optional; }
        }

        // for-loop might use constant variable, instance variable, class variable or global variable:
        public LeftValue Unsplat {
            get { return _unsplat; }
        }

        public LocalVariable Block {
            get { return _block; }
        }

        public Parameters(LeftValue/*!*/[] mandatory, int leadingMandatoryCount, SimpleAssignmentExpression/*!*/[] optional, LeftValue unsplat, LocalVariable block, SourceSpan location)
            : base(location) {

            mandatory = mandatory ?? Array.Empty<LeftValue>();
            optional = optional ?? Array.Empty<SimpleAssignmentExpression>();

            Debug.Assert(leadingMandatoryCount >= 0 && leadingMandatoryCount <= mandatory.Length);
            Debug.Assert(leadingMandatoryCount == mandatory.Length || optional != null || unsplat != null);

            _mandatory = mandatory;
            _leadingMandatoryCount = leadingMandatoryCount;
            _optional = optional;
            _unsplat = unsplat;
            _block = block;
        }

        internal MSA.Expression/*!*/ TransformOptionalsInitialization(AstGenerator/*!*/ gen) {
            Assert.NotNull(gen);

            if (_optional.Length == 0) {
                return AstUtils.Empty();
            }

            MSA.Expression singleton = gen.CurrentScope.DefineHiddenVariable("#default", typeof(object));

            MSA.Expression result = AstUtils.Empty();
            for (int i = 0; i < _optional.Length; i++) {
                result = AstUtils.IfThen(
                    Ast.Equal(_optional[i].Left.TransformRead(gen), singleton),
                    Ast.Block(
                        // A parameter that is about to take its default is nil while the default
                        // is being evaluated, so `def m(a = a)` binds nil rather than leaking the
                        // "no argument given" sentinel.
                        _optional[i].Left.TransformWrite(gen, AstUtils.Constant(null, typeof(object))),
                        result,
                        _optional[i].TransformRead(gen) // assignment
                    )
                );
            }
            
            return Ast.Block(
                Ast.Assign(singleton, Ast.Field(null, Fields.DefaultArgument)),
                result,
                AstUtils.Empty()
            );
        }

        internal void TransformForSuperCall(AstGenerator/*!*/ gen, CallSiteBuilder/*!*/ siteBuilder) {
            if (_superSplat != null) {
                siteBuilder.SplattedArgument = _superSplat.TransformRead(gen);
                return;
            }

            int trailingMandatoryCount = _mandatory.Length - _leadingMandatoryCount;
            if (_unsplat != null && trailingMandatoryCount > 0) {
                // A call site splats at the end of the argument list only, so a signature with
                // post parameters (`def m(a, *b, c)`) has to hand `super` the whole list as one
                // splatted array to keep `a, *b, c` in that order.
                MSA.Expression array = Methods.MakeArray0.OpCall();
                for (int i = 0; i < _leadingMandatoryCount; i++) {
                    array = Methods.AddItem.OpCall(array, AstUtils.Box(_mandatory[i].TransformRead(gen)));
                }
                foreach (SimpleAssignmentExpression s in _optional) {
                    array = Methods.AddItem.OpCall(array, AstUtils.Box(s.Left.TransformRead(gen)));
                }
                array = Methods.AddRange.OpCall(array, TransformUnsplatForSuperCall(gen));
                for (int i = _leadingMandatoryCount; i < _mandatory.Length; i++) {
                    array = Methods.AddItem.OpCall(array, AstUtils.Box(_mandatory[i].TransformRead(gen)));
                }
                siteBuilder.SplattedArgument = array;
                return;
            }

            for (int i = 0; i < _leadingMandatoryCount; i++) {
                siteBuilder.Add(_mandatory[i].TransformRead(gen));
            }

            foreach (SimpleAssignmentExpression s in _optional) {
                siteBuilder.Add(s.Left.TransformRead(gen));
            }

            for (int i = _leadingMandatoryCount; i < _mandatory.Length; i++) {
                siteBuilder.Add(_mandatory[i].TransformRead(gen));
            }

            if (_unsplat != null) {
                siteBuilder.SplattedArgument = TransformUnsplatForSuperCall(gen);
            }
        }

        /// <summary>
        /// The rest parameter as `super` passes it on. It is an ordinary local that the body may
        /// have reassigned to anything, so it is splatted the way `f(*x)` would splat it:
        /// `args = {a: 1}` passes [[:a, 1]], `args = 5` passes [5].
        /// </summary>
        private MSA.Expression/*!*/ TransformUnsplatForSuperCall(AstGenerator/*!*/ gen) {
            return AstUtils.LightDynamic(ExplicitSplatAction.Make(gen.Context), typeof(IList), _unsplat.TransformRead(gen));
        }

        internal BlockSignatureAttributes GetBlockSignatureAttributes() {
            var result = BlockSignatureAttributes.None;

            if (_unsplat != null) {
                result |= BlockSignatureAttributes.HasUnsplatParameter;
            }

            if (_block != null) {
                result |= BlockSignatureAttributes.HasProcParameter;
            }

            int arity = _mandatory.Length;
            if (_unsplat is Placeholder && _optional.Length == 0) {
                // |a,| takes the rest implicitly: MRI's arity is 1 and a lambda is strict about it
            } else if (_unsplat != null || _optional.Length > 0) {
                // optional parameters make the block variadic: MRI reports -(mandatory + 1)
                arity = -(arity + 1);
            } else if (arity > 0 && _mandatory[_mandatory.Length - 1] is Placeholder) {
                arity--;
            }

            return BlockDispatcher.MakeAttributes(result, arity);
        }
    }
}
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstExpressions = ReadOnlyCollectionBuilder<MSA.Expression>;
    using AstBlock = Microsoft.Scripting.Ast.BlockBuilder;

    public partial class CompoundLeftValue : LeftValue {
        // TODO: 1.9
        
        /// <summary>
        /// Empty LHS - used by blocks.
        /// </summary>
        internal static readonly CompoundLeftValue/*!*/ EmptyBlockSignature = new CompoundLeftValue(LeftValue.EmptyArray);

        // TODO: 1.9

        /// <summary>
        /// Unspecified LHS - used by blocks.
        /// </summary>
        internal static readonly CompoundLeftValue/*!*/ UnspecifiedBlockSignature = new CompoundLeftValue(LeftValue.EmptyArray);

        /// <summary>
        /// List of l-values, possibly compound.
        /// </summary>
        private readonly LeftValue/*!*/[]/*!*/ _leftValues;

        /// <summary>
        /// The index in _leftValues of *args l-value. 
        /// _leftValues.Length if there is none.
        /// </summary>
        private readonly int _unsplattedValueIndex;

        public LeftValue/*!*/[]/*!*/ LeftValues {
            get { return _leftValues; }
        }

        public int UnsplattedValueIndex {
            get { return _unsplattedValueIndex; }
        }

        public LeftValue UnsplattedValue {
            get { return HasUnsplattedValue ? _leftValues[_unsplattedValueIndex] : null; }
        }

        public bool HasUnsplattedValue {
            get { return _unsplattedValueIndex < _leftValues.Length; }
        }

        public CompoundLeftValue(LeftValue/*!*/[]/*!*/ leftValues)
            : this(leftValues, leftValues.Length) {
        }

        public CompoundLeftValue(LeftValue/*!*/[]/*!*/ leftValues, int unsplattedValueIndex) 
            : base(SourceSpan.None) {
            Assert.NotNullItems(leftValues);
            Debug.Assert(unsplattedValueIndex >= 0 && unsplattedValueIndex <= leftValues.Length);
            _leftValues = leftValues;
            _unsplattedValueIndex = unsplattedValueIndex;
        }

        internal override MSA.Expression/*!*/ TransformRead(AstGenerator/*!*/ gen, MSA.Expression targetValue, bool tryRead) {
            // called in Parameters.TransformForSuperCall
            throw new NotSupportedException("TODO: reading compound l-value");
        }

        internal override MSA.Expression TransformTargetRead(AstGenerator/*!*/ gen) {
            return null;
        }

        internal override MSA.Expression/*!*/ TransformWrite(AstGenerator/*!*/ gen, MSA.Expression targetValue, MSA.Expression/*!*/ rightValue) {
            // called when dealing with nested compound LHS, e.g. in a,(b,c),d = x,y,z this method implements (b,c) = y assignment
            Debug.Assert(targetValue == null);
            return TransformWrite(gen, rightValue, true);
        }

        internal MSA.Expression/*!*/ TransformWrite(AstGenerator/*!*/ gen, Arguments/*!*/ rightValues) {
            Debug.Assert(!rightValues.IsEmpty);
            Assert.NotEmpty(_leftValues);

            if (_leftValues.Length == 1) {
                // (...) = RHS is equivalent to ... = RHS:
                CompoundLeftValue compound = _leftValues[0] as CompoundLeftValue;
                if (compound != null) {
                    return compound.TransformWrite(gen, rightValues);
                }

                if (!HasUnsplattedValue) {
                    return _leftValues[0].TransformWrite(gen, rightValues.TransformToArray(gen));
                }
            }

            // MRI evaluates the receivers of the l-values first, left to right, and only then
            // the right hand side: `a.x, b.y = f, g` calls a, b, f, g in that order.
            var targets = new Queue<MSA.Expression>();
            var evaluation = new AstBlock();
            EvaluateTargets(gen, evaluation, targets);

            MSA.Expression result;
            if (rightValues.Expressions.Length == 1) {
                result = TransformWrite(gen, rightValues.Expressions[0].TransformRead(gen), true, targets);
            } else {
                result = TransformWrite(gen, rightValues.TransformToArray(gen), false, targets);
            }

            if (evaluation.Count == 0) {
                return result;
            }
            evaluation.Add(result);
            return evaluation;
        }

        /// <summary>
        /// Evaluates the target (receiver) of every l-value that has one into a temporary, in
        /// source order, and queues the temporaries for the write phase to use in the same order.
        /// </summary>
        private void EvaluateTargets(AstGenerator/*!*/ gen, AstBlock/*!*/ evaluation, Queue<MSA.Expression>/*!*/ targets) {
            // Nested targets ((a, b), c) recurse through here (see StackGuard).
            if (!IronRuby.Runtime.StackGuard.IsSufficient) {
                IronRuby.Runtime.StackGuard.RunOnFreshStack(() => EvaluateTargets(gen, evaluation, targets));
                return;
            }
            foreach (var lvalue in _leftValues) {
                var nested = lvalue as CompoundLeftValue;
                if (nested != null) {
                    nested.EvaluateTargets(gen, evaluation, targets);
                    continue;
                }

                var target = lvalue.TransformTargetRead(gen);
                if (target == null) {
                    targets.Enqueue(null);
                    continue;
                }

                var temp = gen.CurrentScope.DefineHiddenVariable("#lhs", target.Type);
                evaluation.Add(Ast.Assign(temp, target));
                targets.Enqueue(temp);
            }
        }

        /// <summary>
        /// Writes one l-value, using the receiver the target-evaluation phase already computed
        /// when there is one queued (a nested write, which happens outside a multiple assignment,
        /// passes no queue and evaluates the receiver here).
        /// </summary>
        private static MSA.Expression/*!*/ Write(AstGenerator/*!*/ gen, LeftValue/*!*/ lvalue, Queue<MSA.Expression> targets,
            MSA.Expression/*!*/ rightValue) {

            var nested = lvalue as CompoundLeftValue;
            if (nested != null) {
                // (see EvaluateTargets)
                if (!IronRuby.Runtime.StackGuard.IsSufficient) {
                    return IronRuby.Runtime.StackGuard.RunOnFreshStack(() => nested.TransformWrite(gen, rightValue, true, targets));
                }
                return nested.TransformWrite(gen, rightValue, true, targets);
            }
            return targets != null
                ? lvalue.TransformWrite(gen, targets.Dequeue(), rightValue)
                : lvalue.TransformWrite(gen, rightValue);
        }

        private MSA.Expression/*!*/ TransformWrite(AstGenerator/*!*/ gen, MSA.Expression/*!*/ transformedRight, bool isSimpleRhs) {
            return TransformWrite(gen, transformedRight, isSimpleRhs, null);
        }

        private MSA.Expression/*!*/ TransformWrite(AstGenerator/*!*/ gen, MSA.Expression/*!*/ transformedRight, bool isSimpleRhs,
            Queue<MSA.Expression> targets) {
            var writes = new AstBlock();
            
            MSA.Expression rightList = gen.CurrentScope.DefineHiddenVariable("#rhs", typeof(IList));
            MSA.Expression result;

            if (isSimpleRhs) {
                // 1.9 returns the RHS, not an unsplatted array, if there is just a single RHS:
                result = gen.CurrentScope.DefineHiddenVariable("#pr", transformedRight.Type);
                writes.Add(Ast.Assign(result, transformedRight)); 

                transformedRight = AstUtils.LightDynamic(ImplicitSplatAction.Make(gen.Context), typeof(IList), result);
            } else {
                result = rightList;
            }

            writes.Add(Ast.Assign(rightList, transformedRight));

            for (int i = 0; i < _unsplattedValueIndex; i++) {
                writes.Add(Write(gen, _leftValues[i], targets, Methods.GetArrayItem.OpCall(rightList, AstUtils.Constant(i))));
            }

            if (HasUnsplattedValue) {
                MSA.Expression explicitCount = AstUtils.Constant(_leftValues.Length - 1);

                // remaining RHS values:
                MSA.Expression array = Methods.GetArrayRange.OpCall(rightList, AstUtils.Constant(_unsplattedValueIndex), explicitCount);
                writes.Add(Write(gen, _leftValues[_unsplattedValueIndex], targets, array));

                for (int i = _unsplattedValueIndex + 1; i < _leftValues.Length; i++) {
                    writes.Add(Write(gen, _leftValues[i], targets,
                        Methods.GetTrailingArrayItem.OpCall(rightList, AstUtils.Constant(_leftValues.Length - i), explicitCount)));
                }
            }

            writes.Add(result);
            return writes;
        }

        public override string/*!*/ ToString() {
            var result = new StringBuilder();
            bool first = true;

            for (int i = 0; i < _leftValues.Length; i++) {
                if (!first) {
                    result.Append(',');
                } else {
                    first = false;
                }

                if (i == _unsplattedValueIndex) {
                    result.Append('*');
                    result.Append(_leftValues[i].ToString());
                } else {
                    var compound = _leftValues[i] as CompoundLeftValue;
                    if (compound != null) {
                        result.Append('(');
                    }

                    result.Append(_leftValues[i].ToString());

                    if (compound != null) {
                        result.Append(')');
                    }
                }
            }

            return result.ToString();
        }
    }
}

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

using System.Linq.Expressions;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Scripting.Utils;
using IronRuby.Compiler;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using System.Collections;

namespace IronRuby.Runtime.Calls {
    using Ast = Expression;

    //
    // Maps arguments to parameters.
    //
    // Arguments:
    // <simple>, *<splat>, <rhs>
    //
    // Parameters:
    // <implicit>, <leading-mandatory>, <trailing-mandatory>, <optional>, *<unsplat>
    //
    public sealed class ArgsBuilder {
        private readonly Expression[]/*!*/ _arguments;
        private readonly int _implicitParamCount;
        private readonly int _mandatoryParamCount;
        private readonly int _leadingMandatoryParamCount;
        private readonly int _optionalParamCount;
        private readonly bool _hasUnsplatParameter;

        private int _actualArgumentCount;
        private CallArguments _callArguments;
        private int _lastArgumentParameterIndex = -1;

        // splatted argument list length and storage:
        private int _listLength;
        private ParameterExpression _listVariable;

        public int ActualArgumentCount {
            get { return _actualArgumentCount; }
        }

        public bool HasTooFewArguments {
            get { return _actualArgumentCount < _mandatoryParamCount; }
        }

        public bool HasTooManyArguments {
            get { return !_hasUnsplatParameter && _actualArgumentCount > _mandatoryParamCount + _optionalParamCount; }
        }

        public int LeadingMandatoryIndex {
            get { return _implicitParamCount; }
        }

        public int TrailingMandatoryIndex {
            get { return _implicitParamCount + _leadingMandatoryParamCount; }
        }

        public int OptionalParameterIndex {
            get { return _implicitParamCount + _mandatoryParamCount; }
        }

        public int UnsplatParameterIndex {
            get { return OptionalParameterIndex + _optionalParamCount; }
        }

        public int TrailingMandatoryCount {
            get { return _mandatoryParamCount - _leadingMandatoryParamCount; }
        }

        /// <summary>
        /// Where the last of the call's actual arguments ended up, or -1 when it went into the
        /// rest parameter (which has its own handling) or the call had no arguments. Only a
        /// trailing hash can be the keyword arguments of a call, so this is the one parameter a
        /// callee that declares no keywords has to unmark.
        /// </summary>
        public int LastArgumentParameterIndex {
            get {
                Debug.Assert(_actualArgumentCount != -1);
                return _lastArgumentParameterIndex;
            }
        }

        /// <param name="implicitParamCount">Parameters for which arguments are provided implicitly, i.e. not specified by user.</param>
        /// <param name="mandatoryParamCount">Number of parameters for which an actual argument must be specified.</param>
        /// <param name="leadingMandatoryParamCount">Number of mandatory parameters that precede any optional parameters.</param>
        /// <param name="optionalParamCount">Number of optional parameters.</param>
        /// <param name="hasUnsplatParameter">Method has * parameter (accepts any number of additional parameters).</param>
        public ArgsBuilder(int implicitParamCount, int mandatoryParamCount, int leadingMandatoryParamCount, int optionalParamCount, bool hasUnsplatParameter) {
            Debug.Assert(leadingMandatoryParamCount >= 0 && leadingMandatoryParamCount <= mandatoryParamCount);
            Debug.Assert(leadingMandatoryParamCount == mandatoryParamCount || optionalParamCount > 0 || hasUnsplatParameter);
            
            _arguments = new Expression[implicitParamCount + mandatoryParamCount + optionalParamCount + (hasUnsplatParameter ? 1 : 0)];
            _implicitParamCount = implicitParamCount;
            _mandatoryParamCount = mandatoryParamCount;
            _leadingMandatoryParamCount = leadingMandatoryParamCount;
            _optionalParamCount = optionalParamCount;
            _hasUnsplatParameter = hasUnsplatParameter;
            _actualArgumentCount = -1;
        }

        public Expression this[int index] {
            get {
                Debug.Assert(_actualArgumentCount != -1);
                return _arguments[index];
            }
        }

        internal Expression/*!*/[]/*!*/ GetArguments() {
            Debug.Assert(_actualArgumentCount != -1);
            return _arguments;
        }

        public void SetImplicit(int index, Expression/*!*/ arg) {
            Debug.Assert(_actualArgumentCount == -1);
            _arguments[index] = arg;
        }

        /// <summary>
        /// MRI spells the expected count of a variable-arity signature as a range:
        /// "2+" (mandatory + rest), "1..3" (mandatory + optionals), plain "2" otherwise.
        /// </summary>
        private void SetArityError(MetaObjectBuilder/*!*/ metaBuilder) {
            if (_hasUnsplatParameter) {
                metaBuilder.SetError(Methods.MakeWrongNumberOfArgumentsErrorN.OpCall(
                    AstUtils.Constant(_actualArgumentCount),
                    AstUtils.Constant(_mandatoryParamCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "+")
                ));
            } else if (_optionalParamCount > 0) {
                metaBuilder.SetError(Methods.MakeWrongNumberOfArgumentsErrorN.OpCall(
                    AstUtils.Constant(_actualArgumentCount),
                    AstUtils.Constant(
                        _mandatoryParamCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".." +
                        (_mandatoryParamCount + _optionalParamCount).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    )
                ));
            } else {
                metaBuilder.SetWrongNumberOfArgumentsError(_actualArgumentCount, _mandatoryParamCount);
            }
        }

        private Expression GetArgument(int argIndex, out bool isSplatted) {
            if (argIndex < _callArguments.SimpleArgumentCount) {
                isSplatted = false;
                return _callArguments.GetSimpleArgumentExpression(argIndex);
            }

            int i = argIndex - _callArguments.SimpleArgumentCount;
            if (i < _listLength) {
                isSplatted = true;
                return Ast.Call(_listVariable, Methods.IList_get_Item, AstUtils.Constant(i));
            }

            if (i == _listLength && _callArguments.Signature.HasRhsArgument) {
                isSplatted = false;
                return _callArguments.GetRhsArgumentExpression();
            }

            isSplatted = false;
            return null;
        }

        public void AddCallArguments(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            _callArguments = args;

            // calculate actual argument count:
            _actualArgumentCount = args.SimpleArgumentCount;
            if (args.Signature.HasSplattedArgument) {
                var splattedArg = args.GetSplattedMetaArgument();
                metaBuilder.AddSplattedArgumentTest((IList)splattedArg.Value, splattedArg.Expression, out _listLength, out _listVariable);
                _actualArgumentCount += _listLength;
            }
            if (args.Signature.HasRhsArgument) {
                _actualArgumentCount++;
            }

            // check:
            if (HasTooFewArguments || HasTooManyArguments) {
                SetArityError(metaBuilder);
                return;
            }

            bool isSplatted;

            int lastArgument = _actualArgumentCount - 1;

            // leading mandatory:
            for (int i = 0; i < _leadingMandatoryParamCount; i++) {
                _arguments[LeadingMandatoryIndex + i] = GetArgument(i, out isSplatted);
                if (i == lastArgument) {
                    _lastArgumentParameterIndex = LeadingMandatoryIndex + i;
                }
            }

            // trailing mandatory:
            for (int i = 0; i < TrailingMandatoryCount; i++) {
                _arguments[TrailingMandatoryIndex + i] = GetArgument(_actualArgumentCount - TrailingMandatoryCount + i, out isSplatted);
                if (_actualArgumentCount - TrailingMandatoryCount + i == lastArgument) {
                    _lastArgumentParameterIndex = TrailingMandatoryIndex + i;
                }
            }

            int start = _leadingMandatoryParamCount;
            int end = _actualArgumentCount - TrailingMandatoryCount;

            // optional:
            for (int i = 0; i < _optionalParamCount; i++) {
                if (start < end) {
                    if (start == lastArgument) {
                        _lastArgumentParameterIndex = OptionalParameterIndex + i;
                    }
                    _arguments[OptionalParameterIndex + i] = GetArgument(start++, out isSplatted);
                } else {
                    _arguments[OptionalParameterIndex + i] = Ast.Field(null, Fields.DefaultArgument);
                }
            }

            // unsplat:
            if (_hasUnsplatParameter) {
                Expression array;
                if (args.Signature.HasSplattedArgument) {
                    // simple:
                    var argsToUnsplat = new List<Expression>();
                    while (start < end) {
                        var arg = GetArgument(start, out isSplatted);
                        if (isSplatted) {
                            break;
                        }
                        argsToUnsplat.Add(AstUtils.Box(arg));
                        start++;
                    }
                    array = Methods.MakeArrayOpCall(argsToUnsplat);
                    
                    int rangeStart = start - args.SimpleArgumentCount;
                    int rangeLength = Math.Min(end - start, _listLength - rangeStart);

                    // splatted:
                    if (rangeLength > 0) {
                        array = Methods.AddSubRange.OpCall(array, _listVariable, Ast.Constant(rangeStart), Ast.Constant(rangeLength));
                        start += rangeLength;
                    }

                    // rhs:
                    while (start < end) {
                        array = Methods.AddItem.OpCall(array, AstUtils.Box(GetArgument(start, out isSplatted)));
                        start++;
                    }
                } else {
                    var argsToUnsplat = new List<Expression>(end - start);
                    while (start < end) {
                        argsToUnsplat.Add(AstUtils.Box(GetArgument(start++, out isSplatted)));
                    }
                    array = Methods.MakeArrayOpCall(argsToUnsplat);
                }

                _arguments[UnsplatParameterIndex] = array;
            }

            _callArguments = null;
            _listVariable = null;
            Debug.Assert(CollectionUtils.TrueForAll(_arguments, (e) => e != null));
        }
    }
}

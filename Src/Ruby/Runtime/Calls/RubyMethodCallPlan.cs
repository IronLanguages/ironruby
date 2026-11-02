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

using System;
using IronRuby.Builtins;
using Microsoft.Scripting.Utils;

namespace IronRuby.Runtime.Calls {

    /// <summary>
    /// How a call of a Ruby method is made without a rule when the precompiled dispatchers of Ruby methods (MethodDispatcher) do not
    /// take its parameters - optional ones, a rest parameter: the call's arguments are mapped to the parameters the way the rule maps
    /// them (RubyMethodInfo.BuildCallNoFlow, ArgsBuilder) and the method's body is called with them. A call of a number of arguments the
    /// method does not take is left to the rule (which raises the ArgumentError), as is one through an alias (see RubyCallAction).
    /// </summary>
    internal sealed class RubyMethodCallPlan {
        private readonly RubyMethodBody/*!*/ _body;
        private readonly Delegate/*!*/ _method;

        // the parameters: leading mandatory, trailing mandatory, optional, rest (ArgsBuilder's order, after self and the block)
        private readonly int _leadingCount;
        private readonly int _mandatoryCount;
        private readonly int _optionalCount;
        private readonly bool _hasRest;

        // RubyMethodInfo.BuildCallNoFlow: the rest array gets a trailing keyword hash unmarked (or marked for ruby2_keywords), and the
        // parameter the last argument goes to of a method that declares no keywords gets it unmarked:
        private readonly bool _normalizesRest;
        private readonly bool _clearsKeywords;

        private RubyMethodCallPlan(RubyMethodInfo/*!*/ method) {
            var parameters = method.Parameters;
            _body = method.Body;
            _method = method.GetDelegate();
            _leadingCount = parameters.LeadingMandatoryCount;
            _mandatoryCount = parameters.Mandatory.Length;
            _optionalCount = parameters.Optional.Length;
            _hasRest = parameters.Unsplat != null;
            _normalizesRest = _hasRest && method.KeepsTrailingHashInRest;
            _clearsKeywords = !method.TakesOrRefusesKeywords;
        }

        private int ParameterCount {
            get { return _mandatoryCount + _optionalCount + (_hasRest ? 1 : 0); }
        }

        /// <summary>
        /// A plan for calls of <paramref name="method"/> with <paramref name="count"/> arguments, or null if the method doesn't take them.
        /// </summary>
        internal static RubyMethodCallPlan TryCreate(RubyMethodInfo/*!*/ method, int count) {
            var parameters = method.Parameters;
            int mandatory = parameters.Mandatory.Length;
            if (count < mandatory || parameters.Unsplat == null && count > mandatory + parameters.Optional.Length) {
                return null;
            }

            var plan = new RubyMethodCallPlan(method);
            int parameterCount = plan.ParameterCount;
            var type = plan._method.GetType();
            if (type != RubyMethodInfo.ParamsArrayDelegateType && type != MemberDispatcher.GetRubyMethodDelegateType(parameterCount)) {
                return null;
            }
            return plan;
        }

        internal object Invoke(object self, Proc block, object[]/*!*/ args) {
            int count = args.Length;
            int lastArgument = count - 1;
            int lastParameter = -1;
            var parameters = new object[ParameterCount];

            // leading mandatory:
            for (int i = 0; i < _leadingCount; i++) {
                parameters[i] = args[i];
                if (i == lastArgument) {
                    lastParameter = i;
                }
            }

            // trailing mandatory:
            int trailingCount = _mandatoryCount - _leadingCount;
            for (int i = 0; i < trailingCount; i++) {
                int a = count - trailingCount + i;
                parameters[_leadingCount + i] = args[a];
                if (a == lastArgument) {
                    lastParameter = _leadingCount + i;
                }
            }

            // optional:
            int start = _leadingCount;
            int end = count - trailingCount;
            for (int i = 0; i < _optionalCount; i++) {
                if (start < end) {
                    if (start == lastArgument) {
                        lastParameter = _mandatoryCount + i;
                    }
                    parameters[_mandatoryCount + i] = args[start++];
                } else {
                    parameters[_mandatoryCount + i] = RubyOps.DefaultArgument;
                }
            }

            // rest:
            if (_hasRest) {
                var rest = new RubyArray(end - start);
                while (start < end) {
                    rest.Add(args[start++]);
                }
                parameters[_mandatoryCount + _optionalCount] = _normalizesRest ? RubyOps.NormalizeRestArgument(rest, _body) : rest;
            }

            if (_clearsKeywords && lastParameter >= 0) {
                parameters[lastParameter] = RubyOps.ClearKeywordArguments(parameters[lastParameter]);
            }

            return Call(self, block, parameters);
        }

        private object Call(object self, Proc block, object[]/*!*/ p) {
            if (_method.GetType() == RubyMethodInfo.ParamsArrayDelegateType) {
                return ((Func<object, Proc, object[], object>)_method)(self, block, p);
            }

            switch (p.Length) {
                case 0: return ((Func<object, Proc, object>)_method)(self, block);
                case 1: return ((Func<object, Proc, object, object>)_method)(self, block, p[0]);
                case 2: return ((Func<object, Proc, object, object, object>)_method)(self, block, p[0], p[1]);
                case 3: return ((Func<object, Proc, object, object, object, object>)_method)(self, block, p[0], p[1], p[2]);
                case 4: return ((Func<object, Proc, object, object, object, object, object>)_method)(self, block, p[0], p[1], p[2], p[3]);
                case 5: return ((Func<object, Proc, object, object, object, object, object, object>)_method)(self, block, p[0], p[1], p[2], p[3], p[4]);
                default: throw Assert.Unreachable;
            }
        }
    }
}

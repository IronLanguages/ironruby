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
    /// A call of a Ruby method made without a rule: of a method defined by def (<see cref="RubyMethodCallPlan"/>) or by
    /// define_method (<see cref="LambdaMethodCall"/>), the arguments given as an array.
    /// </summary>
    internal abstract class RubyMemberCall {
        internal abstract object Invoke(object self, Proc block, object[]/*!*/ args);
    }

    /// <summary>
    /// How a call of a Ruby method is made without a rule when the precompiled dispatchers of Ruby methods (MethodDispatcher) do not
    /// take its parameters - optional ones, a rest parameter: the call's arguments are mapped to the parameters the way the rule maps
    /// them (RubyMethodInfo.BuildCallNoFlow, ArgsBuilder) and the method's body is called with them. A call of a number of arguments the
    /// method does not take is left to the rule (which raises the ArgumentError), as is one through an alias (see RubyCallAction).
    /// </summary>
    internal sealed class RubyMethodCallPlan : RubyMemberCall {
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

        // The name a call through an alias calls the method by, which the body's scope learns for __callee__
        // (RubyOps.MarkAliasCall); null for a call by the method's own name.
        private readonly string _calleeName;
        private readonly string/*!*/ _definitionName;

        private RubyMethodCallPlan(RubyMethodInfo/*!*/ method, string/*!*/ name) {
            _definitionName = method.DefinitionName;
            _calleeName = (name != _definitionName) ? name : null;
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
        /// <param name="name">The name the method is called by (an alias of it, maybe).</param>
        internal static RubyMethodCallPlan TryCreate(RubyMethodInfo/*!*/ method, int count, string/*!*/ name) {
            var parameters = method.Parameters;
            int mandatory = parameters.Mandatory.Length;
            if (count < mandatory || parameters.Unsplat == null && count > mandatory + parameters.Optional.Length) {
                return null;
            }

            var plan = new RubyMethodCallPlan(method, name);
            int parameterCount = plan.ParameterCount;
            var type = plan._method.GetType();
            if (type != RubyMethodInfo.ParamsArrayDelegateType && type != MemberDispatcher.GetRubyMethodDelegateType(parameterCount)) {
                return null;
            }
            return plan;
        }

        internal override object Invoke(object self, Proc block, object[]/*!*/ args) {
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

            // after every argument, just before the body runs (RubyMethodInfo.BuildCallNoFlow wraps the last argument):
            if (_calleeName != null) {
                RubyOps.MarkAliasCall(null, _calleeName, _definitionName);
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
    /// <summary>
    /// A call of a method define_method made of a block (RubyLambdaMethodInfo), as its rule makes it (RubyLambdaMethodInfo.
    /// BuildCallNoFlow, Proc.BuildCall): the lambda yielded to with the arguments, the receiver as self and the block given, in a
    /// frame of its own that a return from the lambda unwinds to. A call of a number of arguments the method doesn't take is left to
    /// the rule (which raises), as is a splatted call (whose arguments the rule doesn't count).
    /// </summary>
    internal sealed class LambdaMethodCall : RubyMemberCall {
        private readonly Proc/*!*/ _lambda;

        private LambdaMethodCall(Proc/*!*/ lambda) {
            _lambda = lambda;
        }

        internal static LambdaMethodCall TryCreate(RubyLambdaMethodInfo/*!*/ method, int count) {
            var dispatcher = method.Lambda.Dispatcher;
            int arity = dispatcher.Arity;
            int mandatory = arity >= 0 ? arity : -arity - 1;
            int maximum = dispatcher.HasUnsplatParameter ? Int32.MaxValue : dispatcher.ParameterCount;
            var signature = dispatcher.ParameterSignature;
            if (maximum == Int32.MaxValue && signature != null && signature.MaxArgumentCount >= 0) {
                maximum = signature.MaxArgumentCount;
            }
            return (count >= mandatory && count <= maximum) ? new LambdaMethodCall(method.Lambda) : null;
        }

        internal override object Invoke(object self, Proc block, object[]/*!*/ args) {
            var bfc = RubyOps.CreateBfcForProcCall(_lambda);
            try {
                object result;
                switch (args.Length) {
                    case 0: result = RubyOps.Yield0(block, self, bfc); break;
                    case 1: result = RubyOps.Yield1(args[0], block, self, bfc); break;
                    case 2: result = RubyOps.Yield2(args[0], args[1], block, self, bfc); break;
                    case 3: result = RubyOps.Yield3(args[0], args[1], args[2], block, self, bfc); break;
                    case 4: result = RubyOps.Yield4(args[0], args[1], args[2], args[3], block, self, bfc); break;
                    default: result = RubyOps.YieldN(args, block, self, bfc); break;
                }
                return RubyOps.MethodProcCall(bfc, result);
            } catch (Exception unwinder) when (RubyOps.IsLambdaUnwinderTarget(bfc, unwinder)) {
                // a return in a block nested in the lambda:
                return RubyOps.GetLambdaUnwinderReturnValue(unwinder);
            } finally {
                RubyOps.LeaveProcCall(bfc);
            }
        }
    }
}

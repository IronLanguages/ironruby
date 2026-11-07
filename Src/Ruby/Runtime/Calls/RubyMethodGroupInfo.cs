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
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using IronRuby.Builtins;
using IronRuby.Compiler;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Actions.Calls;

namespace IronRuby.Runtime.Calls {
    using Ast = MSA.Expression;

    /// <summary>
    /// A group of CLR methods that are treated as a single Ruby method.
    /// </summary>
    public class RubyMethodGroupInfo : RubyMethodGroupBase {
        // True: The group contains only static methods and can only be called statically (with no receiver).
        // False: The group contain instance methods and/or extension methods, or operators.
        private readonly bool _isStatic;

        internal RubyMethodGroupInfo(OverloadInfo/*!*/[]/*!*/ methods, RubyModule/*!*/ declaringModule, bool isStatic)
            : base(methods, RubyMemberFlags.Public, declaringModule) {
            _isStatic = isStatic;
        }

        // copy ctor
        private RubyMethodGroupInfo(RubyMethodGroupInfo/*!*/ info, RubyMemberFlags flags, RubyModule/*!*/ module)
            : base(info.MethodBases, flags, module) {
            _isStatic = info._isStatic;
        }

        // copy ctor
        private RubyMethodGroupInfo(RubyMethodGroupInfo/*!*/ info, OverloadInfo/*!*/[] methods)
            : base(methods, info.Flags, info.DeclaringModule) {
            _isStatic = info._isStatic;
        }

        protected internal override RubyMemberInfo/*!*/ Copy(RubyMemberFlags flags, RubyModule/*!*/ module) {
            return new RubyMethodGroupInfo(this, flags, module);
        }

        protected override RubyMemberInfo/*!*/ Copy(OverloadInfo/*!*/[]/*!*/ methods) {
            return new RubyMethodGroupInfo(this, methods);
        }

        internal override SelfCallConvention CallConvention {
            get { return _isStatic ? SelfCallConvention.NoSelf : SelfCallConvention.SelfIsInstance; }
        }

        internal bool IsStatic {
            get { return _isStatic; }
        }

        internal override bool ImplicitProtocolConversions {
            get { return true; }
        }

        public override MemberInfo/*!*/[]/*!*/ GetMembers() {
            return ArrayUtils.ConvertAll(MethodBases, (o) => o.ReflectionInfo);
        }

        /// <summary>
        /// A CLR method that is not overloaded has a parameter list to describe, with names - the names
        /// keywords bind to, in IronRuby's snake_case form: [[:req, :name], [:opt, :greeting], [:rest, :args]].
        /// Out parameters are results, not arguments, and are left out. An overloaded method keeps the
        /// approximation derived from its arity.
        /// </summary>
        public override RubyArray/*!*/ GetRubyParameterArray() {
            if (MethodBases.Length != 1 || DeclaringModule == null) {
                return base.GetRubyParameterArray();
            }

            var context = DeclaringModule.Context;
            var method = MethodBases[0];
            var result = new RubyArray();
            for (int i = RubyOverloadResolver.GetHiddenParameterCount(method, CallConvention); i < method.ParameterCount; i++) {
                var parameter = method.Parameters[i];
                if (parameter.IsOutParameter()) {
                    continue;
                }

                string kind = method.IsParamArray(i) ? "rest" : parameter.IsMandatory() ? "req" : "opt";
                string name = parameter.Name;
                if (string.IsNullOrEmpty(name)) {
                    result.Add(new RubyArray { context.CreateAsciiSymbol(kind) });
                } else {
                    result.Add(new RubyArray {
                        context.CreateAsciiSymbol(kind),
                        context.CreateSymbol(RubyUtils.TryMangleName(name) ?? name, RubyEncoding.UTF8)
                    });
                }
            }
            return result;
        }
        
        #region Dynamic Call

        internal override void BuildCallNoFlow(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ name) {
            var visibleOverloads = GetVisibleOverloads(args, GetReceiverOverloads(args, MethodBases), false);
            if (visibleOverloads.Count == 0) {
                metaBuilder.SetError(Methods.MakeClrProtectedMethodCalledError.OpCall(
                    args.MetaContext.Expression, args.MetaTarget.Expression, Ast.Constant(name)
                ));
            } else {
                BuildCallNoFlow(metaBuilder, args, name, visibleOverloads, CallConvention, ImplicitProtocolConversions);
            }
        }

        /// <summary>
        /// The Integer class has Int32 as its CLR type, so its CLR instance methods are Int32's. An Int64 or BigInteger is an
        /// Integer too and calls its own type's method of the same name instead: (2**40).ToString would otherwise narrow the
        /// receiver to an Int32 and raise RangeError. The rule tests the receiver's CLR type, so it is only used for that type.
        /// </summary>
        private static IList<OverloadInfo>/*!*/ GetReceiverOverloads(CallArguments/*!*/ args, OverloadInfo/*!*/[]/*!*/ overloads) {
            object target = args.Target;
            if (!(target is long || target is BigInteger)) {
                return overloads;
            }

            Type targetType = target.GetType();
            List<OverloadInfo> result = null;
            HashSet<string> retargeted = null;
            for (int i = 0; i < overloads.Length; i++) {
                var overload = overloads[i];
                if (overload.IsStatic || overload.DeclaringType.IsAssignableFrom(targetType)) {
                    if (result != null) {
                        result.Add(overload);
                    }
                    continue;
                }

                if (result == null) {
                    result = new List<OverloadInfo>(overloads.Take(i));
                    retargeted = new HashSet<string>();
                }

                if (retargeted.Add(overload.Name)) {
                    foreach (MethodInfo method in targetType.GetMember(overload.Name, MemberTypes.Method, BindingFlags.Public | BindingFlags.Instance)) {
                        result.Add(new ReflectionOverloadInfo(method));
                    }
                }
            }
            return (IList<OverloadInfo>)result ?? overloads;
        }

        #endregion
    }
}


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
using System.Diagnostics;
using System.Dynamic;
using System.Reflection;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Runtime;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using IronRuby.Compiler;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Builtins {
    using Ast = Expression;

    public partial class RubyMethod : IRubyDynamicMetaObjectProvider {
        public DynamicMetaObject/*!*/ GetMetaObject(Expression/*!*/ parameter) {
            return new Meta(parameter, BindingRestrictions.Empty, this);
        }

        internal sealed class Meta : RubyMetaObject<RubyMethod>, IConvertibleRubyMetaObject {
            public override RubyContext/*!*/ Context {
                get { return Value.Info.Context; }
            }

            protected override MethodInfo/*!*/ ContextConverter {
                get { return Methods.GetContextFromMethod; }
            }

            public Meta(Expression/*!*/ expression, BindingRestrictions/*!*/ restrictions, RubyMethod/*!*/ value)
                : base(expression, restrictions, value) {
            }

            bool IConvertibleMetaObject.CanConvertTo(Type/*!*/ type, bool @explicit) {
                return IsConvertibleTo(type, @explicit).IsConvertible;
            }

            // Conversion to a delegate, like a Proc's: a Method passed where a CLR method takes a delegate
            // (a Func<int, int> for method(:twice)) becomes one that calls it.
            // Explicit: convertible to any delegate type.
            // Implicit: convertible to a delegate type whose parameter count the method accepts.
            public Convertibility IsConvertibleTo(Type/*!*/ type, bool @explicit) {
                if (!typeof(Delegate).IsAssignableFrom(type)) {
                    return Convertibility.NotConvertible;
                }

                if (@explicit) {
                    return Convertibility.AlwaysConvertible;
                }

                MethodInfo invoke;
                if (!HasValue || (invoke = type.GetMethod("Invoke")) == null) {
                    return Convertibility.NotConvertible;
                }

                int delegateArity = invoke.GetParameters().Length;
                int arity = Value.Info.GetArity();
                return new Convertibility(
                    arity >= 0 ? delegateArity == arity : delegateArity >= -arity - 1,
                    Ast.Equal(Ast.Property(AstUtils.Convert(Expression, typeof(RubyMethod)), "Info"), AstUtils.Constant(Value.Info))
                );
            }

            public override DynamicMetaObject/*!*/ BindConvert(ConvertBinder/*!*/ binder) {
                return InteropBinder.TryBindCovertToDelegate(this, binder, Methods.CreateDelegateFromMethod) 
                    ?? base.BindConvert(binder);
            }

            public override DynamicMetaObject/*!*/ BindInvoke(InvokeBinder/*!*/ binder, DynamicMetaObject/*!*/[]/*!*/ args) {
                return InteropBinder.Invoke.Bind(binder, this, args, Value.BuildInvoke);
            }
        }
    }
}

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
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Scripting.Generation;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting.Utils;
using System.Collections.Generic;

namespace IronRuby.Compiler.Generation {
    public class RubyTypeEmitter : ClsTypeEmitter {
        private readonly FieldBuilder _immediateClassField;
        private readonly FieldBuilder _instanceDataField;

        public RubyTypeEmitter(TypeBuilder tb)
            : base(tb) {

            if (!typeof(IRubyType).IsAssignableFrom(tb.BaseType)) {
                _immediateClassField = tb.DefineField(RubyObject.ImmediateClassFieldName, typeof(RubyClass), FieldAttributes.Private);
                _instanceDataField = tb.DefineField(RubyObject.InstanceDataFieldName, typeof(RubyInstanceData), FieldAttributes.Private);
            }
        }

        internal bool IsDerivedRubyType {
            get { return _immediateClassField == null; }
        }

        internal FieldBuilder ImmediateClassField {
            get { return _immediateClassField; }
        }

        internal FieldBuilder InstanceDataField {
            get { return _instanceDataField; }
        }

        [Emitted]
        public static Exception/*!*/ InvokeMethodMissing(object o, string/*!*/ name) {
            return RubyExceptions.CreateMethodMissing(RubyContext._Default, o, name);
        }

        protected override MethodInfo MissingInvokeMethodException() {
            return typeof(RubyTypeEmitter).GetMethod("InvokeMethodMissing");
        }

        /// <summary>
        /// The result of CompareTo implemented in Ruby (compare_to, or &lt;=&gt;): nil means that the objects are not comparable,
        /// which Comparable reports as an ArgumentError; any other result is read the way Comparable reads the result of &lt;=&gt;.
        /// </summary>
        [Emitted]
        public static int ConvertCompareToResult(RubyContext/*!*/ context, object result, object self, object other) {
            if (result == null) {
                throw RubyExceptions.MakeComparisonError(context, self, other);
            }
            return Protocols.ConvertCompareResult(new ComparisonStorage(context), result);
        }

        /// <summary>
        /// The result of GetEnumerator implemented in Ruby (get_enumerator, or Enumerable#to_a): an enumerator or an enumerable.
        /// </summary>
        [Emitted]
        public static System.Collections.IEnumerator/*!*/ ConvertGetEnumeratorResult(RubyContext/*!*/ context, object result) {
            if (result is System.Collections.IEnumerator enumerator) {
                return enumerator;
            }
            if (result is System.Collections.IEnumerable enumerable) {
                return enumerable.GetEnumerator();
            }
            throw RubyExceptions.CreateImplicitConversionError(context.GetClassDisplayName(result), "System::Collections::IEnumerator");
        }

        protected override bool TryEmitResultConversion(ILGen/*!*/ il, MethodInfo/*!*/ mi, LocalBuilder/*!*/ result) {
            ParameterInfo[] ps = mi.GetParameters();
            if (mi.Name == "CompareTo" && mi.ReturnType == typeof(int) && ps.Length == 1 && !ps[0].ParameterType.IsByRef) {
                EmitImplicitContext(il);
                il.Emit(OpCodes.Ldloc, result);
                il.EmitLoadArg(0);
                il.EmitLoadArg(1);
                il.EmitBoxing(ps[0].ParameterType);
                il.EmitCall(typeof(RubyTypeEmitter).GetMethod(nameof(ConvertCompareToResult)));
                return true;
            }
            if (mi.Name == "GetEnumerator" && mi.ReturnType == typeof(System.Collections.IEnumerator) && ps.Length == 0) {
                EmitImplicitContext(il);
                il.Emit(OpCodes.Ldloc, result);
                il.EmitCall(typeof(RubyTypeEmitter).GetMethod(nameof(ConvertGetEnumeratorResult)));
                return true;
            }
            return false;
        }

        [Emitted]
        public static RubyCallAction/*!*/ MakeRubyCallSite(string/*!*/ methodName, int argumentCount) {
            // TODO: load context from class field?
            return RubyCallAction.MakeShared(methodName, new RubyCallSignature(argumentCount, RubyCallFlags.HasImplicitSelf | RubyCallFlags.IsVirtualCall));
        }

        protected override void EmitMakeCallAction(string name, int nargs, bool isList) {
            ILGen cctor = GetCCtor();
            cctor.Emit(OpCodes.Ldstr, name);
            cctor.EmitInt(nargs);
            cctor.EmitCall(typeof(RubyTypeEmitter), "MakeRubyCallSite");
        }


        #region Dynamic Conversions

        private static Dictionary<Type, CallSite> _conversionSites;

        [Emitted]
        public static CallSite<Func<CallSite, RubyContext, object, T>>/*!*/ GetConversionSite<T>() {
            if (_conversionSites == null) {
                Interlocked.CompareExchange(ref _conversionSites, new Dictionary<Type, CallSite>(), null);
            }
            Type toType = typeof(T);

            lock (_conversionSites) {
                CallSite site;
                if (_conversionSites.TryGetValue(toType, out site)) {
                    return (CallSite<Func<CallSite, RubyContext, object, T>>)site;
                }
                var newSite = CallSite<Func<CallSite, RubyContext, object, T>>.Create(ProtocolConversionAction.GetConversionAction(null, toType, true));
                _conversionSites[toType] = newSite;
                return newSite;
            }
        }

        protected override MethodInfo GetGenericConversionSiteFactory(Type toType) {
            return typeof(RubyTypeEmitter).GetMethod("GetConversionSite").MakeGenericMethod(toType);
        }

        protected override FieldInfo GetConversionSiteField(Type toType) {
            return AllocateDynamicSite(
                new Type[] { typeof(CallSite), typeof(RubyContext), typeof(object), toType },
                (site) => Expression.Assign(
                    Expression.Field(null, site),
                    Expression.Call(
                        null,
                        site.FieldType.GetMethod("Create"),
                        ProtocolConversionAction.GetConversionAction(null, toType, true).CreateExpression()
                    )
                )
            );
        }

        #endregion

        protected override void EmitImplicitContext(ILGen il) {
            il.EmitLoadArg(0);
            EmitClassObjectFromInstance(il);
            il.EmitPropertyGet(typeof(RubyModule).GetDeclaredProperty("Context"));
        }

        protected override void EmitClassObjectFromInstance(ILGen il) {
            if (typeof(IRubyObject).IsAssignableFrom(BaseType)) {
                il.EmitCall(Methods.IRubyObject_get_ImmediateClass);
            } else {
                il.EmitFieldGet(_immediateClassField);
            }
        }

        protected override Type/*!*/[]/*!*/ MakeSiteSignature(int nargs) {
            Type[] sig = new Type[nargs + 4];
            sig[0] = typeof(CallSite);
            sig[1] = typeof(RubyContext);
            for (int i = 2; i < sig.Length; i++) {
                sig[i] = typeof(object);
            }
            return sig;
        }

        protected override Type/*!*/ ContextType {
            get {
                return typeof(RubyContext);
            }
        }
    }
}

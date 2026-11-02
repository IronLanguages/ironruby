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
using System.Reflection;
using System.Text;

namespace IronRuby.Runtime.Calls {
    /// <summary>
    /// A call of a static library method through its entry point, which a <see cref="LibraryCallPlan"/> makes instead of a
    /// CallInstruction for the method's shape (see LibraryFunctionCall.Generator.rb). A CallInstruction of a method is an
    /// instantiation of a generic class over the method's parameter types, made by reflection (MakeGenericType, a constructor
    /// invoked, a delegate bound): tens of microseconds per method called, and hundreds where the instantiation needs code the
    /// JIT has to compile - more than a call made once costs otherwise. A function of reference-type parameters is called here
    /// as one of object parameters, through one compiled call per shape: a reference is passed the same way whatever its type.
    /// So each argument passed to a reference parameter is checked to be an instance of the parameter's type first, as a
    /// CallInstruction's cast would check it; a value-type argument is unboxed as the CallInstruction unboxes it.
    /// </summary>
    internal sealed unsafe partial class LibraryFunctionCall {
        private readonly IntPtr _function;
        private readonly int _shape;

        // the type of each reference parameter that an argument has to be checked against (null where any reference will do,
        // and for value types), or null if there is none
        private readonly Type[] _checkedTypes;

        private LibraryFunctionCall(IntPtr function, int shape, Type[] checkedTypes) {
            _function = function;
            _shape = shape;
            _checkedTypes = checkedTypes;
        }

        /// <summary>
        /// The call of a static non-generic method of a shape there is a call for, or null.
        /// </summary>
        internal static LibraryFunctionCall TryCreate(MethodInfo/*!*/ method, ParameterInfo/*!*/[]/*!*/ parameters) {
            if (!method.IsStatic || method.IsGenericMethod || method.ContainsGenericParameters || method.DeclaringType == null ||
                method.DeclaringType.IsGenericType || parameters.Length > MaxArity) {
                return null;
            }

            var shape = new StringBuilder(parameters.Length + 2);
            char kind;
            if (method.ReturnType == typeof(void)) {
                kind = 'V';
            } else if ((kind = GetKind(method.ReturnType)) == '\0') {
                return null;
            }
            shape.Append(kind).Append(':');

            Type[] checkedTypes = null;
            for (int i = 0; i < parameters.Length; i++) {
                var type = parameters[i].ParameterType;
                if ((kind = GetKind(type)) == '\0') {
                    return null;
                }
                shape.Append(kind);
                if (kind == 'R' && type != typeof(object)) {
                    checkedTypes ??= new Type[parameters.Length];
                    checkedTypes[i] = type;
                }
            }

            int index = GetShapeIndex(shape.ToString());
            if (index < 0) {
                return null;
            }

            IntPtr function;
            try {
                function = method.MethodHandle.GetFunctionPointer();
            } catch (Exception) {
                return null;
            }
            return (function != IntPtr.Zero) ? new LibraryFunctionCall(function, index, checkedTypes) : null;
        }

        // R: a reference type (not a pointer nor a by-ref type), I/B/D: Int32, Boolean, Double; '\0': none of these
        private static char GetKind(Type/*!*/ type) {
            if (type == typeof(int)) {
                return 'I';
            }
            if (type == typeof(bool)) {
                return 'B';
            }
            if (type == typeof(double)) {
                return 'D';
            }
            return (type.IsValueType || type.IsByRef || type.IsPointer || type.ContainsGenericParameters) ? '\0' : 'R';
        }

        /// <summary>
        /// Calls the method with <paramref name="args"/>, one for each parameter; returns its result boxed as a rule boxes it.
        /// </summary>
        internal object Invoke(object[]/*!*/ args) {
            var types = _checkedTypes;
            if (types != null) {
                for (int i = 0; i < types.Length; i++) {
                    var type = types[i];
                    if (type != null && args[i] != null && !type.IsInstanceOfType(args[i])) {
                        throw new InvalidCastException(String.Format("Unable to cast object of type '{0}' to type '{1}'.",
                            args[i].GetType(), type));
                    }
                }
            }
            return Call(_shape, _function, args);
        }
    }
}

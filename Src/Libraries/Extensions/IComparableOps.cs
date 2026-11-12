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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    // <=> answers nil for what cannot be compared, as Ruby's <=> does: nil, and whatever CompareTo rejects
    // (IComparable.CompareTo throws ArgumentException for an object of another type).

    [RubyModule(Extends = typeof(IComparable), Restrictions = ModuleRestrictions.None)]
    [Includes(typeof(Comparable))]
    public static class IComparableOps {
        [RubyMethod("<=>", RubyMethodAttributes.PublicInstance)]
        public static object Compare(IComparable/*!*/ self, object other) {
            if (other == null) {
                return null;
            }
            try {
                return ScriptingRuntimeHelpers.Int32ToObject(self.CompareTo(other));
            } catch (ArgumentException) {
                return null;
            }
        }
    }

    /// <summary>
    /// A type that implements only IComparable&lt;T&gt; is Comparable too. The module of the generic type definition
    /// is mixed into every IComparable&lt;X&gt;, and so into every type implementing one; which CompareTo applies is
    /// decided by the argument: the one whose T it is an instance of.
    /// </summary>
    [RubyModule(Extends = typeof(IComparable<>), Restrictions = ModuleRestrictions.None)]
    [Includes(typeof(Comparable))]
    public static class GenericIComparableOps {
        // the T of each IComparable<T> a type implements
        private static readonly ConcurrentDictionary<Type, Type[]>/*!*/ _comparandTypes = new ConcurrentDictionary<Type, Type[]>();

        // T -> (self, other) => ((IComparable<T>)self).CompareTo((T)other)
        private static readonly ConcurrentDictionary<Type, Func<object, object, int>>/*!*/ _comparers =
            new ConcurrentDictionary<Type, Func<object, object, int>>();

        private static readonly MethodInfo/*!*/ _compareMethod =
            typeof(GenericIComparableOps).GetMethod("CompareTo", BindingFlags.NonPublic | BindingFlags.Static);

        private static int CompareTo<T>(object self, object other) {
            return ((IComparable<T>)self).CompareTo((T)other);
        }

        private static Type[]/*!*/ GetComparandTypes(Type/*!*/ type) {
            return _comparandTypes.GetOrAdd(type, t => {
                var result = new List<Type>();
                foreach (Type iface in t.GetInterfaces()) {
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IComparable<>)) {
                        result.Add(iface.GetGenericArguments()[0]);
                    }
                }
                return result.ToArray();
            });
        }

        /// <summary>
        /// The T whose CompareTo takes <paramref name="other"/>: its own type, else one it is an instance of. A Ruby
        /// string goes to an IComparable&lt;string&gt; as a CLR string.
        /// </summary>
        private static Type FindComparandType(Type/*!*/ selfType, ref object/*!*/ other) {
            Type[] comparands = GetComparandTypes(selfType);
            Type otherType = other.GetType();
            foreach (Type comparand in comparands) {
                if (comparand == otherType) {
                    return comparand;
                }
            }
            foreach (Type comparand in comparands) {
                if (comparand.IsInstanceOfType(other)) {
                    return comparand;
                }
            }
            var str = other as MutableString;
            if (str != null && Array.IndexOf(comparands, typeof(string)) >= 0) {
                other = str.ToString();
                return typeof(string);
            }
            return null;
        }

        [RubyMethod("<=>", RubyMethodAttributes.PublicInstance)]
        public static object Compare(object/*!*/ self, object other) {
            Type comparand;
            if (other == null || (comparand = FindComparandType(self.GetType(), ref other)) == null) {
                return null;
            }

            var compare = _comparers.GetOrAdd(comparand, t =>
                (Func<object, object, int>)_compareMethod.MakeGenericMethod(t).CreateDelegate(typeof(Func<object, object, int>))
            );
            return ScriptingRuntimeHelpers.Int32ToObject(compare(self, other));
        }
    }
}

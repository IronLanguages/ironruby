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
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// What the #deconstruct and #deconstruct_keys of CLR types share.
    /// </summary>
    internal static class ClrDeconstruction {
        /// <summary>
        /// A pattern tests a value with ===, and String === accepts only Ruby strings, so a CLR string is handed
        /// to the pattern as one; `in [String => s]` would never match otherwise.
        /// </summary>
        internal static object ToPatternValue(object value) {
            string str = value as string;
            return (str != null) ? MutableString.Create(str, RubyEncoding.UTF8) : value;
        }

        /// <summary>
        /// The argument of deconstruct_keys: nil when the pattern wants every key (it has a **rest), otherwise
        /// the keys it looks up. Anything else is an error, as it is for Struct#deconstruct_keys.
        /// </summary>
        internal static IList GetRequestedKeys(RubyContext/*!*/ context, object keys) {
            if (keys == null) {
                return null;
            }
            IList list = keys as IList;
            if (list == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Array or nil)", context.GetClassDisplayName(keys));
            }
            return list;
        }

        /// <summary>
        /// The name a requested key stands for. Patterns ask with Symbols; Strings are accepted too, as
        /// Struct#deconstruct_keys accepts them. Null for any other key, which then simply matches nothing.
        /// </summary>
        internal static string GetKeyName(object key) {
            var symbol = key as RubySymbol;
            if (symbol != null) {
                return symbol.ToString();
            }
            var str = key as MutableString;
            return (str != null) ? str.ToString() : null;
        }

        /// <summary>
        /// {key:, value:} for a key-value pair. The CLR names Key and Value are accepted as well, as they are
        /// for any other CLR object's members.
        /// </summary>
        internal static Hash/*!*/ DeconstructPairKeys(RubyContext/*!*/ context, object key, object value, object keys) {
            var result = new Hash(context);
            var requested = GetRequestedKeys(context, keys);
            if (requested == null) {
                result.Add(context.CreateAsciiSymbol("key"), ToPatternValue(key));
                result.Add(context.CreateAsciiSymbol("value"), ToPatternValue(value));
                return result;
            }

            foreach (object requestedKey in requested) {
                switch (GetKeyName(requestedKey)) {
                    case "key":
                    case "Key":
                        RubyUtils.SetHashElement(context, result, requestedKey, ToPatternValue(key));
                        break;

                    case "value":
                    case "Value":
                        RubyUtils.SetHashElement(context, result, requestedKey, ToPatternValue(value));
                        break;
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Every ValueTuple and Tuple implements ITuple: they convert to an Array, so that multiple assignment
    /// (min, max = ...), a block's |a, b| and array patterns take them apart.
    /// </summary>
    [RubyModule(Extends = typeof(ITuple), Restrictions = ModuleRestrictions.None)]
    public static class ITupleOps {
        [RubyMethod("to_a")]
        [RubyMethod("to_ary")]
        public static RubyArray/*!*/ ToArray(ITuple/*!*/ self) {
            var result = new RubyArray(self.Length);
            for (int i = 0; i < self.Length; i++) {
                result.Add(self[i]);
            }
            return result;
        }

        [RubyMethod("deconstruct")]
        public static RubyArray/*!*/ Deconstruct(ITuple/*!*/ self) {
            var result = new RubyArray(self.Length);
            for (int i = 0; i < self.Length; i++) {
                result.Add(ClrDeconstruction.ToPatternValue(self[i]));
            }
            return result;
        }

        [RubyMethod("size")]
        [RubyMethod("length")]
        public static int GetLength(ITuple/*!*/ self) {
            return self.Length;
        }

        /// <summary>
        /// Array#[] with an index: counted from the end when negative, nil when out of range.
        /// </summary>
        [RubyMethod("[]")]
        public static object GetItem(ITuple/*!*/ self, [DefaultProtocol]int index) {
            if (index < 0) {
                index += self.Length;
            }
            return (index >= 0 && index < self.Length) ? self[index] : null;
        }
    }

    /// <summary>
    /// Mixed into every KeyValuePair&lt;K, V&gt; (a generic type definition module is mixed into its instantiations),
    /// so that a dictionary's #each/#map hand a block with |k, v| the key and the value. Array patterns use
    /// KeyValuePair's own Deconstruct method, which is found before anything a mixin defines.
    /// </summary>
    [RubyModule(Extends = typeof(KeyValuePair<,>), Restrictions = ModuleRestrictions.None)]
    public static class KeyValuePairOps {
        private static readonly ConcurrentDictionary<Type, Func<object, KeyValuePair<object, object>>>/*!*/ _unboxers =
            new ConcurrentDictionary<Type, Func<object, KeyValuePair<object, object>>>();

        private static readonly MethodInfo/*!*/ _unboxMethod =
            typeof(KeyValuePairOps).GetMethod("Unbox", BindingFlags.NonPublic | BindingFlags.Static);

        private static KeyValuePair<object, object> Unbox<TKey, TValue>(object pair) {
            var typed = (KeyValuePair<TKey, TValue>)pair;
            return new KeyValuePair<object, object>(typed.Key, typed.Value);
        }

        private static KeyValuePair<object, object> GetPair(RubyContext/*!*/ context, object self) {
            Type type = self != null ? self.GetType() : null;
            // the module can also be included into a Ruby class
            if (type == null || !type.IsGenericType || type.GetGenericTypeDefinition() != typeof(KeyValuePair<,>)) {
                throw RubyExceptions.CreateTypeError("{0} is not a KeyValuePair", context.GetClassDisplayName(self));
            }

            var unbox = _unboxers.GetOrAdd(type, t =>
                (Func<object, KeyValuePair<object, object>>)_unboxMethod.MakeGenericMethod(t.GetGenericArguments())
                    .CreateDelegate(typeof(Func<object, KeyValuePair<object, object>>))
            );
            return unbox(self);
        }

        [RubyMethod("to_a")]
        [RubyMethod("to_ary")]
        public static RubyArray/*!*/ ToArray(RubyContext/*!*/ context, object self) {
            var pair = GetPair(context, self);
            return new RubyArray(2) { pair.Key, pair.Value };
        }

        [RubyMethod("deconstruct_keys")]
        public static Hash/*!*/ DeconstructKeys(RubyContext/*!*/ context, object self, object keys) {
            var pair = GetPair(context, self);
            return ClrDeconstruction.DeconstructPairKeys(context, pair.Key, pair.Value, keys);
        }
    }

    /// <summary>
    /// What a non-generic IDictionary (Hashtable, ...) enumerates; behaves like KeyValuePair.
    /// </summary>
    [RubyClass(Extends = typeof(DictionaryEntry), Restrictions = ModuleRestrictions.None)]
    public static class DictionaryEntryOps {
        [RubyMethod("to_a")]
        [RubyMethod("to_ary")]
        public static RubyArray/*!*/ ToArray(DictionaryEntry self) {
            return new RubyArray(2) { self.Key, self.Value };
        }

        [RubyMethod("deconstruct")]
        public static RubyArray/*!*/ Deconstruct(DictionaryEntry self) {
            return new RubyArray(2) { ClrDeconstruction.ToPatternValue(self.Key), ClrDeconstruction.ToPatternValue(self.Value) };
        }

        [RubyMethod("deconstruct_keys")]
        public static Hash/*!*/ DeconstructKeys(RubyContext/*!*/ context, DictionaryEntry self, object keys) {
            return ClrDeconstruction.DeconstructPairKeys(context, self.Key, self.Value, keys);
        }
    }
}

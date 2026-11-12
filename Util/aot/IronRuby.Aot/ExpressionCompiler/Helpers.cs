// Ported from the Microsoft Reference Source (System.Core, MIT License); see ../../README.md.
using System; using System.Collections.Generic; using System.Collections.ObjectModel; using System.Diagnostics; using System.Linq.Expressions; using System.Reflection; using System.Reflection.Emit; using System.Runtime.CompilerServices; using Closure = IronRuby.Runtime.Precompiled.Closure; using RuntimeOps = IronRuby.Runtime.Precompiled.RuntimeOps;

/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * dlr@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System.Linq.Expressions;

using System.Collections.Generic;

namespace IronRuby.Aot.Compiler {
    // Miscellaneous helpers that don't belong anywhere else
    internal static class Helpers {

        internal static T CommonNode<T>(T first, T second, Func<T, T> parent) where T : class {
            var cmp = EqualityComparer<T>.Default;
            if (cmp.Equals(first, second)) {
                return first;
            }
            var set = new Set<T>(cmp);
            for (T t = first; t != null; t = parent(t)) {
                set.Add(t);
            }
            for (T t = second; t != null; t = parent(t)) {
                if (set.Contains(t)) {
                    return t;
                }
            }
            return null;
        }

        internal static void IncrementCount<T>(T key, Dictionary<T, int> dict) {
            int count;
            dict.TryGetValue(key, out count);
            dict[key] = count + 1;
        }
    }
}

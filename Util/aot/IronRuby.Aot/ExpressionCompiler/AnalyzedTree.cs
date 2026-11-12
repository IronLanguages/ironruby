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

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.SymbolStore;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace IronRuby.Aot.Compiler {
    internal sealed class AnalyzedTree {
        internal readonly Dictionary<object, CompilerScope> Scopes = new Dictionary<object, CompilerScope>();
        internal readonly Dictionary<LambdaExpression, BoundConstants> Constants = new Dictionary<LambdaExpression, BoundConstants>();

        internal DebugInfoGenerator DebugInfoGenerator { get; set; }

        // Created by VariableBinder
        internal AnalyzedTree() {
        }
    }
}

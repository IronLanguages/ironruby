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

namespace IronRuby.Runtime {
    /// <summary>
    /// A CLR type that is not an IRubyObject but keeps its own instance data - instance variables,
    /// frozen flag, object id, singleton class - in a field, instead of in RubyContext's table of
    /// instance data keyed by object. For a library class whose Ruby half keeps its state in
    /// instance variables (Enumerator's external iteration in ruby4.rb), that table lookup was
    /// most of the cost of every @x, and the writes missed the shape cache RubyObject's take.
    /// </summary>
    public interface IRubyInstanceDataOwner {
        /// <summary>The instance data, or null if the object has none yet.</summary>
        RubyInstanceData TryGetInstanceData();

        /// <summary>The instance data, made on first use (thread-safe).</summary>
        RubyInstanceData/*!*/ GetInstanceData();
    }
}

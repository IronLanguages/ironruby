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

using System.Threading;

namespace IronRuby.Runtime {
    /// <summary>
    /// A CLR type that is not an IRubyObject but keeps its own instance data - instance variables,
    /// frozen flag, object id, singleton class - in a field, instead of in RubyContext's table of
    /// instance data keyed by object. For a library class whose Ruby half keeps its state in
    /// instance variables (Enumerator's external iteration in ruby4.rb), that table lookup was
    /// most of the cost of every @x, and the writes missed the shape cache RubyObject's take.
    ///
    /// The field holds the data of one runtime: the first that asks for it. Each runtime has a
    /// table of its own, so an object handed to a second runtime in the same process must not
    /// show it the first one's variables or singleton class; for any other runtime the owner
    /// answers false and the runtime goes to its table, as before.
    /// </summary>
    public interface IRubyInstanceDataOwner {
        /// <summary>
        /// False when the data is not <paramref name="context"/>'s to find here (look in its table);
        /// otherwise <paramref name="data"/> is the data, null if the object has none yet.
        /// </summary>
        bool TryGetInstanceData(RubyContext/*!*/ context, out RubyInstanceData data);

        /// <summary>The data, made on first use (thread-safe); null when it is not <paramref name="context"/>'s to keep here.</summary>
        RubyInstanceData GetInstanceData(RubyContext/*!*/ context);
    }

    /// <summary>The field an <see cref="IRubyInstanceDataOwner"/> keeps: the data, and whose it is.</summary>
    public struct OwnedInstanceData {
        private sealed class Entry {
            internal readonly RubyContext/*!*/ Context;
            internal readonly RubyInstanceData/*!*/ Data;

            internal Entry(RubyContext/*!*/ context, RubyInstanceData/*!*/ data) {
                Context = context;
                Data = data;
            }
        }

        private Entry _entry;

        public bool TryGet(RubyContext/*!*/ context, out RubyInstanceData data) {
            var entry = Volatile.Read(ref _entry);
            if (entry == null) {
                data = null;
                return true;
            }
            if (entry.Context == context) {
                data = entry.Data;
                return true;
            }
            data = null;
            return false;
        }

        public RubyInstanceData GetOrCreate(RubyContext/*!*/ context) {
            var entry = Volatile.Read(ref _entry);
            if (entry == null) {
                // of two threads making it at once, one's is kept
                entry = Interlocked.CompareExchange(ref _entry, new Entry(context, RubyInstanceData.Create()), null) ?? _entry;
            }
            return (entry.Context == context) ? entry.Data : null;
        }
    }
}

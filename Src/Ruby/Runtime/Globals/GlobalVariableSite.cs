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
using System.Threading;

namespace IronRuby.Runtime {
    /// <summary>
    /// One per global variable reference in compiled code (`$x', `$x = v'). Remembers the
    /// GlobalVariable object the name denoted, stamped with the version of the context's name
    /// table it was looked up at, so a read or write of the global needs neither the dictionary
    /// nor the GlobalVariablesLock while no global has been defined, aliased or removed since.
    /// </summary>
    [CLSCompliant(false)]
    public sealed class GlobalVariableSite {
        private sealed class Binding {
            internal readonly int Version;
            // null: the name denotes no global at this version
            internal readonly GlobalVariable Variable;

            internal Binding(int version, GlobalVariable variable) {
                Version = version;
                Variable = variable;
            }
        }

        internal readonly string/*!*/ Name;

        // Version and variable travel together in one immutable object, so a reader never pairs
        // a version with the variable of another one.
        private volatile Binding _binding;

        // in the core library written in Ruby, which Ractor isolation does not apply to (RactorIsolation)
        internal readonly bool IsCoreLibrary;

        public GlobalVariableSite(string/*!*/ name)
            : this(name, false) {
        }

        public GlobalVariableSite(string/*!*/ name, bool isCoreLibrary) {
            Name = name;
            IsCoreLibrary = isCoreLibrary;
        }

        /// <summary>The variable the name denotes in the context, or null if it denotes none.</summary>
        internal GlobalVariable GetVariable(RubyContext/*!*/ context) {
            Binding binding = _binding;
            if (binding != null && binding.Version == context.GlobalVariablesVersion) {
                return binding.Variable;
            }

            GlobalVariable variable;
            int version;
            context.TryGetGlobalVariable(Name, out variable, out version);
            _binding = new Binding(version, variable);
            return variable;
        }

        /// <summary>
        /// Versions come from one process-wide counter, so a version number belongs to exactly
        /// one context and a site can never take another context's binding for its own.
        /// </summary>
        private static int _lastVersion;

        internal static int NextVersion() {
            return Interlocked.Increment(ref _lastVersion);
        }
    }
}

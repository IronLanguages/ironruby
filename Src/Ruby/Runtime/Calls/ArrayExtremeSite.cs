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

using IronRuby.Builtins;

namespace IronRuby.Runtime.Calls {

    /// <summary>
    /// `[a, b].min' and `[a, b].max' without the array, the way MRI's opt_newarray_send does it:
    /// as long as Array#min (#max) is still the library's own method, the answer is the one that
    /// method would give for the two elements, compared by the same &lt;=&gt; calls in the same order.
    /// Otherwise - the method redefined, aliased, made private, or any refinement about - the
    /// compiled code builds the array and makes the call as written.
    /// </summary>
    public sealed class ArrayExtremeSite {
        /// <summary>Returned when the call has to be made as written.</summary>
        internal static readonly object/*!*/ NotHandled = new object();

        private sealed class Binding {
            internal readonly RubyContext/*!*/ Context;
            internal readonly int Version;
            internal readonly bool Intact;
            internal readonly ComparisonStorage/*!*/ Storage;

            internal Binding(RubyContext/*!*/ context, int version, bool intact, ComparisonStorage/*!*/ storage) {
                Context = context;
                Version = version;
                Intact = intact;
                Storage = storage;
            }
        }

        private readonly bool _isMax;
        private volatile Binding _binding;

        // public, as is IsMax: the startup library (Util/aot) re-creates the site at load time
        public ArrayExtremeSite(bool isMax) {
            _isMax = isMax;
        }

        public bool IsMax => _isMax;

        internal object/*!*/ Invoke(RubyScope/*!*/ scope, object first, object second) {
            var context = scope.RubyContext;
            if (context.HasRefinements) {
                return NotHandled;
            }

            var binding = _binding;
            if (binding == null || binding.Version != RubyModule.GlobalMethodVersion || binding.Context != context) {
                _binding = binding = Bind(context, binding);
            }

            if (!binding.Intact) {
                return NotHandled;
            }

            // IListOps.GetExtreme: the current extreme is asked how it compares to the candidate.
            int compareResult = -Protocols.Compare(binding.Storage, first, second);
            return compareResult == (_isMax ? +1 : -1) ? second : first;
        }

        private Binding/*!*/ Bind(RubyContext/*!*/ context, Binding previous) {
            // read before the lookup: a change that races with it leaves the binding stale, not wrong
            int version = RubyModule.GlobalMethodVersion;
            var lookup = context.GetClass(typeof(RubyArray)).ResolveMethod(_isMax ? "max" : "min", VisibilityContext.AllVisible);
            bool intact = lookup.Found && lookup.Info.IsPublic &&
                IsLibraryMethod(lookup.Info, _isMax ? "GetMaximum" : "GetMinimum");

            var storage = (previous != null && previous.Context == context) ? previous.Storage : new ComparisonStorage(context);
            return new Binding(context, version, intact, storage);
        }

        private static bool IsLibraryMethod(RubyMemberInfo/*!*/ method, string/*!*/ clrName) {
            var libraryMethod = method as RubyLibraryMethodInfo;
            if (libraryMethod == null) {
                return false;
            }

            foreach (var member in libraryMethod.GetMembers()) {
                if (member.Name != clrName || member.DeclaringType.FullName != "IronRuby.Builtins.IListOps") {
                    return false;
                }
            }
            return true;
        }
    }
}

/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 * By using this source code in any fashion, you are agreeing to be bound by the terms of the
 * Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IronRuby.Runtime {
    /// <summary>
    /// Weakly remembers objects so that ObjectSpace.each_object can find them: the CLR cannot walk
    /// its heap, and each_object must also see objects created before it is first called, so they
    /// are recorded as they are created. Holds one weak GC handle per object - no wrapper object -
    /// and drops the handles of collected objects whenever the table fills up, so adding is
    /// amortized O(1) and the table stays proportional to the objects alive.
    /// </summary>
    internal sealed class ObjectSpaceRegistry {
        private const int InitialCapacity = 256;

        private readonly object _lock = new object();
        private IntPtr[]/*!*/ _handles = new IntPtr[InitialCapacity];
        private int _count;

        public void Add(object/*!*/ obj) {
            IntPtr handle = GCHandle.ToIntPtr(GCHandle.Alloc(obj, GCHandleType.Weak));
            lock (_lock) {
                if (_count == _handles.Length) {
                    CompactNoLock();
                }
                _handles[_count++] = handle;
            }
        }

        // Frees the handles of collected objects; grows the table if it is still more than half full
        // afterwards (so the next compaction is at least as far away as this one cost), shrinks it
        // if it is mostly empty.
        private void CompactNoLock() {
            var handles = _handles;
            int live = 0;
            for (int i = 0; i < _count; i++) {
                var handle = GCHandle.FromIntPtr(handles[i]);
                if (handle.Target == null) {
                    handle.Free();
                } else {
                    handles[live++] = handles[i];
                }
            }
            Array.Clear(handles, live, _count - live);
            _count = live;

            if (live > handles.Length / 2) {
                Array.Resize(ref _handles, handles.Length * 2);
            } else if (live < handles.Length / 8 && handles.Length > InitialCapacity) {
                Array.Resize(ref _handles, handles.Length / 2);
            }
        }

        /// <summary>
        /// The recorded objects that have not been collected yet.
        /// </summary>
        public List<object>/*!*/ GetObjects() {
            lock (_lock) {
                var result = new List<object>(_count);
                for (int i = 0; i < _count; i++) {
                    object target = GCHandle.FromIntPtr(_handles[i]).Target;
                    if (target != null) {
                        result.Add(target);
                    }
                }
                return result;
            }
        }

        ~ObjectSpaceRegistry() {
            for (int i = 0; i < _count; i++) {
                GCHandle.FromIntPtr(_handles[i]).Free();
            }
        }
    }
}

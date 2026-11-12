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
using System.Runtime.CompilerServices;
using IronRuby.Builtins;

namespace IronRuby.Runtime {

    /// <summary>
    /// Where an object was allocated, as ObjectSpace.allocation_sourcefile and friends report it.
    /// </summary>
    public sealed class AllocationSite {
        public readonly string Path;
        public readonly int Line;
        /// <summary>The class the allocating method was defined in, null outside a method body.</summary>
        public readonly string ClassPath;
        /// <summary>The allocating method's name, null outside a method body.</summary>
        public readonly string MethodName;
        public readonly int Generation;

        internal AllocationSite(string path, int line, string classPath, string methodName, int generation) {
            Path = path;
            Line = line;
            ClassPath = classPath;
            MethodName = methodName;
            Generation = generation;
        }
    }

    /// <summary>
    /// The objspace library's bookkeeping, for the two things MRI gets from walking its heap and
    /// from a per-object allocation record:
    ///
    ///  * ObjectSpace.dump_all / memsize_of_all need a list of the objects that exist. The CLR heap
    ///    cannot be walked, so the objects are remembered weakly as they are created - like
    ///    <see cref="RubyContext.ObjectSpaceObjects"/>, but for the builtin types too, and turned
    ///    on by `require "objspace"` rather than by -X:ObjectSpace.
    ///  * ObjectSpace.trace_object_allocations needs the source location of each allocation.
    ///    Rather than build a stack trace per object - which would cost far more than MRI's record
    ///    - the location is taken from the TracePoint :line hook of the statement being run:
    ///    tracing forces the :line event on (<see cref="TracePoint.ForceEvents"/>) and each hook
    ///    leaves its file and line here for the next allocation to pick up.
    ///
    /// Everything is off, and costs one static field read per allocation, until the library asks
    /// for it. The allocation points are the constructors of <see cref="MutableString"/>,
    /// <see cref="RubyArray"/>, <see cref="Hash"/> and <see cref="RubyObject"/>: objects of other
    /// types (a Proc, a Range, a CLR object) are not recorded.
    /// </summary>
    public static class ObjectTracking {
        /// <summary>
        /// True while either sink wants allocations; read at every allocation point, hence public.
        /// </summary>
        public static bool Enabled;

        private static readonly object _lock = new object();
        private static bool _trackObjects;
        private static int _traceDepth;
        private static ObjectSpaceRegistry _objects;
        private static ConditionalWeakTable<object, AllocationSite>/*!*/ _sites = new ConditionalWeakTable<object, AllocationSite>();

        // The statement being run on this thread, as its :line hook reported it.
        [ThreadStatic] private static string _path;
        [ThreadStatic] private static int _line;
        [ThreadStatic] private static RubyScope _scope;
        [ThreadStatic] private static AllocationSite _cachedSite;
        [ThreadStatic] private static bool _inTracker;

        #region Object registry (dump_all, memsize_of_all)

        /// <summary>
        /// Starts or stops remembering the objects as they are created. What was recorded is kept
        /// when it stops, so that a later dump_all still sees it.
        /// </summary>
        public static void TrackObjects(bool enable) {
            lock (_lock) {
                if (enable && _objects == null) {
                    _objects = new ObjectSpaceRegistry();
                }
                _trackObjects = enable;
                Enabled = enable || _traceDepth > 0;
            }
        }

        public static bool IsTrackingObjects {
            get { return _trackObjects; }
        }

        /// <summary>
        /// The recorded objects that are still alive; empty when nothing was ever recorded.
        /// </summary>
        public static List<object>/*!*/ GetTrackedObjects() {
            var registry = _objects;
            return registry != null ? registry.GetObjects() : new List<object>();
        }

        #endregion

        #region Allocation sites (trace_object_allocations)

        public static bool IsTracingAllocations {
            get { return _traceDepth > 0; }
        }

        /// <summary>
        /// trace_object_allocations_start; nests, and only the outermost start switches the
        /// :line event on.
        /// </summary>
        public static void StartTracingAllocations() {
            lock (_lock) {
                if (_traceDepth++ == 0) {
                    Enabled = true;
                    TracePoint.ForceEvents(TraceEvents.Line, true);
                }
            }
        }

        /// <summary>
        /// trace_object_allocations_stop. More stops than starts is not an error in MRI.
        /// </summary>
        public static void StopTracingAllocations() {
            lock (_lock) {
                if (_traceDepth == 0) {
                    return;
                }
                if (--_traceDepth == 0) {
                    TracePoint.ForceEvents(TraceEvents.Line, false);
                    Enabled = _trackObjects;
                }
            }
        }

        public static void ClearAllocationSites() {
            _sites = new ConditionalWeakTable<object, AllocationSite>();
        }

        /// <summary>
        /// Where <paramref name="obj"/> was allocated, or null if it was not created while tracing
        /// (an immediate never is).
        /// </summary>
        public static AllocationSite GetAllocationSite(object obj) {
            if (obj == null) {
                return null;
            }
            AllocationSite site;
            return _sites.TryGetValue(obj, out site) ? site : null;
        }

        /// <summary>
        /// The :line hook of every statement, while allocations are traced.
        /// </summary>
        internal static void SetCurrentLine(RubyScope scope, string path, int line) {
            _path = path;
            _line = line;
            _scope = scope;
            if (scope != null) {
                scope.MarkCaptured();
            }
            _cachedSite = null;
        }

        #endregion

        /// <summary>
        /// Records a newly created object. Only called when <see cref="Enabled"/>.
        /// </summary>
        public static void Track(object/*!*/ obj) {
            if (_inTracker) {
                return;
            }
            _inTracker = true;
            try {
                var registry = _objects;
                if (registry != null && _trackObjects) {
                    registry.Add(obj);
                }

                if (_traceDepth > 0) {
                    var site = GetCurrentSite();
                    if (site != null) {
                        _sites.AddOrUpdate(obj, site);
                    }
                }
            } finally {
                _inTracker = false;
            }
        }

        // One site object per statement per thread: an allocating loop body reuses it.
        private static AllocationSite GetCurrentSite() {
            if (_path == null) {
                return null;
            }
            var site = _cachedSite;
            if (site == null) {
                string classPath = null, methodName = null;
                // MRI reports the class and method of the allocating frame; a block or the top
                // level has neither (see the trace_object_allocations specs).
                var methodScope = _scope as RubyMethodScope;
                if (methodScope != null) {
                    var declaringModule = methodScope.DeclaringModule;
                    classPath = declaringModule != null ? declaringModule.Name : null;
                    methodName = methodScope.DefinitionName;
                }
                site = _cachedSite = new AllocationSite(_path, _line, classPath, methodName, GC.CollectionCount(0));
            }
            return site;
        }
    }
}

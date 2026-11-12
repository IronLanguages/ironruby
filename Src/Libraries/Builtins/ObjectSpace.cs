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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using System.Runtime.CompilerServices;

namespace IronRuby.Builtins {
    /// <summary>
    /// What a C extension's rb_data_type_t.dsize function reports: the bytes an object holds
    /// outside its slot.  ObjectSpace.memsize_of adds it to the slot, as MRI does.
    /// </summary>
    internal interface IMemorySized {
        long ExtraMemorySize { get; }
    }

    [RubyModule("ObjectSpace")]
    public static class ObjectSpace {
        #region define_finalizer, undefine_finalizer

        /// <summary>
        /// The finalizers of one object. It hangs off the object, so the CLR finalizes it once the object
        /// is gone; the context runs whatever is left at exit. Each finalizer is called with the object's id.
        /// </summary>
        private sealed class FinalizerInvoker : IExitFinalizer, IPerObjectState {
            public const string InstanceVariableName = "<FINALIZER>";

            private readonly RubyContext/*!*/ _context;
            private readonly CallSite<Func<CallSite, object, object, object>>/*!*/ _callSite;
            private readonly List<object>/*!*/ _finalizers = new List<object>();
            private readonly object _objectId;
            private int _ran;

            public FinalizerInvoker(RubyContext/*!*/ context, CallSite<Func<CallSite, object, object, object>>/*!*/ callSite, object objectId) {
                Assert.NotNull(context, callSite);
                _context = context;
                _callSite = callSite;
                _objectId = objectId;
            }

            public List<object>/*!*/ Finalizers {
                get { return _finalizers; }
            }

            // dup and clone copy the finalizers: they run once for each object, with its own id
            public object CopyFor(object copy) {
                var result = new FinalizerInvoker(_context, _callSite, ClrInteger.Narrow(RubyUtils.GetObjectId(_context, copy)));
                lock (_finalizers) {
                    result._finalizers.AddRange(_finalizers);
                }
                _context.RegisterExitFinalizer(result);
                return result;
            }

            ~FinalizerInvoker() {
                // Not Run(): see the comment on _pending. The CLR finalizer thread only parks the
                // work, which resurrects this object into the queue until the main thread runs it.
                Park(this);
            }

            public void RunAtExit() {
                Run();
                GC.SuppressFinalize(this);
            }

            /// <summary>Runs the finalizers on the caller's thread. Only the main thread calls it.</summary>
            internal void RunOnMainThread() {
                Run();
            }

            internal RubyContext/*!*/ Context {
                get { return _context; }
            }

            public void Cancel() {
                Interlocked.Exchange(ref _ran, 1);
                GC.SuppressFinalize(this);
            }

            private void Run() {
                if (Interlocked.Exchange(ref _ran, 1) != 0) {
                    return;
                }

                object[] finalizers;
                lock (_finalizers) {
                    finalizers = _finalizers.ToArray();
                }
                foreach (var finalizer in finalizers) {
                    try {
                        _callSite.Target(_callSite, finalizer, _objectId);
                    } catch (SystemExit) {
                        // `exit' in a finalizer ends that finalizer, not the others
                    } catch (Exception e) {
                        _context.ReportFinalizerException(finalizer, e);
                    }
                }
            }
        }

        /// <summary>
        /// Finalizers waiting for the main thread.
        ///
        /// A Ruby finalizer is arbitrary Ruby code, and the CLR runs ~FinalizerInvoker on its own
        /// finalizer thread. Running the Ruby there means it mutates the very tables the main
        /// thread is in the middle of using, with no lock between them: a Ruby Hash *is* a
        /// Dictionary&lt;object,object&gt;, so two threads writing one leaves a corrupted dictionary
        /// that throws "Operations that change non-concurrent collections must have exclusive
        /// access" for the rest of the process. Tempfile's own finalizer does exactly this - it
        /// deletes from a Hash the main thread writes to - which made `gem install` fail at random
        /// with every later gemspec reported as corrupt.
        ///
        /// MRI does not have the problem: it runs finalizers on the main thread at an interrupt
        /// check. PosixSignals already parks POSIX signals here for the same reason, and this is
        /// the same mechanism - park the work, let the main thread pick it up at a safe point
        /// (a blocking call, Thread.pass, Kernel#sleep) or at GC.start, and run whatever is left
        /// at exit.
        /// </summary>
        private static readonly Queue<FinalizerInvoker>/*!*/ _pending = new Queue<FinalizerInvoker>();

        /// <summary>Guards against a finalizer whose own safe point would re-enter the drain.</summary>
        [ThreadStatic]
        private static bool _draining;

        private static void Park(FinalizerInvoker/*!*/ invoker) {
            lock (_pending) {
                _pending.Enqueue(invoker);
                RubyUtils.RequestSafePoint();
            }
        }

        /// <summary>
        /// A safe point on the main thread: runs the finalizers the CLR's finalizer thread parked.
        /// Installed into RubyUtils.FinalizerHandler the first time a finalizer is defined.
        /// </summary>
        internal static void RunPendingFinalizers() {
            if (_draining) {
                return;
            }

            _draining = true;
            try {
                while (true) {
                    FinalizerInvoker invoker;
                    lock (_pending) {
                        if (_pending.Count == 0) {
                            return;
                        }
                        invoker = _pending.Peek();
                        // Only the thread the finalizer's own context calls "main" runs it, so that
                        // a worker thread's safe point does not pick up another context's work.
                        if (invoker.Context.MainThread != Thread.CurrentThread) {
                            return;
                        }
                        _pending.Dequeue();
                        RubyUtils.SafePointRequestDone();
                    }
                    invoker.RunOnMainThread();
                }
            } finally {
                _draining = false;
            }
        }

        /// <summary>
        /// GC.start runs finalizers in MRI, so it drains here too - after waiting for the CLR to
        /// hand the collected objects' invokers over.
        /// </summary>
        internal static void CollectAndRunFinalizers() {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            RunPendingFinalizers();
        }

        /// <summary>
        /// The finalizer may be given as a block instead of an argument.
        /// </summary>
        [RubyMethod("define_finalizer", RubyMethodAttributes.PublicSingleton)]
        public static object DefineFinalizer(RespondToStorage/*!*/ respondTo, BinaryOpStorage/*!*/ call, BinaryOpStorage/*!*/ equals,
            [NotNull]BlockParam/*!*/ block, RubyModule/*!*/ self, object obj) {

            return DefineFinalizer(respondTo, call, equals, self, obj, block.Proc);
        }

        [RubyMethod("define_finalizer", RubyMethodAttributes.PublicSingleton)]
        public static object DefineFinalizer(RespondToStorage/*!*/ respondTo, BinaryOpStorage/*!*/ call, BinaryOpStorage/*!*/ equals,
            RubyModule/*!*/ self, object obj, object finalizer) {

            var context = respondTo.Context;
            if (!Protocols.RespondTo(respondTo, finalizer, "call")) {
                throw RubyExceptions.CreateArgumentError("finalizer should be callable (respond to :call)");
            }
            // an immediate - nil, true, a Symbol, a small Integer - is never collected
            if (obj == null || obj is bool || obj is RubySymbol || !RubyUtils.HasObjectState(obj)) {
                throw RubyExceptions.CreateArgumentError("cannot define finalizer for {0}", context.GetClassDisplayName(obj));
            }
            if (context.IsObjectFrozen(obj)) {
                throw RubyExceptions.CreateObjectFrozenError(context, obj);
            }

            // A finalizer that holds on to the object keeps it alive for good, so it only runs at exit.
            if (ReferencesObject(finalizer, obj)) {
                context.ReportWarning("finalizer references object to be finalized");
            }

            RubyUtils.FinalizerHandler = RunPendingFinalizers;

            FinalizerInvoker invoker;
            object existing;
            if (context.TryGetInstanceVariable(obj, FinalizerInvoker.InstanceVariableName, out existing) && existing is FinalizerInvoker) {
                invoker = (FinalizerInvoker)existing;
            } else {
                invoker = new FinalizerInvoker(context, call.GetCallSite("call"),
                    // the id as #object_id answers it - an Integer, which a Hash keyed by object_id can find
                    ClrInteger.Narrow(RubyUtils.GetObjectId(context, obj)));
                context.SetInstanceVariable(obj, FinalizerInvoker.InstanceVariableName, invoker);
                context.RegisterExitFinalizer(invoker);
            }

            // the same finalizer - by #== - is defined once, and answering for it is the one defined first
            lock (invoker.Finalizers) {
                foreach (var defined in invoker.Finalizers) {
                    if (Protocols.IsEqual(equals, defined, finalizer)) {
                        finalizer = defined;
                        goto done;
                    }
                }
                invoker.Finalizers.Add(finalizer);
            }

          done:
            RubyArray result = new RubyArray(2);
            result.Add(0);
            result.Add(finalizer);
            return result;
        }

        private static bool ReferencesObject(object finalizer, object obj) {
            if (ReferenceEquals(finalizer, obj)) {
                return true;
            }
            var proc = finalizer as Proc;
            if (proc != null) {
                return ReferenceEquals(proc.Self, obj);
            }
            var method = finalizer as RubyMethod;
            if (method != null) {
                return ReferenceEquals(method.Target, obj);
            }
            return false;
        }

        [RubyMethod("undefine_finalizer", RubyMethodAttributes.PublicSingleton)]
        public static object UndefineFinalizer(RubyContext/*!*/ context, RubyModule/*!*/ self, object obj) {
            if (context.IsObjectFrozen(obj)) {
                throw RubyExceptions.CreateObjectFrozenError(context, obj);
            }

            object invokerObj;
            if (context.TryRemoveInstanceVariable(obj, FinalizerInvoker.InstanceVariableName, out invokerObj)) {
                var invoker = invokerObj as FinalizerInvoker;
                if (invoker != null) {
                    invoker.Cancel();
                }
            }
            return obj;
        }

        #endregion

        // MRI walks its heap. The CLR cannot, so IronRuby records what each_object can find as it is
        // created (RubyContext.ObjectSpaceModules/Objects): modules and classes, and - with
        // -X:ObjectSpace - the instances of Ruby classes deriving from Object or BasicObject. What is
        // not recorded - instances of the builtin types (String, Array, Exception, ...), or any object
        // without -X:ObjectSpace - is asked for with an error rather than an empty walk, as in JRuby.
        [RubyMethod("each_object", RubyMethodAttributes.PublicSingleton)]
        public static Enumerator/*!*/ GetEachObjectEnumerator(RubyModule/*!*/ self, [Optional]RubyModule theModule) {
            return theModule != null ? new Enumerator(self, "each_object", theModule) : new Enumerator(self, "each_object");
        }

        [RubyMethod("each_object", RubyMethodAttributes.PublicSingleton)]
        public static object EachObject([NotNull]BlockParam/*!*/ block, RubyModule/*!*/ self, [Optional]RubyModule theModule) {
            var context = self.Context;
            var objects = context.GetObjectSpaceObjects();
            if (objects == null) {
                if (theModule == null || !OnlyModulesAreInstances(theModule)) {
                    throw RubyExceptions.CreateRuntimeError(
                        "each_object only supported for modules and classes unless ObjectSpace is enabled (-X:ObjectSpace)"
                    );
                }
            } else if (theModule != null && !CanFindInstances(theModule)) {
                throw RubyExceptions.CreateRuntimeError(
                    "each_object only supported for modules, classes and objects of classes derived from Object or BasicObject"
                );
            }

            int matches = 0;
            foreach (var list in new[] { context.GetObjectSpaceModules(), objects }) {
                if (list == null) {
                    continue;
                }
                foreach (object obj in list) {
                    var module = obj as RubyModule;
                    if (module != null && IsHidden(module)) {
                        continue;
                    }

                    if (theModule == null || context.IsKindOf(obj, theModule)) {
                        matches++;

                        object result;
                        if (block.Yield(obj, out result)) {
                            return result;
                        }
                    }
                }
            }
            return matches;
        }

        // The dummy singleton class that ends a chain of singleton classes has no MRI counterpart. And
        // MRI hides the singleton class of a class until it has a singleton class of its own
        // (rb_singleton_class_internal_p) - it gets one when Kernel#singleton_class exposes it; IronRuby
        // creates the singleton class of every class eagerly, with a dummy one after it.
        private static bool IsHidden(RubyModule/*!*/ module) {
            if (module.IsDummySingletonClass) {
                return true;
            }
            var cls = module as RubyClass;
            return cls != null && cls.IsSingletonClass && cls.SingletonClassOf is RubyClass && cls.ImmediateClass.IsDummySingletonClass;
        }

        // Module, Class, singleton classes of modules - and modules, which may extend a module.
        private static bool OnlyModulesAreInstances(RubyModule/*!*/ module) {
            var cls = module as RubyClass;
            return cls == null || cls.HasAncestor(module.Context.ModuleClass);
        }

        private static bool CanFindInstances(RubyModule/*!*/ module) {
            if (OnlyModulesAreInstances(module)) {
                return true;
            }
            var cls = (RubyClass)module;
            while (cls.IsSingletonClass) {
                cls = cls.SuperClass;
            }
            Type type = cls.GetUnderlyingSystemType();
            return type == typeof(object) || typeof(RubyObject).IsAssignableFrom(type);
        }

        // Deprecated in 4.0 but still working. Only an id MRI computes (nil, true, false, an Integer)
        // or one of the objects each_object can find (with -X:ObjectSpace for anything but modules)
        // is turned back into its object: IronRuby keeps no table from ids to the other objects.
        [RubyMethod("_id2ref", RubyMethodAttributes.PublicSingleton)]
        public static object IdToReference(RubyModule/*!*/ self, [DefaultProtocol]IntegerValue id) {
            var context = self.Context;
            context.ReportDeprecationWarning("ObjectSpace._id2ref is deprecated");

            if (id.IsFixnum) {
                int value = id.Fixnum;
                if (value == RubyUtils.NilObjectId) {
                    return null;
                } else if (value == RubyUtils.TrueObjectId) {
                    return ScriptingRuntimeHelpers.True;
                } else if (value == RubyUtils.FalseObjectId) {
                    return ScriptingRuntimeHelpers.False;
                }

                foreach (var list in new[] { context.GetObjectSpaceModules(), context.GetObjectSpaceObjects() }) {
                    if (list == null) {
                        continue;
                    }
                    foreach (object obj in list) {
                        // an object whose id has not been asked for yet has no instance data, and it is not created here
                        var rubyObject = obj as IRubyObject;
                        var data = rubyObject != null ? rubyObject.TryGetInstanceData() : null;
                        if (data != null && data.HasObjectId && RubyUtils.GetObjectId(context, obj) == value) {
                            return obj;
                        }
                    }
                }

                // The ids IronRuby hands out to objects are consecutive, so an odd one is not
                // necessarily an Integer's (2n + 1) as in MRI: the objects were looked at first.
                if ((value & 1) != 0) {
                    return ScriptingRuntimeHelpers.Int32ToObject(value >> 1);
                }
            }

            throw RubyExceptions.CreateRangeError(String.Format("\"{0}\" is not an id value", id.IsFixnum ? id.Fixnum.ToString() : id.Bignum.ToString()));
        }

        // GC.start's keywords (full_mark:, immediate_sweep:) have no CLR counterpart and are ignored
        [RubyMethod("garbage_collect", RubyMethodAttributes.PublicSingleton)]
        public static void GarbageCollect(RubyModule/*!*/ self, [Keywords, Optional]IDictionary<object, object> options) {
            GC.Collect();
        }

        #region objspace library primitives

        // What `require "objspace"` adds to ObjectSpace is Ruby (Src/StdLib/ironruby/objspace.rb);
        // only what needs the runtime is here, private and under reserved names so that ObjectSpace
        // looks untouched until the library is required.

        /// <summary>
        /// Starts or stops remembering objects as they are created, which is what dump_all and
        /// memsize_of_all walk in place of MRI's heap. Switched on by `require "objspace"`.
        /// </summary>
        [RubyMethod("__track_objects__", RubyMethodAttributes.PrivateSingleton)]
        public static void TrackObjects(RubyModule/*!*/ self, bool enable) {
            ObjectTracking.TrackObjects(enable);
        }

        /// <summary>
        /// The objects ObjectSpace knows of: every module and class, whatever the allocation
        /// tracking has recorded, and what each_object can see with -X:ObjectSpace.
        /// </summary>
        [RubyMethod("__tracked_objects__", RubyMethodAttributes.PrivateSingleton)]
        public static RubyArray/*!*/ GetTrackedObjects(RubyModule/*!*/ self) {
            var result = new RubyArray();
            foreach (object obj in EnumerateKnownObjects(self.Context)) {
                result.Add(obj);
            }
            return result;
        }

        private static IEnumerable<object>/*!*/ EnumerateKnownObjects(RubyContext/*!*/ context) {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (object obj in context.GetObjectSpaceModules()) {
                var module = obj as RubyModule;
                if (module != null && IsHidden(module)) {
                    continue;
                }
                if (seen.Add(obj)) {
                    yield return obj;
                }
            }

            foreach (object obj in ObjectTracking.GetTrackedObjects()) {
                if (seen.Add(obj)) {
                    yield return obj;
                }
            }

            var objects = context.GetObjectSpaceObjects();
            if (objects != null) {
                foreach (object obj in objects) {
                    if (seen.Add(obj)) {
                        yield return obj;
                    }
                }
            }
        }

        [RubyMethod("__memsize_of__", RubyMethodAttributes.PrivateSingleton)]
        public static object/*!*/ GetMemorySize(RubyModule/*!*/ self, object obj) {
            return ClrInteger.Narrow(MemorySize(self.Context, obj));
        }

        [RubyMethod("__memsize_of_all__", RubyMethodAttributes.PrivateSingleton)]
        public static object/*!*/ GetMemorySizeOfAll(RubyModule/*!*/ self, [Optional]RubyModule theModule) {
            var context = self.Context;
            long total = 0;
            foreach (object obj in EnumerateKnownObjects(context)) {
                if (theModule == null || context.IsKindOf(obj, theModule)) {
                    total += MemorySize(context, obj);
                }
            }
            return ClrInteger.Narrow(total);
        }

        // MRI reports what the object occupies in its heap; there is no such number on .NET, so
        // this estimates it the same way: an object header of one slot, plus what the payload
        // needs beyond what fits in the slot. The size is only meant to be compared with the size
        // of another object of the same kind, which is all the objspace specs ask of it.
        private const int SlotSize = 40;

        private static long MemorySize(RubyContext/*!*/ context, object obj) {
            // an immediate is its own value - it occupies no slot
            if (obj == null || obj is bool || obj is int || obj is RubySymbol) {
                return 0;
            }

            var str = obj as MutableString;
            if (str != null) {
                int count;
                try {
                    count = str.GetByteCount();
                } catch (Exception) {
                    count = str.GetCharCount() * 2;
                }
                // a short string lives in the slot (MRI embeds up to 23 bytes)
                return SlotSize + (count > 23 ? count + 1 : 0);
            }

            var array = obj as RubyArray;
            if (array != null) {
                return SlotSize + (array.Count > 3 ? (long)array.Count * IntPtr.Size : 0);
            }

            var hash = obj as Hash;
            if (hash != null) {
                return SlotSize + (long)hash.Count * 5 * IntPtr.Size;
            }

            long size = SlotSize;
            var sized = obj as IMemorySized;
            if (sized != null) {
                size += sized.ExtraMemorySize;
            }

            var module = obj as RubyModule;
            if (module != null) {
                // a module carries its method and constant tables
                size += 3 * SlotSize;
            }

            size += (long)context.GetInstanceVariableNames(obj).Length * IntPtr.Size;
            return size;
        }

        /// <summary>
        /// The objects directly reachable from <paramref name="obj"/>: its class, its instance
        /// variables, and whatever a container holds. Null for an immediate, as in MRI. Unlike
        /// MRI's this does not see what a CLR object keeps in its fields, beyond the containers
        /// known here.
        /// </summary>
        [RubyMethod("__reachable_objects_from__", RubyMethodAttributes.PrivateSingleton)]
        public static RubyArray GetReachableObjectsFrom(RubyModule/*!*/ self, object obj) {
            var context = self.Context;
            if (obj == null || obj is bool || obj is int || obj is RubySymbol) {
                return null;
            }

            var found = new List<object>();
            found.Add(context.GetClassOf(obj));

            var array = obj as RubyArray;
            if (array != null) {
                found.AddRange(array);
            }

            var hash = obj as Hash;
            if (hash != null) {
                foreach (var entry in hash) {
                    found.Add(entry.Key);
                    found.Add(entry.Value);
                }
            }

            var queue = obj as IronRuby.StandardLibrary.Threading.RubyQueue;
            if (queue != null) {
                found.AddRange(queue.GetContents());
            }

            foreach (string name in context.GetInstanceVariableNames(obj)) {
                object value;
                if (context.TryGetInstanceVariable(obj, name, out value)) {
                    found.Add(value);
                }
            }

            // MRI leaves out what has no heap slot of its own
            var result = new RubyArray(found.Count);
            foreach (object item in found) {
                if (item != null && !(item is bool) && !(item is int) && !(item is RubySymbol)) {
                    result.Add(item);
                }
            }
            return result;
        }

        [RubyMethod("__address_of__", RubyMethodAttributes.PrivateSingleton)]
        public static object/*!*/ GetAddress(RubyModule/*!*/ self, object obj) {
            return ClrInteger.Narrow(RubyUtils.GetObjectId(self.Context, obj));
        }

        [RubyMethod("__trace_start__", RubyMethodAttributes.PrivateSingleton)]
        public static void StartTracingAllocations(RubyModule/*!*/ self) {
            ObjectTracking.StartTracingAllocations();
        }

        [RubyMethod("__trace_stop__", RubyMethodAttributes.PrivateSingleton)]
        public static void StopTracingAllocations(RubyModule/*!*/ self) {
            ObjectTracking.StopTracingAllocations();
        }

        [RubyMethod("__trace_clear__", RubyMethodAttributes.PrivateSingleton)]
        public static void ClearAllocationSites(RubyModule/*!*/ self) {
            ObjectTracking.ClearAllocationSites();
        }

        /// <summary>
        /// [sourcefile, sourceline, class_path, method_id, generation] of the object's allocation,
        /// or nil if it was not created while allocations were traced.
        /// </summary>
        [RubyMethod("__allocation_info__", RubyMethodAttributes.PrivateSingleton)]
        public static RubyArray GetAllocationInfo(RubyModule/*!*/ self, object obj) {
            var site = ObjectTracking.GetAllocationSite(obj);
            if (site == null) {
                return null;
            }

            var context = self.Context;
            var result = new RubyArray(5);
            result.Add(site.Path != null ? context.EncodePath(site.Path) : null);
            result.Add(ScriptingRuntimeHelpers.Int32ToObject(site.Line));
            result.Add(site.ClassPath != null ? MutableString.CreateMutable(site.ClassPath, context.GetIdentifierEncoding()) : null);
            result.Add(site.MethodName != null ? context.CreateSymbol(site.MethodName, context.GetIdentifierEncoding()) : null);
            result.Add(ScriptingRuntimeHelpers.Int32ToObject(site.Generation));
            return result;
        }

        #endregion
    }
}

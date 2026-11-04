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

using System.Collections.Generic;
using Microsoft.Scripting;
using System.Threading;
using IronRuby.Builtins;
using Microsoft.Scripting.Utils;
using System.Diagnostics;
using System;

namespace IronRuby.Runtime {
    /// <summary>
    /// Stores the per-instance data that all Ruby objects need (frozen?, tainted?, untrusted?, instance_variables, etc)
    /// Stored in a lookaside weak hashtable for types that don't implement IRubyObject (i.e. .NET types).
    /// </summary>
    /// <remarks>
    /// INSTANCE VARIABLES
    ///
    /// The variables live in <see cref="_ivars"/>: element 0 is the object's
    /// <see cref="InstanceVariableShape"/>, element i (1 &lt;= i &lt;= shape.Count) the value of the
    /// shape's i-th variable, and the elements past that are spare capacity. The shape and the
    /// slots it describes are one array, so one reference read gives a reader a consistent pair:
    /// element 0 always describes the layout of the array it is in. A site that has seen the shape
    /// before reads `s[0] == cachedShape ? s[cachedIndex]` - no lock, no hashing.
    ///
    /// There are no locks on reads, on writes of an existing variable, or on adding a variable
    /// while the array has room. The operations, and why they are safe under the CLR memory model
    /// (a Volatile.Write is a release, a Volatile.Read an acquire, an Interlocked operation a full
    /// fence; plain accesses promise nothing more - in particular a plain store followed by a
    /// plain load of another location may be reordered, even on x64):
    ///
    /// * Read. s = _ivars; shape = Volatile.Read(s[0]); value = s[i]. The acquire on s[0] orders
    ///   the slot read after it, so a reader that sees a shape published by an add also sees the
    ///   value the add stored before publishing it.
    ///
    /// * Write of an existing variable. s[i] = v, then re-read s[0]; if it is no longer the shape
    ///   the write was made for, redo the write the slow way. Never loses a write - see Move.
    ///
    /// * Add while the array has room. CAS s[0] from S to <see cref="InstanceVariableShape.Busy"/>
    ///   (so two adders cannot both claim slot S.Count + 1), store the value, Volatile.Write s[0]
    ///   = S'. Concurrent writers of other variables store into other slots, and see the shape
    ///   change on their re-read (a harmless redo). Anyone who sees Busy spins: the window is two
    ///   stores long.
    ///
    /// * Move: growing past the capacity, removing a variable, going "too complex". Done under
    ///   lock(this), so movers are serialized. CAS s[0] from S to
    ///   <see cref="InstanceVariableShape.Moved"/>, then
    ///   <see cref="Interlocked.MemoryBarrierProcessWide"/>, then copy the slots into the new
    ///   array and publish it with a release write of _ivars. The old array is dead from then on
    ///   - its element 0 stays Moved - and anyone who meets Moved waits for the lock and reloads.
    ///   A writer that stored into the old array without a fence of its own is not lost: the
    ///   process-wide barrier runs a full fence on every thread, so for each writer either its
    ///   store is globally visible before the copy (and copied), or its re-read of s[0] comes
    ///   after the barrier and sees Moved (and it redoes the write into the new array). This is
    ///   the asymmetric Dekker pattern: the rare side (a move) pays for the fence so that the hot
    ///   side (every ivar write) does not. A move of an array with no slots needs none of that -
    ///   nobody can be writing into it (it may be the shared EmptySlots) - and is a CAS on _ivars.
    ///
    /// Removing a variable moves rather than leaving a hole in place because a hole cannot be
    /// reused safely: a late writer of the removed variable could land its store in the slot
    /// after a new variable took it.
    ///
    /// Initial capacity comes from the class (<see cref="RubyClass.InstanceVariableCapacity"/>
    /// remembers how many variables its instances got), so an object normally never moves.
    ///
    /// A too-complex object's array is { Complex, ComplexInstanceVariables } and every operation
    /// on it takes lock(this).
    /// </remarks>
    public sealed class RubyInstanceData : IRubyObjectState {
        // TODO: compress

        private static int _CurrentObjectId = 42; // Last unique Id we gave out.

        // Given out lazily, as MRI does since 2.7: most objects are never asked for their id, and
        // the shared counter is a point of contention between threads that allocate.
        private int _objectId;

        // The values are unused if the object itself implements IRubyObjectState.
        internal bool _frozen;
        private bool _tainted, _untrusted;

        internal object[]/*!*/ _ivars = InstanceVariableShape.EmptySlots;
        private RubyClass _immediateClass;

        // Objects with more variables than this start at this capacity and grow.
        private const int MaxCapacityHint = 64;
        private const int DefaultCapacity = 3;

        /// <summary>
        /// Null - uninitialized (lazy init'd) => object doesn't have an instance singleton.
        /// Class - object doesn't have an instance singleton
        /// Singleton - object has an instance singleton
        ///
        /// Not used by implementations of IRubyObject.
        /// </summary>
        internal RubyClass ImmediateClass {
            get { return _immediateClass; }
            set { _immediateClass = value; }
        }

        // Updates the immediate class if it has not been initialized yet.
        internal void UpdateImmediateClass(RubyClass/*!*/ immediate) {
            Interlocked.CompareExchange(ref _immediateClass, immediate, null);
        }

        internal RubyClass InstanceSingleton {
            get { return (_immediateClass != null && _immediateClass.IsSingletonClass) ? _immediateClass : null; }
        }

        /// <summary>
        /// WARNING: not all objects store their ID here.
        /// Use ObjectOps.GetObjectId instead.
        /// </summary>
        internal int ObjectId {
            get {
                int id = Volatile.Read(ref _objectId);
                if (id == 0) {
                    Interlocked.CompareExchange(ref _objectId, Interlocked.Increment(ref _CurrentObjectId), 0);
                    id = _objectId;
                }
                return id;
            }
        }

        /// <summary>
        /// Whether the object has been given an id yet.
        /// </summary>
        public bool HasObjectId {
            get { return Volatile.Read(ref _objectId) != 0; }
        }

        internal RubyInstanceData(int id) {
            _objectId = id;
        }

        internal RubyInstanceData() {
        }

        #region Flags

        public bool IsTainted {
            get { return _tainted; }
            set {
                Mutate();
                _tainted = value;
            }
        }

        public bool IsUntrusted {
            get { return _untrusted; }
            set {
                Mutate();
                _untrusted = value;
            }
        }

        public bool IsFrozen {
            get { return _frozen; }
        }

        public void Freeze() {
            _frozen = true;
        }

        private void Mutate() {
            if (_frozen) {
                throw RubyExceptions.CreateObjectFrozenError();
            }
        }

        #endregion

        #region Instance Variables: layout

        /// <summary>
        /// Element 0 of a slot array - its shape or a sentinel - read with acquire semantics, so
        /// that slot reads after it see what the writer stored before publishing it. Every slot
        /// array has an element 0, so neither a bounds nor an array-type check is needed.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static object ShapeOf(object[]/*!*/ slots) {
            return Volatile.Read(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(slots));
        }

        // Slot access for the cached fast paths, without the bounds and covariance checks: they
        // only use an index that the shape in element 0 of the same array vouches for (a shape's
        // Count is never more than its array's Length - 1).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static object GetSlot(object[]/*!*/ slots, int index) {
            Debug.Assert(index > 0 && index < slots.Length);
            return System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(slots), index);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static void SetSlot(object[]/*!*/ slots, int index, object value) {
            Debug.Assert(index > 0 && index < slots.Length);
            System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(slots), index) = value;
        }

        /// <summary>
        /// The current slot array and its shape, which is a real shape or Complex - never Busy
        /// or Moved. Must not be called by a thread in the middle of a move of this object.
        /// </summary>
        private object[]/*!*/ GetStable(out InstanceVariableShape/*!*/ shape) {
            var spinner = new SpinWait();
            while (true) {
                object[] s = Volatile.Read(ref _ivars);
                var current = (InstanceVariableShape)ShapeOf(s);
                if (!current.IsSentinel || current == InstanceVariableShape.Complex) {
                    shape = current;
                    return s;
                }

                if (current == InstanceVariableShape.Moved) {
                    // the mover holds the lock until the new array is published:
                    lock (this) { }
                } else {
                    // Busy: a lock-free add is two stores away from done
                    spinner.SpinOnce();
                }
            }
        }

        /// <summary>
        /// Called under lock(this) with s == _ivars. Marks s dead so that every writer that could
        /// still be storing into it either lands its store before the caller copies the slots or
        /// notices and redoes the write. False if s[0] is not <paramref name="shape"/> any more (a
        /// lock-free add got in) - the caller starts over.
        /// </summary>
        private static bool TryRetire(object[]/*!*/ s, InstanceVariableShape/*!*/ shape) {
            if (s.Length == 1) {
                // no slots - nobody writes into it (it may be the shared EmptySlots):
                return true;
            }

            if (Interlocked.CompareExchange(ref s[0], InstanceVariableShape.Moved, shape) != shape) {
                return false;
            }

            Interlocked.MemoryBarrierProcessWide();
            return true;
        }

        /// <summary>
        /// Called under lock(this) after a successful TryRetire(s).
        /// </summary>
        private bool Publish(object[]/*!*/ s, object[]/*!*/ newSlots) {
            if (s.Length == 1) {
                // a lock-free first add may have got in - the caller starts over then:
                return Interlocked.CompareExchange(ref _ivars, newSlots, s) == s;
            }

            Volatile.Write(ref _ivars, newSlots);
            return true;
        }

        private static int GetCapacity(RubyClass hintClass, int needed, int current) {
            int capacity = (current > 0) ? current * 2 : DefaultCapacity;
            if (hintClass != null) {
                capacity = Math.Max(capacity, hintClass.InstanceVariableCapacity);
            }
            return Math.Max(capacity, needed);
        }

        internal static void UpdateCapacityHint(RubyClass hintClass, int count) {
            if (hintClass != null && count > hintClass.InstanceVariableCapacity && count <= MaxCapacityHint) {
                // racy but benign: a lost update only costs a later object a move
                hintClass.InstanceVariableCapacity = count;
            }
        }

        /// <summary>
        /// Converts the variables to a too-complex table. Called under lock(this).
        /// </summary>
        private ComplexInstanceVariables TryMakeComplex(object[]/*!*/ s, InstanceVariableShape/*!*/ shape) {
            if (!TryRetire(s, shape)) {
                return null;
            }

            var table = new ComplexInstanceVariables(shape.Count + 1);
            var names = shape.GetNames();
            for (int i = 0; i < names.Length; i++) {
                table.Set(names[i], s[i + 1]);
            }
            return Publish(s, new object[] { InstanceVariableShape.Complex, table }) ? table : null;
        }

        #endregion

        #region Instance Variables

        internal bool HasInstanceVariables {
            get {
                InstanceVariableShape shape;
                var s = GetStable(out shape);
                if (shape == InstanceVariableShape.Complex) {
                    lock (this) {
                        return ((ComplexInstanceVariables)Volatile.Read(ref _ivars)[1]).Count > 0;
                    }
                }
                return shape.Count > 0;
            }
        }

        /// <summary>
        /// The shape of the object's variables; Complex if it has too many for shapes.
        /// </summary>
        internal InstanceVariableShape/*!*/ Shape {
            get {
                InstanceVariableShape shape;
                GetStable(out shape);
                return shape;
            }
        }

        internal void CopyInstanceVariablesTo(RubyInstanceData/*!*/ dup, object copy) {
            InstanceVariableShape shape;
            var s = GetStable(out shape);
            if (shape != InstanceVariableShape.Complex && dup._ivars == InstanceVariableShape.EmptySlots) {
                // The copy is a fresh object nobody else has seen yet: it gets the same shape.
                if (shape.Count == 0) {
                    return;
                }

                var slots = new object[s.Length];
                for (int i = 1; i <= shape.Count; i++) {
                    var value = s[i];
                    var state = value as IPerObjectState;
                    slots[i] = state != null ? state.CopyFor(copy) : value;
                }
                slots[0] = shape;
                Volatile.Write(ref dup._ivars, slots);
                return;
            }

            foreach (var var in GetInstanceVariablePairs()) {
                var state = var.Value as IPerObjectState;
                dup.SetInstanceVariable(var.Key, state != null ? state.CopyFor(copy) : var.Value, null);
            }
        }

        internal string/*!*/[]/*!*/ GetInstanceVariableNames() {
            InstanceVariableShape shape;
            var s = GetStable(out shape);
            if (shape == InstanceVariableShape.Complex) {
                var pairs = GetInstanceVariablePairs();
                var result = new string[pairs.Count];
                for (int i = 0; i < result.Length; i++) {
                    result[i] = pairs[i].Key;
                }
                return result;
            }

            return ArrayUtils.Copy(shape.GetNames());
        }

        // Returns a copy of the current instance variable key-value pairs for this object, in the
        // order they were added.
        internal List<KeyValuePair<string, object>>/*!*/ GetInstanceVariablePairs() {
            InstanceVariableShape shape;
            var s = GetStable(out shape);
            if (shape == InstanceVariableShape.Complex) {
                var result = new List<KeyValuePair<string, object>>();
                lock (this) {
                    ((ComplexInstanceVariables)Volatile.Read(ref _ivars)[1]).CopyTo(result);
                }
                return result;
            } else {
                var names = shape.GetNames();
                var result = new List<KeyValuePair<string, object>>(names.Length);
                for (int i = 0; i < names.Length; i++) {
                    result.Add(new KeyValuePair<string, object>(names[i], s[i + 1]));
                }
                return result;
            }
        }

        internal bool TryGetInstanceVariable(string/*!*/ name, out object value) {
            InstanceVariableShape shape;
            var s = GetStable(out shape);
            if (shape == InstanceVariableShape.Complex) {
                lock (this) {
                    return ((ComplexInstanceVariables)Volatile.Read(ref _ivars)[1]).TryGetValue(name, out value);
                }
            }

            int index = shape.IndexOf(name);
            if (index > 0) {
                value = s[index];
                return true;
            }

            value = null;
            return false;
        }

        internal object GetInstanceVariable(string/*!*/ name) {
            object value;
            TryGetInstanceVariable(name, out value);
            return value;
        }

        /// <summary>
        /// The slow path of a cached read: looks the variable up and caches where it was found.
        /// </summary>
        internal object GetInstanceVariable(InstanceVariableSite/*!*/ site) {
            InstanceVariableShape shape;
            var s = GetStable(out shape);
            if (shape == InstanceVariableShape.Complex) {
                object value;
                lock (this) {
                    ((ComplexInstanceVariables)Volatile.Read(ref _ivars)[1]).TryGetValue(site.Name, out value);
                }
                return value;
            }

            // the site's second entry: an `@x` that sees two shapes in turn (say, in a method
            // of a superclass whose subclasses add variables of their own) does not thrash
            var entry = site.Cache;
            if (shape == entry.OtherShape) {
                return s[entry.OtherIndex];
            }

            int index = shape.IndexOf(site.Name);
            if (index > 0) {
                site.Update(entry.Remember(shape, index));
                return s[index];
            }
            return null;
        }

        internal bool TryRemoveInstanceVariable(string/*!*/ name, out object value) {
            // frozen state checked by caller
            lock (this) {
                while (true) {
                    InstanceVariableShape shape;
                    var s = GetStable(out shape);
                    if (shape == InstanceVariableShape.Complex) {
                        return ((ComplexInstanceVariables)s[1]).Remove(name, out value);
                    }

                    int index = shape.IndexOf(name);
                    if (index == 0) {
                        value = null;
                        return false;
                    }

                    var newShape = shape.WithoutVariable(name);
                    if (newShape == null) {
                        var table = TryMakeComplex(s, shape);
                        if (table != null) {
                            return table.Remove(name, out value);
                        }
                        continue;
                    }

                    if (!TryRetire(s, shape)) {
                        continue;
                    }

                    // read after the barrier: the latest value any writer stored
                    value = s[index];

                    object[] newSlots;
                    if (newShape.Count == 0) {
                        newSlots = InstanceVariableShape.EmptySlots;
                    } else {
                        newSlots = new object[s.Length];
                        newSlots[0] = newShape;
                        Array.Copy(s, 1, newSlots, 1, index - 1);
                        Array.Copy(s, index + 1, newSlots, index, shape.Count - index);
                    }
                    if (!Publish(s, newSlots)) {
                        throw Assert.Unreachable;
                    }
                    return true;
                }
            }
        }

        internal void SetInstanceVariable(string/*!*/ name, object value) {
            SetInstanceVariable(name, value, null, null);
        }

        internal void SetInstanceVariable(string/*!*/ name, object value, RubyClass hintClass) {
            SetInstanceVariable(name, value, hintClass, null);
        }

        /// <summary>
        /// Sets the variable, adding it if need be. Frozen state checked by caller.
        /// <paramref name="hintClass"/> is the class whose instances' variable count sizes the slot
        /// array (optional); <paramref name="site"/> the cache to fill (optional).
        /// </summary>
        internal void SetInstanceVariable(string/*!*/ name, object value, RubyClass hintClass, InstanceVariableSite site) {
            var spinner = new SpinWait();
            while (true) {
                object[] s = Volatile.Read(ref _ivars);
                var shape = (InstanceVariableShape)ShapeOf(s);
                if (shape.IsSentinel) {
                    if (shape == InstanceVariableShape.Complex) {
                        lock (this) {
                            if (Volatile.Read(ref _ivars) == s) {
                                ((ComplexInstanceVariables)s[1]).Set(name, value);
                                return;
                            }
                        }
                    } else if (shape == InstanceVariableShape.Moved) {
                        lock (this) { }
                    } else {
                        spinner.SpinOnce();
                    }
                    continue;
                }

                int index = shape.IndexOf(name);
                if (index > 0) {
                    s[index] = value;
                    if (ShapeOf(s) == shape) {
                        if (site != null) {
                            site.Update(site.Cache.Remember(shape, index));
                        }
                        return;
                    }
                    // the shape changed under the write - redo it on whatever is there now
                    continue;
                }

                var next = shape.GetChild(name);
                if (next == null) {
                    lock (this) {
                        if (Volatile.Read(ref _ivars) == s) {
                            var table = TryMakeComplex(s, shape);
                            if (table != null) {
                                table.Set(name, value);
                                return;
                            }
                        }
                    }
                    continue;
                }

                index = next.Count;
                if (index < s.Length) {
                    // room in the array: claim it, store, publish
                    if (Interlocked.CompareExchange(ref s[0], InstanceVariableShape.Busy, shape) == shape) {
                        s[index] = value;
                        Volatile.Write(ref s[0], next);
                        if (site != null) {
                            site.Update(new InstanceVariableSite.Entry(shape, index, next, 0));
                        }
                        UpdateCapacityHint(hintClass, index);
                        return;
                    }
                    continue;
                }

                int capacity = GetCapacity(hintClass, index, s.Length - 1);
                if (s.Length == 1) {
                    // the first variable: nothing to copy, nobody writing
                    Debug.Assert(index == 1);
                    var first = new object[capacity + 1];
                    first[0] = next;
                    first[1] = value;
                    if (Interlocked.CompareExchange(ref _ivars, first, s) == s) {
                        if (site != null) {
                            site.Update(new InstanceVariableSite.Entry(shape, index, next, capacity));
                        }
                        UpdateCapacityHint(hintClass, index);
                        return;
                    }
                    continue;
                }

                lock (this) {
                    if (Volatile.Read(ref _ivars) != s || !TryRetire(s, shape)) {
                        continue;
                    }

                    var grown = new object[capacity + 1];
                    Array.Copy(s, 1, grown, 1, shape.Count);
                    grown[index] = value;
                    grown[0] = next;
                    if (!Publish(s, grown)) {
                        throw Assert.Unreachable;
                    }
                }
                UpdateCapacityHint(hintClass, index);
                return;
            }
        }

        /// <summary>
        /// Fast path of a cached write. False if the cache does not apply, or the write has to
        /// allocate or wait: the caller takes the slow path. Frozen state checked by caller.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TrySetCached(InstanceVariableSite.Entry/*!*/ entry, object value, RubyObject/*!*/ owner) {
            object[] s = _ivars;
            object shape = ShapeOf(s);
            if (shape != entry.Shape) {
                if (shape == entry.OtherShape) {
                    SetSlot(s, entry.OtherIndex, value);
                    return ShapeOf(s) == shape;
                }
                return false;
            }

            if (entry.Next == null) {
                SetSlot(s, entry.Index, value);
                return ShapeOf(s) == entry.Shape;
            }

            if (entry.Index < s.Length) {
                if (Interlocked.CompareExchange(ref s[0], InstanceVariableShape.Busy, entry.Shape) == entry.Shape) {
                    SetSlot(s, entry.Index, value);
                    Volatile.Write(ref s[0], entry.Next);
                    return true;
                }
                return false;
            }

            if (s.Length == 1) {
                // the object's first variable
                // the class may have learnt since the site was filled that its instances get more
                var first = new object[Math.Max(entry.Capacity, owner.ImmediateClass.InstanceVariableCapacity) + 1];
                first[0] = entry.Next;
                first[1] = value;
                return Interlocked.CompareExchange(ref _ivars, first, s) == s;
            }

            return false;
        }

        internal VariableDebugView[]/*!*/ GetInstanceVariablesDebugView(RubyContext/*!*/ context) {
            var result = new List<VariableDebugView>();
            foreach (var var in GetInstanceVariablePairs()) {
                result.Add(new VariableDebugView(context, this, var.Key));
            }

            result.Sort((var1, var2) => String.CompareOrdinal(var1._name, var2._name));
            return result.ToArray();
        }

        [DebuggerDisplay("{GetValue()}", Name = "{_name,nq}", Type = "{GetClassName(),nq}")]
        public sealed class VariableDebugView {
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            private readonly RubyContext/*!*/ _context;
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            private readonly RubyInstanceData/*!*/ _data;
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            internal readonly string/*!*/ _name;

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public object A {
                get { return GetValue(); }
            }

            [DebuggerDisplay("{B}", Name = "Raw Value", Type = "{GetClrType()}")]
            public object B {
                get { return GetValue(); }
                set { _data.SetInstanceVariable(_name, value); }
            }

            private object GetValue() {
                return _data.GetInstanceVariable(_name);
            }

            private Type GetClrType() {
                var value = GetValue();
                return value != null ? value.GetType() : null;
            }

            private string/*!*/ GetClassName() {
                return _context.GetClassDisplayName(GetValue());
            }

            internal VariableDebugView(RubyContext/*!*/ context, RubyInstanceData/*!*/ data, string/*!*/ name) {
                _context = context;
                _data = data;
                _name = name;
            }
        }

        #endregion
    }
}

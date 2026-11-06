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
using System.Diagnostics;
using System.Threading;

namespace IronRuby.Runtime {
    /// <summary>
    /// A node in the instance variable transition tree: "the instance variables N1, N2, ... Nk,
    /// added in that order". An object's variables live in a flat slot array whose element 0 is
    /// the shape and whose element i holds the value of Ni - see <see cref="RubyInstanceData"/>.
    ///
    /// Shapes are immutable (apart from the lazily grown transition table and index cache, which
    /// are published by a single reference write), and shared by every object - of any class -
    /// that got the same variables in the same order. So `@b` on an object whose shape is S lives
    /// at slot S.IndexOf("@b"), and a site that has seen S before knows that slot without looking.
    ///
    /// This is the design of CRuby 3.2+'s object shapes, TruffleRuby's and V8's hidden classes.
    /// Differences: there is one tree for all classes (a shape does not name a class, so the site
    /// cache still checks one reference), frozenness is not part of the shape (a setter checks the
    /// frozen flag it has at hand anyway), and a "too complex" object keeps its variables in an
    /// ordered table instead (<see cref="Complex"/>).
    /// </summary>
    public sealed class InstanceVariableShape {
        // Transitions past these limits make the object "too complex" instead: it gets an ordered
        // dictionary of its own. The shape tree is never collected, so something has to bound it
        // when variable names are made up at runtime (instance_variable_set("@#{key}", ...)).
        internal const int MaxVariations = 256;
        internal const int MaxShapes = 250000;

        // Deeper shapes than this get a name -> index dictionary instead of a walk to the root.
        private const int IndexDictionaryDepth = 8;

        private static readonly object _TreeLock = new object();
        private static int _ShapeCount;

        internal static readonly InstanceVariableShape/*!*/ Root = new InstanceVariableShape(null, null, 0);

        // Sentinels. None of them is ever the shape a site caches, so a fast path that meets one
        // falls to the slow path, which knows what to do:
        //   Busy    - a lock-free add is between claiming the slot array and publishing the new shape
        //   Moved   - the slot array is being replaced (under the owner's lock); the old one is dead
        //   Complex - element 1 of the array is a ComplexInstanceVariables table
        internal static readonly InstanceVariableShape/*!*/ Busy = new InstanceVariableShape(null, "<busy>", -1);
        internal static readonly InstanceVariableShape/*!*/ Moved = new InstanceVariableShape(null, "<moved>", -1);
        internal static readonly InstanceVariableShape/*!*/ Complex = new InstanceVariableShape(null, "<complex>", -1);

        /// <summary>
        /// The slot array of every object without instance variables. It has no slots, so nothing
        /// ever writes into it: the first variable replaces it.
        /// </summary>
        internal static readonly object[]/*!*/ EmptySlots = new object[] { Root };

        private readonly InstanceVariableShape _parent;
        private readonly string _name;
        private readonly int _count;

        // null, a single child (whose _name is the key), or a Dictionary that is never mutated
        // once published - a new child copies it. Read without a lock.
        private object _transitions;
        private int _childCount;

        // Built on demand for deep shapes; immutable once published.
        private Dictionary<string, int> _index;
        private string[] _names;

        private InstanceVariableShape(InstanceVariableShape parent, string name, int count) {
            _parent = parent;
            _name = name;
            _count = count;
        }

        /// <summary>
        /// The number of instance variables, which is also the slot index of the last one.
        /// Negative for the sentinels.
        /// </summary>
        public int Count {
            get { return _count; }
        }

        internal InstanceVariableShape Parent {
            get { return _parent; }
        }

        internal bool IsSentinel {
            get { return _count < 0; }
        }

        /// <summary>
        /// The slot that holds <paramref name="name"/>, or 0 if this shape has no such variable.
        /// </summary>
        internal int IndexOf(string/*!*/ name) {
            if (_count <= IndexDictionaryDepth) {
                for (var shape = this; shape._count > 0; shape = shape._parent) {
                    if ((object)shape._name == (object)name || shape._name == name) {
                        return shape._count;
                    }
                }
                return 0;
            }

            var index = Volatile.Read(ref _index) ?? BuildIndex();
            int result;
            return index.TryGetValue(name, out result) ? result : 0;
        }

        private Dictionary<string, int>/*!*/ BuildIndex() {
            var index = new Dictionary<string, int>(_count);
            for (var shape = this; shape._count > 0; shape = shape._parent) {
                index[shape._name] = shape._count;
            }
            Volatile.Write(ref _index, index);
            return index;
        }

        /// <summary>
        /// The variable names in the order they were added. The array is shared - do not modify it.
        /// </summary>
        internal string/*!*/[]/*!*/ GetNames() {
            if (_count <= 0) {
                return Microsoft.Scripting.Utils.ArrayUtils.EmptyStrings;
            }

            var names = Volatile.Read(ref _names);
            if (names == null) {
                names = new string[_count];
                for (var shape = this; shape._count > 0; shape = shape._parent) {
                    names[shape._count - 1] = shape._name;
                }
                Volatile.Write(ref _names, names);
            }
            return names;
        }

        /// <summary>
        /// The shape this one becomes when <paramref name="name"/> is added, or null if that
        /// transition would pass the limits and the object has to become too complex.
        /// Thread-safe; lock-free when the transition exists.
        /// </summary>
        internal InstanceVariableShape GetChild(string/*!*/ name) {
            Debug.Assert(!IsSentinel && IndexOf(name) == 0);

            var child = TryGetChild(name);
            if (child != null) {
                return child;
            }

            lock (_TreeLock) {
                child = TryGetChild(name);
                if (child != null) {
                    return child;
                }

                if (_ShapeCount >= MaxShapes || (_parent != null && _childCount >= MaxVariations)) {
                    return null;
                }

                child = new InstanceVariableShape(this, name, _count + 1);
                var transitions = _transitions;
                if (transitions == null) {
                    Volatile.Write(ref _transitions, child);
                } else {
                    var single = transitions as InstanceVariableShape;
                    Dictionary<string, InstanceVariableShape> dict;
                    if (single != null) {
                        dict = new Dictionary<string, InstanceVariableShape>(2);
                        dict.Add(single._name, single);
                    } else {
                        dict = new Dictionary<string, InstanceVariableShape>((Dictionary<string, InstanceVariableShape>)transitions);
                    }
                    dict.Add(name, child);
                    Volatile.Write(ref _transitions, dict);
                }
                _childCount++;
                _ShapeCount++;
                return child;
            }
        }

        private InstanceVariableShape TryGetChild(string/*!*/ name) {
            var transitions = Volatile.Read(ref _transitions);
            if (transitions == null) {
                return null;
            }

            var single = transitions as InstanceVariableShape;
            if (single != null) {
                return (single._name == name) ? single : null;
            }

            InstanceVariableShape result;
            ((Dictionary<string, InstanceVariableShape>)transitions).TryGetValue(name, out result);
            return result;
        }

        /// <summary>
        /// The shape of this one's variables without <paramref name="name"/>, the rest in the same
        /// order; null if it cannot be had within the limits.
        /// </summary>
        internal InstanceVariableShape WithoutVariable(string/*!*/ name) {
            var result = Root;
            foreach (var n in GetNames()) {
                if (n != name) {
                    result = result.GetChild(n);
                    if (result == null) {
                        return null;
                    }
                }
            }
            return result;
        }

        public override string/*!*/ ToString() {
            return IsSentinel ? _name : "[" + String.Join(", ", GetNames()) + "]";
        }
    }

    /// <summary>
    /// The instance variables of an object that is too complex for shapes: an ordered table.
    /// Only ever touched under the lock of the owning <see cref="RubyInstanceData"/>.
    /// </summary>
    internal sealed class ComplexInstanceVariables {
        private readonly Dictionary<string, int>/*!*/ _index;
        private readonly List<string>/*!*/ _names;
        private readonly List<object>/*!*/ _values;
        private int _removed;

        internal ComplexInstanceVariables(int capacity) {
            _index = new Dictionary<string, int>(capacity);
            _names = new List<string>(capacity);
            _values = new List<object>(capacity);
        }

        internal int Count {
            get { return _index.Count; }
        }

        internal bool TryGetValue(string/*!*/ name, out object value) {
            int i;
            if (_index.TryGetValue(name, out i)) {
                value = _values[i];
                return true;
            }
            value = null;
            return false;
        }

        internal void Set(string/*!*/ name, object value) {
            int i;
            if (_index.TryGetValue(name, out i)) {
                _values[i] = value;
            } else {
                _index.Add(name, _names.Count);
                _names.Add(name);
                _values.Add(value);
            }
        }

        internal bool Remove(string/*!*/ name, out object value) {
            int i;
            if (!_index.TryGetValue(name, out i)) {
                value = null;
                return false;
            }
            value = _values[i];
            _index.Remove(name);
            _names[i] = null;
            _values[i] = null;
            if (++_removed > 16 && _removed > _names.Count / 2) {
                Compact();
            }
            return true;
        }

        private void Compact() {
            int j = 0;
            for (int i = 0; i < _names.Count; i++) {
                if (_names[i] != null) {
                    _names[j] = _names[i];
                    _values[j] = _values[i];
                    _index[_names[j]] = j;
                    j++;
                }
            }
            _names.RemoveRange(j, _names.Count - j);
            _values.RemoveRange(j, _values.Count - j);
            _removed = 0;
        }

        internal void CopyTo(List<KeyValuePair<string, object>>/*!*/ result) {
            for (int i = 0; i < _names.Count; i++) {
                if (_names[i] != null) {
                    result.Add(new KeyValuePair<string, object>(_names[i], _values[i]));
                }
            }
        }
    }

    /// <summary>
    /// The inline cache of one `@x` read or write, or of one attr_reader/attr_writer call site.
    /// It remembers the last shape seen and where the variable lives in it (and, for a write that
    /// adds the variable, which shape the object moves to). The (shape, index) pair is one
    /// immutable object replaced by a single reference write, so a site read concurrently with
    /// its update never pairs one shape with another's index.
    /// </summary>
    public sealed class InstanceVariableSite {
        internal sealed class Entry {
            // The shape the object must have. Null never matches: element 0 of a slot array
            // is never null.
            internal readonly InstanceVariableShape Shape;

            // The slot of the variable in Shape - for an adding write, in Next.
            internal readonly int Index;

            // An adding write: the shape after the variable is added; null otherwise.
            internal readonly InstanceVariableShape Next;

            // An adding write from no variables at all: how many slots to allocate.
            internal readonly int Capacity;

            // A second shape the variable was found in, and its slot there - the site's previous
            // entry, so a site that sees two shapes in turn stays cached. Checked after Shape,
            // and never for an adding write.
            internal readonly InstanceVariableShape OtherShape;
            internal readonly int OtherIndex;

            internal Entry(InstanceVariableShape shape, int index, InstanceVariableShape next, int capacity)
                : this(shape, index, next, capacity, null, 0) {
            }

            private Entry(InstanceVariableShape shape, int index, InstanceVariableShape next, int capacity,
                InstanceVariableShape otherShape, int otherIndex) {
                Shape = shape;
                Index = index;
                Next = next;
                Capacity = capacity;
                OtherShape = otherShape;
                OtherIndex = otherIndex;
            }

            /// <summary>
            /// The entry for a variable found at <paramref name="index"/> in <paramref name="shape"/>
            /// that keeps this one's (non-adding) shape as the second.
            /// </summary>
            internal Entry/*!*/ Remember(InstanceVariableShape/*!*/ shape, int index) {
                if (shape == Shape && Next == null) {
                    return this;
                }
                return (Next == null && Shape != null) ?
                    new Entry(shape, index, null, 0, Shape, Index) :
                    new Entry(shape, index, null, 0);
            }
        }

        internal static readonly Entry/*!*/ EmptyEntry = new Entry(null, 0, null, 0);

        internal readonly string/*!*/ Name;
        internal Entry/*!*/ Cache = EmptyEntry;

        public InstanceVariableSite(string/*!*/ name) {
            Debug.Assert(name != null);
            Name = name;
        }

        // A release: a thread that reads the new entry sees it fully constructed (its reads of
        // the fields depend on the reference, so they are ordered after it).
        internal void Update(Entry/*!*/ entry) {
            Volatile.Write(ref Cache, entry);
        }

        public override string/*!*/ ToString() {
            return Name;
        }
    }
}

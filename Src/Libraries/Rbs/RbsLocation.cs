/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * RBS::Location - ext/rbs_extension/legacy_location.c of the rbs gem (BSD-2-Clause / Ruby).
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Rbs {

    [RubyModule("RBS")]
    public static partial class RbsOps {

        /// <summary>
        /// rb_raise: the C extension's messages are ASCII-8BIT Strings, not UTF-8 ones.
        /// </summary>
        internal static Exception/*!*/ Binary(Exception/*!*/ exception) {
            RubyExceptionData.GetInstance(exception).Message =
                MutableString.CreateBinary(System.Text.Encoding.UTF8.GetBytes(exception.Message), RubyEncoding.Binary);
            return exception;
        }

        /// <summary>
        /// RBS::Location: a buffer, a character range in it, and named child ranges, some of
        /// them required.  The C extension keeps the children in a small array with a bitmap of
        /// which are required (at most 32 of them) and looks a name up by its first entry; this
        /// is the same structure, with the names as Symbols rather than ids in rbs's global
        /// constant pool.  rbs/location_aux.rb, from the installed gem, builds the public API on
        /// these primitives.
        /// </summary>
        [RubyClass("Location", Inherits = typeof(object))]
        public sealed class Location : RubyObject {
            private const int MaxChildren = 32; // bits in rbs_loc_entry_bitmap

            internal object _buffer;
            internal int _start = -1, _end = -1;

            // null when no children are allocated, as rbs_loc's children pointer
            private Child[] _children;
            private int _childCount;
            private uint _requiredBits;

            internal struct Child {
                public RubySymbol Name;
                public int Start, End;
            }

            public Location(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            internal Location(RubyClass/*!*/ rubyClass, object buffer, int start, int end) : base(rubyClass) {
                _buffer = buffer;
                _start = start;
                _end = end;
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Location(ImmediateClass.NominalClass);
            }

            // Class#new on a library class binds to a constructor rather than to #initialize, so the
            // factory takes #initialize's arguments (location_initialize).  rbs/location_aux.rb's
            // Location.new reaches it through super.
            [RubyConstructor]
            public static Location/*!*/ Create(RubyClass/*!*/ self, object buffer, int startPos, int endPos) {
                var result = new Location(self);
                Initialize(result, buffer, startPos, endPos);
                return result;
            }

            /// <summary>rbs_loc_legacy_alloc_children</summary>
            internal void AllocateChildren(int capacity) {
                CheckChildrenMax(capacity);
                _children = new Child[capacity];
                _childCount = 0;
                _requiredBits = 0;
            }

            private static void CheckChildrenMax(int n) {
                if (n > MaxChildren) {
                    throw RbsOps.Binary(RubyExceptions.CreateRuntimeError(String.Format("Too many children added to location: {0}", n)));
                }
            }

            private void CheckChildrenCapacity() {
                if (_children == null) {
                    AllocateChildren(1);
                } else if (_childCount == _children.Length) {
                    CheckChildrenMax(_children.Length + 1);
                    Array.Resize(ref _children, _children.Length + 1);
                }
            }

            internal void AddChild(RubySymbol/*!*/ name, int start, int end, bool required) {
                CheckChildrenCapacity();
                int i = _childCount++;
                _children[i] = new Child { Name = name, Start = start, End = end };
                if (required) {
                    _requiredBits |= 1u << i;
                }
            }

            /// <summary>The children exactly as the parser recorded them.</summary>
            internal void SetChildren(Child[]/*!*/ children, int count, uint requiredBits) {
                CheckChildrenMax(children.Length);
                _children = children;
                _childCount = count;
                _requiredBits = requiredBits;
            }

            private bool IsRequired(int i) {
                return (_requiredBits & (1u << i)) != 0;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(Location/*!*/ self, object buffer, int startPos, int endPos) {
                self._buffer = buffer;
                self._start = startPos;
                self._end = endPos;
                self._children = null;
                self._childCount = 0;
                self._requiredBits = 0;
                return null;
            }

            [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
            public static object InitializeCopy(Location/*!*/ self, [NotNull]Location/*!*/ other) {
                self._buffer = other._buffer;
                self._start = other._start;
                self._end = other._end;
                self._children = null;
                self._childCount = 0;
                self._requiredBits = 0;
                if (other._children != null) {
                    self._children = (Child[])other._children.Clone();
                    self._childCount = other._childCount;
                    self._requiredBits = other._requiredBits;
                }
                return null;
            }

            [RubyMethod("buffer")]
            public static object GetBuffer(Location/*!*/ self) {
                return self._buffer;
            }

            [RubyMethod("_start_pos")]
            public static int GetStartPos(Location/*!*/ self) {
                return self._start;
            }

            [RubyMethod("_end_pos")]
            public static int GetEndPos(Location/*!*/ self) {
                return self._end;
            }

            [RubyMethod("_add_required_child")]
            public static object AddRequiredChild(Location/*!*/ self, [NotNull]RubySymbol/*!*/ name, int start, int end) {
                self.AddChild(name, start, end, true);
                return null;
            }

            [RubyMethod("_add_optional_child")]
            public static object AddOptionalChild(Location/*!*/ self, [NotNull]RubySymbol/*!*/ name, int start, int end) {
                self.AddChild(name, start, end, false);
                return null;
            }

            /// <summary>An optional child that is absent: NULL_RANGE, which #[] answers nil for.</summary>
            [RubyMethod("_add_optional_no_child")]
            public static object AddOptionalNoChild(Location/*!*/ self, [NotNull]RubySymbol/*!*/ name) {
                self.AddChild(name, -1, -1, false);
                return null;
            }

            [RubyMethod("_optional_keys")]
            public static RubyArray/*!*/ GetOptionalKeys(Location/*!*/ self) {
                return self.Keys(false);
            }

            [RubyMethod("_required_keys")]
            public static RubyArray/*!*/ GetRequiredKeys(Location/*!*/ self) {
                return self.Keys(true);
            }

            private RubyArray/*!*/ Keys(bool required) {
                var keys = new RubyArray();
                for (int i = 0; i < _childCount; i++) {
                    if (IsRequired(i) == required) {
                        keys.Add(_children[i].Name);
                    }
                }
                return keys;
            }

            /// <summary>
            /// The first child of that name: nil for an optional one that is absent, a new
            /// RBS::Location (with no children) otherwise.
            /// </summary>
            [RubyMethod("[]")]
            public static object GetChild(UnaryOpStorage/*!*/ toSStorage, Location/*!*/ self, object name) {
                var symbol = name as RubySymbol;
                if (symbol != null && self._children != null) {
                    for (int i = 0; i < self._childCount; i++) {
                        if (SameName(self._children[i].Name, symbol)) {
                            var child = self._children[i];
                            if (!self.IsRequired(i) && child.Start == -1) {
                                return null;
                            }
                            // rbs_new_location_from_loc_range: always an RBS::Location, whatever self is
                            return new Location(LocationClass(self.ImmediateClass.Context), self._buffer, child.Start, child.End);
                        }
                    }
                }

                var toS = toSStorage.GetCallSite("to_s");
                throw RbsOps.Binary(RubyExceptions.CreateRuntimeError(String.Format("Unknown child name given: {0}", toS.Target(toS, name))));
            }

            internal static RubyClass/*!*/ LocationClass(RubyContext/*!*/ context) {
                return context.GetClass(typeof(Location));
            }

            /// <summary>The constant pool compares names by their bytes.</summary>
            private static bool SameName(RubySymbol/*!*/ a, RubySymbol/*!*/ b) {
                if (ReferenceEquals(a, b)) return true;
                var x = a.String.ToByteArray();
                var y = b.String.ToByteArray();
                if (x.Length != y.Length) return false;
                for (int i = 0; i < x.Length; i++) {
                    if (x[i] != y[i]) return false;
                }
                return true;
            }
        }
    }
}

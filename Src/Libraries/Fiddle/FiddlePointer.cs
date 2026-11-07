/* ****************************************************************************
 *
 * Fiddle::Pointer - a raw address, with the block behind it when Fiddle owns one.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Fiddle {

    public static partial class FiddleOps {

        /// <summary>
        /// An address, the number of bytes Fiddle believes are readable there, and the free
        /// function that owns them (0 for memory Fiddle did not allocate).  Memory whose
        /// free function is set is released by #free, by #call_free, or by the finalizer -
        /// the same three ways CRuby releases it.
        ///
        /// One difference from CRuby is worth stating plainly.  There,
        /// <c>Fiddle::Pointer[str]</c> hands out the address of the String's own character
        /// buffer, so writes through the pointer are writes to the String.  A MutableString
        /// has no such stable address, so the bytes are copied into a block this Pointer
        /// owns.  To keep the usual pattern working - build a buffer, hand it to C, read it
        /// back - the Pointer remembers the String it was made from, and
        /// Fiddle::Function#call copies the block back into it after any call that was
        /// handed the Pointer.  Writes from Ruby (#[]=) do not reach the String.
        /// </summary>
        [RubyClass("Pointer")]
        public class Pointer : RubyObject {
            private IntPtr _address;
            private long _size;
            private IntPtr _freeFunc;
            private bool _freed;

            /// <summary>
            /// True when this Pointer allocated the block itself for a String (Pointer[str])
            /// and must release it even though Fiddle reports no free function, matching
            /// CRuby's "free=0x0" for the same pointer.
            /// </summary>
            private bool _ownsCopy;

            /// <summary>The String this pointer is a copy of, if any; see the class remarks.</summary>
            internal MutableString BackingString;

            /// <summary>
            /// The start of the block this Pointer owns, when that is not its own address -
            /// a call that answers a pointer into a buffer Fiddle allocated for it (strcpy
            /// answering its destination) leaves the two apart.
            /// </summary>
            private IntPtr _ownedBlock;

            internal bool IsInside(IntPtr block, long length) {
                long address = (long)_address, start = (long)block;
                return address >= start && address < start + length;
            }

            /// <summary>Takes over a block, so that it outlives the call that made it.</summary>
            internal void AdoptBlock(IntPtr block) {
                _ownsCopy = true;
                _ownedBlock = block;
                TakeOwnership();
            }

            /// <summary>
            /// Whether the finalizer is armed.  A Pointer that owns nothing - the common
            /// case, since every TYPE_VOIDP return makes one - has nothing to release, and
            /// putting it on the finalizer queue would cost an extra collection each time.
            /// </summary>
            private bool _finalizable;

            public Pointer(RubyClass/*!*/ rubyClass) : base(rubyClass) {
                GC.SuppressFinalize(this);
            }

            /// <summary>Arms the finalizer: from here on this Pointer owns its memory.</summary>
            private void TakeOwnership() {
                if (!_finalizable) {
                    _finalizable = true;
                    GC.ReRegisterForFinalize(this);
                }
            }

            private void Disown() {
                if (_finalizable) {
                    _finalizable = false;
                    GC.SuppressFinalize(this);
                }
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Pointer(ImmediateClass.NominalClass);
            }

            ~Pointer() {
                if (!_freed && _address != IntPtr.Zero && (_freeFunc != IntPtr.Zero || _ownsCopy)) {
                    _freed = true;
                    if (_ownsCopy && _freeFunc == IntPtr.Zero) {
                        Release((_ownedBlock == IntPtr.Zero) ? _address : _ownedBlock);
                    } else {
                        CallFreeFunction(_freeFunc, _address);
                    }
                }
            }

            internal IntPtr Address {
                get { return _address; }
            }

            internal long Size {
                get { return _size; }
            }

            internal static Pointer/*!*/ Create(RubyContext/*!*/ context, IntPtr address, long size, IntPtr freeFunc) {
                var result = new Pointer(context.GetClass(typeof(Pointer)));
                result._address = address;
                result._size = size;
                result._freeFunc = freeFunc;
                if (freeFunc != IntPtr.Zero) {
                    result.TakeOwnership();
                }
                return result;
            }

            /// <summary>Pointer[str] / Pointer.to_ptr(str): a block holding a copy of the bytes.</summary>
            internal static Pointer/*!*/ CreateFromString(RubyContext/*!*/ context, MutableString/*!*/ str) {
                byte[] bytes = str.ToByteArray();
                IntPtr block = Allocate(bytes.Length + 1);
                Marshal.Copy(bytes, 0, block, bytes.Length);
                Marshal.WriteByte(block, bytes.Length, 0);

                var result = Create(context, block, bytes.Length, IntPtr.Zero);
                result._ownsCopy = true;
                result.BackingString = str;
                result.TakeOwnership();
                return result;
            }

            /// <summary>
            /// Copies the block back into the String this pointer was made from, so that a C
            /// function that wrote through the pointer is visible in Ruby.  Called by
            /// Function#call for every String-backed Pointer it was handed.
            /// </summary>
            internal void SyncBackingString() {
                MutableString str = BackingString;
                if (str == null || str.IsFrozen || _freed || _address == IntPtr.Zero) {
                    return;
                }
                int count = Math.Min(str.GetByteCount(), (int)_size);
                for (int i = 0; i < count; i++) {
                    str.SetByte(i, Marshal.ReadByte(_address, i));
                }
            }

            private void CheckLive() {
                if (_freed) {
                    throw new DLError("dangling pointer");
                }
            }

            #region Construction

            [RubyConstructor]
            public static Pointer/*!*/ Create(RubyClass/*!*/ self, object address,
                [DefaultParameterValue(0)]int size, [DefaultParameterValue(null)]object freeFunc) {

                var result = new Pointer(self);
                Reinitialize(result, address, size, freeFunc);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static Pointer/*!*/ Reinitialize(Pointer/*!*/ self, object address,
                [DefaultParameterValue(0)]int size, [DefaultParameterValue(null)]object freeFunc) {

                self._address = ToAddress(address);
                self._size = size;
                self._freeFunc = ToAddress(freeFunc);
                self._freed = false;
                if (self._freeFunc != IntPtr.Zero) {
                    self.TakeOwnership();
                }
                return self;
            }

            [RubyMethod("malloc", RubyMethodAttributes.PublicSingleton)]
            public static object Malloc(BlockParam block, RubyClass/*!*/ self, [DefaultProtocol]int size,
                [DefaultParameterValue(null)]object freeFunc) {

                if (block != null && freeFunc == null) {
                    throw RubyExceptions.CreateArgumentError("a free function must be supplied to Fiddle::Pointer.malloc with a block");
                }

                var result = new Pointer(self);
                result._address = Allocate(size);
                result._size = size;
                result._freeFunc = ToAddress(freeFunc);
                if (result._freeFunc != IntPtr.Zero) {
                    result.TakeOwnership();
                }
                // malloc(3) does not zero; Fiddle's callers expect a clean buffer as often
                // as not, and CRuby's ruby_xmalloc does not zero either - but a block of
                // uninitialized bytes reaching Ruby is worse here, where nothing else can
                // see it, so it is cleared.
                for (long i = 0; i < size; i++) {
                    Marshal.WriteByte(result._address, (int)i, 0);
                }

                if (block != null) {
                    object blockResult;
                    try {
                        if (block.Yield(result, out blockResult)) {
                            return blockResult;
                        }
                    } finally {
                        ReleaseBlock(result);
                    }
                    return blockResult;
                }
                return result;
            }

            [RubyMethod("to_ptr", RubyMethodAttributes.PublicSingleton)]
            [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
            public static Pointer/*!*/ ToPtr(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]MutableString/*!*/ str) {
                return CreateFromString(context, str);
            }

            [RubyMethod("to_ptr", RubyMethodAttributes.PublicSingleton)]
            [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
            public static Pointer/*!*/ ToPtr(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]Pointer/*!*/ ptr) {
                return ptr;
            }

            /// <summary>
            /// Anything else: #to_ptr if it has one - which must answer a Pointer - then
            /// #to_int, then whatever ToAddress can already make sense of.
            /// </summary>
            [RubyMethod("to_ptr", RubyMethodAttributes.PublicSingleton)]
            [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
            public static Pointer/*!*/ ToPtr(RespondToStorage/*!*/ respondTo, UnaryOpStorage/*!*/ conversion,
                RubyClass/*!*/ self, object value) {

                RubyContext context = respondTo.Context;
                if (Protocols.RespondTo(respondTo, value, "to_ptr")) {
                    var site = conversion.GetCallSite("to_ptr", 0);
                    var ptr = site.Target(site, value) as Pointer;
                    if (ptr == null) {
                        throw new DLError("to_ptr should return a Fiddle::Pointer object");
                    }
                    return ptr;
                }
                if (Protocols.RespondTo(respondTo, value, "to_int")) {
                    var site = conversion.GetCallSite("to_int", 0);
                    return Create(context, ToAddress(site.Target(site, value)), 0, IntPtr.Zero);
                }
                return Create(context, ToAddress(value), 0, IntPtr.Zero);
            }

            /// <summary>Pointer.read(address, len): the bytes at a bare address.</summary>
            [RubyMethod("read", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ ReadMemory(RubyClass/*!*/ self, object address, [DefaultProtocol]int length) {
                return ReadBytes(ToAddress(address), length);
            }

            /// <summary>Pointer.write(address, str): overwrites the bytes at a bare address.</summary>
            [RubyMethod("write", RubyMethodAttributes.PublicSingleton)]
            public static object WriteMemory(RubyClass/*!*/ self, object address, [NotNull]MutableString/*!*/ str) {
                byte[] bytes = str.ToByteArray();
                Marshal.Copy(bytes, 0, ToAddress(address), bytes.Length);
                return null;
            }

            #endregion

            #region Reading and writing

            internal static MutableString/*!*/ ReadBytes(IntPtr address, int length) {
                if (address == IntPtr.Zero) {
                    throw RubyExceptions.CreateArgumentError("NULL pointer given");
                }
                byte[] bytes = new byte[length];
                if (length > 0) {
                    Marshal.Copy(address, bytes, 0, length);
                }
                return MutableString.CreateBinary(bytes);
            }

            private static int CStringLength(IntPtr address) {
                int length = 0;
                while (Marshal.ReadByte(address, length) != 0) {
                    length++;
                }
                return length;
            }

            /// <summary>The NUL-terminated string at this address, or its first len bytes.</summary>
            [RubyMethod("to_s")]
            public static MutableString/*!*/ ToS(Pointer/*!*/ self) {
                self.CheckLive();
                if (self._address == IntPtr.Zero) {
                    throw RubyExceptions.CreateArgumentError("NULL pointer given");
                }
                return ReadBytes(self._address, CStringLength(self._address));
            }

            [RubyMethod("to_s")]
            public static MutableString/*!*/ ToS(Pointer/*!*/ self, [DefaultProtocol]int length) {
                self.CheckLive();
                return ReadBytes(self._address, length);
            }

            /// <summary>The whole block, NULs and all: #size bytes, or len of them.</summary>
            [RubyMethod("to_str")]
            public static MutableString/*!*/ ToStr(Pointer/*!*/ self) {
                self.CheckLive();
                return ReadBytes(self._address, checked((int)self._size));
            }

            [RubyMethod("to_str")]
            public static MutableString/*!*/ ToStr(Pointer/*!*/ self, [DefaultProtocol]int length) {
                self.CheckLive();
                return ReadBytes(self._address, length);
            }

            private void CheckIndex(long index, long count) {
                if (index < 0 || count < 0 || (_size > 0 && index + count > _size)) {
                    throw RubyExceptions.CreateIndexError("index " + index.ToString(CultureInfo.InvariantCulture) + " out of pointer");
                }
                if (_address == IntPtr.Zero) {
                    // #[] and #[]= say DLError where #to_s and #to_str say ArgumentError;
                    // that is CRuby's split, not a slip.
                    throw new DLError("NULL pointer dereference");
                }
            }

            [RubyMethod("[]")]
            public static object GetByte(Pointer/*!*/ self, [DefaultProtocol]int index) {
                self.CheckLive();
                self.CheckIndex(index, 1);
                return ScriptingRuntimeHelpers.Int32ToObject(Marshal.ReadByte(self._address, index));
            }

            [RubyMethod("[]")]
            public static MutableString/*!*/ GetBytes(Pointer/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol]int length) {
                self.CheckLive();
                self.CheckIndex(start, length);
                return ReadBytes(self._address + start, length);
            }

            [RubyMethod("[]=")]
            public static object SetByte(Pointer/*!*/ self, [DefaultProtocol]int index, [DefaultProtocol]int value) {
                self.CheckLive();
                self.CheckIndex(index, 1);
                Marshal.WriteByte(self._address, index, unchecked((byte)value));
                return ScriptingRuntimeHelpers.Int32ToObject(value);
            }

            [RubyMethod("[]=")]
            public static object SetBytes(Pointer/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol]int length, object value) {
                self.CheckLive();
                self.CheckIndex(start, length);

                var str = value as MutableString;
                if (str != null) {
                    byte[] bytes = str.ToByteArray();
                    Marshal.Copy(bytes, 0, self._address + start, Math.Min(length, bytes.Length));
                    return value;
                }
                if (value == null) {
                    for (int i = 0; i < length; i++) {
                        Marshal.WriteByte(self._address, start + i, 0);
                    }
                    return null;
                }

                IntPtr source = ToAddress(value);
                for (int i = 0; i < length; i++) {
                    Marshal.WriteByte(self._address, start + i, Marshal.ReadByte(source, i));
                }
                return value;
            }

            #endregion

            #region Arithmetic and dereferencing

            [RubyMethod("+")]
            public static Pointer/*!*/ Add(RubyContext/*!*/ context, Pointer/*!*/ self, [DefaultProtocol]int delta) {
                self.CheckLive();
                return Create(context, self._address + delta, self._size - delta, IntPtr.Zero);
            }

            [RubyMethod("-")]
            public static Pointer/*!*/ Subtract(RubyContext/*!*/ context, Pointer/*!*/ self, [DefaultProtocol]int delta) {
                self.CheckLive();
                return Create(context, self._address - delta, self._size + delta, IntPtr.Zero);
            }

            /// <summary>+ptr: the pointer stored at this address.</summary>
            [RubyMethod("ptr")]
            [RubyMethod("+@")]
            public static Pointer/*!*/ Dereference(RubyContext/*!*/ context, Pointer/*!*/ self) {
                self.CheckLive();
                if (self._address == IntPtr.Zero) {
                    throw RubyExceptions.CreateArgumentError("NULL pointer given");
                }
                return Create(context, Marshal.ReadIntPtr(self._address), 0, IntPtr.Zero);
            }

            /// <summary>-ptr: a new cell holding this address, i.e. a pointer to the pointer.</summary>
            [RubyMethod("ref")]
            [RubyMethod("-@")]
            public static Pointer/*!*/ Reference(RubyContext/*!*/ context, Pointer/*!*/ self) {
                self.CheckLive();
                IntPtr cell = Allocate(IntPtr.Size);
                Marshal.WriteIntPtr(cell, self._address);
                var result = Create(context, cell, 0, FreeAddress);
                return result;
            }

            #endregion

            #region Identity and lifetime

            [RubyMethod("to_i")]
            [RubyMethod("to_int")]
            public static object ToInteger(Pointer/*!*/ self) {
                return Protocols.Normalize((long)self._address);
            }

            [RubyMethod("null?")]
            public static bool IsNull(Pointer/*!*/ self) {
                return self._address == IntPtr.Zero;
            }

            [RubyMethod("size")]
            public static object GetSize(Pointer/*!*/ self) {
                return Protocols.Normalize(self._size);
            }

            [RubyMethod("size=")]
            public static object SetSize(Pointer/*!*/ self, [DefaultProtocol]int size) {
                self._size = size;
                return ScriptingRuntimeHelpers.Int32ToObject(size);
            }

            /// <summary>The free function as a callable Fiddle::Function, or nil.</summary>
            [RubyMethod("free")]
            public static object GetFree(RubyContext/*!*/ context, Pointer/*!*/ self) {
                if (self._freeFunc == IntPtr.Zero) {
                    return null;
                }
                return Function.CreateFreeFunction(context, self._freeFunc);
            }

            [RubyMethod("free=")]
            public static object SetFree(Pointer/*!*/ self, object freeFunc) {
                self._freeFunc = ToAddress(freeFunc);
                if (self._freeFunc != IntPtr.Zero) {
                    self.TakeOwnership();
                }
                return freeFunc;
            }

            [RubyMethod("call_free")]
            public static object CallFree(Pointer/*!*/ self) {
                if (self._freed || self._freeFunc == IntPtr.Zero) {
                    return null;
                }
                CallFreeFunction(self._freeFunc, self._address);
                self._freed = true;
                self.Disown();
                return null;
            }

            [RubyMethod("freed?")]
            public static bool IsFreed(Pointer/*!*/ self) {
                return self._freed;
            }

            private static void ReleaseBlock(Pointer/*!*/ self) {
                if (!self._freed && self._address != IntPtr.Zero) {
                    if (self._freeFunc != IntPtr.Zero) {
                        CallFreeFunction(self._freeFunc, self._address);
                    } else if (self._ownsCopy) {
                        Release((self._ownedBlock == IntPtr.Zero) ? self._address : self._ownedBlock);
                    }
                    self._freed = true;
                    self.Disown();
                }
            }

            [RubyMethod("==")]
            [RubyMethod("eql?")]
            public static bool Equal(Pointer/*!*/ self, object other) {
                var ptr = other as Pointer;
                return ptr != null && ptr._address == self._address;
            }

            [RubyMethod("<=>")]
            public static object Compare(Pointer/*!*/ self, object other) {
                var ptr = other as Pointer;
                if (ptr == null) {
                    return null;
                }
                long difference = (long)self._address - (long)ptr._address;
                return ScriptingRuntimeHelpers.Int32ToObject(difference < 0 ? -1 : (difference > 0 ? 1 : 0));
            }

            [RubyMethod("hash")]
            public static int GetHash(Pointer/*!*/ self) {
                return self._address.GetHashCode();
            }

            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, Pointer/*!*/ self) {
                return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture,
                    "#<{0}:0x{1:x16} ptr=0x{2:x16} size={3} free=0x{4:x16}>",
                    context.GetClassName(self),
                    (ulong)RuntimeHelpers.GetHashCode(self),
                    (ulong)(long)self._address,
                    self._size,
                    (ulong)(long)self._freeFunc));
            }

            /// <summary>
            /// CRuby reads the address back as a VALUE - the bit pattern of a live Ruby
            /// object.  IronRuby's objects are CLR objects with no address a foreign
            /// function could have been handed, so there is nothing this could honestly
            /// answer; it says so rather than returning a plausible lie.
            /// </summary>
            [RubyMethod("to_value")]
            public static object ToValue(Pointer/*!*/ self) {
                throw new DLError("Fiddle::Pointer#to_value is not supported on IronRuby: " +
                    "a Ruby object has no address here");
            }

            #endregion
        }
    }
}

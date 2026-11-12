/* ****************************************************************************
 *
 * IronRuby's implementation of Fiddle's C extension (fiddle.so).
 *
 * Fiddle is CRuby's libffi binding: it opens a shared library, looks a symbol up
 * in it, and calls it through a signature that is only known at run time.  The
 * three things that needs are all in the BCL, so none of libffi is required:
 *
 *   dlopen/dlsym/dlclose  ->  System.Runtime.InteropServices.NativeLibrary
 *                             (.Load / .TryGetExport / .Free; .GetMainProgramHandle
 *                             is dlopen(NULL), which is what Fiddle.dlopen(nil) means)
 *   the call itself       ->  a DynamicMethod whose body is a single
 *                             OpCodes.Calli with the runtime-chosen native
 *                             signature (see FiddleFunction.cs)
 *   raw memory            ->  malloc/realloc/free resolved out of the process
 *                             itself, so that Fiddle::RUBY_FREE is the very
 *                             free(3) that frees what Fiddle.malloc returns
 *
 * The pure-Ruby half of fiddle - fiddle/import, fiddle/cparser, fiddle/struct,
 * fiddle/value, fiddle/pack, fiddle/types, fiddle/closure, fiddle/function - is
 * vendored unchanged from the fiddle 1.1.8 gem into Src/StdLib/ironruby/fiddle;
 * it sits on top of exactly the classes defined here, as it does on top of
 * fiddle.so in CRuby.  Src/StdLib/ironruby/fiddle.rb is the entry point.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Fiddle {

    [RubyModule("Fiddle")]
    public static partial class FiddleOps {

        #region Platform

        internal static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// sizeof(long): 4 on Windows (LLP64), 8 on every 64-bit Unix (LP64).  Fiddle's
        /// TYPE_LONG has to follow the C compiler, not the CLR's Int64.
        /// </summary>
        internal static readonly int LongSize = IsWindows ? 4 : IntPtr.Size;

        #endregion

        #region Exceptions

        [RubyException("Error")]
        public class FiddleError : SystemException {
            public FiddleError() : this(null, null) { }
            public FiddleError(string message) : this(message, null) { }
            public FiddleError(string message, Exception inner) : base(message ?? "Fiddle::Error", inner) { }
        }

        [RubyException("DLError")]
        public class DLError : FiddleError {
            public DLError() : this(null, null) { }
            public DLError(string message) : this(message, null) { }
            public DLError(string message, Exception inner) : base(message ?? "Fiddle::DLError", inner) { }
        }

        [RubyException("ClearedReferenceError")]
        public class ClearedReferenceError : FiddleError {
            public ClearedReferenceError() : this(null, null) { }
            public ClearedReferenceError(string message) : this(message, null) { }
            public ClearedReferenceError(string message, Exception inner)
                : base(message ?? "Fiddle::ClearedReferenceError", inner) { }
        }

        #endregion

        #region The process allocator

        /// <summary>
        /// Fiddle hands raw addresses to C and takes raw addresses back, and
        /// Fiddle::RUBY_FREE - the free function Pointer.malloc installs by default, and
        /// which fiddle/struct passes around explicitly - has to be the address of the
        /// free(3) that pairs with whatever allocated the block.  Marshal.AllocHGlobal is
        /// not that on every platform, so malloc/realloc/free are resolved out of the
        /// running process (dlopen(NULL) already has libc in scope) and called through it.
        /// Where that fails - a host that exports no C allocator - Marshal is used and
        /// RUBY_FREE is 0, which is exactly how Fiddle reports "no free function".
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr MallocFn(IntPtr size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ReallocFn(IntPtr ptr, IntPtr size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeFn(IntPtr ptr);

        private static readonly MallocFn _malloc;
        private static readonly ReallocFn _realloc;
        private static readonly FreeFn _free;
        internal static readonly IntPtr FreeAddress;

        static FiddleOps() {
            IntPtr main;
            try {
                main = NativeLibrary.GetMainProgramHandle();
            } catch (Exception) {
                main = IntPtr.Zero;
            }

            IntPtr m, r, f;
            if (main != IntPtr.Zero &&
                NativeLibrary.TryGetExport(main, "malloc", out m) &&
                NativeLibrary.TryGetExport(main, "realloc", out r) &&
                NativeLibrary.TryGetExport(main, "free", out f)) {
                _malloc = Marshal.GetDelegateForFunctionPointer<MallocFn>(m);
                _realloc = Marshal.GetDelegateForFunctionPointer<ReallocFn>(r);
                _free = Marshal.GetDelegateForFunctionPointer<FreeFn>(f);
                FreeAddress = f;
            } else {
                FreeAddress = IntPtr.Zero;
            }

            MainProgramHandle = main;
            // Static field initializers all run before this body, so RUBY_FREE cannot be
            // one of them: it would be read while FreeAddress was still zero.
            RUBY_FREE = Protocols.Normalize((long)FreeAddress);
        }

        internal static readonly IntPtr MainProgramHandle;

        internal static IntPtr Allocate(long size) {
            if (size < 0) {
                throw RubyExceptions.CreateArgumentError("negative size");
            }
            IntPtr result = (_malloc != null) ? _malloc((IntPtr)size) : Marshal.AllocHGlobal((IntPtr)Math.Max(size, 1));
            if (result == IntPtr.Zero) {
                throw new DLError("failed to allocate memory");
            }
            return result;
        }

        internal static IntPtr Reallocate(IntPtr ptr, long size) {
            IntPtr result = (_realloc != null) ? _realloc(ptr, (IntPtr)size) : Marshal.ReAllocHGlobal(ptr, (IntPtr)Math.Max(size, 1));
            if (result == IntPtr.Zero) {
                throw new DLError("failed to allocate memory");
            }
            return result;
        }

        internal static void Release(IntPtr ptr) {
            if (ptr == IntPtr.Zero) {
                return;
            }
            if (_free != null) {
                _free(ptr);
            } else {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// Calls an arbitrary free function - whatever address a Pointer was told to free
        /// itself with.  Never throws: it runs from #call_free, from #free= replacing one,
        /// and from the finalizer, and a throw out of the last of those kills the process.
        /// </summary>
        internal static void CallFreeFunction(IntPtr freeFunc, IntPtr ptr) {
            if (ptr == IntPtr.Zero || freeFunc == IntPtr.Zero) {
                return;
            }
            try {
                if (freeFunc == FreeAddress && _free != null) {
                    _free(ptr);
                } else {
                    Marshal.GetDelegateForFunctionPointer<FreeFn>(freeFunc)(ptr);
                }
            } catch (Exception) {
                // a bad free function is the caller's problem, not a reason to abort
            }
        }

        #endregion

        #region Constants

        [RubyConstant]
        public readonly static bool WINDOWS = IsWindows;

        // dlopen(3) modes.  NativeLibrary does not expose the mode, so these are the values
        // callers pass around rather than anything that reaches dlopen.
        [RubyConstant]
        public readonly static int RTLD_LAZY = 0x00001;
        [RubyConstant]
        public readonly static int RTLD_NOW = 0x00002;
        [RubyConstant]
        public readonly static int RTLD_GLOBAL = 0x00100;

        [RubyConstant]
        public readonly static int SIZEOF_VOIDP = IntPtr.Size;
        [RubyConstant]
        public readonly static int SIZEOF_CHAR = 1;
        [RubyConstant]
        public readonly static int SIZEOF_UCHAR = 1;
        [RubyConstant]
        public readonly static int SIZEOF_SHORT = 2;
        [RubyConstant]
        public readonly static int SIZEOF_USHORT = 2;
        [RubyConstant]
        public readonly static int SIZEOF_INT = 4;
        [RubyConstant]
        public readonly static int SIZEOF_UINT = 4;
        [RubyConstant]
        public readonly static int SIZEOF_LONG = IsWindows ? 4 : IntPtr.Size;
        [RubyConstant]
        public readonly static int SIZEOF_ULONG = IsWindows ? 4 : IntPtr.Size;
        [RubyConstant]
        public readonly static int SIZEOF_LONG_LONG = 8;
        [RubyConstant]
        public readonly static int SIZEOF_ULONG_LONG = 8;
        [RubyConstant]
        public readonly static int SIZEOF_FLOAT = 4;
        [RubyConstant]
        public readonly static int SIZEOF_DOUBLE = 8;
        [RubyConstant]
        public readonly static int SIZEOF_BOOL = 1;
        [RubyConstant]
        public readonly static int SIZEOF_CONST_STRING = IntPtr.Size;
        [RubyConstant]
        public readonly static int SIZEOF_SIZE_T = IntPtr.Size;
        [RubyConstant]
        public readonly static int SIZEOF_SSIZE_T = IntPtr.Size;
        [RubyConstant]
        public readonly static int SIZEOF_PTRDIFF_T = IntPtr.Size;
        [RubyConstant]
        public readonly static int SIZEOF_INTPTR_T = IntPtr.Size;
        [RubyConstant]
        public readonly static int SIZEOF_UINTPTR_T = IntPtr.Size;
        [RubyConstant]
        public readonly static int SIZEOF_INT8_T = 1;
        [RubyConstant]
        public readonly static int SIZEOF_UINT8_T = 1;
        [RubyConstant]
        public readonly static int SIZEOF_INT16_T = 2;
        [RubyConstant]
        public readonly static int SIZEOF_UINT16_T = 2;
        [RubyConstant]
        public readonly static int SIZEOF_INT32_T = 4;
        [RubyConstant]
        public readonly static int SIZEOF_UINT32_T = 4;
        [RubyConstant]
        public readonly static int SIZEOF_INT64_T = 8;
        [RubyConstant]
        public readonly static int SIZEOF_UINT64_T = 8;

        [RubyConstant]
        public readonly static int ALIGN_VOIDP = IntPtr.Size;
        [RubyConstant]
        public readonly static int ALIGN_CHAR = 1;
        [RubyConstant]
        public readonly static int ALIGN_SHORT = 2;
        [RubyConstant]
        public readonly static int ALIGN_INT = 4;
        [RubyConstant]
        public readonly static int ALIGN_LONG = IsWindows ? 4 : IntPtr.Size;
        [RubyConstant]
        public readonly static int ALIGN_LONG_LONG = 8;
        [RubyConstant]
        public readonly static int ALIGN_FLOAT = 4;
        [RubyConstant]
        public readonly static int ALIGN_DOUBLE = 8;
        [RubyConstant]
        public readonly static int ALIGN_BOOL = 1;
        [RubyConstant]
        public readonly static int ALIGN_SIZE_T = IntPtr.Size;
        [RubyConstant]
        public readonly static int ALIGN_SSIZE_T = IntPtr.Size;
        [RubyConstant]
        public readonly static int ALIGN_PTRDIFF_T = IntPtr.Size;
        [RubyConstant]
        public readonly static int ALIGN_INTPTR_T = IntPtr.Size;
        [RubyConstant]
        public readonly static int ALIGN_UINTPTR_T = IntPtr.Size;
        [RubyConstant]
        public readonly static int ALIGN_INT8_T = 1;
        [RubyConstant]
        public readonly static int ALIGN_INT16_T = 2;
        [RubyConstant]
        public readonly static int ALIGN_INT32_T = 4;
        [RubyConstant]
        public readonly static int ALIGN_INT64_T = 8;

        // The type codes are fiddle's own (ext/fiddle/fiddle.h).  A negative code is the
        // unsigned flavour of its positive twin, which is why TYPE_SIZE_T is -TYPE_LONG.
        [RubyConstant]
        public readonly static int TYPE_VOID = FiddleType.Void;
        [RubyConstant]
        public readonly static int TYPE_VOIDP = FiddleType.VoidP;
        [RubyConstant]
        public readonly static int TYPE_CHAR = FiddleType.Char;
        [RubyConstant]
        public readonly static int TYPE_UCHAR = -FiddleType.Char;
        [RubyConstant]
        public readonly static int TYPE_SHORT = FiddleType.Short;
        [RubyConstant]
        public readonly static int TYPE_USHORT = -FiddleType.Short;
        [RubyConstant]
        public readonly static int TYPE_INT = FiddleType.Int;
        [RubyConstant]
        public readonly static int TYPE_UINT = -FiddleType.Int;
        [RubyConstant]
        public readonly static int TYPE_LONG = FiddleType.Long;
        [RubyConstant]
        public readonly static int TYPE_ULONG = -FiddleType.Long;
        [RubyConstant]
        public readonly static int TYPE_LONG_LONG = FiddleType.LongLong;
        [RubyConstant]
        public readonly static int TYPE_ULONG_LONG = -FiddleType.LongLong;
        [RubyConstant]
        public readonly static int TYPE_FLOAT = FiddleType.Float;
        [RubyConstant]
        public readonly static int TYPE_DOUBLE = FiddleType.Double;
        [RubyConstant]
        public readonly static int TYPE_VARIADIC = FiddleType.Variadic;
        [RubyConstant]
        public readonly static int TYPE_CONST_STRING = FiddleType.ConstString;
        [RubyConstant]
        public readonly static int TYPE_BOOL = FiddleType.Bool;

        // The fixed-width aliases map onto whichever C type has that width here.
        [RubyConstant]
        public readonly static int TYPE_INT8_T = FiddleType.Char;
        [RubyConstant]
        public readonly static int TYPE_UINT8_T = -FiddleType.Char;
        [RubyConstant]
        public readonly static int TYPE_INT16_T = FiddleType.Short;
        [RubyConstant]
        public readonly static int TYPE_UINT16_T = -FiddleType.Short;
        [RubyConstant]
        public readonly static int TYPE_INT32_T = FiddleType.Int;
        [RubyConstant]
        public readonly static int TYPE_UINT32_T = -FiddleType.Int;
        [RubyConstant]
        public readonly static int TYPE_INT64_T = IsWindows ? FiddleType.LongLong : FiddleType.Long;
        [RubyConstant]
        public readonly static int TYPE_UINT64_T = IsWindows ? -FiddleType.LongLong : -FiddleType.Long;
        [RubyConstant]
        public readonly static int TYPE_SIZE_T = IsWindows ? -FiddleType.LongLong : -FiddleType.Long;
        [RubyConstant]
        public readonly static int TYPE_SSIZE_T = IsWindows ? FiddleType.LongLong : FiddleType.Long;
        [RubyConstant]
        public readonly static int TYPE_PTRDIFF_T = IsWindows ? FiddleType.LongLong : FiddleType.Long;
        [RubyConstant]
        public readonly static int TYPE_INTPTR_T = IsWindows ? FiddleType.LongLong : FiddleType.Long;
        [RubyConstant]
        public readonly static int TYPE_UINTPTR_T = IsWindows ? -FiddleType.LongLong : -FiddleType.Long;

        /// <summary>
        /// The address of free(3).  Pointer.malloc installs it as the free function, and
        /// fiddle/struct passes it explicitly; 0 means "there is no free function", which
        /// is how Fiddle already spells a pointer it does not own.
        /// </summary>
        [RubyConstant]
        public readonly static object RUBY_FREE;

        /// <summary>
        /// CRuby exposes the bit patterns of its own immediates here.  Nothing in IronRuby
        /// has them, and nothing portable reads them; the values are MRI's so that code
        /// comparing against them still parses.
        /// </summary>
        [RubyConstant]
        public readonly static int Qnil = 4;
        [RubyConstant]
        public readonly static int Qtrue = 20;
        [RubyConstant]
        public readonly static int Qfalse = 0;
        [RubyConstant]
        public readonly static int Qundef = 36;

        #endregion

        #region Module methods

        [RubyMethod("dlopen", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("dlopen", RubyMethodAttributes.PrivateInstance)]
        public static Handle/*!*/ DlOpen(RubyContext/*!*/ context, object self, object library) {
            return Handle.OpenLibrary(context.GetClass(typeof(Handle)), library, RTLD_LAZY | RTLD_GLOBAL);
        }

        [RubyMethod("malloc", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("malloc", RubyMethodAttributes.PrivateInstance)]
        public static object Malloc(object self, [DefaultProtocol]int size) {
            return Protocols.Normalize((long)Allocate(size));
        }

        [RubyMethod("realloc", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("realloc", RubyMethodAttributes.PrivateInstance)]
        public static object Realloc(object self, object address, [DefaultProtocol]int size) {
            return Protocols.Normalize((long)Reallocate(ToAddress(address), size));
        }

        [RubyMethod("free", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("free", RubyMethodAttributes.PrivateInstance)]
        public static object Free(object self, object address) {
            Release(ToAddress(address));
            return null;
        }

        /// <summary>errno, as the last foreign call left it.</summary>
        /// <summary>
        /// Under CRuby this is errno after the last foreign call.  It is not filled in here;
        /// see the head of FiddleCall.cs for why the CLR makes errno unreadable after a
        /// call.  What remains is the per-thread slot itself, which Ruby can read and write.
        /// </summary>
        [RubyMethod("last_error", RubyMethodAttributes.PublicSingleton)]
        public static object GetLastError(object self) {
            return _lastError;
        }

        [RubyMethod("last_error=", RubyMethodAttributes.PublicSingleton)]
        public static object SetLastError(object self, object value) {
            _lastError = value;
            return value;
        }

        [RubyMethod("win32_last_error", RubyMethodAttributes.PublicSingleton)]
        public static object GetWin32LastError(object self) {
            return _win32LastError;
        }

        [RubyMethod("win32_last_error=", RubyMethodAttributes.PublicSingleton)]
        public static object SetWin32LastError(object self, object value) {
            _win32LastError = value;
            return value;
        }

        [RubyMethod("win32_last_socket_error", RubyMethodAttributes.PublicSingleton)]
        public static object GetWin32LastSocketError(object self) {
            return _win32LastSocketError;
        }

        [RubyMethod("win32_last_socket_error=", RubyMethodAttributes.PublicSingleton)]
        public static object SetWin32LastSocketError(object self, object value) {
            _win32LastSocketError = value;
            return value;
        }

        // Fiddle's errno is per-thread, as the C one is.
        [ThreadStatic]
        private static object _lastError;
        [ThreadStatic]
        private static object _win32LastError;
        [ThreadStatic]
        private static object _win32LastSocketError;

        #endregion

        #region Address conversion

        /// <summary>
        /// The address behind any of the things Fiddle lets stand in for one: an Integer, a
        /// Pointer, a Handle, a Function, a Closure, nil.  Anything else is asked for #to_i
        /// by the caller, which is what fiddle's own TO_PTR does.
        /// </summary>
        internal static IntPtr ToAddress(object value) {
            if (value == null) {
                return IntPtr.Zero;
            }
            if (value is int) {
                return (IntPtr)(int)value;
            }
            if (value is BigInteger) {
                return (IntPtr)(long)(BigInteger)value;
            }
            if (value is long) {
                return (IntPtr)(long)value;
            }
            var ptr = value as Pointer;
            if (ptr != null) {
                return ptr.Address;
            }
            var handle = value as Handle;
            if (handle != null) {
                return handle.Address;
            }
            var function = value as Function;
            if (function != null) {
                return function.Address;
            }
            var closure = value as Closure;
            if (closure != null) {
                return closure.Address;
            }
            throw RubyExceptions.CreateTypeError("cannot convert to an address");
        }

        #endregion
    }

    /// <summary>
    /// fiddle's type codes, as ext/fiddle/fiddle.h numbers them.  Kept in one place
    /// because the constants above, the argument conversion and the IL emitter all have to
    /// agree on them.
    /// </summary>
    internal static class FiddleType {
        internal const int Void = 0;
        internal const int VoidP = 1;
        internal const int Char = 2;
        internal const int Short = 3;
        internal const int Int = 4;
        internal const int Long = 5;
        internal const int LongLong = 6;
        internal const int Float = 7;
        internal const int Double = 8;
        internal const int Variadic = 9;
        internal const int ConstString = 10;
        internal const int Bool = 11;
    }
}

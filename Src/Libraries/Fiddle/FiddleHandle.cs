/* ****************************************************************************
 *
 * Fiddle::Handle - dlopen(3)/dlsym(3)/dlclose(3) on NativeLibrary.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Fiddle {

    public static partial class FiddleOps {

        /// <summary>
        /// An open shared library.  <see cref="NativeLibrary"/> is a real dlopen/dlsym on
        /// POSIX and LoadLibrary/GetProcAddress on Windows, so a Handle here is the same
        /// thing it is under CRuby, with two differences worth knowing:
        ///
        ///   - The RTLD_* flags are recorded but not honoured: NativeLibrary.Load takes no
        ///     mode.  It resolves lazily and adds the library to the global scope, which is
        ///     the RTLD_LAZY | RTLD_GLOBAL that Fiddle.dlopen asks for anyway.
        ///   - Handle::NEXT (RTLD_NEXT) cannot be expressed at all; looking a symbol up
        ///     through it raises DLError rather than quietly answering the wrong address.
        ///
        /// Handle.new with no argument is dlopen(NULL) - the running program, with libc and
        /// everything else already loaded in its scope - which is what Fiddle.dlopen(nil)
        /// means and what Handle::DEFAULT is.
        /// </summary>
        [RubyClass("Handle")]
        public sealed class Handle : RubyObject {
            private IntPtr _address;
            private MutableString _fileName;
            private int _flags;
            private bool _open;
            private bool _enableClose;

            /// <summary>
            /// True for the dlopen(NULL) handle, which must never be dlclose()d and whose
            /// symbol lookup is the process-wide one.
            /// </summary>
            private bool _mainProgram;

            /// <summary>Handle::NEXT, which has no address to look anything up in.</summary>
            private bool _next;

            public Handle(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Handle(ImmediateClass.NominalClass);
            }

            internal IntPtr Address {
                get { return _address; }
            }

            internal static Handle/*!*/ OpenLibrary(RubyClass/*!*/ cls, object library, int flags) {
                var result = new Handle(cls);
                Reinitialize(result, library, flags);
                return result;
            }

            private static void Reinitialize(Handle/*!*/ self, object library, int flags) {
                self._flags = flags;
                self._open = true;
                self._enableClose = false;
                self._next = false;

                MutableString name = library as MutableString;
                if (library != null && name == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion into String");
                }

                if (name == null) {
                    self._mainProgram = true;
                    self._fileName = null;
                    self._address = MainProgramHandle;
                    if (self._address == IntPtr.Zero) {
                        throw new DLError("could not open the main program");
                    }
                    return;
                }

                self._mainProgram = false;
                try {
                    self._address = NativeLibrary.Load(name.ToString());
                } catch (Exception e) {
                    // On glibc the CLR appends dlerror(3)'s own text as the last line of the
                    // exception message, and that is the text CRuby reports.
                    throw new DLError(LastLine(e.Message) ?? ("could not open library " + name.ToString()));
                }
                // #file_name is the path the loader settled on, not the name asked for -
                // "libc.so.6" opens /lib/x86_64-linux-gnu/libc.so.6 - so ask the loader.
                string resolved = ResolvedPath(self._address);
                self._fileName = (resolved == null) ? name : MutableString.CreateMutable(resolved, name.Encoding);
            }

            /// <summary>
            /// dlinfo(handle, RTLD_DI_LINKMAP, &amp;map) answers the struct link_map the loader
            /// keeps for a library; its second field is the path it was loaded from.  glibc
            /// only: elsewhere - and on Windows - this answers null and the name the caller
            /// gave is reported instead.
            /// </summary>
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int DlInfoFn(IntPtr handle, int request, IntPtr info);

            private const int RTLD_DI_LINKMAP = 2;
            private static DlInfoFn _dlinfo;
            private static bool _dlinfoResolved;

            private static string ResolvedPath(IntPtr handle) {
                if (!_dlinfoResolved) {
                    _dlinfoResolved = true;
                    IntPtr address;
                    if (!IsWindows && MainProgramHandle != IntPtr.Zero &&
                        NativeLibrary.TryGetExport(MainProgramHandle, "dlinfo", out address)) {
                        _dlinfo = Marshal.GetDelegateForFunctionPointer<DlInfoFn>(address);
                    }
                }
                if (_dlinfo == null || handle == IntPtr.Zero) {
                    return null;
                }

                IntPtr cell = Marshal.AllocHGlobal(IntPtr.Size);
                try {
                    Marshal.WriteIntPtr(cell, IntPtr.Zero);
                    if (_dlinfo(handle, RTLD_DI_LINKMAP, cell) != 0) {
                        return null;
                    }
                    IntPtr map = Marshal.ReadIntPtr(cell);
                    if (map == IntPtr.Zero) {
                        return null;
                    }
                    // struct link_map { ElfW(Addr) l_addr; char *l_name; ... }
                    IntPtr namePtr = Marshal.ReadIntPtr(map, IntPtr.Size);
                    string path = (namePtr == IntPtr.Zero) ? null : Marshal.PtrToStringUTF8(namePtr);
                    return String.IsNullOrEmpty(path) ? null : path;
                } catch (Exception) {
                    return null;
                } finally {
                    Marshal.FreeHGlobal(cell);
                }
            }

            private static string LastLine(string message) {
                if (String.IsNullOrEmpty(message)) {
                    return null;
                }
                string[] lines = message.Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--) {
                    string line = lines[i].Trim();
                    if (line.Length != 0) {
                        return line;
                    }
                }
                return null;
            }

            #region Construction

            [RubyConstructor]
            public static object Create(BlockParam block, RubyClass/*!*/ self, [DefaultParameterValue(null)]object library,
                [DefaultParameterValue(RTLD_LAZY_GLOBAL)]int flags) {

                var result = new Handle(self);
                Reinitialize(result, library, flags);
                if (block != null) {
                    object blockResult;
                    try {
                        if (block.Yield(result, out blockResult)) {
                            return blockResult;
                        }
                    } finally {
                        if (result._open) {
                            Close(result);
                        }
                    }
                }
                return result;
            }

            internal const int RTLD_LAZY_GLOBAL = 0x00001 | 0x00100;

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(BlockParam block, Handle/*!*/ self, [DefaultParameterValue(null)]object library,
                [DefaultParameterValue(RTLD_LAZY_GLOBAL)]int flags) {

                Reinitialize(self, library, flags);
                if (block != null) {
                    object blockResult;
                    try {
                        if (block.Yield(self, out blockResult)) {
                            return blockResult;
                        }
                    } finally {
                        if (self._open) {
                            Close(self);
                        }
                    }
                }
                return self;
            }

            /// <summary>
            /// Handle::NEXT.  RTLD_NEXT is a dlsym pseudo-handle that means "the next
            /// definition after mine", and nothing in NativeLibrary expresses it; the
            /// object exists so that code mentioning the constant loads, and every symbol
            /// lookup through it says so rather than answering something else.
            /// </summary>
            [RubyMethod("__make_next__", RubyMethodAttributes.PublicSingleton)]
            public static Handle/*!*/ MakeNext(RubyClass/*!*/ self) {
                var result = new Handle(self);
                result._open = true;
                result._next = true;
                return result;
            }

            #endregion

            #region Symbols

            /// <summary>
            /// The address of an export, or zero.  On Windows a name is tried as CRuby's fiddle
            /// tries it (fiddle_handle_find_func): as given, then with the ANSI "A" suffix - the
            /// Win32 API exports FillConsoleOutputCharacterA and ...W but no
            /// FillConsoleOutputCharacter, which reline asks for - then with the stdcall
            /// decorations "@0" to "@252", plain and after the "A".
            /// </summary>
            private IntPtr Lookup(string/*!*/ name) {
                if (!_open) {
                    throw new DLError("closed handle");
                }
                if (_next) {
                    throw new DLError("RTLD_NEXT is not supported on this platform");
                }
                IntPtr address = Export(name);
                if (address != IntPtr.Zero || !OperatingSystem.IsWindows()) {
                    return address;
                }
                if ((address = Export(name + "A")) != IntPtr.Zero) {
                    return address;
                }
                foreach (string stem in new[] { name, name + "A" }) {
                    for (int i = 0; i < 256; i += 4) {
                        if ((address = Export(stem + "@" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))) != IntPtr.Zero) {
                            return address;
                        }
                    }
                }
                return IntPtr.Zero;
            }

            private IntPtr Export(string/*!*/ name) {
                try {
                    IntPtr address;
                    return NativeLibrary.TryGetExport(_address, name, out address) ? address : IntPtr.Zero;
                } catch (Exception) {
                    return IntPtr.Zero;
                }
            }

            [RubyMethod("sym")]
            [RubyMethod("[]")]
            public static object Symbol(Handle/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ name) {
                IntPtr address = self.Lookup(name.ToString());
                if (address == IntPtr.Zero) {
                    throw new DLError("unknown symbol \"" + name.ToString() + "\"");
                }
                return Protocols.Normalize((long)address);
            }

            /// <summary>The address, as CRuby answers it, or nil - not true/false.</summary>
            [RubyMethod("sym_defined?")]
            public static object IsSymbolDefined(Handle/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ name) {
                IntPtr address = self.Lookup(name.ToString());
                return (address == IntPtr.Zero) ? null : Protocols.Normalize((long)address);
            }

            #endregion

            #region Lifetime

            [RubyMethod("close")]
            public static object Close(Handle/*!*/ self) {
                if (!self._open) {
                    throw new DLError("closed handle");
                }
                self._open = false;
                if (!self._mainProgram && !self._next && self._address != IntPtr.Zero) {
                    NativeLibrary.Free(self._address);
                }
                return ScriptingRuntimeHelpers.Int32ToObject(0);
            }

            [RubyMethod("close_enabled?")]
            public static bool IsCloseEnabled(Handle/*!*/ self) {
                return self._enableClose;
            }

            [RubyMethod("enable_close")]
            public static object EnableClose(Handle/*!*/ self) {
                self._enableClose = true;
                return null;
            }

            [RubyMethod("disable_close")]
            public static object DisableClose(Handle/*!*/ self) {
                self._enableClose = false;
                return null;
            }

            /// <summary>
            /// CRuby's escape hatch for handles closed behind Fiddle's back.  Nothing here
            /// can be closed behind its back, so there is nothing to disable.
            /// </summary>
            [RubyMethod("disable_closed_handle_check")]
            public static object DisableClosedHandleCheck(Handle/*!*/ self) {
                return null;
            }

            #endregion

            #region Inspection

            [RubyMethod("to_i")]
            [RubyMethod("to_int")]
            public static object ToInteger(Handle/*!*/ self) {
                return Protocols.Normalize((long)self._address);
            }

            [RubyMethod("to_ptr")]
            public static Pointer/*!*/ ToPointer(RubyContext/*!*/ context, Handle/*!*/ self) {
                return Pointer.Create(context, self._address, 0, IntPtr.Zero);
            }

            [RubyMethod("file_name")]
            public static MutableString FileName(Handle/*!*/ self) {
                return (self._fileName == null) ? null : self._fileName.Clone();
            }

            #endregion
        }
    }
}

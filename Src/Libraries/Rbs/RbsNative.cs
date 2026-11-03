/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Rbs {

    /// <summary>
    /// librbs: the rbs gem's parser (its src/ and include/, unmodified) with
    /// Src/Libraries/Rbs/native/rbs_ironruby.c, built by Util/build-librbs.sh and shipped
    /// next to the IronRuby assemblies as libprism is.
    ///
    /// The library is loaded by path and its five entry points bound to delegates, rather
    /// than through [DllImport]: an assembly can have only one DllImport resolver, and
    /// IronRuby.Libraries is shared by every library in it.
    /// </summary>
    internal static class RbsNative {
        // rbs_ironruby.c's RBS_IR_ABI_VERSION
        private const int AbiVersion = 1;

        internal const int Ok = 1;
        internal const int Error = 0;
        internal const int UnknownEncoding = -1;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AbiVersionFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InitFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeFn(IntPtr data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ParseFn(int kind, IntPtr source, int sourceLength, IntPtr encodingName,
            int startPos, int endPos, IntPtr variables, int variableCount, int flags, out IntPtr result, out UIntPtr resultLength);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int LexFn(IntPtr source, int sourceLength, IntPtr encodingName, int endPos,
            out IntPtr result, out UIntPtr resultLength);

        private static readonly object _lock = new object();
        private static bool _loaded;
        private static string _loadError;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RbsVersionFn();

        private static string _rbsVersion;
        private static FreeFn _free;
        private static ParseFn _parse;
        private static LexFn _lex;

        private static readonly string[] FileNames =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { "librbs.dll", "rbs.dll" } :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new[] { "librbs.dylib" } :
            new[] { "librbs.so" };

        private static IEnumerable<string> SearchDirectories() {
            yield return AppContext.BaseDirectory;
            string location = typeof(RbsNative).Assembly.Location;
            if (!String.IsNullOrEmpty(location)) {
                string own = Path.GetDirectoryName(location);
                if (!String.IsNullOrEmpty(own)) yield return own;
            }
        }

        /// <summary>
        /// Loads librbs, or throws the LoadError `require "rbs_extension"` should raise when
        /// it is not there.
        /// </summary>
        internal static void EnsureLoaded() {
            lock (_lock) {
                if (_loaded) return;
                if (_loadError != null) throw new LoadError(_loadError);

                IntPtr handle = IntPtr.Zero;
                foreach (string dir in SearchDirectories()) {
                    foreach (string fileName in FileNames) {
                        string path = Path.Combine(dir, fileName);
                        if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle)) goto found;
                    }
                }
                foreach (string fileName in FileNames) {
                    if (NativeLibrary.TryLoad(fileName, out handle)) goto found;
                }

                _loadError = "cannot load such file -- rbs_extension (IronRuby was built without librbs; " +
                    "run Util/build-librbs.sh and rebuild)";
                throw new LoadError(_loadError);

            found:
                var abi = Bind<AbiVersionFn>(handle, "rbs_ir_abi_version");
                if (abi() != AbiVersion) {
                    _loadError = String.Format("cannot load such file -- rbs_extension (librbs speaks ABI {0}, IronRuby expects {1}; rebuild it with Util/build-librbs.sh)",
                        abi(), AbiVersion);
                    throw new LoadError(_loadError);
                }
                Bind<InitFn>(handle, "rbs_ir_init")();
                _rbsVersion = Marshal.PtrToStringAnsi(Bind<RbsVersionFn>(handle, "rbs_ir_rbs_version")());
                _free = Bind<FreeFn>(handle, "rbs_ir_free");
                _parse = Bind<ParseFn>(handle, "rbs_ir_parse");
                _lex = Bind<LexFn>(handle, "rbs_ir_lex");
                _loaded = true;
            }
        }

        /// <summary>The version of the rbs gem librbs was built from.</summary>
        internal static string/*!*/ RbsVersion {
            get {
                EnsureLoaded();
                return _rbsVersion;
            }
        }

        private static T Bind<T>(IntPtr handle, string name) where T : Delegate {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
        }

        /// <summary>
        /// The source as the lexer wants it: the bytes and a NUL behind them, as a Ruby
        /// String's buffer has.
        /// </summary>
        private static byte[] Terminated(byte[] bytes) {
            var result = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        private static byte[] CString(string str) {
            var bytes = System.Text.Encoding.ASCII.GetBytes(str);
            return Terminated(bytes);
        }

        /// <summary>
        /// The parser keeps interned names in one global constant pool, which CRuby's GVL
        /// guards for the C extension; here every call into librbs takes the same lock.
        /// </summary>
        internal static int Parse(int kind, byte[]/*!*/ source, string/*!*/ encodingName, int startPos, int endPos,
            byte[] variables, int variableCount, int flags, out byte[] result) {

            EnsureLoaded();
            byte[] src = Terminated(source);
            byte[] enc = CString(encodingName);
            var hSrc = GCHandle.Alloc(src, GCHandleType.Pinned);
            var hEnc = GCHandle.Alloc(enc, GCHandleType.Pinned);
            GCHandle hVars = variables != null ? GCHandle.Alloc(variables, GCHandleType.Pinned) : default(GCHandle);
            try {
                lock (_lock) {
                    IntPtr data;
                    UIntPtr length;
                    int status = _parse(kind, hSrc.AddrOfPinnedObject(), source.Length, hEnc.AddrOfPinnedObject(), startPos, endPos,
                        variables != null ? hVars.AddrOfPinnedObject() : IntPtr.Zero, variableCount, flags, out data, out length);
                    result = Take(data, length);
                    return status;
                }
            } finally {
                hSrc.Free();
                hEnc.Free();
                if (variables != null) hVars.Free();
            }
        }

        internal static int Lex(byte[]/*!*/ source, string/*!*/ encodingName, int endPos, out byte[] result) {
            EnsureLoaded();
            byte[] src = Terminated(source);
            byte[] enc = CString(encodingName);
            var hSrc = GCHandle.Alloc(src, GCHandleType.Pinned);
            var hEnc = GCHandle.Alloc(enc, GCHandleType.Pinned);
            try {
                lock (_lock) {
                    IntPtr data;
                    UIntPtr length;
                    int status = _lex(hSrc.AddrOfPinnedObject(), source.Length, hEnc.AddrOfPinnedObject(), endPos, out data, out length);
                    result = Take(data, length);
                    return status;
                }
            } finally {
                hSrc.Free();
                hEnc.Free();
            }
        }

        private static byte[] Take(IntPtr data, UIntPtr length) {
            if (data == IntPtr.Zero) return Array.Empty<byte>();
            try {
                var bytes = new byte[checked((int)length.ToUInt64())];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return bytes;
            } finally {
                _free(data);
            }
        }
    }
}

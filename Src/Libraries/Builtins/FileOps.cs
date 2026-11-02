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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using IronRuby.Compiler;
using System.Globalization;
using IronRuby.Runtime.Conversions;
using System.Runtime.CompilerServices;
using System.Reflection;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {

    /// <summary>
    /// File builtin class. Derives from IO
    /// </summary>
    [RubyClass("File", Extends = typeof(RubyFile))]
    public static class RubyFileOps {
        internal static bool FileExists(RubyContext/*!*/ context, MutableString/*!*/ path) {
            return context.Platform.FileExists(context.DecodePath(path));
        }

        internal static bool DirectoryExists(RubyContext/*!*/ context, MutableString/*!*/ path) {
            return context.Platform.DirectoryExists(context.DecodePath(path));
        }

        internal static bool Exists(RubyContext/*!*/ context, MutableString/*!*/ path) {
            var strPath = context.DecodePath(path);
            return context.Platform.DirectoryExists(strPath) || context.Platform.FileExists(strPath);
        }

        #region Construction

        [RubyConstructor]
        public static RubyFile/*!*/ CreateFile(
            ConversionStorage<int?>/*!*/ toInt,
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toPath,
            ConversionStorage<MutableString>/*!*/ toStr,
            RubyClass/*!*/ self,
            object descriptorOrPath, 
            [Optional]object optionsOrMode, 
            [Optional]object optionsOrPermissions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            return Reinitialize(toInt, toHash, toPath, toStr, new RubyFile(self.Context), descriptorOrPath, optionsOrMode, optionsOrPermissions, options);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyFile/*!*/ Reinitialize(
            ConversionStorage<int?>/*!*/ toInt,
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toPath,
            ConversionStorage<MutableString>/*!*/ toStr,
            RubyFile/*!*/ self,
            object descriptorOrPath, 
            [Optional]object optionsOrMode, 
            [Optional]object optionsOrPermissions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            var context = self.Context;
            
            Protocols.TryConvertToOptions(toHash, ref options, ref optionsOrMode, ref optionsOrPermissions);
            var toIntSite = toInt.GetSite(TryConvertToFixnumAction.Make(toInt.Context));

            // A nil mode or permission means "not given", as in CRuby.
            IOInfo info = new IOInfo();
            if (optionsOrMode != Missing.Value && optionsOrMode != null) {
                int? m = toIntSite.Target(toIntSite, optionsOrMode);
                info = m.HasValue ? new IOInfo(IOModeNative.ToIOMode(m.Value)) : IOInfo.Parse(context, Protocols.CastToString(toStr, optionsOrMode));
            }

            int permissions = 0;
            if (optionsOrPermissions != Missing.Value && optionsOrPermissions != null) {
                int? p = toIntSite.Target(toIntSite, optionsOrPermissions);
                if (!p.HasValue) {
                    throw RubyExceptions.CreateImplicitConversionError(context.GetClassName(optionsOrPermissions), "Integer");
                }
                permissions = p.Value;
            }

            if (options != null) {
                info = info.AddOptions(toStr, options);

                // perm: says what the third argument would, and MRI refuses both at once.
                object permOption;
                if (options.TryGetValue(context.CreateAsciiSymbol("perm"), out permOption) && permOption != null) {
                    if (optionsOrPermissions != Missing.Value && optionsOrPermissions != null) {
                        throw RubyExceptions.CreateArgumentError("perm specified twice");
                    }
                    int? p = toIntSite.Target(toIntSite, permOption);
                    if (!p.HasValue) {
                        throw RubyExceptions.CreateImplicitConversionError(context.GetClassName(permOption), "Integer");
                    }
                    permissions = p.Value;
                }
            }
            self.ConversionOptions = options;
            
            // descriptor or path:
            int? descriptor = toIntSite.Target(toIntSite, descriptorOrPath);
            if (descriptor.HasValue) {
                RubyIOOps.Reinitialize(self, descriptor.Value, info);

                // File.new(fd, path: "...") names the descriptor; File#path answers it.
                object path;
                if (options != null && options.TryGetValue(context.CreateAsciiSymbol("path"), out path) && path != null) {
                    MutableString strPath = Protocols.CastToString(toStr, path);
                    self.Path = context.DecodePath(strPath);
                    self.PathEncoding = strPath.Encoding;
                }
            } else {
                Reinitialize(self, Protocols.CastToPath(toPath, descriptorOrPath), info, permissions);
            }

            return self;
        }

        private static void Reinitialize(RubyFile/*!*/ file, MutableString/*!*/ path, IOInfo info, int permission) {
            var strPath = file.Context.DecodePath(path);

            // open(2) only honours the permission argument when it actually creates the
            // file, and applies the umask to it.
            bool creating = (info.Mode & IOMode.CreateIfNotExists) != 0
                && !file.Context.Platform.FileExists(strPath);

            var stream = RubyFile.OpenFileStream(file.Context, strPath, info.Mode);

            if (creating && permission != 0 && Posix.IsAvailable) {
                int umask = NativeUmask(0);
                NativeUmask(umask);
                int errno;
                Posix.Chmod(strPath, permission & ~umask, out errno);
            }

            file.Path = strPath;
            file.PathEncoding = path.Encoding;
            file.Mode = info.Mode;
            file.SetStream(stream);
            file.SetFileDescriptor(file.Context.AllocateFileDescriptor(stream));

            if (info.HasEncoding) {
                file.SetEncodings(info.ExternalEncoding, info.InternalEncoding);
            } else if ((info.Mode & IOMode.PreserveEndOfLines) != 0) {
                // The "b" flag with no explicit encoding means BINARY.
                file.SetEncodings(RubyEncoding.Binary, null);
            } else {
                // MRI still runs the defaults through rb_io_ext_int_to_encs when the stream is
                // opened, so a stream opened while a default internal encoding was in force keeps
                // the external encoding it had then, rather than following later reassignments.
                file.SetEncodings(null, null);
            }
        }
        
        #endregion

        #region Declared Constants

        static RubyFileOps() {
            // On Unix '\\' is an ordinary filename character and PATH is colon
            // separated; File::ALT_SEPARATOR is nil there, as in CRuby.
            ALT_SEPARATOR = IsWindows
                ? MutableString.CreateAscii(AltDirectorySeparatorChar.ToString()).Freeze()
                : null;
            SEPARATOR = MutableString.CreateAscii(DirectorySeparatorChar.ToString()).Freeze();
            Separator = SEPARATOR;
            PATH_SEPARATOR = MutableString.CreateAscii(PathSeparatorChar.ToString()).Freeze();
        }

        private const char AltDirectorySeparatorChar = '\\';
        private const char DirectorySeparatorChar = '/';

        internal static readonly bool IsWindows = System.IO.Path.DirectorySeparatorChar == '\\';

        private static char PathSeparatorChar {
            get { return IsWindows ? ';' : ':'; }
        }

        private static readonly char[] SeparatorChars = IsWindows
            ? new[] { DirectorySeparatorChar, AltDirectorySeparatorChar }
            : new[] { DirectorySeparatorChar };

        internal static bool IsDirectorySeparator(int c) {
            return c == DirectorySeparatorChar || (IsWindows && c == AltDirectorySeparatorChar);
        }

        [RubyConstant]
        public readonly static MutableString ALT_SEPARATOR;

        [RubyConstant]
        public readonly static MutableString PATH_SEPARATOR;

        [RubyConstant]
        public readonly static MutableString SEPARATOR;

        [RubyConstant]
        public readonly static MutableString Separator = SEPARATOR;

        private const string NUL_VALUE = "NUL";

        [RubyModule("Constants")]
        public static class Constants {
            [RubyConstant]
            public readonly static int APPEND = IOModeNative.Append;
            [RubyConstant]
            public readonly static int BINARY = IOModeNative.Binary;
            [RubyConstant]
            public readonly static int CREAT = IOModeNative.CreateIfNotExists;
            [RubyConstant]
            public readonly static int EXCL = IOModeNative.ErrorIfExists;
            [RubyConstant]
            public readonly static int FNM_CASEFOLD = 0x08;
            [RubyConstant]
            public readonly static int FNM_DOTMATCH = 0x04;
            [RubyConstant]
            public readonly static int FNM_NOESCAPE = 0x01;
            [RubyConstant]
            public readonly static int FNM_PATHNAME = 0x02;
            [RubyConstant]
            public readonly static int FNM_EXTGLOB = Glob.FnmExtGlob;
            [RubyConstant]
            public readonly static int FNM_SHORTNAME = 0x00;
            // FNM_CASEFOLD only on a case insensitive file system; 0 on Unix.
            [RubyConstant]
            public readonly static int FNM_SYSCASE = Glob.FnmSysCase;
            [RubyConstant]
            public readonly static int LOCK_EX = 0x02;
            [RubyConstant]
            public readonly static int LOCK_NB = 0x04;
            [RubyConstant]
            public readonly static int LOCK_SH = 0x01;
            [RubyConstant]
            public readonly static int LOCK_UN = 0x08;
            [RubyConstant]
            public readonly static int NONBLOCK = IOModeNative.NonBlocking;
            [RubyConstant]
            public readonly static int NOCTTY = IOModeNative.NoControllingTerminal;
            [RubyConstant]
            public readonly static int SYNC = IOModeNative.Synchronized;
            [RubyConstant]
            public readonly static int SHARE_DELETE = IOModeNative.ShareDelete;
            [RubyConstant]
            public readonly static int RDONLY = 0;
            [RubyConstant]
            public readonly static int RDWR = 2;
            [RubyConstant]
            public readonly static int TRUNC = IOModeNative.Truncate;
            [RubyConstant]
            public readonly static int WRONLY = 1;
        }

        #endregion

        internal const int WriteModeMask = 0x80; // Oct 0200
        internal const int ReadWriteMode = 0x1B6; // Oct 0666

        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ Open() {
            return RubyIOOps.Open();
        }

        #region chmod, chown, lchmod, lchown, umask

        [RubyMethod("chmod")]
        public static int Chmod(RubyFile/*!*/ self, [DefaultProtocol]int permission) {
            self.RequireInitialized();
            if (Posix.IsAvailable) {
                int fd = GetNativeFileDescriptor(self);
                int errno;
                if (fd >= 0) {
                    if (Posix.FChmod(fd, permission, out errno) != 0) {
                        throw Posix.Error(errno, self.Path);
                    }
                    return 0;
                }
            }
            if (self.Path == null) {
                throw new NotSupportedException("TODO: cannot chmod for files without path");
            }
            Chmod(self.Path, permission);
            return 0;
        }

        [RubyMethod("chmod", RubyMethodAttributes.PublicSingleton)]
        public static int Chmod(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, [DefaultProtocol]int permission, params object[]/*!*/ paths) {
            foreach (var path in paths) {
                Chmod(self.Context.DecodePath(Protocols.CastToPath(toPath, path)), permission);
            }
            return paths.Length;
        }

        [RubyMethod("lchmod", RubyMethodAttributes.PublicSingleton)]
        public static int LChmod(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, [DefaultProtocol]int permission, params object[]/*!*/ paths) {
            // Linux has no lchmod(2): the permission bits of a symbolic link are
            // meaningless there, so there is nothing correct to do.
            throw new IronRuby.Builtins.NotImplementedError("lchmod() function is unimplemented on this machine");
        }

        internal static void Chmod(string/*!*/ path, int permission) {
#if FEATURE_FILESYSTEM
            if (Posix.IsAvailable) {
                int errno;
                if (Posix.Chmod(path, permission, out errno) != 0) {
                    throw Posix.Error(errno, path);
                }
                return;
            }
            FileAttributes oldAttributes = File.GetAttributes(path);
            if ((permission & WriteModeMask) == 0) {
                File.SetAttributes(path, oldAttributes | FileAttributes.ReadOnly);
            } else {
                File.SetAttributes(path, oldAttributes & ~FileAttributes.ReadOnly);
            }
#endif
        }

        private static int ToUidGid(RubyContext/*!*/ context, object value, bool owner) {
            if (value == null) {
                return -1;
            }
            if (value is int) {
                return (int)value;
            }
            throw RubyExceptions.CreateUnexpectedTypeError(context, value, "Integer");
        }

        [RubyMethod("chown")]
        public static int ChangeOwner(RubyContext/*!*/ context, RubyFile/*!*/ self, object owner, object group) {
            self.RequireInitialized();
            int uid = ToUidGid(context, owner, true);
            int gid = ToUidGid(context, group, false);

            if (Posix.IsAvailable) {
                int fd = GetNativeFileDescriptor(self);
                int errno;
                if (fd >= 0) {
                    if (Posix.FChown(fd, uid, gid, out errno) != 0) {
                        throw Posix.Error(errno, self.Path);
                    }
                }
            }
            return 0;
        }

        [RubyMethod("chown", RubyMethodAttributes.PublicSingleton)]
        public static int ChangeOwner(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object owner, object group,
            params object[]/*!*/ paths) {

            return ChangeOwner(toPath, self, owner, group, paths, true);
        }

        [RubyMethod("lchown", RubyMethodAttributes.PublicSingleton)]
        public static int LChangeOwner(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object owner, object group,
            params object[]/*!*/ paths) {

            return ChangeOwner(toPath, self, owner, group, paths, false);
        }

        private static int ChangeOwner(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object owner, object group,
            object[]/*!*/ paths, bool followLinks) {

            var context = self.Context;
            int uid = ToUidGid(context, owner, true);
            int gid = ToUidGid(context, group, false);

            foreach (var path in paths) {
                string strPath = context.DecodePath(Protocols.CastToPath(toPath, path));
                if (Posix.IsAvailable) {
                    int errno;
                    int rc = followLinks ? Posix.Chown(strPath, uid, gid, out errno) : Posix.LChown(strPath, uid, gid, out errno);
                    if (rc != 0) {
                        throw Posix.Error(errno, strPath);
                    }
                } else if (!Exists(context, Protocols.CastToPath(toPath, path))) {
                    throw RubyExceptions.CreateENOENT("No such file or directory - {0}", strPath);
                }
            }
            return paths.Length;
        }

        internal static readonly object UmaskKey = new object();

        private static bool IsUnixPlatform {
            get { return System.IO.Path.DirectorySeparatorChar == '/'; }
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "umask")]
        private static extern int NativeUmask(int mask);

        [RubyMethod("umask", RubyMethodAttributes.PublicSingleton)]
        public static int GetUmask(RubyClass/*!*/ self, [DefaultProtocol]int mask) {
            if (IsUnixPlatform) {
                return NativeUmask(mask);
            }
            int result = (int)self.Context.GetOrCreateLibraryData(UmaskKey, () => 0);
            self.Context.TrySetLibraryData(UmaskKey, CalculateUmask(mask));
            return result;
        }

        [RubyMethod("umask", RubyMethodAttributes.PublicSingleton)]
        public static int GetUmask(RubyClass/*!*/ self) {
            if (IsUnixPlatform) {
                int current = NativeUmask(0);
                NativeUmask(current);
                return current;
            }
            return (int)self.Context.GetOrCreateLibraryData(UmaskKey, () => 0);
        }

        private static int CalculateUmask(int mask) {
            return (mask % 512) / 128 * 128;
        }
        
        #endregion

        #region delete, unlink, truncate, rename

        [RubyMethod("delete", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("unlink", RubyMethodAttributes.PublicSingleton)]
        public static int Delete(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            string strPath = self.Context.DecodePath(Protocols.CastToPath(toPath, path));

            if (Posix.IsAvailable) {
                // unlink(2) rather than System.IO: the FileExists guard below answers false for a
                // symbolic link that points at a directory -- System.IO sees a directory -- so
                // removing such a link reported ENOENT rather than doing it. Letting the kernel
                // decide also gets EISDIR, EPERM and EACCES right without guessing.
                int errno;
                if (Posix.Unlink(strPath, out errno) != 0) {
                    throw Posix.Error(errno, strPath);
                }
                return 1;
            }

            if (!self.Context.Platform.FileExists(strPath)) {
                throw RubyExceptions.CreateENOENT("{0}", strPath);
            }

            Delete(self.Context, strPath);
            return 1;
        }

        internal static void Delete(RubyContext/*!*/ context, string/*!*/ path) {
            try {
                context.Platform.DeleteFile(path, true);
#if FEATURE_FILESYSTEM
            } catch (DirectoryNotFoundException) {
                throw RubyExceptions.CreateENOENT("No such file or directory - {0}", path);
#endif
            } catch (IOException e) {
                throw Errno.CreateEACCES(e.Message, e);
            }
        }

        [RubyMethod("delete", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("unlink", RubyMethodAttributes.PublicSingleton)]
        public static int Delete(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, params object[] paths) {
            foreach (MutableString path in paths) {
                Delete(toPath, self, path);
            }

            return paths.Length;
        }

#if FEATURE_FILESYSTEM
        // The size is an off_t: a file of 4 GiB and more is an ordinary thing to ask for.
        [RubyMethod("truncate", BuildConfig = "FEATURE_FILESYSTEM")]
        public static int Truncate(ConversionStorage<IntegerValue>/*!*/ integerConversion, RubyFile/*!*/ self, object length) {
            long size = Protocols.CastToInteger(integerConversion, length).ToInt64();
            self.RequireWritable();
            if (size < 0) {
                throw self.Path != null ? RubyExceptions.CreateEINVAL("Invalid argument - {0}", self.Path) : RubyExceptions.CreateEINVAL();
            }

            // ftruncate(2) leaves the file offset alone; Stream.SetLength pulls it back
            // to the new end, which would make a later write land in the wrong place.
            long position = self.Position;
            self.Length = size;
            self.Position = position;
            return 0;
        }

        [RubyMethod("truncate", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int Truncate(ConversionStorage<MutableString>/*!*/ toPath, ConversionStorage<IntegerValue>/*!*/ integerConversion,
            RubyClass/*!*/ self, object path, object length) {
            string strPath = self.Context.DecodePath(Protocols.CastToPath(toPath, path));
            long size = Protocols.CastToInteger(integerConversion, length).ToInt64();
            if (size < 0) {
                throw RubyExceptions.CreateEINVAL("Invalid argument - {0}", strPath);
            }
            using (RubyFile f = new RubyFile(self.Context, strPath, IOMode.ReadWrite)) {
                f.Length = size;
            }
            return 0;
        }
#endif

        [RubyMethod("rename", RubyMethodAttributes.PublicSingleton)]
        public static int Rename(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object oldPath, object newPath) {
            var context = self.Context;

            string strOldPath = context.DecodePath(Protocols.CastToPath(toPath, oldPath));
            string strNewPath = context.DecodePath(Protocols.CastToPath(toPath, newPath));

            if (strOldPath.Length == 0 || strNewPath.Length == 0) {
                throw RubyExceptions.CreateENOENT();
            }

            // MRI names both ends of a failed rename: "No such file or directory - (old, new)".
            string paths = "(" + strOldPath + ", " + strNewPath + ")";
            if (!context.Platform.FileExists(strOldPath) && !context.Platform.DirectoryExists(strOldPath)) {
                throw RubyExceptions.CreateENOENT("No such file or directory - {0}", paths);
            }

            if (RubyUtils.ExpandPath(context.Platform, strOldPath) == RubyUtils.ExpandPath(context.Platform, strNewPath)) {
                return 0;
            }

            if (context.Platform.FileExists(strNewPath)) {
                Delete(context, strNewPath);
            }

            try {
                context.Platform.MoveFileSystemEntry(strOldPath, strNewPath);
            } catch (DirectoryNotFoundException e) {
                throw RubyExceptions.CreateENOENT(e, "No such file or directory - {0}", paths);
            } catch (FileNotFoundException e) {
                throw RubyExceptions.CreateENOENT(e, "No such file or directory - {0}", paths);
            } catch (IOException e) {
                throw Errno.CreateEACCES(e.Message, e);
            }

            return 0;
        }

        #endregion

        #region path, basename, dirname, extname, expand_path, absolute_path, fnmatch

        [RubyMethod("path", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ ToPath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return Protocols.CastToPath(toPath, path);
        }

        [RubyMethod("basename", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ BaseName(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self,
            object path, [DefaultProtocol, NotNull, Optional]MutableString suffix) {
            return BaseName(Protocols.CastToPath(toPath, path), suffix);
        }

        private static MutableString/*!*/ BaseName(MutableString/*!*/ path, MutableString suffix) {
            if (path.IsEmpty) {
                return path;
            }

            string strPath = path.ConvertToString();
            string[] parts = strPath.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0) {
                return MutableString.CreateMutable(path.Encoding).Append((char)path.GetLastChar()).TaintBy(path);
            }

            bool isWindows = Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX;
            if (isWindows) {
                string first = parts[0];
                if (strPath.Length >= 2 && IsDirectorySeparator(strPath[0]) && IsDirectorySeparator(strPath[1])) {
                    // UNC: skip 2 parts. The root's base name is its trailing separator as written
                    // ("\\foo\bar\" gives "\"), or "/" when there is none (MRI).
                    if (parts.Length <= 2) {
                        char lastChar = strPath[strPath.Length - 1];
                        return MutableString.CreateMutable(path.Encoding)
                            .Append(IsDirectorySeparator(lastChar) ? lastChar : DirectorySeparatorChar).TaintBy(path);
                    }
                } else if (first.Length == 2 && Tokenizer.IsLetter(first[0]) && first[1] == ':') {
                    // skip drive letter "X:"
                    if (parts.Length <= 1) {
                        var result = MutableString.CreateMutable(path.Encoding).TaintBy(path);
                        if (strPath.Length > 2) {
                            result.Append(strPath[2]);
                        }
                        return result;
                    }
                }
            }

            string last = parts[parts.Length - 1];
            // "C:x" is x relative to drive C's current directory: its base name is "x" (MRI).
            if (isWindows && parts.Length == 1 && IsDriveRelative(strPath)) {
                last = last.Substring(2);
            }
            if (MutableString.IsNullOrEmpty(suffix)) {
                return MutableString.CreateMutable(last, path.Encoding);
            }

            StringComparison comparison = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int matchLength = last.Length;

            string strSuffix = suffix.ToString();
            if (strSuffix == ".*") {
                // ".*" is the only wildcard rmext() understands; any other suffix,
                // including ".t*", is matched literally.
                matchLength = last.Length - FindExtension(last).Length;
            } else if (last.EndsWith(strSuffix, comparison)) {
                matchLength = last.Length - strSuffix.Length;
            }

            // Stripping the suffix never leaves nothing behind: basename("bar", "bar")
            // is "bar", not "".
            if (matchLength == 0) {
                matchLength = last.Length;
            }

            return MutableString.CreateMutable(path.Encoding).Append(last, 0, matchLength).TaintBy(path);
        }

        [RubyMethod("dirname", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ DirName(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return DirName(Protocols.CastToPath(toPath, path));
        }

        [RubyMethod("dirname", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ DirName(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [DefaultProtocol]int level) {

            if (level < 0) {
                throw RubyExceptions.CreateArgumentError("negative level: {0}", level);
            }

            MutableString result = Protocols.CastToPath(toPath, path);
            if (level == 0) {
                // Level 0 removes no component, but still normalises: the leading run of
                // slashes collapses and an empty path becomes ".", as in CRuby.
                string strPath = result.ConvertToString();
                if (!IsWindows) {
                    strPath = NormalizeRoot(strPath);
                }
                return MutableString.CreateMutable(strPath, result.Encoding).TaintBy(result);
            }

            for (int i = 0; i < level; i++) {
                MutableString next = DirName(result);
                // "/" and "." are fixed points; stop early so a huge level is cheap.
                if (next.ConvertToString() == result.ConvertToString()) {
                    return next;
                }
                result = next;
            }
            return result;
        }

        private static MutableString/*!*/ DirName(MutableString/*!*/ path) {
            if (!IsWindows) {
                return MutableString.CreateMutable(PosixDirName(path.ConvertToString()), path.Encoding).TaintBy(path);
            }
            return WindowsDirName(path);
        }

        /// <summary>
        /// dirname(3) as CRuby implements it on Unix: a leading run of slashes collapses
        /// to a single "/", interior runs are preserved, and a path with no slash left is ".".
        /// </summary>
        private static string/*!*/ NormalizeRoot(string/*!*/ path) {
            int start = 0;
            while (start < path.Length && path[start] == '/') {
                start++;
            }

            if (start == 0) {
                return path.Length == 0 ? "." : path;
            }
            return "/" + path.Substring(start);
        }

        private static string/*!*/ PosixDirName(string/*!*/ path) {
            int start = 0;
            while (start < path.Length && path[start] == '/') {
                start++;
            }

            bool rooted = start > 0;
            if (start == path.Length) {
                return rooted ? "/" : ".";
            }

            // Everything after the root: "/////foo/bar/" is treated as root + "foo/bar/".
            string rest = path.Substring(start);

            int end = rest.Length;
            while (end > 0 && rest[end - 1] == '/') {
                end--;
            }

            int slash = rest.LastIndexOf('/', end - 1);
            if (slash < 0) {
                return rooted ? "/" : ".";
            }

            while (slash > 0 && rest[slash - 1] == '/') {
                slash--;
            }

            return (rooted ? "/" : "") + rest.Substring(0, slash);
        }

        /// <summary>
        /// "C:" or "C:name" with no separator after the colon: a name relative to the current
        /// directory of drive C.
        /// </summary>
        private static bool IsDriveRelative(string/*!*/ path) {
            return path.Length >= 2 && Tokenizer.IsLetter(path[0]) && path[1] == ':' &&
                path.IndexOfAny(SeparatorChars, 2) < 0;
        }

        private static MutableString/*!*/ WindowsDirName(MutableString/*!*/ path) {
            string strPath = path.ConvertToString();
            string directoryName = strPath;

            // MRI: the directory of "C:" and of "C:x" is "C:.", the current directory of drive C.
            // pathname tells a DOS-ish platform by File.dirname("A:") == "A:.", and without it
            // Pathname#absolute? was false for every "C:/..." path.
            if (IsDriveRelative(strPath)) {
                return MutableString.CreateMutable(strPath.Substring(0, 2) + ".", path.Encoding);
            }

            // More than two leading separators are a UNC prefix all the same: "\\\foo\bar" is
            // \\foo\bar, the root of share bar on server foo (MRI).
            int leading = 0;
            while (leading < strPath.Length && IsDirectorySeparator(strPath[leading])) {
                leading++;
            }
            if (leading > 2 && leading < strPath.Length) {
                strPath = strPath.Substring(leading - 2);
                directoryName = strPath;
            }

            if (IsValidPath(strPath)) {
                strPath = StripPathCharacters(strPath);

                // handle top-level UNC paths
                directoryName = Path.GetDirectoryName(strPath);
                if (directoryName == null) {
                    return MutableString.CreateMutable(strPath, path.Encoding);
                }

                string fileName = Path.GetFileName(strPath);
                if (!String.IsNullOrEmpty(fileName)) {
                    directoryName = StripPathCharacters(strPath.Substring(0, strPath.LastIndexOf(fileName, StringComparison.Ordinal)));
                }
            } else {
                if (directoryName.Length > 1) {
                    directoryName = "//";
                }
            }

            directoryName = String.IsNullOrEmpty(directoryName) ? "." : directoryName;
            return MutableString.CreateMutable(directoryName, path.Encoding);
        }

        private static bool IsValidPath(string path) {
            foreach (char c in path) {
                if (c != '/' && c != '\\') {
                    return true;
                }
            }
            return false;

        }

        private static string StripPathCharacters(string path) {
            int limit = 0;
            for (int charIndex = path.Length - 1; charIndex > 0; charIndex--) {
                if (!((path[charIndex] == '/') || (path[charIndex] == '\\')))
                    break;
                limit++;
            }
            if (limit > 0) {
                limit--;
                if (path.Length == 3 && path[1] == ':') limit--;
                return path.Substring(0, path.Length - limit - 1);
            }
            return path;
        }

        [RubyMethod("extname", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ GetExtension(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            MutableString pathStr = Protocols.CastToPath(toPath, path);
            string last = LastPathComponent(pathStr.ConvertToString());
            string extension = FindExtension(last);

            // CRuby hands back a shared empty binary string when there is no extension,
            // so File.extname("foo").encoding is ASCII-8BIT, not the path's encoding.
            return extension.Length == 0
                ? MutableString.CreateBinary().TaintBy(pathStr)
                : MutableString.Create(extension, pathStr.Encoding).TaintBy(pathStr);
        }

        private static string/*!*/ LastPathComponent(string/*!*/ path) {
            for (int i = path.Length - 1; i >= 0; i--) {
                if (IsDirectorySeparator(path[i])) {
                    return path.Substring(i + 1);
                }
            }
            return path;
        }

        /// <summary>
        /// ruby_enc_find_extname: leading dots belong to the name ("/.config" is hidden,
        /// not an extension of ""), and the extension runs from the last remaining dot to
        /// the end, so "foo." has the extension ".".
        /// </summary>
        private static string/*!*/ FindExtension(string/*!*/ name) {
            int start = 0;
            while (start < name.Length && name[start] == '.') {
                start++;
            }

            if (start >= name.Length) {
                return "";
            }

            int dot = name.LastIndexOf('.');
            return dot < start ? "" : name.Substring(dot);
        }

        [RubyMethod("expand_path", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ ExpandPath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [DefaultParameterValue(null)]object basePath) {
            var context = self.Context;
            MutableString pathStr = Protocols.CastToPath(toPath, path);
            RequireUsablePathEncoding(context, pathStr);

            string result = RubyUtils.ExpandPath(
                context.Platform,
                ExpandTilde(context, DecodePathLike(context, pathStr)),
                (basePath == null)
                    ? context.Platform.CurrentDirectory
                    : ExpandTilde(context, DecodePathLike(context, Protocols.CastToPath(toPath, basePath))),
                false
            );

            return EncodePathLike(context, result, pathStr);
        }

        /// <summary>
        /// "~" uses $HOME and falls back to the user database; "~name" always goes to the
        /// user database.  A $HOME that is empty or relative is an error, as in CRuby, and
        /// an unknown user name is an ArgumentError rather than a silent pass-through.
        /// </summary>
        private static string/*!*/ ExpandTilde(RubyContext/*!*/ context, string/*!*/ path) {
            if (path.Length == 0 || path[0] != '~') {
                return path;
            }

            if (IsWindows) {
                return ExpandWindowsTilde(context, path);
            }

            int slash = path.IndexOf('/', 1);
            string userName = (slash < 0) ? path.Substring(1) : path.Substring(1, slash - 1);
            string rest = (slash < 0) ? null : path.Substring(slash + 1);

            string home;
            if (userName.Length == 0) {
                home = RubyEnvironment.GetVariable(context.Platform, "HOME");
                if (home == null) {
                    home = Posix.GetHomeDirectory(Posix.GetEUid());
                }
                if (home == null) {
                    // MRI's rb_default_home_dir names the real uid, whatever it looked up
                    throw RubyExceptions.CreateArgumentError("couldn't find home for uid '{0}'", Posix.GetUid());
                }
                if (home.Length == 0 || home[0] != DirectorySeparatorChar) {
                    throw RubyExceptions.CreateArgumentError("non-absolute home");
                }
            } else {
                home = Posix.GetHomeDirectory(userName);
                if (home == null) {
                    throw RubyExceptions.CreateArgumentError("user {0} doesn't exist", userName);
                }
            }

            return (rest == null) ? home : home + "/" + rest;
        }

        /// <summary>
        /// "~" on Windows, where there is no user database to ask. MRI looks at $HOME, then at
        /// $USERPROFILE, then at $HOMEDRIVE + $HOMEPATH, as Dir.home does, and refuses "~name"
        /// outright. Leaving
        /// the tilde alone - which is what this used to do - made Dir.home, Gem's user directory
        /// and every "~/.somethingrc" resolve inside the working directory.
        /// </summary>
        private static string/*!*/ ExpandWindowsTilde(RubyContext/*!*/ context, string/*!*/ path) {
            int slash = path.IndexOfAny(SeparatorChars, 1);
            string userName = (slash < 0) ? path.Substring(1) : path.Substring(1, slash - 1);
            string rest = (slash < 0) ? null : path.Substring(slash + 1);

            if (userName.Length != 0) {
                throw RubyExceptions.CreateArgumentError("can't find user {0}", userName);
            }

            string home = RubyEnvironment.GetVariable(context.Platform, "HOME");
            if (home == null) {
                home = RubyEnvironment.GetVariable(context.Platform, "USERPROFILE");
            }
            if (home == null) {
                string drive = RubyEnvironment.GetVariable(context.Platform, "HOMEDRIVE");
                string tail = RubyEnvironment.GetVariable(context.Platform, "HOMEPATH");
                if (tail != null) {
                    home = (drive ?? "") + tail;
                }
            }
            if (home == null) {
                throw RubyExceptions.CreateArgumentError("couldn't find HOME environment -- expanding '~'");
            }

            home = home.Replace(AltDirectorySeparatorChar, DirectorySeparatorChar);
            return (rest == null) ? home : home + "/" + rest;
        }

        /// <summary>
        /// A path in the filesystem encoding has to survive a trip through a .NET string, and one
        /// whose default external encoding is not ASCII compatible cannot: there is no byte
        /// sequence for a separator to look for. MRI reports the same thing, as an incompatibility
        /// between the path and the external encoding.
        /// </summary>
        private static void RequireUsablePathEncoding(RubyContext/*!*/ context, MutableString/*!*/ path) {
            var external = context.DefaultExternalEncoding;
            if (external != null && !external.IsAsciiIdentity) {
                throw RubyExceptions.CreateEncodingCompatibilityError(path.Encoding, external);
            }
        }

        /// <summary>
        /// A BINARY path is a sequence of bytes MRI never interprets, so it crosses the .NET
        /// string layer one byte to one character rather than being decoded - decoding it would
        /// replace whatever is not valid in the filesystem encoding, and the bytes would be gone.
        /// </summary>
        private static string/*!*/ DecodePathLike(RubyContext/*!*/ context, MutableString/*!*/ path) {
            if (path.Encoding != RubyEncoding.Binary) {
                return context.DecodePath(path);
            }

            int count = path.GetByteCount();
            var chars = new char[count];
            for (int i = 0; i < count; i++) {
                chars[i] = (char)path.GetByte(i);
            }
            return new String(chars);
        }

        /// <summary>
        /// The path methods hand back a string in the encoding of the path they were
        /// given, not in the filesystem encoding.
        /// </summary>
        private static MutableString/*!*/ EncodePathLike(RubyContext/*!*/ context, string/*!*/ result, MutableString/*!*/ original) {
            if (original.Encoding == RubyEncoding.Binary) {
                // the counterpart of DecodePathLike; anything above U+00FF did not come from
                // this path's bytes, so that one is left to the general route below
                var bytes = new byte[result.Length];
                int i = 0;
                for (; i < result.Length; i++) {
                    if (result[i] > 0xFF) {
                        break;
                    }
                    bytes[i] = (byte)result[i];
                }
                if (i == result.Length) {
                    return MutableString.CreateBinary(bytes).TaintBy(original);
                }
            }

            // Encode with the filesystem encoding and then relabel, rather than transcode:
            // the bytes came from the OS, and a path whose bytes are not representable in
            // the argument's encoding still has to come back (CRuby "forces" the encoding).
            MutableString encoded = context.EncodePath(result);
            encoded.ForceEncoding(original.Encoding);
            return encoded.TaintBy(original);
        }

        [RubyMethod("absolute_path", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ AbsolutePath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [DefaultParameterValue(null)]object basePath) {
            var context = self.Context;

            MutableString pathStr = Protocols.CastToPath(toPath, path);
            string result = RubyUtils.ExpandPath(
                context.Platform,
                context.DecodePath(pathStr),
                (basePath == null) ? context.Platform.CurrentDirectory : context.DecodePath(Protocols.CastToPath(toPath, basePath)),
                false
            );

            return EncodePathLike(context, result, pathStr);
        }

        [RubyMethod("absolute_path?", RubyMethodAttributes.PublicSingleton)]
        public static bool IsAbsolutePath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            // No expansion and no filesystem access: "~" and "C:/x" are relative on Unix.
            string strPath = Protocols.CastToPath(toPath, path).ConvertToString();
            if (IsWindows) {
                return strPath.Length >= 3 && Tokenizer.IsLetter(strPath[0]) && strPath[1] == ':' && IsDirectorySeparator(strPath[2])
                    || strPath.Length >= 2 && IsDirectorySeparator(strPath[0]) && IsDirectorySeparator(strPath[1]);
            }
            return strPath.Length > 0 && strPath[0] == DirectorySeparatorChar;
        }

        [RubyMethod("fnmatch", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("fnmatch?", RubyMethodAttributes.PublicSingleton)]
        public static bool FnMatch(ConversionStorage<MutableString>/*!*/ toPath, object/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ pattern, object path, [DefaultProtocol, Optional]int flags) {

            return Glob.FnMatch(pattern.ConvertToString(), Protocols.CastToPath(toPath, path).ConvertToString(), flags);
        }

        #endregion

        #region split, join

        [RubyMethod("split", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray Split(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            MutableString p = Protocols.CastToPath(toPath, path);
            RubyArray result = new RubyArray(2);
            result.Add(DirName(p));
            result.Add(BaseName(p, null));
            return result;
        }

        [RubyMethod("join", RubyMethodAttributes.PublicSingleton)]
        public static MutableString Join(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, params object[]/*!*/ parts) {
            MutableString result = null;
            Dictionary<object, bool> visitedLists = null;
            var worklist = new Stack<object>();
            int current = 0;
            MutableString str;

            Push(worklist, parts);
            while (worklist.Count > 0) {
                object part = worklist.Pop();
                var list = part as IList;
                if (list != null) {
                    if (list.Count == 0) {
                        str = MutableString.FrozenEmpty;
                    } else if (visitedLists != null && visitedLists.ContainsKey(list)) {
                        throw RubyExceptions.CreateArgumentError("recursive array");
                    } else {
                        if (visitedLists == null) {
                            visitedLists = new Dictionary<object, bool>(ReferenceEqualityComparer<object>.Instance);
                        }
                        visitedLists.Add(list, true);
                        Push(worklist, list);
                        continue;
                    }
                } else if (part == null) {
                    throw RubyExceptions.CreateImplicitConversionError("NilClass", "String");
                } else {
                    try {
                        str = Protocols.CastToPath(toPath, part);
                    } catch (ArgumentException e) when (e.Message == "path name contains null byte") {
                        // rb_file_join reports a NUL as a plain string problem.
                        throw RubyExceptions.CreateArgumentError("string contains null byte");
                    }
                }

                if (current > 0) {
                    // Append negotiates the encodings, so the join of two EUC-JP paths
                    // stays EUC-JP instead of decaying to binary.
                    AppendDirectoryName(result, str);
                } else {
                    result = MutableString.CreateMutable(str.Encoding).Append(str);
                }
                current++;
            }

            return result ?? MutableString.CreateEmpty();
        }

        private static void Push(Stack<Object>/*!*/ stack, IList/*!*/ values) {
            for (int i = values.Count - 1; i >= 0; i--) {
                stack.Push(values[i]);
            }
        }

        private static void AppendDirectoryName(MutableString/*!*/ result, MutableString/*!*/ name) {
            int resultLength = result.GetCharCount();

            int i;
            for (i = resultLength - 1; i >= 0; i--) {
                if (!IsDirectorySeparator(result.GetChar(i))) {
                    break;
                }
            }

            if (i == resultLength - 1) {
                if (!IsDirectorySeparator(name.GetFirstChar())) {
                    result.Append(DirectorySeparatorChar);
                }
                result.Append(name);
            } else if (IsDirectorySeparator(name.GetFirstChar())) {
                result.Replace(i + 1, resultLength - i - 1, name);
            } else {
                result.Append(name);
            }
        }

        #endregion

        #region flock, readlink, link, symlink

#if FEATURE_FILESYSTEM
        /// <summary>
        /// IronRuby's "file descriptor" is an index into a per-context table, not
        /// an OS descriptor, so syscalls that need a real fd have to dig the
        /// handle out of the underlying FileStream.
        /// </summary>
        internal static int GetNativeFileDescriptor(RubyIO/*!*/ io) {
            var stream = io.GetStream().BaseStream;
            var fs = stream as FileStream;
            if (fs == null) {
                return -1;
            }
            return (int)fs.SafeFileHandle.DangerousGetHandle();
        }

        [RubyMethod("flock", BuildConfig = "FEATURE_FILESYSTEM")]
        public static object FileLock(RubyFile/*!*/ self, [DefaultProtocol]int operation) {
            self.RequireInitialized();
            if (OperatingSystem.IsWindows()) {
                return WindowsFileLock(self, operation);
            }
            if (!Posix.IsFlockAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("flock() function is unimplemented on this machine");
            }

            int fd = GetNativeFileDescriptor(self);
            if (fd < 0) {
                throw RubyExceptions.CreateEBADF();
            }

            int errno;
            bool nonBlocking = (operation & Posix.LOCK_NB) != 0;

            // A blocking flock(2) is issued as poll + sleep rather than as one blocking call:
            // a thread parked inside a P/Invoke is reported as running by the CLR and cannot
            // be woken, so Thread#status would never say "sleep" and Thread#kill would never
            // land. MRI's flock is interruptible, so this is also the closer behaviour.
            while (true) {
                if (Posix.Flock(fd, operation | Posix.LOCK_NB, out errno) == 0) {
                    return 0;
                }
                if (errno != Posix.EWouldBlock) {
                    throw Posix.Error(errno, self.Path);
                }
                if (nonBlocking) {
                    // LOCK_NB on a locked file reports "would block" rather than raising
                    return false;
                }
                System.Threading.Thread.Sleep(10);
            }
        }

        private const int LOCKFILE_FAIL_IMMEDIATELY = 1;
        private const int LOCKFILE_EXCLUSIVE_LOCK = 2;
        private const int ERROR_LOCK_VIOLATION = 33;
        private const int ERROR_NOT_LOCKED = 158;
        private const int ERROR_IO_PENDING = 997;

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        private static extern bool LockFileEx(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int flags, int reserved,
            uint lengthLow, uint lengthHigh, ref System.Threading.NativeOverlapped overlapped);

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        private static extern bool UnlockFileEx(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int reserved,
            uint lengthLow, uint lengthHigh, ref System.Threading.NativeOverlapped overlapped);

        /// <summary>
        /// flock on Windows, as CRuby's win32.c does it: LockFileEx/UnlockFileEx over the
        /// whole file, shared or exclusive. A blocking lock polls with FAIL_IMMEDIATELY for
        /// the reason the Posix path above does. RubyGems takes one for every binstub it
        /// writes, so without it `gem install` of any gem with an executable failed.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static object WindowsFileLock(RubyFile/*!*/ self, int operation) {
            var fs = self.GetStream().BaseStream as FileStream;
            if (fs == null) {
                throw RubyExceptions.CreateEBADF();
            }
            // Windows locks are mandatory: what is buffered has to reach the file first.
            fs.Flush();

            var overlapped = new System.Threading.NativeOverlapped();
            if ((operation & Posix.LOCK_UN) != 0) {
                if (!UnlockFileEx(fs.SafeFileHandle, 0, UInt32.MaxValue, UInt32.MaxValue, ref overlapped)) {
                    int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    // Unlocking a file that is not locked is not an error for flock(2).
                    if (error != ERROR_NOT_LOCKED) {
                        throw RubyExceptions.CreateEINVAL("{0}", self.Path);
                    }
                }
                return 0;
            }

            int flags = LOCKFILE_FAIL_IMMEDIATELY;
            if ((operation & Posix.LOCK_EX) != 0) {
                flags |= LOCKFILE_EXCLUSIVE_LOCK;
            } else if ((operation & Posix.LOCK_SH) == 0) {
                throw RubyExceptions.CreateEINVAL("{0}", self.Path);
            }

            while (true) {
                overlapped = new System.Threading.NativeOverlapped();
                if (LockFileEx(fs.SafeFileHandle, flags, 0, UInt32.MaxValue, UInt32.MaxValue, ref overlapped)) {
                    return 0;
                }
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (error != ERROR_LOCK_VIOLATION && error != ERROR_IO_PENDING) {
                    throw RubyExceptions.CreateEINVAL("{0}", self.Path);
                }
                if ((operation & Posix.LOCK_NB) != 0) {
                    return false;
                }
                System.Threading.Thread.Sleep(10);
            }
        }

        [RubyMethod("readlink", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static MutableString/*!*/ Readlink(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            string strPath = self.Context.DecodePath(Protocols.CastToPath(toPath, path));
            if (!Posix.IsAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("readlink() function is unimplemented on this machine");
            }

            int errno;
            string target = Posix.ReadLink(strPath, out errno);
            if (target == null) {
                throw Posix.Error(errno, strPath);
            }
            return self.Context.EncodePath(target);
        }

        [RubyMethod("link", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int Link(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object oldPath, object newPath) {
            string strOld = self.Context.DecodePath(Protocols.CastToPath(toPath, oldPath));
            string strNew = self.Context.DecodePath(Protocols.CastToPath(toPath, newPath));
            if (!Posix.IsAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("link() function is unimplemented on this machine");
            }

            int errno;
            if (Posix.Link(strOld, strNew, out errno) != 0) {
                // MRI: an existing target is reported by its own path, anything else by both paths.
                throw Posix.Error(errno, errno == Posix.EEXIST ? strNew : "(" + strOld + ", " + strNew + ")");
            }
            return 0;
        }

        [RubyMethod("symlink", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int SymLink(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object oldPath, object newPath) {
            string strOld = self.Context.DecodePath(Protocols.CastToPath(toPath, oldPath));
            string strNew = self.Context.DecodePath(Protocols.CastToPath(toPath, newPath));
            if (!Posix.IsAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("symlink() function is unimplemented on this machine");
            }

            int errno;
            if (Posix.Symlink(strOld, strNew, out errno) != 0) {
                throw Posix.Error(errno, errno == Posix.EEXIST ? strNew : "(" + strOld + ", " + strNew + ")");
            }
            return 0;
        }

        [RubyMethod("mkfifo", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int MakeFifo(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [DefaultParameterValue(0x1B6 /* 0666 */)]int mode) {

            string strPath = self.Context.DecodePath(Protocols.CastToPath(toPath, path));
            if (!Posix.IsAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("mkfifo() function is unimplemented on this machine");
            }

            int errno;
            if (Posix.MkFifo(strPath, mode, out errno) != 0) {
                throw Posix.Error(errno, strPath);
            }
            return 0;
        }

        #region realpath, realdirpath

        /// <summary>
        /// Resolves ".", ".." and every symbolic link in <paramref name="path"/>.
        /// When <paramref name="strict"/> the whole path must exist (realpath);
        /// otherwise only everything but the last component must (realdirpath).
        /// </summary>
        private static string/*!*/ ResolvePath(RubyContext/*!*/ context, string/*!*/ path, string basedir, bool strict) {
            int links = 0;
            return ResolvePath(context, path, basedir, strict, ref links);
        }

        // The symlink budget is shared across the whole resolution, not reset per component:
        // a link that points at itself ("a" -> "a") otherwise recurses until the stack dies.
        private static string/*!*/ ResolvePath(RubyContext/*!*/ context, string/*!*/ path, string basedir, bool strict, ref int links) {
            string absolute = RubyUtils.ExpandPath(context.Platform, path, basedir ?? context.Platform.CurrentDirectory, false);

            var components = new List<string>();
            foreach (var part in absolute.Split('/')) {
                if (part.Length == 0 || part == ".") {
                    continue;
                }
                if (part == "..") {
                    if (components.Count > 0) {
                        components.RemoveAt(components.Count - 1);
                    }
                    continue;
                }
                components.Add(part);
            }

            string resolved = "";
            for (int i = 0; i < components.Count; i++) {
                bool last = i == components.Count - 1;
                string candidate = resolved + "/" + components[i];

                while (true) {
                    Posix.StatData data;
                    int errno;
                    if (!Posix.TryStat(candidate, false, out data, out errno)) {
                        if (last && !strict && errno == Posix.ENOENT) {
                            break;
                        }
                        throw Posix.Error(errno, candidate);
                    }

                    if (data.FileType != Posix.S_IFLNK) {
                        if (!last && data.FileType != Posix.S_IFDIR) {
                            throw Posix.Error(Posix.ENOTDIR, candidate);
                        }
                        break;
                    }

                    if (++links > 32) {
                        throw Posix.Error(Posix.ELOOP, candidate);
                    }

                    string target = Posix.ReadLink(candidate, out errno);
                    if (target == null) {
                        throw Posix.Error(errno, candidate);
                    }

                    if (target.StartsWith("/", StringComparison.Ordinal)) {
                        candidate = ResolvePath(context, target, null, strict || !last, ref links);
                        break;
                    }
                    candidate = ResolvePath(context, target, resolved.Length == 0 ? "/" : resolved, strict || !last, ref links);
                    break;
                }

                resolved = candidate;
            }

            return resolved.Length == 0 ? "/" : resolved;
        }

        [RubyMethod("realpath", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static MutableString/*!*/ RealPath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [Optional]object basedir) {

            return RealPath(toPath, self, path, basedir, true);
        }

        [RubyMethod("realdirpath", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static MutableString/*!*/ RealDirPath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [Optional]object basedir) {

            return RealPath(toPath, self, path, basedir, false);
        }

        private static MutableString/*!*/ RealPath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            object basedir, bool strict) {

            MutableString pathStr = Protocols.CastToPath(toPath, path);
            string strPath = self.Context.DecodePath(pathStr);
            string strBase = (basedir == Missing.Value || basedir == null)
                ? null
                : self.Context.DecodePath(Protocols.CastToPath(toPath, basedir));

            if (!Posix.IsAvailable) {
                MutableString expanded = ExpandPath(toPath, self, path, basedir == Missing.Value ? null : basedir);

                // No realpath(3) here, so links are not resolved; but what has to exist does, as in
                // MRI: the whole path for realpath, all but the last component for realdirpath.
                // pry asks Pathname#realpath whether a ./.pryrc is there and expects ENOENT if not.
                string full = self.Context.DecodePath(expanded).TrimEnd('/', '\\');
                string mustExist = strict ? full : System.IO.Path.GetDirectoryName(full);
                if (!String.IsNullOrEmpty(mustExist) && !System.IO.File.Exists(mustExist) && !System.IO.Directory.Exists(mustExist)) {
                    throw RubyExceptions.CreateENOENT("{0}", strPath);
                }
                return expanded;
            }

            string resolved = ResolvePath(self.Context, strPath, strBase, strict);

            // MRI's realdirpath converts a non-ASCII result into the argument's encoding, and where
            // that cannot be done leaves it in the file system encoding; realpath, which uses
            // realpath(3), relabels it either way.
            if (!strict && pathStr.Encoding != RubyEncoding.Binary && !IsAsciiString(resolved)) {
                return (CanEncode(resolved, pathStr.Encoding)
                    ? MutableString.Create(resolved, pathStr.Encoding)
                    : self.Context.EncodePath(resolved)).TaintBy(pathStr);
            }
            return EncodePathLike(self.Context, resolved, pathStr);
        }

        private static bool CanEncode(string/*!*/ str, RubyEncoding/*!*/ encoding) {
            try {
                encoding.StrictEncoding.GetByteCount(str);
                return true;
            } catch (EncoderFallbackException) {
                return false;
            }
        }

        private static bool IsAsciiString(string/*!*/ str) {
            foreach (char c in str) {
                if (c > 0x7F) {
                    return false;
                }
            }
            return true;
        }

        #endregion
#endif
        #endregion

        #region atime, ctime, mtime, utime
#if FEATURE_FILESYSTEM

        [RubyMethod("atime", BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime AccessTime(RubyContext/*!*/ context, RubyFile/*!*/ self) {
            return RubyStatOps.AccessTime(RubyStatOps.Create(self));
        }

        [RubyMethod("atime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime AccessTime(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.AccessTime(RubyStatOps.Create(self.Context, Protocols.CastToPath(toPath, path)));
        }

        [RubyMethod("ctime", BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime CreateTime(RubyContext/*!*/ context, RubyFile/*!*/ self) {
            return RubyStatOps.CreateTime(RubyStatOps.Create(self));
        }

        [RubyMethod("ctime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime CreateTime(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.CreateTime(RubyStatOps.Create(self.Context, Protocols.CastToPath(toPath, path)));
        }

        [RubyMethod("mtime", BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime ModifiedTime(RubyContext/*!*/ context, RubyFile/*!*/ self) {
            return RubyStatOps.ModifiedTime(RubyStatOps.Create(self));
        }

        [RubyMethod("mtime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime ModifiedTime(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.ModifiedTime(RubyStatOps.Create(self.Context, Protocols.CastToPath(toPath, path)));
        }

        [RubyMethod("utime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int UpdateTimes(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object accessTime, object modifiedTime,
            params object[]/*!*/ paths) {

            return UpdateTimes(toPath, self, accessTime, modifiedTime, paths, true);
        }

        [RubyMethod("lutime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int UpdateLinkTimes(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object accessTime, object modifiedTime,
            params object[]/*!*/ paths) {

            return UpdateTimes(toPath, self, accessTime, modifiedTime, paths, false);
        }

        private static int UpdateTimes(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object accessTime, object modifiedTime,
            object[]/*!*/ paths, bool followLinks) {

            var context = self.Context;
            RubyTime atime = MakeTime(context, accessTime);
            RubyTime mtime = MakeTime(context, modifiedTime);

            foreach (var path in paths) {
                string strPath = context.DecodePath(Protocols.CastToPath(toPath, path));

                if (Posix.IsAvailable) {
                    long[] times = new long[4];
                    SplitTime(atime, out times[0], out times[1]);
                    SplitTime(mtime, out times[2], out times[3]);

                    int errno;
                    if (Posix.UTimes(strPath, times, followLinks, out errno) != 0) {
                        throw Posix.Error(errno, strPath);
                    }
                    continue;
                }

                FileInfo info = new FileInfo(strPath);
                if (!info.Exists) {
                    throw RubyExceptions.CreateENOENT("No such file or directory - {0}", strPath);
                }
                info.LastAccessTimeUtc = atime.ToUniversalTime();
                info.LastWriteTimeUtc = mtime.ToUniversalTime();
            }

            return paths.Length;
        }

        private static void SplitTime(RubyTime/*!*/ time, out long seconds, out long nanoseconds) {
            // whole seconds and nanoseconds rather than ticks, which overflow long long before
            // the times a file system can store
            seconds = time.Seconds;
            nanoseconds = time.Nanoseconds;
        }
#endif
        private static RubyTime MakeTime(RubyContext/*!*/ context, object obj) {
            if (obj == null) {
                return new RubyTime(DateTime.Now);
            } else if (obj is RubyTime) {
                return (RubyTime)obj;
            } else if (obj is int) {
                return new RubyTime(RubyTime.ToLocalTime(RubyTime.Epoch.AddSeconds((int)obj)));
            } else if (obj is double) {
                return new RubyTime(RubyTime.ToLocalTime(RubyTime.Epoch.AddSeconds((double)obj)));
            } else {
                string name = context.GetClassOf(obj).Name;
                throw RubyExceptions.CreateTypeConversionError(name, "time");
            }
        }
        
        #endregion

        #region ftype, stat, inspect, path, to_path

#if FEATURE_FILESYSTEM
        [RubyMethod("ftype", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static MutableString FileType(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            // ftype reports on the link itself, not on its target
            return RubyStatOps.FileType(RubyStatOps.Create(self.Context, self.Context.DecodePath(Protocols.CastToPath(toPath, path)), false));
        }

        [RubyMethod("stat", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static FileSystemInfo/*!*/ Stat(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            MutableString strPath = Protocols.CastToPath(toPath, path);
            string decoded = self.Context.DecodePath(strPath);
            try {
                return RubyStatOps.Create(self.Context, decoded);
            } catch (Exception e) {
                // MRI's message carries the path's own bytes, so a binary path that is not valid
                // in any encoding still shows up in it (and the message stays binary).
                string message = e.Message;
                if (strPath.Encoding == RubyEncoding.Binary && !strPath.IsAscii() && message.EndsWith(decoded, StringComparison.Ordinal)) {
                    var binary = MutableString.CreateBinary(System.Text.Encoding.ASCII.GetBytes(message.Substring(0, message.Length - decoded.Length)));
                    RubyExceptionData.GetInstance(e).Message = binary.Append(strPath);
                }
                throw;
            }
        }

        [RubyMethod("lstat", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static FileSystemInfo/*!*/ LStat(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.Create(self.Context, self.Context.DecodePath(Protocols.CastToPath(toPath, path)), false);
        }

        [RubyMethod("lstat", BuildConfig = "FEATURE_FILESYSTEM")]
        [RubyMethod("stat", BuildConfig = "FEATURE_FILESYSTEM")]
        public static FileSystemInfo Stat(RubyFile/*!*/ self) {
            return RubyStatOps.Create(self);
        }

        [RubyMethod("birthtime", BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime BirthTime(RubyContext/*!*/ context, RubyFile/*!*/ self) {
            return RubyStatOps.BirthTime(RubyStatOps.Create(self));
        }

        [RubyMethod("birthtime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime BirthTime(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.BirthTime(RubyStatOps.Create(self.Context, Protocols.CastToPath(toPath, path)));
        }

        [RubyMethod("size", BuildConfig = "FEATURE_FILESYSTEM")]
        public static object FileSize(RubyFile/*!*/ self) {
            // fstat the descriptor rather than the path: the size of a file that has
            // since been unlinked is still readable, and a closed File raises IOError.
            self.RequireOpen();
            return RubyStatOps.Size(RubyStatOps.Create((RubyIO)self));
        }
#endif
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyFile/*!*/ self) {
            var result = MutableString.CreateMutable(self.Context.GetPathEncoding()).
                Append("#<").
                Append(self.Context.GetClassOf(self).GetName(self.Context)).
                Append(':');
            if (self.Path != null) {
                result.Append(self.Path).Append(self.Closed ? " (closed)" : "");
            } else if (self.Closed) {
                // a File made from a descriptor has no path to show, only the descriptor
                result.Append("(closed)");
            } else {
                result.Append("fd ").Append(self.GetFileDescriptor().ToString(CultureInfo.InvariantCulture));
            }
            return result.Append('>');
        }

        [RubyMethod("path")]
        [RubyMethod("to_path")]
        public static MutableString GetPath(RubyFile/*!*/ self) {
            self.RequireInitialized();
            if (self.Path == null) {
                return null;
            }
            // File#path hands back the path in the encoding it was opened with.
            MutableString result = self.Context.EncodePath(self.Path);
            if (self.PathEncoding != null) {
                result = MutableString.CreateMutable(result.ConvertToString(), self.PathEncoding);
            }
            return result;
        }

        #endregion

        #region File::Stat
#if FEATURE_FILESYSTEM

        /// <summary>
        /// A File::Stat backed by a real statx(2) result. Off Unix (or if libc
        /// is unavailable) File::Stat keeps using bare FileInfo/DirectoryInfo
        /// and the legacy approximations below.
        /// </summary>
        internal sealed class StatInfo : FileSystemInfo {
            internal readonly Posix.StatData Data;

            // Off Unix there is no statx result to hold, so the FileInfo/DirectoryInfo the
            // old implementation used rides along instead. File::Stat is this class either
            // way, which is what makes File.stat(...).instance_of?(File::Stat) true.
            internal readonly FileSystemInfo Legacy;

            internal StatInfo(string/*!*/ path, Posix.StatData/*!*/ data) {
                Data = data;
                OriginalPath = path;
                FullPath = path;
            }

            internal StatInfo(FileSystemInfo/*!*/ legacy) {
                Legacy = legacy;
                OriginalPath = legacy.FullName;
                FullPath = legacy.FullName;
            }

            public override bool Exists {
                get { return Legacy == null || Legacy.Exists; }
            }

            public override string/*!*/ Name {
                get { return System.IO.Path.GetFileName(FullPath); }
            }

            public override void Delete() {
                if (Legacy != null) {
                    Legacy.Delete();
                } else {
                    File.Delete(FullPath);
                }
            }
        }

        /// <summary>
        /// Stat
        /// </summary>
        [RubyClass("Stat", Extends = typeof(StatInfo), Inherits = typeof(object), BuildConfig = "FEATURE_FILESYSTEM"), Includes(typeof(Comparable))]
        public class RubyStatOps {

            /// <summary>
            /// fstat(2) on any IO, including one that never had a path (a pipe, a socket,
            /// or a file opened from a descriptor).
            /// </summary>
            internal static FileSystemInfo/*!*/ Create(RubyIO/*!*/ io) {
                io.RequireOpen();

                // Buffered writes have to reach the descriptor before it is stat'd, and
                // fstat needs the real OS handle, not IronRuby's descriptor table index.
                try {
                    io.Flush();
                } catch (NotSupportedException) {
                    // A read-only console stream cannot be flushed and needs no flushing.
                }
                // Off POSIX (Windows) there is no fstat to call - Posix.TryFStat answers false
                // with no errno, which used to surface as EBADF from every File#stat, #lstat and
                // #size on an open file, and so from FileUtils.cp.  Answer from the path the file
                // was opened with instead, as File.stat does there; a file renamed or deleted
                // since it was opened cannot be stat'd this way, which is Windows' own limit too.
                if (!Posix.IsAvailable) {
                    var file = io as RubyFile;
                    if (file != null && file.Path != null) {
                        return Create(io.Context, file.Path);
                    }
                }

                int fd = RubyIO.PrimaryDescriptorOf(io.GetStream().BaseStream);
                if (fd < 0) {
                    // The console streams are not FileStreams, but their descriptors are
                    // the well-known 0/1/2.
                    switch (io.ConsoleStreamType) {
                        case Microsoft.Scripting.Utils.ConsoleStreamType.Input: fd = 0; break;
                        case Microsoft.Scripting.Utils.ConsoleStreamType.Output: fd = 1; break;
                        case Microsoft.Scripting.Utils.ConsoleStreamType.ErrorOutput: fd = 2; break;
                        default: throw RubyExceptions.CreateEBADF();
                    }
                }

                Posix.StatData data;
                int errno;
                if (Posix.TryFStat(fd, out data, out errno)) {
                    return new StatInfo("", data);
                }
                throw Posix.Error(errno == 0 ? Posix.EBADF : errno, "");
            }

            internal static FileSystemInfo/*!*/ Create(RubyFile/*!*/ file) {
                file.RequireInitialized();
                file.RequireOpen();

                // fstat the descriptor rather than the path: the file may have been renamed or
                // unlinked since it was opened, and MRI still answers for what it holds open.
                // The IO overload is the one that knows how to find the real descriptor behind
                // IronRuby's own table.
                return Create((RubyIO)file);
            }

            internal static FileSystemInfo/*!*/ Create(RubyContext/*!*/ context, MutableString/*!*/ path) {
                return Create(context, context.DecodePath(path));
            }

            internal static FileSystemInfo/*!*/ Create(RubyContext/*!*/ context, string/*!*/ path) {
                return Create(context, path, true);
            }

            internal static FileSystemInfo/*!*/ Create(RubyContext/*!*/ context, string/*!*/ path, bool followLinks) {
                FileSystemInfo fsi;
                int errno;
                if (TryCreate(context, path, followLinks, out fsi, out errno)) {
                    return fsi;
                }
                throw Posix.Error(errno == 0 ? Posix.ENOENT : errno, path);
            }

            internal static bool TryCreate(RubyContext/*!*/ context, string/*!*/ path, out FileSystemInfo result) {
                int errno;
                return TryCreate(context, path, true, out result, out errno);
            }

            internal static bool TryCreate(RubyContext/*!*/ context, string/*!*/ path, bool followLinks, out FileSystemInfo result, out int errno) {
                result = null;
                errno = 0;

                if (Posix.IsAvailable) {
                    Posix.StatData data;
                    if (Posix.TryStat(path, followLinks, out data, out errno)) {
                        result = new StatInfo(path, data);
                        return true;
                    }
                    if (errno == 0) {
                        errno = Posix.ENOENT;
                    }
                    return false;
                }

                PlatformAdaptationLayer pal = context.Platform;
                if (pal.FileExists(path)) {
                    result = new StatInfo(new FileInfo(path));
                } else if (pal.DirectoryExists(path)) {
                    result = new StatInfo(new DirectoryInfo(path));
                } else if (path.ToUpperInvariant().Equals(NUL_VALUE)) {
                    result = new StatInfo(new DeviceInfo(NUL_VALUE));
                } else {
                    errno = Posix.ENOENT;
                    return false;
                }
                return true;
            }

            private static Posix.StatData D(FileSystemInfo/*!*/ self) {
                var si = self as StatInfo;
                return si != null ? si.Data : null;
            }

            /// <summary>The System.IO object behind an off-Unix File::Stat, if there is one.</summary>
            private static FileSystemInfo L(FileSystemInfo self) {
                var si = self as StatInfo;
                return si != null ? si.Legacy : self;
            }

            [RubyConstructor]
            public static FileSystemInfo/*!*/ Create(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
                return Create(self.Context, Protocols.CastToPath(toPath, path));
            }

            [RubyMethod("<=>")]
            public static int Compare(FileSystemInfo/*!*/ self, [NotNull]FileSystemInfo/*!*/ other) {
                var a = D(self);
                var b = D(other);
                if (a != null && b != null) {
                    int c = a.MTimeSec.CompareTo(b.MTimeSec);
                    return c != 0 ? c : a.MTimeNsec.CompareTo(b.MTimeNsec);
                }
                return self.LastWriteTime.CompareTo(other.LastWriteTime);
            }

            [RubyMethod("<=>")]
            public static object Compare(FileSystemInfo/*!*/ self, object other) {
                Debug.Assert(other as FileSystemInfo == null);
                return null;
            }

            internal static RubyTime/*!*/ MakeTime(long seconds, long nanoseconds) {
                // exact, as Time.at is: a file time can lie beyond DateTime's year 9999
                var exact = ExactNum.FromInteger(seconds) +
                    ExactNum.FromInteger(nanoseconds) / ExactNum.FromInteger(RubyTime.NanosecondsPerSecond);
                return RubyTime.FromExactSeconds(exact, RubyTimeZoneKind.Local, ExactNum.Zero);
            }

            [RubyMethod("atime")]
            public static RubyTime/*!*/ AccessTime(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? MakeTime(d.ATimeSec, d.ATimeNsec) : new RubyTime(self.LastAccessTime);
            }

            [RubyMethod("mtime")]
            public static RubyTime/*!*/ ModifiedTime(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? MakeTime(d.MTimeSec, d.MTimeNsec) : new RubyTime(self.LastWriteTime);
            }

            [RubyMethod("ctime")]
            public static RubyTime/*!*/ CreateTime(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? MakeTime(d.CTimeSec, d.CTimeNsec) : new RubyTime(self.CreationTime);
            }

            [RubyMethod("birthtime")]
            public static RubyTime/*!*/ BirthTime(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d == null) {
                    return new RubyTime(self.CreationTime);
                }
                if (!d.HasBirthTime) {
                    // MRI says "birthtime() function is unimplemented" when the OS has no
                    // statx at all, and "birthtime is unimplemented" when statx answered but
                    // the filesystem does not track it. We only reach here in the latter case.
                    throw new IronRuby.Builtins.NotImplementedError("birthtime is unimplemented on this filesystem");
                }
                return MakeTime(d.BTimeSec, d.BTimeNsec);
            }

            [RubyMethod("blksize")]
            public static object BlockSize(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)d.BlockSize : null;
            }

            [RubyMethod("blocks")]
            public static object Blocks(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)Protocols.Normalize(d.Blocks) : null;
            }

            [RubyMethod("blockdev?")]
            public static bool IsBlockDevice(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && d.FileType == Posix.S_IFBLK;
            }

            [RubyMethod("chardev?")]
            public static bool IsCharDevice(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && d.FileType == Posix.S_IFCHR;
            }

            [RubyMethod("dev")]
            public static object DeviceId(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? Protocols.Normalize(d.Dev) : (object)3;
            }

            [RubyMethod("rdev")]
            public static object RDeviceId(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? Protocols.Normalize(d.Rdev) : (object)3;
            }

            [RubyMethod("dev_major")]
            public static object DeviceIdMajor(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)d.DevMajor : null;
            }

            [RubyMethod("dev_minor")]
            public static object DeviceIdMinor(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)d.DevMinor : null;
            }

            [RubyMethod("rdev_major")]
            public static object RDeviceIdMajor(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)d.RdevMajor : null;
            }

            [RubyMethod("rdev_minor")]
            public static object RDeviceIdMinor(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)d.RdevMinor : null;
            }

            [RubyMethod("directory?")]
            public static bool IsDirectory(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.FileType == Posix.S_IFDIR : (L(self) is DirectoryInfo);
            }

            [RubyMethod("file?")]
            public static bool IsFile(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.FileType == Posix.S_IFREG : (L(self) is FileInfo);
            }

            [RubyMethod("pipe?")]
            public static bool IsPipe(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && d.FileType == Posix.S_IFIFO;
            }

            [RubyMethod("socket?")]
            public static bool IsSocket(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && d.FileType == Posix.S_IFSOCK;
            }

            [RubyMethod("symlink?")]
            public static bool IsSymLink(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && d.FileType == Posix.S_IFLNK;
            }

            [RubyMethod("ftype")]
            public static MutableString/*!*/ FileType(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d == null) {
                    return MutableString.CreateAscii(IsFile(self) ? "file" : "directory");
                }
                switch (d.FileType) {
                    case Posix.S_IFREG: return MutableString.CreateAscii("file");
                    case Posix.S_IFDIR: return MutableString.CreateAscii("directory");
                    case Posix.S_IFCHR: return MutableString.CreateAscii("characterSpecial");
                    case Posix.S_IFBLK: return MutableString.CreateAscii("blockSpecial");
                    case Posix.S_IFIFO: return MutableString.CreateAscii("fifo");
                    case Posix.S_IFLNK: return MutableString.CreateAscii("link");
                    case Posix.S_IFSOCK: return MutableString.CreateAscii("socket");
                    default: return MutableString.CreateAscii("unknown");
                }
            }

            #region permission predicates

            private static bool ModeAccess(Posix.StatData/*!*/ d, int bit, bool real) {
                int uid = real ? Posix.GetUid() : Posix.GetEUid();
                if (uid == 0) {
                    // root bypasses read/write; execute still needs some x bit
                    return bit != Posix.X_OK || (d.Mode & 0x49 /* 0111 */) != 0;
                }

                int shift;
                if (d.Uid == uid) {
                    shift = 6;
                } else if (d.Gid == (real ? Posix.GetGid() : Posix.GetEGid()) || Array.IndexOf(Posix.GetGroups(), d.Gid) >= 0) {
                    shift = 3;
                } else {
                    shift = 0;
                }
                return (d.Mode & (bit << shift)) != 0;
            }

            [RubyMethod("executable?")]
            public static bool IsExecutable(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d != null) {
                    return ModeAccess(d, Posix.X_OK, false);
                }
                if (System.IO.Path.DirectorySeparatorChar == '/') {
                    var mode = self.UnixFileMode;
                    return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
                }
                return IsWindowsExecutableExtension(self.Extension);
            }

            /// <summary>
            /// Windows has no execute bit: what makes a file runnable is its extension being
            /// in %PATHEXT%, and that is what MRI's rb_file_executable_p looks at there.
            /// Accepting only ".exe" said no to every .bat and .cmd - including the ir.cmd
            /// that mspec is pointed at as RUBY_EXE, which it then silently replaced with a
            /// command line reconstructed from RbConfig.
            /// </summary>
            private static bool IsWindowsExecutableExtension(string extension) {
                if (String.IsNullOrEmpty(extension)) {
                    return false;
                }
                string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
                foreach (string candidate in pathExt.Split(';')) {
                    if (candidate.Length != 0 && extension.Equals(candidate, StringComparison.OrdinalIgnoreCase)) {
                        return true;
                    }
                }
                return false;
            }

            [RubyMethod("executable_real?")]
            public static bool IsExecutableReal(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? ModeAccess(d, Posix.X_OK, true) : IsExecutable(self);
            }

            [RubyMethod("readable?")]
            public static bool IsReadable(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? ModeAccess(d, Posix.R_OK, false) : true;
            }

            [RubyMethod("readable_real?")]
            public static bool IsReadableReal(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? ModeAccess(d, Posix.R_OK, true) : true;
            }

            [RubyMethod("writable?")]
            public static bool IsWritable(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? ModeAccess(d, Posix.W_OK, false) : ((self.Attributes & FileAttributes.ReadOnly) == 0);
            }

            [RubyMethod("writable_real?")]
            public static bool IsWritableReal(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? ModeAccess(d, Posix.W_OK, true) : IsWritable(self);
            }

            [RubyMethod("world_readable?")]
            public static object IsWorldReadable(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d == null || (d.Mode & 04) == 0) {
                    return null;
                }
                return d.Mode & 0xFFF /* 07777 */;
            }

            [RubyMethod("world_writable?")]
            public static object IsWorldWritable(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d == null || (d.Mode & 02) == 0) {
                    return null;
                }
                return d.Mode & 0xFFF /* 07777 */;
            }

            [RubyMethod("owned?")]
            public static bool IsUserOwned(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.Uid == Posix.GetEUid() : true;
            }

            /// <summary>Owned by the real uid rather than the effective one - what `test ?O` asks.</summary>
            public static bool IsUserOwnedReal(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.Uid == Posix.GetUid() : true;
            }

            [RubyMethod("grpowned?")]
            public static bool IsGroupOwned(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d == null) {
                    return false;
                }
                return d.Gid == Posix.GetEGid() || Array.IndexOf(Posix.GetGroups(), d.Gid) >= 0;
            }

            [RubyMethod("setgid?")]
            public static bool IsSetGid(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && (d.Mode & Posix.S_ISGID) != 0;
            }

            [RubyMethod("setuid?")]
            public static bool IsSetUid(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && (d.Mode & Posix.S_ISUID) != 0;
            }

            [RubyMethod("sticky?")]
            public static object IsSticky(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)((d.Mode & Posix.S_ISVTX) != 0) : null;
            }

            #endregion

            [RubyMethod("identical?")]
            public static bool AreIdentical(RubyContext/*!*/ context, FileSystemInfo/*!*/ self, [NotNull]FileSystemInfo/*!*/ other) {
                var a = D(self);
                var b = D(other);
                if (a != null && b != null) {
                    return a.Dev == b.Dev && a.Ino == b.Ino;
                }
                return self.Exists && other.Exists && context.Platform.PathComparer.Compare(self.FullName, other.FullName) == 0;
            }

            [RubyMethod("gid")]
            public static int GroupId(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.Gid : 0;
            }

            [RubyMethod("uid")]
            public static int UserId(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.Uid : 0;
            }

            [RubyMethod("ino")]
            public static object Inode(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? Protocols.Normalize(d.Ino) : (object)0;
            }

            [RubyMethod("nlink")]
            public static int NumberOfLinks(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.Nlink : 1;
            }

            [RubyMethod("mode")]
            public static int Mode(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d != null) {
                    return d.Mode;
                }
                int mode = (L(self) is FileInfo) ? 0x8000 : 0x4000;
                mode |= 0x100; // S_IREAD;
                if ((L(self).Attributes & FileAttributes.ReadOnly) == 0) {
                    mode |= 0x80; // S_IWRITE;
                }
                return mode;
            }

            [RubyMethod("size")]
            public static object Size(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d != null) {
                    return Protocols.Normalize(d.Size);
                }
                if (L(self) is DeviceInfo) {
                    return 0;
                }
                FileInfo info = (L(self) as FileInfo);
                return (info == null) ? 0 : (object)Protocols.Normalize(info.Length);
            }

            [RubyMethod("size?")]
            public static object NullableSize(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d != null) {
                    return d.Size == 0 ? null : Protocols.Normalize(d.Size);
                }
                if (L(self) is DeviceInfo) {
                    return 0;
                }
                FileInfo info = (L(self) as FileInfo);
                if (info == null) {
                    return null;
                }
                return (info.Length == 0) ? null : (object)(int)info.Length;
            }

            [RubyMethod("zero?")]
            public static bool IsZeroLength(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d != null) {
                    return d.FileType != Posix.S_IFDIR && d.Size == 0;
                }
                if (L(self) is DeviceInfo) {
                    return true;
                }
                FileInfo info = (L(self) as FileInfo);
                return (info == null) ? false : info.Length == 0;
            }

            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, FileSystemInfo/*!*/ self) {
               return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture,
                    "#<File::Stat dev={0}, ino={1}, mode={2}, nlink={3}, uid={4}, gid={5}, rdev={6}, size={7}, blksize={8}, blocks={9}, atime={10}, mtime={11}, ctime={12}>",
                    FormatDev(DeviceId(self)),
                    context.Inspect(Inode(self)),
                    FormatMode(Mode(self)),
                    context.Inspect(NumberOfLinks(self)),
                    context.Inspect(UserId(self)),
                    context.Inspect(GroupId(self)),
                    FormatDev(RDeviceId(self)),
                    context.Inspect(Size(self)),
                    context.Inspect(BlockSize(self)),
                    context.Inspect(Blocks(self)),
                    context.Inspect(AccessTime(self)),
                    context.Inspect(ModifiedTime(self)),
                    context.Inspect(CreateTime(self))
                ));
            }

            private static string FormatDev(object dev) {
                return dev is int ? "0x" + ((int)dev).ToString("x") : dev.ToString();
            }

            private static string FormatMode(int mode) {
                return "0" + Convert.ToString(mode, 8);
            }

            internal class DeviceInfo : FileSystemInfo {

                private string/*!*/ _name;

                internal DeviceInfo(string/*!*/ name) {
                    _name = name;
                }

                public override void Delete() {
                    throw new NotImplementedException();
                }

                public override bool Exists {
                    get { return true; }
                }

                public override string Name {
                    get { return _name; }
                }
            }
        }
#endif
        #endregion

        #region FileTest methods (public singletons only)
#if FEATURE_FILESYSTEM
        // TODO: conversion: to_io, to_path, to_str

        [RubyMethod("blockdev?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsBlockDevice(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsBlockDevice(toPath, self, path);
        }

        [RubyMethod("chardev?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsCharDevice(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsCharDevice(toPath, self, path);
        }

        [RubyMethod("directory?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsDirectory(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsDirectory(respondToStorage, toIoStorage, toPath, self, path);
        }

        [RubyMethod("executable?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsExecutable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsExecutable(toPath, self, path);
        }

        [RubyMethod("executable_real?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsExecutableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsExecutableReal(toPath, self, path);
        }

        [RubyMethod("exist?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        // #exists? was deprecated in 2.1 and removed in 3.9.
        public static bool Exists(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.Exists(toPath, self, path);
        }

        [RubyMethod("file?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsFile(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsFile(toPath, self, path);
        }

        [RubyMethod("grpowned?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsGroupOwned(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsGroupOwned(toPath, self, path);
        }

        [RubyMethod("identical?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool AreIdentical(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path1, object path2) {
            return FileTest.AreIdentical(toPath, self, path1, path2);
        }

        [RubyMethod("owned?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsUserOwned(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsUserOwned(toPath, self, path);
        }

        [RubyMethod("pipe?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsPipe(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsPipe(toPath, self, path);
        }

        [RubyMethod("readable?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsReadable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsReadable(toPath, self, path);
        }

        [RubyMethod("readable_real?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsReadableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsReadableReal(toPath, self, path);
        }

        [RubyMethod("setgid?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsSetGid(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsSetGid(toPath, self, path);
        }

        [RubyMethod("setuid?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsSetUid(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsSetUid(toPath, self, path);
        }

        [RubyMethod("size", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object Size(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.Size(toPath, self, path);
        }

        [RubyMethod("size?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object NullableSize(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.NullableSize(toPath, self, path);
        }

        [RubyMethod("socket?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsSocket(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsSocket(toPath, self, path);
        }

        [RubyMethod("sticky?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object IsSticky(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsSticky(toPath, self, path);
        }

        [RubyMethod("symlink?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsSymLink(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsSymLink(toPath, self, path);
        }

        [RubyMethod("writable?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsWritable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsWritable(toPath, self, path);
        }

        [RubyMethod("writable_real?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsWritableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsWritableReal(toPath, self, path);
        }

        [RubyMethod("world_readable?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object IsWorldReadable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsWorldReadable(toPath, self, path);
        }

        [RubyMethod("world_writable?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object IsWorldWritable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsWorldWritable(toPath, self, path);
        }

        [RubyMethod("empty?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        [RubyMethod("zero?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsZeroLength(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsZeroLength(toPath, self, path);
        }
#endif
        #endregion
    }
}

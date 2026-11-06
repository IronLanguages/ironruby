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
using System.IO;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    // The predicates MRI answers with rb_stat - the file-type and ownership tests, the size
    // queries, identical? - take a stream as well as a path: an IO, or anything whose #to_io gives
    // one, is fstat'd, so a pipe or a deleted-but-open file answers for what it holds open. The
    // permission tests (readable?, writable?, executable? and their _real forms) and symlink? go
    // through access(2) / lstat(2) on a path in MRI, and take only a path (#to_path or #to_str).

    [RubyModule("FileTest")]
    public static class FileTest {
        /// <summary>
        /// Every one of these predicates answers false (or nil) for a path that
        /// does not exist -- none of them raises. Ruby only raises from the
        /// non-predicate queries (File.size, File.ftype, ...).
        /// </summary>
        private static bool Query(RubyContext/*!*/ context, MutableString/*!*/ path, bool followLinks, Func<FileSystemInfo, bool>/*!*/ predicate) {
            FileSystemInfo fsi;
            int errno;
            if (!RubyFileOps.RubyStatOps.TryCreate(context, context.DecodePath(path), followLinks, out fsi, out errno)) {
                return false;
            }
            return predicate(fsi);
        }

        private static object NullableQuery(RubyContext/*!*/ context, MutableString/*!*/ path, Func<FileSystemInfo, object>/*!*/ query) {
            FileSystemInfo fsi;
            int errno;
            if (!RubyFileOps.RubyStatOps.TryCreate(context, context.DecodePath(path), true, out fsi, out errno)) {
                return null;
            }
            return query(fsi);
        }

        // Query and NullableQuery for the rb_stat predicates: a stream is fstat'd - a closed one is
        // an IOError, as in MRI - and only what is not one is asked for its path.
        private static bool Query(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path, Func<FileSystemInfo, bool>/*!*/ predicate) {

            var io = ToIO(respondToStorage, toIoStorage, self.Context, path);
            if (io != null) {
                return predicate(RubyFileOps.RubyStatOps.Create(io));
            }
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, predicate);
        }

        private static object NullableQuery(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path, Func<FileSystemInfo, object>/*!*/ query) {

            var io = ToIO(respondToStorage, toIoStorage, self.Context, path);
            if (io != null) {
                return query(RubyFileOps.RubyStatOps.Create(io));
            }
            return NullableQuery(self.Context, Protocols.CastToPath(toPath, path), query);
        }

        /// <summary>
        /// MRI's rb_stat: fstat(2) of a stream, stat(2) of anything else's path. Raises when the
        /// path does not exist (File.size, File.mtime, ...).
        /// </summary>
        internal static FileSystemInfo/*!*/ Stat(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyContext/*!*/ context, object path) {

            var io = ToIO(respondToStorage, toIoStorage, context, path);
            if (io != null) {
                return RubyFileOps.RubyStatOps.Create(io);
            }
            return RubyFileOps.RubyStatOps.Create(context, Protocols.CastToPath(toPath, path));
        }

        [RubyMethod("blockdev?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("blockdev?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsBlockDevice(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsBlockDevice);
        }

        [RubyMethod("chardev?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("chardev?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsCharDevice(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsCharDevice);
        }

        /// <summary>
        /// #directory? takes a stream as well as a path: an IO, or anything that answers
        /// #to_io. Only what is neither is asked for its path.
        /// </summary>
        [RubyMethod("directory?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("directory?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsDirectory(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {

            return Query(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsDirectory);
        }

        /// <summary>
        /// MRI's rb_check_convert_type(obj, T_FILE, "IO", :to_io): the IO itself, what #to_io gives,
        /// or null when there is no #to_io or it answers nil - the argument is then a path. A #to_io
        /// that gives anything else is a TypeError.
        /// </summary>
        internal static RubyIO ToIO(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, RubyContext/*!*/ context, object obj) {

            var io = obj as RubyIO;
            if (io != null) {
                return io;
            }
            if (obj is MutableString || !Protocols.RespondTo(respondToStorage, obj, "to_io")) {
                return null;
            }
            var site = toIoStorage.GetCallSite("to_io", 0);
            object converted = site.Target(site, obj);
            if (converted == null) {
                return null;
            }
            io = converted as RubyIO;
            if (io == null) {
                throw RubyExceptions.CreateTypeError("can't convert {0} to IO ({0}#to_io gives {1})",
                    context.GetClassDisplayName(obj), context.GetClassDisplayName(converted));
            }
            return io;
        }

        [RubyMethod("executable?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("executable?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsExecutable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsExecutable);
        }

        [RubyMethod("executable_real?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("executable_real?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsExecutableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsExecutableReal);
        }

        [RubyMethod("exist?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("exist?", RubyMethodAttributes.PrivateInstance)]
        // #exists? was deprecated in 2.1 and removed in 3.9.
        public static bool Exists(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path, (fsi) => true);
        }

        [RubyMethod("file?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("file?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsFile(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsFile);
        }

        [RubyMethod("grpowned?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("grpowned?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsGroupOwned(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsGroupOwned);
        }

        [RubyMethod("identical?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("identical?", RubyMethodAttributes.PrivateInstance)]
        public static bool AreIdentical(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path1, object path2) {
            FileSystemInfo info1, info2;

            return TryStat(respondToStorage, toIoStorage, toPath, self.Context, path1, out info1)
                && TryStat(respondToStorage, toIoStorage, toPath, self.Context, path2, out info2)
                && RubyFileOps.RubyStatOps.AreIdentical(self.Context, info1, info2);
        }

        private static bool TryStat(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyContext/*!*/ context, object path, out FileSystemInfo info) {

            var io = ToIO(respondToStorage, toIoStorage, context, path);
            if (io != null) {
                info = RubyFileOps.RubyStatOps.Create(io);
                return true;
            }
            int errno;
            return RubyFileOps.RubyStatOps.TryCreate(context, context.DecodePath(Protocols.CastToPath(toPath, path)), true, out info, out errno);
        }

        [RubyMethod("owned?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("owned?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsUserOwned(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsUserOwned);
        }

        [RubyMethod("pipe?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("pipe?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsPipe(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsPipe);
        }

        [RubyMethod("readable?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("readable?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsReadable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsReadable);
        }

        [RubyMethod("readable_real?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("readable_real?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsReadableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsReadableReal);
        }

        [RubyMethod("setgid?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("setgid?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsSetGid(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsSetGid);
        }

        [RubyMethod("setuid?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("setuid?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsSetUid(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsSetUid);
        }

        [RubyMethod("size", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("size", RubyMethodAttributes.PrivateInstance)]
        public static object Size(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return RubyFileOps.RubyStatOps.Size(Stat(respondToStorage, toIoStorage, toPath, self.Context, path));
        }

        [RubyMethod("size?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("size?", RubyMethodAttributes.PrivateInstance)]
        public static object NullableSize(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return NullableQuery(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.NullableSize);
        }

        [RubyMethod("socket?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("socket?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsSocket(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsSocket);
        }

        [RubyMethod("sticky?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("sticky?", RubyMethodAttributes.PrivateInstance)]
        public static object IsSticky(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(respondToStorage, toIoStorage, toPath, self, path,
                (fsi) => RubyFileOps.RubyStatOps.IsSticky(fsi) as bool? ?? false);
        }

        [RubyMethod("symlink?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("symlink?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsSymLink(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), false, RubyFileOps.RubyStatOps.IsSymLink);
        }

        [RubyMethod("writable?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("writable?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsWritable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsWritable);
        }

        [RubyMethod("writable_real?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("writable_real?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsWritableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsWritableReal);
        }

        [RubyMethod("world_readable?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("world_readable?", RubyMethodAttributes.PrivateInstance)]
        public static object IsWorldReadable(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return NullableQuery(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsWorldReadable);
        }

        [RubyMethod("world_writable?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("world_writable?", RubyMethodAttributes.PrivateInstance)]
        public static object IsWorldWritable(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return NullableQuery(respondToStorage, toIoStorage, toPath, self, path, RubyFileOps.RubyStatOps.IsWorldWritable);
        }

        [RubyMethod("empty?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("empty?", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("zero?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("zero?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsZeroLength(RespondToStorage/*!*/ respondToStorage, CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            var io = ToIO(respondToStorage, toIoStorage, self.Context, path);
            if (io != null) {
                return RubyFileOps.RubyStatOps.IsZeroLength(RubyFileOps.RubyStatOps.Create(io));
            }

            var p = Protocols.CastToPath(toPath, path);
            string strPath = self.Context.DecodePath(p);

            // NUL/nul is a special-cased filename on Windows
            if (!Posix.IsAvailable && strPath.ToUpperInvariant() == "NUL") {
                return RubyFileOps.RubyStatOps.IsZeroLength(RubyFileOps.RubyStatOps.Create(self.Context, strPath));
            }

            return Query(self.Context, p, true, RubyFileOps.RubyStatOps.IsZeroLength);
        }
    }
}

/* ****************************************************************************
 *
 * POSIX file-system primitives that .NET does not expose.
 *
 * The original IronRuby File/File::Stat implementation was written against
 * Win32 and faked everything it could not get out of System.IO (uid, gid, ino,
 * nlink, the file type, symbolic links, ...). On Unix all of that is one
 * statx(2) away, so this file provides a thin binding and the File classes are
 * built on top of it. Everything here degrades to "unsupported" off Unix, in
 * which case the callers fall back to the old System.IO-only behaviour.
 *
 * statx() is used rather than stat() because struct statx has a fixed,
 * architecture-independent layout, whereas struct stat does not.
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace IronRuby.Builtins {

    internal static class Posix {
        internal static readonly bool IsAvailable =
            System.IO.Path.DirectorySeparatorChar == '/' &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>
        /// flock(2) is the same call with the same operation bits on every Unix, so it is
        /// offered on macOS too, where the rest of this class (statx, Linux errno numbers)
        /// is not.  RubyGems takes a lock on every binstub it writes, so without this
        /// `gem install` of anything with an executable failed on a Mac.
        /// </summary>
        internal static readonly bool IsFlockAvailable = System.IO.Path.DirectorySeparatorChar == '/';

        /// <summary>
        /// errno for "would block" on this platform: EAGAIN is 11 on Linux and 35 on macOS and
        /// the other BSDs, whose errno numbering macOS inherited.  IsFlockAvailable lets every
        /// Unix in, so a BSD has to get 35 too, or a contended LOCK_NB raises instead of
        /// returning false and a blocking lock never retries.
        /// </summary>
        internal static readonly int EWouldBlock = IsBsdErrno() ? 35 : EWOULDBLOCK;

        private static bool IsBsdErrno() {
            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.Create("NETBSD")) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.Create("OPENBSD"));
        }

        #region errno numbers

        internal const int EPERM = 1;
        internal const int ENOENT = 2;
        internal const int ENXIO = 6;
        internal const int EBADF = 9;
        internal const int EWOULDBLOCK = 11;  // == EAGAIN on Linux
        internal const int EACCES = 13;
        internal const int EEXIST = 17;
        internal const int EXDEV = 18;
        internal const int ENOTDIR = 20;
        internal const int EISDIR = 21;
        internal const int EINVAL = 22;
        internal const int ENOTEMPTY = 39;
        internal const int ELOOP = 40;
        internal const int ENAMETOOLONG = 36;
        internal const int EOPNOTSUPP = 95;

        #endregion

        #region flock(2) operations

        internal const int LOCK_SH = 1;
        internal const int LOCK_EX = 2;
        internal const int LOCK_NB = 4;
        internal const int LOCK_UN = 8;

        #endregion

        #region file type bits (S_IFMT)

        internal const int S_IFMT = 0xF000;
        internal const int S_IFSOCK = 0xC000;
        internal const int S_IFLNK = 0xA000;
        internal const int S_IFREG = 0x8000;
        internal const int S_IFBLK = 0x6000;
        internal const int S_IFDIR = 0x4000;
        internal const int S_IFCHR = 0x2000;
        internal const int S_IFIFO = 0x1000;
        internal const int S_ISUID = 0x800;
        internal const int S_ISGID = 0x400;
        internal const int S_ISVTX = 0x200;

        #endregion

        #region statx

        private const int AT_FDCWD = -100;
        private const int AT_SYMLINK_NOFOLLOW = 0x100;
        private const int AT_EMPTY_PATH = 0x1000;
        private const uint STATX_ALL = 0xFFF; // STATX_BASIC_STATS | STATX_BTIME

        /// <summary>
        /// struct statx, as defined by the kernel. The layout is stable across
        /// architectures (verified against /usr/include/.../stat.h).
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct StatxBuffer {
            [FieldOffset(0)] public uint Mask;
            [FieldOffset(4)] public uint BlockSize;
            [FieldOffset(16)] public uint Nlink;
            [FieldOffset(20)] public uint Uid;
            [FieldOffset(24)] public uint Gid;
            [FieldOffset(28)] public ushort Mode;
            [FieldOffset(32)] public ulong Ino;
            [FieldOffset(40)] public ulong Size;
            [FieldOffset(48)] public ulong Blocks;
            [FieldOffset(64)] public long ATimeSec;
            [FieldOffset(72)] public uint ATimeNsec;
            [FieldOffset(80)] public long BTimeSec;
            [FieldOffset(88)] public uint BTimeNsec;
            [FieldOffset(96)] public long CTimeSec;
            [FieldOffset(104)] public uint CTimeNsec;
            [FieldOffset(112)] public long MTimeSec;
            [FieldOffset(120)] public uint MTimeNsec;
            [FieldOffset(128)] public uint RdevMajor;
            [FieldOffset(132)] public uint RdevMinor;
            [FieldOffset(136)] public uint DevMajor;
            [FieldOffset(140)] public uint DevMinor;
        }

        private const uint STATX_BTIME = 0x800;

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        private static extern int sys_statx(int dirfd, byte[] pathname, int flags, uint mask, out StatxBuffer buf);

        /// <summary>
        /// The parts of struct statx that Ruby's File::Stat exposes, decoded.
        /// </summary>
        internal sealed class StatData {
            public long Dev;
            public long Ino;
            public int Mode;
            public int Nlink;
            public int Uid;
            public int Gid;
            public long Rdev;
            public long Size;
            public int BlockSize;
            public long Blocks;
            public long ATimeSec, ATimeNsec;
            public long MTimeSec, MTimeNsec;
            public long CTimeSec, CTimeNsec;
            public long BTimeSec, BTimeNsec;
            public bool HasBirthTime;
            public int DevMajor, DevMinor, RdevMajor, RdevMinor;

            public int FileType { get { return Mode & S_IFMT; } }
        }

        /// <summary>
        /// The kernel's dev_t encoding (see glibc's makedev): 12 bits of major
        /// and 20 bits of minor, interleaved.
        /// </summary>
        private static long MakeDev(uint major, uint minor) {
            return (long)(((ulong)(minor & 0xFFu)) |
                          (((ulong)(major & 0xFFFu)) << 8) |
                          (((ulong)(minor & ~0xFFu)) << 12) |
                          (((ulong)(major & ~0xFFFu)) << 32));
        }

        internal static bool TryStat(string path, bool followLinks, out StatData result, out int errno) {
            result = null;
            errno = 0;
            if (!IsAvailable) {
                return false;
            }

            StatxBuffer buf;
            int flags = followLinks ? 0 : AT_SYMLINK_NOFOLLOW;
            int rc;
            try {
                rc = sys_statx(AT_FDCWD, ToPath(path), flags, STATX_ALL, out buf);
            } catch (EntryPointNotFoundException) {
                return false;
            } catch (DllNotFoundException) {
                return false;
            }

            if (rc != 0) {
                errno = Marshal.GetLastWin32Error();
                return false;
            }

            result = Decode(ref buf);
            return true;
        }

        internal static bool TryFStat(int fd, out StatData result, out int errno) {
            result = null;
            errno = 0;
            if (!IsAvailable) {
                return false;
            }

            StatxBuffer buf;
            int rc;
            try {
                rc = sys_statx(fd, ToPath(""), AT_EMPTY_PATH, STATX_ALL, out buf);
            } catch (EntryPointNotFoundException) {
                return false;
            } catch (DllNotFoundException) {
                return false;
            }

            if (rc != 0) {
                errno = Marshal.GetLastWin32Error();
                return false;
            }

            result = Decode(ref buf);
            return true;
        }

        private static StatData Decode(ref StatxBuffer buf) {
            return new StatData {
                Dev = MakeDev(buf.DevMajor, buf.DevMinor),
                DevMajor = (int)buf.DevMajor,
                DevMinor = (int)buf.DevMinor,
                Rdev = MakeDev(buf.RdevMajor, buf.RdevMinor),
                RdevMajor = (int)buf.RdevMajor,
                RdevMinor = (int)buf.RdevMinor,
                Ino = (long)buf.Ino,
                Mode = buf.Mode,
                Nlink = (int)buf.Nlink,
                Uid = (int)buf.Uid,
                Gid = (int)buf.Gid,
                Size = (long)buf.Size,
                BlockSize = (int)buf.BlockSize,
                Blocks = (long)buf.Blocks,
                ATimeSec = buf.ATimeSec, ATimeNsec = buf.ATimeNsec,
                MTimeSec = buf.MTimeSec, MTimeNsec = buf.MTimeNsec,
                CTimeSec = buf.CTimeSec, CTimeNsec = buf.CTimeNsec,
                BTimeSec = buf.BTimeSec, BTimeNsec = buf.BTimeNsec,
                HasBirthTime = (buf.Mask & STATX_BTIME) != 0,
            };
        }

        #endregion

        #region links, fifos, ownership, times

        [DllImport("libc", EntryPoint = "symlink", SetLastError = true)]
        private static extern int sys_symlink(byte[] target, byte[] linkpath);

        [DllImport("libc", EntryPoint = "unlink", SetLastError = true)]
        private static extern int sys_unlink(byte[] pathname);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int sys_link(byte[] oldpath, byte[] newpath);

        [DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
        private static extern IntPtr sys_readlink(byte[] path, byte[] buf, IntPtr bufsiz);

        [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
        private static extern int sys_mkfifo(byte[] path, int mode);

        [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
        private static extern int sys_mkdir(byte[] path, int mode);

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int sys_chmod(byte[] path, int mode);

        [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
        private static extern int sys_fchmod(int fd, int mode);

        [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
        private static extern int sys_chown(byte[] path, int owner, int group);

        [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
        private static extern int sys_lchown(byte[] path, int owner, int group);

        [DllImport("libc", EntryPoint = "fchown", SetLastError = true)]
        private static extern int sys_fchown(int fd, int owner, int group);

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int sys_open(byte[] path, int flags, int mode);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int sys_close(int fd);

        [DllImport("libcrypt.so.1", EntryPoint = "crypt", SetLastError = true)]
        private static extern IntPtr sys_crypt(byte[] key, byte[] salt);

        /// <summary>
        /// crypt(3). Both arguments are C strings, so they are passed with the terminator the
        /// caller is expected to have left room for. Null when the library is not there or the
        /// salt is one it does not recognise - crypt sets errno and answers NULL for that.
        /// </summary>
        internal static string Crypt(byte[]/*!*/ key, byte[]/*!*/ salt) {
            try {
                IntPtr result = sys_crypt(key, salt);
                return (result == IntPtr.Zero) ? null : Marshal.PtrToStringAnsi(result);
            } catch (DllNotFoundException) {
                return null;
            } catch (EntryPointNotFoundException) {
                return null;
            }
        }

        [DllImport("libc", EntryPoint = "fchdir", SetLastError = true)]
        private static extern int sys_fchdir(int fd);

        [DllImport("libc", EntryPoint = "chroot", SetLastError = true)]
        private static extern int sys_chroot(byte[] path);

        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        private static extern int sys_flock(int fd, int operation);

        [DllImport("libc", EntryPoint = "utimensat", SetLastError = true)]
        private static extern int sys_utimensat(int dirfd, byte[] path, long[] times, int flags);

        [DllImport("libc", EntryPoint = "getuid")] private static extern int sys_getuid();
        [DllImport("libc", EntryPoint = "geteuid")] private static extern int sys_geteuid();
        [DllImport("libc", EntryPoint = "getgid")] private static extern int sys_getgid();
        [DllImport("libc", EntryPoint = "getegid")] private static extern int sys_getegid();
        [DllImport("libc", EntryPoint = "getgroups", SetLastError = true)] private static extern int sys_getgroups(int size, int[] list);

        [DllImport("libc", EntryPoint = "access", SetLastError = true)]
        private static extern int sys_access(byte[] path, int mode);

        [DllImport("libc", EntryPoint = "euidaccess", SetLastError = true)]
        private static extern int sys_euidaccess(byte[] path, int mode);

        internal static byte[] ToPath(string path) {
            var bytes = Encoding.UTF8.GetBytes(path ?? "");
            var result = new byte[bytes.Length + 1];
            Array.Copy(bytes, result, bytes.Length);
            return result;
        }

        internal static int Symlink(string target, string linkPath, out int errno) {
            return Run(() => sys_symlink(ToPath(target), ToPath(linkPath)), out errno);
        }

        /// <summary>
        /// unlink(2). System.IO.File.Delete cannot do this job: it refuses a symbolic link that
        /// points at a directory, because it sees a directory, whereas unlink(2) removes the link
        /// itself. File.unlink on such a link reported ENOENT instead of removing it.
        /// </summary>
        internal static int Unlink(string path, out int errno) {
            return Run(() => sys_unlink(ToPath(path)), out errno);
        }

        internal static int Link(string oldPath, string newPath, out int errno) {
            return Run(() => sys_link(ToPath(oldPath), ToPath(newPath)), out errno);
        }

        internal static int MkFifo(string path, int mode, out int errno) {
            return Run(() => sys_mkfifo(ToPath(path), mode), out errno);
        }

        internal static int MkDir(string path, int mode, out int errno) {
            return Run(() => sys_mkdir(ToPath(path), mode), out errno);
        }

        internal static int Chmod(string path, int mode, out int errno) {
            return Run(() => sys_chmod(ToPath(path), mode), out errno);
        }

        internal static int FChmod(int fd, int mode, out int errno) {
            return Run(() => sys_fchmod(fd, mode), out errno);
        }

        internal static int Chown(string path, int owner, int group, out int errno) {
            return Run(() => sys_chown(ToPath(path), owner, group), out errno);
        }

        internal static int LChown(string path, int owner, int group, out int errno) {
            return Run(() => sys_lchown(ToPath(path), owner, group), out errno);
        }

        internal static int FChown(int fd, int owner, int group, out int errno) {
            return Run(() => sys_fchown(fd, owner, group), out errno);
        }

        #region open(2) flags

        internal const int O_RDONLY = 0;
        internal const int O_DIRECTORY = 0x10000;
        internal const int O_CLOEXEC = 0x80000;

        #endregion

        /// <summary>
        /// open(2). Unlike the calls above a successful return is positive rather than
        /// zero, so it cannot go through Run.
        /// </summary>
        internal static int Open(string path, int flags, out int errno) {
            errno = 0;
            Marshal.SetLastSystemError(0);
            int fd = sys_open(ToPath(path), flags, 0);
            if (fd < 0) {
                errno = Marshal.GetLastWin32Error();
            }
            return fd;
        }

        internal static int Close(int fd, out int errno) {
            return Run(() => sys_close(fd), out errno);
        }

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int sys_ioctl(int fd, UIntPtr request, byte[] arg);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int sys_ioctl(int fd, UIntPtr request, IntPtr arg);

        /// <summary>ioctl(2) with a buffer, or with a plain integer when buffer is null.</summary>
        internal static int Ioctl(int fd, long request, byte[] buffer, long arg, out int errno) {
            errno = 0;
            Marshal.SetLastSystemError(0);
            var req = new UIntPtr(unchecked((ulong)request));
            int rc = (buffer != null) ? sys_ioctl(fd, req, buffer) : sys_ioctl(fd, req, new IntPtr(arg));
            if (rc < 0) {
                errno = Marshal.GetLastWin32Error();
            }
            return rc;
        }

        internal static int FChDir(int fd, out int errno) {
            return Run(() => sys_fchdir(fd), out errno);
        }

        internal static int ChRoot(string path, out int errno) {
            return Run(() => sys_chroot(ToPath(path)), out errno);
        }

        internal static int Flock(int fd, int operation, out int errno) {
            return Run(() => sys_flock(fd, operation), out errno);
        }

        internal const long UTIME_OMIT = (1L << 30) - 2;

        /// <summary>times is {atime_sec, atime_nsec, mtime_sec, mtime_nsec}.</summary>
        internal static int UTimes(string path, long[] times, bool followLinks, out int errno) {
            return Run(() => sys_utimensat(AT_FDCWD, ToPath(path), times, followLinks ? 0 : AT_SYMLINK_NOFOLLOW), out errno);
        }

        // struct passwd on glibc/musl x86-64: two pointers, uid_t, gid_t, then three
        // more pointers.  Only pw_dir is needed, so the rest stay opaque.
        [StructLayout(LayoutKind.Sequential)]
        private struct PasswdEntry {
            internal IntPtr Name;
            internal IntPtr Passwd;
            internal int Uid;
            internal int Gid;
            internal IntPtr Gecos;
            internal IntPtr Dir;
            internal IntPtr Shell;
        }

        [DllImport("libc", EntryPoint = "getpwnam", SetLastError = true)]
        private static extern IntPtr sys_getpwnam(byte[] name);

        [DllImport("libc", EntryPoint = "getpwuid", SetLastError = true)]
        private static extern IntPtr sys_getpwuid(int uid);

        private static string GetHomeDirectory(IntPtr passwd) {
            if (passwd == IntPtr.Zero) {
                return null;
            }
            var entry = Marshal.PtrToStructure<PasswdEntry>(passwd);
            return entry.Dir == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(entry.Dir);
        }

        /// <summary>
        /// The home directory of a named user, or null when there is no such user.
        /// </summary>
        internal static string GetHomeDirectory(string/*!*/ userName) {
            if (!IsAvailable) {
                return null;
            }
            try {
                return GetHomeDirectory(sys_getpwnam(ToPath(userName)));
            } catch (DllNotFoundException) {
                return null;
            } catch (EntryPointNotFoundException) {
                return null;
            }
        }

        /// <summary>
        /// The home directory recorded for a uid, or null when there is no such user.
        /// </summary>
        internal static string GetHomeDirectory(int uid) {
            if (!IsAvailable) {
                return null;
            }
            try {
                return GetHomeDirectory(sys_getpwuid(uid));
            } catch (DllNotFoundException) {
                return null;
            } catch (EntryPointNotFoundException) {
                return null;
            }
        }

        internal static int GetUid() { return IsAvailable ? sys_getuid() : 0; }
        internal static int GetEUid() { return IsAvailable ? sys_geteuid() : 0; }
        internal static int GetGid() { return IsAvailable ? sys_getgid() : 0; }
        internal static int GetEGid() { return IsAvailable ? sys_getegid() : 0; }

        internal static int[] GetGroups() {
            if (!IsAvailable) {
                return new int[0];
            }
            try {
                int n = sys_getgroups(0, null);
                if (n <= 0) {
                    return new int[0];
                }
                var list = new int[n];
                n = sys_getgroups(n, list);
                if (n < 0) {
                    return new int[0];
                }
                if (n < list.Length) {
                    Array.Resize(ref list, n);
                }
                return list;
            } catch (EntryPointNotFoundException) {
                return new int[0];
            }
        }

        internal const int R_OK = 4;
        internal const int W_OK = 2;
        internal const int X_OK = 1;
        internal const int F_OK = 0;

        /// <summary>access(2) -- uses the real uid/gid, like Ruby's *_real? predicates.</summary>
        internal static bool AccessReal(string path, int mode) {
            if (!IsAvailable) {
                return false;
            }
            int errno;
            return Run(() => sys_access(ToPath(path), mode), out errno) == 0;
        }

        /// <summary>euidaccess(3) -- uses the effective uid/gid, like Ruby's readable?/writable?/executable?.</summary>
        internal static bool AccessEffective(string path, int mode) {
            if (!IsAvailable) {
                return false;
            }
            int errno;
            try {
                return Run(() => sys_euidaccess(ToPath(path), mode), out errno) == 0;
            } catch (EntryPointNotFoundException) {
                return AccessReal(path, mode);
            }
        }

        internal static string ReadLink(string path, out int errno) {
            errno = 0;
            if (!IsAvailable) {
                errno = EINVAL;
                return null;
            }

            int size = 256;
            while (size <= 64 * 1024) {
                var buf = new byte[size];
                IntPtr rc;
                try {
                    Marshal.SetLastSystemError(0);
                    rc = sys_readlink(ToPath(path), buf, (IntPtr)size);
                } catch (EntryPointNotFoundException) {
                    errno = EINVAL;
                    return null;
                }
                long n = rc.ToInt64();
                if (n < 0) {
                    errno = Marshal.GetLastWin32Error();
                    return null;
                }
                if (n < size) {
                    return Encoding.UTF8.GetString(buf, 0, (int)n);
                }
                size *= 2;
            }
            errno = ENAMETOOLONG;
            return null;
        }

        private static int Run(Func<int> call, out int errno) {
            errno = 0;
            Marshal.SetLastSystemError(0);
            int rc = call();
            if (rc != 0) {
                errno = Marshal.GetLastWin32Error();
            }
            return rc;
        }

        #endregion

        #region errors

        internal static string ErrorMessage(int errno) {
            switch (errno) {
                case EPERM: return "Operation not permitted";
                case ENOENT: return "No such file or directory";
                case EBADF: return "Bad file descriptor";
                case EACCES: return "Permission denied";
                case EEXIST: return "File exists";
                case EXDEV: return "Invalid cross-device link";
                case ENOTDIR: return "Not a directory";
                case EISDIR: return "Is a directory";
                case EINVAL: return "Invalid argument";
                case ENAMETOOLONG: return "File name too long";
                case ENOTEMPTY: return "Directory not empty";
                case ELOOP: return "Too many levels of symbolic links";
                case EOPNOTSUPP: return "Operation not supported";
                default: return "Unknown error " + errno;
            }
        }

        /// <summary>
        /// Turns an errno into the matching Ruby exception. Only the errnos
        /// that have a CLR-backed Errno class get their own type; the rest come
        /// back as a plain SystemCallError, which is still a correct (if less
        /// specific) answer.
        /// </summary>
        internal static Exception Error(int errno, string path) {
            string suffix = path != null ? " - " + path : "";
            switch (errno) {
                case ENOENT: return IronRuby.Runtime.RubyExceptions.CreateENOENT("{0}", path ?? "");
                case EEXIST: return IronRuby.Runtime.RubyExceptions.CreateEEXIST("{0}", path ?? "");
                case EINVAL: return IronRuby.Runtime.RubyExceptions.CreateEINVAL("{0}", path ?? "");
                case EACCES: return new UnauthorizedAccessException("Permission denied" + suffix);
                case ENOTDIR: return new System.IO.DirectoryNotFoundException("Not a directory" + suffix);
                case EISDIR: return IronRuby.Runtime.RubyExceptions.CreateEISDIR(path ?? "");
                case ENXIO: return IronRuby.Runtime.RubyExceptions.CreateENXIO(path ?? "");
                case EBADF: return IronRuby.Runtime.RubyExceptions.CreateEBADF();
                case EXDEV: return new Errno.ImproperLinkError(path);
                case EPERM: return new Errno.OperationNotPermittedError(path);
                case ELOOP: return new Errno.TooManySymbolicLinksError(path);
                case ENOTEMPTY: return new Errno.DirectoryNotEmptyError(path);
                case ENAMETOOLONG: return new Errno.NameTooLongError(path);
                default:
                    return IronRuby.Runtime.RubyExceptions.CreateSystemCallError("{0}", ErrorMessage(errno) + suffix);
            }
        }

        #endregion
    }
}

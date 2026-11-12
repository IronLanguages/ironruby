/* ****************************************************************************
 *
 * Thin wrappers over the POSIX process calls .NET does not expose.
 *
 * Each one hands back an Integer rather than throwing: a negative result is
 * -errno.  The prelude turns that into the right Errno:: class, which is where
 * it has to happen anyway - most of the Errno family is defined in Ruby there
 * and has no CLR type for C# code to throw.
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    public static partial class RubyProcess {

        [StructLayout(LayoutKind.Sequential)]
        private struct RLimit {
            public ulong Current;
            public ulong Maximum;
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "getrlimit")]
        private static extern int SysGetRLimit(int resource, out RLimit limit);

        [DllImport("libc", SetLastError = true, EntryPoint = "setrlimit")]
        private static extern int SysSetRLimit(int resource, ref RLimit limit);

        [DllImport("libc", SetLastError = true, EntryPoint = "getpgid")]
        private static extern int SysGetPgid(int pid);

        [DllImport("libc", SetLastError = true, EntryPoint = "setpgid")]
        private static extern int SysSetPgid(int pid, int pgid);

        [DllImport("libc", SetLastError = true, EntryPoint = "getsid")]
        private static extern int SysGetSid(int pid);

        [DllImport("libc", SetLastError = true, EntryPoint = "setsid")]
        private static extern int SysSetSid();

        [DllImport("libc", SetLastError = true, EntryPoint = "getpriority")]
        private static extern int SysGetPriority(int which, int who);

        [DllImport("libc", SetLastError = true, EntryPoint = "setpriority")]
        private static extern int SysSetPriority(int which, int who, int priority);

        [DllImport("libc", SetLastError = true, EntryPoint = "geteuid")]
        private static extern int SysGetEuid();

        [DllImport("libc", SetLastError = true, EntryPoint = "getegid")]
        private static extern int SysGetEgid();

        [DllImport("libc", SetLastError = true, EntryPoint = "issetugid")]
        private static extern int SysIsSetUgid();

        [DllImport("libc", SetLastError = true, EntryPoint = "setresuid")]
        private static extern int SysSetResUid(int ruid, int euid, int suid);

        [DllImport("libc", SetLastError = true, EntryPoint = "setresgid")]
        private static extern int SysSetResGid(int rgid, int egid, int sgid);

        [DllImport("libc", SetLastError = true, EntryPoint = "seteuid")]
        private static extern int SysSetEuid(int uid);


        [DllImport("libc", SetLastError = true, EntryPoint = "setegid")]
        private static extern int SysSetEgid(int gid);

        [DllImport("libc", SetLastError = true, EntryPoint = "setgroups")]
        private static extern int SysSetGroups(IntPtr count, int[] groups);

        [DllImport("libc", SetLastError = true, EntryPoint = "initgroups")]
        private static extern int SysInitGroups(string user, int group);

        private static int Failure() {
            int error = Marshal.GetLastWin32Error();
            return -(error == 0 ? 1 : error);
        }

        /// <summary>[soft, hard] for the resource, or a negative errno.</summary>
        [RubyMethod("__getrlimit__", RubyMethodAttributes.PublicSingleton)]
        public static object GetRLimit(RubyModule/*!*/ self, [DefaultProtocol]int resource) {
            RLimit limit;
            if (SysGetRLimit(resource, out limit) != 0) {
                return ScriptingRuntimeHelpers.Int32ToObject(Failure());
            }
            return new RubyArray { Clamp(limit.Current), Clamp(limit.Maximum) };
        }

        // RLIM_INFINITY does not fit a Fixnum; hand it over as the unsigned value so the
        // prelude can compare it against Process::RLIM_INFINITY.
        private static object Clamp(ulong value) {
            return (value <= Int32.MaxValue)
                ? ScriptingRuntimeHelpers.Int32ToObject((int)value)
                : (object)new System.Numerics.BigInteger(value);
        }

        [RubyMethod("__setrlimit__", RubyMethodAttributes.PublicSingleton)]
        public static int SetRLimit(RubyModule/*!*/ self, [DefaultProtocol]int resource,
            object soft, object hard) {

            var limit = new RLimit { Current = ToRLimitValue(soft), Maximum = ToRLimitValue(hard) };
            return (SysSetRLimit(resource, ref limit) != 0) ? Failure() : 0;
        }

        // rlim_t is unsigned, so RLIM_INFINITY reaches Ruby as the Bignum 2**64-1 (see Clamp)
        // and has to be able to travel straight back into setrlimit. Taking the limits as long
        // made `Process.setrlimit(r, *Process.getrlimit(r))` throw OverflowException.
        // Negative values wrap into the unsigned range, as they do in MRI.
        private static ulong ToRLimitValue(object value) {
            if (value is int) {
                return unchecked((ulong)(long)(int)value);
            }

            if (value is System.Numerics.BigInteger) {
                var big = (System.Numerics.BigInteger)value;
                if (big.Sign < 0) {
                    if (big < Int64.MinValue) {
                        throw RubyExceptions.CreateRangeError("bignum too big to convert into 'unsigned long'");
                    }
                    return unchecked((ulong)(long)big);
                }

                if (big > UInt64.MaxValue) {
                    throw RubyExceptions.CreateRangeError("bignum too big to convert into 'unsigned long'");
                }
                return (ulong)big;
            }

            throw RubyExceptions.CreateTypeError("no implicit conversion into Integer");
        }

        [RubyMethod("__getpgid__", RubyMethodAttributes.PublicSingleton)]
        public static int GetPgid(RubyModule/*!*/ self, [DefaultProtocol]int pid) {
            int result = SysGetPgid(pid);
            return (result < 0) ? Failure() : result;
        }

        [RubyMethod("__setpgid__", RubyMethodAttributes.PublicSingleton)]
        public static int SetPgid(RubyModule/*!*/ self, [DefaultProtocol]int pid, [DefaultProtocol]int pgid) {
            return (SysSetPgid(pid, pgid) != 0) ? Failure() : 0;
        }

        [RubyMethod("__getsid__", RubyMethodAttributes.PublicSingleton)]
        public static int GetSid(RubyModule/*!*/ self, [DefaultProtocol]int pid) {
            int result = SysGetSid(pid);
            return (result < 0) ? Failure() : result;
        }

        [RubyMethod("__setsid__", RubyMethodAttributes.PublicSingleton)]
        public static int SetSid(RubyModule/*!*/ self) {
            int result = SysSetSid();
            return (result < 0) ? Failure() : result;
        }

        [RubyMethod("__getpriority__", RubyMethodAttributes.PublicSingleton)]
        public static int GetPriority(RubyModule/*!*/ self, [DefaultProtocol]int which, [DefaultProtocol]int who) {
            // getpriority legitimately returns -1, so errno has to be cleared first. A priority
            // can be anything from -20 to 19, so a failure is reported as -1000 - errno, which
            // the prelude tells apart from a priority.
            Marshal.SetLastSystemError(0);
            int result = SysGetPriority(which, who);
            if (result == -1 && Marshal.GetLastWin32Error() != 0) {
                return Failure() - 1000;
            }
            return result;
        }

        [RubyMethod("__setpriority__", RubyMethodAttributes.PublicSingleton)]
        public static int SetPriority(RubyModule/*!*/ self, [DefaultProtocol]int which,
            [DefaultProtocol]int who, [DefaultProtocol]int priority) {
            return (SysSetPriority(which, who, priority) != 0) ? Failure() : 0;
        }

        [RubyMethod("__geteuid__", RubyMethodAttributes.PublicSingleton)]
        public static int GetEuid(RubyModule/*!*/ self) {
            return SysGetEuid();
        }

        [RubyMethod("__getegid__", RubyMethodAttributes.PublicSingleton)]
        public static int GetEgid(RubyModule/*!*/ self) {
            return SysGetEgid();
        }

        [RubyMethod("__setuid__", RubyMethodAttributes.PublicSingleton)]
        public static int SetUid(RubyModule/*!*/ self, [DefaultProtocol]int uid) {
            // MRI's Process.uid= changes the real id alone - setresuid(uid, -1, -1) - which is also
            // what a process whose effective id has already been given up is allowed to do.
            return (SysSetResUid(uid, -1, -1) != 0) ? Failure() : 0;
        }

        [RubyMethod("__seteuid__", RubyMethodAttributes.PublicSingleton)]
        public static int SetEuid(RubyModule/*!*/ self, [DefaultProtocol]int uid) {
            return (SysSetEuid(uid) != 0) ? Failure() : 0;
        }

        [RubyMethod("__setgid__", RubyMethodAttributes.PublicSingleton)]
        public static int SetGid(RubyModule/*!*/ self, [DefaultProtocol]int gid) {
            return (SysSetResGid(gid, -1, -1) != 0) ? Failure() : 0;
        }

        [RubyMethod("__setegid__", RubyMethodAttributes.PublicSingleton)]
        public static int SetEgid(RubyModule/*!*/ self, [DefaultProtocol]int gid) {
            return (SysSetEgid(gid) != 0) ? Failure() : 0;
        }

        [RubyMethod("__setgroups__", RubyMethodAttributes.PublicSingleton)]
        public static int SetGroups(RubyModule/*!*/ self, [NotNull]RubyArray/*!*/ groups) {
            int[] gids = new int[groups.Count];
            for (int i = 0; i < gids.Length; i++) {
                gids[i] = Convert.ToInt32(groups[i]);
            }
            return (SysSetGroups((IntPtr)gids.Length, gids) != 0) ? Failure() : 0;
        }

        [RubyMethod("__initgroups__", RubyMethodAttributes.PublicSingleton)]
        public static int InitGroups(RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ user,
            [DefaultProtocol]int group) {
            return (SysInitGroups(user.ConvertToString(), group) != 0) ? Failure() : 0;
        }

        [RubyMethod("__issetugid__", RubyMethodAttributes.PublicSingleton)]
        public static object IsSetUgid(RubyModule/*!*/ self) {
            try {
                return (SysIsSetUgid() != 0);
            } catch (EntryPointNotFoundException) {
                // Linux has no issetugid(2).
                return null;
            }
        }
    }
}

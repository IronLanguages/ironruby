/* ****************************************************************************
 *
 * fcntl(2) and open(2) for P/Invoke, on every Unix including Apple silicon.
 *
 * Both are variadic in C: int fcntl(int fd, int cmd, ...) and
 * int open(const char *path, int flags, ...). A DllImport cannot say so, and
 * declaring the optional argument as an ordinary int works wherever variadic
 * arguments travel the way named ones do - Linux on every architecture, and
 * macOS on x64. Apple's arm64 ABI is the exception: every variadic argument goes
 * on the stack, in an 8-byte slot, while an ordinary third argument goes in a
 * register. So fcntl(fd, F_SETFD, FD_CLOEXEC) declared the usual way hands libc
 * whatever happens to be on the stack as the flags, and open(path, O_CREAT, 0644)
 * creates the file with whatever permissions were there.
 *
 * Filling the eight argument registers with the two named arguments and six
 * padding ones puts the next argument where the callee's va_arg looks: the first
 * stack slot. Declared as a long, it fills the whole slot, as a variadic int
 * would after promotion. (.NET's own libSystem.Native avoids the question by
 * being written in C.)
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;

namespace IronRuby.Builtins {

    public static class LibcVariadic {

        /// <summary>Whether a variadic argument has to be passed on the stack (Apple arm64).</summary>
        public static readonly bool VariadicOnStack =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
        private static extern int sys_fcntl(int fd, int command, int argument);

        [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
        private static extern int sys_fcntl_stack(int fd, int command,
            IntPtr r2, IntPtr r3, IntPtr r4, IntPtr r5, IntPtr r6, IntPtr r7, long argument);

        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        private static extern int sys_open(IntPtr path, int flags, int mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        private static extern int sys_open_stack(IntPtr path, int flags,
            IntPtr r2, IntPtr r3, IntPtr r4, IntPtr r5, IntPtr r6, IntPtr r7, long mode);

        /// <summary>fcntl(2) with an int argument (F_GETFD, F_SETFD, F_GETFL, F_SETFL, F_DUPFD...).
        /// The commands that take no argument ignore it.</summary>
        public static int Fcntl(int fd, int command, int argument) {
            if (VariadicOnStack) {
                return sys_fcntl_stack(fd, command,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, argument);
            }
            return sys_fcntl(fd, command, argument);
        }

        /// <summary>open(2) of a NUL-terminated path; <paramref name="mode"/> is read only with O_CREAT (or O_TMPFILE).</summary>
        public static int Open(IntPtr path, int flags, int mode) {
            if (VariadicOnStack) {
                return sys_open_stack(path, flags,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, mode);
            }
            return sys_open(path, flags, mode);
        }
    }
}

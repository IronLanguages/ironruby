/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/
#if FEATURE_PROCESS

using System;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Pty {

    /// <summary>
    /// The syscalls behind the pty library: allocating a pseudo-terminal pair and starting a
    /// child on its slave side.  Everything Ruby-shaped - argument parsing, the block forms,
    /// PTY.check and PTY::ChildExited - is Ruby, in Src/StdLib/ironruby/pty.rb; the split is
    /// the one io/console and Termios already make.
    ///
    /// MRI's ext/pty forks, and the child calls setsid(2), makes the slave its controlling
    /// terminal with TIOCSCTTY, dup2s it onto 0, 1 and 2 and execs.  .NET cannot fork, so the
    /// child is started with posix_spawn(3) instead, through the same primitive Process.spawn
    /// uses, with POSIX_SPAWN_SETSID and a file action that opens the slave onto descriptor 0
    /// *without* O_NOCTTY.  glibc and musl run setsid before the file actions, and on Linux a
    /// session leader with no controlling terminal acquires the first terminal it opens that
    /// way - the System V rule TIOCSCTTY exists to spell out on BSD.  So the child's pid is
    /// the command's own pid, the command really has the pty as its controlling terminal
    /// (`tty`, /dev/tty, job control and ^C all work), and no helper program such as
    /// util-linux's `setsid --ctty` has to be found on PATH or sit between us and the child.
    ///
    /// Unix only: Windows has no pty, and MRI does not build the library there either.
    /// </summary>
    [RubyModule("PTY", BuildConfig = "FEATURE_PROCESS")]
    public static class PtyOps {

        [DllImport("libc", SetLastError = true, EntryPoint = "posix_openpt")]
        private static extern int sys_posix_openpt(int flags);

        [DllImport("libc", SetLastError = true, EntryPoint = "grantpt")]
        private static extern int sys_grantpt(int fd);

        [DllImport("libc", SetLastError = true, EntryPoint = "unlockpt")]
        private static extern int sys_unlockpt(int fd);

        [DllImport("libc", SetLastError = true, EntryPoint = "ptsname_r")]
        private static extern int sys_ptsname_r(int fd, byte[] buffer, IntPtr length);

        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        private static extern int sys_open(byte[] path, int flags, uint mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "close")]
        private static extern int sys_close(int fd);

        [DllImport("libc", SetLastError = true, EntryPoint = "chmod")]
        private static extern int sys_chmod(byte[] path, uint mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
        private static extern int sys_fcntl(int fd, int command, int argument);

        private static readonly bool IsMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        // Linux's values; macOS spells three of them differently.
        private const int O_RDWR = 2;
        private static readonly int O_NOCTTY = IsMac ? 0x20000 : 0x100;
        private static readonly int O_CLOEXEC = IsMac ? 0x1000000 : 0x80000;
        private static readonly int F_DUPFD_CLOEXEC = IsMac ? 67 : 1030;

        // The file-action opcodes the Process.spawn prelude shares with RubyProcess.
        private const int ActionDup2 = 0;
        private const int ActionOpen = 2;

        /// <summary>
        /// posix_openpt, grantpt, unlockpt, ptsname_r and an open of the slave - MRI's
        /// get_device_once for Unix98 ptys.  Both descriptors are close-on-exec.  With nomesg the
        /// slave is made 0600, as MRI's PTY.open does (mesg n: nobody else may write to it).
        /// </summary>
        private static void GetDevice(bool nomesg, out int master, out int slave, out string slaveName) {
            master = slave = -1;
            slaveName = null;
            try {
                master = sys_posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
                if (master < 0) {
                    throw Failure();
                }
                // glibc's grantpt is a check on Linux's devpts, so MRI's SIGCHLD dance around a
                // helper process it might fork is not needed.
                if (sys_grantpt(master) != 0 || sys_unlockpt(master) != 0) {
                    throw Failure();
                }
                byte[] name = new byte[128];
                if (sys_ptsname_r(master, name, (IntPtr)name.Length) != 0) {
                    throw Failure();
                }
                int length = Array.IndexOf(name, (byte)0);
                slaveName = System.Text.Encoding.UTF8.GetString(name, 0, length < 0 ? name.Length : length);
                byte[] path = CString(slaveName);
                if (nomesg && sys_chmod(path, Convert.ToUInt32("600", 8)) != 0) {
                    throw Failure();
                }
                slave = sys_open(path, O_RDWR | O_NOCTTY | O_CLOEXEC, 0);
                if (slave < 0) {
                    throw Failure();
                }
            } catch {
                if (slave >= 0) {
                    sys_close(slave);
                }
                if (master >= 0) {
                    sys_close(master);
                }
                throw;
            }
        }

        private static Exception/*!*/ Failure() {
            return RubyExceptions.CreateRuntimeError("can't get Master/Slave device");
        }

        private static byte[]/*!*/ CString(string/*!*/ value) {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            Array.Resize(ref bytes, bytes.Length + 1);
            return bytes;
        }

        /// <summary>
        /// An IO (or File) over a descriptor this library just opened.  It owns the descriptor,
        /// and its #fileno is the kernel's number for it, as Process.__os_pipe__'s ends are.
        /// </summary>
        private static RubyIO/*!*/ Wrap(RubyContext/*!*/ context, int descriptor, bool readable, bool writable,
            bool sync, bool file, string/*!*/ path) {

            var stream = new DescriptorStream(descriptor, readable, writable, true);
            int fd = RubyProcess.Adopt(context, descriptor, stream);
            IOMode mode = readable ? (writable ? IOMode.ReadWrite : IOMode.ReadOnly) : IOMode.WriteOnly;

            RubyIO io;
            if (file) {
                var f = new RubyFile(context, stream, fd, mode);
                f.Path = path;
                io = f;
            } else {
                io = new RubyIO(context, stream, fd, mode);
                context.SetInstanceVariable(io, "@__io_path__", MutableString.Create(path, RubyEncoding.UTF8).Freeze());
            }
            io.SetEncodings(null, null);
            io.AutoFlush = sync;
            return io;
        }

        /// <summary>
        /// PTY.open without its block: [master IO, slave File], both read-write, sync and
        /// close-on-exec.  The master's path is "masterpty:" plus the slave's, as in MRI.
        /// </summary>
        [RubyMethod("__open__", RubyMethodAttributes.PrivateSingleton)]
        public static RubyArray/*!*/ Open(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            int master, slave;
            string slaveName;
            GetDevice(true, out master, out slave, out slaveName);
            return new RubyArray {
                Wrap(context, master, true, true, true, false, "masterpty:" + slaveName),
                Wrap(context, slave, true, true, true, true, slaveName),
            };
        }

        /// <summary>
        /// Starts a child on a new pty: file, argv, envp, actions, pgroup and close_others are
        /// what Process.__spawn_setup__ produced for the command.  Returns [reader, writer, pid]
        /// - two Files over the master (the writer a dup of it), named after the slave as MRI
        /// names them - or -errno when the child could not be started.
        /// </summary>
        [RubyMethod("__spawn__", RubyMethodAttributes.PrivateSingleton)]
        public static object Spawn(RubyContext/*!*/ context, RubyModule/*!*/ self,
            [NotNull]MutableString/*!*/ file, [NotNull]RubyArray/*!*/ argv, RubyArray envp, RubyArray actions,
            object pgroup, bool closeOthers) {

            int master, slave;
            string slaveName;
            GetDevice(false, out master, out slave, out slaveName);

            int pid;
            try {
                // The pty goes in first, so that anything the caller redirected (err: "/dev/null",
                // say) is applied on top of it - MRI's child does the same, in the same order.
                var all = new RubyArray {
                    new RubyArray {
                        ScriptingRuntimeHelpers.Int32ToObject(ActionOpen),
                        ScriptingRuntimeHelpers.Int32ToObject(0),
                        MutableString.Create(slaveName, RubyEncoding.UTF8),
                        ScriptingRuntimeHelpers.Int32ToObject(O_RDWR),
                        ScriptingRuntimeHelpers.Int32ToObject(0)
                    },
                    new RubyArray {
                        ScriptingRuntimeHelpers.Int32ToObject(ActionDup2),
                        ScriptingRuntimeHelpers.Int32ToObject(1),
                        ScriptingRuntimeHelpers.Int32ToObject(0)
                    },
                    new RubyArray {
                        ScriptingRuntimeHelpers.Int32ToObject(ActionDup2),
                        ScriptingRuntimeHelpers.Int32ToObject(2),
                        ScriptingRuntimeHelpers.Int32ToObject(0)
                    },
                };
                if (actions != null) {
                    all.AddRange(actions);
                }
                pid = RubyProcess.Spawn(file, argv, envp, all, pgroup, closeOthers, true);
            } catch {
                // Nothing owns the master yet - Wrap has not run - so it would leak.
                sys_close(master);
                throw;
            } finally {
                // The child has its own descriptors on the slave by now (posix_spawn returns once
                // the exec has happened or failed); the parent's copy would only keep the master
                // from ever seeing the hang-up that tells a reader the child is gone.
                sys_close(slave);
            }

            if (pid < 0) {
                sys_close(master);
                return ScriptingRuntimeHelpers.Int32ToObject(pid);
            }

            int writer = sys_fcntl(master, F_DUPFD_CLOEXEC, 0);
            if (writer < 0) {
                int errno = Marshal.GetLastWin32Error();
                sys_close(master);
                return ScriptingRuntimeHelpers.Int32ToObject(-errno);
            }

            return new RubyArray {
                Wrap(context, master, true, false, false, true, slaveName),
                Wrap(context, writer, false, true, true, true, slaveName),
                ScriptingRuntimeHelpers.Int32ToObject(pid),
            };
        }
    }
}
#endif

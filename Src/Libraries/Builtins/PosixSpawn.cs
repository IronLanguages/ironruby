/* ****************************************************************************
 *
 * Starting and reaping children through POSIX rather than through
 * System.Diagnostics.Process.
 *
 * Process.Start hides everything Ruby needs to report about a child: it hands
 * back an ExitCode that has already folded "killed by signal N" into 128+N, it
 * cannot put the child in another process group, and it can only redirect the
 * three standard streams onto pipes of its own making.  posix_spawn(3) exposes
 * all of it - file actions cover arbitrary descriptor plumbing, the attributes
 * cover the process group, and waitpid(2) hands back the raw status word that
 * Process::Status is defined in terms of.
 *
 * Everything here returns an Integer rather than throwing: a negative result is
 * -errno, which the prelude turns into the right Errno:: class.
 *
 * ***************************************************************************/
#if FEATURE_PROCESS

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    public static partial class RubyProcess {

        /// <summary>
        /// Whether the primitives below have a libc to call at all. Off Unix each one hands
        /// over to WindowsSpawn, which implements the same contract - a pid or a negative
        /// errno - on CreateProcess and friends. Without this every one of them would come
        /// out as a DllNotFoundException for "libc" rather than as a Ruby error.
        /// </summary>
        internal static bool IsWindows {
            get { return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows); }
        }

        #region libc

        [DllImport("libc", EntryPoint = "posix_spawn")]
        private static extern int SysPosixSpawn(out int pid, IntPtr path, IntPtr fileActions, IntPtr attr, IntPtr argv, IntPtr envp);

        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
        private static extern int SysFileActionsInit(IntPtr actions);

        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
        private static extern int SysFileActionsDestroy(IntPtr actions);

        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
        private static extern int SysFileActionsAddDup2(IntPtr actions, int fd, int newFd);

        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addclose")]
        private static extern int SysFileActionsAddClose(IntPtr actions, int fd);

        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addopen")]
        private static extern int SysFileActionsAddOpen(IntPtr actions, int fd, IntPtr path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "posix_spawnattr_init")]
        private static extern int SysSpawnAttrInit(IntPtr attr);

        [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")]
        private static extern int SysSpawnAttrDestroy(IntPtr attr);

        [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")]
        private static extern int SysSpawnAttrSetFlags(IntPtr attr, short flags);

        [DllImport("libc", EntryPoint = "posix_spawnattr_setpgroup")]
        private static extern int SysSpawnAttrSetPGroup(IntPtr attr, int pgroup);

        [DllImport("libc", EntryPoint = "posix_spawnattr_setsigdefault")]
        private static extern int SysSpawnAttrSetSigDefault(IntPtr attr, IntPtr sigset);

        [DllImport("libc", EntryPoint = "sigemptyset")]
        private static extern int SysSigEmptySet(IntPtr sigset);

        [DllImport("libc", EntryPoint = "sigaddset")]
        private static extern int SysSigAddSet(IntPtr sigset, int signal);

        [DllImport("libc", SetLastError = true, EntryPoint = "waitpid")]
        private static extern int SysWaitPid(int pid, out int status, int options);

        [DllImport("libc", SetLastError = true, EntryPoint = "execve")]
        private static extern int SysExecve(IntPtr path, IntPtr argv, IntPtr envp);

        [DllImport("libc", SetLastError = true, EntryPoint = "access")]
        private static extern int SysAccess(IntPtr path, int mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "pipe2")]
        private static extern int SysPipe2(int[] fds, int flags);

        [DllImport("libc", SetLastError = true, EntryPoint = "read")]
        private static extern IntPtr SysRead(int fd, byte[] buffer, IntPtr count);

        [DllImport("libc", SetLastError = true, EntryPoint = "write")]
        private static extern IntPtr SysWrite(int fd, byte[] buffer, IntPtr count);

        [DllImport("libc", SetLastError = true, EntryPoint = "close")]
        private static extern int SysClose(int fd);

        [DllImport("libc", SetLastError = true, EntryPoint = "dup2")]
        private static extern int SysDup2(int fd, int newFd);

        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        private static extern int SysOpen(IntPtr path, int flags, uint mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
        private static extern int SysFcntl(int fd, int command, int argument);

        [DllImport("libc", EntryPoint = "getppid")]
        private static extern int SysGetPpid();

        [DllImport("libc", SetLastError = true, EntryPoint = "chdir")]
        private static extern int SysChdir(IntPtr path);

        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np")]
        private static extern int SysFileActionsAddChdir(IntPtr actions, IntPtr path);

        private const short POSIX_SPAWN_SETPGROUP = 0x02;
        private const short POSIX_SPAWN_SETSIGDEF = 0x04;
        private const int SIGPIPE = 13;
        // glibc (2.26+) and musl spell it 0x80; macOS has it as 0x400, and there an open(2)
        // does not hand a session leader its controlling terminal anyway (that takes
        // TIOCSCTTY, which no file action can issue).
        private static readonly short POSIX_SPAWN_SETSID =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? (short)0x400 : (short)0x80;
        // The value differs by kernel: Linux's 0x80000 is not O_CLOEXEC on macOS, whose
        // pipe2 rejects it with EINVAL - which broke every pipe to a child there.
        private static readonly int O_CLOEXEC =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0x1000000 : 0x80000;
        private const int F_GETFD = 1;
        private const int F_SETFD = 2;
        private const int FD_CLOEXEC = 1;
        private const int X_OK = 1;
        private const int EINTR = 4;
        private const int ENOEXEC = 8;
        private const int EACCES = 13;

        #endregion

        #region Native string vectors

        /// <summary>
        /// A NULL-terminated char*[] in unmanaged memory. MutableString is a byte string,
        /// so the bytes go across unchanged rather than through a CLR encoding that could
        /// mangle a path or an argument that is not valid UTF-8.
        /// </summary>
        private sealed class NativeStrings : IDisposable {
            private readonly List<IntPtr> _allocations = new List<IntPtr>();

            public IntPtr Vector { get; private set; }

            public NativeStrings() {
                Vector = IntPtr.Zero;
            }

            public NativeStrings(IList<object>/*!*/ items) {
                IntPtr vector = Marshal.AllocHGlobal(IntPtr.Size * (items.Count + 1));
                _allocations.Add(vector);
                for (int i = 0; i < items.Count; i++) {
                    Marshal.WriteIntPtr(vector, i * IntPtr.Size, Allocate(items[i]));
                }
                Marshal.WriteIntPtr(vector, items.Count * IntPtr.Size, IntPtr.Zero);
                Vector = vector;
            }

            public IntPtr Allocate(object value) {
                byte[] bytes = ToBytes(value);
                IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
                _allocations.Add(p);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                Marshal.WriteByte(p, bytes.Length, 0);
                return p;
            }

            public void Dispose() {
                foreach (var p in _allocations) {
                    Marshal.FreeHGlobal(p);
                }
                _allocations.Clear();
                Vector = IntPtr.Zero;
            }
        }

        private static int ToInt(object value) {
            return (value is int) ? (int)value : Convert.ToInt32(value);
        }

        /// <summary>The environment this process would pass on, as the "K=V" vector execve wants.</summary>
        private static NativeStrings/*!*/ CurrentEnvironment() {
            var items = new List<object>();
            foreach (var entry in RubyEnvironment.GetVariables()) {
                items.Add(MutableString.CreateMutable(entry.Key + "=" + entry.Value, RubyEncoding.UTF8));
            }
            return new NativeStrings(items);
        }

        private static byte[]/*!*/ ToBytes(object value) {
            var str = value as MutableString;
            if (str != null) {
                return str.ToByteArray();
            }
            return System.Text.Encoding.UTF8.GetBytes(value == null ? "" : value.ToString());
        }

        #endregion

        #region __spawn__, __exec__

        // Opcodes for the file-action descriptions the prelude builds. Keeping them numeric
        // keeps the C# side out of the business of understanding redirection syntax.
        private const int ActionDup2 = 0;
        private const int ActionClose = 1;
        private const int ActionOpen = 2;
        private const int ActionChdir = 3;

        private static int ApplyActions(IntPtr fileActions, NativeStrings/*!*/ strings, RubyArray actions) {
            if (actions == null) {
                return 0;
            }
            foreach (var item in actions) {
                var action = (RubyArray)item;
                int error;
                switch (ToInt(action[0])) {
                    case ActionDup2:
                        // No need to clear FD_CLOEXEC on the source here: the action runs in
                        // the child between fork and exec, where the flag has not taken effect
                        // yet, and glibc handles "dup2 a descriptor to itself" by clearing it.
                        error = SysFileActionsAddDup2(fileActions,
                            ToInt(action[2]), ToInt(action[1]));
                        break;

                    case ActionClose:
                        error = SysFileActionsAddClose(fileActions, ToInt(action[1]));
                        break;

                    case ActionChdir:
                        error = SysFileActionsAddChdir(fileActions, strings.Allocate(action[1]));
                        break;

                    default:
                        error = SysFileActionsAddOpen(fileActions,
                            ToInt(action[1]),
                            strings.Allocate(action[2]),
                            ToInt(action[3]),
                            (uint)ToInt(action[4]));
                        break;
                }
                if (error != 0) {
                    return -error;
                }
            }
            return 0;
        }

        /// <summary>
        /// close_others: every descriptor above the standard three that the child was not
        /// explicitly given is closed. The closes go after the redirections, so a descriptor
        /// that was dup2'd from is closed in the child, but only once the copy exists.
        /// </summary>
        private static int AddCloseOthers(IntPtr fileActions, RubyArray actions) {
            var keep = new HashSet<int>();
            if (actions != null) {
                foreach (var item in actions) {
                    var action = (RubyArray)item;
                    int opcode = ToInt(action[0]);
                    if (opcode == ActionDup2 || opcode == ActionOpen) {
                        keep.Add(ToInt(action[1]));
                    }
                }
            }

            // There is no portable way to ask which descriptors are open, and walking /proc
            // would need a descriptor of its own; asking the kernel about each in turn is a
            // cheap syscall and the limit is a thousand or so in practice.
            for (int fd = 3; fd < 4096; fd++) {
                if (keep.Contains(fd) || SysFcntl(fd, F_GETFD, 0) < 0) {
                    continue;
                }
                int error = SysFileActionsAddClose(fileActions, fd);
                if (error != 0) {
                    return -error;
                }
            }
            return 0;
        }

        /// <summary>
        /// posix_spawn(3). Returns the child's pid, or -errno. glibc reports a failure of the
        /// execve in the child back through the return value, so a missing or unusable command
        /// is ENOENT/EACCES from here rather than a silent exit status 127.
        /// </summary>
        [RubyMethod("__spawn__", RubyMethodAttributes.PublicSingleton)]
        public static object SpawnPrimitive(RubyContext/*!*/ context, RubyModule/*!*/ self,
            [NotNull]MutableString/*!*/ file, [NotNull]RubyArray/*!*/ argv, RubyArray envp, RubyArray actions,
            object pgroup, bool closeOthers) {

            if (IsWindows) {
                return ScriptingRuntimeHelpers.Int32ToObject(
                    WindowsSpawn.Spawn(context, file, argv, envp, actions, pgroup));
            }
            return ScriptingRuntimeHelpers.Int32ToObject(
                Spawn(file, argv, envp, actions, pgroup, closeOthers, false));
        }

        /// <summary>
        /// posix_spawn(3) with the file actions the prelude describes. With newSession the child
        /// calls setsid(2) before any file action runs (POSIX_SPAWN_SETSID; glibc and musl both
        /// do it in that order), so the first terminal it then opens without O_NOCTTY becomes
        /// its controlling terminal - which is how the pty library gives a child its pty
        /// without fork. Returns the pid or -errno. Unix only.
        /// </summary>
        internal static int Spawn(MutableString/*!*/ file, RubyArray/*!*/ argv, RubyArray envp, RubyArray actions,
            object pgroup, bool closeOthers, bool newSession) {

            // posix_spawn_file_actions_t is 80 bytes and posix_spawnattr_t 336 on glibc; both
            // are opaque, so over-allocate rather than mirror a private layout.
            IntPtr fileActions = Marshal.AllocHGlobal(1024);
            IntPtr attributes = Marshal.AllocHGlobal(1024);
            // sigset_t is 128 bytes on glibc and smaller elsewhere
            IntPtr signals = Marshal.AllocHGlobal(256);
            var strings = new NativeStrings();
            NativeStrings argvVector = null;
            NativeStrings envpVector = null;
            try {
                for (int i = 0; i < 1024; i += 8) {
                    Marshal.WriteInt64(fileActions, i, 0);
                    Marshal.WriteInt64(attributes, i, 0);
                }
                SysFileActionsInit(fileActions);
                SysSpawnAttrInit(attributes);

                int error = ApplyActions(fileActions, strings, actions);
                if (error != 0) {
                    return error;
                }

                if (closeOthers) {
                    error = AddCloseOthers(fileActions, actions);
                    if (error != 0) {
                        return error;
                    }
                }

                // The .NET runtime ignores SIGPIPE, and an ignored signal stays ignored across
                // exec - so every child used to start with SIGPIPE ignored, and `yes | head` in a
                // child shell (or in a shell driven through PTY.spawn) wrote "Broken pipe" errors
                // where it should have died quietly. MRI's children always get the default.
                SysSigEmptySet(signals);
                SysSigAddSet(signals, SIGPIPE);
                SysSpawnAttrSetSigDefault(attributes, signals);
                short flags = POSIX_SPAWN_SETSIGDEF;
                if (pgroup != null) {
                    SysSpawnAttrSetPGroup(attributes, ToInt(pgroup));
                    flags |= POSIX_SPAWN_SETPGROUP;
                }
                if (newSession) {
                    flags |= POSIX_SPAWN_SETSID;
                }
                SysSpawnAttrSetFlags(attributes, flags);

                argvVector = new NativeStrings(argv);
                envpVector = (envp != null) ? new NativeStrings(envp) : CurrentEnvironment();

                IntPtr path = strings.Allocate(file);
                int pid;
                error = SysPosixSpawn(out pid, path, fileActions, attributes, argvVector.Vector, envpVector.Vector);

                if (error != 0) {
                    return -error;
                }
                return pid;
            } finally {
                SysFileActionsDestroy(fileActions);
                SysSpawnAttrDestroy(attributes);
                Marshal.FreeHGlobal(fileActions);
                Marshal.FreeHGlobal(attributes);
                Marshal.FreeHGlobal(signals);
                strings.Dispose();
                if (argvVector != null) {
                    argvVector.Dispose();
                }
                if (envpVector != null) {
                    envpVector.Dispose();
                }
            }
        }

        /// <summary>
        /// execve(2): replaces this process. Only returns on failure, as -errno. The caller has
        /// already flushed anything Ruby had buffered.
        /// </summary>
        [RubyMethod("__exec__", RubyMethodAttributes.PublicSingleton)]
        public static object ExecPrimitive(RubyContext/*!*/ context, RubyModule/*!*/ self,
            [NotNull]MutableString/*!*/ file, [NotNull]RubyArray/*!*/ argv, RubyArray envp, RubyArray actions) {

            if (IsWindows) {
                return ScriptingRuntimeHelpers.Int32ToObject(WindowsSpawn.Exec(context, file, argv, envp, actions));
            }

            var strings = new NativeStrings();
            try {
                IntPtr path = strings.Allocate(file);

                // A descriptor that was explicitly named in the options survives the exec even
                // if the exec fails, which is what MRI guarantees - so the close-on-exec flags
                // come off before anything can go wrong.
                if (actions != null) {
                    foreach (var item in actions) {
                        var action = (RubyArray)item;
                        if (ToInt(action[0]) == ActionDup2) {
                            ClearCloseOnExec(ToInt(action[1]));
                            ClearCloseOnExec(ToInt(action[2]));
                        }
                    }
                }

                // Applying the redirections is destructive, so refuse before touching anything
                // if the command could not have been run anyway.
                if (SysAccess(path, X_OK) != 0) {
                    return ScriptingRuntimeHelpers.Int32ToObject(Failure());
                }

                if (actions != null) {
                    foreach (var item in actions) {
                        var action = (RubyArray)item;
                        switch (ToInt(action[0])) {
                            case ActionDup2:
                                if (ToInt(action[1]) != ToInt(action[2])) {
                                    SysDup2(ToInt(action[2]), ToInt(action[1]));
                                }
                                break;

                            case ActionClose:
                                SysClose(ToInt(action[1]));
                                break;

                            case ActionChdir:
                                if (SysChdir(strings.Allocate(action[1])) != 0) {
                                    return ScriptingRuntimeHelpers.Int32ToObject(Failure());
                                }
                                break;

                            default:
                                int opened = SysOpen(strings.Allocate(action[2]),
                                    ToInt(action[3]), (uint)ToInt(action[4]));
                                if (opened < 0) {
                                    return ScriptingRuntimeHelpers.Int32ToObject(Failure());
                                }
                                int target = ToInt(action[1]);
                                if (opened != target) {
                                    SysDup2(opened, target);
                                    SysClose(opened);
                                }
                                break;
                        }
                    }
                }

                using (var argvVector = new NativeStrings(argv))
                using (var envpVector = (envp != null) ? new NativeStrings(envp) : CurrentEnvironment()) {
                    SysExecve(path, argvVector.Vector, envpVector.Vector);
                }
                return ScriptingRuntimeHelpers.Int32ToObject(Failure());
            } finally {
                strings.Dispose();
            }
        }

        #endregion

        #region __waitpid__

        /// <summary>
        /// waitpid(2). Returns [pid, Process::Status], nil when WNOHANG found nothing, or
        /// -errno. The status carries the raw wait status word, which is what every
        /// Process::Status predicate is defined in terms of.
        /// </summary>
        [RubyMethod("__waitpid__", RubyMethodAttributes.PublicSingleton)]
        public static object WaitPidPrimitive(RubyContext/*!*/ context, RubyModule/*!*/ self, int pid, int flags) {
            if (IsWindows) {
                return WindowsSpawn.WaitPid(context, pid, flags);
            }

            int status;
            int result;
            do {
                result = SysWaitPid(pid, out status, flags);
            } while (result < 0 && Marshal.GetLastWin32Error() == EINTR);

            if (result < 0) {
                return ScriptingRuntimeHelpers.Int32ToObject(Failure());
            }
            if (result == 0) {
                // WNOHANG and the child is still running.
                return null;
            }

            var childStatus = new Status(result, status);
            context.ChildProcessExitStatus = childStatus;
            return new RubyArray { ScriptingRuntimeHelpers.Int32ToObject(result), childStatus };
        }

        /// <summary>
        /// dup2 clears FD_CLOEXEC on the descriptor it creates, but "redirect this descriptor
        /// to itself" is a no-op that would leave the flag on - and it is precisely how a Ruby
        /// program says "let the child have this one".
        /// </summary>
        private static void ClearCloseOnExec(int fd) {
            if (IsWindows) {
                return;
            }
            int flags = SysFcntl(fd, F_GETFD, 0);
            if (flags >= 0 && (flags & FD_CLOEXEC) != 0) {
                SysFcntl(fd, F_SETFD, flags & ~FD_CLOEXEC);
            }
        }

        /// <summary>
        /// FD_CLOEXEC for a descriptor, which is what IO#close_on_exec really is. Answering
        /// from an instance variable, as the prelude used to, told the truth about nothing:
        /// whether the child sees the descriptor is decided by the kernel flag.
        /// </summary>
        [RubyMethod("__get_cloexec__", RubyMethodAttributes.PublicSingleton)]
        public static object GetCloseOnExec(RubyContext/*!*/ context, RubyModule/*!*/ self, [DefaultProtocol]int descriptor) {
            if (IsWindows) {
                return WindowsSpawn.GetCloseOnExec(context, descriptor);
            }
            int native = NativeDescriptor(context, self, descriptor);
            if (native < 0) {
                return null;
            }
            int flags = SysFcntl(native, F_GETFD, 0);
            return (flags < 0) ? null : (object)((flags & FD_CLOEXEC) != 0);
        }

        /// <summary>
        /// Marks a native descriptor close-on-exec. dup(2) does not carry the flag over, and
        /// MRI's IO#dup sets it on the copy - see IoOps.InitializeCopy.
        /// </summary>
        internal static void SetCloseOnExec(int nativeDescriptor) {
            if (IsWindows) {
                return;
            }
            if (nativeDescriptor < 0) {
                return;
            }
            int flags = SysFcntl(nativeDescriptor, F_GETFD, 0);
            if (flags >= 0) {
                SysFcntl(nativeDescriptor, F_SETFD, flags | FD_CLOEXEC);
            }
        }

        [RubyMethod("__set_cloexec__", RubyMethodAttributes.PublicSingleton)]
        public static object SetCloseOnExec(RubyContext/*!*/ context, RubyModule/*!*/ self,
            [DefaultProtocol]int descriptor, bool value) {

            if (IsWindows) {
                return WindowsSpawn.SetCloseOnExec(context, descriptor, value);
            }

            int native = NativeDescriptor(context, self, descriptor);
            if (native < 0) {
                return null;
            }
            int flags = SysFcntl(native, F_GETFD, 0);
            if (flags < 0) {
                return null;
            }
            flags = value ? (flags | FD_CLOEXEC) : (flags & ~FD_CLOEXEC);
            return (SysFcntl(native, F_SETFD, flags) < 0) ? null : (object)value;
        }

        /// <summary>A Process::Status that did not come from a wait, for Process::Status.wait's
        /// "no children" answer.</summary>
        [RubyMethod("__make_status__", RubyMethodAttributes.PublicSingleton)]
        public static Status/*!*/ MakeStatus(RubyModule/*!*/ self, [DefaultProtocol]int pid, [DefaultProtocol]int status) {
            return new Status(pid, status);
        }

        /// <summary>$? is read-only from Ruby, so putting back a saved value needs a primitive.</summary>
        [RubyMethod("__set_last_status__", RubyMethodAttributes.PublicSingleton)]
        public static object SetLastStatus(RubyContext/*!*/ context, RubyModule/*!*/ self, object status) {
            context.ChildProcessExitStatus = status;
            return status;
        }

        /// <summary>
        /// The operating system descriptor behind one of IronRuby's, which are indices into a
        /// per-context table and mean nothing to a child process. Returns -1 when the IO has no
        /// descriptor the kernel knows about (an in-process pipe or a StringIO, say).
        /// </summary>
        [RubyMethod("__native_fd__", RubyMethodAttributes.PublicSingleton)]
        public static int NativeDescriptor(RubyContext/*!*/ context, RubyModule/*!*/ self, [DefaultProtocol]int descriptor) {
            if (descriptor >= 0 && descriptor <= 2) {
                return descriptor;
            }
            var stream = context.GetStream(descriptor);
            if (stream == null) {
                return -1;
            }
            // Windows has no descriptor table to translate into: WindowsSpawn resolves a
            // redirection by looking the stream up under this very number, so the number to
            // put in the file action is the one Ruby already uses.
            return IsWindows ? descriptor : RubyIO.DescriptorOf(stream);
        }

        #endregion

        #region pipes

        /// <summary>
        /// pipe(2), with each end wrapped in an IO. IO.pipe's own pipe is an in-process queue
        /// whose "descriptors" are indices into a table, so a child process cannot be handed
        /// one; only a real descriptor can survive posix_spawn and exec.
        /// </summary>
        [RubyMethod("__os_pipe__", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ OsPipe(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            if (IsWindows) {
                return WindowsSpawn.OsPipe(context);
            }

            // O_CLOEXEC, so that our end of the pipe does not leak into the child: a child
            // that holds the write end open means the read end never reaches end of file.
            int[] fds = new int[2];
            if (SysPipe2(fds, O_CLOEXEC) != 0) {
                throw RubyExceptions.CreateEINVAL("pipe");
            }
            // Not a FileStream: a read that is already blocked has to be interruptible by a
            // close from another thread, which is what Ruby's IO#close promises.
            var reader = new DescriptorStream(fds[0], true, false, true);
            var writer = new DescriptorStream(fds[1], false, true, true);
            var reading = new RubyIO(context, reader, Adopt(context, fds[0], reader), IOMode.ReadOnly);
            var writing = new RubyIO(context, writer, Adopt(context, fds[1], writer), IOMode.WriteOnly);
            // Both ends resolve the current defaults, as every other freshly opened stream does.
            reading.SetEncodings(null, null);
            writing.SetEncodings(null, null);
            return new RubyArray { reading, writing };
        }

        /// <summary>
        /// Files the stream under the number the kernel gave it, so that #fileno is a
        /// descriptor a child can be told about - "write to fd 7" means nothing to a child
        /// unless 7 is the number the kernel knows. Falls back to the usual table index if
        /// something else already holds that number.
        /// </summary>
        internal static int Adopt(RubyContext/*!*/ context, int descriptor, Stream/*!*/ stream) {
            if (context.GetStream(descriptor) != null) {
                return context.AllocateFileDescriptor(stream);
            }
            context.SetOrAllocateDescriptor(descriptor, stream);
            return descriptor;
        }

        /// <summary>One IO reading from one stream and writing to another, which is what
        /// IO.popen(cmd, "r+") hands back.</summary>
        [RubyMethod("__duplex_io__", RubyMethodAttributes.PublicSingleton)]
        public static RubyIO/*!*/ DuplexIO(RubyContext/*!*/ context, RubyModule/*!*/ self,
            [NotNull]RubyIO/*!*/ reader, [NotNull]RubyIO/*!*/ writer) {

            int readerFd = reader.GetFileDescriptor();
            int writerFd = writer.GetFileDescriptor();
            var input = context.GetStream(readerFd);
            var output = context.GetStream(writerFd);
            // Autoflush: the other end of this pipe is a process waiting to be spoken to, so
            // a write that sits in a buffer is a deadlock rather than a saving.
            var sink = new StreamWriter(output);
            sink.AutoFlush = true;
            var source = new StreamReader(input);

            // The duplex IO takes ownership of both streams. Leaving the two temporary pipe IOs
            // in the descriptor table leaks entries that still name disposed streams; once the
            // kernel reuses those descriptor numbers, later popen/spawn calls receive synthetic
            // descriptors and cannot pass them to children.
            context.RemoveFileDescriptor(readerFd);
            context.RemoveFileDescriptor(writerFd);
            reader.DetachStream();
            writer.DetachStream();
            return new RubyIO(context, source, sink, IOMode.ReadWrite);
        }

        #endregion

        #region backquotes

        /// <summary>
        /// Runs a child with its standard output on a pipe and returns everything it wrote,
        /// which is what Kernel#` is. The pipe has to be a real one: IO.pipe is an in-process
        /// queue with no descriptor a child could inherit.
        /// </summary>
        internal static MutableString/*!*/ CaptureOutput(RubyContext/*!*/ context,
            MutableString/*!*/ file, RubyArray/*!*/ argv, RubyArray envp, RubyArray actions) {

            if (IsWindows) {
                return WindowsSpawn.CaptureOutput(context, file, argv, envp, actions);
            }

            int[] fds = new int[2];
            if (SysPipe2(fds, O_CLOEXEC) != 0) {
                throw RubyExceptions.CreateEINVAL("pipe");
            }

            int pid;
            try {
                var redirected = new RubyArray();
                if (actions != null) {
                    redirected.AddRange(actions);
                }
                redirected.Add(new RubyArray {
                    ScriptingRuntimeHelpers.Int32ToObject(ActionDup2),
                    ScriptingRuntimeHelpers.Int32ToObject(1),
                    ScriptingRuntimeHelpers.Int32ToObject(fds[1])
                });

                pid = ToInt(SpawnPrimitive(context, null, file, argv, envp, redirected, null, false));
                if (pid < 0) {
                    throw RubyExceptions.CreateENOENT(file.ToString());
                }
            } catch {
                SysClose(fds[0]);
                SysClose(fds[1]);
                throw;
            }

            SysClose(fds[1]);
            var output = new List<byte>();
            byte[] buffer = new byte[8192];
            while (true) {
                int read = (int)SysRead(fds[0], buffer, (IntPtr)buffer.Length);
                if (read < 0) {
                    if (Marshal.GetLastWin32Error() == EINTR) {
                        continue;
                    }
                    break;
                }
                if (read == 0) {
                    break;
                }
                for (int i = 0; i < read; i++) {
                    output.Add(buffer[i]);
                }
            }
            SysClose(fds[0]);

            WaitPidPrimitive(context, null, pid, 0);
            return MutableString.CreateBinary(output.ToArray(), context.GetPathEncoding());
        }

        [RubyMethod("__backquote__", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Backquote(RubyContext/*!*/ context, RubyModule/*!*/ self,
            [NotNull]MutableString/*!*/ file, [NotNull]RubyArray/*!*/ argv, RubyArray envp, RubyArray actions) {
            return CaptureOutput(context, file, argv, envp, actions);
        }

        #endregion
    }
}
#endif

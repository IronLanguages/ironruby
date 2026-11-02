/* ****************************************************************************
 *
 * The Windows half of the process primitives PosixSpawn.cs implements with
 * posix_spawn(3), pipe(2) and waitpid(2).
 *
 * The prelude (Src/StdLib/ironruby/ruby4.rb) is written against a Unix-shaped
 * set of primitives - __spawn__ takes a file, an argv, an envp and a list of
 * file actions, and hands back a pid or a negative errno - because that is what
 * Ruby's own Process API is defined in terms of.  Rather than teach the prelude
 * about two worlds, the same primitives are implemented here on CreateProcess,
 * CreatePipe and WaitForSingleObject, and PosixSpawn.cs routes to them when it
 * is not running on Unix.  Errors come back as the nearest Linux errno, since
 * that is what the prelude's __check__ turns into an Errno:: class.
 *
 * What it does not do: redirect anything but the three standard descriptors.
 * Windows has no descriptor table to hand a child, only the three slots in
 * STARTUPINFO plus explicitly inherited handles, and Ruby's "fd 5 in the child
 * is fd 9 here" has no equivalent.  Such a redirection is refused (EINVAL)
 * rather than silently dropped.
 *
 * ***************************************************************************/
#if FEATURE_PROCESS

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// CreateProcess/CreatePipe standing in for posix_spawn/pipe. Everything here is
    /// only reached when <see cref="RubyProcess.IsWindows"/>.
    /// </summary>
    internal static class WindowsSpawn {

        #region kernel32

        private const int STARTF_USESTDHANDLES = 0x00000100;
        private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int CREATE_NEW_PROCESS_GROUP = 0x00000200;
        private const int HANDLE_FLAG_INHERIT = 0x00000001;
        private const int DUPLICATE_SAME_ACCESS = 0x00000002;
        private const uint INFINITE = 0xFFFFFFFF;
        private const uint WAIT_TIMEOUT = 258;
        private const uint WAIT_FAILED = 0xFFFFFFFF;

        private const int STD_INPUT_HANDLE = -10;
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;

        // The Win32 errors CreateProcess reports for a command that cannot be run, and the
        // Linux errno each one stands for on the Ruby side.
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_SHARING_VIOLATION = 32;
        private const int ERROR_LOCK_VIOLATION = 33;
        private const int ERROR_INVALID_NAME = 123;
        private const int ERROR_BAD_EXE_FORMAT = 193;
        private const int ERROR_BROKEN_PIPE = 109;

        // Linux errno values, which is what the prelude's Errno:: table is built from.
        private const int ENOENT = 2;
        private const int EBADF = 9;
        private const int ECHILD = 10;
        private const int EACCES = 13;
        private const int EINVAL = 22;
        private const int ENOEXEC = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars, dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, int dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
            out IntPtr target, int access, bool inheritHandle, int options);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool GetHandleInformation(IntPtr handle, out int flags);

        [DllImport("kernel32", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32", SetLastError = true)]
        private static extern uint WaitForMultipleObjects(int count, IntPtr[] handles, bool waitAll, uint milliseconds);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, int processGroupId);

        #endregion

        #region children

        /// <summary>
        /// The process handles of the children this process started, by pid. Windows has no
        /// waitpid: a pid alone cannot be waited on (and is reused), so the handle CreateProcess
        /// returned is what keeps the exit status readable until Ruby asks for it.
        /// </summary>
        private static readonly Dictionary<int, IntPtr> _children = new Dictionary<int, IntPtr>();

        private static void Remember(int pid, IntPtr handle) {
            lock (_children) {
                IntPtr existing;
                if (_children.TryGetValue(pid, out existing)) {
                    // A pid Windows has already recycled. The old child was reaped or abandoned;
                    // its handle is ours to release.
                    CloseHandle(existing);
                }
                _children[pid] = handle;
            }
        }

        private static bool Forget(int pid, out IntPtr handle) {
            lock (_children) {
                if (_children.TryGetValue(pid, out handle)) {
                    _children.Remove(pid);
                    return true;
                }
                return false;
            }
        }

        private static KeyValuePair<int, IntPtr>[] Children() {
            lock (_children) {
                var result = new KeyValuePair<int, IntPtr>[_children.Count];
                int i = 0;
                foreach (var entry in _children) {
                    result[i++] = entry;
                }
                return result;
            }
        }

        #endregion

        #region descriptors

        /// <summary>
        /// The OS handle behind a Ruby descriptor. 0/1/2 are the standard handles; anything
        /// else has to be a stream this context knows about and that is backed by a real
        /// handle (a file, or one end of a __os_pipe__ pipe). IntPtr.Zero means "no handle",
        /// which is a redirection that cannot be carried out.
        /// </summary>
        internal static IntPtr HandleOf(RubyContext/*!*/ context, int descriptor) {
            switch (descriptor) {
                case 0: return GetStdHandle(STD_INPUT_HANDLE);
                case 1: return GetStdHandle(STD_OUTPUT_HANDLE);
                case 2: return GetStdHandle(STD_ERROR_HANDLE);
            }

            Stream stream = context.GetStream(descriptor);
            return HandleOfStream(stream);
        }

        private static IntPtr HandleOfStream(Stream stream) {
            var file = stream as FileStream;
            if (file != null && file.SafeFileHandle != null && !file.SafeFileHandle.IsInvalid) {
                return file.SafeFileHandle.DangerousGetHandle();
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// A copy of <paramref name="handle"/> that a child inherits. The original is left
        /// alone: it may be this process's own stdout, and turning inheritance on for good
        /// would leak it into every later child.
        /// </summary>
        private static IntPtr Inheritable(IntPtr handle) {
            IntPtr copy;
            IntPtr self = GetCurrentProcess();
            if (!DuplicateHandle(self, handle, self, out copy, 0, true, DUPLICATE_SAME_ACCESS)) {
                return IntPtr.Zero;
            }
            return copy;
        }

        #endregion

        #region command line and environment

        /// <summary>
        /// argv joined the way CommandLineToArgvW takes it apart again: a backslash is only
        /// special before a quote, and an argument is quoted when it is empty or holds a
        /// space, a tab or a quote.
        /// </summary>
        internal static string/*!*/ BuildCommandLine(IList<object>/*!*/ argv) {
            var result = new StringBuilder();

            // cmd.exe does not parse its command line with CommandLineToArgvW: after /c it
            // takes the rest of the line verbatim, quotes, redirections and all. Quoting the
            // command as an argument would hand the shell a literal quoted string to run.
            //
            // /s is what makes the wrapping quotes safe. Without it cmd only leaves quotes
            // alone under a list of conditions (see `cmd /?`) that a real Ruby command line -
            // `"C:\ir\ir.cmd" -e "END { }" 2>&1`, four quotes and a redirection - does not
            // meet, and it then strips the first and the last quote of the whole line, which
            // takes the closing quote off the -e argument. With /s cmd strips exactly the
            // first and last character when both are quotes and runs the rest verbatim.
            if (argv.Count >= 3 && String.Equals(ToStr(argv[1]), "/c", StringComparison.OrdinalIgnoreCase)) {
                AppendArgument(result, ToStr(argv[0]));
                result.Append(" /s /c \"");
                for (int i = 2; i < argv.Count; i++) {
                    if (i > 2) {
                        result.Append(' ');
                    }
                    result.Append(ToStr(argv[i]));
                }
                result.Append('"');
                return result.ToString();
            }

            for (int i = 0; i < argv.Count; i++) {
                if (i > 0) {
                    result.Append(' ');
                }
                AppendArgument(result, ToStr(argv[i]));
            }
            return result.ToString();
        }

        private static void AppendArgument(StringBuilder/*!*/ result, string/*!*/ argument) {
            bool needsQuotes = argument.Length == 0 ||
                argument.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) >= 0;

            if (!needsQuotes) {
                result.Append(argument);
                return;
            }

            result.Append('"');
            for (int i = 0; i < argument.Length; i++) {
                int backslashes = 0;
                while (i < argument.Length && argument[i] == '\\') {
                    backslashes++;
                    i++;
                }
                if (i == argument.Length) {
                    // Trailing backslashes precede the closing quote, so they have to be doubled.
                    result.Append('\\', backslashes * 2);
                    break;
                }
                if (argument[i] == '"') {
                    result.Append('\\', backslashes * 2 + 1).Append('"');
                } else {
                    result.Append('\\', backslashes).Append(argument[i]);
                }
            }
            result.Append('"');
        }

        private static string/*!*/ ToStr(object value) {
            var str = value as MutableString;
            return (str != null) ? str.ToString() : (value == null ? "" : value.ToString());
        }

        /// <summary>
        /// The CREATE_UNICODE_ENVIRONMENT block: "K=V\0K=V\0\0", sorted the way Windows
        /// insists on (case-insensitive by name), or IntPtr.Zero to inherit this one.
        /// </summary>
        private static IntPtr BuildEnvironment(RubyArray envp) {
            if (envp == null) {
                return IntPtr.Zero;
            }

            var entries = new List<string>();
            foreach (object item in envp) {
                entries.Add(ToStr(item));
            }
            entries.Sort((a, b) => String.Compare(NameOf(a), NameOf(b), StringComparison.OrdinalIgnoreCase));

            var block = new StringBuilder();
            foreach (string entry in entries) {
                block.Append(entry).Append('\0');
            }
            block.Append('\0');
            return Marshal.StringToHGlobalUni(block.ToString());
        }

        private static string/*!*/ NameOf(string/*!*/ entry) {
            int equals = entry.IndexOf('=');
            return (equals < 0) ? entry : entry.Substring(0, equals);
        }

        #endregion

        #region file actions

        // Kept in step with PosixSpawn's opcodes, which the prelude emits.
        private const int ActionDup2 = 0;
        private const int ActionClose = 1;
        private const int ActionOpen = 2;
        private const int ActionChdir = 3;

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ_WRITE_DELETE = 0x00000007;
        private const uint CREATE_ALWAYS = 2;
        private const uint OPEN_ALWAYS = 4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_APPEND_DATA = 0x00000004;

        /// <summary>
        /// How a child's three standard slots are set up, plus the directory it starts in.
        /// Handles listed in <see cref="Opened"/> were made here and are closed once the child
        /// has them.
        /// </summary>
        private sealed class Redirections : IDisposable {
            public IntPtr StdIn, StdOut, StdErr;
            public string WorkingDirectory;
            public int Error;                       // -errno, 0 when fine
            public readonly List<IntPtr> Opened = new List<IntPtr>();

            public void Dispose() {
                foreach (IntPtr handle in Opened) {
                    CloseHandle(handle);
                }
                Opened.Clear();
            }
        }

        private static int ToInt(object value) {
            return (value is int) ? (int)value : Convert.ToInt32(value);
        }

        private static Redirections/*!*/ Plan(RubyContext/*!*/ context, RubyArray actions) {
            return Plan(context, actions, IntPtr.Zero);
        }

        /// <summary>
        /// <paramref name="stdOut"/>, when given, is where the child's standard output goes
        /// before any action is applied - the backquote pipe. It has to be in place first so
        /// that a later `err: [:child, :out]` copies the pipe and not this process's console.
        /// </summary>
        private static Redirections/*!*/ Plan(RubyContext/*!*/ context, RubyArray actions, IntPtr stdOut) {
            var plan = new Redirections {
                StdIn = GetStdHandle(STD_INPUT_HANDLE),
                StdOut = (stdOut != IntPtr.Zero) ? stdOut : GetStdHandle(STD_OUTPUT_HANDLE),
                StdErr = GetStdHandle(STD_ERROR_HANDLE),
            };
            if (actions == null) {
                return plan;
            }

            foreach (object item in actions) {
                var action = (RubyArray)item;
                int opcode = ToInt(action[0]);

                if (opcode == ActionChdir) {
                    plan.WorkingDirectory = ToStr(action[1]);
                    continue;
                }

                int target = ToInt(action[1]);
                if (target > 2) {
                    // Nothing in a Windows child answers to descriptor 5.
                    plan.Error = -EINVAL;
                    return plan;
                }

                switch (opcode) {
                    case ActionDup2: {
                        // The actions run in order, and dup2's source is a descriptor *in the
                        // child*: `err: [:child, :out]` is dup2(1 -> 2) and has to pick up
                        // whatever an earlier action put in the child's slot 1, not this
                        // process's own stdout. (mspec's ruby_exe spells `2>&1` exactly this
                        // way, so getting it wrong loses every subprocess's stderr.)
                        int from = ToInt(action[2]);
                        IntPtr source = (from >= 0 && from <= 2) ? Current(plan, from) : HandleOf(context, from);
                        if (source == IntPtr.Zero || source == new IntPtr(-1)) {
                            plan.Error = -EBADF;
                            return plan;
                        }
                        Assign(plan, target, source);
                        break;
                    }

                    case ActionClose:
                        // A child whose stdout is closed is closest to one writing to NUL here;
                        // a real "no handle at all" makes the CRT of most programs crash.
                        Assign(plan, target, OpenNul(plan, target == 0));
                        break;

                    default: {
                        string path = ToStr(action[2]);
                        IntPtr opened = OpenFile(path, ToInt(action[3]));
                        if (opened == new IntPtr(-1)) {
                            int error = Marshal.GetLastWin32Error();
                            plan.Dispose();
                            throw RedirectionError(error, path);
                        }
                        plan.Opened.Add(opened);
                        Assign(plan, target, opened);
                        break;
                    }
                }
            }
            return plan;
        }

        /// <summary>What the child's descriptor holds at this point in the plan.</summary>
        private static IntPtr Current(Redirections/*!*/ plan, int descriptor) {
            switch (descriptor) {
                case 0: return plan.StdIn;
                case 1: return plan.StdOut;
                default: return plan.StdErr;
            }
        }

        private static void Assign(Redirections/*!*/ plan, int target, IntPtr handle) {
            switch (target) {
                case 0: plan.StdIn = handle; break;
                case 1: plan.StdOut = handle; break;
                default: plan.StdErr = handle; break;
            }
        }

        private static IntPtr OpenNul(Redirections/*!*/ plan, bool reading) {
            IntPtr handle = CreateFileW("NUL", reading ? GENERIC_READ : GENERIC_WRITE,
                FILE_SHARE_READ_WRITE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (handle != new IntPtr(-1)) {
                plan.Opened.Add(handle);
            }
            return handle;
        }

        /// <summary>
        /// CreateFile standing in for the open(2) the file action describes. The flags are the
        /// Linux O_* values: the prelude builds the actions from its own SPAWN_O_* constants,
        /// which are the same on every platform because only these two ends read them.
        /// </summary>
        private static IntPtr OpenFile(string/*!*/ path, int flags) {
            const int O_WRONLY = 1, O_RDWR = 2, O_CREAT = 64, O_TRUNC = 512, O_APPEND = 1024;

            bool writing = (flags & (O_WRONLY | O_RDWR)) != 0;
            uint access = writing ? ((flags & O_RDWR) != 0 ? GENERIC_READ | GENERIC_WRITE : GENERIC_WRITE)
                                  : GENERIC_READ;
            if ((flags & O_APPEND) != 0) {
                access = FILE_APPEND_DATA;
            }

            uint creation;
            if ((flags & O_CREAT) != 0) {
                creation = ((flags & O_TRUNC) != 0) ? CREATE_ALWAYS : OPEN_ALWAYS;
            } else {
                creation = ((flags & O_TRUNC) != 0) ? CREATE_ALWAYS : OPEN_EXISTING;
            }

            return CreateFileW(path, access, FILE_SHARE_READ_WRITE_DELETE, IntPtr.Zero, creation,
                FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        }

        /// <summary>
        /// MRI opens a redirection's file in the parent before it starts the child, so a file that
        /// cannot be opened is what the error names. Handing back only -errno made the prelude name
        /// the program instead: `out: "C:/no/dir/x"` read as "No such file or directory - ruby.exe".
        /// </summary>
        private static Exception/*!*/ RedirectionError(int win32Error, string/*!*/ path) {
            switch (Errno(win32Error)) {
                case ENOENT: return new FileNotFoundException(path);
                case EACCES: return Directory.Exists(path) ? RubyExceptions.CreateEISDIR(path) : new UnauthorizedAccessException(path);
                default: return new InvalidError(path);
            }
        }

        // the nearest errno, as MRI's map_errno gives it
        private static int Errno(int win32Error) {
            switch (win32Error) {
                case ERROR_FILE_NOT_FOUND:
                case ERROR_PATH_NOT_FOUND:
                case ERROR_INVALID_NAME: return ENOENT;
                case ERROR_ACCESS_DENIED:
                case ERROR_SHARING_VIOLATION:
                case ERROR_LOCK_VIOLATION: return EACCES;
                case ERROR_BAD_EXE_FORMAT: return ENOEXEC;
                default: return EINVAL;
            }
        }

        #endregion

        #region __spawn__ / __exec__

        /// <summary>
        /// CreateProcess standing in for posix_spawn. Returns the child's pid, or -errno.
        /// </summary>
        internal static int Spawn(RubyContext/*!*/ context, MutableString/*!*/ file, RubyArray/*!*/ argv,
            RubyArray envp, RubyArray actions, object pgroup) {

            using (Redirections plan = Plan(context, actions)) {
                if (plan.Error != 0) {
                    return plan.Error;
                }

                IntPtr stdIn = Inheritable(plan.StdIn);
                IntPtr stdOut = Inheritable(plan.StdOut);
                IntPtr stdErr = Inheritable(plan.StdErr);
                IntPtr environment = BuildEnvironment(envp);
                try {
                    if (stdIn == IntPtr.Zero || stdOut == IntPtr.Zero || stdErr == IntPtr.Zero) {
                        return -EBADF;
                    }

                    var startup = new STARTUPINFO();
                    startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                    startup.dwFlags = STARTF_USESTDHANDLES;
                    startup.hStdInput = stdIn;
                    startup.hStdOutput = stdOut;
                    startup.hStdError = stdErr;

                    int flags = CREATE_UNICODE_ENVIRONMENT;
                    if (pgroup != null) {
                        // Process.spawn(pgroup: true) asks for a group of its own, which is the
                        // closest thing Windows has; a specific group id cannot be honoured.
                        flags |= CREATE_NEW_PROCESS_GROUP;
                    }

                    PROCESS_INFORMATION info;
                    var commandLine = new StringBuilder(BuildCommandLine(argv));
                    if (!CreateProcessW(file.ToString(), commandLine, IntPtr.Zero, IntPtr.Zero, true,
                            flags, environment, plan.WorkingDirectory, ref startup, out info)) {
                        return -Errno(Marshal.GetLastWin32Error());
                    }

                    CloseHandle(info.hThread);
                    Remember(info.dwProcessId, info.hProcess);
                    return info.dwProcessId;
                } finally {
                    if (stdIn != IntPtr.Zero) CloseHandle(stdIn);
                    if (stdOut != IntPtr.Zero) CloseHandle(stdOut);
                    if (stdErr != IntPtr.Zero) CloseHandle(stdErr);
                    if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                }
            }
        }

        /// <summary>
        /// Windows has no execve: MRI's own win32 Process.exec starts the child, waits for it
        /// and exits with its status, and so does this. Only returns when the child could not
        /// be started at all, as -errno.
        /// </summary>
        internal static int Exec(RubyContext/*!*/ context, MutableString/*!*/ file, RubyArray/*!*/ argv,
            RubyArray envp, RubyArray actions) {

            int pid = Spawn(context, file, argv, envp, actions, null);
            if (pid < 0) {
                return pid;
            }

            IntPtr handle;
            if (!Forget(pid, out handle)) {
                Environment.Exit(0);
            }
            WaitForSingleObject(handle, INFINITE);
            uint code;
            GetExitCodeProcess(handle, out code);
            CloseHandle(handle);
            Environment.Exit(unchecked((int)code));
            return 0;   // not reached
        }

        #endregion

        #region kill

        private const int SIGINT = 2;
        private const int SIGKILL = 9;
        private const int EPERM = 1;
        private const int ESRCH = 3;
        private const uint STILL_ACTIVE = 259;
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ERROR_INVALID_PARAMETER = 87;
        private const uint CTRL_C_EVENT = 0;
        private const uint CTRL_BREAK_EVENT = 1;

        /// <summary>
        /// kill(2) as MRI's win32 kill() does it, since there is no kill(2) to call - the libc one
        /// Process.kill used to P/Invoke is not there (DllNotFoundException). Returns 0, or -errno.
        /// KILL is TerminateProcess (with exit code 0, as in MRI), 0 asks whether the process still
        /// runs, INT is a console Ctrl+C - Ctrl+Break for a pid, which has to be a process group
        /// (spawn's pgroup: true). Windows has no other signals to send: EINVAL, as in MRI.
        /// </summary>
        internal static int Kill(int pid, int signal) {
            if (pid < 0 || (pid == 0 && signal != SIGINT)) {
                return -EINVAL;
            }

            switch (signal) {
                case SIGINT:
                    // a console's Ctrl+C cannot be aimed at one process group, so a pid gets Ctrl+Break
                    if (!GenerateConsoleCtrlEvent(pid != 0 ? CTRL_BREAK_EVENT : CTRL_C_EVENT, pid)) {
                        int error = Marshal.GetLastWin32Error();
                        return -(error == 0 ? EPERM : Errno(error));
                    }
                    return 0;

                case 0:
                case SIGKILL:
                    // A child's handle is the one __waitpid__ reaps and closes, so it is used with the
                    // table locked: otherwise another thread could close it between the lookup and the
                    // call, and a child started meanwhile could be handed the same handle value.
                    lock (_children) {
                        IntPtr child;
                        if (_children.TryGetValue(pid, out child)) {
                            return Signal(child, signal);
                        }
                    }

                    // signal 0 only asks, so it needs no right to end the process
                    uint access = PROCESS_QUERY_LIMITED_INFORMATION | (signal == SIGKILL ? PROCESS_TERMINATE : 0);
                    IntPtr handle = OpenProcess(access, false, pid);
                    if (handle == IntPtr.Zero) {
                        return (Marshal.GetLastWin32Error() == ERROR_INVALID_PARAMETER) ? -ESRCH : -EPERM;
                    }
                    try {
                        return Signal(handle, signal);
                    } finally {
                        CloseHandle(handle);
                    }

                default:
                    return -EINVAL;
            }
        }

        /// <summary>KILL or 0 to an open process handle: 0, or -errno.</summary>
        private static int Signal(IntPtr handle, int signal) {
            uint status;
            if (!GetExitCodeProcess(handle, out status)) {
                return -Errno(Marshal.GetLastWin32Error());
            }
            if (status != STILL_ACTIVE) {
                return -ESRCH;
            }
            if (signal == SIGKILL && !TerminateProcess(handle, 0)) {
                return -EPERM;
            }
            return 0;
        }

        #endregion

        #region __waitpid__

        private const int WNOHANG = 1;

        /// <summary>
        /// waitpid(2) over process handles. Returns [pid, status-word], null for a WNOHANG that
        /// found nothing, or -errno. The status word is the child's exit code in the same place
        /// the Unix one puts it, so Process::Status needs no second implementation.
        /// </summary>
        internal static object WaitPid(RubyContext/*!*/ context, int pid, int flags) {
            uint timeout = ((flags & WNOHANG) != 0) ? 0u : INFINITE;

            IntPtr handle;
            if (pid > 0) {
                if (!Forget(pid, out handle)) {
                    return ScriptingRuntimeHelpers.Int32ToObject(-ECHILD);
                }
                uint waited = WaitForSingleObject(handle, timeout);
                if (waited == WAIT_TIMEOUT) {
                    Remember(pid, handle);
                    return null;
                }
                return Reap(context, pid, handle);
            }

            // pid <= 0: any child. Process groups are not tracked, so a negative pid is taken
            // as "any", which is what it degrades to when there is only ever one group.
            var children = Children();
            if (children.Length == 0) {
                return ScriptingRuntimeHelpers.Int32ToObject(-ECHILD);
            }

            var handles = new IntPtr[children.Length];
            for (int i = 0; i < children.Length; i++) {
                handles[i] = children[i].Value;
            }
            uint index = WaitForMultipleObjects(handles.Length, handles, false, timeout);
            if (index == WAIT_TIMEOUT) {
                return null;
            }
            if (index == WAIT_FAILED || index >= (uint)handles.Length) {
                return ScriptingRuntimeHelpers.Int32ToObject(-ECHILD);
            }

            int found = children[index].Key;
            Forget(found, out handle);
            return Reap(context, found, handle);
        }

        private static object Reap(RubyContext/*!*/ context, int pid, IntPtr handle) {
            uint code;
            if (!GetExitCodeProcess(handle, out code)) {
                code = 0;
            }
            CloseHandle(handle);

            // The low byte of a Unix status word says which signal killed the child and the
            // next one up holds the exit code; a Windows child always exits normally.
            var status = new RubyProcess.Status(pid, unchecked((int)(code & 0xff)) << 8);
            context.ChildProcessExitStatus = status;
            return new RubyArray { ScriptingRuntimeHelpers.Int32ToObject(pid), status };
        }

        #endregion

        #region pipes

        /// <summary>
        /// CreatePipe, with each end wrapped in a FileStream. Neither end is inheritable: the
        /// one the child needs is duplicated inheritably at spawn time, and an inheritable
        /// copy of the other would keep the pipe from ever reaching end of file.
        /// </summary>
        internal static RubyArray/*!*/ OsPipe(RubyContext/*!*/ context) {
            var attributes = new SECURITY_ATTRIBUTES {
                nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
                lpSecurityDescriptor = IntPtr.Zero,
                bInheritHandle = 0,
            };

            IntPtr read, write;
            if (!CreatePipe(out read, out write, ref attributes, 0)) {
                throw RubyExceptions.CreateEINVAL("pipe");
            }
            SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0);
            SetHandleInformation(write, HANDLE_FLAG_INHERIT, 0);

            var reader = new FileStream(new SafeFileHandle(read, true), FileAccess.Read, 1, false);
            var writer = new FileStream(new SafeFileHandle(write, true), FileAccess.Write, 1, false);

            var reading = new RubyIO(context, reader, context.AllocateFileDescriptor(reader), IOMode.ReadOnly);
            var writing = new RubyIO(context, writer, context.AllocateFileDescriptor(writer), IOMode.WriteOnly);
            reading.SetEncodings(null, null);
            writing.SetEncodings(null, null);
            return new RubyArray { reading, writing };
        }

        /// <summary>close_on_exec is handle inheritance here, which is the same question.</summary>
        internal static object GetCloseOnExec(RubyContext/*!*/ context, int descriptor) {
            IntPtr handle = HandleOf(context, descriptor);
            int flags;
            if (handle == IntPtr.Zero || !GetHandleInformation(handle, out flags)) {
                return null;
            }
            return (flags & HANDLE_FLAG_INHERIT) == 0;
        }

        internal static object SetCloseOnExec(RubyContext/*!*/ context, int descriptor, bool value) {
            IntPtr handle = HandleOf(context, descriptor);
            if (handle == IntPtr.Zero) {
                return null;
            }
            return SetHandleInformation(handle, HANDLE_FLAG_INHERIT, value ? 0 : HANDLE_FLAG_INHERIT)
                ? (object)value : null;
        }

        #endregion

        #region backquotes

        /// <summary>Kernel#`: the child's standard output, read to the end.</summary>
        internal static MutableString/*!*/ CaptureOutput(RubyContext/*!*/ context, MutableString/*!*/ file,
            RubyArray/*!*/ argv, RubyArray envp, RubyArray actions) {

            var attributes = new SECURITY_ATTRIBUTES {
                nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
                lpSecurityDescriptor = IntPtr.Zero,
                bInheritHandle = 0,
            };

            IntPtr read, write;
            if (!CreatePipe(out read, out write, ref attributes, 0)) {
                throw RubyExceptions.CreateEINVAL("pipe");
            }

            int pid;
            using (Redirections plan = Plan(context, actions, write)) {
                if (plan.Error != 0) {
                    CloseHandle(read);
                    CloseHandle(write);
                    throw RubyExceptions.CreateENOENT(file.ToString());
                }

                IntPtr stdIn = Inheritable(plan.StdIn);
                IntPtr stdOut = Inheritable(plan.StdOut);
                IntPtr stdErr = Inheritable(plan.StdErr);
                IntPtr environment = BuildEnvironment(envp);
                try {
                    var startup = new STARTUPINFO();
                    startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                    startup.dwFlags = STARTF_USESTDHANDLES;
                    startup.hStdInput = stdIn;
                    startup.hStdOutput = stdOut;
                    startup.hStdError = stdErr;

                    PROCESS_INFORMATION info;
                    var commandLine = new StringBuilder(BuildCommandLine(argv));
                    if (!CreateProcessW(file.ToString(), commandLine, IntPtr.Zero, IntPtr.Zero, true,
                            CREATE_UNICODE_ENVIRONMENT, environment, plan.WorkingDirectory, ref startup, out info)) {
                        int error = Marshal.GetLastWin32Error();
                        CloseHandle(read);
                        CloseHandle(write);
                        throw (Errno(error) == EACCES)
                            ? RubyExceptions.CreateEACCES()
                            : RubyExceptions.CreateENOENT(file.ToString());
                    }
                    CloseHandle(info.hThread);
                    pid = info.dwProcessId;
                    Remember(pid, info.hProcess);
                } finally {
                    if (stdIn != IntPtr.Zero) CloseHandle(stdIn);
                    if (stdOut != IntPtr.Zero) CloseHandle(stdOut);
                    if (stdErr != IntPtr.Zero) CloseHandle(stdErr);
                    if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                }
            }

            // The child holds the only other copy of the write end now; if this one stayed
            // open the read below would never see end of file.
            CloseHandle(write);

            var output = new List<byte>();
            using (var stream = new FileStream(new SafeFileHandle(read, true), FileAccess.Read, 1, false)) {
                byte[] buffer = new byte[8192];
                while (true) {
                    int count;
                    try {
                        count = stream.Read(buffer, 0, buffer.Length);
                    } catch (IOException) {
                        break;      // the child exited without closing its end
                    }
                    if (count <= 0) {
                        break;
                    }
                    for (int i = 0; i < count; i++) {
                        output.Add(buffer[i]);
                    }
                }
            }

            WaitPid(context, pid, 0);

            // MRI reads a backquote's pipe in text mode on Windows, so what the child wrote as
            // CRLF comes back as LF - which is what every spec that compares `` output against
            // a "...\n" literal expects. IO.popen goes through a StreamReader and already does
            // this; doing it here keeps the two spellings of "run a child" consistent.
            return MutableString.CreateBinary(StripCarriageReturns(output), context.GetPathEncoding());
        }

        /// <summary>Every CR that precedes an LF, dropped - text-mode reading in one pass.</summary>
        private static byte[]/*!*/ StripCarriageReturns(List<byte>/*!*/ bytes) {
            var result = new byte[bytes.Count];
            int length = 0;
            for (int i = 0; i < bytes.Count; i++) {
                if (bytes[i] == (byte)'\r' && i + 1 < bytes.Count && bytes[i + 1] == (byte)'\n') {
                    continue;
                }
                result[length++] = bytes[i];
            }
            Array.Resize(ref result, length);
            return result;
        }

        #endregion
    }
}
#endif

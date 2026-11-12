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
using System.Diagnostics;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using IronRuby.Runtime;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace IronRuby.Builtins {

    /// <summary>
    /// Process builtin module
    /// </summary>
    [RubyModule("Process")]
    public static partial class RubyProcess {
        #region Utils

        internal static Process/*!*/ CreateProcess(RubyContext/*!*/ context, MutableString/*!*/ command, MutableString[]/*!*/ args) {
            var psi = new ProcessStartInfo(command.ToString(), JoinArguments(args).ToString());
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            try {
                Utils.Log(String.Format("Starting: '{0}' with args: '{1}'", psi.FileName, psi.Arguments), "PROCESS");
                Process p = Process.Start(psi);
                using (RubyUtils.BlockingWait.Enter()) {
                    p.WaitForExit();
                }
                context.ChildProcessExitStatus = new RubyProcess.Status(p);
                return p;
            } catch (Exception e) {
                throw RubyExceptions.CreateENOENT(psi.FileName, e);
            }
        }

        internal static Process/*!*/ CreateProcess(RubyContext/*!*/ context, MutableString/*!*/ command, bool redirectOutput) {
            return CreateProcess(context, command, false, redirectOutput, false);
        }

        private static bool IsUnixPlatform {
            get { return System.IO.Path.DirectorySeparatorChar == '/'; }
        }

        internal static Process/*!*/ CreateProcess(RubyContext/*!*/ context, MutableString/*!*/ command,
            bool redirectInput, bool redirectOutput, bool redirectErrorOutput) {

            var p = new Process();

            if (IsUnixPlatform) {
                // Pass the shell command as a single verbatim argv entry: assigning
                // StartInfo.Arguments would re-parse (and mangle) its quoting.
                Utils.Log(String.Format("Starting: '/bin/sh -c' with command: '{0}'", command), "PROCESS");
                p.StartInfo.FileName = "/bin/sh";
                p.StartInfo.ArgumentList.Add("-c");
                p.StartInfo.ArgumentList.Add(command.ToString());
            } else {
                string fileName, arguments;
                RubyProcess.GetExecutable(context.DomainManager.Platform, command.ToString(), out fileName, out arguments);
                Utils.Log(String.Format("Starting: '{0}' with args: '{1}'", fileName, arguments), "PROCESS");
                p.StartInfo.FileName = fileName;
                p.StartInfo.Arguments = arguments;
            }

            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardInput = redirectInput;
            p.StartInfo.RedirectStandardOutput = redirectOutput;
            p.StartInfo.RedirectStandardError = redirectErrorOutput;
            try {
                p.Start();
            } catch (Exception e) {
                throw RubyExceptions.CreateENOENT(p.StartInfo.FileName, e);
            }

            context.ChildProcessExitStatus = new RubyProcess.Status(p);
            return p;
        }

        private static void GetExecutable(PlatformAdaptationLayer/*!*/ pal, string/*!*/ command, out string executable, out string arguments) {
            command = command.Trim(' ');
            if (command.Length == 0) {
                throw RubyExceptions.CreateEINVAL(command);
            }

            // This seems to be quite complicated:
            // 1) If the first part of the command is a shell command (DIR, ECHO, ...) or
            //    if the command contains unquoted <, > or | then
            //    it uses ENV['COMSPEC'] to execute the command: %COMSPEC% /c "COMMAND"
            // 2) It looks for the shortest prefix of command that is separated by space from the rest of the command that is either
            //      a) An absolute path to an executable file.
            //      b) Try prepend paths from ENV['PATH']
            //      c) Try Environment.SpecialFolder.System.
            //      d) Try SHGetFolderPath(CSIDL_WINDOWS) - we can't get this from Environment.SpecialFolder, so we need to use 
            //         ENV["SystemRoot"] environment variable.
            //    In each step it tries to append ".exe" or ".com" extension if the path doesn't exist.
            // 
            //    For example, if the command is `x/a b/x` and the directory structure is
            //      x\a b\x.exe
            //      x\a.exe
            //    it executes a.exe.
            //    
            //    MRI probably calls CreateProcess Win32 API with lpApplicationName it resolves as described above and 
            //    lpCommandLine == command. System.Diagnostics.Process also uses this API with lpApplicationName == NULL and 
            //    lpCommandLine == '"{ProcessStartInfo.FileName}" {ProcessStartInfo.Arguments}'.
            //
            //    Although CreateProcess does all the searching for an executable if passed no lpApplicationName, 
            //    we need to do it ourselves because it is slightly different in MRI (is that a bug?) and also because System.Diagnostics.Process 
            //    quotes the FileName :(
            //    

            string comspec = RubyEnvironment.GetVariable(pal, "COMSPEC");
            if (!pal.FileExists(comspec)) {
                comspec = null;
            }

            if (comspec != null && IndexOfUnquotedSpecialCharacter(command) >= 0) {
                executable = comspec;
                arguments = "/c \"" + command + "\"";
                return;
            }

            int start = 0;
            while (true) {
                int next = command.IndexOf(' ', start);

                executable = (next >= 0) ? command.Substring(0, next) : command;
                arguments = (next >= 0) ? command.Substring(next + 1) : "";

                if (start == 0 && comspec != null && IsShellCommand(executable)) {
                    executable = comspec;
                    arguments = "/c \"" + command + "\"";
                    return;
                }

                try {
                    foreach (var path in GetExecutableFiles(pal, executable)) {
                        if (pal.FileExists(path)) {
                            // We need to set the path we found as executable. Althought makes command line of the target process
                            // different from when called by MRI it will execute the right process. If we passed the original executable name
                            // CreateProcess might resolve it to a different executable.
                            executable = path;
                            return;
                        }
                    }
                } catch (Exception e) {
                    if (next < 0) {
                        throw RubyExceptions.CreateENOENT(command, e);
                    }
                }

                if (next < 0) {
                    throw RubyExceptions.CreateENOENT(command);
                }

                start = next + 1;
                while (start < command.Length && command[start] == ' ') {
                    start++;
                }
            }
        }

        private static int IndexOfUnquotedSpecialCharacter(string/*!*/ str) {
            bool inDoubleQuote = false;
            bool inSingleQuote = false;
            for (int i = 0; i < str.Length; i++) {
                char c = str[i];
                if (c == '"') {
                    inDoubleQuote = !inDoubleQuote;
                } else if (c == '\'') {
                    inSingleQuote = !inSingleQuote;
                } else if (c == '>' || c == '<' || c == '|') {
                    if (!inSingleQuote && !inDoubleQuote) {
                        return i;
                    }
                }
            }
            return -1;
        }

        private static string[] _ExecutableExtensions = new[] { ".exe", ".com", ".bat", ".cmd" };

        private static IEnumerable<string>/*!*/ GetExecutableFiles(PlatformAdaptationLayer/*!*/ pal, string/*!*/ path) {
            if (path[0] == '"' || path[path.Length - 1] == '"') {
                if (path.Length >= 3 && path[0] == '"' && path[path.Length - 1] == '"') {
                    path = path.Substring(1, path.Length - 2);
                } else {
                    yield break;
                }
            }

            string extension = RubyUtils.GetExtension(path);
            bool hasExtension = !String.IsNullOrEmpty(extension);
            bool isExecutable = hasExtension && Array.IndexOf(_ExecutableExtensions, extension.ToLowerInvariant()) >= 0;

            if (!hasExtension || isExecutable) {
                foreach (var fullPath in GetAbsolutePaths(pal, path)) {
                    if (hasExtension) {
                        yield return fullPath;
                    } else {
                        foreach (var ext in _ExecutableExtensions) {
                            yield return fullPath + ext;
                        }
                    }
                }
            }
        }

        private static IEnumerable<string>/*!*/ GetAbsolutePaths(PlatformAdaptationLayer/*!*/ pal, string/*!*/ path) {
            if (pal.IsAbsolutePath(path)) {
                yield return path;
            } else {
                yield return pal.GetFullPath(path);

                string var = RubyEnvironment.GetVariable(pal, "PATH");
                if (!String.IsNullOrEmpty(var)) {
                    foreach (var prefix in var.Split(Path.PathSeparator)) {
                        if (prefix.Length > 0) {
                            yield return Path.Combine(prefix, path);
                        }
                    }
                }

                var = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (!String.IsNullOrEmpty(var)) {
                    yield return Path.Combine(var, path);
                }

                var = RubyEnvironment.GetVariable(pal, "SystemRoot");
                if (!String.IsNullOrEmpty(var)) {
                    yield return Path.Combine(var, path);
                }
            }
        }

        private static bool IsShellCommand(string/*!*/ str) {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT &&
                Environment.OSVersion.Platform != PlatformID.Win32Windows) {
                return false;
            }

            switch (str.ToUpperInvariant()) {
                case "ASSOC":
                case "BREAK":
                case "CALL":
                case "CD":
                case "CHDIR":
                case "CLS":
                case "COLOR":
                case "COPY":
                case "DATE":
                case "DEL":
                case "DIR":
                case "ECHO":
                case "ENDLOCAL":
                case "ERASE":
                case "EXIT":
                case "FOR":
                case "FTYPE":
                case "GOTO":
                case "IF":
                case "MD":
                case "MKDIR":
                case "MOVE":
                case "PATH":
                case "PAUSE":
                case "POPD":
                case "PROMPT":
                case "PUSHD":
                case "RD":
                case "REM":
                case "REN":
                case "RENAME":
                case "RMDIR":
                case "SET":
                case "SETLOCAL":
                case "SHIFT":
                case "START":
                case "TIME":
                case "TITLE":
                case "TYPE":
                case "VER":
                case "VERIFY":
                case "VOL":
                    return true;

                case "MKLINK":
                    return Environment.OSVersion.Version.Major >= 6; // Vista and later
            };
            return false;
        }

        private static MutableString/*!*/ JoinArguments(MutableString/*!*/[]/*!*/ args) {
            MutableString result = MutableString.CreateMutable(RubyEncoding.Binary);

            for (int i = 0; i < args.Length; i++) {
                result.Append(args[i]);
                if (args.Length > 1 && i < args.Length - 1) {
                    result.Append(' ');
                }
            }

            return result;
        }

        #endregion

        #region Status

        /// <summary>
        /// What wait(2) reported about a child: the pid plus the raw status word. Every
        /// predicate MRI documents is a bit pattern in that word, so keeping the word is what
        /// lets us distinguish "exited with 137" from "killed by signal 9" - a distinction
        /// System.Diagnostics.Process throws away when it folds a signal into ExitCode.
        /// </summary>
        [RubyClass("Status")]
        [HideMethod("new", IsStatic = true)]
        public sealed class Status {
            private readonly int _pid;
            private readonly int _status;

            internal Status(int pid, int status) {
                _pid = pid;
                _status = status;
            }

            internal Status(Process/*!*/ process)
                : this(process.Id, process.HasExited ? (process.ExitCode & 0xff) << 8 : 0) {
            }

            private bool IsExited {
                get { return (_status & 0x7f) == 0; }
            }

            private bool IsSignaled {
                // WIFSIGNALED: a termination signal in the low seven bits, and not the 0x7f
                // that marks a stop.
                get { return ((sbyte)(((_status & 0x7f) + 1) >> 1)) > 0; }
            }

            private bool IsStopped {
                get { return (_status & 0xff) == 0x7f; }
            }

            [RubyMethod("coredump?")]
            public static bool CoreDump(Status/*!*/ self) {
                return (self._status & 0x80) != 0;
            }

            [RubyMethod("exitstatus")]
            public static object ExitStatus(Status/*!*/ self) {
                return self.IsExited ? ScriptingRuntimeHelpers.Int32ToObject((self._status >> 8) & 0xff) : null;
            }

            [RubyMethod("exited?")]
            public static bool Exited(Status/*!*/ self) {
                return self.IsExited;
            }

            [RubyMethod("pid")]
            public static int Pid(Status/*!*/ self) {
                return self._pid;
            }

            [RubyMethod("stopped?")]
            public static bool Stopped(Status/*!*/ self) {
                return self.IsStopped;
            }

            [RubyMethod("stopsig")]
            public static object StopSig(Status/*!*/ self) {
                return self.IsStopped ? ScriptingRuntimeHelpers.Int32ToObject((self._status >> 8) & 0xff) : null;
            }

            [RubyMethod("signaled?")]
            public static bool Signaled(Status/*!*/ self) {
                return self.IsSignaled;
            }

            [RubyMethod("termsig")]
            public static object TermSig(Status/*!*/ self) {
                return self.IsSignaled ? ScriptingRuntimeHelpers.Int32ToObject(self._status & 0x7f) : null;
            }

            /// <summary>
            /// true when the child exited with 0, false when it exited with anything else, and
            /// nil when it did not exit at all - a killed child neither succeeded nor failed.
            /// </summary>
            [RubyMethod("success?")]
            public static object Success(Status/*!*/ self) {
                if (!self.IsExited) {
                    return null;
                }
                return ScriptingRuntimeHelpers.BooleanToObject(((self._status >> 8) & 0xff) == 0);
            }

            [RubyMethod("to_i")]
            public static int ToInt(Status/*!*/ self) {
                return self._status;
            }

            // MRI's pst_equal compares #to_i only: two processes that exited alike are equal
            [RubyMethod("==")]
            public static bool Equals(Status/*!*/ self, object other) {
                var status = other as Status;
                if (status != null) {
                    return self._status == status._status;
                }
                return (other is int) && (int)other == self._status;
            }

            private static string/*!*/ Describe(Status/*!*/ self) {
                if (self.IsStopped) {
                    int signal = (self._status >> 8) & 0xff;
                    string name = PosixSignals.ToName(signal);
                    return String.Format(CultureInfo.InvariantCulture, "stopped {0} (signal {1})",
                        (name != null) ? "SIG" + name : signal.ToString(CultureInfo.InvariantCulture), signal);
                }
                if (self.IsSignaled) {
                    int signal = self._status & 0x7f;
                    string name = PosixSignals.ToName(signal);
                    return String.Format(CultureInfo.InvariantCulture, "{0} (signal {1}){2}",
                        (name != null) ? "SIG" + name : signal.ToString(CultureInfo.InvariantCulture), signal,
                        CoreDump(self) ? " (core dumped)" : "");
                }
                return String.Format(CultureInfo.InvariantCulture, "exit {0}", (self._status >> 8) & 0xff);
            }

            [RubyMethod("to_s")]
            public static MutableString/*!*/ ToStr(Status/*!*/ self) {
                return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture, "pid {0} {1}",
                    self._pid, Describe(self)));
            }

            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(Status/*!*/ self) {
                return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture, "#<Process::Status: pid {0} {1}>",
                    self._pid, Describe(self)));
            }
        }

        #endregion

        // abort
        // detach

        [RubyMethod("egid", RubyMethodAttributes.PublicSingleton)]
        public static int EffectiveGroupId(RubyModule/*!*/ self) {
            return Posix.GetEGid();
        }

        // egid=

        [RubyMethod("euid", RubyMethodAttributes.PublicSingleton)]
        public static int EffectiveUserId(RubyModule/*!*/ self) {
            return Posix.GetEUid();
        }

        // euid=
        // exit
        // exit!
        // fork
        // getpgid
        // getpriority
        // getrlimit

        [RubyMethod("gid", RubyMethodAttributes.PublicSingleton)]
        public static int GroupId(RubyModule/*!*/ self) {
            return Posix.GetGid();
        }

        // gid=

        [RubyMethod("groups", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ Groups(RubyModule/*!*/ self) {
            var groups = Posix.GetGroups();
            var result = new RubyArray(groups.Length);
            foreach (int gid in groups) {
                result.Add(ScriptingRuntimeHelpers.Int32ToObject(gid));
            }
            return result;
        }

        // groups=
        // initgroups

        [RubyMethod("kill", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("kill", RubyMethodAttributes.PublicSingleton)]
        public static object Kill(ConversionStorage<int>/*!*/ toInt, RubyModule/*!*/ self, object signalId, [NotNull]params object[]/*!*/ pids) {
            int signal = PosixSignals.ToNumber(self.Context, signalId);

            // A negative signal - -15, "-TERM", :"-SIGTERM" - is Ruby's way of saying "send it
            // to the process group of each pid", which is kill(2)'s negative pid.
            bool toProcessGroup = signal < 0;
            if (toProcessGroup) {
                signal = -signal;
            }
            if (PosixSignals.IsUnsupportedOnWindows(signal)) {
                // Negative means -errno; the prelude's Process.kill turns it into an Errno class.
                return ScriptingRuntimeHelpers.Int32ToObject(-PosixSignals.EINVAL);
            }

            foreach (var pid in pids) {
                int target = Protocols.CastToFixnum(toInt, pid);
                if (toProcessGroup && target > 0) {
                    target = -target;
                }

                // Signalling yourself with something that would end the process, and no handler to
                // catch it, is a SignalException in MRI - the program gets to rescue it. Letting
                // the real signal through would just kill us. This only stands in for *this*
                // process, though: a process group holds other processes that are entitled to the
                // signal, and standing in for them meant that a child asked to signal its group
                // quietly died instead, leaving whoever was waiting for the signal waiting for
                // ever.
                bool targetsUs = target == Environment.ProcessId;

                // trap(:TERM, "DEFAULT") after a handler is MRI's default again - a SignalException -
                // and "SYSTEM_DEFAULT" is the OS's, which the real kill(2) below gives it.
                bool standIn = targetsUs && !PosixSignals.HasRubyHandler(signal) && !PosixSignals.IsTrappedToSystemDefault(signal);

                if (standIn && PosixSignals.TerminatesByDefault(signal)) {
                    // SIGINT is the one MRI raises as Interrupt - what `rescue Interrupt` is
                    // written for - and with an empty message, not "SIGINT"; #signo still says 2.
                    if (signal == PosixSignals.SignalInterrupt) {
                        throw new Interrupt(String.Empty);
                    }
                    string name = PosixSignals.ToName(signal);
                    throw new SignalException((name != null) ? "SIG" + name : "SIG" + signal);
                }

                // PIPE, SYS and VTALRM: nothing happens, as in MRI, unless a #trap installed something.
                if (standIn && PosixSignals.IgnoredByDefault(signal)) {
                    continue;
                }

                // MRI checks for pending signals as soon as kill(2) returns, so signalling yourself
                // from the main thread runs the handler before Process.kill does - and with
                // Process.kill still on the stack, which ruby/spec looks for in the handler's
                // #caller. Going through the kernel would run it on .NET's signal thread some time
                // later instead, so call it here and skip the round trip.
                if (targetsUs && signal > 0 && PosixSignals.HasRubyHandler(signal) && PosixSignals.IsMainThread) {
                    PosixSignals.RunHandler(signal);
                    continue;
                }

                int error = PosixSignals.Kill(target, signal);
                if (error < 0) {
                    // Negative means -errno; the prelude's Process.kill turns it into an Errno class.
                    return ScriptingRuntimeHelpers.Int32ToObject(error);
                }
            }
            return ScriptingRuntimeHelpers.Int32ToObject(pids.Length);
        }

        // maxgroups
        // maxgroups=

        [RubyMethod("pid", RubyMethodAttributes.PublicSingleton)]
        public static int GetPid(RubyModule/*!*/ self) {
            return OperatingSystem.IsBrowser() ? Environment.ProcessId : Process.GetCurrentProcess().Id;
        }

        [RubyMethod("setproctitle", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ SetProcessTitle(RubyContext/*!*/ context, RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ title) {
            context.SetNativeProcessTitle(title);
            return title;
        }

        [RubyMethod("ppid", RubyMethodAttributes.PublicSingleton)]
        public static int GetParentPid(RubyModule/*!*/ self) {
            // a warm-start worker's own parent is the broker, not what started the program
            int reported = RubyContext.ReportedParentProcessId;
            return (reported > 0) ? reported : SysGetPpid();
        }

        // setpgid
        // setpgrp
        // setpriority
        // setrlimit
        // setsid

        private static readonly DateTime _StartTime = DateTime.UtcNow;

        [RubyMethod("times", RubyMethodAttributes.PublicSingleton)]
        public static RubyStruct/*!*/ GetTimes(RubyModule/*!*/ self) {
            var result = RubyStruct.Create(GetTmsClass(self.Context));
            if (OperatingSystem.IsBrowser()) {
                // no process accounting in the browser: report the time since start as user time
                result[0] = (DateTime.UtcNow - _StartTime).TotalSeconds;
                result[1] = 0.0;
            } else using (var process = Process.GetCurrentProcess()) {
                result[0] = process.UserProcessorTime.TotalSeconds;
                result[1] = process.PrivilegedProcessorTime.TotalSeconds;
            }
            result[2] = 0.0;
            result[3] = 0.0;
            return result;
        }

        private static readonly object TmsClassKey = new object();

        // Process::Tms, the struct Process.times returns. (It was Struct::Tms until Ruby 1.9.)
        [RubyConstant("Tms")]
        internal static RubyClass/*!*/ CreateTmsClass(RubyModule/*!*/ module) {
            return GetTmsClass(module.Context);
        }

        // Process.times can run before Process's constants are initialized, so the class is kept on the context.
        private static RubyClass/*!*/ GetTmsClass(RubyContext/*!*/ context) {
            return (RubyClass)context.GetOrCreateLibraryData(TmsClassKey, () => {
                var tms = RubyStruct.DefineStruct(context.GetClass(typeof(RubyStruct)), null, new[] { "utime", "stime", "cutime", "cstime" });
                tms.SetName("Process::Tms", true);
                return tms;
            });
        }

        [RubyMethod("uid", RubyMethodAttributes.PublicSingleton)]
        public static int UserId(RubyModule/*!*/ self) {
            return Posix.GetUid();
        }

        [RubyMethod("uid=", RubyMethodAttributes.PublicSingleton)]
        public static void SetUserId(RubyModule/*!*/ self, object temp) {
            throw new NotImplementedError("uid=() function is unimplemented on this machine");
        }

        // These three are what Process.wait/wait2/waitall were before there was any way to start a
        // child without waiting for it. The prelude redefines all three on top of __waitpid__
        // below; they stay registered so that an embedder that skips the prelude still has them.
        [RubyMethod("wait", RubyMethodAttributes.PublicSingleton)]
        public static void Wait(RubyModule/*!*/ self) {
            throw new Errno.ChildError();
        }

        [RubyMethod("wait2", RubyMethodAttributes.PublicSingleton)]
        public static void Wait2(RubyModule/*!*/ self) {
            throw new Errno.ChildError();
        }

        [RubyMethod("waitall", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray Waitall(RubyModule/*!*/ self) {
            return new RubyArray();
        }

        #region clock_gettime

        // The clock ids, as Integers with Linux's numbers, which is what MRI has.
        [RubyConstant] public const int CLOCK_REALTIME = 0;
        [RubyConstant] public const int CLOCK_MONOTONIC = 1;
        [RubyConstant] public const int CLOCK_PROCESS_CPUTIME_ID = 2;
        [RubyConstant] public const int CLOCK_THREAD_CPUTIME_ID = 3;
        [RubyConstant] public const int CLOCK_MONOTONIC_RAW = 4;
        [RubyConstant] public const int CLOCK_REALTIME_COARSE = 5;
        [RubyConstant] public const int CLOCK_MONOTONIC_COARSE = 6;
        [RubyConstant] public const int CLOCK_BOOTTIME = 7;
        [RubyConstant] public const int CLOCK_REALTIME_ALARM = 8;
        [RubyConstant] public const int CLOCK_BOOTTIME_ALARM = 9;
        [RubyConstant] public const int CLOCK_TAI = 11;

        private enum ClockKind { Realtime, Monotonic, ProcessTime, ThreadTime }

        private static readonly Dictionary<string, ClockKind> _emulatedClocks = new Dictionary<string, ClockKind> {
            // MRI's emulated clocks, named by Symbols
            { "GETTIMEOFDAY_BASED_CLOCK_REALTIME", ClockKind.Realtime },
            { "TIME_BASED_CLOCK_REALTIME", ClockKind.Realtime },
            { "GETRUSAGE_BASED_CLOCK_PROCESS_CPUTIME_ID", ClockKind.ProcessTime },
            { "TIMES_BASED_CLOCK_PROCESS_CPUTIME_ID", ClockKind.ProcessTime },
            { "CLOCK_BASED_CLOCK_PROCESS_CPUTIME_ID", ClockKind.ProcessTime },
            { "TIMES_BASED_CLOCK_MONOTONIC", ClockKind.Monotonic },
        };

        /// <summary>
        /// Process.clock_gettime(clock_id, unit = :float_second). ActiveSupport::Notifications reads
        /// the monotonic clock twice for every event it instruments, so this is C#. .NET's Stopwatch
        /// is the monotonic (and boot-time) source; CLOCK_PROCESS_CPUTIME_ID is the process's processor
        /// time, and CLOCK_THREAD_CPUTIME_ID the calling thread's, which .NET does not expose: it is read
        /// from the OS (<see cref="TryGetThreadCpuTime"/>). A clock that is not one of these is
        /// Errno::EINVAL, as clock_gettime(2) has it.
        /// </summary>
        [RubyMethod("clock_gettime", RubyMethodAttributes.PublicSingleton)]
        public static object ClockGetTime(ConversionStorage<int>/*!*/ fixnumCast, ConversionStorage<MutableString>/*!*/ tosConversion,
            RubyModule/*!*/ self, object clockId, [Optional]object unit) {

            var context = self.Context;
            ClockKind kind;
            var symbol = clockId as RubySymbol;
            if (symbol != null) {
                if (!_emulatedClocks.TryGetValue(symbol.ToString(), out kind)) {
                    throw RubyExceptions.CreateEINVAL("clock_gettime({0})", context.Inspect(clockId));
                }
            } else if (clockId == null) {
                // NUM2CLOCKID's wording, not to_int's
                throw RubyExceptions.CreateTypeError("no implicit conversion from nil to integer");
            } else {
                switch (Protocols.CastToFixnum(fixnumCast, clockId)) {
                    case CLOCK_REALTIME: case CLOCK_REALTIME_COARSE: case CLOCK_REALTIME_ALARM: case CLOCK_TAI:
                        kind = ClockKind.Realtime;
                        break;
                    case CLOCK_MONOTONIC: case CLOCK_MONOTONIC_RAW: case CLOCK_MONOTONIC_COARSE: case CLOCK_BOOTTIME: case CLOCK_BOOTTIME_ALARM:
                        kind = ClockKind.Monotonic;
                        break;
                    case CLOCK_PROCESS_CPUTIME_ID:
                        kind = ClockKind.ProcessTime;
                        break;
                    case CLOCK_THREAD_CPUTIME_ID:
                        kind = ClockKind.ThreadTime;
                        break;
                    default:
                        throw RubyExceptions.CreateEINVAL("clock_gettime({0})", context.Inspect(clockId));
                }
            }

            double seconds;
            if (kind == ClockKind.ThreadTime) {
                if (TryGetThreadCpuTime(out long nanoseconds)) {
                    return ConvertClockTime(tosConversion, nanoseconds, unit);
                }
                // no per-thread figure on this platform: the process's, as before
                kind = ClockKind.ProcessTime;
            }
            switch (kind) {
                case ClockKind.Realtime:
                    seconds = (double)(DateTime.UtcNow.Ticks - RubyTime.Epoch.Ticks) / TimeSpan.TicksPerSecond;
                    break;
                case ClockKind.ProcessTime:
                    seconds = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
                    break;
                default:
                    seconds = (double)Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                    break;
            }

            string unitName = (unit == Missing.Value || unit == null) ? "float_second" : (unit as RubySymbol)?.ToString();
            switch (unitName) {
                case "float_second": return seconds;
                case "float_millisecond": return seconds * 1000.0;
                case "float_microsecond": return seconds * 1000000.0;
                case "second": return ClrFloat.ToInt(seconds);
                case "millisecond": return ClrFloat.ToInt(seconds * 1000);
                case "microsecond": return ClrFloat.ToInt(seconds * 1000000);
                case "nanosecond": return ClrFloat.ToInt(seconds * 1000000000);
                default:
                    throw RubyExceptions.CreateArgumentError("unexpected unit: " + Protocols.ConvertToString(tosConversion, unit).ToString());
            }
        }

        // a clock read exactly, in nanoseconds: the Integer units are exact, as clock_gettime's timespec makes them in MRI
        private static object ConvertClockTime(ConversionStorage<MutableString>/*!*/ tosConversion, long nanoseconds, object unit) {
            string unitName = (unit == Missing.Value || unit == null) ? "float_second" : (unit as RubySymbol)?.ToString();
            switch (unitName) {
                case "float_second": return nanoseconds / 1e9;
                case "float_millisecond": return nanoseconds / 1e6;
                case "float_microsecond": return nanoseconds / 1e3;
                case "second": return Protocols.Normalize(nanoseconds / 1000000000);
                case "millisecond": return Protocols.Normalize(nanoseconds / 1000000);
                case "microsecond": return Protocols.Normalize(nanoseconds / 1000);
                case "nanosecond": return Protocols.Normalize(nanoseconds);
                default:
                    throw RubyExceptions.CreateArgumentError("unexpected unit: " + Protocols.ConvertToString(tosConversion, unit).ToString());
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec {
            public IntPtr Seconds;      // time_t
            public IntPtr Nanoseconds;  // long
        }

        [DllImport("libc", EntryPoint = "clock_gettime")]
        private static extern int clock_gettime(int clockId, out Timespec time);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetThreadTimes(IntPtr thread, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

        // set once a platform call has failed, so that the next read does not try it again
        private static bool _threadCpuTimeUnavailable;

        /// <summary>
        /// The CPU time the calling thread has used, in nanoseconds: clock_gettime(CLOCK_THREAD_CPUTIME_ID) on Unix, whose
        /// id is 3 on Linux, 16 on macOS and 14 on FreeBSD, and GetThreadTimes' kernel and user time (in 100 ns units) on
        /// Windows. A Ruby Thread is a CLR thread, which is an OS thread, so this is the Ruby Thread's CPU time as in MRI.
        /// One difference remains: a Fiber runs on a worker thread of its own here (ThreadOps.Fiber.cs), while MRI runs it on
        /// the thread that resumes it, so the CPU time a Fiber (or Enumerator#next) spends counts toward neither that thread's
        /// clock nor, read inside the Fiber, the resuming thread's. Charging it back would cost two clock reads per switch.
        /// </summary>
        private static bool TryGetThreadCpuTime(out long nanoseconds) {
            nanoseconds = 0;
            if (_threadCpuTimeUnavailable) {
                return false;
            }
            try {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    if (GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user)) {
                        nanoseconds = (kernel + user) * 100;
                        return true;
                    }
                } else {
                    int clockId = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 16 :
                        RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) ? 14 : CLOCK_THREAD_CPUTIME_ID;
                    if (clock_gettime(clockId, out Timespec time) == 0) {
                        nanoseconds = (long)time.Seconds * 1000000000 + (long)time.Nanoseconds;
                        return true;
                    }
                }
            } catch (DllNotFoundException) {
            } catch (EntryPointNotFoundException) {
            }
            _threadCpuTimeUnavailable = true;
            return false;
        }

        #endregion
    }
}

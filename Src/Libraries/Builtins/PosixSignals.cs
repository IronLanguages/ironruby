/* ****************************************************************************
 *
 * Signal numbers, name parsing and the process-wide handler table shared by
 * Signal.trap and Process.kill.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// Signals were stubbed out in IronRuby: Signal.trap quietly did nothing except for "INT" and
    /// Process.kill raised NotImplementedError. That left ruby/spec waiting forever for a child
    /// process that only a signal can end, so both are implemented here on top of libc's kill(2)
    /// and .NET's PosixSignalRegistration.
    /// </summary>
    internal static class PosixSignals {
        // Linux signal numbers. They are what Signal.list has always reported and what the
        // platform we run ruby/spec on uses.
        private static readonly Dictionary<string, int> _posixNumbers = new Dictionary<string, int>(StringComparer.Ordinal) {
            { "HUP", 1 }, { "INT", 2 }, { "QUIT", 3 }, { "ILL", 4 }, { "TRAP", 5 },
            { "ABRT", 6 }, { "IOT", 6 }, { "BUS", 7 }, { "FPE", 8 }, { "KILL", 9 },
            { "USR1", 10 }, { "SEGV", 11 }, { "USR2", 12 }, { "PIPE", 13 }, { "ALRM", 14 },
            { "TERM", 15 }, { "CHLD", 17 }, { "CLD", 17 }, { "CONT", 18 },
            { "STOP", 19 }, { "TSTP", 20 }, { "TTIN", 21 }, { "TTOU", 22 }, { "URG", 23 },
            { "XCPU", 24 }, { "XFSZ", 25 }, { "VTALRM", 26 }, { "PROF", 27 }, { "WINCH", 28 },
            { "IO", 29 }, { "POLL", 29 }, { "PWR", 30 }, { "SYS", 31 },
        };

        // MRI on Windows knows only the C runtime's signals, by the C runtime's numbers - ABRT is
        // 22 there. Listing HUP or USR1 told code that checks Signal.list.key?("HUP") before
        // trapping it that it could, where MRI says "unsupported signal 'SIGHUP'".
        private static readonly Dictionary<string, int> _windowsNumbers = new Dictionary<string, int>(StringComparer.Ordinal) {
            { "INT", 2 }, { "ILL", 4 }, { "ABRT", 22 }, { "FPE", 8 }, { "KILL", 9 }, { "SEGV", 11 }, { "TERM", 15 },
        };

        private static readonly Dictionary<string, int> _numbers = RubyProcess.IsWindows ? _windowsNumbers : _posixNumbers;

        internal static IEnumerable<KeyValuePair<string, int>> Numbers {
            get { return _numbers; }
        }

        /// <summary>
        /// "TERM", "SIGTERM", :SIGTERM and 15 all name signal 15. EXIT (0) is accepted as a name
        /// the way MRI accepts it, and anything else is an ArgumentError - deliberately without
        /// asking the object for #to_int, which ruby/spec checks for.
        /// </summary>
        internal static int ToNumber(RubyContext context, object signalId) {
            return ToNumber(context, signalId, false);
        }

        /// <summary>
        /// <paramref name="forTrap"/> applies the two extra rules #trap has and Process.kill
        /// does not: a name may not be negated, and the number must name a real signal.
        /// </summary>
        internal static int ToNumber(RubyContext context, object signalId, bool forTrap) {
            if (signalId is int) {
                int given = (int)signalId;
                if (forTrap && (given < 0 || ToName(given) == null)) {
                    throw RubyExceptions.CreateArgumentError("invalid signal number ({0})", given);
                }
                return given;
            }

            string name = null;
            var str = signalId as MutableString;
            if (str != null) {
                name = str.ConvertToString();
            } else {
                var symbol = signalId as RubySymbol;
                if (symbol != null) {
                    name = symbol.ToString();
                }
            }

            if (name == null) {
                // Deliberately without #to_int: ruby/spec checks that an object which only
                // answers #to_int is rejected. The Ruby class name, not the CLR one - MRI says
                // "bad signal type Float", not "Double".
                throw RubyExceptions.CreateArgumentError("bad signal type {0}",
                    context != null ? context.GetClassName(signalId)
                                    : (signalId == null ? "NilClass" : signalId.GetType().Name));
            }

            // "-TERM" and "-SIGTERM" are the spelled-out forms of a negative signal number,
            // which Process.kill reads as "to the process group".
            bool negated = name.StartsWith("-", StringComparison.Ordinal);
            if (negated) {
                if (forTrap) {
                    throw RubyExceptions.CreateArgumentError("negative signal name: {0}", name);
                }
                name = name.Substring(1);
            }

            string bare = name.StartsWith("SIG", StringComparison.Ordinal) ? name.Substring(3) : name;
            if (bare == "EXIT") {
                return 0;
            }

            int number;
            if (!_numbers.TryGetValue(bare, out number)) {
                throw RubyExceptions.CreateArgumentError("unsupported signal '{0}'",
                    name.StartsWith("SIG", StringComparison.Ordinal) ? name : "SIG" + name);
            }
            return negated ? -number : number;
        }

        internal static string ToName(int number) {
            if (number == 0) {
                return "EXIT";
            }
            foreach (var entry in _numbers) {
                if (entry.Value == number) {
                    return entry.Key;
                }
            }
            return null;
        }

        #region kill

        private const int SignalPipe = 13;
        internal const int SignalInterrupt = 2;

        [DllImport("libc", EntryPoint = "signal")]
        private static extern IntPtr SysSignal(int signal, IntPtr handler);

        private static void SetPipeDisposition(bool systemDefault) {
            try {
                SysSignal(SignalPipe, systemDefault ? IntPtr.Zero : new IntPtr(1));   // SIG_DFL : SIG_IGN
            } catch (DllNotFoundException) {
            } catch (EntryPointNotFoundException) {
            }
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
        private static extern int SysKill(int pid, int signal);

        internal const int EINVAL = 22;

        /// <summary>
        /// A signal number that is not in Signal.list on Windows, where MRI's kill refuses it with
        /// EINVAL. Elsewhere kill(2) is the judge.
        /// </summary>
        internal static bool IsUnsupportedOnWindows(int signal) {
            return RubyProcess.IsWindows && signal != 0 && ToName(signal) == null;
        }

        /// <summary>
        /// Signals whose default disposition is to end the process. MRI installs a handler for each of
        /// these and turns them into a SignalException instead of letting the process die, so sending
        /// one to yourself is something a Ruby program can rescue. CHLD, CONT, URG and WINCH are
        /// ignored by default, and KILL and STOP cannot be caught at all.
        /// </summary>
        internal static bool TerminatesByDefault(int signal) {
            switch (signal) {
                case 9:   // KILL
                case 17:  // CHLD
                case 18:  // CONT
                case 19:  // STOP
                case 20:  // TSTP
                case 21:  // TTIN
                case 22:  // TTOU
                case 23:  // URG
                case 28:  // WINCH
                    return false;
                default:
                    return signal > 0 && !IgnoredByDefault(signal);
            }
        }

        /// <summary>
        /// Signals that would end the process but that MRI swallows unless a program asks otherwise:
        /// it installs a do-nothing handler for PIPE and SYS and keeps VTALRM for itself, so a
        /// program that sends one of them to itself carries on.
        /// </summary>
        internal static bool IgnoredByDefault(int signal) {
            switch (signal) {
                case SignalPipe:
                case 26:  // VTALRM
                case 31:  // SYS
                    return true;
                default:
                    return false;
            }
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "getpgid")]
        private static extern int SysGetPgid(int pid);

        /// <summary>This process's group, or 0 if the platform will not say.</summary>
        internal static int ProcessGroupId {
            get {
                try {
                    int pgid = SysGetPgid(0);
                    return (pgid < 0) ? 0 : pgid;
                } catch (Exception) {
                    return 0;
                }
            }
        }

        /// <summary>Whether a handler for the signal is registered with the platform.</summary>
        internal static bool IsRegistered(int signal) {
            lock (_handlers) {
                Handler handler;
                return _handlers.TryGetValue(signal, out handler) && handler.Registration != null;
            }
        }

        internal static bool HasHandler(int signal) {
            lock (_handlers) {
                return _handlers.ContainsKey(signal);
            }
        }

        /// <summary>
        /// Whether a #trap left something for the signal to run or to ignore it with - a handler,
        /// "IGNORE", or SIGINT's "DEFAULT" (Interrupt). Trapping "DEFAULT" back for any other
        /// signal, or "SYSTEM_DEFAULT", is the same as never having trapped it.
        /// </summary>
        internal static bool HasRubyHandler(int signal) {
            lock (_handlers) {
                Handler handler;
                if (!_handlers.TryGetValue(signal, out handler)) {
                    return false;
                }
                return !IsSystemDefault(handler.Command) && !(IsDefault(handler.Command) && handler.Invoke == null);
            }
        }

        /// <summary>Whether the signal was handed back to the OS with trap(signal, "SYSTEM_DEFAULT").</summary>
        internal static bool IsTrappedToSystemDefault(int signal) {
            lock (_handlers) {
                Handler handler;
                return _handlers.TryGetValue(signal, out handler) && IsSystemDefault(handler.Command);
            }
        }

        /// <summary>
        /// Sends the signal and returns 0, or -errno. Errno::ESRCH and most of its siblings are
        /// defined in Ruby by the prelude and have no CLR type to throw, so the number goes back
        /// to the prelude, which knows how to turn it into the right class.
        /// </summary>
        internal static int Kill(int pid, int signal) {
            if (RubyProcess.IsWindows) {
                // MRI's win32 kill() raise()s a signal a process sends itself (any but 0 and KILL), so
                // its trap handler runs. The main thread has run it already (RubyProcess.Kill); from any
                // other thread it is parked for the main one, as a delivered signal would be.
                if (pid == Environment.ProcessId && signal != 0 && signal != SignalKill) {
                    if (HasHandler(signal)) {
                        Enqueue(signal);
                    }
                    return 0;
                }
                return WindowsSpawn.Kill(pid, signal);
            }
            if (SysKill(pid, signal) == 0) {
                return 0;
            }

            int error = Marshal.GetLastWin32Error();
            if (error == EINVAL) {
                throw RubyExceptions.CreateArgumentError("invalid signal number ({0})", signal);
            }
            return -(error == 0 ? 1 : error);
        }

        #endregion

        #region trap

        private sealed class Handler {
            public object Command;                       // Proc, or a MutableString such as "DEFAULT"
            public Action<int> Invoke;                   // null for "IGNORE"
            public PosixSignalRegistration Registration;
        }

        private static readonly Dictionary<int, Handler> _handlers = new Dictionary<int, Handler>();

        /// <summary>
        /// Signals whose handler is waiting to run. MRI does not run a trap handler on whatever
        /// thread the signal happened to land on: it records the signal and the *main* thread runs
        /// the handler at its next interrupt check. .NET hands POSIX signals to a dedicated thread
        /// of its own, so we do the same thing - park the signal here and let the main thread pick
        /// it up at a safe point (Thread.pass, Kernel#sleep, ...).
        ///
        /// Running handlers on the signal thread instead was actively harmful: an exception out of
        /// a handler had to be re-raised asynchronously on the main thread, which landed in the
        /// middle of unrelated code. In ruby/spec that arrived inside the next example's `before`
        /// block, abandoning a half-constructed fixture and leaking its child process.
        /// </summary>
        private static readonly Queue<int> _pending = new Queue<int>();

        private static Thread _mainThread;

        [ThreadStatic]
        private static bool _running;

        /// <summary>The thread MRI would run trap handlers on. Set from the Ruby context.</summary>
        internal static Thread MainThread {
            get { return _mainThread; }
            set { _mainThread = value; }
        }

        internal static bool IsMainThread {
            get { return _mainThread != null && _mainThread == Thread.CurrentThread; }
        }

        /// <summary>
        /// Installs a handler for the signal and returns whatever was installed before, using MRI's
        /// spelling of "nothing was installed" ("DEFAULT"). <paramref name="invoke"/> runs the Ruby
        /// side of the handler; it is null for "IGNORE", which only cancels the default disposition.
        /// </summary>
        /// <summary>
        /// MRI refuses to let a Ruby handler replace the signals its own runtime depends on,
        /// and the kernel refuses KILL and STOP outright.
        /// </summary>
        internal static void CheckTrappable(int signal) {
            switch (signal) {
                case 4:   // ILL
                case 7:   // BUS
                case 8:   // FPE
                case 11:  // SEGV
                case 26:  // VTALRM
                    throw RubyExceptions.CreateArgumentError("can\'t trap reserved signal: SIG{0}", ToName(signal));

                case 9:   // KILL
                case 19:  // STOP
                    throw RubyExceptions.CreateArgumentError("Signal already used by VM or OS: SIG{0}", ToName(signal));
            }
        }

        /// <summary>
        /// What the first #trap of a signal reports as the previous handler. MRI spells "nobody
        /// ever trapped this" SYSTEM_DEFAULT - except for the signals it installs a handler of its
        /// own for at boot (HUP, INT, QUIT, ALRM, TERM, USR1, USR2, CHLD), which report DEFAULT,
        /// and ABRT, SYS and PIPE, which report nil (PIPE is ignored by the interpreter itself, as
        /// it is by .NET). Code tells the two apart: puma asserts that with PUMA_SKIP_SIGUSR2 set
        /// it left SIGUSR2 "DEFAULT".
        /// </summary>
        private static object InitialCommand(int signal) {
            if (RubyProcess.IsWindows && signal == 22) {   // ABRT
                return null;
            }
            switch (signal) {
                case 1:   // HUP
                case 2:   // INT
                case 3:   // QUIT
                case 10:  // USR1
                case 12:  // USR2
                case 14:  // ALRM
                case 15:  // TERM
                case 17:  // CHLD
                    return MutableString.CreateAscii("DEFAULT");
                case 6:   // ABRT
                case 13:  // PIPE
                case 31:  // SYS
                    return null;
                default:
                    return MutableString.CreateAscii("SYSTEM_DEFAULT");
            }
        }

        internal static object Trap(int signal, object command, Action<int> invoke) {
            return Trap(signal, command, invoke, null);
        }

        /// <summary>
        /// <paramref name="defaultInvoke"/>, when given, is what "DEFAULT" means for this signal
        /// in Ruby rather than at the OS level -- SIGINT's default disposition in MRI is to raise
        /// Interrupt on the main thread, not to kill the process. "SYSTEM_DEFAULT" always means
        /// the OS disposition and never uses it.
        /// </summary>
        internal static object Trap(int signal, object command, Action<int> invoke, Action<int> defaultInvoke) {
            CheckTrappable(signal);
            command = NormalizeCommand(command);

            lock (_handlers) {
                Handler handler;
                object previous = InitialCommand(signal);
                if (_handlers.TryGetValue(signal, out handler)) {
                    previous = handler.Command;
                    if (handler.Registration != null) {
                        handler.Registration.Dispose();
                    }
                    _handlers.Remove(signal);
                }

                // SYSTEM_DEFAULT hands SIGPIPE back to the OS, which ends the process on a write to
                // a closed pipe; anything else keeps it ignored so the write fails with EPIPE.
                if (signal == SignalPipe) {
                    SetPipeDisposition(IsSystemDefault(command));
                }

                var installed = new Handler { Command = command };
                if (IsDefault(command)) {
                    // Recorded so the next #trap reports "DEFAULT"; nothing is registered unless
                    // Ruby's own default disposition differs from the platform's (SIGINT raises
                    // Interrupt). The handler still runs on the main thread, never on this one.
                    if (defaultInvoke != null) {
                        installed.Invoke = defaultInvoke;
                        installed.Registration = TryRegister(signal);
                    }
                } else if (!IsSystemDefault(command)) {
                    installed.Invoke = IsIgnore(command) ? null : invoke;
                    installed.Registration = TryRegister(signal);
                }
                _handlers[signal] = installed;
                return previous;
            }
        }

        /// <summary>
        /// Runs the handler for a signal on the calling thread. Used by Process.kill when a process
        /// signals itself from the main thread, which MRI answers before kill(2) even returns.
        /// </summary>
        internal static void RunHandler(int signal) {
            Action<int> invoke;
            lock (_handlers) {
                Handler handler;
                invoke = _handlers.TryGetValue(signal, out handler) ? handler.Invoke : null;
            }
            if (invoke == null) {
                return;
            }

            bool wasRunning = _running;
            _running = true;
            try {
                invoke(signal);
            } finally {
                _running = wasRunning;
            }
        }

        /// <summary>
        /// A safe point on the main thread: runs whatever handlers the signal thread parked. Called
        /// from RubyUtils.CheckAsyncException, which is what Thread.pass and Kernel#sleep reach.
        /// An exception out of a handler propagates from here, which is where MRI raises it too.
        /// </summary>
        internal static void RunPending() {
            if (_running || _mainThread == null || _mainThread != Thread.CurrentThread) {
                return;
            }

            while (true) {
                int signal;
                lock (_handlers) {
                    if (_pending.Count == 0) {
                        return;
                    }
                    signal = _pending.Dequeue();
                    RubyUtils.SafePointRequestDone();
                }
                RunHandler(signal);
            }
        }

        /// <summary>Records a signal for the main thread and nudges it if it is asleep.</summary>
        private static void Enqueue(int signal) {
            lock (_handlers) {
                _pending.Enqueue(signal);
                // so that a main thread spinning in `loop {}` runs the handler at its next back edge
                RubyUtils.RequestSafePoint();
            }

            Thread main = _mainThread;
            if (main != null && main != Thread.CurrentThread) {
                // Ends a Kernel#sleep the way a signal does in MRI. Deliberately not
                // Thread.Interrupt: that throws at arbitrary managed waits, including ones with no
                // handler for it.
                ThreadOps.WakeForSignal(main);
            }
        }

        private const int SignalKill = 9;

        /// <summary>
        /// MRI answers the canonical spelling of a symbolic handler, not the one it was given:
        /// :SIG_IGN, "SIG_IGN" and :IGNORE all come back as "IGNORE".
        /// </summary>
        private static object NormalizeCommand(object command) {
            string name = CommandName(command);
            switch (name) {
                case "DEFAULT":
                case "SIG_DFL": return MutableString.CreateAscii("DEFAULT");
                case "IGNORE":
                case "SIG_IGN": return MutableString.CreateAscii("IGNORE");
                case "SYSTEM_DEFAULT": return MutableString.CreateAscii("SYSTEM_DEFAULT");
                case "EXIT": return MutableString.CreateAscii("EXIT");
                default: return command;
            }
        }

        private static string CommandName(object command) {
            var str = command as MutableString;
            if (str != null) {
                return str.ConvertToString();
            }
            var symbol = command as RubySymbol;
            return (symbol != null) ? symbol.ToString() : null;
        }

        /// <summary>
        /// Whether a handler means "do not run anything of mine": the three symbolic ones and nil.
        /// </summary>
        internal static bool IsDefaultOrIgnore(object command) {
            command = NormalizeCommand(command);
            return IsDefault(command) || IsSystemDefault(command) || IsIgnore(command);
        }

        private static bool IsDefault(object command) {
            return CommandName(command) == "DEFAULT";
        }

        private static bool IsSystemDefault(object command) {
            return CommandName(command) == "SYSTEM_DEFAULT";
        }

        // nil is MRI's third spelling of "ignore this signal".
        private static bool IsIgnore(object command) {
            return command == null || CommandName(command) == "IGNORE";
        }

        private static PosixSignalRegistration TryRegister(int signal) {
            try {
                return PosixSignalRegistration.Create((PosixSignal)signal, context => {
                    // Whatever the signal would normally do to the process, a Ruby handler replaces it.
                    context.Cancel = true;
                    Enqueue(signal);
                });
            } catch (Exception) {
                // Unsupported signal number, or a platform without POSIX signals: the handler is
                // still recorded so that #trap reports it back, it just never fires.
                return null;
            }
        }

        #endregion
    }
}

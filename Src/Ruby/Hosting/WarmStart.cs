/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;
using Microsoft.Scripting.Hosting.Shell;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Hosting {
    /// <summary>
    /// The worker half of warm start (Util/warm): a process the broker (Util/warm/ir-broker.c) started
    /// ahead of time with the same command line ir.sh uses, which has already created its runtime and
    /// loaded the core prelude and RubyGems, and now waits for one `ir` invocation to run.
    ///
    /// The client (Util/warm/ir-client.c) connects to the broker, which hands the connection to an
    /// idle worker. The client's request carries what a freshly exec'ed `ir` would have found in its
    /// process: argv, the environment, the working directory, umask, rlimits, CPU affinity and nice
    /// value, and its standard input, output and error as descriptors (SCM_RIGHTS). The worker checks
    /// that every input the startup already consumed is the same as the pool's - the options parsed
    /// from argv (with RUBYOPT, RUBYLIB, -I, the locale encoding ...), the startup environment
    /// variables, the binary and the files the prelude loaded - and either declines (the client then
    /// execs a cold `ir`, exactly what ir.sh would have done) or takes the request over: it puts the
    /// client's descriptors on 0, 1 and 2, adopts the rest of its process state, runs the program as
    /// the main script and exits. A worker is never reused for a second program.
    ///
    /// Wire format (all integers native-endian):
    ///   broker -> worker   'C' + SCM_RIGHTS(connection)
    ///   worker -> broker   'Y' ready, 'I' idle again (declined), 'A' accepted
    ///   client -> worker   "IRW1" u32 length, then the request (see ReadRequest), SCM_RIGHTS(0, 1, 2)
    ///   worker -> client   'F' u32 length + reason (declined), 'R' (accepted), 'E' i32 status (exiting)
    ///   broker -> client   'W' i32 pid (assigned), 'B' (no worker), 'X' i32 wait status + i64 maxrss KB
    /// </summary>
    public static class WarmStart {
        public const string WorkerFdVariable = "IR_WARM_WORKER_FD";
        public const string DebugVariable = "IR_WARM_DEBUG";

        private static int _brokerFd = -1;
        private static int _logFd = 2;
        private static int _connectionFd = -1;
        private static volatile bool _exiting;
        private static bool _debug;
        private static Dictionary<string, string> _startupEnvironment;
        private static List<KeyValuePair<string, DateTime>> _loadedFileStamps;
        private static List<string> _loadedFeatureNames;
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        public static bool IsWorker {
            get { return _brokerFd >= 0; }
        }

        internal static bool Debug {
            get { return _debug; }
        }

        /// <summary>
        /// Called first thing in Main. Cheap when not a worker: one environment lookup.
        /// </summary>
        public static void InitializeFromEnvironment() {
            string value = Environment.GetEnvironmentVariable(WorkerFdVariable);
            if (value == null) {
                return;
            }
            // Not inherited by anything the program starts, whatever happens next.
            Environment.SetEnvironmentVariable(WorkerFdVariable, null);
            unsetenv(WorkerFdVariable);

            int fd;
            if (!Int32.TryParse(value, out fd) || fd < 0) {
                return;
            }
            fcntl(fd, F_SETFD, FD_CLOEXEC);
            _brokerFd = fd;
            _debug = Environment.GetEnvironmentVariable(DebugVariable) != null;
            if (_debug) {
                // the broker's log, which descriptor 2 stops being once a request is taken over
                _logFd = fcntl(2, F_DUPFD_CLOEXEC, 3);
            }
            _startupEnvironment = CaptureEnvironment();
        }

        internal static void Log(string message) {
            Log(message, false);
        }

        /// <summary>To the broker's log; always when <paramref name="always"/>, otherwise only with IR_WARM_DEBUG.</summary>
        internal static void Log(string message, bool always) {
            if (_debug || always) {
                try {
                    var bytes = Encoding.UTF8.GetBytes(String.Format("[warm {0} +{1}ms] {2}\n", Environment.ProcessId, _clock.ElapsedMilliseconds, message));
                    write(_logFd, bytes, (IntPtr)bytes.Length);
                } catch (Exception) {
                    // diagnostics only
                }
            }
        }

        #region Startup inputs

        /// <summary>
        /// The environment variables that runtime startup - the CLR, the options parser, the RubyContext
        /// constructor, the prelude and RubyGems - reads. A request whose values differ from the pool's
        /// is declined, since the warm process has already acted on its own values. Everything else in
        /// the environment is read lazily and is simply replaced by the request's.
        /// </summary>
        internal static bool IsStartupVariable(string/*!*/ name) {
            if (name.StartsWith("IR_SERVER", StringComparison.Ordinal) || name.StartsWith("IR_WARM", StringComparison.Ordinal) ||
                name == "IR_CONFIG" || name == "IR_TFM") {
                // pick the binary (checked separately) or configure warm start itself
                return false;
            }
            // Not RUBYPATH: only -S reads it, as the request's command line is parsed (and ir.sh prepends
            // to it, so a program that runs ir.sh again would always start cold). Not RUBY_* either:
            // IronRuby reads none of MRI's RUBY_GC_*, RUBY_THREAD_* ..., and mspec passes RUBY_EXE and
            // RUBY_FLAGS to every ruby it starts.
            return name == "RUBYOPT" || name == "RUBYLIB" ||
                name.StartsWith("RUBYGEMS_", StringComparison.Ordinal) ||     // RUBYGEMS_GEMDEPS ...
                name.StartsWith("GEM", StringComparison.Ordinal) ||           // GEM_HOME, GEM_PATH, GEM_SPEC_CACHE, GEMRC ...
                name.StartsWith("BUNDLE", StringComparison.Ordinal) ||
                name.StartsWith("LD_", StringComparison.Ordinal) ||           // the dynamic loader reads LD_PRELOAD, LD_LIBRARY_PATH ... once
                name.StartsWith("MALLOC_", StringComparison.Ordinal) || name == "GLIBC_TUNABLES" ||
                name.StartsWith("DOTNET_", StringComparison.Ordinal) ||       // CLR knobs, read once at CLR startup
                name.StartsWith("COMPlus_", StringComparison.Ordinal) ||
                name.StartsWith("CORECLR_", StringComparison.Ordinal) ||
                name.StartsWith("IR_", StringComparison.Ordinal) ||           // IR_JIT_VERBOSE, IR_OSR_*, IR_CULTURE ...
                name.StartsWith("IRONRUBY", StringComparison.Ordinal) ||
                name.StartsWith("LC_", StringComparison.Ordinal) ||           // locale -> Encoding.default_external
                name == "LANG" || name == "LANGUAGE" ||
                name == "HOME" ||                                              // RubyConsoleHost, RubyGems
                name == "TZ";                                                  // RubyTime's static constructor
        }

        internal static Dictionary<string, string>/*!*/ CaptureEnvironment() {
            return RubyEnvironment.GetVariables();
        }

        internal static string CompareStartupEnvironment(Dictionary<string, string>/*!*/ request) {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var name in _startupEnvironment.Keys) {
                if (IsStartupVariable(name)) names.Add(name);
            }
            foreach (var name in request.Keys) {
                if (IsStartupVariable(name)) names.Add(name);
            }
            foreach (var name in names) {
                string a, b;
                _startupEnvironment.TryGetValue(name, out a);
                request.TryGetValue(name, out b);
                if (a != b) {
                    return "environment variable " + name + " differs from the pool's";
                }
            }
            return null;
        }

        /// <summary>
        /// Makes the process environment - .NET's copy and the C library's environ, which children
        /// started through posix_spawn and execve see - exactly the given one.
        /// </summary>
        internal static void ReplaceEnvironment(Dictionary<string, string>/*!*/ target) {
            var pal = PlatformAdaptationLayer.Default;
            var current = RubyEnvironment.GetVariables();
            foreach (var name in current.Keys) {
                if (!target.ContainsKey(name)) {
                    RubyEnvironment.SetVariable(pal, name, null);
                    unsetenv(name);
                }
            }
            foreach (var entry in target) {
                string value;
                if (current.TryGetValue(entry.Key, out value) && value == entry.Value) {
                    continue;
                }
                RubyEnvironment.SetVariable(pal, entry.Key, entry.Value);
                if (entry.Value.Length > 0) {
                    setenv(entry.Key, entry.Value, 1);
                }
            }
        }

        /// <summary>
        /// Remembers the files the warm-up loaded (the prelude, RubyGems, RbConfig ...) and the runtime's
        /// own assemblies, so that a worker started before a rebuild or an edit of the library does not
        /// run a program against stale code.
        /// </summary>
        internal static void RecordLoadedFiles(RubyContext/*!*/ context) {
            var stamps = new List<KeyValuePair<string, DateTime>>();
            var loadPaths = new List<string>();
            foreach (object dir in context.Loader.GetLoadPaths()) {
                string text = dir as string ?? (dir as MutableString)?.ToString();
                if (text != null && Path.IsPathRooted(text)) {
                    loadPaths.Add(text.TrimEnd('/') + "/");
                }
            }
            var names = new List<string>();
            var files = new List<string>();
            foreach (object file in context.Loader.GetLoadedFiles()) {
                files.Add(file as string ?? (file as MutableString)?.ToString());
            }
            // the prelude's files, which $" leaves out, and anything loaded rather than required
            List<string> resolved = context.Loader.SourceFileLog;
            context.Loader.SourceFileLog = null;
            if (resolved != null) {
                lock (resolved) {
                    files.AddRange(resolved);
                }
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in files) {
                string path = (file != null && Path.IsPathRooted(file)) ? Path.GetFullPath(file) : null;
                if (path != null && seen.Add(path)) {
                    stamps.Add(new KeyValuePair<string, DateTime>(path, GetStamp(path)));
                    // the name require found it under, which a directory in front of its own would shadow
                    foreach (string dir in loadPaths) {
                        if (path.StartsWith(dir, StringComparison.Ordinal)) {
                            names.Add(path.Substring(dir.Length));
                            break;
                        }
                    }
                }
            }
            _loadedFeatureNames = names;
            // The runtime itself: every assembly and native library of the build (a rebuild replaces
            // them; IronRuby.Libraries.dll may not have been loaded yet).
            string baseDirectory = AppContext.BaseDirectory;
            try {
                foreach (string file in Directory.GetFiles(baseDirectory)) {
                    if (file.EndsWith(".dll", StringComparison.Ordinal) || file.EndsWith(".so", StringComparison.Ordinal)) {
                        stamps.Add(new KeyValuePair<string, DateTime>(file, GetStamp(file)));
                    }
                }
            } catch (Exception) {
                // stamps of the scripts only
            }
            _loadedFileStamps = stamps;
            Log("watching " + stamps.Count + " files for changes");
        }

        private static DateTime GetStamp(string path) {
            try {
                return File.GetLastWriteTimeUtc(path);
            } catch (Exception) {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// A file in one of the given directories that a cold start, with them in front of $LOAD_PATH,
        /// would have loaded during startup instead of the one the warm-up loaded; null if none.
        /// </summary>
        internal static string FindShadowingFile(IEnumerable<string>/*!*/ directories) {
            if (_loadedFeatureNames == null) {
                return null;
            }
            foreach (string dir in directories) {
                foreach (string name in _loadedFeatureNames) {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) {
                        return candidate;
                    }
                }
            }
            return null;
        }

        internal static string FindStaleFile() {
            if (_loadedFileStamps == null) {
                return null;
            }
            foreach (var entry in _loadedFileStamps) {
                if (GetStamp(entry.Key) != entry.Value) {
                    return entry.Key;
                }
            }
            return null;
        }

        #endregion

        #region Transport

        internal sealed class Request {
            public int ClientPid, ClientParentPid, Umask, Nice;
            public long IgnoredSignals;
            public readonly List<long[]> Limits = new List<long[]>();
            public byte[] Affinity;
            public string Cwd;
            public string[] Argv;
            public string TerminalPath;       // the client's controlling terminal (ttyname of /dev/tty), or empty
            public readonly Dictionary<string, string> Environment = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool InvalidText;
            public int[] Descriptors;
        }

        internal static void SignalReady() {
            SendToBroker((byte)'Y');
        }

        private static void SendToBroker(byte message) {
            write(_brokerFd, new[] { message }, (IntPtr)1);
        }

        /// <summary>
        /// Waits for the broker to hand over a client connection and reads its request. Null when the
        /// broker has gone away, which is the worker's cue to exit.
        /// </summary>
        internal static Request ReceiveRequest() {
            while (true) {
                var one = new byte[1];
                int[] fds;
                int n = ReceiveWithDescriptors(_brokerFd, one, 0, 1, out fds);
                if (n <= 0) {
                    return null;
                }
                if (one[0] != (byte)'C' || fds.Length != 1) {
                    CloseAll(fds);
                    continue;
                }
                _connectionFd = fds[0];
                Request request = ReadRequest(_connectionFd);
                if (request != null) {
                    return request;
                }
                Log("unreadable request");
                close(_connectionFd);
                _connectionFd = -1;
                SendToBroker((byte)'I');
            }
        }

        private static Request ReadRequest(int fd) {
            var buffer = new byte[65536];
            int[] fds;
            int got = ReceiveWithDescriptors(fd, buffer, 0, buffer.Length, out fds);
            if (got < 8 || buffer[0] != 'I' || buffer[1] != 'R' || buffer[2] != 'W' || buffer[3] != '1') {
                CloseAll(fds);
                return null;
            }
            int total = 8 + BitConverter.ToInt32(buffer, 4);
            if (total > (1 << 26)) {
                CloseAll(fds);
                return null;
            }
            if (total > buffer.Length) {
                Array.Resize(ref buffer, total);
            }
            while (got < total) {
                int[] more;
                int n = ReceiveWithDescriptors(fd, buffer, got, total - got, out more);
                CloseAll(more);
                if (n <= 0) {
                    CloseAll(fds);
                    return null;
                }
                got += n;
            }
            if (fds.Length != 3) {
                CloseAll(fds);
                return null;
            }

            var request = new Request { Descriptors = fds };
            var reader = new Reader(buffer, 8, total);
            request.ClientPid = reader.Int32();
            request.ClientParentPid = reader.Int32();
            request.Umask = reader.Int32();
            request.Nice = reader.Int32();
            request.IgnoredSignals = reader.Int64();
            int limits = reader.Int32();
            for (int i = 0; i < limits; i++) {
                request.Limits.Add(new long[] { reader.Int32(), reader.Int64(), reader.Int64() });
            }
            request.Affinity = reader.Bytes();
            request.Cwd = reader.Text(ref request.InvalidText);
            int argc = reader.Int32();
            request.Argv = new string[argc];
            for (int i = 0; i < argc; i++) {
                request.Argv[i] = reader.Text(ref request.InvalidText);
            }
            int envc = reader.Int32();
            for (int i = 0; i < envc; i++) {
                string entry = reader.Text(ref request.InvalidText);
                int eq = entry.IndexOf('=');
                if (eq > 0 && !request.Environment.ContainsKey(entry.Substring(0, eq))) {
                    request.Environment.Add(entry.Substring(0, eq), entry.Substring(eq + 1));
                }
            }
            request.TerminalPath = reader.AtEnd ? "" : reader.Text(ref request.InvalidText);
            return request;
        }

        private sealed class Reader {
            private static readonly Encoding _strict = new UTF8Encoding(false, true);
            private readonly byte[] _data;
            private readonly int _end;
            private int _position;

            public Reader(byte[] data, int position, int end) {
                _data = data;
                _position = position;
                _end = end;
            }

            public bool AtEnd {
                get { return _position >= _end; }
            }

            public int Int32() {
                int value = BitConverter.ToInt32(_data, _position);
                _position += 4;
                return value;
            }

            public long Int64() {
                long value = BitConverter.ToInt64(_data, _position);
                _position += 8;
                return value;
            }

            public byte[] Bytes() {
                int length = Int32();
                var result = new byte[length];
                Buffer.BlockCopy(_data, _position, result, 0, length);
                _position += length;
                return result;
            }

            public string Text(ref bool invalid) {
                int length = Int32();
                string result;
                try {
                    result = _strict.GetString(_data, _position, length);
                } catch (DecoderFallbackException) {
                    // .NET would have decoded the bytes lossily at startup; only a cold start (and
                    // RubyCommandLine's /proc/self/cmdline recovery) sees the real ones
                    invalid = true;
                    result = Encoding.UTF8.GetString(_data, _position, length);
                }
                _position += length;
                return result;
            }
        }

        internal static void Decline(Request/*!*/ request, string/*!*/ reason) {
            // why a client went cold, for whoever looks after the pool (and clients say so with IR_SERVER_VERBOSE)
            Log("declined: " + reason, true);
            var text = Encoding.UTF8.GetBytes(reason);
            var message = new byte[5 + text.Length];
            message[0] = (byte)'F';
            BitConverter.GetBytes(text.Length).CopyTo(message, 1);
            text.CopyTo(message, 5);
            SendToConnection(message);
            CloseAll(request.Descriptors);
            close(_connectionFd);
            _connectionFd = -1;
        }

        /// <summary>Whether the descriptor is a terminal.</summary>
        internal static bool IsTerminal(int fd) {
            return isatty(fd) == 1;
        }

        internal static void ReturnToPool() {
            SendToBroker((byte)'I');
        }

        private static void SendToConnection(byte[]/*!*/ message) {
            if (_connectionFd >= 0) {
                send(_connectionFd, message, (IntPtr)message.Length, MSG_NOSIGNAL);
            }
        }

        /// <summary>
        /// Takes the request over: from here on this process is the client's `ir`.
        /// </summary>
        internal static void Commit(Request/*!*/ request, RubyContext/*!*/ context) {
            SendToConnection(new[] { (byte)'R' });
            SendToBroker((byte)'A');

            // What the client ignored, a cold `ir` would have inherited as ignored - before .NET's
            // console (Console.CancelKeyPress) looks at SIGINT and SIGQUIT. SIGCHLD is left alone:
            // ignoring it changes what waitpid sees, and .NET relies on it.
            foreach (int signal in _ignorableSignals) {
                if ((request.IgnoredSignals & (1L << signal)) != 0) {
                    WarmStart.signal(signal, (IntPtr)1 /* SIG_IGN */);
                }
            }

            for (int i = 0; i < 3; i++) {
                dup2(request.Descriptors[i], i);
            }
            for (int i = 0; i < 3; i++) {
                if (request.Descriptors[i] > 2 && Array.IndexOf(request.Descriptors, request.Descriptors[i], i + 1) < 0) {
                    close(request.Descriptors[i]);
                }
            }

            umask(request.Umask);
            foreach (var limit in request.Limits) {
                var value = new RLimit { Current = (ulong)limit[1], Maximum = (ulong)limit[2] };
                if (limit[0] == RLIMIT_NOFILE) {
                    // CoreCLR's PAL raises the soft descriptor limit to the hard one as it starts
                    // (INIT_IncreaseDescriptorLimit); a cold `ir` sees that, not what it inherited.
                    value.Current = value.Maximum;
                }
                RLimit current;
                if (getrlimit((int)limit[0], out current) == 0 && (current.Current != value.Current || current.Maximum != value.Maximum)) {
                    if (setrlimit((int)limit[0], ref value) != 0) {
                        Log("setrlimit " + limit[0] + " failed");
                    }
                }
            }

            // Affinity and nice are per thread on Linux: apply them to every thread there is now;
            // threads started later inherit them from the one starting them.
            foreach (int tid in GetThreadIds()) {
                if (request.Affinity.Length > 0) {
                    sched_setaffinity(tid, (IntPtr)request.Affinity.Length, request.Affinity);
                }
                setpriority(PRIO_PROCESS, tid, request.Nice);
            }

            // The program's parent is the client's: Process.ppid must not name the broker.
            RubyContext.ReportedParentProcessId = request.ClientParentPid;

            // This process has no controlling terminal, so /dev/tty does not open; IO.console
            // (io/console.rb) opens the client's terminal by its name instead.
            object io;
            if (!String.IsNullOrEmpty(request.TerminalPath) && context.ObjectClass.TryGetConstant(null, "IO", out io)) {
                context.SetInstanceVariable(io, "@__warm_console__", MutableString.Create(request.TerminalPath, RubyEncoding.UTF8));
            }

            ResetConsole(context.DomainManager.SharedIO);
            StartConnectionWatch();
        }

        internal const int RLIMIT_DATA = 2, RLIMIT_STACK = 3, RLIMIT_NOFILE = 7, RLIMIT_AS = 9;

        /// <summary>
        /// Why the request's resource limits cannot be applied to this process the way a cold start
        /// would have inherited them, or null. The stack size of the main thread is fixed at exec,
        /// and the address-space and data limits decide whether the runtime starts at all (the GC
        /// reserves its heap up front): those have to match. Any other limit is set as the program
        /// starts, which only fails where the request raises a hard limit above this process's own.
        /// </summary>
        internal static string CheckLimits(Request/*!*/ request) {
            bool privileged = geteuid() == 0;
            foreach (var limit in request.Limits) {
                RLimit current;
                if (getrlimit((int)limit[0], out current) != 0) {
                    continue;
                }
                bool same = current.Current == (ulong)limit[1] && current.Maximum == (ulong)limit[2];
                switch ((int)limit[0]) {
                    case RLIMIT_STACK:
                        if (current.Current != (ulong)limit[1]) {
                            return "a different stack size limit";
                        }
                        break;
                    case RLIMIT_AS:
                    case RLIMIT_DATA:
                        if (!same) {
                            return "a different memory limit (RLIMIT_" + (limit[0] == RLIMIT_AS ? "AS" : "DATA") + ")";
                        }
                        break;
                    default:
                        if (!privileged && (ulong)limit[2] > current.Maximum) {
                            return "resource limit " + limit[0] + " above the pool's hard limit";
                        }
                        break;
                }
            }
            return null;
        }

        // SIGHUP SIGINT SIGQUIT SIGUSR1 SIGUSR2 SIGALRM SIGTERM SIGTSTP SIGTTIN SIGTTOU SIGURG SIGXCPU SIGXFSZ SIGVTALRM SIGPROF SIGPWR
        private static readonly int[] _ignorableSignals = { 1, 2, 3, 10, 12, 14, 15, 20, 21, 22, 23, 24, 25, 26, 27, 30 };

        private static IEnumerable<int> GetThreadIds() {
            string[] entries;
            try {
                entries = Directory.GetDirectories("/proc/self/task");
            } catch (Exception) {
                yield break;
            }
            foreach (var entry in entries) {
                int tid;
                if (Int32.TryParse(Path.GetFileName(entry), out tid)) {
                    yield return tid;
                }
            }
        }

        /// <summary>
        /// A client that disappears without a word (SIGKILL, say, which it cannot forward) takes its
        /// program with it, as it would if the program were running in the client's own process.
        /// </summary>
        private static void StartConnectionWatch() {
            int fd = _connectionFd;
            var thread = new Thread(() => {
                var poll = new PollFd { Fd = fd, Events = POLLIN | POLLRDHUP };
                while (true) {
                    int n = WarmStart.poll(ref poll, (UIntPtr)1, -1);
                    if (n > 0) {
                        if (!_exiting) {
                            kill(Environment.ProcessId, 9);
                        }
                        return;
                    }
                    if (n < 0 && Marshal.GetLastWin32Error() != 4 /* EINTR */) {
                        return;
                    }
                }
            });
            thread.IsBackground = true;
            thread.Name = "warm start client watch";
            thread.Start();
        }

        /// <summary>
        /// Called as Main returns. The broker reports the real wait status once the process is gone;
        /// this is the early word, and what the client goes by if the broker is no longer there.
        /// </summary>
        public static void ReportExit(int status) {
            if (_connectionFd < 0) {
                return;
            }
            _exiting = true;
            Log("exit " + status);
            var message = new byte[5];
            message[0] = (byte)'E';
            BitConverter.GetBytes(status).CopyTo(message, 1);
            SendToConnection(message);
        }

        /// <summary>
        /// Calls everything Commit and the connection watch call, with arguments that change nothing,
        /// so that their marshalling stubs are generated now rather than on the client's time.
        /// </summary>
        internal static void WarmUpNative(SharedIO/*!*/ io) {
            int mask = umask(0);
            umask(mask);
            RLimit limit;
            if (getrlimit(7 /* RLIMIT_NOFILE */, out limit) == 0) {
                setrlimit(7, ref limit);
            }
            var cpus = new byte[128];
            foreach (int tid in GetThreadIds()) {
                if (sched_getaffinity(tid, (IntPtr)cpus.Length, cpus) == 0) {
                    sched_setaffinity(tid, (IntPtr)cpus.Length, cpus);
                }
                Marshal.SetLastPInvokeError(0);
                int nice = getpriority(PRIO_PROCESS, tid);
                if (Marshal.GetLastPInvokeError() == 0) {
                    setpriority(PRIO_PROCESS, tid, nice);
                }
            }
            dup2(0, 0);
            close(-1);
            send(-1, new byte[1], (IntPtr)0, MSG_NOSIGNAL);
            var poll = new PollFd { Fd = -1, Events = POLLIN | POLLRDHUP };
            WarmStart.poll(ref poll, (UIntPtr)1, 0);
            kill(Environment.ProcessId, 0);
            IntPtr previous = signal(30 /* SIGPWR */, (IntPtr)0);
            signal(30, previous);
            ResetConsole(io);
        }

        #endregion

        #region Console

        /// <summary>
        /// .NET opens its standard streams on a dup() of 0, 1 and 2 and remembers whether they were
        /// redirected; IronRuby's ConsoleStream and the DLR's SharedIO cache streams too. Whatever the
        /// warm-up created was created against the broker's /dev/null, so all of it goes back to the
        /// lazy state a fresh process starts in, and is opened again on the client's descriptors.
        /// </summary>
        internal static void ResetConsole(SharedIO/*!*/ io) {
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            Type console = typeof(Console);
            foreach (var name in new[] { "s_in", "s_out", "s_error", "_isStdInRedirected", "_isStdOutRedirected", "_isStdErrRedirected" }) {
                FieldInfo field = console.GetField(name, statics);
                if (field != null && field.GetValue(null) != null) {
                    Log("reset Console." + name);
                    field.SetValue(null, null);
                }
            }
            foreach (var name in new[] { "s_isOutTextWriterRedirected", "s_isErrorTextWriterRedirected" }) {
                console.GetField(name, statics)?.SetValue(null, false);
            }
            Type pal = console.Assembly.GetType("System.ConsolePal");
            FieldInfo reader = pal?.GetField("s_stdInReader", statics);
            if (reader != null && reader.GetValue(null) != null) {
                Log("reset ConsolePal.s_stdInReader");
                reader.SetValue(null, null);
            }

            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            bool inputUsed = typeof(SharedIO).GetField("_inputStream", instance)?.GetValue(io) != null;
            foreach (var name in new[] { "_inputStream", "_outputStream", "_errorStream", "_inputReader", "_outputWriter", "_errorWriter", "_inputEncoding" }) {
                FieldInfo field = typeof(SharedIO).GetField(name, instance);
                if (field != null && field.GetValue(io) != null) {
                    Log("reset SharedIO." + name);
                    field.SetValue(io, null);
                }
            }
            if (inputUsed) {
                // ConsoleInputStream.Instance (static readonly) was opened on the old descriptor 0.
                Stream input = Console.OpenStandardInput();
                io.SetInput(input, new StreamReader(input, Console.InputEncoding), Console.InputEncoding);
            }

            ConsoleStream.ResetStandardDescriptors();
        }

        private static readonly string[] _consoleFields = {
            "s_in", "s_out", "s_error", "_isStdInRedirected", "_isStdOutRedirected", "_isStdErrRedirected",
            "s_isOutTextWriterRedirected", "s_isErrorTextWriterRedirected",
        };

        private static readonly string[] _sharedIOFields = {
            "_inputStream", "_outputStream", "_errorStream", "_inputReader", "_outputWriter", "_errorWriter", "_inputEncoding",
        };

        /// <summary>
        /// Whether every private field ResetConsole puts back exists in this runtime (.NET 8's System.Console,
        /// the DLR's SharedIO). A worker on a runtime where one is missing would run programs with
        /// streams still bound to the broker's /dev/null, so it never offers itself.
        /// </summary>
        internal static string FindMissingConsoleField() {
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            foreach (var name in _consoleFields) {
                if (typeof(Console).GetField(name, statics) == null) {
                    return "System.Console." + name;
                }
            }
            Type pal = typeof(Console).Assembly.GetType("System.ConsolePal");
            if (pal == null || pal.GetField("s_stdInReader", statics) == null) {
                return "System.ConsolePal.s_stdInReader";
            }
            foreach (var name in _sharedIOFields) {
                if (typeof(SharedIO).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) == null) {
                    return "SharedIO." + name;
                }
            }
            return null;
        }

        internal static void LogConsoleState() {
            if (!_debug) {
                return;
            }
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var sb = new StringBuilder("console state after warm-up:");
            Type console = typeof(Console);
            foreach (var name in new[] { "s_in", "s_out", "s_error", "_isStdInRedirected", "_isStdOutRedirected", "_isStdErrRedirected", "s_sigIntRegistration" }) {
                sb.Append(' ').Append(name).Append('=').Append(console.GetField(name, statics)?.GetValue(null) != null ? "set" : "-");
            }
            Type pal = console.Assembly.GetType("System.ConsolePal");
            sb.Append(" ConsolePal.s_initialized=").Append(pal?.GetField("s_initialized", statics)?.GetValue(null));
            Log(sb.ToString());
        }

        #endregion

        #region Native

        private const int F_SETFD = 2, FD_CLOEXEC = 1, F_DUPFD_CLOEXEC = 1030;
        private const int MSG_NOSIGNAL = 0x4000, MSG_CMSG_CLOEXEC = 0x40000000;
        private const int SOL_SOCKET = 1, SCM_RIGHTS = 1;
        private const short POLLIN = 0x1, POLLRDHUP = 0x2000;
        private const int PRIO_PROCESS = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct IoVec {
            public IntPtr Base;
            public UIntPtr Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MsgHdr {
            public IntPtr Name;
            public uint NameLength;
            public IntPtr Iov;
            public UIntPtr IovLength;
            public IntPtr Control;
            public UIntPtr ControlLength;
            public int Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RLimit {
            public ulong Current;
            public ulong Maximum;
        }

        [DllImport("libc", SetLastError = true)] private static extern IntPtr recvmsg(int fd, ref MsgHdr msg, int flags);
        [DllImport("libc", SetLastError = true)] private static extern IntPtr send(int fd, byte[] buffer, IntPtr length, int flags);
        [DllImport("libc", SetLastError = true)] private static extern IntPtr write(int fd, byte[] buffer, IntPtr length);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int dup2(int fd, int fd2);
        [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int cmd, int arg);
        [DllImport("libc")] private static extern int umask(int mask);
        [DllImport("libc")] private static extern int geteuid();
        [DllImport("libc")] private static extern int isatty(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int getrlimit(int resource, out RLimit limit);
        [DllImport("libc", SetLastError = true)] private static extern int setrlimit(int resource, ref RLimit limit);
        [DllImport("libc", SetLastError = true)] private static extern int sched_setaffinity(int pid, IntPtr size, byte[] mask);
        [DllImport("libc", SetLastError = true)] private static extern int sched_getaffinity(int pid, IntPtr size, byte[] mask);
        [DllImport("libc", SetLastError = true)] private static extern int setpriority(int which, int who, int prio);
        [DllImport("libc", SetLastError = true)] private static extern int getpriority(int which, int who);
        [DllImport("libc", SetLastError = true)] private static extern int poll(ref PollFd fds, UIntPtr count, int timeout);
        [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
        [DllImport("libc")] private static extern IntPtr signal(int signal, IntPtr handler);
        [DllImport("libc")] private static extern int setenv(string name, string value, int overwrite);
        [DllImport("libc")] private static extern int unsetenv(string name);

        private static void CloseAll(int[] fds) {
            if (fds != null) {
                foreach (int fd in fds) {
                    close(fd);
                }
            }
        }

        /// <summary>recvmsg(2) that also collects SCM_RIGHTS descriptors (close-on-exec).</summary>
        private static int ReceiveWithDescriptors(int fd, byte[]/*!*/ buffer, int offset, int count, out int[]/*!*/ fds) {
            const int controlSize = 256;
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            IntPtr control = Marshal.AllocHGlobal(controlSize);
            IntPtr iov = Marshal.AllocHGlobal(Marshal.SizeOf<IoVec>());
            try {
                Marshal.StructureToPtr(new IoVec { Base = pin.AddrOfPinnedObject() + offset, Length = (UIntPtr)count }, iov, false);
                var header = new MsgHdr { Iov = iov, IovLength = (UIntPtr)1, Control = control, ControlLength = (UIntPtr)controlSize };
                long n;
                do {
                    n = (long)recvmsg(fd, ref header, MSG_CMSG_CLOEXEC);
                } while (n < 0 && Marshal.GetLastWin32Error() == 4 /* EINTR */);

                var result = new List<int>();
                long length = (long)header.ControlLength;
                long position = 0;
                while (n >= 0 && position + 16 <= length) {
                    long cmsgLength = Marshal.ReadInt64(control, (int)position);
                    int level = Marshal.ReadInt32(control, (int)position + 8);
                    int type = Marshal.ReadInt32(control, (int)position + 12);
                    if (cmsgLength < 16) {
                        break;
                    }
                    if (level == SOL_SOCKET && type == SCM_RIGHTS) {
                        for (long p = 16; p + 4 <= cmsgLength; p += 4) {
                            result.Add(Marshal.ReadInt32(control, (int)(position + p)));
                        }
                    }
                    position += (cmsgLength + 7) & ~7L;
                }
                fds = result.ToArray();
                return (int)n;
            } finally {
                Marshal.FreeHGlobal(iov);
                Marshal.FreeHGlobal(control);
                pin.Free();
            }
        }

        #endregion
    }

    public abstract partial class RubyConsoleHost {
        // What the pool's own command line parsed into (see ParseOptions).
        private Dictionary<string, object> _warmLanguageOptions;
        private Dictionary<string, object> _warmRuntimeOptions;
        private bool _warmDebugMode, _warmPrivateBinding;
        private ConsoleOptions _warmConsoleOptions;
        // -I directories (and only those) the request has in front of the pool's load path
        private List<string> _warmExtraLoadPaths;
        private bool _warmStdinIsTerminal;

        private void SnapshotWarmOptions(ScriptRuntimeSetup/*!*/ runtimeSetup, LanguageSetup/*!*/ languageSetup, ConsoleOptions/*!*/ options) {
            _warmLanguageOptions = new Dictionary<string, object>(languageSetup.Options);
            _warmRuntimeOptions = new Dictionary<string, object>(runtimeSetup.Options);
            _warmDebugMode = runtimeSetup.DebugMode;
            _warmPrivateBinding = runtimeSetup.PrivateBinding;
            _warmConsoleOptions = options;
        }

        protected override void ExecuteInternal() {
            if (WarmStart.IsWorker) {
                ExitCode = RunWarmWorker();
                return;
            }
            base.ExecuteInternal();
        }

        private int RunWarmWorker() {
            var context = (RubyContext)HostingHelpers.GetLanguageContext(Engine);
            WarmStart.Log("runtime ready");
            context.Loader.SourceFileLog = new List<string>();
            context.PrepareWarmStart();
            WarmStart.RecordLoadedFiles(context);
            WarmStart.Log("prelude loaded");
            WarmUpRequestPath(context);
            WarmStart.Log("request path warmed up");
            WarmStart.LogConsoleState();
            string missing = WarmStart.FindMissingConsoleField();
            if (missing != null) {
                // the broker gives up after three workers that die warming up; clients then start cold
                WarmStart.Log("cannot serve requests: this runtime has no " + missing, true);
                return 1;
            }
            WarmStart.SignalReady();

            while (true) {
                WarmStart.Request request = WarmStart.ReceiveRequest();
                if (request == null) {
                    // the broker is gone and nobody is going to ask
                    return 0;
                }
                WarmStart.Log("request: " + String.Join(" ", request.Argv.Skip(1)));

                RubyConsoleOptions options;
                IDictionary<string, object> languageOptions;
                string stale = WarmStart.FindStaleFile();
                if (stale != null) {
                    // A rebuild or an edit since this worker loaded the file; the broker starts fresh ones.
                    WarmStart.Decline(request, "stale: " + stale + " changed since the worker started");
                    return 0;
                }

                string reason = TryAdoptRequest(request, out options, out languageOptions);
                WarmStart.Log("request parsed");
                if (reason != null) {
                    WarmStart.Decline(request, reason);
                    WarmStart.ReturnToPool();
                    continue;
                }

                WarmStart.Commit(request, context);
                WarmStart.Log("committed");
                context.ApplyWarmStartRequest(
                    (string)GetValue(languageOptions, "MainFile"),
                    GetValue(languageOptions, "Arguments") as ICollection<string> ?? new string[0],
                    GetValue(languageOptions, "RequiredPaths") as IList<string>,
                    GetValue(languageOptions, "SearchPaths"),
                    _warmExtraLoadPaths,
                    languageOptions
                );
                WarmStart.Log("running");
                return RunWarmRequest(options);
            }
        }

        /// <summary>
        /// Takes a request just like the ones to come through every step that does not commit to it -
        /// the pool's own command line with `-e nil`, in the pool's environment and directory - and
        /// compiles a small program the way a main script is compiled, without running it.
        /// </summary>
        private void WarmUpRequestPath(RubyContext/*!*/ context) {
            try {
                var request = new WarmStart.Request {
                    Cwd = Environment.CurrentDirectory,
                    Argv = new[] { Environment.ProcessPath }
                        .Concat(Environment.GetCommandLineArgs().Skip(1))
                        .Concat(new[] { "-e", "nil" }).ToArray(),
                };
                foreach (var entry in WarmStart.CaptureEnvironment()) {
                    request.Environment[entry.Key] = entry.Value;
                }
                RubyConsoleOptions options;
                IDictionary<string, object> languageOptions;
                string reason = TryAdoptRequest(request, out options, out languageOptions);
                if (reason != null) {
                    WarmStart.Log("warm-up request declined: " + reason);
                }
                WarmStart.WarmUpNative(context.DomainManager.SharedIO);
            } catch (Exception e) {
                WarmStart.Log("request path warm-up failed: " + e.Message);
            }
        }

        private static object GetValue(IDictionary<string, object>/*!*/ options, string/*!*/ name) {
            object value;
            return options.TryGetValue(name, out value) ? value : null;
        }

        /// <summary>
        /// Puts the request's environment and working directory in place and parses its command line
        /// exactly as a cold start would. Returns null when the result is one the warm process can run;
        /// otherwise everything is put back and the reason is returned.
        /// </summary>
        private string TryAdoptRequest(WarmStart.Request/*!*/ request, out RubyConsoleOptions options, out IDictionary<string, object> languageOptions) {
            options = null;
            languageOptions = null;

            if (request.InvalidText) {
                return "argv, environment or directory is not valid UTF-8";
            }
            if (request.Argv.Length < 1 || !SameFile(request.Argv[0], Environment.ProcessPath)) {
                return "a different ir binary";
            }
            string reason = WarmStart.CompareStartupEnvironment(request.Environment);
            if (reason != null) {
                return reason;
            }
            reason = WarmStart.CheckLimits(request);
            if (reason != null) {
                return reason;
            }

            // -X:UsePrism is accepted and ignored, as by Main: prism is the only parser.
            var args = new List<string>(request.Argv.Skip(1));
            while (args.Remove("-X:UsePrism")) { }

            var savedEnvironment = WarmStart.CaptureEnvironment();
            string savedDirectory = Environment.CurrentDirectory;
            WarmStart.ReplaceEnvironment(request.Environment);
            try {
                Directory.SetCurrentDirectory(request.Cwd);
            } catch (Exception) {
                WarmStart.ReplaceEnvironment(savedEnvironment);
                return "cannot enter " + request.Cwd;
            }

            _warmStdinIsTerminal = request.Descriptors != null && WarmStart.IsTerminal(request.Descriptors[0]);
            reason = ParseRequest(args.ToArray(), out options, out languageOptions);
            if (reason != null) {
                WarmStart.ReplaceEnvironment(savedEnvironment);
                Directory.SetCurrentDirectory(savedDirectory);
                return reason;
            }

            // A cold start does this in the host's constructor, with the environment it was given.
            SetHomeEnvironmentVariable();
            return null;
        }

        private static bool SameFile(string a, string b) {
            try {
                return a != null && b != null && Path.GetFullPath(ResolveLinks(a)) == Path.GetFullPath(ResolveLinks(b));
            } catch (Exception) {
                return false;
            }
        }

        private static string ResolveLinks(string path) {
            var target = File.ResolveLinkTarget(path, true);
            return target != null ? target.FullName : path;
        }

        private string ParseRequest(string[]/*!*/ args, out RubyConsoleOptions options, out IDictionary<string, object> languageOptions) {
            options = null;
            languageOptions = null;

            var runtimeSetup = CreateRuntimeSetup();
            var hostOptions = new ConsoleHostOptions();
            try {
                new ConsoleHostOptionsParser(hostOptions, runtimeSetup).Parse(args);
            } catch (InvalidOptionException) {
                return "invalid host option";
            }
            if (hostOptions.RunAction != Options.RunAction || hostOptions.HasLanguageProvider != Options.HasLanguageProvider ||
                hostOptions.EnvironmentVars.Count != 0 || Options.EnvironmentVars.Count != 0 ||
                (hostOptions.SourceUnitSearchPaths != null && hostOptions.SourceUnitSearchPaths.Length != 0)) {
                return "DLR host options";
            }

            string provider = Provider.AssemblyQualifiedName;
            LanguageSetup languageSetup = runtimeSetup.LanguageSetups.FirstOrDefault(s => s.TypeName == provider);
            if (languageSetup == null) {
                languageSetup = new LanguageSetup(provider, Provider.Name);
                runtimeSetup.LanguageSetups.Add(languageSetup);
            }

            // RubyConsoleHost.ParseOptions and ConsoleHost.ParseOptions, minus Host's side effects
            // (-C, -v), which are applied once the request is known to be one this process can run.
            languageSetup.Options["ApplicationBase"] = AppDomain.CurrentDomain.BaseDirectory;
            runtimeSetup.HostType = typeof(RubyScriptHost);
            var parser = CreateOptionsParser();
            var rubyParser = parser as RubyOptionsParser;
            if (rubyParser != null) {
                // `ironruby' alone starts irb at a terminal: the client's stdin decides, not this process's
                rubyParser.StdinIsTerminal = _warmStdinIsTerminal;
            }
            // The parser warns on Console.Error (an unknown --enable feature, say), and descriptor 2 is still the
            // broker's until the request is committed: a request it warns about goes cold, where the warning is seen.
            var parserErrors = new StringWriter();
            var brokerError = Console.Error;
            Console.SetError(parserErrors);
            try {
                parser.Parse(hostOptions.IgnoredArgs.ToArray(), runtimeSetup, languageSetup, PlatformAdaptationLayer);
            } catch (InvalidOptionException) {
                // a cold start reports it
                return "invalid option";
            } finally {
                Console.SetError(brokerError);
            }
            if (parserErrors.GetStringBuilder().Length > 0) {
                return "the options parser printed a warning";
            }
            options = parser.CommonConsoleOptions as RubyConsoleOptions;
            if (options == null) {
                return "unexpected console options";
            }

            var diffs = new List<string>();
            CompareOptions("language", _warmLanguageOptions, languageSetup.Options,
                new[] { "MainFile", "Arguments", "RequiredPaths", "SearchPaths" }.Concat(RubyContext.WarmStartProgramOptions).ToArray(), diffs);
            CompareOptions("runtime", _warmRuntimeOptions, runtimeSetup.Options, new string[0], diffs);
            if (runtimeSetup.DebugMode != _warmDebugMode || runtimeSetup.PrivateBinding != _warmPrivateBinding) {
                diffs.Add("runtime setup");
            }

            // -r: the pool loaded its own list (the prelude) already; more can follow, as they would
            // in a cold start, where they are required after the prelude in the main scope too.
            var poolRequired = GetValue(_warmLanguageOptions, "RequiredPaths") as IList<string> ?? new string[0];
            var required = GetValue(languageSetup.Options, "RequiredPaths") as IList<string> ?? new string[0];
            if (required.Count < poolRequired.Count || !poolRequired.SequenceEqual(required.Take(poolRequired.Count))) {
                diffs.Add("required libraries");
            }

            // -I: directories in front of the pool's load path are put in front of $LOAD_PATH, where a
            // cold start has them - unless one of them has a file the warm-up loaded from elsewhere,
            // since a cold start would have loaded that one (RUBYLIB is an environment variable and
            // has to match anyway).
            var poolPaths = (GetValue(_warmLanguageOptions, "SearchPaths") as IEnumerable)?.Cast<object>().Select(o => (string)o).ToList() ?? new List<string>();
            var paths = (GetValue(languageSetup.Options, "SearchPaths") as IEnumerable)?.Cast<object>().Select(o => (string)o).ToList() ?? new List<string>();
            _warmExtraLoadPaths = null;
            if (paths.Count < poolPaths.Count || !poolPaths.SequenceEqual(paths.Skip(paths.Count - poolPaths.Count))) {
                diffs.Add("load path");
            } else if (paths.Count > poolPaths.Count) {
                _warmExtraLoadPaths = paths.Take(paths.Count - poolPaths.Count).ToList();
                string shadow = WarmStart.FindShadowingFile(_warmExtraLoadPaths);
                if (shadow != null) {
                    diffs.Add("-I directory has " + shadow);
                }
            }

            foreach (object flag in (GetValue(languageSetup.Options, "WarningCategoryFlags") as IEnumerable) ?? new object[0]) {
                if (Array.IndexOf(RubyContext.WarmStartWarningCategories, flag as string) < 0) {
                    diffs.Add("-W:" + flag);
                }
            }

            CompareConsoleOptions(_warmConsoleOptions, options, diffs);
            if (rubyParser != null && rubyParser.NoProgramGiven && _warmStdinIsTerminal) {
                // No program at a terminal is irb (or the program typed at it): .NET looked at descriptor 0
                // once, as the warm-up started (the native terminal state behind Console.ReadLine and
                // ReadKey), so on a terminal the line editor would echo twice.
                diffs.Add("an interactive session");
            }
            if (diffs.Count > 0) {
                return "options differ from the pool's: " + String.Join(", ", diffs);
            }

            languageOptions = languageSetup.Options;
            return null;
        }

        private static void CompareOptions(string/*!*/ what, IDictionary<string, object>/*!*/ pool, IDictionary<string, object>/*!*/ request,
            string[]/*!*/ ignored, List<string>/*!*/ diffs) {
            foreach (var name in pool.Keys.Union(request.Keys)) {
                if (Array.IndexOf(ignored, name) >= 0) {
                    continue;
                }
                if (!ValueEquals(GetValue(pool, name), GetValue(request, name))) {
                    diffs.Add(what + "." + name);
                }
            }
        }

        private static bool ValueEquals(object a, object b) {
            if (ReferenceEquals(a, b)) {
                return true;
            }
            if (a == null || b == null) {
                return false;
            }
            if (a is string || b is string) {
                return a.Equals(b);
            }
            var ea = a as IEnumerable;
            var eb = b as IEnumerable;
            if (ea != null && eb != null) {
                var la = ea.Cast<object>().ToList();
                var lb = eb.Cast<object>().ToList();
                if (la.Count != lb.Count) {
                    return false;
                }
                for (int i = 0; i < la.Count; i++) {
                    if (!ValueEquals(la[i], lb[i])) {
                        return false;
                    }
                }
                return true;
            }
            return a.Equals(b);
        }

        private static void CompareConsoleOptions(ConsoleOptions/*!*/ pool, ConsoleOptions/*!*/ request, List<string>/*!*/ diffs) {
            // The program and where it runs are the request's own, and so is -v's banner in front of a
            // program (alone, -v prints the version and exits: PrintVersion, Exit); everything else has to match.
            var perRequest = new[] { "Command", "FileName", "ChangeDirectory", "RemainingArgs", "DisplayVersion" };
            for (Type type = pool.GetType(); type != null && type != typeof(object); type = type.BaseType) {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                    string name = field.Name.TrimStart('_');
                    if (field.Name.Contains("k__BackingField")) {
                        name = field.Name.Substring(1, field.Name.IndexOf('>') - 1);
                    }
                    if (perRequest.Any(p => String.Equals(p, name, StringComparison.OrdinalIgnoreCase))) {
                        continue;
                    }
                    if (!ValueEquals(field.GetValue(pool), field.GetValue(request))) {
                        diffs.Add("console." + name);
                    }
                }
            }
        }

        private int RunWarmRequest(RubyConsoleOptions/*!*/ options) {
            // what Host.ParseOptions does in a cold start
            if (options.ChangeDirectory != null) {
                Environment.CurrentDirectory = options.ChangeDirectory;
            }
            if (options.DisplayVersion && (options.Command != null || options.FileName != null)) {
                Console.WriteLine(RubyContext.MakeDescriptionString());
            }

            // ConsoleHost.RunCommandLine. A cold start creates the console before the prelude runs,
            // and the prelude's trap("INT", "DEFAULT") then replaces the Ctrl-C handler the console
            // installs (Host.CreateConsole); here the prelude ran first, so its choice is put back.
            var context = (RubyContext)HostingHelpers.GetLanguageContext(Engine);
            Action interruptHandler = context.InterruptSignalHandler;
            CommandLine commandLine = CreateCommandLine();
            IConsole console = CreateConsole(Engine, commandLine, options);
            context.InterruptSignalHandler = interruptHandler;
            if (options.HandleExceptions) {
                try {
                    commandLine.Run(Engine, console, options);
                } catch (Exception e) {
                    UnhandledException(Engine, e);
                }
            } else {
                commandLine.Run(Engine, console, options);
            }
            return commandLine.ExitCode;
        }
    }
}

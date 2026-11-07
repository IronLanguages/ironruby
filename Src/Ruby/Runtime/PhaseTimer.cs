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
using System.Text;

namespace IronRuby.Runtime {
    /// <summary>
    /// IR_PHASE_TIMES=1: exclusive wall time per startup phase (parse, bridge, transform, LightCompile,
    /// binding, ...) on the thread that created the first runtime, printed to stderr at exit. A phase
    /// nested in another is subtracted from it, so the numbers add up to the time measured. Exact
    /// where the sampling profiler only estimates, and costs one branch per phase entry when off.
    /// </summary>
    public static class PhaseTimer {
        public const int Run = 0, PrismNative = 1, PrismLoad = 2, Bridge = 3, Transform = 4, LightCompileTop = 5,
            DefCompile = 6, BindRule = 7, Bind = 8, ClrMember = 9, Precompiled = 10, DefBridge = 11, DefTransform = 12, Count = 13;

        private static readonly string[] _Names = {
            "run (everything else)", "prism native parse", "prism load", "AST bridge", "transform (top level)",
            "LightCompile (top level)", "def body: LightCompile and rest", "bind: rule building", "bind: other (rule compile, caches)",
            "CLR member lookup", "precompiled code load", "def body: AST bridge", "def body: transform"
        };

        public static readonly bool Enabled = Environment.GetEnvironmentVariable("IR_PHASE_TIMES") == "1";

        /// <summary>IR_BIND_TRACE=1: one stderr line per BindDelegate: microseconds, rule kind, binder, receiver type.</summary>
        public static readonly bool BindTrace = Environment.GetEnvironmentVariable("IR_BIND_TRACE") == "1";

        [ThreadStatic]
        internal static string BindKind;

        // inclusive time of outermost binds on the timed thread
        private static int _bindNesting;
        private static long _bindInclusiveTicks, _bindOuterStart;
        private static int _bindOuterCount;

        public static void EnterBindInclusive() {
            if (Enabled && _thread == Environment.CurrentManagedThreadId && _bindNesting++ == 0) {
                _bindOuterStart = Stopwatch.GetTimestamp();
                _bindOuterCount++;
            }
        }

        public static void ExitBindInclusive() {
            if (Enabled && _thread == Environment.CurrentManagedThreadId && --_bindNesting == 0) {
                _bindInclusiveTicks += Stopwatch.GetTimestamp() - _bindOuterStart;
            }
        }

        public static void TraceBind(object binder, object[] args, long start) {
            double us = (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency;
            object target = args.Length > 1 ? args[1] : (args.Length > 0 ? args[0] : null);
            string receiver = target == null ? "nil" : target is IronRuby.Builtins.RubyModule m ? "module " + m.Name : target.GetType().Name;
            Console.Error.WriteLine("[bind] " + us.ToString("F0") + " " + (BindKind ?? "?") + " " + binder + " :: " + receiver);
            BindKind = null;
        }
        private static readonly long[] _ticks = new long[Count];
        private static readonly int[] _counts = new int[Count];
        private static readonly int[] _stack = new int[256];
        private static int _depth;
        private static long _last;
        private static int _thread;
        private static long _start;

        private static bool OnThread {
            get {
                if (_thread == 0) {
                    _thread = Environment.CurrentManagedThreadId;
                    _start = _last = Stopwatch.GetTimestamp();
                    AppDomain.CurrentDomain.ProcessExit += (_, __) => Report();
                }
                return _thread == Environment.CurrentManagedThreadId;
            }
        }

        /// <summary>Returns true if the phase was entered (and Exit must be called).</summary>
        public static bool Enter(int phase) {
            if (!Enabled || !OnThread || _depth >= _stack.Length - 1) {
                return false;
            }
            long now = Stopwatch.GetTimestamp();
            _ticks[_stack[_depth]] += now - _last;
            _last = now;
            _stack[++_depth] = phase;
            _counts[phase]++;
            return true;
        }

        public static void Exit(bool entered) {
            if (!entered) {
                return;
            }
            long now = Stopwatch.GetTimestamp();
            _ticks[_stack[_depth]] += now - _last;
            _last = now;
            _depth--;
        }

        // milliseconds since the process started (Linux: /proc/uptime against /proc/self/stat; 10 ms resolution)
        private static double _preludeStart, _preludeEnd;

        private static double SinceProcessStart() {
            try {
                string stat = System.IO.File.ReadAllText("/proc/self/stat");
                string[] fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
                double startTicks = double.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture);
                double uptime = double.Parse(System.IO.File.ReadAllText("/proc/uptime").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
                return (uptime - startTicks / 100.0) * 1000.0;
            } catch (Exception) {
                return -1;
            }
        }

        public static void MarkPrelude(bool start) {
            if (Enabled) {
                if (start) {
                    _preludeStart = SinceProcessStart();
                } else {
                    _preludeEnd = SinceProcessStart();
                }
            }
        }

        private static void Report() {
            long now = Stopwatch.GetTimestamp();
            _ticks[_stack[_depth]] += now - _last;
            _last = now;
            var sb = new StringBuilder();
            sb.AppendFormat("[phase] wall (10 ms resolution): prelude starts at {0:F0} ms, ends at {1:F0} ms, exit at {2:F0} ms after process start\n",
                _preludeStart, _preludeEnd, SinceProcessStart());
            double total = (now - _start) * 1000.0 / Stopwatch.Frequency;
            sb.AppendFormat("[phase] total {0,8:F1} ms since the first phase\n", total);
            for (int i = 0; i < Count; i++) {
                double ms = _ticks[i] * 1000.0 / Stopwatch.Frequency;
                sb.AppendFormat("[phase] {0,8:F1} ms {1,5:F1}% {2,7} x  {3}\n", ms, 100 * ms / total, _counts[i], _Names[i]);
            }
            sb.AppendFormat("[phase] binding, inclusive of everything under it: {0:F1} ms in {1} outermost binds\n",
                _bindInclusiveTicks * 1000.0 / Stopwatch.Frequency, _bindOuterCount);
            sb.AppendFormat("[phase] JIT: {0} methods, {1:F1} ms; GC: {2} gen0, {3} gen1, {4} gen2; allocated {5:F1} MB\n",
                System.Runtime.JitInfo.GetCompiledMethodCount(false), System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalAllocatedBytes() / 1048576.0);
            Console.Error.Write(sb.ToString());
        }
    }
}

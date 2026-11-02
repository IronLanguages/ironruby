/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace IronRuby.Runtime {

    /// <summary>
    /// IRONRUBY_STARTUP_TRACE=1: where a process's time goes until it exits, written to stderr
    /// at exit. Milestones since the process started; for the Ruby files it loaded, the time
    /// spent parsing (prism), transforming (to the DLR tree), compiling (the interpreter's
    /// instructions) and running each file's own code, without the files it required.
    /// </summary>
    internal static class StartupTrace {
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable("IRONRUBY_STARTUP_TRACE") == "1";

        private static readonly DateTime _processStart = Enabled ? Process.GetCurrentProcess().StartTime : default(DateTime);
        private static readonly List<KeyValuePair<double, string>> _marks = new List<KeyValuePair<double, string>>();
        private static readonly Dictionary<string, long[]> _files = new Dictionary<string, long[]>();
        private static readonly Stack<long[]> _open = new Stack<long[]>();
        private static long _parse, _transform, _compile;
        private static bool _registered;

        internal static void Mark(string what) {
            if (!Enabled) return;
            lock (_marks) {
                _marks.Add(new KeyValuePair<double, string>((DateTime.Now - _processStart).TotalMilliseconds, what));
                Register();
            }
        }

        internal static long Now() {
            return Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        internal static void Parsed(long start) { if (Enabled) Add(ref _parse, 0, start); }
        internal static void Transformed(long start) { if (Enabled) Add(ref _transform, 1, start); }
        internal static void Compiled(long start) { if (Enabled) Add(ref _compile, 2, start); }

        // [parse, transform, compile, inclusive, children]
        private static void Add(ref long total, int slot, long start) {
            long ticks = Stopwatch.GetTimestamp() - start;
            lock (_open) {
                total += ticks;
                if (_open.Count > 0) _open.Peek()[slot] += ticks;
            }
        }

        internal static void BeginFile() {
            if (!Enabled) return;
            lock (_open) { _open.Push(new long[5]); Register(); }
        }

        internal static void EndFile(string path, long start) {
            if (!Enabled) return;
            long ticks = Stopwatch.GetTimestamp() - start;
            lock (_open) {
                long[] own = _open.Pop();
                own[3] = ticks;
                if (_open.Count > 0) _open.Peek()[4] += ticks;
                // a file loaded more than once (Kernel#load) counts every time
                long[] earlier;
                if (_files.TryGetValue(path ?? "?", out earlier)) {
                    for (int i = 0; i < own.Length; i++) own[i] += earlier[i];
                }
                _files[path ?? "?"] = own;
            }
        }

        private static void Register() {
            if (_registered) return;
            _registered = true;
            AppDomain.CurrentDomain.ProcessExit += (sender, e) => Report();
        }

        private static double Ms(long ticks) {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static void Report() {
            var err = Console.Error;
            Mark("exit");
            err.WriteLine("startup trace (ms since the process started):");
            foreach (var mark in _marks) {
                err.WriteLine("  {0,8:F0}  {1}", mark.Key, mark.Value);
            }
            long run = 0;
            foreach (long[] f in _files.Values) {
                run += f[3] - f[4] - f[0] - f[1] - f[2];
            }
            err.WriteLine("{0} Ruby files: parse {1:F0}, transform {2:F0}, compile {3:F0}, run their own code {4:F0} ms",
                _files.Count, Ms(_parse), Ms(_transform), Ms(_compile), Ms(run));
            err.WriteLine("slowest files, own time (parse/transform/compile/run):");
            foreach (var f in _files.OrderByDescending(p => p.Value[3] - p.Value[4]).Take(15)) {
                long[] v = f.Value;
                err.WriteLine("  {0,6:F0}  ({1:F0}/{2:F0}/{3:F0}/{4:F0})  {5}", Ms(v[3] - v[4]), Ms(v[0]), Ms(v[1]), Ms(v[2]),
                    Ms(v[3] - v[4] - v[0] - v[1] - v[2]), f.Key);
            }
        }
    }
}

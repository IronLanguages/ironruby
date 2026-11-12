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

using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Coverage {

    /// <summary>
    /// The primitives under the Coverage module: the measurement itself lives in the runtime
    /// (<see cref="CoverageState"/>, fed by the :line hooks of code compiled while it is set up);
    /// the module's API and its checks are Ruby, in Src/StdLib/ironruby/coverage.rb.
    /// </summary>
    [RubyModule("Coverage")]
    public static class CoverageOps {

        [RubyMethod("__setup__", RubyMethodAttributes.PrivateSingleton)]
        public static void Setup(RubyModule/*!*/ self, int modes) {
            self.Context.Coverage = new CoverageState((CoverageModes)modes);
        }

        [RubyMethod("__resume__", RubyMethodAttributes.PrivateSingleton)]
        public static void Resume(RubyModule/*!*/ self, bool resume) {
            var state = self.Context.Coverage;
            if (state != null) {
                state.Resumed = resume;
            }
        }

        /// <summary>
        /// [[path, lines, oneshot_lines, methods], ...] in the order the files were first measured;
        /// lines holds a count or nil per line of the file, methods one
        /// [class, name, start line, start column, end line, end column, calls] per method defined
        /// in the file while it was measured.
        /// </summary>
        [RubyMethod("__peek__", RubyMethodAttributes.PrivateSingleton)]
        public static RubyArray/*!*/ Peek(RubyModule/*!*/ self) {
            var result = new RubyArray();
            var state = self.Context.Coverage;
            if (state == null) {
                return result;
            }

            foreach (var file in state.GetFiles()) {
                var lines = new RubyArray();
                foreach (var count in file.GetLines()) {
                    lines.Add(count.HasValue ? (object)count.Value : null);
                }

                var oneshot = new RubyArray();
                foreach (var line in file.GetOneshotLines()) {
                    oneshot.Add(line);
                }

                var methods = new RubyArray();
                foreach (var method in file.GetMethods()) {
                    var entry = new RubyArray(7);
                    entry.Add(method.Owner);
                    entry.Add(self.Context.CreateSymbol(method.Name, self.Context.GetIdentifierEncoding()));
                    entry.Add(method.StartLine);
                    entry.Add(method.StartColumn);
                    entry.Add(method.EndLine);
                    entry.Add(method.EndColumn);
                    entry.Add(method.Counts[0]);
                    methods.Add(entry);
                }

                result.Add(RubyOps.MakeArray4(self.Context.EncodePath(file.Path), lines, oneshot, methods));
            }
            return result;
        }

        [RubyMethod("__clear__", RubyMethodAttributes.PrivateSingleton)]
        public static void Clear(RubyModule/*!*/ self) {
            var state = self.Context.Coverage;
            if (state != null) {
                state.Clear();
            }
        }

        /// <summary>
        /// Ends the measurement: code compiled from now on is not measured, and what was compiled
        /// under it no longer counts.
        /// </summary>
        [RubyMethod("__reset__", RubyMethodAttributes.PrivateSingleton)]
        public static void Reset(RubyModule/*!*/ self) {
            var state = self.Context.Coverage;
            if (state != null) {
                state.Resumed = false;
                self.Context.Coverage = null;
            }
        }
    }
}

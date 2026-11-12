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
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Scripting;

namespace IronRuby.Runtime {
    /// <summary>
    /// The process environment as Ruby sees it.
    ///
    /// .NET cannot hold an environment variable whose value is the empty string: handing "" to
    /// Environment.SetEnvironmentVariable deletes the variable instead, and reading it back
    /// answers null. Ruby can hold one, and the difference is visible - `ENV["HOME"] = ""` leaves
    /// HOME set to nothing, which is why File.expand_path("~") then raises ArgumentError rather
    /// than falling back to the user database the way it does when HOME is unset.
    ///
    /// The names set that way are therefore remembered here, and every read of the environment in
    /// the runtime goes through this class so that they are not lost. The set is process-wide,
    /// because the environment is: two runtimes in one process share it.
    ///
    /// Only Unix needs this. On Windows an empty value means "delete this variable" to the OS
    /// itself, and MRI behaves the same way there.
    /// </summary>
    public static class RubyEnvironment {
        private static readonly HashSet<string>/*!*/ _emptyValued = new HashSet<string>(StringComparer.Ordinal);

        private static bool IsUnix {
            get { return Path.DirectorySeparatorChar == '/'; }
        }

        // The platform layer only knows the Windows call for this, and P/Invoking kernel32 on
        // Unix throws DllNotFoundException, so the Unix pair is declared here. Keeping the real
        // environment in step matters for anything that reads environ directly - a child process
        // started through execve, for one.
        [DllImport("libc", EntryPoint = "setenv")]
        private static extern int NativeSetEnv(string/*!*/ name, string/*!*/ value, int overwrite);

        [DllImport("libc", EntryPoint = "unsetenv")]
        private static extern int NativeUnsetEnv(string/*!*/ name);

        public static string GetVariable(PlatformAdaptationLayer/*!*/ platform, string/*!*/ name) {
            lock (_emptyValued) {
                if (_emptyValued.Contains(name)) {
                    return String.Empty;
                }
            }

            return platform.GetEnvironmentVariable(name);
        }

        /// <summary>
        /// A fresh dictionary of the whole environment. The platform layer builds a new one on
        /// every call, so adding to it here is safe.
        /// </summary>
        public static Dictionary<string, string>/*!*/ GetVariables(PlatformAdaptationLayer/*!*/ platform) {
            return AddEmptyValued(platform.GetEnvironmentVariables());
        }

        /// <summary>For callers with no platform layer to hand, such as the spawn path.</summary>
        public static Dictionary<string, string>/*!*/ GetVariables() {
            var result = new Dictionary<string, string>();
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables()) {
                result[(string)entry.Key] = (entry.Value as string) ?? String.Empty;
            }
            return AddEmptyValued(result);
        }

        private static Dictionary<string, string>/*!*/ AddEmptyValued(Dictionary<string, string>/*!*/ variables) {
            lock (_emptyValued) {
                foreach (string name in _emptyValued) {
                    variables[name] = String.Empty;
                }
            }
            return variables;
        }

        public static void SetVariable(PlatformAdaptationLayer/*!*/ platform, string/*!*/ name, string value) {
            if (value != null && value.Length == 0 && IsUnix) {
                NativeSetEnv(name, String.Empty, 1);

                // Whatever .NET is still holding for this name would otherwise shadow the empty
                // value on the way out of GetVariable.
                platform.SetEnvironmentVariable(name, null);

                lock (_emptyValued) {
                    _emptyValued.Add(name);
                }
                return;
            }

            bool wasEmpty;
            lock (_emptyValued) {
                wasEmpty = _emptyValued.Remove(name);
            }

            if (wasEmpty && IsUnix) {
                if (value == null) {
                    NativeUnsetEnv(name);
                } else {
                    NativeSetEnv(name, value, 1);
                }
            }

            platform.SetEnvironmentVariable(name, value);
        }
    }
}

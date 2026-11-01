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
#if FEATURE_FULL_CONSOLE
using System;
using System.IO;
using System.Threading;
using IronRuby;
using IronRuby.Builtins;
using IronRuby.Hosting;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;
using Microsoft.Scripting.Hosting.Shell;
using Microsoft.Scripting.Utils;

namespace IronRuby.Hosting {
    public abstract partial class RubyConsoleHost : ConsoleHost {
        protected RubyConsoleHost() {
            SetHomeEnvironmentVariable();
        }

        protected override Type Provider {
            get { return typeof(RubyContext); }
        }

        protected override CommandLine/*!*/ CreateCommandLine() {
            return new RubyCommandLine();
        }

        protected override OptionsParser/*!*/ CreateOptionsParser() {
            return new RubyOptionsParser();
        }

        protected override LanguageSetup CreateLanguageSetup() {
            return Ruby.CreateRubySetup();
        }

        protected override ConsoleOptions ParseOptions(string[] args, ScriptRuntimeSetup runtimeSetup, LanguageSetup languageSetup) {
            languageSetup.Options["ApplicationBase"] = AppDomain.CurrentDomain.BaseDirectory;
            runtimeSetup.HostType = typeof(RubyScriptHost);
            ConsoleOptions result = base.ParseOptions(args, runtimeSetup, languageSetup);
            if (result != null && WarmStart.IsWorker) {
                SnapshotWarmOptions(runtimeSetup, languageSetup, result);
            }
            return result;
        }

        /// <summary>
        /// The host the runtime asks for its PlatformAdaptationLayer, so that source files
        /// are opened the way MRI opens a script: readable by anyone and deletable while
        /// open. The DLR's default opens them FileShare.Read, which on Windows makes the
        /// file undeletable until the stream is collected - and a Ruby program that writes a
        /// script, loads it and then removes it (every ruby/spec code-loading example does)
        /// gets Errno::EACCES for its trouble. Unix never noticed because unlink(2) there
        /// does not care who has the file open.
        /// </summary>
        private sealed class RubyScriptHost : ScriptHost {
            public override PlatformAdaptationLayer PlatformAdaptationLayer {
                get { return SharedPlatform; }
            }
        }

        private static readonly PlatformAdaptationLayer/*!*/ SharedPlatform = new RubyPlatformAdaptationLayer();

        private sealed class RubyPlatformAdaptationLayer : PlatformAdaptationLayer {
            public override Stream OpenInputFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) {
                return base.OpenInputFileStream(path, mode, access, share | FileShare.ReadWrite | FileShare.Delete, bufferSize);
            }
        }

        private static void SetHomeEnvironmentVariable() {
            try {
                PlatformAdaptationLayer platform = PlatformAdaptationLayer.Default;
                string homeDir = RubyUtils.GetHomeDirectory(platform);
                platform.SetEnvironmentVariable("HOME", homeDir);
            } catch (System.Security.SecurityException) {
                // Ignore EnvironmentPermission exception
            }
        }
    }
}
#endif
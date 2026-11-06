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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;

namespace IronRuby.Tests {
    public class TestCase {
        public Action TestMethod { get; set; }
        public OptionsAttribute Options { get; set; }
        public string Name { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RunAttribute : Attribute {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class OptionsAttribute : Attribute {
        public bool PrivateBinding { get; set; }
        public bool NoRuntime { get; set; }
        public Type Pal { get; set; }
    }

    public class TestRuntime {
        public string/*!*/ TestName { get; }
        public ScriptEngine Engine { get; }
        public ScriptRuntime ScriptRuntime { get; }
        public RubyContext Context { get; }

        public TestRuntime(Driver/*!*/ driver, TestCase/*!*/ testCase) {
            TestName = testCase.Name;

            // The front end is a process-wide static; every test parses with prism, as ir does.
            RubyContext.AlternativeParser = IronRuby.Prism.PrismAstBridge.Parse;

            if (testCase.Options.NoRuntime) {
                return;
            }

            var runtimeSetup = ScriptRuntimeSetup.ReadConfiguration();
            var languageSetup = runtimeSetup.AddRubySetup();

            runtimeSetup.DebugMode = driver.IsDebug;
            runtimeSetup.PrivateBinding = testCase.Options.PrivateBinding;
            runtimeSetup.HostType = typeof(TestHost);
            runtimeSetup.HostArguments = new object[] { testCase.Options };
            languageSetup.Options["ApplicationBase"] = driver.BaseDirectory;
            languageSetup.Options["NoAdaptiveCompilation"] = driver.NoAdaptiveCompilation;
            languageSetup.Options["CompilationThreshold"] = driver.CompilationThreshold;
            languageSetup.Options["Verbosity"] = 2;

            ScriptRuntime = Ruby.CreateRuntime(runtimeSetup);
            Engine = Ruby.GetEngine(ScriptRuntime);
            Context = (RubyContext)HostingHelpers.GetLanguageContext(Engine);
        }
    }

    public class TestHost : ScriptHost {
        private readonly PlatformAdaptationLayer/*!*/ _pal;

        public TestHost(OptionsAttribute/*!*/ options) {
            _pal = options.Pal != null ?
                (PlatformAdaptationLayer)Activator.CreateInstance(options.Pal) :
                SharedPlatform;
        }

        public override PlatformAdaptationLayer PlatformAdaptationLayer => _pal;

        private static readonly PlatformAdaptationLayer/*!*/ SharedPlatform = new TestPlatformAdaptationLayer();

        /// <summary>
        /// The same platform layer RubyConsoleHost installs for `ir`: loaded files are opened
        /// FileShare.ReadWrite | Delete. The DLR's default opens them FileShare.Read, and on
        /// Windows that makes the file undeletable while the stream is alive - so every test
        /// that requires a temp file and then deletes it (Autoload1,
        /// ConstantCaching_AutoUpdating1A/B) failed in Dispose with "the process cannot access
        /// the file", on Windows only. Unix never noticed: unlink(2) does not care who has the
        /// file open.
        /// </summary>
        private sealed class TestPlatformAdaptationLayer : PlatformAdaptationLayer {
            public override Stream OpenInputFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) {
                return base.OpenInputFileStream(path, mode, access, share | FileShare.ReadWrite | FileShare.Delete, bufferSize);
            }
        }
    }

    public class Driver {
        private readonly record struct FailedAssertion(string TestName, StackFrame Frame, string Message);
        private readonly record struct UnexpectedException(string TestName, Exception Exception);

        private readonly List<FailedAssertion>/*!*/ _failedAssertions = new();
        private readonly List<UnexpectedException>/*!*/ _unexpectedExceptions = new();
        private Tests _tests;

        private bool _excludeSelectedCases;
        private bool _verbose;
        private bool _displayList;

        public Driver(string/*!*/ baseDirectory) {
            BaseDirectory = baseDirectory;
        }

        public TestRuntime TestRuntime { get; private set; }
        public string/*!*/ BaseDirectory { get; }
        public bool IsDebug { get; private set; }
        public bool NoAdaptiveCompilation { get; private set; }

        // 0 (compile every method before its first run) unless /sync1 or /interpret says otherwise.
        public int CompilationThreshold { get; private set; }

        /// <summary>
        /// Takes the options out of args and leaves the names of the tests to run (or exclude).
        /// Returns false if the driver has nothing more to do (/help).
        /// </summary>
        private bool ParseArguments(List<string>/*!*/ args) {
            if (args.Contains("/help") || args.Contains("-?") || args.Contains("/?") || args.Contains("-help")) {
                Console.WriteLine("Verbose                      : /verbose");
                Console.WriteLine("No adaptive compilation      : /noadaptive       (-X:NoAdaptiveCompilation)");
                Console.WriteLine("Synchronous compilation      : /sync0            (-X:CompilationThreshold 0, the default)");
                Console.WriteLine("Synchronous compilation      : /sync1            (-X:CompilationThreshold 1)");
                Console.WriteLine("Interpret only               : /interpret        (-X:CompilationThreshold Int32.MaxValue)");
                Console.WriteLine("Debug Mode                   : /debug            (-D)");
                Console.WriteLine("Run Specific Tests           : [/exclude] [test_to_run ...]");
                Console.WriteLine("List Tests                   : /list");
                return false;
            }

            bool Take(params string[] names) {
                bool found = false;
                foreach (var name in names) {
                    found |= args.Remove(name);
                }
                return found;
            }

            _displayList = Take("/list");
            _verbose = Take("/verbose");
            IsDebug = Take("/debug", "-D");
            NoAdaptiveCompilation = Take("/noadaptive", "-X:NoAdaptiveCompilation");
            if (Take("/sync0")) CompilationThreshold = 0;
            if (Take("/sync1")) CompilationThreshold = 1;
            if (Take("/interpret")) CompilationThreshold = Int32.MaxValue;
            _excludeSelectedCases = Take("/exclude");
            return true;
        }

        public static void Main(string[]/*!*/ arguments) {
            string culture = Environment.GetEnvironmentVariable("IR_CULTURE");
            if (!String.IsNullOrEmpty(culture)) {
                Thread.CurrentThread.CurrentCulture = new CultureInfo(culture, false);
            }
            Environment.ExitCode = Run(new List<string>(arguments), AppContext.BaseDirectory);
        }

        public static int Run(List<string>/*!*/ args, string/*!*/ baseDirectory) {
            if (Thread.CurrentThread.CurrentCulture.ToString() != "en-US") {
                Console.WriteLine("Current culture: {0}", Thread.CurrentThread.CurrentCulture);
            }

            var driver = new Driver(baseDirectory);
            if (!driver.ParseArguments(args)) {
                return 0;
            }

            return driver.RunUnitTests(args);
        }

        private int RunUnitTests(List<string>/*!*/ largs) {
            _tests = new Tests(this);

            if (_displayList) {
                foreach (var m in _tests.TestMethods) {
                    Console.WriteLine(m.Method.Name);
                }
                return -1;
            }

            var selectedCases = new List<TestCase>();

            // a [Run] attribute on a test preselects it:
            foreach (var m in _tests.TestMethods) {
                if (m.Method.IsDefined(typeof(RunAttribute), false)) {
                    AddTestCases(selectedCases, m);
                }
            }

            if (selectedCases.Count == 0) {
                foreach (var m in _tests.TestMethods) {
                    if (largs.Count == 0 || largs.Contains(m.Method.Name) != _excludeSelectedCases) {
                        AddTestCases(selectedCases, m);
                    }
                }
            } else if (largs.Count > 0) {
                Console.WriteLine("Arguments overrided by Run attribute.");
            }

            foreach (TestCase testCase in selectedCases) {
                RunTestCase(testCase);
            }

            var failedCases = new List<string>();
            foreach (var (test, frame, message) in _failedAssertions) {
                failedCases.Add(test);
                Console.Error.WriteLine();
                ColorWrite(ConsoleColor.Red, "{0}) {1} {2} : {3}", failedCases.Count, test, frame.GetFileName(), frame.GetFileLineNumber());
                Console.Error.WriteLine(message);
            }

            foreach (var (test, exception) in _unexpectedExceptions) {
                Console.Error.WriteLine();
                ColorWrite(ConsoleColor.Red, "{0}) {1} (unexpected exception)", failedCases.Count, test);
                Console.Error.WriteLine(exception);
                failedCases.Add(test);
            }

            if (failedCases.Count == 0) {
                ColorWrite(ConsoleColor.Green, "PASSED");
            } else {
                Console.WriteLine();
                Console.Write("Repro: {0}", Environment.CommandLine);
                if (largs.Count == 0) {
                    Console.Write(" {0}", String.Join(" ", failedCases));
                }
                Console.WriteLine();
            }
            return failedCases.Count;
        }

        private static void AddTestCases(List<TestCase>/*!*/ cases, Action/*!*/ testMethod) {
            var attrs = testMethod.Method.GetCustomAttributes(typeof(OptionsAttribute), false).Cast<OptionsAttribute>().ToArray();
            foreach (var options in attrs.Length > 0 ? attrs : new[] { new OptionsAttribute() }) {
                cases.Add(new TestCase {
                    Name = testMethod.Method.Name,
                    TestMethod = testMethod,
                    Options = options,
                });
            }
        }

        private void RunTestCase(TestCase/*!*/ testCase) {
            TestRuntime = new TestRuntime(this, testCase);

            if (_verbose) {
                Console.WriteLine("Executing {0}", testCase.Name);
            } else {
                Console.Write('.');
            }

            try {
                testCase.TestMethod();
            } catch (Exception e) {
                PrintTestCaseFailed();
                _unexpectedExceptions.Add(new UnexpectedException(testCase.Name, e));
            }
        }

        private void PrintTestCaseFailed() {
            ColorWrite(ConsoleColor.Red, "\n> FAILED: {0}", TestRuntime.TestName);
        }

        internal static void ColorWrite(ConsoleColor color, string/*!*/ str, params object[] args) {
            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(str, args);
            Console.ForegroundColor = oldColor;
        }

        [DebuggerHidden]
        internal void AssertionFailed(string/*!*/ msg) {
            // the first frame that is not an assertion helper:
            var trace = new StackTrace(true);
            StackFrame frame = null;
            for (int i = 0; i < trace.FrameCount; i++) {
                frame = trace.GetFrame(i);
                if (!frame.GetMethod().IsDefined(typeof(DebuggerHiddenAttribute), true)) {
                    break;
                }
            }

            Debug.Assert(frame != null);

            _failedAssertions.Add(new FailedAssertion(TestRuntime.TestName, frame, msg));
            PrintTestCaseFailed();
        }

        internal string/*!*/ MakeTempDir() {
            string dir = Path.Combine(Path.GetTempPath(), TestRuntime.TestName);
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal TempFile/*!*/ MakeTempFile(string/*!*/ globalVariableName, string/*!*/ suffix, string/*!*/ content) {
            var fileName = MakeTempFile(suffix, content);
            TestRuntime.Context.DefineGlobalVariable(globalVariableName, fileName);
            return new TempFile(fileName);
        }

        internal static string/*!*/ MakeTempFile(string/*!*/ suffix, string/*!*/ content) {
            string path = Path.Combine(Path.GetTempPath(), $"IR_{Environment.ProcessId}_{Guid.NewGuid():N}{suffix}");
            using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew))) {
                writer.Write(content);
            }
            return path;
        }

        internal sealed class TempFile : IDisposable {
            private readonly string/*!*/ _file;

            public TempFile(string/*!*/ file) {
                Debug.Assert(file != null);
                _file = file;
            }

            public void Dispose() {
                File.Delete(_file);
            }
        }
    }
}

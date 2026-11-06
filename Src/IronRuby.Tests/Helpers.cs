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
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Builtins;

namespace IronRuby.Tests {

    record ErrorLogRecord(string Message, SourceSpan Span, int Code, Severity Severity);

    /// <summary>
    /// Counts and records the parse errors and warnings of a test's code, without printing them.
    /// </summary>
    class LoggingErrorSink : ErrorCounter {
        public readonly List<ErrorLogRecord> Errors = new();

        public override void Add(SourceUnit source, string/*!*/ message, SourceSpan span, int errorCode, Severity severity) {
            CountError(severity);
            Errors.Add(new ErrorLogRecord(message, span, errorCode, severity));
        }
    }

    public class TestHelpers {
        public static int GetClassVersion([NotNull]RubyClass/*!*/ cls) {
            return cls.Version.Method;
        }
    }

    public partial class Tests {
        private readonly Driver/*!*/ _driver;
        private readonly Action[]/*!*/ _methods;

        public Action[] TestMethods { get { return _methods; } }
        public ScriptRuntime Runtime { get { return _driver.TestRuntime.ScriptRuntime; } }
        public ScriptEngine Engine { get { return _driver.TestRuntime.Engine; } }
        public RubyContext Context { get { return _driver.TestRuntime.Context; } }
        
        public string/*!*/ Eval(bool eval, string/*!*/ code) {
            if (eval) {
                return code.Replace("#<","eval %q{").Replace("#>", "}");
            } else {
                return code.Replace("#<", "").Replace("#>", "");
            }
        }

        public void UsingEval(Action<bool>/*!*/ test) {
            test(true);
            test(false);
        }

        public void LoadTestLibrary() {
            Context.ObjectClass.SetConstant("TestHelpers", Context.GetClass(typeof(TestHelpers)));
        }

        public void CompilerTest(string/*!*/ code) {
            CompilerTest(code, 0, 0);
        }

        public void CompilerTest(string/*!*/ code, int expectedCompilerWarningCount, int expectedRuntimeWarningCount) {
            var log = new LoggingErrorSink();
            CompilerTest(code, log);
            Assert(log.ErrorCount == 0 && log.FatalErrorCount == 0, "Compile time error: " + DescribeLog(log));
            Assert(log.WarningCount == expectedCompilerWarningCount, String.Format(
                "Wrong number of compile time errors/warnings: expected {0}, got {1}: {2}",
                expectedCompilerWarningCount, log.WarningCount, DescribeLog(log)));
            Assert(Context.RuntimeErrorSink.WarningCount == expectedRuntimeWarningCount, "Wrong number of runtime warnings");
        }

        private static string/*!*/ DescribeLog(LoggingErrorSink/*!*/ log) {
            return String.Join("; ", log.Errors.Select(e => $"{e.Severity} at {e.Span.Start.Line}: {e.Message}"));
        }

        public void CompilerTest(string/*!*/ code, ErrorSink/*!*/ sink) {
            Debug.Assert(code != null && sink != null);
            string name = _driver.TestRuntime.TestName;
            SourceUnit source = _driver.TestRuntime.Context.CreateSnippet(code, name + ".rb", SourceCodeKind.File);

            ScriptCode compiledCode = source.Compile(new RubyCompilerOptions(), sink);
            if (compiledCode != null) {
                compiledCode.Run(new Scope());
            }
        }

        [Flags]
        enum OutputFlags {
            None = 0,
            Raw = 1,
            Match = 2
        }

        [DebuggerHiddenAttribute]
        private void AssertEquals<T>(object actual, T expected)
            where T : IEquatable<T> {
            Assert(actual is T && ((T)actual).Equals(expected));
        }

        [DebuggerHiddenAttribute]
        private void TestOutput(string code, string expectedOutput) {
            AssertOutput(() => CompilerTest(code), expectedOutput);
        }

        /// <summary>
        /// The tests run with warnings on (Verbosity 2, ruby -w), so the parse warnings are the ones
        /// `ruby -wc` reports for the same code.
        /// </summary>
        [DebuggerHiddenAttribute]
        private void TestOutput(string code, string expectedOutput, int expectedCompilerWarningCount) {
            AssertOutput(() => CompilerTest(code, expectedCompilerWarningCount, 0), expectedOutput);
        }

        [DebuggerHiddenAttribute]
        private void TestOutput(string code, string expectedOutput, int expectedCompilerWarningCount, int expectedRuntimeWarningCount) {
            AssertOutput(() => CompilerTest(code, expectedCompilerWarningCount, expectedRuntimeWarningCount), expectedOutput);
        }

        /// <summary>
        /// TestOutput for code that calls using_clr_extensions, which reports once per runtime that it is deprecated:
        /// once in this test, which runs in a runtime of its own.
        /// </summary>
        [DebuggerHiddenAttribute]
        private void LegacyExtensionsTestOutput(string code, string expectedOutput) {
            TestOutput(code, expectedOutput, 0, 1);
        }

        [DebuggerHiddenAttribute]
        private void TestOutputWithEval(string code, string expectedOutput) {
            UsingEval((eval) => AssertOutput(() => CompilerTest(Eval(eval, code)), expectedOutput));
        }

        [DebuggerHiddenAttribute]
        private void TestOutputWithEval(string code, string expectedOutput, int expectedCompilerWarningCount) {
            UsingEval((eval) => AssertOutput(() => CompilerTest(Eval(eval, code), expectedCompilerWarningCount, 0), expectedOutput));
        }

        [DebuggerHiddenAttribute]
        private void AssertOutput(Action f, string expectedOutput) {
            AssertOutput(f, expectedOutput, OutputFlags.None);
        }

        [DebuggerHiddenAttribute]
        private void AssertOutput(Action f, string expectedOutput, OutputFlags flags) {
            var error = CompareOutput(f, expectedOutput, flags);
            if (error != null) {
                Assert(false, error);
            }
        }

        private string CompareOutput(Action f, string expectedOutput, OutputFlags flags) {
            StringBuilder builder = new StringBuilder();

            using (StringWriter output = new StringWriter(builder)) {
                RedirectOutput(output, f);
            }

            string actualOutput = builder.ToString();

            if ((flags & OutputFlags.Raw) == 0) {
                actualOutput = actualOutput.Trim().Replace("\r", "");
                expectedOutput = expectedOutput.Trim().Replace("\r", "");
            }

            if ((flags & OutputFlags.Match) != 0) {
                Regex regex = new Regex(Regex.Escape(expectedOutput).Replace("\\*", ".*").Replace("\\?", "."));
                if (!regex.IsMatch(actualOutput)) {
                    return String.Format("Unexpected output: \n\n'{0}'.", actualOutput);
                }
            } else {
                int i = 0;
                while (i < actualOutput.Length && i < expectedOutput.Length && actualOutput[i] == expectedOutput[i]) i++;

                if (actualOutput != expectedOutput) {
                    return String.Format("Unexpected output: \n\n'{0}'.\n\nFirst difference ({1}):\nactual = '{2}'\nexpected = '{3}'\n",
                        Escape(builder), i,
                        (i < actualOutput.Length ? Escape(actualOutput[i]) : "<end>"),
                        (i < expectedOutput.Length ? Escape(expectedOutput[i]) : "<end>")
                    );
                }
            }

            return null;
        }

        private static string Escape(char ch) {
            return ch.ToString().Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string Escape(string str) {
            return str.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string Escape(StringBuilder str) {
            return str.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t").ToString();
        }

        [DebuggerHidden]
        private void RedirectOutput(TextWriter/*!*/ output, Action f) {
            // TODO:
            if (Runtime == null) {
                f();
            } else {
                MemoryStream stream = new MemoryStream();
                Runtime.IO.SetOutput(stream, Encoding.Default);
                Runtime.IO.SetErrorOutput(Console.OpenStandardError(), Console.Error);

                try {
                    f();
                } finally {
                    output.Write(Encoding.Default.GetString(stream.ToArray()));
                    Runtime.IO.RedirectToConsole();
                }
            }
        }

        [DebuggerHidden]
        public void AssertExceptionThrown<T>(Action f) where T : Exception {
            AssertExceptionThrown<T>(f, null);
        }

        [DebuggerHidden]
        public void AssertExceptionThrown<T>(Action f, Predicate<T> condition) where T : Exception {
            try {
                RedirectOutput(TextWriter.Null, f);
            } catch (T e) {
                if (condition != null) {
                    Assert(condition(e), "Exception has been thrown but the condition doesn't hold");
                }
                return;
            } catch (Exception e) {
                Assert(false, "Expecting exception '" + typeof(T) + "', got '" + e.GetType() + "'.");
            }

            Assert(false, "Expecting exception '" + typeof(T) + "'.");
        }

        /// <summary>
        /// Asserts two values are equal
        /// </summary>
        [DebuggerHidden]
        public void AreEqual(object x, object y) {
            if (x == null && y == null) return;

            Assert(x != null && x.Equals(y), String.Format("values aren't equal: {0} and {1}", x, y));
        }
        
        /// <summary>
        /// Asserts two sets are equal.
        /// </summary>
        public void AreSetsEqual<T>(IEnumerable<T> x, IEnumerable<T> y) {
            if (x == null && y == null) return;

            var set = new HashSet<T>(x);
            set.SymmetricExceptWith(y);
            Assert(set.Count == 0);
        }
        
        /// <summary>
        /// Asserts two bags (lists with insignificant order) are equal.
        /// </summary>
        public void AreBagsEqual<T>(IEnumerable<T> x, IEnumerable<T> y) {
            if (x == null && y == null) return;

            var bag = new HashSet<(T, int)>(GroupWithCount(x));
            bag.SymmetricExceptWith(GroupWithCount(y));
            Assert(bag.Count == 0);
        }

        private static IEnumerable<(T, int)> GroupWithCount<T>(IEnumerable<T> sequence) {
            return sequence.GroupBy(item => item).Select(group => (group.Key, group.Count()));
        }

        /// <summary>
        /// Asserts an condition it true
        /// </summary>
        [DebuggerHidden]
        public void Assert(bool condition, string msg) {
            if (!condition) {
                AssertBreak();
                _driver.AssertionFailed(msg);
            }
        }

        [DebuggerHidden]
        public void Assert(bool condition) {
            Assert(condition, "Assertion failed");
        }

        internal void AssertBreak() {
            if (Debugger.IsAttached) {
                Debugger.Break();
            }
        }
    }
}

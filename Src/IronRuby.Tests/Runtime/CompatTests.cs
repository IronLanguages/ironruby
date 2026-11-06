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
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IronRuby.Tests {

    /// <summary>
    /// Cross-product tests, ported from the generated Tests/Legacy/Compat suite.  Each one generates a script that
    /// tries every combination of a handful of constructs and prints a trace; the trace must be the one CRuby 4.0
    /// prints for the same script, which is pinned by its line count and SHA-256 (the traces run to thousands of lines).
    /// On a mismatch the script and IronRuby's output are written to a temp folder, to diff against `ruby script.rb`.
    /// </summary>
    public partial class Tests {

        private void AssertCompatOutput(string/*!*/ name, string/*!*/ script, int expectedLines, string/*!*/ expectedSha256) {
            var builder = new StringBuilder();
            var log = new LoggingErrorSink();
            using (var output = new StringWriter(builder)) {
                RedirectOutput(output, () => CompilerTest(script, log));
            }
            Assert(log.ErrorCount == 0 && log.FatalErrorCount == 0, "Compile time error: " + DescribeLog(log));

            string actual = builder.ToString().Replace("\r", "");
            int lines = actual.Split('\n').Length - 1;
            string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual))).ToLowerInvariant();
            if (lines != expectedLines || sha != expectedSha256) {
                string dir = Path.Combine(Path.GetTempPath(), "IronRuby.Tests.Compat");
                Directory.CreateDirectory(dir);
                string scriptPath = Path.Combine(dir, name + ".rb");
                string outputPath = Path.Combine(dir, name + ".ironruby.txt");
                File.WriteAllText(scriptPath, script);
                File.WriteAllText(outputPath, actual);
                Assert(false, String.Format(
                    "{0}: got {1} lines (sha256 {2}), CRuby 4.0 prints {3} lines (sha256 {4}). Compare: ruby {5} | diff - {6}",
                    name, lines, sha, expectedLines, expectedSha256, scriptPath, outputPath));
            }
        }

        /// <summary>
        /// Numbers each "B" in the text 1, 2, 3, ... so that the trace says which statements ran.
        /// </summary>
        private static string NumberMarkers(string/*!*/ text) {
            var result = new StringBuilder();
            int count = 0;
            foreach (char c in text) {
                if (c == 'B') {
                    result.Append(++count);
                } else {
                    result.Append(c);
                }
            }
            return result.ToString();
        }

        /// <summary>
        /// Names each "A" in the text a, b, c, ... and returns the names, comma-separated.
        /// </summary>
        private static string NameVariables(string/*!*/ text, out string/*!*/ names) {
            var result = new StringBuilder();
            var list = new StringBuilder();
            char next = 'a';
            foreach (char c in text) {
                if (c == 'A') {
                    if (list.Length > 0) list.Append(',');
                    list.Append(next);
                    result.Append(next++);
                } else {
                    result.Append(c);
                }
            }
            names = list.ToString();
            return result.ToString();
        }

        #region Control flow out of blocks, procs and lambdas

        private static readonly string[] _blockConsumers = (
            "iterator_via_yield iterator_via_call method_call_iterator_via_yield method_call_iterator_via_call " +
            "method_use_lambda_and_yield method_use_proc_and_yield method_use_lambda_and_call method_use_proc_and_call " +
            "method_use_lambda_and_yield_2 method_yield_in_loop method_call_in_loop"
        ).Split(' ');

        private const string BlockBody = "{ puts B; ctrl_flow; puts B}";

        private static readonly string[] _blockProducers = new[] {
            "lambda" + BlockBody, "Proc.new" + BlockBody, "get_block" + BlockBody, "get_lambda" + BlockBody, "get_proc" + BlockBody,
            "get_local_block", "get_local_lambda", "get_local_proc"
        };

        /// <summary>
        /// Runs the statement <paramref name="ctrl"/> in a block created in place, as a lambda or a Proc, locally or
        /// by a method, and called by yield, Proc#call, from a lambda, a Proc, a for loop or a deeper call stack, at
        /// the top of a method or in a method defined inside it.
        /// </summary>
        private static string/*!*/ BlockControlFlowScript(string/*!*/ ctrl) {
            var s = new StringBuilder(@"
def myeval(line); puts B; eval(line); puts B; end
def call1(x); puts B; call2(x); puts B; end
def call2(x); puts B; call3(x); puts B; end
def call3(x); puts B; puts x.call; puts B; end

def get_block(&p);    p;                end
def get_lambda(&p);   lambda(&p);       end
def get_proc(&p);     Proc.new(&p);     end

def get_local_block;        get_block { puts B; ctrl_flow; puts B };    end
def get_local_lambda;       lambda { puts B; ctrl_flow; puts B };       end
def get_local_proc;         Proc.new { puts B; ctrl_flow; puts B };     end

def iterator_via_yield;                     puts B; x = yield; puts x; puts B;     end
def iterator_via_call(&p);                  puts B; puts(p.call); puts B;   end

def method_call_iterator_via_yield(&p);     puts B; iterator_via_yield(&p); puts B;     end
def method_call_iterator_via_call(&p);      puts B; iterator_via_call(&p); puts B;      end

def method_use_lambda_and_yield;            puts B; x = lambda { puts B; yield; puts B }; puts x.call; puts B; end
def method_use_proc_and_yield;              puts B; x = Proc.new { puts B; yield; puts B }; puts x.call; puts B; end
def method_use_lambda_and_call(&p);         puts B; x = lambda { puts B; p.call; puts B }; puts x.call; puts B; end
def method_use_proc_and_call(&p);           puts B; x = Proc.new { puts B; p.call; puts B }; puts x.call; puts B; end

def method_use_lambda_and_yield_2;          puts B; x = lambda { puts B; yield; puts B }; call1(x); puts B; end

def method_yield_in_loop;                   puts B; for i in [1, 2]; puts B; yield; puts B; end; puts B; end
def method_call_in_loop(&p);                puts B; for i in [3, 4]; puts B; p.call; puts B; end; puts B; end
");
            const string Rescue = "; rescue; puts B; puts $!.class; end";
            int i = 0;

            // created in place
            s.Append("def test\n");
            foreach (var x in _blockConsumers) {
                string t = "$g = 0; begin; puts B; " + x + " " + BlockBody + "; puts B" + Rescue;
                i++;
                s.Append(t).Append('\n');
                s.Append("$g = 0; def m_" + i + "; puts B; " + t + "; puts B; end; m_" + i + "\n");
            }
            s.Append("end\ntest\n");

            // created locally or by a method
            foreach (var x in _blockProducers) {
                s.Append("def test\n");
                foreach (var y in _blockConsumers) {
                    s.Append("$g = 0; begin; p = " + x + "; puts B; " + y + "(&p); puts B" + Rescue + "\n");
                    i++;
                    s.Append("$g = 0; def m_" + i + "; p = " + x + "; puts B; " + y + "(&p); puts B; end;\n");
                    s.Append("begin; puts B; m_" + i + "; puts B" + Rescue + "\n");
                }
                s.Append("end\ntest\n");
            }

            // called directly, from a method, and from a method's caller
            s.Append("def test\n");
            foreach (var x in _blockProducers) {
                s.Append("$g = 0; begin; puts B; p = " + x + "; puts(p.call); puts B" + Rescue + "\n");
                i++;
                s.Append("$g = 0; def m_" + i + "; puts B; p = " + x + "; puts(p.call); puts B; end;\n");
                s.Append("begin; puts B; m_" + i + "; puts B" + Rescue + "\n");
                s.Append("$g = 0; begin; puts B; puts m_" + i + "; puts B" + Rescue + "\n");
                i++;
                s.Append("$g = 0; def m_" + i + "; puts B; puts m_" + (i - 1) + "; puts B; end;\n");
                s.Append("begin; puts B; m_" + i + "; puts B" + Rescue + "\n");
            }
            s.Append("end\ntest\n");

            // called inside a lambda, a Proc or a block
            foreach (var x in new[] { "lambda", "Proc.new", "get_block", "get_lambda", "get_proc" }) {
                s.Append("def test\n");
                foreach (var y in _blockProducers) {
                    string t = "x = " + x + " { puts B; p = " + y + "; puts p.call; puts B}; puts x.call; puts B";
                    s.Append("$g = 0; begin; puts B; " + t + Rescue + "\n");
                    i++;
                    s.Append("$g = 0; def m_" + i + "; puts B; " + t + "; end;\n");
                    s.Append("begin; puts B; m_" + i + "; puts B" + Rescue + "\n");
                }
                s.Append("end\ntest\n");
            }

            // called inside a for loop
            s.Append("def test\n");
            foreach (var x in _blockProducers) {
                string t = "for i in [1, 2]; puts B; p = " + x + "; puts p.call; puts B; end; puts B";
                s.Append("$g = 0; begin; puts B; " + t + Rescue + "\n");
                i++;
                s.Append("$g = 0; def m_" + i + "; puts B; " + t + "; end;\n");
                s.Append("begin; puts B; m_" + i + "; puts B" + Rescue + "\n");
            }
            s.Append("end\ntest\n");

            return NumberMarkers(s.ToString().Replace("ctrl_flow", ctrl));
        }

        public void Compat_BlockControlFlow_Normal() {
            AssertCompatOutput("block_ctrl_flow_normal", BlockControlFlowScript("puts \"A\""),
                3276, "dae06d8992a0b9596499c7d56de3d10efc990bbef8626ccd13d5be133c4dae35");
        }

        public void Compat_BlockControlFlow_Raise() {
            AssertCompatOutput("block_ctrl_flow_raise", BlockControlFlowScript("raise IOError"),
                1868, "3dec7735bcd8283b4d649b4159734eac9468c1365babb45462278efdcc611949");
        }

        public void Compat_BlockControlFlow_Return() {
            AssertCompatOutput("block_ctrl_flow_return", BlockControlFlowScript("return"),
                1042, "43660039fe2b4aa0fe966fa9de7f5db0f20ffdf8a207bd7a2f067161446177f2");
        }

        public void Compat_BlockControlFlow_ReturnValue() {
            AssertCompatOutput("block_ctrl_flow_return_value", BlockControlFlowScript("return 41"),
                1042, "3f9bb622254d87861145192284b1fa0f05a0311aba778baf41a36cf1b265c844");
        }

        public void Compat_BlockControlFlow_Break() {
            AssertCompatOutput("block_ctrl_flow_break", BlockControlFlowScript("break"),
                2050, "14cf2410ec3fc645ec77669f9bf23acb528100dc52b69de8164a36fc2e961ea6");
        }

        public void Compat_BlockControlFlow_BreakValue() {
            AssertCompatOutput("block_ctrl_flow_break_value", BlockControlFlowScript("break 42"),
                2050, "9cf0b575c596829f21192fbf183d1703011987a9954e13a7f9650b1b235c82ba");
        }

        public void Compat_BlockControlFlow_Next() {
            AssertCompatOutput("block_ctrl_flow_next", BlockControlFlowScript("next"),
                2636, "1458a1c3b1741ed36b616e869f4c608a812e09ba6bc43c2b294f782d5c7511d6");
        }

        public void Compat_BlockControlFlow_Redo() {
            AssertCompatOutput("block_ctrl_flow_redo", BlockControlFlowScript("$g += 1; redo if $g < 4;"),
                3778, "e6bf09512c838a9d01cca2a6cd511bfc09be9065292194d2245b9f296a07edcc");
        }

        #endregion

        #region Assignment and argument passing

        private const string CompatPrint = @"
def p *a
  a.each { |x| print x.inspect, '; ' }
  puts
end
";

        /// <summary>
        /// Every left-hand side of a (multiple) assignment against every right-hand side.
        /// </summary>
        public void Compat_Assignment() {
            var lefts = "A A,A *A A,*A (A,) (A,A) (A,*A) (*A) (A,A),A".Split(' ');
            var rights = "nil [] [nil] B B,B B,B,B [B] [B,B] [B,B,B] *B *nil *[] *[nil] *[B] *[B,B] B,*[B,B] *[[]] B,*[] [B,B],B nil,B".Split(' ');

            var s = new StringBuilder(CompatPrint);
            for (int i = 0; i < lefts.Length; i++) {
                string left = NameVariables(lefts[i], out string vars);
                s.Append("def f" + i + "()\n");
                foreach (var r in rights) {
                    string right = NumberMarkers(r);
                    s.Append("    " + vars + " = 0; puts 'repro: " + left + " = " + right + "'; " + left + " = " + right + "; p " + vars + "\n");
                }
                s.Append("end\n");
            }
            for (int i = 0; i < lefts.Length; i++) {
                s.Append("f" + i + "()\n");
            }

            AssertCompatOutput("assignment", s.ToString(),
                360, "30223cc49c153b10b8093201bd9974f6d5d694fc7b8d49e98294dfc1486c50dd");
        }

        /// <summary>
        /// Every parameter list against every argument list, with and without splats.
        /// </summary>
        public void Compat_ArgumentSplatting() {
            var callees = "A *A A,A A,*A A,A,A A,A,*A".Split(' ');
            var callers = (
                "nil *nil *[nil] nil,nil nil,nil,nil nil,*[nil] B,nil [] B B,B B,B,B B,[] B,[B] B,[B,B] [[]] [B] [B,B] [B,B,B] " +
                "[B,B,*[B]] *[] *B B,*B B,B,*B B,*[] B,*[B] B,*[B,B] *[[]] *[B] *[B,B] *[B,B,B] *[B,[B,B]] *[B,*[B,B]] nil,*B"
            ).Split(' ');

            var s = new StringBuilder(CompatPrint);
            for (int i = 0; i < callees.Length; i++) {
                string parameters = NameVariables(callees[i], out string vars);
                s.Append("def test_" + i + "\n");
                s.Append("    def f(" + parameters + ")\n");
                s.Append("        return " + vars + "\n");
                s.Append("    end\n");
                foreach (var c in callers) {
                    string arguments = NumberMarkers(c);
                    s.Append("    begin; puts \"repro:f(" + arguments + ")\"; p f(" + arguments + "); rescue ArgumentError; puts('except'); end;\n");
                }
                s.Append("end\n");
            }
            for (int i = 0; i < callees.Length; i++) {
                s.Append("test_" + i + "\n");
            }

            AssertCompatOutput("argument_splatting", s.ToString(),
                396, "92e902ff45412ddcee8d3431aad9a58aee5f6df3f4fcdbde405acda317a8d528");
        }

        #endregion
    }
}

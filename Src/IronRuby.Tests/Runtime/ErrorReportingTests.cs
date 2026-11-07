/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
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

using System;
using Microsoft.Scripting.Hosting;

namespace IronRuby.Tests {
    /// <summary>
    /// How errors are reported, as CRuby 4.0 reports them: error_highlight's snippet under the
    /// message, did_you_mean's suggestions (local variables included, through NameError#local_variables),
    /// prism's annotated SyntaxError messages and the frame of the method an arity error is for. The
    /// expected output is CRuby 4.0's.
    /// </summary>
    public partial class Tests {
        /// <summary>
        /// error_highlight finds the node a frame ran in a prism parse of its file: the call, constant or
        /// operator-assignment a NameError names, the call to the C# method a TypeError's or
        /// ArgumentError's frame belongs to, the `raise` that raised it, and for a wrong number of
        /// arguments both the call and the `def'. A fragment it cannot single out gets no snippet.
        /// </summary>
        public void ErrorHighlight_Spots1() {
            TestOutput(@"
require 'ruby4'
require 'ironruby/error_highlight'
require 'ironruby/did_you_mean'
require 'tmpdir'
Dir.mktmpdir do |d|
  file = File.join(d, 'eh.rb')
  File.write(file, <<~'RUBY')
    def t
      yield
    rescue Exception => e
      puts e.detailed_message(highlight: false).gsub(""#{__dir__}/"", '')
    end
    class EhC; def hello; end; end
    t { EhC.new.helo }
    t { a = nil; a.foo.bar }
    t { 1 + ""a"" }
    t { Strng }
    t { Math::PIE }
    t { foo = 1; fo0 }
    t { h = nil; h[:x] = 1 }
    t { obj = Object.new; obj.attr = 1 }
    t { nil.foo += 1 }
    t { def two(a, b); end; two(1) }
    t { Integer(""z"") }
    t { raise ArgumentError, ""bad"" }
    t { [].each_slice }
    t { x = nil; [x.foo, x.foo] }
    def th; yield; rescue => e; p e.detailed_message(highlight: true); end
    th { nil.upcase }
    p ErrorHighlight.spot(NameError.new(""x""))
    p((ErrorHighlight.spot(1) rescue $!))
  RUBY
  verbose, $VERBOSE = $VERBOSE, nil
  load file
  $VERBOSE = verbose
end
", @"
undefined method 'helo' for an instance of EhC (NoMethodError)

t { EhC.new.helo }
           ^^^^^
Did you mean?  hello
undefined method 'foo' for nil (NoMethodError)

t { a = nil; a.foo.bar }
              ^^^^
String can't be coerced into Integer (TypeError)

t { 1 + ""a"" }
        ^^^
uninitialized constant Strng (NameError)

t { Strng }
    ^^^^^
Did you mean?  String
uninitialized constant Math::PIE (NameError)

t { Math::PIE }
        ^^^^^
undefined local variable or method 'fo0' for main (NameError)

t { foo = 1; fo0 }
             ^^^
Did you mean?  foo
               for
undefined method '[]=' for nil (NoMethodError)

t { h = nil; h[:x] = 1 }
              ^^^^^^
undefined method 'attr=' for an instance of Object (NoMethodError)

t { obj = Object.new; obj.attr = 1 }
                         ^^^^^^^
undefined method 'foo' for nil (NoMethodError)

t { nil.foo += 1 }
       ^^^^
wrong number of arguments (given 1, expected 2) (ArgumentError)

    caller: eh.rb:16
    | t { def two(a, b); end; two(1) }
                              ^^^
    callee: eh.rb:16
    | t { def two(a, b); end; two(1) }
              ^^^
invalid value for Integer(): ""z"" (ArgumentError)

t { Integer(""z"") }
            ^^^
bad (ArgumentError)

t { raise ArgumentError, ""bad"" }
          ^^^^^^^^^^^^^^^^^^^^
wrong number of arguments (given 0, expected 1) (ArgumentError)

    caller: eh.rb:19
    | t { [].each_slice }
            ^^^^^^^^^^^
undefined method 'foo' for nil (NoMethodError)
""\e[1mundefined method 'upcase' for nil (\e[1;4mNoMethodError\e[m\e[1m)\e[m\n\n\e[1mth { nil.upcase }\e[m\n\e[1m        ^^^^^^^\e[m""
nil
#<TypeError: Exception is expected>
");
        }

        /// <summary>
        /// The report of an uncaught error has the snippet and the suggestion: the message is written
        /// as it is although the snippet's text was read as bytes.
        /// </summary>
        public void ErrorHighlight_Report1() {
            Engine.Execute("require 'ruby4'; require 'ironruby/error_highlight'; require 'ironruby/did_you_mean'");
            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ir_eh_report1_" + Environment.ProcessId + ".rb");
            System.IO.File.WriteAllText(file, "class EhReport; def hello; end; end\n[1].each { EhReport.new.helo }\n");
            try {
                // rescued in Ruby, which has the backtrace's lines (an exception that leaves compiled code
                // for the host has them only with debug information)
                var e = (Exception)Engine.Execute("begin; load '" + file.Replace("\\", "/") + "'; rescue => e; e; end");
                string report = Engine.GetService<ExceptionOperations>().FormatException(e).Replace("\r\n", "\n");
                Assert(report.Contains(
                    "undefined method 'helo' for an instance of EhReport (NoMethodError)\n\n[1].each { EhReport.new.helo }\n                       ^^^^^\nDid you mean?  hello\n"
                ), report);
            } finally {
                System.IO.File.Delete(file);
            }
        }

        /// <summary>
        /// Messages asked for on many threads at once, about errors in more files than error_highlight
        /// keeps parsed: its cache of parsed files is shared by every thread, and a Hash changed by
        /// two threads at once threw IndexOutOfRangeException out of #detailed_message (or failed
        /// an assertion in the Hash's compaction).
        /// </summary>
        public void ErrorHighlight_Threads1() {
            Engine.Execute("require 'ruby4'; require 'ironruby/error_highlight'");
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ir_eh_threads1_" + Environment.ProcessId);
            System.IO.Directory.CreateDirectory(dir);
            try {
                for (int i = 0; i < 8; i++) {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "f" + i + ".rb"), "def eh_threads_f" + i + "(x) = x.zz\n");
                }
                TestOutput(@"
dir = '" + dir.Replace("\\", "/") + @"'
8.times { |i| load File.join(dir, ""f#{i}.rb"") }
(nil.zz rescue $!.detailed_message)
bad = Queue.new
8.times.map { |t|
  Thread.new {
    120.times { |k|
      i = (t + k) % 8
      begin
        send(""eh_threads_f#{i}"", nil)
      rescue NoMethodError => e
        begin
          m = e.detailed_message(highlight: false)
          bad << ""no snippet"" unless m.include?(""^^^"")
        rescue Exception => x
          bad << x.class.name
        end
      end
    }
  }
}.each(&:join)
p bad.size
", @"
0
");
            } finally {
                System.IO.Directory.Delete(dir, true);
            }
        }

        /// <summary>
        /// NameError#local_variables: those of the scope an undefined local variable or method was named
        /// in, or NameError.new was called in, which did_you_mean suggests. A struct's missing member
        /// carries its name and the struct, for did_you_mean to suggest the members.
        /// </summary>
        public void NameError_LocalVariables1() {
            TestOutput(@"
require 'ruby4'
require 'ironruby/did_you_mean'
def t
  yield
rescue Exception => e
  puts e.detailed_message(highlight: false)
end
t { first_name = 1; frst_name; first_name }
t { [1].each { |item| iten } }
def lv_check(value); valeu; end
t { lv_check(1) }
DymStruct1 = Struct.new(:width)
t { DymStruct1.new(1)[:widht] }
t { {name: 1}.fetch(:nmae) }
t { case {name: 1}; in {nmae:} then nmae; end }
t { require 'net/htp' }
p NameError.new('x').local_variables
begin; undefined_thing; rescue NameError => e; p e.local_variables; end
[1].each { |blk_var| begin; qqq; rescue NameError => e; p e.local_variables; end }
p NoMethodError.new('x').local_variables
p((DymStruct1.new(1)[:nope] rescue [$!.name, $!.receiver]))
", @"
undefined local variable or method 'frst_name' for main (NameError)
Did you mean?  first_name
undefined local variable or method 'iten' for main (NameError)
Did you mean?  item
undefined local variable or method 'valeu' for main (NameError)
Did you mean?  value
no member 'widht' in struct (NameError)
Did you mean?  width
               width=
key not found: :nmae (KeyError)
Did you mean?  :name
{name: 1}: key not found: :nmae (NoMatchingPatternKeyError)
Did you mean?  :name
cannot load such file -- net/htp (LoadError)
Did you mean?  net/http
[:e]
[:e]
[:blk_var, :e]
[:e]
[:nope, #<struct DymStruct1 width=1>]
");
        }

        /// <summary>
        /// SyntaxError#message is MRI's: "syntax errors found" and prism's picture of the lines the errors
        /// are on, each marked with its message; a SyntaxError made without a message says "compile error".
        /// </summary>
        public void SyntaxError_Formatted1() {
            TestOutput(@"
require 'ruby4'
def show
  yield
rescue SyntaxError => e
  p e.message
  p e.path
end
show { eval('1 +', nil, 'one.rb') }
show { eval(""def x(\n"", nil, 'fake.rb', 10) }
show { eval(""a = 1\n"" * 12 + ""foo(\n"", nil, 'long.rb') }
show { eval(""x = 1\ny = [1, 2\nz = 3\np(x\n"", nil, 'multi.rb') }
show { eval(""1 +\n"" + ('x' * 80) + "" + ("", nil, 'wide.rb') }
p SyntaxError.new.message
p SyntaxError.new(nil).message
p SyntaxError.new('msg').message
p Class.new(SyntaxError).new.message
p SyntaxError.new.path
", @"
""one.rb:1: syntax errors found\n> 1 | 1 +\n    |    ^ unexpected end-of-input, assuming it is closing the parent top level context\n    |    ^ unexpected end-of-input; expected an expression after the operator\n""
""one.rb""
""fake.rb:10: syntax errors found\n> 10 | def x(\n     | ^~~ expected an `end` to close the `def` statement\n     |       ^ unexpected end-of-input, assuming it is closing the parent top level context\n     |       ^ unexpected end-of-input; expected a `)` to close the parameters\n""
""fake.rb""
""long.rb:13: syntax error found\n  11 | a = 1\n  12 | a = 1\n> 13 | foo(\n     |     ^ unexpected end-of-input; expected a `)` to close the arguments\n""
""long.rb""
""multi.rb:3: syntax errors found\n  1 | x = 1\n  2 | y = [1, 2\n> 3 | z = 3\n    | ^ unexpected local variable or method; expected a `,` separator for the array elements\n> 4 | p(x\n    | ^ unexpected local variable or method; expected a `,` separator for the array elements\n    |    ^ unexpected end-of-input; expected a `]` to close the array\n    |    ^ unexpected end-of-input; expected a `)` to close the arguments\n""
""multi.rb""
""wide.rb:2: syntax error found\n  1 | 1 +\n> 2 | ... \n    |     ^ expected a matching `)`\n""
""wide.rb""
""compile error""
""SyntaxError""
""msg""
""compile error""
nil
");
        }

        /// <summary>
        /// An ArgumentError for the wrong number of arguments starts its backtrace with the frame of the
        /// method called - at its `def' for a Ruby method, at the call for a library one - as MRI's does.
        /// Module#const_missing has no frame, and code evaluated as "&lt;internal:...&gt;" keeps one frame
        /// for all it ran (RubyGems' Kernel#require is).
        /// </summary>
        public void Backtrace_ArityCalleeFrame1() {
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
Dir.mktmpdir do |d|
  file = File.join(d, 'frames.rb')
  File.write(file, <<~'RUBY')
    def frames(n = 2)
      yield
    rescue Exception => e
      base = __LINE__
      p e.backtrace_locations.first(n).map { |l| [l.lineno - base, l.label] }
    end
    def arity_m1(a); end
    frames { arity_m1(1, 2) }
    frames { [].each_slice }
    class AritySingleton1; def self.bar(x) = x; end
    frames { AritySingleton1.bar }
    frames(1) { Strng }
    frames(1) { Object.const_missing(:Nope) }
    eval('def internal_foo1; yield; end; def internal_bar1; [1].each { yield }; end', nil, '<internal:x>')
    frames(3) { internal_foo1 { raise 'x' } }
    frames(3) { internal_bar1 { raise 'x' } }
    def arity_kw1(a:); end
    frames { arity_kw1 }
  RUBY
  load file
end
", @"
[[3, ""Object#arity_m1""], [4, ""block in <top (required)>""]]
[[5, ""Enumerable#each_slice""], [5, ""block in <top (required)>""]]
[[6, ""AritySingleton1.bar""], [7, ""block in <top (required)>""]]
[[8, ""block in <top (required)>""]]
[[9, ""block in <top (required)>""]]
[[11, ""block (2 levels) in <top (required)>""], [11, ""Object#internal_foo1""], [11, ""block in <top (required)>""]]
[[12, ""block (2 levels) in <top (required)>""], [12, ""Object#internal_bar1""], [12, ""block in <top (required)>""]]
[[13, ""Object#arity_kw1""], [14, ""block in <top (required)>""]]
");
        }
    }
}

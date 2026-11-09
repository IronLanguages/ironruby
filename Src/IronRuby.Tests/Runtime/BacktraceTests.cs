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
using System.Text;
using Microsoft.Scripting;
using IronRuby.Runtime;
using System.Runtime.CompilerServices;

namespace IronRuby.Tests {
    public partial class Tests {
        public void Backtrace1() {
            AssertOutput(delegate() {
                Engine.CreateScriptSourceFromString(@"
def goo()
  foo()
end

def foo()
  bar()
end

def bar()  
  baz()
end

def baz()
  puts caller[0..3]
end

goo
", "Backtrace1.rb", SourceCodeKind.File).Execute();
            }, @"
Backtrace1.rb:11:in 'Object#bar'
Backtrace1.rb:7:in 'Object#foo'
Backtrace1.rb:3:in 'Object#goo'
Backtrace1.rb:18:in '<main>'
");
        }

        public void Backtrace2() {
           AssertOutput(delegate() {
                CompilerTest(@"
def goo()
  foo()
end

def foo()
  bar()
end

def bar()  
  baz()
end

def baz()
  raise
end

begin
  goo
rescue
  puts $@[0..4]
end
");
            }, @"
Backtrace2.rb:15:in 'Object#baz'
Backtrace2.rb:11:in 'Object#bar'
Backtrace2.rb:7:in 'Object#foo'
Backtrace2.rb:3:in 'Object#goo'
Backtrace2.rb:19:in '<main>'
");
        }

        public void Backtrace3() {
            AssertOutput(delegate() {
                CompilerTest(@"
def goo()
  foo()
rescue  
  raise
end

def foo()
  bar()
end

def bar()
  baz()
rescue  
  raise
end

def baz()
  raise
end

begin
  goo
rescue
  puts $@[0..4]
end
");
            }, @"
Backtrace3.rb:19:in 'Object#baz'
Backtrace3.rb:13:in 'Object#bar'
Backtrace3.rb:9:in 'Object#foo'
Backtrace3.rb:3:in 'Object#goo'
Backtrace3.rb:23:in '<main>'
");
        }

        public class ClrBacktrace {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Bar(Action d) {
                d();
            }
        }

        public void Backtrace4() {
            Context.ObjectClass.SetConstant("C", Context.GetClass(typeof(ClrBacktrace)));
            Context.ObjectClass.SetConstant("A", Context.GetClass(typeof(Action)));

            AssertOutput(delegate() {
                CompilerTest(@"
def goo
  puts caller[0..4]
end

def baz
  goo
end

def foo
  C.new.bar(A.new { baz })
end

foo
");
            }, @"
Backtrace4.rb:7:in 'Object#baz'
Backtrace4.rb:11:in 'block in Object#foo'
*:*:in 'Bar'
Backtrace4.rb:11:in 'Object#foo'
Backtrace4.rb:14:in '<main>'
", OutputFlags.Match);
        }

        public void Backtrace6() {
            TestOutput(@"
def f1
  f2 
end

def f2
  #
  #
  #
  1.times do
    #
    #
    f3
    #
    #
  end
  #
  #
  #
end

def f3
  raise
end

f1 rescue puts $@[0..5]
", @"
Backtrace6.rb:23:in 'Object#f3'
Backtrace6.rb:13:in 'block in Object#f2'
Backtrace6.rb:10:in 'Integer#times'
Backtrace6.rb:10:in 'Object#f2'
Backtrace6.rb:3:in 'Object#f1'
Backtrace6.rb:26:in '<main>'
");
        }
        
        /// <summary>
        /// A frame of ExceptionDispatchInfo.Throw is bookkeeping, not a Ruby frame: a rethrow through it keeps the frames the
        /// exception came through. The DLR's interpreter (IronLanguages/dlr #371) rethrows an exception a frame has no handler
        /// for that way, which put ":0:in 'Throw'" on top of every backtrace of an exception leaving an interpreted frame.
        /// </summary>
        public void Backtrace8() {
            AssertOutput(delegate() {
                CompilerTest(@"
def f
  System::Runtime::ExceptionServices::ExceptionDispatchInfo.throw(System::InvalidOperationException.new('x'))
end
begin
  f
rescue System::InvalidOperationException => e
  puts e.backtrace[0].sub(/:\d+:/, ':N:').sub('Object#', '')
  puts e.backtrace.grep(/Throw/).size
end
");
            }, @"
Backtrace8.rb:N:in 'f'
0
");
        }

        /// <summary>
        /// The interpreter calls a method with this many parameters through reflection (DLR patch 0103): what the method
        /// raised kept its frames, the caller's and those below the rescuing one, and the reflection frames do not show.
        /// Built against a DLR without that patch, the trace starts at caller_of_many (issue 132); the build now refuses one.
        /// </summary>
        public void Backtrace9() {
            AssertOutput(delegate() {
                CompilerTest(@"
def many(a, b, c, d, e, f, g, h, i, j, k)
  raise ArgumentError, 'x'
end

def caller_of_many
  many(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11)
end

begin
  caller_of_many
rescue => e
  puts e.backtrace.first(3).map { |f| f[/in '(.*)'/, 1] || f[/\w+\.rb/] }
end
");
            }, @"
Object#many
Object#caller_of_many
<main>
");
        }

        /// <summary>
        /// Code run through the hosting API (ScriptEngine.Execute, ScriptSource.Execute, CompilerTest) gets the labels CRuby 4.0
        /// and ir print (issue 158): a top-level method is "Object#many" although a hosted scope defines it in main's singleton
        /// class, and the top level is "<main>" - it was "#", which read "block in #" and left the top-level frame unlabelled.
        /// </summary>
        public void Backtrace_HostedLabels1() {
            const string code = @"
def many
  raise ArgumentError, 'x'
end

class Foo
  def bar = many
  def self.baz = new.bar
end

begin
  [1].each { Foo.baz }
rescue => e
  puts e.backtrace.first(6).map { |f| f[/in '(.*)'/, 1] || '-' }
end
[1].each { puts caller(0, 1)[0][/in '(.*)'/, 1] }
puts caller(0, 1)[0][/in '(.*)'/, 1]
";
            const string expected = @"
Object#many
Foo#bar
Foo.baz
block in <main>
Array#each
<main>
block in <main>
<main>
";
            AssertOutput(() => Engine.Execute(code), expected);
            AssertOutput(() => Engine.CreateScriptSourceFromString(code, "hosted.rb", SourceCodeKind.File).Execute(), expected);
            AssertOutput(() => CompilerTest(code), expected);
        }

        private const string HostedBacktraceCode = @"
def many
  raise ArgumentError, 'x'
end

class Foo
  def bar = many
  def self.baz = new.bar
end

begin
  [1].each { Foo.baz }
rescue => e
  puts e.backtrace
end
";

        /// <summary>
        /// Code without a path (ScriptEngine.Execute(string)) had no line information at all (issue 163): a method's frame was
        /// reported at the line of its `def' and a frame of the top level - Array#each's too - at line 0. It now has the lines
        /// a file has, under the name "(eval)" its frames already had. Nothing of the host is reported below "<main>".
        /// </summary>
        public void Backtrace_HostedPathlessLines1() {
            AssertOutput(() => Engine.Execute(HostedBacktraceCode), @"
(eval):3:in 'Object#many'
(eval):7:in 'Foo#bar'
(eval):8:in 'Foo.baz'
(eval):12:in 'block in <main>'
(eval):12:in 'Array#each'
(eval):12:in '<main>'
");
            // Run in a scope of the host's (ScriptCode.Run(Scope), as CompilerTest does), a top-level `def' defines a
            // method of that scope: main's singleton class. So Foo#bar, reopened here, calls the Object#many that
            // Engine.Execute defined above - and reports it where that one raises, not at its `def'.
            AssertOutput(() => CompilerTest(HostedBacktraceCode), @"
(eval):3:in 'Object#many'
Backtrace_HostedPathlessLines1.rb:7:in 'Foo#bar'
Backtrace_HostedPathlessLines1.rb:8:in 'Foo.baz'
Backtrace_HostedPathlessLines1.rb:12:in 'block in <main>'
Backtrace_HostedPathlessLines1.rb:12:in 'Array#each'
Backtrace_HostedPathlessLines1.rb:12:in '<main>'
");
            AssertOutput(() => Engine.Execute(@"
def m = __method__
p method(:m).source_location
p caller(0)
"), @"
[""(eval)"", 2]
[""(eval):4:in '<main>'""]
");
        }

        /// <summary>
        /// The CLR frames of the host that runs Ruby code (ScriptSource.Execute's caller and its callers: CompilerTest,
        /// RunTestCase, Main) were reported below "<main>" (issue 163). CRuby has nothing below the main script's frame.
        /// A CLR method that Ruby code calls and that calls back into Ruby is still reported (Backtrace4).
        /// </summary>
        public void Backtrace_HostedNoHostFrames1() {
            AssertOutput(() => Engine.CreateScriptSourceFromString(HostedBacktraceCode, "hosted.rb", SourceCodeKind.File).Execute(), @"
hosted.rb:3:in 'Object#many'
hosted.rb:7:in 'Foo#bar'
hosted.rb:8:in 'Foo.baz'
hosted.rb:12:in 'block in <main>'
hosted.rb:12:in 'Array#each'
hosted.rb:12:in '<main>'
");
            AssertOutput(() => CompilerTest(@"
p caller
p caller(0)
[1].each { p caller(0) }
begin
  raise 'x'
rescue => e
  p e.backtrace
end
"), @"
[]
[""Backtrace_HostedNoHostFrames1.rb:3:in '<main>'""]
[""Backtrace_HostedNoHostFrames1.rb:4:in 'block in <main>'"", ""Backtrace_HostedNoHostFrames1.rb:4:in 'Array#each'"", ""Backtrace_HostedNoHostFrames1.rb:4:in '<main>'""]
[""Backtrace_HostedNoHostFrames1.rb:6:in '<main>'""]
");

            // a method the host calls has no frame below its own
            var scope = Engine.CreateScope();
            Engine.Execute("def frames(start) = caller(start)", scope);
            var frames = scope.GetVariable<Func<int, object>>("frames");
            var all = (IronRuby.Builtins.RubyArray)frames(0);
            Assert(all.Count == 1);
            Assert(all[0].ToString() == "(eval):1:in 'Object#frames'");
            Assert(((IronRuby.Builtins.RubyArray)frames(1)).Count == 0);
        }

        /// <summary>
        /// A compiled method or block (the tests compile every method before its first run; ir.sh, once it has run ~300
        /// times) has no line information of its own: every frame of one was reported at the line its method or block
        /// starts on. It keeps its line in a frame of PrecompiledFrames (see CompiledFrameExpression) - also once an
        /// exception has left it, and for a recursive call that has returned - and for caller (Thread#backtrace,
        /// caller_locations and backtrace_locations, which the prelude defines, read the same lines).
        /// </summary>
        public void Backtrace_CompiledLines1() {
            TestOutput(@"
def inner(x)
  y = x
  raise IOError, 'x' if y > 2
end

def middle(x)
  [x].each do |v|
    inner(v)
  end
end

def walk(n)
  return 0 if n == 0
  r = walk(n - 1)
  raise 'after' if n == 2
  r
end

def here
  caller(0, 2).map { |f| f[/:(\d+)/, 1].to_i }
end

4.times do |i|
  begin
    middle(i)
  rescue IOError => e
    p e.backtrace.first(4).map { |f| f[/:(\d+)/, 1].to_i }
  end
end
begin
  walk(3)
rescue => e
  p e.backtrace.first(3).map { |f| f[/:(\d+)/, 1].to_i }
end
p here
", @"
[4, 9, 8, 8]
[16, 15, 32]
[21, 36]
");
        }

        /// <summary>
        /// The frames a rescued exception unwound keep their lines for its backtrace only: a recursive call that raised
        /// and was rescued left a frame of the same method above the live one, and an exception the live one raised
        /// after that was reported at the line of the call that had raised (10 here, not 8).
        /// </summary>
        public void Backtrace_CompiledLines2() {
            TestOutput(@"
def foo(n)
  if n > 0
    begin
      foo(n-1)
    rescue
    end
    raise 'outer'
  else
    raise 'inner'
  end
end
begin
  foo(1)
rescue => e
  p e.backtrace.first(2).map { |f| f[/:(\d+)/, 1].to_i }
end
", @"
[8, 14]
");
        }

        /// <summary>
        /// A lambda System.Linq.Expressions compiled without a name is no frame of Ruby code: a fiber's backtrace from
        /// code ir.sh had compiled (once hot) had a bogus "0:in 'lambda_methodNN'" under the fiber's block.
        /// </summary>
        public void Backtrace_UnnamedLambdaFrame1() {
            var trace = System.Linq.Expressions.Expression.Lambda<Func<System.Diagnostics.StackTrace>>(
                System.Linq.Expressions.Expression.New(typeof(System.Diagnostics.StackTrace))
            ).Compile()();
            var method = trace.GetFrame(0).GetMethod();
            Assert(method != null && method.Name.StartsWith("lambda_method", StringComparison.Ordinal));
            Assert(!RubyStackTraceBuilder.IsVisibleClrFrame(method));
        }

        /// <summary>
        /// Under the C locale (US-ASCII, Docker's default) a non-ASCII path is its UTF-8 bytes in a BINARY string. The
        /// frames of a file in such a directory were US-ASCII strings of characters it has none for, and require_relative,
        /// caller_locations(0)[0].path and the error report raised "invalid byte sequence in US-ASCII"; load of the
        /// BINARY path Dir.children returns, and File.expand_path of a UTF-8 name in such a directory, read each byte as a
        /// character of its own.
        /// </summary>
        public void Backtrace_AsciiLocalePaths1() {
            if (OperatingSystem.IsWindows()) {
                return;
            }

            TestOutput(@"
require 'ruby4'
require 'tmpdir'
Dir.mktmpdir do |t|
  d = File.join(t, ""café"")
  Dir.mkdir(d)
  File.write(File.join(d, 'lib.rb'), ""LIB = caller_locations(0)[0].path\n"")
  File.write(File.join(d, 'm.rb'), ""require_relative 'lib'\nM = caller(0)[0]\n"")
  old = Encoding.default_external
  Encoding.default_external = Encoding::US_ASCII
  begin
    dc = Dir.children(t).first
    p dc.encoding
    load File.join(t.b, dc, 'm.rb')
    p [LIB, M].map(&:encoding), [LIB, M].all?(&:valid_encoding?)
    p LIB.b.end_with?(""café/lib.rb"".b), M.b.include?(""café/m.rb:2"".b)
    p File.expand_path('x', File.dirname(LIB)).end_with?(""café/x"")
  ensure
    Encoding.default_external = old
  end
end
", @"
#<Encoding:BINARY (ASCII-8BIT)>
[#<Encoding:BINARY (ASCII-8BIT)>, #<Encoding:BINARY (ASCII-8BIT)>]
true
true
true
true
");
        }

        /// <summary>
        /// An exception a library method throws whose class has an initialize written in Ruby - SystemCallError's, in the
        /// prelude - is replaced by a new one when it is rescued; the backtrace went with the thrown one, and Dir.mkdir's
        /// Errno::ENOENT had none - a re-raise of it (FileUtils.mkdir_p) got one from the `raise' instead. A LoadError of
        /// require_relative starts at its caller's line under 'Kernel#require_relative', with no frame of the
        /// Kernel#require it calls.
        /// </summary>
        public void Backtrace_LibraryErrors1() {
            TestOutput(@"
require 'ruby4'
def mk
  Dir.mkdir('/nonexistent_dir_zz/x')
end
def mk_again
  mk
rescue SystemCallError
  raise
end
def req
  require_relative 'nonexistent_file_zz'
end
[-> { mk }, -> { mk_again }, -> { req }].each do |l|
  begin
    l.call
  rescue SystemCallError, LoadError => e
    p e.backtrace.first(3).map { |f| f.sub(/\A.*?\.rb:/, '') }
  end
end
", @"
[""4:in 'Dir.mkdir'"", ""4:in 'Object#mk'"", ""14:in 'block in <main>'""]
[""4:in 'Dir.mkdir'"", ""4:in 'Object#mk'"", ""7:in 'Object#mk_again'""]
[""12:in 'Kernel#require_relative'"", ""12:in 'Object#req'"", ""14:in 'block in <main>'""]
");
        }

        /// <summary>
        /// A method of the prelude skips its own frame with caller(n + 1) (Kernel#caller_locations); called from another
        /// one (Thread#backtrace), whose frame stands in for the frames it calls, the caller's frame was skipped instead:
        /// Thread.current.backtrace started at the caller's caller.
        /// </summary>
        public void Backtrace_PreludeCallerSkip1() {
            TestOutput(@"
require 'ruby4'
Object.new.instance_exec do
  p Thread.current.backtrace[0..1].map { |f| f.sub(/\A.*?\.rb:/, '') }
end
", @"
[""4:in 'Thread#backtrace'"", ""4:in 'block in <main>'""]
");
        }

        public void Backtrace7() {
            // TODO: start name by \0 -> bug in reflection?
            StringBuilder sb = new StringBuilder();

            int maxLength = _driver.IsDebug ? RubyStackTraceBuilder.MaxDebugModePathSize : Char.MaxValue;

            for (int i = 1; i <= maxLength; i++) {
                sb.Append((char)i);
            }

            var srcName = sb.ToString();

            var frameInfo = Engine.CreateScriptSourceFromString(@"
def foo 
  caller[0]
end

def bar
  foo
end

bar
", srcName).Execute<string>();

            if (_driver.IsDebug) {
                Assert(frameInfo.StartsWith(srcName.Substring(0, maxLength)));
            } else {
                Assert(frameInfo.StartsWith(srcName + ":"));
            }
        }

        /// <summary>
        /// MRI defines `collect' and `map' (and `each_pair' and `each', `format' and `sprintf', ...) as one C
        /// function under several names, and a backtrace names the frame by the name called. Here one CLR
        /// method was registered under them all, and the frame said "Array#map" under `collect' too; a module
        /// function called on the module is "Kernel.format", not "Kernel#format". The methods are still one
        /// to UnboundMethod#==.
        /// </summary>
        public void Backtrace_CalledNameLabels1() {
            TestOutput(@"
def label(i)
  yield
rescue => e
  e.backtrace[i][/'(.*)'/, 1]
end
class E; include Enumerable; def each; yield 1; end; end
puts label(1) { [1].collect { raise 'x' } }, label(1) { [1].map { raise 'x' } }
puts label(1) { [1].collect! { raise 'x' } }, label(1) { [1].map! { raise 'x' } }
puts label(1) { [1].filter { raise 'x' } }, label(1) { [1].select { raise 'x' } }
puts label(1) { [1].index { raise 'x' } }, label(1) { [1].find_index { raise 'x' } }
puts label(1) { {a: 1}.each_pair { raise 'x' } }, label(1) { {a: 1}.each { raise 'x' } }
puts label(1) { {a: 1}.filter { raise 'x' } }, label(1) { {a: 1}.select { raise 'x' } }
puts label(1) { {a: 1}.filter! { raise 'x' } }, label(1) { {a: 1}.select! { raise 'x' } }
puts label(1) { 'ab'.chars { raise 'x' } }, label(1) { 'ab'.each_char { raise 'x' } }
puts label(1) { 'ab'.bytes { raise 'x' } }, label(1) { 'ab'.each_byte { raise 'x' } }
puts label(1) { 'ab'.codepoints { raise 'x' } }, label(1) { 'ab'.each_codepoint { raise 'x' } }
puts label(2) { E.new.collect { raise 'x' } }, label(2) { E.new.map { raise 'x' } }
puts label(2) { E.new.detect { raise 'x' } }, label(2) { E.new.find { raise 'x' } }
puts label(2) { E.new.filter { raise 'x' } }, label(2) { E.new.find_all { raise 'x' } }, label(2) { E.new.select { raise 'x' } }
puts label(0) { format('%d', 'x') }, label(0) { sprintf('%d', 'x') }
puts label(0) { Kernel.format('%d', 'x') }, label(0) { Kernel.sprintf('%d', 'x') }
puts label(0) { format }, label(0) { Kernel.sprintf }
p Array.instance_method(:collect) == Array.instance_method(:map), Hash.instance_method(:filter) == Hash.instance_method(:select)
p Kernel.instance_method(:format) == Kernel.instance_method(:sprintf), Kernel.method(:format) == Kernel.method(:sprintf)
p Enumerable.instance_method(:detect) == Enumerable.instance_method(:find), String.instance_method(:chars) == String.instance_method(:each_char)
a = [1]
p a.method(:collect).hash == a.method(:map).hash, a.method(:collect).arity, Kernel.method(:sprintf).arity
", @"
Array#collect
Array#map
Array#collect!
Array#map!
Array#filter
Array#select
Array#index
Array#find_index
Hash#each_pair
Hash#each
Hash#filter
Hash#select
Hash#filter!
Hash#select!
String#chars
String#each_char
String#bytes
String#each_byte
String#codepoints
String#each_codepoint
Enumerable#collect
Enumerable#map
Enumerable#detect
Enumerable#find
Enumerable#filter
Enumerable#find_all
Enumerable#select
Kernel#format
Kernel#sprintf
Kernel.format
Kernel.sprintf
Kernel#format
Kernel.sprintf
true
true
true
true
true
false
true
0
-1
");
        }

        /// <summary>
        /// An Enumerator a builtin answers without a block names the method by the name it was called by, as MRI's
        /// does: `[1].collect' is #&lt;Enumerator: [1]:collect&gt;, not :map. An alias names the method it aliases.
        /// Enumerable#find and #detect said #&lt;Enumerator: ...&gt;.
        /// </summary>
        public void Enumerator_CalledName1() {
            TestOutput(@"
require 'ruby4'
class E; include Enumerable; def each; yield 1; end; def inspect; 'e'; end; end
class A2 < Array; alias foo collect; alias bar filter; def collect!; super; end; end
S = Struct.new(:a)
[
  [1].collect, [1].map, [1].collect!, [1].map!, [1].filter, [1].select, [1].index, [1].find_index,
  {}.each_pair, {}.each, {}.filter, {}.select, {}.filter!, {}.select!,
  E.new.collect, E.new.map, E.new.detect, E.new.find, E.new.find(:none), E.new.filter, E.new.find_all, E.new.select,
  E.new.find_index, S.new(1).filter, S.new(1).select, A2.new([1]).foo, A2.new([1]).bar, A2.new([1]).collect!,
  [1].method(:collect).call, [1].send(:filter),
].each { |e| puts e.inspect }
p E.new.find.each { |x| x == 1 }, E.new.find(-> { 0 }).each { |x| x == 2 }, E.new.find_index.each { |x| x == 1 }
p [1, 2].index.each { |x| x == 2 }, [3].filter.each { |x| x > 1 }, {a: 1}.each_pair.to_a, S.new(1).filter.to_a
p [1, 2].map!.with_index { |x, i| x * i }, [3, 4].collect.with_index(1).to_a, (1..3).detect.size
", @"
#<Enumerator: [1]:collect>
#<Enumerator: [1]:map>
#<Enumerator: [1]:collect!>
#<Enumerator: [1]:map!>
#<Enumerator: [1]:filter>
#<Enumerator: [1]:select>
#<Enumerator: [1]:index>
#<Enumerator: [1]:find_index>
#<Enumerator: {}:each_pair>
#<Enumerator: {}:each>
#<Enumerator: {}:filter>
#<Enumerator: {}:select>
#<Enumerator: {}:filter!>
#<Enumerator: {}:select!>
#<Enumerator: e:collect>
#<Enumerator: e:map>
#<Enumerator: e:detect>
#<Enumerator: e:find>
#<Enumerator: e:find(:none)>
#<Enumerator: e:filter>
#<Enumerator: e:find_all>
#<Enumerator: e:select>
#<Enumerator: e:find_index>
#<Enumerator: #<struct S a=1>:filter>
#<Enumerator: #<struct S a=1>:select>
#<Enumerator: [1]:collect>
#<Enumerator: [1]:filter>
#<Enumerator: [1]:collect!>
#<Enumerator: [1]:collect>
#<Enumerator: [1]:filter>
1
0
0
1
[3]
[[:a, 1]]
[1]
[0, 2]
[[3, 1], [4, 2]]
nil
");
        }

        /// <summary>
        /// MRI gives a C function a frame when it is called as a method (rb_funcall, rb_block_call) and none when C calls it
        /// directly. Of library methods calling one another only the outermost was reported, which lost the ones a library
        /// method calls as Ruby methods: Array#map under Kernel#public_send, Hash#each under Enumerable#map. Methods of the
        /// Ruby half of the core library showed their helpers (Math.__dom__, Enumerable#__extreme_by__) and the methods
        /// they are written with (Array#select! under Array#keep_if, Enumerable#sum under Array#sum); an alias there was
        /// labelled by the method it aliases (Array#filter! as Array#select!, Enumerable#reduce as #inject); a module
        /// function called on its module was labelled as the instance method (Math#sqrt, Kernel#Integer for Kernel.Integer).
        /// </summary>
        public void Backtrace_LibraryFrames1() {
            TestOutput(@"
require 'ruby4'
# the frames of methods with an owner: the blocks and the host's frames are not what this is about
def bt; yield; rescue => e; e.backtrace.map { |l| l[/'(.*)'/, 1] }.grep(/\A[A-Z][\w:]*[#.]/).grep_v(/\AObject#/).join(' < '); end
2.times { puts bt { [1].public_send(:map) { raise 'x' } } }
puts bt { {a: 1}.map { raise 'x' } }, bt { [1].each_with_index { raise 'x' } }, bt { [1].each.with_index { raise 'x' } }
puts bt { [1, 2].min_by { raise 'x' } }, bt { [1, 2].sum { raise 'x' } }, bt { [1, 2].uniq { raise 'x' } }
puts bt { Math.sqrt(-1) }, bt { Math.sqrt('x') }, bt { Math.log(-1) }
puts bt { Kernel.Integer('zz') }, bt { Integer('zz') }, bt { Kernel.Float('zz') }, bt { Float('zz') }
puts bt { [1].filter! { raise 'x' } }, bt { [1].keep_if { raise 'x' } }, bt { [1].select! { raise 'x' } }
puts bt { {a: 1, b: 2}.reduce { raise 'x' } }, bt { {a: 1, b: 2}.inject { raise 'x' } }
puts bt { [1].collect_concat { raise 'x' } }, bt { [1].flat_map { raise 'x' } }, bt { [].each.next }
puts bt { 'ab'.each_line { raise 'x' } }, bt { 'ab'.upto('ac') { raise 'x' } }
p Array.instance_method(:filter!) == Array.instance_method(:select!), Enumerable.instance_method(:reduce) == Enumerable.instance_method(:inject)
p Enumerable.instance_method(:collect_concat) == Enumerable.instance_method(:flat_map), IO.instance_method(:each) == IO.instance_method(:each_line)
p Array.instance_method(:keep_if) == Array.instance_method(:select!), Kernel.method(:Integer).call('12'), Kernel.Float('1.5')
p [1, 2, 3].filter!(&:odd?), [1].filter!.inspect, [1, 2, 3].keep_if(&:even?), [1, 2].reduce(:+), [[1], [2]].collect_concat.to_a
", @"
Array#map < Kernel#public_send < Integer#times
Array#map < Kernel#public_send < Integer#times
Hash#each < Enumerable#map
Array#each < Enumerable#each_with_index
Array#each < Enumerator#with_index
Array#each < Enumerable#min_by
Array#sum
Array#uniq
Math.sqrt
Math.sqrt
Math.log
Kernel.Integer
Kernel#Integer
Kernel.Float
Kernel#Float
Array#filter!
Array#keep_if
Array#select!
Hash#each < Enumerable#reduce
Hash#each < Enumerable#inject
Array#each < Enumerable#collect_concat
Array#each < Enumerable#flat_map
Enumerator#next
String#each_line
String#upto
true
true
true
true
false
12
1.5
[1, 3]
""#<Enumerator: [1]:filter!>""
[2]
3
[[1], [2]]
");
        }

        /// <summary>
        /// An Errno a library method raised had no backtrace when rescued: the rescue replaces the exception with one the
        /// class's own #initialize made (SystemCallError's is Ruby), and copied the backtrace over before it had been taken.
        /// Kernel#Float's warning about a number out of range is located in &lt;internal:kernel&gt;, as MRI's (written in Ruby).
        /// </summary>
        public void Backtrace_RescuedErrno1() {
            TestOutput(@"
require 'ruby4'
# the backtrace reaches the block that called the method, above the method that rescued it
def bt; yield; rescue => e; [e.class, e.backtrace.any? { |l| l.include?('block in') }]; end
p bt { File.read('/nonexistent/x') }, bt { Dir.rmdir('/nonexistent/x') }, bt { File.unlink('/nonexistent/x') }
def f; File.stat('/nonexistent/x'); end
p bt { f }
begin; f; rescue => e; p e.backtrace.grep(/[#']f'/).size; end
def Warning.warn(message, category: nil) = print(message)
$VERBOSE = true
Float('1e400'); Float('1e400', exception: false)
$VERBOSE = false
", @"
[Errno::ENOENT, true]
[Errno::ENOENT, true]
[Errno::ENOENT, true]
[Errno::ENOENT, true]
1
<internal:kernel>:196: warning: Float 1e400 out of range
<internal:kernel>:198: warning: Float 1e400 out of range
");
        }

        /// <summary>
        /// StringIO's iterators without a block were Enumerators over a generator (#&lt;Enumerator:
        /// #&lt;Enumerator::Generator:...&gt;:each&gt;) rather than of the method, as MRI's; under a block that
        /// raised, StringIO#each_line had two frames - the Ruby wrapper's in stringio.rb and the built-in's, named
        /// StringIO#each - where MRI's C extension has one, and IO#each_line was IO#each and IO#each_line.
        /// </summary>
        public void StringIO_IteratorNames1() {
            TestOutput(@"
require 'ruby4'
require 'stringio'
[StringIO.new(""a\nb"").each, StringIO.new('a').each_line, StringIO.new('ab').each_char, StringIO.new('ab').each_byte,
 StringIO.new('ab').each_codepoint].each { |e| puts e.inspect.sub(/0x\h+/, 'X') }
p StringIO.new(""a\nb"").each.to_a, StringIO.new(""a\nb"").each_line(chomp: true).to_a, StringIO.new('ab').each_char.size
p StringIO.instance_method(:each_line).source_location
# the frame after the method's is the block that called it, not a second one of the method
def label(i); yield; rescue => e; e.backtrace[i][/'(.*)'/, 1].sub(/ in .*/, ''); end
puts label(1) { StringIO.new('a').each_line { raise 'x' } }, label(2) { StringIO.new('a').each_line { raise 'x' } }
puts label(1) { StringIO.new('a').each_byte { raise 'x' } }, label(2) { StringIO.new('a').each_byte { raise 'x' } }
r, w = IO.pipe
w.puts 'a', 'b'
w.close
puts label(1) { r.each_line { raise 'x' } }, label(2) { r.each_line { raise 'x' } }
", @"
#<Enumerator: #<StringIO:X>:each>
#<Enumerator: #<StringIO:X>:each_line>
#<Enumerator: #<StringIO:X>:each_char>
#<Enumerator: #<StringIO:X>:each_byte>
#<Enumerator: #<StringIO:X>:each_codepoint>
[""a\n"", ""b""]
[""a"", ""b""]
nil
nil
StringIO#each_line
block
StringIO#each_byte
block
IO#each_line
block
");
        }
    }
}
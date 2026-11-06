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
        private bool PreciseSinglePassTraces {
            get { 
                // We have precise traces in debug mode and if we interpret the methods. We assume that the tests are not executing any metho
                return Runtime.Setup.DebugMode || !_driver.NoAdaptiveCompilation && _driver.CompilationThreshold > 0; 
            }
        }

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
            }, PreciseSinglePassTraces ? @"
Backtrace1.rb:11:in 'Object#bar'
Backtrace1.rb:7:in 'Object#foo'
Backtrace1.rb:3:in 'Object#goo'
Backtrace1.rb:18
" : @"
Backtrace1.rb:10:in 'Object#bar'
Backtrace1.rb:6:in 'Object#foo'
Backtrace1.rb:2:in 'Object#goo'
Backtrace1.rb:0
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
            }, PreciseSinglePassTraces ? @"
Backtrace2.rb:15:in 'baz'
Backtrace2.rb:11:in 'bar'
Backtrace2.rb:7:in 'foo'
Backtrace2.rb:3:in 'goo'
Backtrace2.rb:19
" : @"
Backtrace2.rb:14:in 'baz'
Backtrace2.rb:10:in 'bar'
Backtrace2.rb:6:in 'foo'
Backtrace2.rb:2:in 'goo'
Backtrace2.rb:0
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
            }, PreciseSinglePassTraces ? @"
Backtrace3.rb:19:in 'baz'
Backtrace3.rb:13:in 'bar'
Backtrace3.rb:9:in 'foo'
Backtrace3.rb:3:in 'goo'
Backtrace3.rb:23
" : @"
Backtrace3.rb:18:in 'baz'
Backtrace3.rb:12:in 'bar'
Backtrace3.rb:8:in 'foo'
Backtrace3.rb:2:in 'goo'
Backtrace3.rb:0
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
            }, PreciseSinglePassTraces ? @"
Backtrace4.rb:7:in 'baz'
Backtrace4.rb:11:in 'block in foo'
*:*:in 'Bar'
Backtrace4.rb:11:in 'foo'
Backtrace4.rb:14
" : @"
Backtrace4.rb:6:in 'baz'
Backtrace4.rb:11:in 'block in foo'
*:*:in 'Bar'
Backtrace4.rb:10:in 'foo'
Backtrace4.rb:0
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
", PreciseSinglePassTraces ? @"
Backtrace6.rb:23:in 'f3'
Backtrace6.rb:13:in 'block in f2'
Backtrace6.rb:10:in 'Integer#times'
Backtrace6.rb:10:in 'f2'
Backtrace6.rb:3:in 'f1'
Backtrace6.rb:26
" : @"
Backtrace6.rb:22:in 'f3'
Backtrace6.rb:10:in 'block in f2'
Backtrace6.rb:6:in 'Integer#times'
Backtrace6.rb:6:in 'f2'
Backtrace6.rb:2:in 'f1'
Backtrace6.rb:0
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
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

namespace IronRuby.Tests {
    public partial class Tests {

        /// <summary>
        /// Tests whether user initializer is properly called (w/o block argument).
        /// If this fails all RSpec tests fails.
        /// </summary>
        public void Scenario_RubyInitializers0() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def initialize *a
    puts a.inspect
  end
end

C.new 1,2,3
");
            }, @"[1, 2, 3]");
        }

        public void Scenario_RubyInitializers1() {
            AssertOutput(delegate() {
                CompilerTest(@"
x = Array.new(5) { break 'foo' }
puts x
");
            }, @"foo");
        }

        public void Scenario_RubyInitializers2A() {
            AssertOutput(delegate() {
                CompilerTest(@"
class A
  def initialize
    yield
  end
end

x = A.new { break 'foo' }
puts x
");
            }, @"foo");
        }

        /// <summary>
        /// Block's proc-converter ("new") is not alive when the block breaks.
        /// This tests whether "new" frame is cleaned up correctly.
        /// </summary>
        public void Scenario_RubyInitializers2B() {
            AssertOutput(delegate() {
                CompilerTest(@"
class A
  def initialize &b
    $b = b
  end
end

x = A.new { break 'foo' }
1.times(&$b) rescue p $!
", 1, 0);
            }, @"#<LocalJumpError: break from proc-closure>");
        }

        /// <summary>
        /// A block is yielded to from "initialize", not from the factory.
        /// </summary>
        public void Scenario_RubyInitializers4B() {
            AssertOutput(delegate() {
                CompilerTest(@"
class Array
  def initialize *args
    puts 'init'
    p self
  end
end
Array.new(10) { puts 'unreachable' }
");
            }, @"
init
[]
");
        }

        public void Scenario_RubyInitializers4C() {
            AssertOutput(delegate() {
                CompilerTest(@"
p Array.new(10) { break 5 }
");
            }, @"5");
        }

        /// <summary>
        /// Test initializers with procs (instead of blocks - the proc-converter is different now).
        /// </summary>
        public void Scenario_RubyInitializers5() {
            AssertOutput(delegate() {
                CompilerTest(@"
def bar &p
  x = Array.new(5, &p)
  puts ""A: #{x}""
end

z = bar { |i| puts ""B: #{i}""; break 'foo' }
puts ""C: #{z}""
");
            }, @"
B: 0
C: foo
");
        }

        public void Scenario_RubyInitializers6() {
            TestOutput(@"
class C
  undef :initialize rescue p $!

  define_method(:initialize) { |*args| p args }
  new(1,2,3)

  def initialize; puts 'init'; end
  new
end
", @"
#<TypeError: Cannot undefine 'initialize' method>
[1, 2, 3]
init
");
        }

        public void RubyInitializersCaching1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C; end
C.new

class C
  def initialize
    puts 'init'
  end
end
C.new
");
            }, @"init");
        }

        public void RubyInitializersCaching2() {
            AssertOutput(delegate() {
                CompilerTest(@"
class S < String; end
puts S.new('foo')

class S
  def initialize a
    super 'bar'
  end
end
puts S.new('baz')
");
            }, @"
foo
bar
");
        }

        public void RubyInitializersCaching3() {
            TestOutput(@"
class Object
  remove_method :initialize
end

class BasicObject
  remove_method :initialize rescue p $!
end
", @"
#<NameError: Cannot remove BasicObject#initialize>
");
        }

        /// <summary>
        /// What CRuby 4.0.6 prints for the same code: it undefines MatchData.allocate and
        /// Module.allocate (Class.allocate is defined again on Class itself), an uninitialized
        /// Regexp inspects as a plain object, and an anonymous Struct class is left out of its
        /// instances' inspect. ("?" matches CRuby's quote and IronRuby's backtick alike.)
        /// </summary>
        public void RubyAllocators1() {
            AssertOutput(delegate() {
                CompilerTest(@"
p Array.allocate
p Hash.allocate
p Range.allocate
p Regexp.allocate
p String.allocate
p MatchData.allocate rescue p $!
p Object.allocate
p Module.allocate rescue p $!
p Class.allocate
p Struct.new(:f,:g).allocate
p Exception.allocate
p IO.allocate

p Binding.allocate rescue p $!
p Struct.allocate rescue p $!
p Method.allocate rescue p $!
p UnboundMethod.allocate rescue p $!
p Integer.allocate rescue p $!
p Float.allocate rescue p $!
p TrueClass.allocate rescue p $!
p FalseClass.allocate rescue p $!
p NilClass.allocate rescue p $!
p Proc.allocate rescue p $!
");
            }, @"
[]
{}
nil..nil
#<Regexp:*>
""""
#<NoMethodError: undefined method ?allocate' for class MatchData>
#<Object:*>
#<NoMethodError: undefined method ?allocate' for class Module>
#<Class:*>
#<struct f=nil, g=nil>
#<Exception: Exception>
#<IO:*>
#<TypeError: allocator undefined for Binding>
#<TypeError: allocator undefined for Struct>
#<TypeError: allocator undefined for Method>
#<TypeError: allocator undefined for UnboundMethod>
#<TypeError: allocator undefined for Integer>
#<TypeError: allocator undefined for Float>
#<TypeError: allocator undefined for TrueClass>
#<TypeError: allocator undefined for FalseClass>
#<TypeError: allocator undefined for NilClass>
#<TypeError: allocator undefined for Proc>
", OutputFlags.Match);
        }

        /// <summary>
        /// `C.new(k: v)' from a JIT keyword call site allocates the instance and calls C's specialized initialize
        /// directly, without the keyword Hash (JitKeywordCall.ResolveConstruction). Anything that changes what new
        /// would do - another class at the site, a prepended initialize, a singleton new, private_class_method,
        /// TracePoint, an error from the prologue, an exception class - is the ordinary call's, as before.
        /// The output is CRuby 4.0's (whose opt_new reports no c_call for new either).
        /// </summary>
        public void NewKeywordsDirect1() {
            TestOutput(@"
require 'ruby4'
class Pt
  attr_reader :x, :y
  def initialize(x:, y: 0)
    @x = x
    @y = y
  end
end
class Pt3 < Pt
  def initialize(x:, y: 1)
    @x = -x
    @y = y
  end
end
class Cmp
  include Comparable
  attr_reader :v
  def initialize(v:) = @v = v
  def <=>(o) = v <=> o.v
end
def make(c, i) = c.new(x: i, y: 2)
def make1(c, i) = c.new(x: i)
def try = yield rescue ""#{$!.class}: #{$!.message}""
a = 100.times.map { |i| make(Pt, i) }
p a.last.class, a.last.x, a.last.y, a.map(&:x).sum, a.map(&:object_id).uniq.size
p 100.times.map { |i| make1(Pt, i).y }.uniq
p [make(Pt3, 5).x, make(Pt, 5).x, make1(Pt3, 5).y]
p 100.times.map { |i| Cmp.new(v: i) }.max.v
p try { Pt.new(x: 1, z: 2) }
p try { Pt.new(y: 2) }
p try { make1(Class, 1) }
Pt.prepend(Module.new { def initialize(x:, y: 0) = (super; @x *= 10) })
p make(Pt, 3).x
class << Pt
  def new(**kw) = ""custom #{kw}""
end
p make(Pt, 4)
class << Pt
  remove_method :new
end
p make(Pt, 5).x
calls = []
tp = TracePoint.new(:c_call, :c_return, :call, :return) { |t| calls << [t.event, t.method_id] if [:new, :initialize].include?(t.method_id) }
tp.enable { make(Pt, 6) }
p calls
class Pt; private_class_method :new; end
p try { make(Pt, 7) }
class E < StandardError
  def initialize(msg:) = super(msg)
end
p 50.times.map { |i| E.new(msg: ""m#{i}"") }.last.message
", @"
Pt
99
2
4950
100
[0]
[-5, 5, 1]
99
""ArgumentError: unknown keyword: :z""
""ArgumentError: missing keyword: :x""
""TypeError: superclass must be an instance of Class (given an instance of Hash)""
30
""custom {x: 4, y: 2}""
50
[[:call, :initialize], [:call, :initialize], [:return, :initialize], [:return, :initialize]]
""NoMethodError: private method 'new' called for class Pt""
""m49""
");
        }
    }
}

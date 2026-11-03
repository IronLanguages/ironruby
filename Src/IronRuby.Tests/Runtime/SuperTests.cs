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
        public void Super1() {
            TestOutputWithEval(@"
class C
  def foo a
    puts 'C.foo'
    puts a
  end
end

class D < C
  def foo
    puts 'D.foo'
    #<super('arg')#>  
  end
end

D.new.foo
", @"
D.foo
C.foo
arg"
            );
        }

        public void SuperParameterless1() {
            TestOutputWithEval(@"
class C
  def foo *a
    puts 'C.foo'
    p a
  end
end

class D < C
  def foo a, b=1, c=2, d=3, e=4, f=5, g=6, h=7, i=8, j=9, k=a, *rest, &blck
    puts 'D.foo'
    p rest
    y = 1
    z = 2    
    #<super#>
  end
end

D.new.foo 0
", @"
D.foo
[]
C.foo
[0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0]
", 2
            );
        }

        public void SuperParameterless2A() {
            TestOutputWithEval(@"
class C
  def foo a
    puts 'C.foo'
    puts a
    yield
  end
end

class D < C
  def foo a
    puts 'D.foo'
    #<super { puts 'block' }#>
  end
end

D.new.foo 'arg'
", @"
D.foo
C.foo
arg
block
"
            );
        }

        /// <summary>
        /// Super passes the most recent values of the parameter variables but keeps the original block value.
        /// This is consistent with yield.
        /// </summary>
        public void SuperParameterless2B() {
            TestOutputWithEval(@"
class C
  def foo *a
    p a
    yield
  end
end

class D < C
  def foo a, &b
    a = 2
    b = lambda { puts '2' }    
    1.times { #<super#> }
  end
end

D.new.foo(1) { puts '1' }
", @"
[2]
1
"
            );
        }

        public void SuperParameterless3() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def foo
    puts 'C.foo'
  end
end

class D < C
  def foo a
    super()
  end
end

D.new.foo 'arg'
");
            }, @"
C.foo
");
        }

        /// <summary>
        /// A parameterless super in a method with **rest, ** or **nil forwards the keywords as `**rest' would - read
        /// from the locals, so a reassigned rest passes its new value, and nothing at all when there are none -
        /// rather than the hidden trailing hash the keywords arrived in, whose `{}' was one positional argument
        /// too many for a parent without keywords. The output is CRuby's.
        /// </summary>
        public void SuperParameterlessKeywordRest1() {
            TestOutput(@"
class P
  def m(h = :none) h end
  def r(*a) a end
  def k(**kw) kw end
end
class C < P
  def m(**kw) super end
  def r(a, **) super end
  def k(b: 1, **rest) super end
end
class D < P
  def m(**nil) super end
  def r(a, b = 2, **kw) kw = {y: a}; super end
end
def s(x)
  x.is_a?(Hash) ? [:hash, *x.to_a] : x.is_a?(Array) ? x.map { |e| s(e) } : x
end
p s(C.new.m), s(C.new.m(a: 1)), s(C.new.r(1)), s(C.new.r(1, z: 2)), s(C.new.k), s(C.new.k(z: 2))
p s(D.new.m), s(D.new.r(1)), s(D.new.r(1, 3, q: 4))
class E < StandardError; def initialize(**kw) super end; end
p E.new.message
", @"
:none
[:hash, [:a, 1]]
[1]
[1, [:hash, [:z, 2]]]
[:hash, [:b, 1]]
[:hash, [:z, 2], [:b, 1]]
:none
[1, 2, [:hash, [:y, 1]]]
[1, 3, [:hash, [:y, 1]]]
""E""
");
        }

        /// <summary>
        /// `super(a, **h)`, and a parameterless super in a method with **rest, pass h through one of two plain call
        /// sites: nothing at all when it is empty, and a copy when it is not - so what the parent does to the hash is
        /// its own - and through #to_hash or not at all when the rest was reassigned to something else. The
        /// arguments are evaluated in source order. The output is CRuby's.
        /// </summary>
        public void SuperKeywordSplat1() {
            TestOutput(@"
class P
  def m(h = :none) h end
  def r(*a, **kw) [a, kw] end
  def w(h = nil) h[:w] = 1 if h; h end
  def lg(x) ($log ||= []) << x; x end
end
class C < P
  def m(**kw) super(**kw) end
  def r(*a, **kw) super(*a, **kw) end
  def w(**kw) r = super; [r, kw] end
end
class D < P
  def r(a, k: 1, **rest) super end
  def w(**kw) kw = nil; super end
end
class E < P
  def r(a) super(lg(a), **lg({x: 1})) end
end
def s(x)
  x.is_a?(Hash) ? [:hash, *x.to_a] : x.is_a?(Array) ? x.map { |e| s(e) } : x
end
p s(C.new.m), s(C.new.m(a: 1))
p s(C.new.r(1, 2)), s(C.new.r(1, 2, z: 3)), s(C.new.r)
p s(C.new.w), s(C.new.w(a: 1))
p s(D.new.r(1)), s(D.new.r(1, z: 2, k: 3))
p s(D.new.w(a: 1))
p s(E.new.r(5)), s($log)
", @"
:none
[:hash, [:a, 1]]
[[1, 2], [:hash]]
[[1, 2], [:hash, [:z, 3]]]
[[], [:hash]]
[nil, [:hash]]
[[:hash, [:a, 1], [:w, 1]], [:hash, [:a, 1]]]
[[1], [:hash, [:k, 1]]]
[[1], [:hash, [:z, 2], [:k, 3]]]
nil
[[5], [:hash, [:x, 1]]]
[5, [:hash, [:x, 1]]]
");
        }

        /// <summary>
        /// The parameterless super of a method whose only keyword parameter is **rest passes the parent the hash the call
        /// passed while the rest still holds exactly that, and lets go of it as it does: a second super, one in a loop, an
        /// eval'd one, and one after the body changed the rest each pass a copy, so whatever a parent keeps or changes -
        /// a positional Hash parameter, Array#push - is its own. The output is CRuby's.
        /// </summary>
        public void SuperKeywordRestPassOn1() {
            TestOutput(@"
class P
  def m(h = :none) h[:p] = (h[:p] || 0) + 1 if h.is_a?(Hash); h end
  def k(**kw) kw[:pk] = 1; kw end
end
class C < P
  def m(**kw) r1 = super; r2 = super; [r1, r2, kw, r1.equal?(r2)] end
  def k(**kw) r = super; [r, kw] end
end
class L < P
  def m(**kw) (1..2).map { super } end
end
class E < P
  def m(**kw) r = super; [r, eval('super'), r.equal?(eval('super'))] end
end
class M < P
  def m(**kw) kw[:q] = 2; super end
end
class AA < Array
  def push(**kw) super; super; self end
end
class A3; def initialize(**o) @o = o end; attr_reader :o; end
class B3 < A3; def initialize(**o) super end; end
class C3 < B3; def initialize(**o) super; o[:after] = 1 end; end
def s(x)
  x.is_a?(Hash) ? [:hash, *x.to_a] : x.is_a?(Array) ? x.map { |e| s(e) } : x
end
p s(C.new.m(q: 1)), s(C.new.m)
p s(C.new.k(q: 1)), s(C.new.k)
p s(L.new.m(q: 1))
p s(E.new.m(q: 1))
p s(M.new.m(q: 1))
a = AA.new.push(x: 1)
p a[0].equal?(a[1])
p s(C3.new(a: 1).o)
", @"
[[:hash, [:q, 1], [:p, 1]], [:hash, [:q, 1], [:p, 1]], [:hash, [:q, 1]], false]
[:none, :none, [:hash], true]
[[:hash, [:q, 1], [:pk, 1]], [:hash, [:q, 1]]]
[[:hash, [:pk, 1]], [:hash]]
[[:hash, [:q, 1], [:p, 1]], [:hash, [:q, 1], [:p, 1]]]
[[:hash, [:q, 1], [:p, 1]], [:hash, [:q, 1], [:p, 1]], false]
[:hash, [:q, 2], [:p, 1]]
false
[:hash, [:a, 1]]
");
        }

        /// <summary>
        /// A parameterless super after the body set the **rest to something else: with named keywords it has to be a
        /// Hash still (TypeError otherwise, nil included); a lone **rest goes as `**rest` would, nil passing nothing and
        /// anything else through #to_hash. MRI's rules, and its output.
        /// </summary>
        public void SuperParameterlessKeywordRest2() {
            TestOutput(@"
class O; def to_hash = {z: 9}; end
class P; def m(*a, **k) [a, k] end; def n(*a, **k) [a, k] end; def o(*a, **k) [a, k] end; end
class C < P
  def m(a, b: 1, **r) r = O.new; super end
  def n(a, **r) r = O.new; super end
  def o(a, **r) r = 5; super end
end
class D < P
  def m(a, b: 1, **r) r = nil; super end
  def n(a, **r) r = nil; super end
  def o(a, **r) r = {'s' => 1}; super end
end
def s(x)
  x.is_a?(Hash) ? [:hash, *x.to_a] : x.is_a?(Array) ? x.map { |e| s(e) } : x
end
[C, D].each { |c| %i[m n o].each { |m| p((s(c.new.send(m, 1)) rescue [$!.class, $!.message])) } }
", @"
[TypeError, ""wrong argument type O (expected Hash)""]
[[1], [:hash, [:z, 9]]]
[TypeError, ""no implicit conversion of Integer into Hash""]
[TypeError, ""wrong argument type nil (expected Hash)""]
[[1], [:hash]]
[[1], [:hash, [""s"", 1]]]
");
        }

        /// <summary>
        /// A **rest parameter of a call that passed no keywords takes over the empty default the hidden keyword
        /// parameter was given, instead of copying it: it is still a new, plain hash on every call, in a method,
        /// a proc, a lambda, a define_method block and a signature with optional and rest positionals, and
        /// passing it on positionally passes a Hash, not keywords. The output is CRuby's.
        /// </summary>
        public void KeywordRestOmitted1() {
            TestOutput(@"
def s(x)
  x.is_a?(Hash) ? [:hash, *x.to_a] : x.is_a?(Array) ? x.map { |e| s(e) } : x
end
def r(**kw) kw end
a = r; a[:a] = 1
p s(r), a.equal?(r), s(a)
def t2(*a, **k) [a, k] end
def pass2(**kw) t2(kw) end
def pass3(**kw) t2(**kw) end
p s(pass2), s(pass3), s(pass2(q: 1)), s(pass3(q: 1))
pr = proc { |**kw| kw[:z] = 1; kw }
x = pr.call; y = pr.call
p s(x), x.equal?(y)
l = lambda { |a, **kw| kw[a] = a; kw }
p s(l.(1)), s(l.(2)), s(l.(3, b: 4))
def g(a, b = 2, *c, **kw) kw[:q] = a; kw end
p s(g(1)), s(g(2)), s(g(3, z: 1))
class D; define_method(:dm) { |**kw| kw[:d] = 1; kw }; end
p s(D.new.dm), s(D.new.dm)
", @"
[:hash]
false
[:hash, [:a, 1]]
[[[:hash]], [:hash]]
[[], [:hash]]
[[[:hash, [:q, 1]]], [:hash]]
[[], [:hash, [:q, 1]]]
[:hash, [:z, 1]]
false
[:hash, [1, 1]]
[:hash, [2, 2]]
[:hash, [:b, 4], [3, 3]]
[:hash, [:q, 1]]
[:hash, [:q, 2]]
[:hash, [:z, 1], [:q, 3]]
[:hash, [:d, 1]]
[:hash, [:d, 1]]
");
        }

        /// <summary>
        /// eval("super") in a method with keyword parameters passes the hidden parameter the keywords arrived in as
        /// `**` would - nothing at all for an empty one - not as one more positional argument. Without keywords that is
        /// the **rest itself (it took the empty default over), and after a parameterless super passed the keywords on
        /// it is the **rest too, so both see what the body put in it. The output is CRuby's.
        /// </summary>
        public void SuperEvalKeywords1() {
            TestOutput(@"
class P
  def m(h = :none) h end
  def n(*a, **k) [a, k] end
  def q(*a, **k) [a, k] end
  def w(a, h = :none) [a, h] end
end
class C < P
  def m(**kw) eval('super') end
  def n(**kw) eval('super') end
  def o(**kw) kw[:y] = 2; eval('super') end
  def q(**kw) r = super; kw[:y] = 2; [r, eval('super')] end
  def w(a, **kw) eval('super') end
end
class P; def o(h = :none) h end; end
def s(x)
  x.is_a?(Hash) ? [:hash, *x.to_a] : x.is_a?(Array) ? x.map { |e| s(e) } : x
end
p s(C.new.m), s(C.new.m(x: 1))
p s(C.new.n), s(C.new.n(x: 1))
p s(C.new.o)
p s(C.new.q(x: 1))
p s(C.new.w(1)), s(C.new.w(1, x: 1))
", @"
:none
[:hash, [:x, 1]]
[[], [:hash]]
[[], [:hash, [:x, 1]]]
[:hash, [:y, 2]]
[[[], [:hash, [:x, 1]]], [[], [:hash, [:x, 1], [:y, 2]]]]
[1, :none]
[1, [:hash, [:x, 1]]]
");
        }

        /// <summary>
        /// Calls to super with block and no arguments (was bug in parser/AST).
        /// </summary>
        public void Super2() {
            AssertOutput(delegate() {
                CompilerTest(@"
$p = proc {}

class C
  def foo *a
    p a, block_given?
  end
end

class D < C
  def foo a 
    super                     # pass 'a'
    super()    
    super &$p                 
    super(&$p)
    super { }                 # pass 'a'
    super() { }
  end
end

D.new.foo(1)
puts
D.new.foo(2) { }
", 1, 0);
            }, @"
[1]
false
[]
false
[]
true
[]
true
[1]
true
[]
true

[2]
true
[]
true
[]
true
[]
true
[2]
true
[]
true
");
        }

        public void SuperToAttribute1() {
            AssertOutput(() => CompilerTest(@"
class C
  attr_accessor :foo
end

class D < C
  def foo
    super
  end
  
  def foo= v
    super
  end
end

d = D.new
p d.foo = 123
p d.foo
p d.foo {}
p d.foo(1) rescue p $!
"), @"
123
123
123
#<ArgumentError: wrong number of arguments (given 1, expected 0)>
");
        }

        /// <summary>
        /// Super calls method_missing.
        /// </summary>
        public void SuperAndMethodMissing1() {
            AssertOutput(() => CompilerTest(@"
class C
  def method_missing name
    puts ""mm(C): #{name}""
  end
end

class D < C
  def method_missing name
    puts ""mm(D): #{name}""
  end
  
  def bar
    puts 'D: bar'    
    super                                  # calls D#method_missing!
  end
end

D.new.bar
"), @"
D: bar
mm(D): bar
");
        }

        public void SuperAndMethodMissing2() {
            AssertOutput(() => CompilerTest(@"
class D
  def bar
    super                                
  end
end

D.new.bar rescue p $!
"), @"
#<NoMethodError: super: no superclass method 'bar' for an instance of D>
");
        }

        public void SuperCaching1() {
            AssertOutput(() => CompilerTest(@"
module M
  def bar(x)
    puts 'M'
  end
end
class C
  def bar(x)
    puts 'C'
    super(x)
  end
end
C.new.bar(1) rescue puts 'error'
class C
  include M
end
C.new.bar(1)
"), @"
C
error
C
M
");
        }

        public void SuperInDefineMethod1() {
            SuperInDefineMethod1_Test(false);
        }

        public void SuperInDefineMethodEval1() {
            SuperInDefineMethod1_Test(true);
        }

        /// <summary>
        /// Super in a proc invoked via a call to a method defined by define_method uses 
        /// the method's name and declaring module for super-method lookup.
        /// </summary>
        public void SuperInDefineMethod1_Test(bool eval) {
            AssertOutput(delegate() {
                CompilerTest(Eval(eval, @"
def def_lambda
  1.times {
    $p = lambda { 
       1.times { 
         p self.class
         #<super()#>
       }
    }
  }
end

class C
  def foo
    puts 'C.foo'
  end
end

def_lambda

class D < C
  define_method :foo, &$p
end

D.new.foo
"));
            }, @"
D
C.foo
");
        }

        /// <summary>
        /// Super in a proc invoked via "call" uses the self and parameters captured in closure by the block.
        /// </summary>
        public void SuperInBlocks1() {
            TestOutput(@"
class A 
  def def_lambda a
    puts 'A.def_lambda'
    puts a
  end
end

class B < A
  def def_lambda a
    1.times {
      $p = lambda { |x|
        1.times { 
          p self.class
          super
        }
      }
    }
  end
end

B.new.def_lambda 'arg'
$p.call 'foo'
", @"
B
A.def_lambda
arg
");
        }

        /// <summary>
        /// define_method returns a lambda that behaves like a method-block when called.
        /// This behavior is not very well defined in CRuby (see http://redmine.ruby-lang.org/issues/show/2419).
        /// </summary>
        public void SuperInDefineMethod2() {
            TestOutput(@"
class B
  def m
    puts self.class.to_s + '::m'    
  end
end

class C < B
  q = nil

  C.new.instance_eval do   # we need to close the block over self that is an instance of C
    q = Proc.new do
      super()
    end
  end

  mq = define_method :m, &q
  puts mq.object_id == q.object_id  
  
  q.call rescue p $!
  C.new.m
end
", @"
false
#<NoMethodError: super called outside of method>
C::m
");
        }

        /// <summary>
        /// Caching - a single super call site can be used for invocation of two different methods,
        /// if it is defined in a block that is used in define_method.
        /// </summary>
        public void SuperInDefineMethod3() {
            TestOutput(@"
class B
  def foo
    puts 'B::foo'
  end

  def bar
    puts 'B::bar'
  end
end

class C < B
  def foo
    1.times { $p = Proc.new { 1.times { 1.times { super() } } } }
    1.times(&$p)
  end
end

c = C.new
c.foo

class C
  define_method(:bar, &$p)
end

c.bar
", @"
B::foo
B::bar
");
        }

        /// <summary>
        /// Caching - The same as SuperInDefineMethod4 but with implicit arguments at super call site. 
        /// This should throw an exception as in Ruby 1.9.
        /// </summary>
        public void SuperInDefineMethod4() {
            TestOutput(@"
class B
  def foo
    puts 'B::foo'
  end

  def bar
    puts 'B::bar'
  end
end

class C < B
  def foo
    1.times { $p = Proc.new { 1.times { 1.times { super } } } }
    1.times(&$p)
  end
end

c = C.new
c.foo

class C
  define_method(:bar, &$p)
end

c.bar rescue p $!
c.foo
", @"
B::foo
#<RuntimeError: implicit argument passing of super from method defined by define_method() is not supported. Specify all arguments explicitly.>
B::foo
");
        }

        /// <summary>
        /// Caching - the same super call site can be used to invoke different method names.
        /// </summary>
        public void SuperInDefineMethod5() {
            TestOutput(@"
class C
  def a; 'a'; end
  def b; 'b'; end
end

class D < C
  $p = Proc.new do
    super()
  end

  [:a, :b].each do |name|
    define_method(name, &$p)
  end
end

puts D.new.a, D.new.b, D.new.a
", @"
a
b
a
");
        }

        public void SuperInTopLevelCode1() {
            TestOutput(@"
class B
  def m
    puts 'B::m'
  end
end

1.times do
  $p = Proc.new do
    1.times do
      super()
    end
  end
end

$p.call rescue p $!

class C < B
  define_method(:m, &$p)
end

C.new.m
", @"
#<NoMethodError: super called outside of method>
B::m
");
        }

        /// <summary>
        /// Alias doesn't change DeclaringModule of the method => super call uses the class in which the method is defined.
        /// </summary>
        public void SuperInAliasedDefinedMethod1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class B
  def m
    puts 'B::m'
  end
end

class C < B
  define_method :m do
    puts 'C::m'
    super()
  end
  
  def n
    puts 'C::n'
  end
end

class D < C
  alias n m
end

D.new.n
");
            }, @"
C::m
B::m
");
        }

        /// <summary>
        /// super doesn't use self, declaring-module defined by module_eval/instance_eval.
        /// </summary>
        public void SuperInModuleEval1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class A
  def foo
    puts 'A::foo'
  end
end

class B < A
  def foo
    puts 'B::foo'
  end
end

class C < B
  def foo
    A.module_eval {
      super
    }
    A.new.instance_eval {
      super
    }
  end
end 

C.new.foo
");
            }, @"
B::foo
B::foo
");
        }

#if OBSOLETE
        /// <summary>
        /// MRI 1.8: The self object captured by Kernel#binding is the receiver of the binding call.
        /// Thus the self used in eval might be different from the one the scope holds on.
        /// Super call uses the scope's self object.
        /// </summary>
        public void SuperCallInEvalWithBinding18() {
            TestOutput(@"
module Kernel
  public :binding
end

class A
  def m
    puts 'A::m'
  end
end

class D < A
  def m
    puts 'D::m'
    p self.class
    
    cb = C.new.binding             # binding captures C.new as self
    eval('
        p self.class               # self is different from RubyScope.SelfObject here
        super                      # super picks up RubyScope.SelfObject
    ', cb)
  end
end

class C
end

D.new.m", @"
D::m
D
C
A::m
");
        }
#endif

        /// <summary>
        /// MRI 1.9: The self object captured by Kernel#binding is the one captured by the scope, not the receiver of the binding call.
        /// </summary>
        public void SuperCallInEvalWithBinding19() {
            TestOutput(@"
class C
  def foo
    123
  end
end

b = C.new.send(:binding)
p self
eval('
  p self
  p self.foo rescue p $!
', b)
", @"
main
main
#<NoMethodError: undefined method 'foo' for main>
");
        }
    }

}

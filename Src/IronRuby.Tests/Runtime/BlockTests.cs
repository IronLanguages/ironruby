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
using IronRuby.Runtime;

namespace IronRuby.Tests {

    public partial class Tests {
        public void BlockEmpty() {
            CompilerTest("1.times { }");
        }

        public void RubyBlocks0() {
            TestOutput(@"
3.times { |x| print x }
", @"
012
");
        }

        public void RubyBlocks_Params1() {
            TestOutput(@"
def y; yield 0,1,2,3,4,5,6,7,8,9; end

y { |x0,x1,x2,x3,x4,x5,x6,x7,x8,x9| print x0,x1,x2,x3,x4,x5,x6,x7,x8,x9 }
", @"
0123456789
");
        }

        public void RubyBlocks_Params2() {
            TestOutput(@"
def y; yield 0,1,2,3,4,5,6,7,8,9; end

y { |x0,x1,x2,x3,x4,x5,x6,x7,*z| print x0,x1,x2,x3,x4,x5,x6,x7,z[0],z[1]; }
", @"
0123456789
");
        }

        public void RubyBlocks_Params3() {
            XTestOutput(@"
l = lambda do |a,b=:b,*c,d,&e;f,g|
  p [a,b,c,d,e.class,f,g]
end

l.(1,2) { }
", @"
[1, :b, [], 2, Proc, nil, nil]
");
        }

        public void ProcYieldCaching1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo 
  yield
end

foo { print 'A' }
foo { print 'B' }
foo { print 'C' }
");
            }, "ABC");
        }

        public void ProcCallCaching1() {
            AssertOutput(delegate() {
                CompilerTest(@"
$p = lambda { puts 1 }
$q = lambda { puts 2 } 

$p.call
$q.call
");
            }, @"
1
2
");
        }

        public void BlockDefinition1() {
            TestOutput(@"
a = []
b = []
2.times {
  a << -> {}
  b << proc { }
}
puts a[0] == a[1], a[0].object_id == a[1].object_id, a[0].lambda?, a[1].lambda?
puts b[0] == b[1], b[0].object_id == b[1].object_id, b[0].lambda?, b[1].lambda?
", @"
false
false
true
true
false
false
false
false
");
        }

        public void ProcSelf1() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  1.times { p self }
end        
");
            }, @"M");
        }

        public void RubyBlocks2() {
            AssertExceptionThrown<MissingMethodException>(delegate() {
                CompilerTest(@"
3.times { |x| z = 1 }
puts z # undef z
");
            });
        }

        public void RubyBlocks3() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def foo
    puts 'X',yield(1,2,3)
  end
end

C.new.foo { |a,b,c| puts a,b,c; 'foo' }
");
            }, @"
1
2
3
X
foo
");
        }

        public void RubyBlocks5() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def foo 
    puts block_given?
  end
end

C.new.foo { puts 'goo' }
C.new.foo
            ", 0, 1);
            }, @"
true
false
            ");
        }

        /// <summary>
        /// Break in a block.
        /// </summary>
        public void RubyBlocks7() {
            TestOutput(@"
x = 4.times { |x|
  puts x
  break 'foo'
}
puts x
", @"
0
foo
");
        }

        /// <summary>
        /// Redo in a block.
        /// </summary>
        public void RubyBlocks8() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 0
x = 2.times { |x|
  puts x
  i = i + 1
  if i < 3 then redo end
}
puts x
");
            }, @"
0
0
0
1
2
");
        }

        /// <summary>
        /// Next in a block.
        /// </summary>
        public void RubyBlocks9() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 0
x = 5.times { |x|
  puts x
  i = i + 1  
  if i < 3 then next end
  puts 'bar'
}
", 1, 0);
            }, @"
0
1
2
bar
3
bar
4
bar
");
        }

        /// <summary>
        /// Return with stack unwinding.
        /// </summary>
        public void RubyBlocks11() {
            TestOutputWithEval(@"
def foo
    puts 'begin'
    1.times {
        1.times {
            puts 'block'
            #<return 'result'#>
        }
    }
    puts 'end'
ensure
    puts 'ensure'
end

puts foo
",@"
begin
block
ensure
result
");
        }

        public void RubyBlocks12() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
  yield 1,2,3
end

foo { |a,*b| puts a,'-',b }
");
            }, @"
1
-
2
3
");
        }

        public void RubyBlocks13() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
  yield 1,2,3
end

foo { |a,b| puts a,b }
");
            }, @"
1
2");
        }

        /// <summary>
        /// Nested yielding.
        /// </summary>
        public void RubyBlocks14A() {
            // The eval variant (run first) gives foo no yield of its own: ruby -w warns that the
            // block passed to it may be ignored, and the count is shared by both variants.
            UsingEval((eval) => AssertOutput(() => CompilerTest(Eval(eval, @"
def bar
  yield
  yield  # shouldn't be called
end

def foo
  bar {
    print 'x'
    #<yield#>
  }
end

foo { break }
"), 0, 1), @"
x
"));
        }

        /// <summary>
        /// Covers RubyOps.YieldBlockBreak.
        /// </summary>
        public void RubyBlocks14B() {
            TestOutput(@"
def proc_conv(&b)
  1.times { puts 'yielding'; yield }
end

def foo
  proc_conv { puts 'breaking'; break }
end

foo
", @"
yielding
breaking
");
        }

        /// <summary>
        /// Covers RubyOps.YieldBlockBreak error path.
        /// </summary>
        public void RubyBlocks14C() {
            // With `eval %q{yield}` the method has no yield of its own, and ruby -w warns that the
            // block passed to it may be ignored. The eval variant runs first and the warning count
            // belongs to the runtime both variants share, so both see that one warning.
            UsingEval((eval) => AssertOutput(() => CompilerTest(Eval(eval, @"
def proc_conv(&b)
  $x = b
end

def y
  1.times { #<yield#> rescue p $! }   # proc-converter is not active any more, hence error
end

def foo
  proc_conv { break }
  y(&$x)
end

foo
"), 0, 1), @"
#<LocalJumpError: break from proc-closure>
"));
        }

        /// <summary>
        /// Tests optimization of block break from another block. 
        /// 
        /// Yield yields to a block that breaks to its proc-converter, which is foo.
        /// So the example should retrun 1 from the foo call.
        /// 
        /// Break is propagated thru yields in two ways:
        /// 1) returning ReturnReason == Break via BlockParam (fast path)
        /// 2) throwing MethodUnwinder exception (slow path)
        /// 
        /// ReturnReason should be propagated by yields as long as the owner of the block that contains the yield 
        /// is the target frame for the break. That's the case for for-loop blocks in this test.
        /// </summary>
        public void RubyBlocks16() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
    for j in [0]
        for i in [1]
            yield
        end 
        puts 'Unreachable'
    end
    puts 'Unreachable'
end 

x = foo do
    break 1
end
puts x
", 2, 0);
            }, @"1");
        }

        public void RubyBlocks18() {
            TestOutput(@"
class C
  def y
    yield
  end 

  def foo
    y { p self.class }
  end
end

C.new.foo
", @"
C
");
        }

        /// <summary>
        /// Block return propagates thru a single library method with a block without throwing unwinding exception.
        /// </summary>
        public void BlockReturnOptimization1() {
            StackUnwinder.InstanceCount = 0;
            TestOutput(@"
def foo
  10.times do
    return 123
  end
end

puts foo
", @"
123
");
            Assert(StackUnwinder.InstanceCount == 0);

            StackUnwinder.InstanceCount = 0;
            TestOutput(@"
def foo
  x = proc do
    return 123
  end  
  eval('10.times(&x)')
end

puts foo
", @"
123
", 1);
            Assert(StackUnwinder.InstanceCount == 1);
        }

        /// <summary>
        /// Block return propagates thru multiple library method calls with a block without throwing unwinding exception.
        /// </summary>
        public void BlockReturnOptimization2() {
            StackUnwinder.InstanceCount = 0;
            TestOutput(@"
def foo
  10.times do
    10.times do
      10.times do
        return 123
      end
    end
  end
end

puts foo
", @"
123
");
            Assert(StackUnwinder.InstanceCount == 0);
        }

        /// <summary>
        /// Block return propagates thru user method calls with a block without throwing unwinding exception.
        /// </summary>
        public void BlockReturnOptimization3() {
            StackUnwinder.InstanceCount = 0;
            TestOutput(@"
def f0
  $b = proc { return 123 }
  f1 {}
end

def f1
  f2 {} 
end

def f2 
  f3(&$b)
end

def f3
  yield
end

puts f0
", @"
123
", 0, 2);
            Assert(StackUnwinder.InstanceCount == 0);
        }

        /// <summary>
        /// An unwinding exception is thrown if any frame is called w/o a block.
        /// </summary>
        public void BlockReturnOptimization4() {
            StackUnwinder.InstanceCount = 0;
            TestOutput(@"
def f0
  $b = proc { return 123 }
  f1
end

def f1
  f2 {} 
end

def f2 
  f3(&$b)
end

def f3
  yield
end

puts f0
", @"
123
", 0, 1);
            Assert(StackUnwinder.InstanceCount == 1);
        }

        /// <summary>
        /// Return propagates thru proc/lambda calls.
        /// </summary>
        public void BlockReturnOptimization5() {
            StackUnwinder.InstanceCount = 0;
            TestOutput(@"
def foo
  l = lambda do
    yield 
  end
  l.call
  puts 'un'
end

def bar
  foo { return 123 }
end

p bar
", @"
123
");
            Assert(StackUnwinder.InstanceCount == 1);
        }

        /// <summary>
        /// Return propagates thru proc/lambda calls.
        /// </summary>
        public void BlockReturnOptimization6() {
            StackUnwinder.InstanceCount = 0;
            TestOutput(@"
def make_block(&b); b; end

def foo
  b = make_block { return 123 }
  l = lambda { b.call }
  l.call

  'unreachable'
end

puts foo
", @"
123
");
            Assert(StackUnwinder.InstanceCount == 1);
        }

        /// <summary>
        /// Return propagates thru yield in a block.
        /// </summary>
        public void BlockReturnOptimization7() {
            StackUnwinder.InstanceCount = 0;
            TestOutput(@"
def f1
  f2 { return 123 } 
  puts 'unreachable'
end

def f2
  1.times { yield }
end

p f1
", @"
123
");
            Assert(StackUnwinder.InstanceCount == 0);

            TestOutput(@"
def f1
  f2 { return 123 } 
  puts 'unreachable'
end

def f2
  1.times { eval('yield') }
end

p f1
", @"
123
");
            Assert(StackUnwinder.InstanceCount == 1);
        }

        public void Scenario_RubyProcCallArgs1() {
            TestOutput(@"
lambda { |x| p x }.call rescue p $!
lambda { |x| p x }.call 1
lambda { |x| p x }.call 1,2 rescue p $!
lambda { |x| p x }.call []
lambda { |x| p x }.call [1]
lambda { |x| p x }.call [1,2]
lambda { |x| p x }.call(*[1])
lambda { |x,| p x }.call 1
lambda { |x,| p x }.call [1]
lambda { |x,| p x }.call [1,2]
lambda { |(x,y)| p [x,y] }.call [1,2]
", @"
#<ArgumentError: wrong number of arguments (given 0, expected 1)>
1
#<ArgumentError: wrong number of arguments (given 2, expected 1)>
[]
[1]
[1, 2]
1
1
[1]
[1, 2]
[1, 2]
");

            TestOutput(@"
proc { |x| p x }.call
proc { |x| p x }.call 1
proc { |x| p x }.call 1,2
proc { |x| p x }.call 1,2,3
proc { |x| p x }.call 1,2,3,4
proc { |x| p x }.call 1,2,3,4,5
proc { |x| p x }.call []
proc { |x| p x }.call [1]
proc { |x| p x }.call [1,2]
proc { |x| p x }.call(*[])
proc { |x| p x }.call(*[1])
proc { |x| p x }.call(*[1,2])
proc { |x| p x }.call(1,*[2])
proc { |x| p x }.call(1,2,*[3])
proc { |x| p x }.call(1,2,3,*[4])
proc { |x| p x }.call(1,2,3,4,*[5])
proc { |x| p x }.call(1,2,3,4,5,*[6])

proc { |x,| puts x.inspect }.call 1
proc { |x,| puts x.inspect }.call [1]
proc { |x,| puts x.inspect }.call [1,2]
proc { |(x,y)| puts [x,y].inspect }.call [1,2]
", @"
nil
1
1
1
1
1
[]
[1]
[1, 2]
nil
1
1
1
1
1
1
1
1
1
1
[1, 2]
");
        }

        public void Scenario_RubyProcCallArgs2A() {
            AssertOutput(delegate() {
                CompilerTest(@"
lambda { |x,y| p [x, y] }.call rescue puts 'error'
lambda { |x,y| p [x, y] }.call 1 rescue puts 'error'
lambda { |x,y| p [x, y] }.call 1,2
lambda { |x,y| p [x, y] }.call [] rescue puts 'error'
lambda { |x,y| p [x, y] }.call [1] rescue puts 'error'
lambda { |x,y| p [x, y] }.call [1,2] rescue puts 'error'
lambda { |x,y| p [x, y] }.call *[1,2] 
lambda { |x,y| p [x, y] }.call *[[1]] rescue puts 'error'
lambda { |x,y| p [x, y] }.call *[[1,2]] rescue puts 'error'
lambda { |x,y| p [x, y] }.call *[[1,2,3]] rescue puts 'error'
", 4, 0);
            }, @"
error
error
[1, 2]
error
error
error
[1, 2]
error
error
error
");
        }

        public void Scenario_RubyProcCallArgs2B() {
            AssertOutput(delegate() {
                CompilerTest(@"
Proc.new { |x,y| p [x, y] }.call 
Proc.new { |x,y| p [x, y] }.call 1
Proc.new { |x,y| p [x, y] }.call 1,2
Proc.new { |x,y| p [x, y] }.call []
Proc.new { |x,y| p [x, y] }.call [1]
Proc.new { |x,y| p [x, y] }.call [1,2]
Proc.new { |x,y| p [x, y] }.call *[1,2] 
Proc.new { |x,y| p [x, y] }.call *[[1]]
Proc.new { |x,y| p [x, y] }.call *[[1,2]]
Proc.new { |x,y| p [x, y] }.call *[[1,2,3]]
", 4, 0);
            }, @"
[nil, nil]
[1, nil]
[1, 2]
[nil, nil]
[1, nil]
[1, 2]
[1, 2]
[1, nil]
[1, 2]
[1, 2]
");
        }

        public void Scenario_RubyProcCallArgs2C() {
            AssertOutput(delegate() {
                CompilerTest(@"
Proc.new { || p [] }.call 
Proc.new { |x| p [x] }.call 1
Proc.new { |x,y| p [x,y] }.call 1,2
Proc.new { |x,y,z| p [x,y,z] }.call 1,2,3
Proc.new { |x,y,z,w| p [x,y,z,w] }.call 1,2,3,4
Proc.new { |x,y,z,w,u| p [x,y,z,w,u] }.call 1,2,3,4,5
Proc.new { |x,y,z,w,u,v| p [x,y,z,w,u,v] }.call 1,2,3,4,5,6
");
            }, @"
[]
[1]
[1, 2]
[1, 2, 3]
[1, 2, 3, 4]
[1, 2, 3, 4, 5]
[1, 2, 3, 4, 5, 6]
");
        }

        /// <summary>
        /// Tests MRI inconsistency in Yield1 vs YieldNoSplat1 when invoked from Call1.
        /// </summary>
        public void Scenario_RubyProcCallArgs2D() {
            TestOutput(@"
f = proc {|x,| x}
p f.call(1)
p f.call([1])
p f.call([[1]])
p f.call([1,2])

f = lambda {|x,| x}
p f.call(1)
p f.call([1])
p f.call([[1]])
p f.call([1,2])
", @"
1
1
[1]
1
1
[1]
[[1]]
[1, 2]
");
        }

        public void Scenario_RubyProcYieldArgs1() {
            TestOutput(@"
def y0; yield; end
def y1; yield 1; end
def y2; yield 1,2; end
def y3; yield 1,2,3; end
def y4; yield 1,2,3,4; end
def y5; yield 1,2,3,4,5; end

puts '- L(0,0) -'
y0 { || p [] }
y1 { || p [] }

puts '- L(1,0) -'
y0 { |x| p [x] }
y1 { |x| p [x] }
y2 { |x| p [x] }
y3 { |x| p [x] }
y4 { |x| p [x] }
y5 { |x| p [x] }

puts '- L(2,0) -'
y0 { |x,y| p [x,y] }
y1 { |x,y| p [x,y] }
y2 { |x,y| p [x,y] }
y3 { |x,y| p [x,y] }
y4 { |x,y| p [x,y] }
y5 { |x,y| p [x,y] }

puts '- L(3,0) -'
y0 { |x,y,z| p [x,y,z] }
y1 { |x,y,z| p [x,y,z] }
y2 { |x,y,z| p [x,y,z] }
y3 { |x,y,z| p [x,y,z] }
y4 { |x,y,z| p [x,y,z] }
y5 { |x,y,z| p [x,y,z] }

puts '- L(4,0) -'
y0 { |x,y,z,u| p [x,y,z,u] }
y1 { |x,y,z,u| p [x,y,z,u] }
y2 { |x,y,z,u| p [x,y,z,u] }
y3 { |x,y,z,u| p [x,y,z,u] }
y4 { |x,y,z,u| p [x,y,z,u] }
y5 { |x,y,z,u| p [x,y,z,u] }

puts '- L(5,0) -'
y0 { |x,y,z,u,v| p [x,y,z,u,v] }
y1 { |x,y,z,u,v| p [x,y,z,u,v] }
y2 { |x,y,z,u,v| p [x,y,z,u,v] }
y3 { |x,y,z,u,v| p [x,y,z,u,v] }
y4 { |x,y,z,u,v| p [x,y,z,u,v] }
y5 { |x,y,z,u,v| p [x,y,z,u,v] }
", @"
- L(0,0) -
[]
[]
- L(1,0) -
[nil]
[1]
[1]
[1]
[1]
[1]
- L(2,0) -
[nil, nil]
[1, nil]
[1, 2]
[1, 2]
[1, 2]
[1, 2]
- L(3,0) -
[nil, nil, nil]
[1, nil, nil]
[1, 2, nil]
[1, 2, 3]
[1, 2, 3]
[1, 2, 3]
- L(4,0) -
[nil, nil, nil, nil]
[1, nil, nil, nil]
[1, 2, nil, nil]
[1, 2, 3, nil]
[1, 2, 3, 4]
[1, 2, 3, 4]
- L(5,0) -
[nil, nil, nil, nil, nil]
[1, nil, nil, nil, nil]
[1, 2, nil, nil, nil]
[1, 2, 3, nil, nil]
[1, 2, 3, 4, nil]
[1, 2, 3, 4, 5]
");
        }

        public void Scenario_RubyProcYieldArgs2() {
            TestOutput(@"
def y00; yield(*[]); end
def y01; yield(*[1]); end
def y02; yield(*[1,2]); end
def y11; yield(1,*[2]); end
def y21; yield(1,2,*[3]); end
def y31; yield(1,2,3,*[4]); end
def y41; yield(1,2,3,4,*[5]); end

puts '- L(0,0) -'
y00 { || p [] }
y01 { || p [] }
y02 { || p [] }
y11 { || p [] }
y21 { || p [] }
y31 { || p [] }
y41 { || p [] }

puts '- L(1,0) -'
y00 { |x| p [x] }
y01 { |x| p [x] }
y02 { |x| p [x] }
y11 { |x| p [x] }
y21 { |x| p [x] }
y31 { |x| p [x] }
y41 { |x| p [x] }

puts '- L(2,0) -'
y00 { |x,y| p [x,y] }
y01 { |x,y| p [x,y] }
y02 { |x,y| p [x,y] }
y11 { |x,y| p [x,y] }
y21 { |x,y| p [x,y] }
y31 { |x,y| p [x,y] }
y41 { |x,y| p [x,y] }

puts '- L(3,0) -'
y00 { |x,y,z| p [x,y,z] }
y01 { |x,y,z| p [x,y,z] }
y02 { |x,y,z| p [x,y,z] }
y11 { |x,y,z| p [x,y,z] }
y21 { |x,y,z| p [x,y,z] }
y31 { |x,y,z| p [x,y,z] }
y41 { |x,y,z| p [x,y,z] }

puts '- L(4,0) -'
y00 { |x,y,z,u| p [x,y,z,u] }
y01 { |x,y,z,u| p [x,y,z,u] }
y02 { |x,y,z,u| p [x,y,z,u] }
y11 { |x,y,z,u| p [x,y,z,u] }
y21 { |x,y,z,u| p [x,y,z,u] }
y31 { |x,y,z,u| p [x,y,z,u] }
y41 { |x,y,z,u| p [x,y,z,u] }

puts '- L(5,0) -'
y00 { |x,y,z,u,v| p [x,y,z,u,v] }
y01 { |x,y,z,u,v| p [x,y,z,u,v] }
y02 { |x,y,z,u,v| p [x,y,z,u,v] }
y11 { |x,y,z,u,v| p [x,y,z,u,v] }
y21 { |x,y,z,u,v| p [x,y,z,u,v] }
y31 { |x,y,z,u,v| p [x,y,z,u,v] }
y41 { |x,y,z,u,v| p [x,y,z,u,v] }
", @"
- L(0,0) -
[]
[]
[]
[]
[]
[]
[]
- L(1,0) -
[nil]
[1]
[1]
[1]
[1]
[1]
[1]
- L(2,0) -
[nil, nil]
[1, nil]
[1, 2]
[1, 2]
[1, 2]
[1, 2]
[1, 2]
- L(3,0) -
[nil, nil, nil]
[1, nil, nil]
[1, 2, nil]
[1, 2, nil]
[1, 2, 3]
[1, 2, 3]
[1, 2, 3]
- L(4,0) -
[nil, nil, nil, nil]
[1, nil, nil, nil]
[1, 2, nil, nil]
[1, 2, nil, nil]
[1, 2, 3, nil]
[1, 2, 3, 4]
[1, 2, 3, 4]
- L(5,0) -
[nil, nil, nil, nil, nil]
[1, nil, nil, nil, nil]
[1, 2, nil, nil, nil]
[1, 2, nil, nil, nil]
[1, 2, 3, nil, nil]
[1, 2, 3, 4, nil]
[1, 2, 3, 4, 5]
");
        }

        public void Scenario_RubyProcYieldArgs3() {
            TestOutput(@"
def y0; yield []; end
def y1; yield [1]; end
def y2; yield [1,2]; end
def y3; yield [1,2,3]; end
def y4; yield [1,2,3,4]; end
def y5; yield [1,2,3,4,5]; end

y0 { |x|         p [x] } 
y0 { |x,y|       p [x,y] }
y0 { |x,y,z|     p [x,y,z] }
y0 { |x,y,z,u|   p [x,y,z,u] }
y0 { |x,y,z,u,v| p [x,y,z,u,v] }

y1 { |x|         p [x] } 
y1 { |x,y|       p [x,y] }
y1 { |x,y,z|     p [x,y,z] }
y1 { |x,y,z,u|   p [x,y,z,u] }
y1 { |x,y,z,u,v| p [x,y,z,u,v] }

y2 { |x|         p [x] } 
y2 { |x,y|       p [x,y] }
y2 { |x,y,z|     p [x,y,z] }
y2 { |x,y,z,u|   p [x,y,z,u] }
y2 { |x,y,z,u,v| p [x,y,z,u,v] }

y3 { |x|         p [x] } 
y3 { |x,y|       p [x,y] }
y3 { |x,y,z|     p [x,y,z] }
y3 { |x,y,z,u|   p [x,y,z,u] }
y3 { |x,y,z,u,v| p [x,y,z,u,v] }

y4 { |x|         p [x] } 
y4 { |x,y|       p [x,y] }
y4 { |x,y,z|     p [x,y,z] }
y4 { |x,y,z,u|   p [x,y,z,u] }
y4 { |x,y,z,u,v| p [x,y,z,u,v] }

y5 { |x|         p [x] } 
y5 { |x,y|       p [x,y] }
y5 { |x,y,z|     p [x,y,z] }
y5 { |x,y,z,u|   p [x,y,z,u] }
y5 { |x,y,z,u,v| p [x,y,z,u,v] }
", @"
[[]]
[nil, nil]
[nil, nil, nil]
[nil, nil, nil, nil]
[nil, nil, nil, nil, nil]
[[1]]
[1, nil]
[1, nil, nil]
[1, nil, nil, nil]
[1, nil, nil, nil, nil]
[[1, 2]]
[1, 2]
[1, 2, nil]
[1, 2, nil, nil]
[1, 2, nil, nil, nil]
[[1, 2, 3]]
[1, 2]
[1, 2, 3]
[1, 2, 3, nil]
[1, 2, 3, nil, nil]
[[1, 2, 3, 4]]
[1, 2]
[1, 2, 3]
[1, 2, 3, 4]
[1, 2, 3, 4, nil]
[[1, 2, 3, 4, 5]]
[1, 2]
[1, 2, 3]
[1, 2, 3, 4]
[1, 2, 3, 4, 5]
");
        }

        public void Scenario_RubyProcYieldArgs4() {
            TestOutput(@"
def y00; yield(*[[]]); end
def y01; yield(*[[1]]); end
def y02; yield(*[[1,2]]); end
def y03; yield(*[[1,2,3]]); end
def y04; yield(*[[1,2,3,4]]); end
def y10; yield([],*[[]]); end
def y11; yield([:a],*[[1]]); end
def y12; yield([:a,:b],*[[1,2]]); end
def y13; yield([:a,:b,:c],*[[1,2,3]]); end
def y14; yield([:a,:b,:c,:d],*[[1,2,3,4]]); end

puts '- L(0,0) -'
y00 { || p [] }
y01 { || p [] }
y02 { || p [] }
y03 { || p [] }
y04 { || p [] }
y10 { || p [] }
y11 { || p [] }
y12 { || p [] }
y13 { || p [] }
y14 { || p [] }

puts '- L(1,0) -'
y00 { |x| p [x] }
y01 { |x| p [x] }
y02 { |x| p [x] }
y03 { |x| p [x] }
y04 { |x| p [x] }
y10 { |x| p [x] }
y11 { |x| p [x] }
y12 { |x| p [x] }
y13 { |x| p [x] }
y14 { |x| p [x] }

puts '- L(2,0) -'
y00 { |x,y| p [x,y] }
y01 { |x,y| p [x,y] }
y02 { |x,y| p [x,y] }
y03 { |x,y| p [x,y] }
y04 { |x,y| p [x,y] }
y10 { |x,y| p [x,y] }
y11 { |x,y| p [x,y] }
y12 { |x,y| p [x,y] }
y13 { |x,y| p [x,y] }
y14 { |x,y| p [x,y] }

puts '- L(3,0) -'
y00 { |x,y,z| p [x,y,z] }
y01 { |x,y,z| p [x,y,z] }
y02 { |x,y,z| p [x,y,z] }
y03 { |x,y,z| p [x,y,z] }
y04 { |x,y,z| p [x,y,z] }
y10 { |x,y,z| p [x,y,z] }
y11 { |x,y,z| p [x,y,z] }
y12 { |x,y,z| p [x,y,z] }
y13 { |x,y,z| p [x,y,z] }
y14 { |x,y,z| p [x,y,z] }

puts '- L(4,0) -'
y00 { |x,y,z,u| p [x,y,z,u] }
y01 { |x,y,z,u| p [x,y,z,u] }
y02 { |x,y,z,u| p [x,y,z,u] }
y03 { |x,y,z,u| p [x,y,z,u] }
y04 { |x,y,z,u| p [x,y,z,u] }
y10 { |x,y,z,u| p [x,y,z,u] }
y11 { |x,y,z,u| p [x,y,z,u] }
y12 { |x,y,z,u| p [x,y,z,u] }
y13 { |x,y,z,u| p [x,y,z,u] }
y14 { |x,y,z,u| p [x,y,z,u] }

puts '- L(5,0) -'
y00 { |x,y,z,u,v| p [x,y,z,u,v] }
y01 { |x,y,z,u,v| p [x,y,z,u,v] }
y02 { |x,y,z,u,v| p [x,y,z,u,v] }
y03 { |x,y,z,u,v| p [x,y,z,u,v] }
y04 { |x,y,z,u,v| p [x,y,z,u,v] }
y10 { |x,y,z,u,v| p [x,y,z,u,v] }
y11 { |x,y,z,u,v| p [x,y,z,u,v] }
y12 { |x,y,z,u,v| p [x,y,z,u,v] }
y13 { |x,y,z,u,v| p [x,y,z,u,v] }
y14 { |x,y,z,u,v| p [x,y,z,u,v] }

puts '- L(0, *) -'
y00 { |*x| p x }
y01 { |*x| p x }
y02 { |*x| p x }
y03 { |*x| p x }
y04 { |*x| p x }
y10 { |*x| p x }
y11 { |*x| p x }
y12 { |*x| p x }
y13 { |*x| p x }
y14 { |*x| p x }

puts '- L(1, *) -'
y00 { |x,*y| p [x,y] }
y01 { |x,*y| p [x,y] }
y02 { |x,*y| p [x,y] }
y03 { |x,*y| p [x,y] }
y04 { |x,*y| p [x,y] }
y10 { |x,*y| p [x,y] }
y11 { |x,*y| p [x,y] }
y12 { |x,*y| p [x,y] }
y13 { |x,*y| p [x,y] }
y14 { |x,*y| p [x,y] }

puts '- L(2, *) -'
y00 { |x,y,*z| p [x,y] }
y01 { |x,y,*z| p [x,y] }
y02 { |x,y,*z| p [x,y] }
y03 { |x,y,*z| p [x,y] }
y04 { |x,y,*z| p [x,y] }
y10 { |x,y,*z| p [x,y] }
y11 { |x,y,*z| p [x,y] }
y12 { |x,y,*z| p [x,y] }
y13 { |x,y,*z| p [x,y] }
y14 { |x,y,*z| p [x,y] }

puts '- L(1, &) -'
y00 { |x,&y| p x }
y01 { |x,&y| p x }
y02 { |x,&y| p x }
y03 { |x,&y| p x }
y04 { |x,&y| p x }
y10 { |x,&y| p x }
y11 { |x,&y| p x }
y12 { |x,&y| p x }
y13 { |x,&y| p x }
y14 { |x,&y| p x }

puts '- L(2, &) -'
y00 { |x,y,&z| p [x,y] }
y01 { |x,y,&z| p [x,y] }
y02 { |x,y,&z| p [x,y] }
y03 { |x,y,&z| p [x,y] }
y04 { |x,y,&z| p [x,y] }
y10 { |x,y,&z| p [x,y] }
y11 { |x,y,&z| p [x,y] }
y12 { |x,y,&z| p [x,y] }
y13 { |x,y,&z| p [x,y] }
y14 { |x,y,&z| p [x,y] }

puts '- L(0, *, &) -'
y00 { |*x,&y| p x }
y01 { |*x,&y| p x }
y02 { |*x,&y| p x }
y03 { |*x,&y| p x }
y04 { |*x,&y| p x }
y10 { |*x,&y| p x }
y11 { |*x,&y| p x }
y12 { |*x,&y| p x }
y13 { |*x,&y| p x }
y14 { |*x,&y| p x }

puts '- L(1, *, &) -'
y00 { |x,*y,&z| p [x,y] }
y01 { |x,*y,&z| p [x,y] }
y02 { |x,*y,&z| p [x,y] }
y03 { |x,*y,&z| p [x,y] }
y04 { |x,*y,&z| p [x,y] }
y10 { |x,*y,&z| p [x,y] }
y11 { |x,*y,&z| p [x,y] }
y12 { |x,*y,&z| p [x,y] }
y13 { |x,*y,&z| p [x,y] }
y14 { |x,*y,&z| p [x,y] }

puts '- L(2, *, &) -'
y00 { |x,y,*z,&w| p [x,y,z] }
y01 { |x,y,*z,&w| p [x,y,z] }
y02 { |x,y,*z,&w| p [x,y,z] }
y03 { |x,y,*z,&w| p [x,y,z] }
y04 { |x,y,*z,&w| p [x,y,z] }
y10 { |x,y,*z,&w| p [x,y,z] }
y11 { |x,y,*z,&w| p [x,y,z] }
y12 { |x,y,*z,&w| p [x,y,z] }
y13 { |x,y,*z,&w| p [x,y,z] }
y14 { |x,y,*z,&w| p [x,y,z] }
", @"
- L(0,0) -
[]
[]
[]
[]
[]
[]
[]
[]
[]
[]
- L(1,0) -
[[]]
[[1]]
[[1, 2]]
[[1, 2, 3]]
[[1, 2, 3, 4]]
[[]]
[[:a]]
[[:a, :b]]
[[:a, :b, :c]]
[[:a, :b, :c, :d]]
- L(2,0) -
[nil, nil]
[1, nil]
[1, 2]
[1, 2]
[1, 2]
[[], []]
[[:a], [1]]
[[:a, :b], [1, 2]]
[[:a, :b, :c], [1, 2, 3]]
[[:a, :b, :c, :d], [1, 2, 3, 4]]
- L(3,0) -
[nil, nil, nil]
[1, nil, nil]
[1, 2, nil]
[1, 2, 3]
[1, 2, 3]
[[], [], nil]
[[:a], [1], nil]
[[:a, :b], [1, 2], nil]
[[:a, :b, :c], [1, 2, 3], nil]
[[:a, :b, :c, :d], [1, 2, 3, 4], nil]
- L(4,0) -
[nil, nil, nil, nil]
[1, nil, nil, nil]
[1, 2, nil, nil]
[1, 2, 3, nil]
[1, 2, 3, 4]
[[], [], nil, nil]
[[:a], [1], nil, nil]
[[:a, :b], [1, 2], nil, nil]
[[:a, :b, :c], [1, 2, 3], nil, nil]
[[:a, :b, :c, :d], [1, 2, 3, 4], nil, nil]
- L(5,0) -
[nil, nil, nil, nil, nil]
[1, nil, nil, nil, nil]
[1, 2, nil, nil, nil]
[1, 2, 3, nil, nil]
[1, 2, 3, 4, nil]
[[], [], nil, nil, nil]
[[:a], [1], nil, nil, nil]
[[:a, :b], [1, 2], nil, nil, nil]
[[:a, :b, :c], [1, 2, 3], nil, nil, nil]
[[:a, :b, :c, :d], [1, 2, 3, 4], nil, nil, nil]
- L(0, *) -
[[]]
[[1]]
[[1, 2]]
[[1, 2, 3]]
[[1, 2, 3, 4]]
[[], []]
[[:a], [1]]
[[:a, :b], [1, 2]]
[[:a, :b, :c], [1, 2, 3]]
[[:a, :b, :c, :d], [1, 2, 3, 4]]
- L(1, *) -
[nil, []]
[1, []]
[1, [2]]
[1, [2, 3]]
[1, [2, 3, 4]]
[[], [[]]]
[[:a], [[1]]]
[[:a, :b], [[1, 2]]]
[[:a, :b, :c], [[1, 2, 3]]]
[[:a, :b, :c, :d], [[1, 2, 3, 4]]]
- L(2, *) -
[nil, nil]
[1, nil]
[1, 2]
[1, 2]
[1, 2]
[[], []]
[[:a], [1]]
[[:a, :b], [1, 2]]
[[:a, :b, :c], [1, 2, 3]]
[[:a, :b, :c, :d], [1, 2, 3, 4]]
- L(1, &) -
[]
[1]
[1, 2]
[1, 2, 3]
[1, 2, 3, 4]
[]
[:a]
[:a, :b]
[:a, :b, :c]
[:a, :b, :c, :d]
- L(2, &) -
[nil, nil]
[1, nil]
[1, 2]
[1, 2]
[1, 2]
[[], []]
[[:a], [1]]
[[:a, :b], [1, 2]]
[[:a, :b, :c], [1, 2, 3]]
[[:a, :b, :c, :d], [1, 2, 3, 4]]
- L(0, *, &) -
[[]]
[[1]]
[[1, 2]]
[[1, 2, 3]]
[[1, 2, 3, 4]]
[[], []]
[[:a], [1]]
[[:a, :b], [1, 2]]
[[:a, :b, :c], [1, 2, 3]]
[[:a, :b, :c, :d], [1, 2, 3, 4]]
- L(1, *, &) -
[nil, []]
[1, []]
[1, [2]]
[1, [2, 3]]
[1, [2, 3, 4]]
[[], [[]]]
[[:a], [[1]]]
[[:a, :b], [[1, 2]]]
[[:a, :b, :c], [[1, 2, 3]]]
[[:a, :b, :c, :d], [[1, 2, 3, 4]]]
- L(2, *, &) -
[nil, nil, []]
[1, nil, []]
[1, 2, []]
[1, 2, [3]]
[1, 2, [3, 4]]
[[], [], []]
[[:a], [1], []]
[[:a, :b], [1, 2], []]
[[:a, :b, :c], [1, 2, 3], []]
[[:a, :b, :c, :d], [1, 2, 3, 4], []]
");
        }


        /// <summary>
        /// RHS is list, LHS is not simple, but contains splatting.
        /// </summary>
        public void Scenario_RubyBlockArgs3() {
            AssertOutput(delegate() {
                CompilerTest(@"
def baz
   yield [1,2,3]
end
baz { |*a| puts a.inspect }
");
            }, @"[[1, 2, 3]]");
        }

        /// <summary>
        /// !L(1,-) and R(0,*), empty array to splat.
        /// </summary>
        public void Scenario_RubyBlockArgs4A() {
            AssertOutput(delegate() {
                CompilerTest(@"
def y
   yield *[]
end

y { |*a| puts a.inspect }
", 1, 0);
            }, @"[]");
        }

        /// <summary>
        /// Anonymous unsplat parameters.
        /// </summary>
        public void Scenario_RubyBlockArgs4B() {
            TestOutput(@"
def y
  a = [1,2,3,4,5]
  yield a,[6]
end

y { |(x,y,*),*| p x,y }
puts '-'
y { |(x,y,*a),*| p x,y,a }
puts '-'
y { |(x,y,*),*a| p x,y,a }
", @"
1
2
-
1
2
[3, 4, 5]
-
1
2
[[6]]
");
        }

        public void Scenario_RubyBlockArgs5() {
            TestOutput(@"
c1 = class C1
  define_method('[]=') { |a| }
  new
end

c2 = class C2
  define_method('[]=') { |a,b| p [a,b] }
  new
end

c3 = class C3
  define_method('[]=') { |a,b,c| p [a,b,c] }
  new
end

c4 = class C4
  define_method('[]=') { |a,b,c,d| p [a,b,c,d] }
  new
end

c5 = class C5
  define_method('[]=') { |a,b,c,d,e| p [a,b,c,d,e] }
  new
end

c6 = class C6
  define_method('[]=') { |a,*b| p [a,b] }
  new
end

#(c1[1] = 2) rescue p $!              # TODO: raise exception
#(c1[1,*[]] = 2) rescue p $!          # TODO: raise exception
#(c2[1,2] = 3) rescue p $!            # TODO: raise exception
#(c2[*[1,2]] = 3) rescue p $!         # TODO: raise exception
c3[1,2,*[]] = 3
c4[1,*[2,3]] = 4
c5[1,2,*[3,4]] = 5
c6[1,2] = 3
c6[1,*[2,3,4,5,6]] = 7
", 
// #<ArgumentError: wrong number of arguments (given 2, expected 1)>
// #<ArgumentError: wrong number of arguments (given 2, expected 1)>
// #<ArgumentError: wrong number of arguments (given 3, expected 2)>
// #<ArgumentError: wrong number of arguments (given 3, expected 2)>
@"
[1, 2, 3]
[1, 2, 3, 4]
[1, 2, 3, 4, 5]
[1, [2, 3]]
[1, [2, 3, 4, 5, 6, 7]]
", 2);
        }

        public void Scenario_RubyBlockArgs6() {
            TestOutput(@"
class C
  def to_a
    [1,2]
  end
end

def baz
  yield C.new
end
baz { |a,b| p b }

class C
  def to_ary
    1
  end
end
baz { |a,b| p b } rescue p $!

class C
  def to_ary
    [3,4]
  end
end
baz { |a,b| p b }
", @"
nil
#<TypeError: can't convert C into Array (C#to_ary gives Integer)>
4
");
        }
        
        public void RubyProcs1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo x,y,&f
  yield x,y
  f[3,4]
end

foo(1,2) do |a,b|
  puts a,b
end
");
            }, @"
1
2
3
4
");
        }

        /// <summary>
        /// Assigning to a block parameter should not affect yield.
        /// </summary>
        public void RubyProcs2() {
            TestOutput(@"
def foo(&b)
  b = nil
  yield
end

foo { puts 'foo' }
", @"
foo
");
        }

        public void RubyProcArgConversion1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def to_proc
    lambda { |x| puts x }
  end
end

class D
  def to_proc
    lambda { |x| puts x + 1 }
  end
end

class E  
end

1.times(&C.new)
1.times(&D.new)
1.times(&E.new) rescue puts 'error'
");
            }, @"
0
1
error
");
        }

        public void RubyProcArgConversion2() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def to_proc; 1; end
end

class D
  def to_proc; lambda { |i| puts 'ok2' }; end
end

1.times(&lambda { |i| puts 'ok1' })
1.times(&C.new) rescue puts $!
1.times(&D.new)
");
            }, @"
ok1
can't convert C into Proc (C#to_proc gives Integer)
ok2
");
        }

        public void RubyProcArgConversion3() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo &b
  p b
end

foo(&nil)
");
            }, @"nil");
        }

        public void RubyProcArgConversion4() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def respond_to? name
    puts name
    $has_to_proc
  end

  def to_proc
    lambda { |i| puts 'ok' }
  end
end

c = C.new

$has_to_proc = false
1.times(&c) rescue puts 'error'

$has_to_proc = true
1.times(&c)
");
            }, @"
to_proc
error
to_proc
ok
");
        }

        /// <summary>
        /// Ruby 3.0 removed Proc.new's habit of adopting the enclosing method's block, so these
        /// two now raise instead of producing that block.
        /// </summary>
        public void ProcNew1() {
            TestOutput(@"
def foo
  1.times { |x| $x = Proc.new }
end

y = lambda { puts 'foo' }
begin
  foo(&y)
rescue ArgumentError => e
  puts e.message
end
", @"
tried to create Proc object without a block
", 0, 1);
        }

        public void ProcNew2() {
            TestOutput(@"
class P < Proc
end

def foo
  1.times { |x| $x = P.new }
end

y = lambda { puts 'foo' }
begin
  foo(&y)
rescue ArgumentError => e
  puts e.message
end
", @"
tried to create Proc object without a block
", 0, 1);
        }

        public void ProcNew4() {
            TestOutput(@"
class P < Proc
  def initialize *args, &b
    p args, b.class, self.class, block_given?
    123
  end
end

def arg
  puts 'arg'
  []
end

def foo
  P.new(*arg)
end

begin
  foo { puts 1 }
rescue ArgumentError => e
  puts e.message
end
", @"
arg
tried to create Proc object without a block
", 0, 1);
        }
        
        /// <summary>
        /// TODO: 1.9 actually doesn't allow proc.binding
        /// </summary>
        public void MethodToProc1() {
            AssertOutput(() => CompilerTest(@"
class C
  def foo a=:a, b=:b, *args
    p self, a, b, args
    123
  end
end

class D < C
end

x = 'hello'
q = C.new.method(:foo).to_proc
p q[1]
p q[] rescue p $!

# the proc's binding: MRI's self is the method's receiver rather than the caller's.
# (MRI's binding has no locals either - `x' there is a NameError - where this one still
# reaches the scope the to_proc call was made in.)
eval('puts x, self', q.binding)

p D.new.instance_eval(&q)
", 1, 0), @"
#<C:0x*>
1
:b
[]
123
#<C:0x*>
:a
:b
[]
123
hello
#<C:0x*>
#<C:0x*>
#<D:0x*>
:b
[]
123
", OutputFlags.Match);
        }

        public void DefineMethod1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def foo
    $x = lambda {
      puts self.class
    }
    
    $x.call
  end
end

C.new.foo

class D
  define_method :goo, &$x
end

D.new.goo
");
            }, @"
C
D");
        }

        /// <summary>
        /// define_method and class_eval change owner of the method definition.
        /// </summary>
        public void DefineMethod2() {
            // TODO:
            TestOutput(@"
module M
  $p = lambda {    
    def goo
      def bar
        puts 'bar'
      end
    end  
  }      
end

class C
  define_method :foo,&$p
end

class D
 class_eval(&$p)
end

c = C.new
d = D.new

d.goo
c.foo
c.goo

p M.instance_methods(false).sort
p C.instance_methods(false).sort
p D.instance_methods(false).sort
", @"
#<NoMethodError: undefined method `goo' for #<C:0x24c5a00>>
[:goo]
[:foo]
[:bar, :goo]
");

        }

        public void ProcPosition1() {
             AssertOutput(() => CompilerTest(@"
def foo &q 
  p q
end

class C < Proc
end

foo { }                         # line 9
p lambda {}                     
p Proc.new {}                   
p method(:foo).to_proc          # TODO: source info not available
p C.new {}                      
"), @"
#<Proc:0x* *ProcPosition1.rb:9>
#<Proc:0x* *ProcPosition1.rb:10 (lambda)>
#<Proc:0x* *ProcPosition1.rb:11>
#<Proc:0x* (lambda)>
#<C:0x* *ProcPosition1.rb:13>
", OutputFlags.Match);
        }

        public void BlockArity1() {
            TestOutput(@"
puts '== -4'

p Proc.new{|(a,b,c,*)|}.arity

puts '== -2'

p Proc.new{|(a,b,c,*),*|}.arity

puts '== -1'

p Proc.new{|(*)|}.arity
p Proc.new{}.arity
p Proc.new{|*|}.arity

puts '== 0'

p Proc.new{||}.arity

puts '== 1'

p Proc.new{|x|}.arity    
p Proc.new{|x,|}.arity    
p Proc.new{|((x,y))|}.arity
p Proc.new{|(*),|}.arity
p Proc.new{|(x,*),|}.arity

puts '== 2'

p Proc.new{|x,y|}.arity  
p Proc.new{|x,y,|}.arity 
p Proc.new{|x,y,|}.arity 
p Proc.new{|(x,y)|}.arity

puts '== 3'

p Proc.new{|x,y,z|}.arity
p Proc.new{|x,y,z,|}.arity
p Proc.new{|(x,y,z)|}.arity
", @"
== -4
1
== -2
-2
== -1
1
0
-1
== 0
0
== 1
1
1
1
1
1
== 2
2
2
2
1
== 3
3
3
1
");
        }
        
        public void Proc_RhsAndBlockArguments1() {
            AssertOutput(() => CompilerTest(@"
class Proc
  alias []= []
end

x = Proc.new { |*a| p a, block_given? }

x.call(1,2,&lambda {})
x[1,2] = 3

"), @"
[1, 2]
false
[1, 2, 3]
false
");
        }

        public void Block_ProcParams1() {
            TestOutput(@"
lambda { |&q| p [q.class] }.() {}
lambda { |a,&q| p [a,q.class] }.(1) {}
lambda { |a,b,&q| p [a,b,q.class] }.(1,2) {}
lambda { |a,b,c,&q| p [a,b,c,q.class] }.(1,2,3) {}
lambda { |a,b,c,d,&q| p [a,b,c,d,q.class] }.(1,2,3,4) {}
lambda { |a,b,c,d,e,f,g,&q| p [a,b,c,d,e,f,g,q.class] }.(1,2,3,4,5,6,7) {}

lambda { |&q| p [q.class] }.()
lambda { |a,&q| p [a,q.class] }.(1)
lambda { |a,b,&q| p [a,b,q.class] }.(1,2)
lambda { |a,b,c,&q| p [a,b,c,q.class] }.(1,2,3)
lambda { |a,b,c,d,&q| p [a,b,c,d,q.class] }.(1,2,3,4)
lambda { |a,b,c,d,e,f,g,&q| p [a,b,c,d,e,f,g,q.class] }.(1,2,3,4,5,6,7)

lambda { |a,*b,&q| p [a,b,q.class] }.(1,2)
lambda { |a,b,*c,&q| p [a,b,c,q.class] }.(1,2,3)
lambda { |a,b,c,*d,&q| p [a,b,c,d,q.class] }.(1,2,3,4)
lambda { |a,b,c,d,*e,&q| p [a,b,c,d,q.class] }.(1,2,3,4)
lambda { |a,b,c,d,e,f,*g,&q| p [a,b,c,d,e,f,g,q.class] }.(1,2,3,4,5,6,7)
", @"
[Proc]
[1, Proc]
[1, 2, Proc]
[1, 2, 3, Proc]
[1, 2, 3, 4, Proc]
[1, 2, 3, 4, 5, 6, 7, Proc]
[NilClass]
[1, NilClass]
[1, 2, NilClass]
[1, 2, 3, NilClass]
[1, 2, 3, 4, NilClass]
[1, 2, 3, 4, 5, 6, 7, NilClass]
[1, [2], NilClass]
[1, 2, [3], NilClass]
[1, 2, 3, [4], NilClass]
[1, 2, 3, 4, NilClass]
[1, 2, 3, 4, 5, 6, [7], NilClass]
");
        }

        /// <summary>
        /// Break from a block called via Proc#call.
        /// </summary>
        public void EvalBreak2() {
            TestOutput(@"
def f(&b)
  b.call
end
p f { break 123 }
", @"
123
");
        }

        public void EvalReturn1() {
            TestOutputWithEval(@"
def y
  yield
end

def foo
  $b = Proc.new {  
    eval('return 123')
  }
  goo
end

def goo
  y(&$b)
end

p foo
", @"
123
"
            );
        }

        public void EvalReturn2() {
            TestOutputWithEval(@"
def foo
  $p.call
end

def owner
  $p = lambda do
    #<return 123#>
  end

  p foo

  puts 'owner.end'
end

owner
", @"
123
owner.end
"
            );
        }

        public void EvalReturn3() {
            TestOutputWithEval(@"
def foo
  $p.call
rescue 
  p $!
end

def owner
  $p = Proc.new do
    #<return 123#>
  end
end

owner
foo
", @"
#<LocalJumpError: unexpected return>
"
            );
        }

        public void EvalReturn4() {
            TestOutput(@"
def foo
  eval <<-END
    10.times do
      return 123
    end
  END
end

puts foo
", @"
123
");
        }

        /// <summary>
        /// A BEGIN block shares the top-level scope (MRI 1.9+): x is already a local there, nil
        /// until the assignment after BEGIN has run, and the block's binding sees it afterwards.
        /// </summary>
        public void BEGIN1() {
            TestOutput(@"
x = 1
BEGIN {
  p x rescue p $!
  y = 1
  z = 2
  1.times { p y + z }
  $binding = binding
}

p eval('x', $binding) rescue p $!
p eval('y+z', $binding)
", @"
nil
3
1
3
");
        }

        public void BEGIN2() {
            TestOutput(@"
puts '5'
BEGIN {
  puts '2'
  BEGIN {
    puts '1'
  }   
  puts '3'
}
puts '6'
BEGIN {
  puts '4'
}
puts '7'
", @"
1
2
3
4
5
6
7
");
        }

#if OBSOLETE
        [Options(Compatibility = RubyCompatibility.Ruby186)]
        public void BEGIN3() {
            TestOutput(@"
def f1
end

BEGIN {
  def f2
  end
}

class C
  private
  while true
    eval <<-END
      BEGIN {
        def f3
        end
      
        private
      
        break
        puts 'unreachable'
      } 
    END
  end
  
  def f4
  end
end

p self.private_methods(false).include?('f1')
p self.public_methods(false).include?('f2')

p C.public_instance_methods(false).include?('f3')
p C.private_instance_methods(false).include?('f4')
", @"
true
true
true
true
");
        }
#endif

        public void SymbolToProc1() {
            TestOutput(@"
class C
  def bar
    yield(self) rescue p $!
  end

  private
  def foo; 'foo'; end
  def to_s; 'C'; end
end

C.new.bar(&:foo)

n = :nil?.to_proc
p n.call(nil)
n.call() rescue p $!
p n.call([1,2])
", @"
#<NoMethodError: private method 'foo' called for an instance of C>
true
#<ArgumentError: no receiver given>
false
");
        }
    
        /// <summary>
        /// A call site that forwards a block argument (&amp;b) is first bound with b nil, which
        /// picks a library overload without a block. The rule has to test for that, or the same
        /// site keeps ignoring the block once a real one arrives - x.count(&amp;b) counted every
        /// element whatever the block said.
        /// </summary>
        public void BlockArgument_NilThenProc1() {
            TestOutput(@"
def c(x, &b)
  x.count(&b)
end
p c([1, 2, 3])
p(c([1, 2, 3]) { |v| v > 1 })
def e(x, &b)
  x.each(&b)
end
p e([1]).class
p(e([1]) { |v| }.class)
def i(x, &b)
  x.inject(&b)
end
p((i([1, 2]) rescue :no_block))
p(i([1, 2]) { |s, v| s + v })
p((1..10).count)
p((1..3).count { |v| v > 1 })
", @"
3
2
Enumerator
Array
:no_block
3
10
2
");
        }
    }
}

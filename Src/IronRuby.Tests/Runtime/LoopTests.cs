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
        public void RubyForLoop1() {
            AssertOutput(delegate() {
                CompilerTest(@"
for a in [1,2,3]
    x = 'ok'
    print a
end

print x         # x visible here, for-loop doesn't define a scope
");
            }, "123ok");
        }

        public void WhileLoop1() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 0
x = while i < 4 do 
  puts i
  i = i + 1
end
puts x
");
            }, @"
0
1
2
3

");
        }

        /// <summary>
        /// Break in a while loop.
        /// </summary>
        public void LoopBreak1() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 3
x = while i > 0 do 
  puts i
  if i == 2 then
    break
  end
  i = i - 1
end
puts x
");
            }, @"
3
2

");
        }

        /// <summary>
        /// Break with a value in a while loop.
        /// </summary>
        public void LoopBreak2() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 3
x = while i > 0 do 
  puts i
  if i == 2 then
    break 'foo'
  end
  i = i - 1
end
puts x
");
            }, @"
3
2
foo
");
        }

        /// <summary>
        /// Loop break from within class declaration.
        /// </summary>
        public void LoopBreak3() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 0
while i < 5 do
  puts i

  class C
    puts 'in C'
    break
  end
  
  i = i + 1
end
");
            }, @"
0
in C
");
        }

        /// <summary>
        /// Redo in a while loop.
        /// </summary>
        public void LoopRedo1() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 3
j = 2
x = while i > 0 do 
  puts i
  if i == 2 and j > 0 then
    j = j - 1
    redo
  end
  i = i - 1
end
puts x
");
            }, @"
3
2
2
2
1

");
        }

        /// <summary>
        /// Next in a while loop.
        /// </summary>
        public void LoopNext1() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 3
j = 2
x = while i > 0 do 
  puts i
  if i == 2 and j > 0 then
    j = j - 1
    next 'foo'
  end
  i = i - 1
end
puts x
");
            }, @"
3
2
2
2
1

");
        }

        public void Scenario_RubyUntilLoop1() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 1
until i > 3 do
  puts i
  i = i + 1
end
");
            }, @"
1
2
3");
        }

        public void Scenario_WhileLoopCondition1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def w x
  while x; p x; break; end
end

w []
w 0
w 1
w nil
w true
w false
");
            }, @"
[]
0
1
true
");
        }

        public void PostTestWhile1() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  puts 1
end while (puts 2)
");
            }, @"
1
2
");
        }

        public void PostTestUntil1() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  puts 1
end until (puts(2); true)
");
            }, @"
1
2
");
        }

        public void WhileModifier1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 1 while (puts 2)
");
            }, @"
2
");
        }

        public void UntilModifier1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 1 until (puts(2); true)
");
            }, @"
2
");
        }

        public void WhileModifier2() {
            AssertOutput(delegate() {
                CompilerTest(@"
$i = false
def c
  puts 'c'
  $i = !$i 
end

def x
  puts 'x'
end

(x) while c
");
            }, @"
c
x
c
");
        }

        public void UntilModifier2() {
            AssertOutput(delegate() {
                CompilerTest(@"
$i = true
def c
  puts 'c'
  $i = !$i 
end

def x
  puts 'x'
end

(x) until c
");
            }, @"
c
x
c
");
        }

        /// <summary>
        /// A global variable read or assigned in a specialized method or an outlined loop (JitCompiler.EmitGlobalRead,
        /// EmitGlobalWrite): the value it holds now, whatever it is, through an alias too; what an assignment stores
        /// is what a read sees, and trace_var still sees every assignment. A special variable, an undefined one, or
        /// one the loop both reads and assigns keeps the loop generic. The output is CRuby 4.0's.
        /// </summary>
        public void JitGlobals1() {
            TestOutput(@"
require 'ruby4'
$g = 3
def gm(n) = $g + n
def gl(m)
  i = 0; s = 0
  while i < m
    s = $g
    i += 1
  end
  s
end
p 100.times.map { |i| gm(i) }.sum
p gl(10_000)
$g = 7
p gm(1), gl(100)
$g = 2**70
p gm(1), gl(100)
$g = ""str""
p gl(100)
alias $h $g
$h = :via_alias
p gl(100)
p((gm(""x"") rescue $!.class))
def gs(m)
  i = 0; s = nil
  while i < m
    s = $stdout
    i += 1
  end
  s.equal?(STDOUT)
end
p gs(10_000)
def gu(m)
  i = 0; s = 1
  while i < m
    s = $undefined_here
    i += 1
  end
  s
end
$VERBOSE = nil
p gu(10_000)
$t = 0
th = Thread.new { 1000.times { |k| $t = k } }
th.join
p gl(10), $t
def gw(m)
  i = 0
  while i < m
    $w = i * 1_000_000
    i += 1
  end
  $w
end
p gw(10_000)
$cnt = 0
def grw(m)
  i = 0
  while i < m
    $cnt = $cnt + 1
    i += 1
  end
  $cnt
end
p grw(10_000), grw(10_000)
traced = []
trace_var(:$w) { |v| traced << v }
p gw(20_000), traced.size, traced.last
untrace_var(:$w)
p gw(5), traced.size
def mw(v) = ($w2 = v)
p 100.times.map { |i| mw(i) }.sum, $w2
", @"
5250
3
8
7
1180591620717411303425
1180591620717411303424
""str""
:via_alias
NoMethodError
true
nil
:via_alias
999
9999000000
10000
20000
19999000000
20000
19999000000
4000000
20000
4950
99
");
        }
    }
}

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
using IronRuby.Builtins;

namespace IronRuby.Tests {
    public partial class Tests {

        public void Scenario_RubyArrays1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts Array.[]('a','b','c')
puts Array[1,2,3]
puts ['e','f','g']
");
            }, String.Format("a{0}b{0}c{0}1{0}2{0}3{0}e{0}f{0}g{0}", Environment.NewLine));
        }

        public void Scenario_RubyArrays2() {
            AssertOutput(delegate() {
                CompilerTest(@"
a = ['e',['f'],'g']
a[2] = a[0]
print a[2]
print a[1][0]
");
            }, "ef");
        }

        public void Scenario_RubyArrays3() {
            TestOutput(@"
puts %w{}.size
puts %W{}.size
puts %w{x}
puts %W{x}
puts %w{          y          }
puts %W{          y          }
puts %w{hello world}
puts %W{hello world}
puts %w<cup<T> cup<co-phi>>
puts %W<cup<T> cup<co-phi>>
puts %w{hello w#{0}r#{1}d}
puts %W{hello w#{0}r#{1}d}
", @"
0
0
x
x
y
y
hello
world
hello
world
cup<T>
cup<co-phi>
cup<T>
cup<co-phi>
hello
w#{0}r#{1}d
hello
w0r1d
");
        }

        public void Scenario_RubyArrays4() {
            AssertOutput(delegate() {
                // an array literal copies what it splats (MRI does too)
                CompilerTest(@"
a = [*x = [1,2]]
puts a.object_id == x.object_id
puts a.inspect
");
            }, @"
false
[1, 2]");
        }

        public void Scenario_RubyArrays5() {
            AssertOutput(delegate() {
                CompilerTest(@"
a = [*4]
b = [*[4]]
c = [*[*4]]
d = [*[*[4]]]
puts a.inspect,b.inspect,c.inspect,d.inspect
");
            }, @"
[4]
[4]
[4]
[4]
");
        }

        public void Scenario_RubyArrays6() {
            AssertOutput(delegate() {
                CompilerTest(@"
a = [1,*[[],[*4], *[*[]]]]
puts a.inspect
");
            }, @"[1, [], [4]]");
        }

        /// <summary>
        /// Array#delete answers the last element that matched (MRI's rb_ary_delete), not the
        /// argument; its block gets the argument; and deleting many elements is one pass.
        /// </summary>
        public void Array_DeleteInPlace1() {
            TestOutput(@"
a = [1, 1.0, 2, 1.0]
x = a.delete(1)
p x, x.class, a
b = [1]
a = [[1], b, 2]
p a.delete([1]).equal?(b), a
a = [1, 2, 3]
p a.delete(5) { |x| [:none, x] }, a
p a.delete(2) { |x| :none }, a
p [1, 2].freeze.delete(3)
begin; [1, 2].freeze.delete(1); rescue FrozenError; p :frozen; end
a = [1, 2] * 100000
a.delete(1)
p a.size, a.uniq
a = (1..10).to_a
a.shift; a.shift
a.push(1, 3, 1)
p a.delete(3), a
", @"
1.0
Float
[2]
true
[2]
[:none, 5]
[1, 2, 3]
2
[1, 3]
nil
:frozen
100000
[2]
3
[4, 5, 6, 7, 8, 9, 10, 1, 1]
");
        }

        public void Array_CompactUniqInPlace1() {
            TestOutput(@"
a = [nil, 1, nil, 2, nil]
p a.compact!, a
p [1, 2].compact!
begin; [1].freeze.compact!; rescue FrozenError; p :frozen; end
a = [1, nil] * 100000
a.compact!
p a.size
a = [1, 2, 1, nil, 3, nil, 2, 1.0]
p a.uniq!, a
p [1, 2].uniq!
a = (1..100000).to_a * 2
a.uniq!
p a.size, a.last
", @"
[1, 2]
[1, 2]
nil
:frozen
100000
[1, 2, nil, 3, 1.0]
[1, 2, nil, 3, 1.0]
nil
100000
100000
");
        }

        /// <summary>
        /// A count so big that start + count overflowed Int32 used to keep its huge value, so
        /// [1, 2, 3][1, 2**31 - 1] was [] and slice! raised.
        /// </summary>
        public void Array_SliceHugeLength1() {
            TestOutput(@"
a = [1, 2, 3]
p a[1, 2**31 - 1], a[-1, 2**31 - 1], a[1, 2**31 - 2], a[3, 2**31 - 1], a[4, 2**31 - 1]
b = a.dup
p b.slice!(1, 2**31 - 1), b
b = a.dup
p b.slice!(2..0), b
c = (1..10).to_a
c.shift; c.shift; c.shift
p c[1, 3]
", @"
[2, 3]
[3]
[2, 3]
[]
nil
[2, 3]
[1]
[]
[1, 2, 3]
[5, 6, 7]
");
        }

        /// <summary>
        /// Blocks with a rest parameter and nothing before it - what Enumerable's C# methods
        /// and the Ruby-written ones pass to #each - get every yielded value in the rest array.
        /// </summary>
        public void Array_SplatBlockYield1() {
            TestOutput(@"
def y1; yield 1; end
def y1a; yield [1, 2]; end
def y2; yield 1, 2; end
def y4; yield 1, 2, 3, 4; end
def y5; yield 1, 2, 3, 4, 5; end
p y1 { |*a| a }, y1a { |*a| a }, y2 { |*a| a }, y4 { |*a| a }, y5 { |*a| a }
p y1(&lambda { |*a| a }), y1a(&lambda { |*a| a }), y2(&proc { |*a| a << 0 })
p (1..7).each_slice(3).to_a, (1..4).each_cons(2).to_a
o = Object.new
def o.each; yield 1, 2; yield 3; yield; end
o.extend(Enumerable)
p o.to_a, o.each_slice(2).to_a
", @"
[1]
[[1, 2]]
[1, 2]
[1, 2, 3, 4]
[1, 2, 3, 4, 5]
[1]
[[1, 2]]
[1, 2, 0]
[[1, 2, 3], [4, 5, 6], [7]]
[[1, 2], [2, 3], [3, 4]]
[[1, 2], 3, nil]
[[[1, 2], 3], [nil]]
");
        }

        public void IListOps_EnumerateRecursively1() {
            var a = new RubyArray();
            var b = new RubyArray();
            b.Add(new RubyArray { new RubyArray { b } });
            a.Add(b);

            var result = IListOps.EnumerateRecursively(new Runtime.ConversionStorage<System.Collections.IList>(Context), a, -1, list => {
                Assert(list == b);
                return 123;
            }).ToArray();

            Assert(result.Length == 1 && (int)result[0] == 123);
        }

        /// <summary>
        /// Array#sum of Integers and Floats (ArrayOps.NumericSum), Array#min/max of Fixnums while
        /// Integer#&lt;=&gt; is the built-in one, Array#shift(n) and Range#count of an Integer range: the
        /// same answers as the general paths, a redefined Integer#&lt;=&gt; is still called, and a frozen
        /// array raises FrozenError before the count is converted.
        /// </summary>
        public void Array_NumericSumMinShift1() {
            TestOutput(@"
require 'ruby4'
p [[1, 2, 3].sum, [].sum, [].sum(0.0), [2**62, 2**62, 2**62].sum, [2**64, 1, -(2**64)].sum]
p [([0.1] * 10).sum, [1, 0.1, 2, 0.2, 3**40].sum, [1, 2].sum(0.5), [1, 0.1, 0.2].sum(0.5), [1, 2].sum(2**70)]
p [[1.0, Float::INFINITY, 2].sum, [Float::INFINITY, -Float::INFINITY].sum.nan?, [1.0, Float::NAN].sum.nan?, [-Float::INFINITY, 1.0].sum]
p [[1, 2r, 0.5].sum, [""a"", ""b""].sum(""""), [1, 2].sum { |x| x * 2 }, [3, 1, 2].min, [3, 1, 2**70, 2].max, [3, 1.5, 2].min, [-3, -1, -2].max]
p(([3, nil].min rescue $!.class))
class Integer
  alias __test_cmp <=>
  def <=>(o) = -__test_cmp(o)
end
p [3, 1, 2].min, [3, 1, 2].max
class Integer
  alias <=> __test_cmp
  remove_method :__test_cmp
end
p [3, 1, 2].min
a = [1, 2, 3, 4]
p a.shift(2), a, a.shift(0), a.shift(5), a.shift(1), a.shift
p(([1].freeze.shift(nil) rescue $!.class), ([1].shift(-1) rescue $!.class), ([1].shift(nil) rescue $!.class))
p [(1..10).count, (1...10).count, (5..1).count, (1..).count, (""a""..""c"").count, (1..10).count(3), (1..3.5).count]
", @"
[6, 0, 0.0, 13835058055282163712, 1]
[1.0, 1.2157665459056929e+19, 3.5, 1.8, 1180591620717411303427]
[Infinity, true, true, -Infinity]
[3.5, ""ab"", 6, 1, 1180591620717411303424, 1.5, -1]
ArgumentError
3
1
1
[1, 2]
[]
[]
[3, 4]
[]
nil
FrozenError
ArgumentError
TypeError
[10, 9, 0, Infinity, 3, 1, 3]
");
        }
    }
}

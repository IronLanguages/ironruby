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

        /// <summary>
        /// Enumerable#tally and #group_by over an Array whose #each is the built-in one run in C#
        /// (Enumerable.ListTally, ListGroupBy); a subclass or singleton #each, or another Enumerable,
        /// still goes through #each. #tally stores the element itself as the key, as MRI's tally_up
        /// does (a String is not copied and frozen), #group_by stores a frozen copy, as MRI does, and
        /// Array#sort_by of Fixnum keys compares them directly.
        /// </summary>
        public void Enumerable_TallyGroupBy1() {
            TestOutput(@"
require 'ruby4'
a = [3, 1, 2, 3, nil, ""a"", ""a"", :s, 1.0, 1, [1], [1]]
p a.tally.to_a, [].tally.to_a
s = +""x""
t = [s, s.dup].tally
p [t.keys[0].equal?(s), t.keys[0].frozen?, t.values]
h = {}
[s].tally(h)
p [h.keys[0].equal?(s), ([1].tally({1 => ""a""}) rescue $!.class), ([1].tally({}.freeze) rescue $!.class)]
p a.group_by { |x| x.class.name }.to_a
g = [1, 2].group_by { s }
p [g.keys[0].frozen?, g.keys[0].equal?(s), [1, 2, 3].group_by { |x| x.odd? ? nil : x }.to_a]
p [1, 2, 3].group_by { |x| break x * 10 if x == 2; x }
b = [1, 2, 3]
p b.group_by { |x| b << 4 if x == 1 && b.size == 3; x }.keys
b = [1, 2, 3]
p b.group_by { |x| b.pop; x }.keys
c = Class.new(Array) { def each; yield 9; yield 8; end }
p c.new([1, 2]).group_by { |x| x }.keys, c.new([1]).tally.to_a
d = [1, 2]
def d.each; yield 7; end
p d.group_by { |x| x }.keys, d.tally.to_a
o = Object.new
o.extend(Enumerable)
def o.each; yield 1, 2; yield 3; end
p o.group_by { |x| x }.keys, o.tally.to_a
p [5, 3, 9, 1].sort_by { |x| -x }, (1..12).to_a.sort_by { |x| x % 3 }, [2**70, 1, -5].sort_by { |x| x }, [3, 1.5, 2].sort_by { |x| x }
", @"
[[3, 2], [1, 2], [2, 1], [nil, 1], [""a"", 2], [:s, 1], [1.0, 1], [[1], 2]]
[]
[true, false, [2]]
[true, TypeError, FrozenError]
[[""Integer"", [3, 1, 2, 3, 1]], [""NilClass"", [nil]], [""String"", [""a"", ""a""]], [""Symbol"", [:s]], [""Float"", [1.0]], [""Array"", [[1], [1]]]]
[true, false, [[nil, [1, 3]], [2, [2]]]]
20
[1, 2, 3, 4]
[1, 2]
[9, 8]
[[9, 1], [8, 1]]
[7]
[[7, 1]]
[[1, 2], 3]
[[[1, 2], 1], [3, 1]]
[9, 5, 3, 1]
[3, 6, 9, 12, 1, 4, 7, 10, 2, 5, 8, 11]
[-5, 1, 1180591620717411303424]
[1.5, 2, 3]
");
        }

        /// <summary>
        /// Enumerable#zip without a block reads a non-Array argument with #each, after the receiver, when neither
        /// the receiver nor any argument has an #each that runs anyone else's code (Hash, Struct, Set and Integer
        /// Range ones do not) - #89: Enumerator#next cost a fiber switch per element. The elements read, the order
        /// any side effects happen in, laziness on endless arguments and errors are MRI's zip_i ones; everything
        /// else still goes through #next. A receiver's own #each can change an argument between yields, and an
        /// argument's own #each must stay suspended (its ensure not run, its StopIteration ending it quietly) as
        /// under #next. The expected output is CRuby 4.0's.
        /// </summary>
        public void Enumerable_ZipEnumerated1() {
            TestOutput(@"
require 'ruby4'
$log = []
class Loud
  include Enumerable
  def initialize(name, n) = (@name, @n = name, n)
  def each
    @n.times { |i| $log << ""#{@name}#{i}""; yield i }
    $log << ""#{@name}-end""
  end
end
class Multi
  include Enumerable
  def each; yield; yield 1; yield 1, 2; end
end
def show(label)
  $log = []
  res = yield
  puts ""#{label}: #{res.inspect} #{$log.inspect}""
rescue => e
  puts ""#{label}: #{e.class}: #{e.message} #{$log.inspect}""
end
r = Set[1, 2, 3]
h = {a: 1, b: 2}
show(:quiet) { r.zip(h, Struct.new(:x, :y).new(7, 8), 'y'.., Set[5]) }
show(:loud_arg) { r.zip(Loud.new(:x, 5)) }
show(:loud_recv) { Loud.new(:r, 3).zip(h, 'a'..) }
show(:loud_both) { Loud.new(:r, 2).zip(Loud.new(:x, 5)) }
show(:loud_two) { r.zip(Loud.new(:x, 5), Loud.new(:y, 2)) }
show(:empty) { Set[].zip(Loud.new(:x, 5), 1.0..2.0) }
show(:float) { r.zip(1.0..2.0) }
show(:multi) { r.zip(Multi.new) }
show(:endless) { r.zip(Enumerator.new { |y| i = 0; loop { $log << i; y << i; i += 1 } }) }
show(:overridden) { hx = Class.new(Hash) { def each; $log << :hx; super; end }.new; hx[1] = 2; Loud.new(:r, 2).zip(hx) }
show(:block) { out = []; r.zip(h) { |row| out << row }; out }
show(:lazy) { (1..Float::INFINITY).lazy.map { _1 * 2 }.zip(h).first(2) }
class Mutating
  include Enumerable
  def initialize(h) = @h = h
  def each; yield 0; @h[:a] = 9; yield 1; end
end
show(:mutated) { Mutating.new($hh = {a: 1, b: 2}).zip($hh) }
raising = Object.new
def raising.each; yield 1; yield 2; ensure; raise 'cleanup'; end
show(:ensure_raises) { (1..1).zip(raising) }
stopping = Object.new
def stopping.each; yield 1; raise StopIteration; end
show(:stop_iteration) { (1..2).zip(stopping) }
suspended = Object.new
def suspended.each; yield 1; yield 2; ensure; $log << :ensure; end
show(:suspended) { (1..1).zip(suspended) }
", @"
quiet: [[1, [:a, 1], 7, ""y"", 5], [2, [:b, 2], 8, ""z"", nil], [3, nil, nil, ""aa"", nil]] []
loud_arg: [[1, 0], [2, 1], [3, 2]] [""x0"", ""x1"", ""x2""]
loud_recv: [[0, [:a, 1], ""a""], [1, [:b, 2], ""b""], [2, nil, ""c""]] [""r0"", ""r1"", ""r2"", ""r-end""]
loud_both: [[0, 0], [1, 1]] [""r0"", ""x0"", ""r1"", ""x1"", ""r-end""]
loud_two: [[1, 0, 0], [2, 1, 1], [3, 2, nil]] [""x0"", ""y0"", ""x1"", ""y1"", ""x2"", ""y-end""]
empty: [] []
float: TypeError: can't iterate from Float []
multi: [[1, nil], [2, 1], [3, [1, 2]]] []
endless: [[1, 0], [2, 1], [3, 2]] [0, 1, 2]
overridden: [[0, [1, 2]], [1, nil]] [""r0"", :hx, ""r1"", ""r-end""]
block: [[1, [:a, 1]], [2, [:b, 2]], [3, nil]] []
lazy: [[2, [:a, 1]], [4, [:b, 2]]] []
mutated: [[0, [:a, 1]], [1, [:b, 2]]] []
ensure_raises: [[1, 1]] []
stop_iteration: [[1, 1], [2, nil]] []
suspended: [[1, 1]] []
");
        }

        /// <summary>
        /// Array#cycle is MRI's rb_ary_cycle, which reads the array afresh on every pass: Enumerable#cycle, which
        /// Array used, cycles over what the first pass saw, so `a.cycle { a.clear }` never ended.
        /// </summary>
        public void Array_Cycle1() {
            TestOutput(@"
require 'ruby4'
a = [0, 1, 2]
p(a.cycle { a.clear }, a)
a = [1, 2]
r = []
a.cycle { |x| r << x; a << 3 if r.size == 1; break if r.size == 6 }
p r
r = []
[1, 2].cycle(2.7) { |x| r << x }
p r, [1].cycle(0) { }, [1].cycle(-1) { }
p [1].method(:cycle).owner, [1].cycle.inspect, [1].cycle(nil).inspect, [1, 2].cycle(2).to_a
p [1, 2].cycle.size, [].cycle.size, [1, 2].cycle(3).size
p(([1].cycle('x') { } rescue $!), ([1].cycle(1, 2) { } rescue $!))
o = Object.new
def o.to_int = '2'
p(([1].cycle(o) { } rescue $!))
", @"
nil
[]
[1, 2, 3, 1, 2, 3]
[1, 2, 1, 2]
nil
nil
Array
""#<Enumerator: [1]:cycle>""
""#<Enumerator: [1]:cycle(nil)>""
[1, 2, 1, 2]
Infinity
0
6
#<TypeError: no implicit conversion of String into Integer>
#<ArgumentError: wrong number of arguments (given 2, expected 0..1)>
#<TypeError: can't convert Object to Integer (Object#to_int gives String)>
");
        }
    }
}

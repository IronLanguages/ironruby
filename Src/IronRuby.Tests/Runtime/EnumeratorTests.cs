using System;

namespace IronRuby.Tests {
    public partial class Tests {
        // Enumerator#next over a built-in method walks it with a cursor (Enumerator.cs), which has to
        // be indistinguishable from MRI's fiber: the values, StopIteration and its result and cause,
        // #feed, a receiver changed half way, a hash that stays iterated, a method redefined before
        // the iteration starts, another thread. Util/enum-next-matrix.rb has the full matrix.
        public void Enumerator_ExternalCursor1() {
            TestOutput(@"
require 'ruby4'
def drain(e)
  r = []
  loop { r << e.next }
  r
end
e = [1, 2, 3].each
p [e.peek, e.next, e.next_values, e.peek_values, e.next]
begin
  e.next
rescue StopIteration => s
  p [s.message, s.result]
end
s2 = (e.next rescue $!)
p [s2.class, s2.cause.equal?(s), s2.result]
e.rewind
p e.next
p drain([1, 2].each_with_index), [1, 2].each_with_index.next_values
p drain([1, 2, 3].reverse_each), drain([5, 6].each_index)
p drain((1..3).each), drain((1...3).each), (1..).each.next, drain((2**31 - 1..2**31).each)
p drain(3.times), drain(1.upto(3)), drain(3.downto(1)), drain(0.times)
p drain(""h\u00e9"".each_char).map(&:ord), drain(""h\u00e9"".each_byte)
S = Struct.new(:a, :b)
p drain(S.new(1, 2).each)
h = {a: 1, b: 2}
p drain(h.each), drain(h.each_key), drain(h.each_value)
g = {a: 1}
p g.each_pair.next_values
# an iteration that never gets to its end leaves the hash iterated, as MRI's fiber does
p((g.store(:b, 2) rescue $!.class))
e = h.each
e.next
begin
  h[:c] = 3
rescue => x
  p x.message
end
h[:a] = 10
h.delete(:b)
p((e.next rescue $!.result))
h[:c] = 3
p h
a = [1, 2]
e = a.each
p e.next
a << 3
p e.next, e.next
a = [1, 2]
e = a.each
def a.each
  yield :own
  self
end
p drain(e)
e = [1, 2, 3].each
e.next
e.feed :x
p((e.feed(:y) rescue $!.class))
p e.next
e = [1, 2].each
e.next
p((e.dup rescue $!.message))
p Thread.new { e.next rescue $!.class }.value
p e.next
a = [1]
class << a
  private def rewind
    puts ""rewind""
  end
end
e = a.each
e.next
e.rewind
p e.next
", @"
[1, 1, [2], [3], 3]
[""iteration reached an end"", [1, 2, 3]]
[StopIteration, true, [1, 2, 3]]
1
[[1, 0], [2, 1]]
[1, 0]
[3, 2, 1]
[0, 1]
[1, 2, 3]
[1, 2]
1
[2147483647, 2147483648]
[0, 1, 2]
[1, 2, 3]
[3, 2, 1]
[]
[104, 233]
[104, 195, 169]
[1, 2]
[[:a, 1], [:b, 2]]
[:a, :b]
[1, 2]
[[:a, 1]]
RuntimeError
""can't add a new key into hash during iteration""
{a: 10}
{a: 10, c: 3}
1
2
3
[:own]
TypeError
2
""can't copy execution context""
FiberError
2
rewind
1
");
        }

        // What stays on the fiber: a generator, and #feed into it, and a method that has been
        // redefined - until it is the built-in one again.
        public void Enumerator_ExternalFiber1() {
            TestOutput(@"
require 'ruby4'
e = Enumerator.new { |y| y << 1; r = y.yield(2, 3); y.yield r; :done }
p e.next, e.peek, e.next_values, e.peek
e.rewind
p e.next, e.next
e.feed :fed
p e.next
p((e.next rescue $!.result))
p((e.next rescue [$!.class, $!.cause.class]))
e.rewind
p e.next
class Array
  alias_method :__orig_each__, :each
  def each(&b)
    return to_enum(:each) unless b
    __orig_each__ { |x| b.call(x * 10) }
  end
end
p [1, 2].each.next, [1, 2].each_with_index.next
class Array
  remove_method :each
  alias_method :each, :__orig_each__
end
p [1, 2].each.next
", @"
1
[2, 3]
[2, 3]
nil
1
[2, 3]
:fed
:done
[StopIteration, StopIteration]
1
10
[10, 0]
1
");
        }
    }
}

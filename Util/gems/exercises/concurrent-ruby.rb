require 'concurrent'

# Map with a default proc: the lookup runs the block inside the backend's own
# lock, which is what the Monitor patch in ironruby/gem_compat.rb exists for.
m = Concurrent::Map.new { |h, k| h[k] = k * 3 }
puts m[7]
m[:a] = 1
puts m.keys.map(&:to_s).sort.inspect

n = Concurrent::AtomicFixnum.new(0)
ts = 4.times.map { Thread.new { 250.times { n.increment } } }
ts.each(&:join)
puts n.value

r = Concurrent::AtomicReference.new(:old)
puts r.compare_and_set(:old, :new)
puts r.compare_and_set(:old, :other)
puts r.get.inspect

q = Queue.new
Concurrent::Array.new([3, 1, 2]).sort.each { |x| q << x }
puts q.size

f = Concurrent::Promises.future { 6 * 7 }
puts f.value!

puts Concurrent::Hash.new.merge({ 'b' => 2, 'a' => 1 }).sort.inspect

d = Concurrent::Delay.new { 'computed once' }
puts d.value
puts d.value

puts Concurrent::CountDownLatch.new(1).tap(&:count_down).wait(1)

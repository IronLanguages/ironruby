# Stress test for instance variables shared between threads (IronRuby has no GIL).
#
#   ./ir.sh Util/ivar-stress.rb [threads] [iterations]
#
# Several threads hammer a few shared objects - a plain Object subclass, a String and an
# Array (whose instance variables live in a side table), a class (class-level ivars) - through
# both `@x` sites (compiled, inline-cached) and instance_variable_get/set/remove.
#
# Thread t owns:
#   @o<t>        written with increasing values tagged t; never removed
#   @c<t>_<j>    "churn": added, read back and removed again, so the objects keep changing
#                shape, growing past their capacity and moving (see RubyInstanceData)
# and every thread reads everybody's variables and checks:
#   * a value read for @o<u> is tagged u (a wrong slot would give another variable's value)
#     and never goes backwards for the same reader (no stale read after a newer one)
#   * the owner reads back exactly what it just wrote (a write lost to a concurrent move of
#     the slot array would read back the previous value)
#   * a churn variable, when defined, holds its own tag
#   * instance_variables has no duplicates, and every name it lists reads back a value
#     tagged for that name (or is gone by then - a concurrent remove)
# At the end every @o<t> must hold the last value its owner wrote: a write lost to a
# concurrent move would show here.
#
# Prints "OK" and exits 0, or the first few failures and exits 1.

THREADS = (ARGV[0] || 8).to_i
ITERS = (ARGV[1] || 100_000).to_i
TAG = 64

class Shared
end

# One reader and one writer method per thread, with literal ivar names, so that the
# compiled `@x` / `@x = v` fast paths are what gets exercised.
THREADS.times do |t|
  Shared.class_eval <<-RUBY
    def w#{t}(v); @o#{t} = v; end
    def r#{t}; @o#{t}; end
    def d#{t}; defined?(@o#{t}); end
  RUBY
end

module ClassLevel
end

objects = [Shared.new, Shared.new, +"shared string", [1, 2, 3], ClassLevel]
failures = Queue.new
record = lambda do |msg|
  failures << msg if failures.size < 20
end

def tag_of(v) = v % TAG
def seq_of(v) = v / TAG

readers = objects.map do |obj|
  if obj.is_a?(Shared)
    [->(t) { obj.__send__(:"r#{t}") }, ->(t, v) { obj.__send__(:"w#{t}", v) }]
  else
    [->(t) { obj.instance_variable_get(:"@o#{t}") }, ->(t, v) { obj.instance_variable_set(:"@o#{t}", v) }]
  end
end

last_written = Array.new(THREADS) { Array.new(objects.size, nil) }

threads = THREADS.times.map do |t|
  Thread.new do
    rng = Random.new(t + 1)
    seen = Array.new(objects.size) { Array.new(THREADS, -1) }
    seq = 0
    ITERS.times do |i|
      k = rng.rand(objects.size)
      obj = objects[k]
      read, write = readers[k]
      case rng.rand(10)
      when 0..2
        seq += 1
        v = seq * TAG + t
        write.(t, v)
        last_written[t][k] = v
        # nobody else writes @o<t>: reading anything else back means the write was lost
        got = read.(t)
        record.("#{obj.class} @o#{t} read back #{got.inspect}, wrote #{v}") unless got == v
      when 3..5
        u = rng.rand(THREADS)
        v = read.(u)
        next if v.nil?
        unless v.is_a?(Integer) && tag_of(v) == u
          record.("#{obj.class} @o#{u} read #{v.inspect} (tag #{v.is_a?(Integer) ? tag_of(v) : '-'})")
          next
        end
        if seq_of(v) < seen[k][u]
          record.("#{obj.class} @o#{u} went backwards: #{seq_of(v)} after #{seen[k][u]}")
        end
        seen[k][u] = seq_of(v)
      when 6, 7
        j = rng.rand(12)
        name = :"@c#{t}_#{j}"
        v = (i * TAG + t) * 100 + j
        obj.instance_variable_set(name, v)
        got = obj.instance_variable_get(name)
        record.("#{obj.class} #{name} read back #{got.inspect}, wrote #{v}") unless got == v
        if rng.rand(2) == 0
          removed = obj.remove_instance_variable(name)
          record.("#{obj.class} #{name} removed #{removed.inspect}, wrote #{v}") unless removed == v
          record.("#{obj.class} #{name} still defined after remove") if obj.instance_variable_defined?(name)
        end
      when 8
        names = obj.instance_variables
        record.("#{obj.class} duplicate names #{names.inspect}") if names.uniq.size != names.size
        names.each do |name|
          v = obj.instance_variable_get(name)
          next if v.nil?
          case name.to_s
          when /\A@o(\d+)\z/
            record.("#{obj.class} #{name} = #{v.inspect}") unless v.is_a?(Integer) && tag_of(v) == $1.to_i
          when /\A@c(\d+)_(\d+)\z/
            record.("#{obj.class} #{name} = #{v.inspect}") unless v.is_a?(Integer) && v % 100 == $2.to_i && tag_of(v / 100) == $1.to_i
          end
        end
      else
        u = rng.rand(THREADS)
        name = :"@c#{u}_#{rng.rand(12)}"
        v = obj.instance_variable_get(name)
        if v && !(v.is_a?(Integer) && tag_of(v / 100) == u)
          record.("#{obj.class} #{name} = #{v.inspect}")
        end
      end
    end
  end
end
threads.each(&:join)

objects.each_with_index do |obj, k|
  THREADS.times do |t|
    expected = last_written[t][k]
    next unless expected
    got = obj.instance_variable_get(:"@o#{t}")
    record.("#{obj.class} @o#{t} lost a write: #{got.inspect}, last written #{expected}") unless got == expected
  end
end

# Phase 2: the narrow races, concentrated. Most threads write-and-read-back their own
# variable on one object as fast as they can, through the cached fast path, while the others
# keep moving that object's slot array (add a variable past the capacity, remove it again).
hot = Shared.new
THREADS.times { |t| hot.__send__(:"w#{t}", t) }
churners = [THREADS / 4, 1].max
running = true
movers = churners.times.map do |c|
  Thread.new do
    n = 0
    while running
      name = :"@m#{c}_#{n % 40}"
      hot.instance_variable_set(name, n)
      hot.remove_instance_variable(name) if n.odd?
      n += 1
    end
  end
end
writers = (churners...THREADS).map do |t|
  Thread.new do
    w = :"w#{t}"
    r = :"r#{t}"
    (ITERS * 2).times do |i|
      v = i * TAG + t
      hot.__send__(w, v)
      got = hot.__send__(r)
      record.("hot @o#{t} read back #{got.inspect}, wrote #{v}") unless got == v
    end
  end
end
writers.each(&:join)
running = false
movers.each(&:join)

if failures.empty?
  puts "OK (#{THREADS} threads x #{ITERS} iterations, #{objects.size} shared objects)"
else
  puts "FAILED:"
  puts failures.pop until failures.empty?
  exit 1
end

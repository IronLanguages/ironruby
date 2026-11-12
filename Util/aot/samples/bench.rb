# Steady-state comparison: the same file under ./ir.sh and as an ahead-of-time compiled DLL.
# Each workload runs a few rounds; the best round is reported, so start-up and warm-up (the
# interpreter -> IL tier in ir, tiered JIT in both) are left out.
def fib(n)
  n < 2 ? n : fib(n - 1) + fib(n - 2)
end

class Vec
  attr_reader :x, :y
  def initialize(x, y); @x = x; @y = y; end
  def +(o) = Vec.new(@x + o.x, @y + o.y)
  def len2 = @x * @x + @y * @y
end

def vectors(n)
  acc = Vec.new(0, 0)
  n.times { |i| acc = acc + Vec.new(i % 7, i % 3) }
  acc.len2
end

def strings(n)
  words = %w[alpha beta gamma delta epsilon]
  h = Hash.new(0)
  n.times { |i| h["#{words[i % 5]}-#{i % 11}"] += 1 }
  h.size
end

def best(label, rounds = 5)
  times = []
  result = nil
  rounds.times do
    t = Process.clock_gettime(Process::CLOCK_MONOTONIC)
    result = yield
    times << Process.clock_gettime(Process::CLOCK_MONOTONIC) - t
  end
  puts format("%-10s %8.1f ms  (%s)", label, times.min * 1000, result)
end

best("fib(25)") { fib(25) }
best("vectors") { vectors(300_000) }
best("strings") { strings(200_000) }

# Run in Release. Keep GC populations in separate processes, e.g.:
# IR_CONFIG=Release ./ir.sh Util/bench/fiber.rb gc 1000

def elapsed
  start = Process.clock_gettime(Process::CLOCK_MONOTONIC)
  yield
  Process.clock_gettime(Process::CLOCK_MONOTONIC) - start
end

mode = ARGV.shift || 'switch'
case mode
when 'switch'
  n = Integer(ARGV.shift || 100_000)
  raise ArgumentError, 'iterations must be positive' unless n > 0
  f = Fiber.new { (n + 2000).times { Fiber.yield 1 } }
  begin
    2000.times { f.resume }
    seconds = elapsed { n.times { f.resume } }
    puts "switch iterations=#{n} total_s=#{seconds} us_per_roundtrip=#{seconds * 1e6 / n}"
  ensure
    f.resume while f.alive?
  end
  100.times { Fiber.new {}.resume }
  seconds = elapsed { n.times { Fiber.new {}.resume } }
  puts "create_run iterations=#{n} total_s=#{seconds} us_per_fiber=#{seconds * 1e6 / n}"
when 'gc'
  n = Integer(ARGV.shift || 1000)
  samples = Integer(ARGV.shift || 20)
  raise ArgumentError, 'population must be nonnegative and samples positive' unless n >= 0 && samples > 0
  fibers = []
  begin
    n.times do
      f = Fiber.new { Fiber.yield; nil }
      f.resume
      fibers << f
    end
    3.times { GC.start }
    pauses = Array.new(samples) { elapsed { GC.start } * 1000 }.sort
    middle = samples / 2
    median = samples.even? ? (pauses[middle - 1] + pauses[middle]) / 2.0 : pauses[middle]
    puts "gc parked=#{n} samples=#{samples} min_ms=#{pauses.first} median_ms=#{median} max_ms=#{pauses.last}"
  ensure
    fibers.each { |f| f.resume if f.alive? }
  end
else
  raise ArgumentError, 'mode must be switch or gc'
end

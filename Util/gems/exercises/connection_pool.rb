require 'connection_pool'

made = Queue.new
pool = ConnectionPool.new(size: 3, timeout: 1) { made << 1; Object.new }

ids = Queue.new
ts = 6.times.map { Thread.new { pool.with { |c| ids << c.object_id } } }
ts.each(&:join)
puts ids.size
puts made.size <= 3
puts pool.size
puts pool.available <= 3

v = pool.with { |c| c.frozen? }
puts v

empty = ConnectionPool.new(size: 1, timeout: 0) { Object.new }
held = Thread.new { empty.with { sleep 0.5 } }
sleep 0.1
begin
  empty.with { |_| }
rescue ConnectionPool::TimeoutError => e
  puts e.class
end
held.join

wrapper = ConnectionPool::Wrapper.new(size: 1, timeout: 1) { +'shared' }
puts wrapper.upcase
puts wrapper.length

pool.shutdown { |_c| }
begin
  pool.with { |_| }
rescue ConnectionPool::PoolShuttingDownError => e
  puts e.class
end

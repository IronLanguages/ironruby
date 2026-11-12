require 'parallel'

# in_threads exercises real CLR threads; in_processes would need fork, which
# the CLR does not have, so that is deliberately not asked for here.
puts Parallel.map(1..8, in_threads: 4) { |i| i * i }.inspect
puts Parallel.map(%w[a b c], in_threads: 2) { |s| s.upcase }.inspect
puts Parallel.each(1..4, in_threads: 2) { |i| i }.inspect

acc = Queue.new
Parallel.each(1..20, in_threads: 5) { |i| acc << i }
puts acc.size
got = []
got << acc.pop until acc.empty?
puts got.sort.inspect

puts Parallel.map_with_index(%w[x y], in_threads: 2) { |s, i| "#{i}#{s}" }.inspect
puts Parallel.map(1..4, in_threads: 0) { |i| i }.inspect

begin
  Parallel.map(1..4, in_threads: 2) { |i| raise 'inner' if i == 3; i }
rescue RuntimeError => e
  puts "#{e.class}: #{e.message}"
end

puts Parallel.map(1..4, in_threads: 2) { |i| raise Parallel::Break if i == 3; i }.inspect
puts Parallel.processor_count > 0

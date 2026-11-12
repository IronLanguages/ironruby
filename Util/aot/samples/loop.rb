# The bare interpreter: a counting loop and a few calls, timed.
puts 1 + 2
t = Time.now
i = 0
while i < 1_000_000
  i += 1
end
puts "while loop: #{((Time.now - t) * 1000).round} ms"

def add(a, b) = a + b
t = Time.now
s = 0
200_000.times { |k| s = add(s, k) }
puts "calls: #{((Time.now - t) * 1000).round} ms (#{s})"

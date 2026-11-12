# Focused Hash microbenchmarks.  Same shape as Util/bench/bench.rb: one benchmark per
# process, the elapsed seconds of the work alone printed on the last line, so
# interpreter start-up never lands in the number.
#
#   ruby Util/bench/hash-bench.rb --list
#   ruby Util/bench/hash-bench.rb insert_sym [scale]
#   Util/bench/hash-run.sh [repetitions] [outfile] [benchmark...]

BENCHES = {}

def bench(name, &b)
  BENCHES[name.to_s] = b
end

# --- inserts ---------------------------------------------------------------

bench :insert_sym do |n|
  keys = (0...1000).map { |i| :"k#{i}" }
  (200 * n).times do
    h = {}
    keys.each { |k| h[k] = 1 }
  end
end

bench :insert_str do |n|
  keys = (0...1000).map { |i| "k#{i}" }
  (100 * n).times do
    h = {}
    keys.each { |k| h[k] = 1 }
  end
end

bench :insert_int do |n|
  (400 * n).times do
    h = {}
    i = 0
    while i < 1000
      h[i] = i
      i += 1
    end
  end
end

# overwriting an existing key: must keep the original position and key object
bench :update_existing do |n|
  h = {}
  1000.times { |i| h[i] = i }
  (2000 * n).times do
    i = 0
    while i < 1000
      h[i] = i
      i += 1
    end
  end
end

# --- lookups ---------------------------------------------------------------

bench :lookup_sym do |n|
  keys = (0...1000).map { |i| :"k#{i}" }
  h = {}
  keys.each { |k| h[k] = 1 }
  (2000 * n).times { keys.each { |k| h[k] } }
end

bench :lookup_str do |n|
  keys = (0...1000).map { |i| "k#{i}" }
  h = {}
  keys.each { |k| h[k] = 1 }
  (500 * n).times { keys.each { |k| h[k] } }
end

bench :lookup_miss do |n|
  h = {}
  1000.times { |i| h[i] = i }
  (3000 * n).times do
    i = 1000
    while i < 2000
      h[i]
      i += 1
    end
  end
end

bench :small_lookup do |n|
  h = { :a => 1, :b => 2, :c => 3, :d => 4 }
  (100_000 * n).times { h[:a]; h[:d]; h[:c] }
end

# --- delete-heavy ----------------------------------------------------------

# the pathological shape: alternate delete and insert forever on a hash that
# stays the same size.  Tombstones + a bad compaction threshold go quadratic here.
bench :delete_insert_churn do |n|
  h = {}
  1000.times { |i| h[i] = i }
  i = 0
  lim = 300_000 * n
  while i < lim
    h.delete(i % 1000)
    h[i % 1000] = i
    i += 1
  end
end

bench :delete_all do |n|
  (300 * n).times do
    h = {}
    1000.times { |i| h[i] = i }
    1000.times { |i| h.delete(i) }
  end
end

bench :delete_half_then_iterate do |n|
  (200 * n).times do
    h = {}
    1000.times { |i| h[i] = i }
    500.times { |i| h.delete(i * 2) }
    s = 0
    h.each_pair { |k, v| s += v }
  end
end

bench :shift_all do |n|
  (200 * n).times do
    h = {}
    1000.times { |i| h[i] = i }
    h.shift while h.size > 0
  end
end

# --- iteration -------------------------------------------------------------

bench :iterate_each do |n|
  h = {}
  1000.times { |i| h[i] = i }
  s = 0
  (1000 * n).times { h.each { |k, v| s += v } }
end

bench :iterate_keys do |n|
  h = {}
  1000.times { |i| h[i] = i }
  (2000 * n).times { h.keys }
end

bench :iterate_to_a do |n|
  h = {}
  1000.times { |i| h[i] = i }
  (500 * n).times { h.to_a }
end

# --- whole-hash operations -------------------------------------------------

bench :dup_hash do |n|
  h = {}
  1000.times { |i| h[i] = i }
  (2000 * n).times { h.dup }
end

bench :merge_hash do |n|
  a = {}
  1000.times { |i| a[i] = i }
  b = {}
  1000.times { |i| b[i + 500] = i }
  (1000 * n).times { a.merge(b) }
end

bench :rehash_hash do |n|
  h = {}
  1000.times { |i| h[i] = i }
  (1000 * n).times { h.rehash }
end

# --- large hashes ----------------------------------------------------------

bench :large_insert do |n|
  (5 * n).times do
    h = {}
    i = 0
    while i < 500_000
      h[i] = i
      i += 1
    end
  end
end

bench :large_lookup do |n|
  h = {}
  i = 0
  while i < 500_000
    h[i] = i
    i += 1
  end
  (20 * n).times do
    j = 0
    while j < 500_000
      h[j]
      j += 1
    end
  end
end

bench :large_delete do |n|
  (5 * n).times do
    h = {}
    i = 0
    while i < 500_000
      h[i] = i
      i += 1
    end
    i = 0
    while i < 500_000
      h.delete(i)
      i += 2
    end
  end
end

# --- keyword arguments / literals ------------------------------------------

def kw_target(a: 1, b: 2, c: 3)
  a
end

bench :kwargs_call do |n|
  (300_000 * n).times { kw_target(:a => 1, :b => 2, :c => 3) }
end

bench :hash_literal do |n|
  (300_000 * n).times { { :a => 1, :b => 2, :c => 3, :d => 4 } }
end

if ARGV[0] == "--list"
  puts BENCHES.keys
  exit
end

name = ARGV[0]
scale = (ARGV[1] || 1).to_i
b = BENCHES[name] or abort("no such benchmark: #{name}")
t = Time.now
b.call(scale)
puts format("%.4f", Time.now - t)

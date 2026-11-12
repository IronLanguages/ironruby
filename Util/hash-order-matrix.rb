# Expression matrix for Hash insertion order.
#
# Ruby has guaranteed since 1.9 that a Hash enumerates in insertion order, and the rules
# for what an operation does to that order are exact: assigning to a key that is already
# there leaves it where it is (and keeps the original key object), while deleting a key
# and putting it back moves it to the end.  This file drives a large spread of
# insert / delete / re-insert / update / shift / replace / merge / rehash /
# compare_by_identity sequences and prints the resulting key order.
#
# Run the same file through CRuby and through IronRuby and diff the output:
#
#   ruby   Util/hash-order-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/hash-order-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
#
# Every line is "<source> => <result>" or "<source> !! <ExceptionClass>: <message>",
# so a diff points straight at the sequence that orders differently.

EXPRESSIONS = <<~'RUBY'.lines.map(&:strip).reject { |l| l.empty? || l.start_with?("#") }
  # --- the basic guarantee
  (h = {}; %w[a b c d].each { |k| h[k] = 1 }; h.keys)
  (h = {}; [3, 1, 2, 0].each { |k| h[k] = 1 }; h.keys)
  (h = {}; 20.times { |i| h[i] = i }; h.keys)
  (h = {}; 20.downto(1) { |i| h[i] = i }; h.keys)
  ({"method" => 1, "data" => 2}.tap { |h| h.delete("method"); h["rel"] = 3 }.keys)

  # --- delete then re-insert moves to the end
  (h = {a: 1, b: 2, c: 3}; h.delete(:a); h[:a] = 9; h.keys)
  (h = {a: 1, b: 2, c: 3}; h.delete(:b); h[:b] = 9; h.keys)
  (h = {a: 1, b: 2, c: 3}; h.delete(:c); h[:c] = 9; h.keys)
  (h = {a: 1, b: 2, c: 3}; h.delete(:b); h[:z] = 9; h.keys)
  (h = {a: 1, b: 2, c: 3}; h.delete(:a); h.delete(:c); h[:a] = 1; h[:c] = 1; h.keys)
  (h = {}; 10.times { |i| h[i] = i }; 10.times { |i| h.delete(i) if i.even? }; h.keys)
  (h = {}; 10.times { |i| h[i] = i }; 10.times { |i| h.delete(i) if i.even? }; 10.times { |i| h[i] = i }; h.keys)
  (h = {}; 30.times { |i| h[i] = i }; h.delete_if { |k, _| k % 3 == 0 }; h.keys)
  (h = {}; 30.times { |i| h[i] = i }; h.delete_if { |k, _| k % 3 == 0 }; 30.times { |i| h[i] = i }; h.keys)
  (h = {}; 50.times { |i| h[i] = i }; 50.times { |i| h.delete(i) }; 5.times { |i| h[i] = i }; h.keys)
  (h = {}; 100.times { |i| h[i] = i }; 99.times { |i| h.delete(i) }; h.keys)
  (h = {}; 100.times { |i| h[i] = i }; 100.times { |i| h.delete(i); h[i + 100] = i }; h.keys)
  (h = {}; 200.times { |i| h[i] = i }; 200.times { |i| h.delete(i) if i % 4 != 0 }; h.keys)
  (h = {}; 200.times { |i| h[i] = i }; 200.times { |i| h.delete(i) if i % 4 != 0 }; 200.times { |i| h[i] = i }; h.keys.first(20))
  (h = {}; 500.times { |i| h[i] = i }; 500.times { |i| h.delete(i) if i % 7 != 0 }; h.keys)
  (h = {}; 1000.times { |i| h[i] = i }; 1000.times { |i| h.delete(i) if i % 3 != 0 }; h.keys.last(10))
  (h = {}; 1000.times { |i| h[i] = i }; 900.times { |i| h.delete(i) }; 50.times { |i| h[i] = i }; h.keys.first(15))

  # --- churn: delete and re-add the same keys over and over (compaction)
  (h = {}; 64.times { |i| h[i] = i }; 2000.times { |i| k = i % 64; h.delete(k); h[k] = i }; h.keys)
  (h = {}; 8.times { |i| h[i] = i }; 500.times { |i| h.delete(i % 8); h[100 + i] = i; h.delete(100 + i) if i % 2 == 0; h[i % 8] = i }; h.size)
  (h = {}; 3000.times { |i| h[i] = i; h.delete(i - 10) if i >= 10 }; h.keys)

  # --- assigning to an existing key does NOT move it
  (h = {a: 1, b: 2, c: 3}; h[:a] = 99; h.keys)
  (h = {a: 1, b: 2, c: 3}; h[:b] = 99; h.values)
  (h = {a: 1, b: 2, c: 3}; h.store(:a, 99); h.keys)
  (h = {}; 10.times { |i| h[i] = i }; 10.times { |i| h[i] = i * 2 }; [h.keys, h.values])
  (h = {a: 1, b: 2}; h.merge!(a: 9); h.keys)
  (h = {a: 1, b: 2}; h.update(b: 9, c: 3); h.keys)
  (h = {a: 1, b: 2}; h.merge({a: 9}) { |k, o, n| o + n }.keys)

  # --- and it keeps the ORIGINAL key object
  (k1 = "k".dup; k2 = "k".dup; h = {}; h[k1] = 1; h[k2] = 2; h.keys.first.equal?(k1))
  (k1 = "k".dup; k2 = "k".dup; h = {}; h[k1] = 1; h[k2] = 2; h.keys.first.equal?(k2))
  (k1 = "k".dup; k2 = "k".dup; h = {}; h[k1] = 1; h.delete(k1); h[k2] = 2; h.keys.first.equal?(k2))
  (k1 = [1]; k2 = [1]; h = {}; h[k1] = 1; h[k2] = 2; h.keys.first.equal?(k1))
  (k = "s".dup; h = {}; h[k] = 1; h.keys.first.frozen?)
  (k = "s".dup; h = {}; h[k] = 1; h.keys.first.equal?(k))

  # --- shift takes the first-inserted
  (h = {a: 1, b: 2, c: 3}; [h.shift, h.keys])
  (h = {a: 1, b: 2, c: 3}; h.delete(:a); [h.shift, h.keys])
  (h = {a: 1, b: 2, c: 3}; h.delete(:a); h[:a] = 9; [h.shift, h.keys])
  (h = {}; 10.times { |i| h[i] = i }; r = []; 10.times { r << h.shift }; [r, h.keys, h.size])
  (h = {}; 100.times { |i| h[i] = i }; r = []; 100.times { r << h.shift.first }; [r.first(5), r.last(5), h.size])
  (h = {}; 50.times { |i| h[i] = i }; 25.times { h.shift }; 5.times { |i| h[1000 + i] = i }; h.keys)
  (h = {}; 3.times { |i| h[i] = i }; 3.times { h.shift }; h.shift)
  (h = {a: 1}; h.shift; h.shift)
  (h = Hash.new(0); h.shift)
  (h = Hash.new { |x, k| "d#{k}" }; h.shift)

  # --- first / min_by / sort_by / each read the same order
  ({a: 1, b: 2, c: 3}.first)
  ({a: 1, b: 2, c: 3}.first(2))
  (h = {a: 1, b: 2, c: 3}; h.delete(:a); h[:a] = 0; h.first)
  (h = {b: 1, a: 1, c: 1}; h.min_by { |k, v| v })
  (h = {b: 1, a: 1, c: 1}; h.max_by { |k, v| v })
  (h = {b: 1, a: 1, c: 1}; h.sort_by { |k, v| v }.map(&:first))
  (h = {b: 2, a: 1, c: 3}; h.each_with_object([]) { |(k, _), a| a << k })
  (h = {b: 2, a: 1, c: 3}; r = []; h.each { |k, v| r << k }; r)
  (h = {b: 2, a: 1, c: 3}; r = []; h.each_pair { |k, v| r << k }; r)
  (h = {b: 2, a: 1, c: 3}; r = []; h.each_key { |k| r << k }; r)
  (h = {b: 2, a: 1, c: 3}; r = []; h.each_value { |v| r << v }; r)
  (h = {b: 2, a: 1, c: 3}; h.each_entry.to_a)
  (h = {b: 2, a: 1, c: 3}; h.to_a)
  (h = {b: 2, a: 1, c: 3}; h.flat_map { |k, v| [k, v] })
  (h = {b: 2, a: 1, c: 3}; h.inject([]) { |a, (k, v)| a << k })
  (h = {b: 2, a: 1, c: 3}; h.detect { |k, v| true })
  (h = {b: 2, a: 1, c: 3}; h.take(2))
  (h = {b: 2, a: 1, c: 3}; h.each_slice(2).to_a)
  (h = {b: 2, a: 1, c: 3}; h.zip([1, 2, 3]).map { |(p, _)| p.first })
  (h = {b: 2, a: 1, c: 3}; h.each_with_index.to_a)
  (h = {b: 2, a: 1, c: 3}; h.lazy.map { |k, v| k }.first(3))
  (h = {}; 20.times { |i| h[i] = i }; 20.times { |i| h.delete(i) if i % 3 == 1 }; h.map { |k, _| k })
  (h = {}; 20.times { |i| h[i] = i }; 20.times { |i| h.delete(i) if i % 3 == 1 }; h.inspect)
  (h = {}; 20.times { |i| h[i] = i }; 20.times { |i| h.delete(i) if i % 3 == 1 }; h.to_s)

  # --- derived hashes keep the order of the receiver
  (h = {c: 3, a: 1, b: 2}; h.select { |k, v| v > 1 }.keys)
  (h = {c: 3, a: 1, b: 2}; h.reject { |k, v| v > 1 }.keys)
  (h = {c: 3, a: 1, b: 2}; h.filter_map { |k, v| k }.to_a)
  (h = {c: 3, a: 1, b: 2}; h.invert.keys)
  (h = {c: 3, a: 1, b: 2}; h.transform_values { |v| v * 2 }.keys)
  (h = {c: 3, a: 1, b: 2}; h.transform_keys(&:to_s).keys)
  (h = {c: 3, a: 1, b: 2}; h.to_h { |k, v| [v, k] }.keys)
  (h = {c: 3, a: 1, b: 2}; h.compact.keys)
  (h = {c: 3, a: nil, b: 2}; h.compact.keys)
  (h = {c: 3, a: 1, b: 2}; h.slice(:b, :c).keys)
  (h = {c: 3, a: 1, b: 2}; h.except(:c).keys)
  (h = {c: 3, a: 1, b: 2}; h.group_by { |k, v| v.odd? }.values.map { |v| v.map(&:first) })
  (h = {c: 3, a: 1, b: 2}; h.partition { |k, v| v.odd? }.map { |p| p.map(&:first) })
  (h = {c: 3, a: 1, b: 2}; h.dup.keys)
  (h = {c: 3, a: 1, b: 2}; h.clone.keys)
  (h = {c: 3, a: 1, b: 2}; h.delete(:c); h.dup.keys)
  (h = {c: 3, a: 1, b: 2}; h.delete(:c); h[:c] = 3; h.clone.keys)
  (h = {c: 3, a: 1, b: 2}; h.to_a.to_h.keys)
  (h = {c: 3, a: 1, b: 2}; Hash[h.to_a].keys)
  (h = {c: 3, a: 1, b: 2}; Hash[h].keys)
  (h = {c: 3, a: 1, b: 2}; h.each_with_object({}) { |(k, v), o| o[k] = v }.keys)

  # --- replace / merge / update
  (h = {a: 1, b: 2}; h.replace(x: 9, y: 8); h.keys)
  (h = {a: 1, b: 2}; h.replace(b: 9, a: 8); h.keys)
  (h = {a: 1, b: 2}; h.delete(:a); h.replace(z: 1, a: 2); h.keys)
  (h = {}; 30.times { |i| h[i] = i }; 15.times { |i| h.delete(i * 2) }; h.replace(q: 1); h.keys)
  ({a: 1}.merge({b: 2}, {c: 3}, {a: 4}).keys)
  ({a: 1, b: 2}.merge!({c: 3}) { |k, o, n| o }.keys)
  (h = {b: 1}; h.merge!({a: 2}); h.merge!({b: 3}); h.keys)
  (h = {}; 10.times { |i| h[i] = i }; other = {}; 10.times { |i| other[19 - i] = i }; h.merge(other).keys)
  (h = {}; 10.times { |i| h[i] = i }; 10.times { |i| h.delete(i) if i.odd? }; h.merge(b: 1).keys)
  (a = {x: 1}; b = {y: 2}; a.update(b) { |k, o, n| o }.keys)

  # --- rehash, including after a key mutates
  ({a: 1, b: 2, c: 3}.rehash.keys)
  (h = {a: 1, b: 2, c: 3}; h.delete(:b); h.rehash.keys)
  (h = {a: 1, b: 2, c: 3}; h.delete(:b); h[:b] = 9; h.rehash.keys)
  (k = [1]; h = {k => "v", :z => 1}; k << 2; h.rehash; [h.keys, h[[1, 2]]])
  (k = "s".dup; h = {}; h[k] = 1; h[:z] = 2; h.rehash.keys)
  (h = {}; 40.times { |i| h[[i]] = i }; 40.times { |i| h.delete([i]) if i.even? }; h.rehash.keys.flatten)
  (h = {}; 40.times { |i| h[[i]] = i }; 40.times { |i| h.delete([i]) if i.even? }; h.rehash; h[[39]])
  (h = {}; 200.times { |i| h[i.to_s] = i }; 200.times { |i| h.delete(i.to_s) if i % 5 != 0 }; h.rehash.keys)
  (h = {a: 1}; h.rehash.equal?(h))

  # --- compare_by_identity
  (h = {}.compare_by_identity; a = "x".dup; b = "x".dup; h[a] = 1; h[b] = 2; h.size)
  (h = {}.compare_by_identity; a = "x".dup; b = "x".dup; h[a] = 1; h[b] = 2; h.values)
  (h = {}.compare_by_identity; 10.times { |i| h[i] = i }; h.keys)
  (h = {}.compare_by_identity; 10.times { |i| h[i] = i }; 10.times { |i| h.delete(i) if i.even? }; h.keys)
  (h = {c: 1, a: 2, b: 3}; h.compare_by_identity.keys)
  (h = {c: 1, a: 2, b: 3}; h.delete(:a); h.compare_by_identity.keys)
  (h = {c: 1, a: 2, b: 3}; h.compare_by_identity; h[:d] = 4; h.keys)
  (h = {}.compare_by_identity; h.compare_by_identity?)
  (h = {}; h.compare_by_identity?)
  (h = {}.compare_by_identity; h[:a] = 1; h.dup.compare_by_identity?)
  (h = {}.compare_by_identity; h[:a] = 1; h.dup.keys)
  (h = {a: 1}; g = {}.compare_by_identity; g[:b] = 1; h.replace(g); [h.compare_by_identity?, h.keys])
  (h = {}.compare_by_identity; h[:a] = 1; h.replace({b: 2}); [h.compare_by_identity?, h.keys])
  (h = {}.compare_by_identity; s = "k".dup; h[s] = 1; h.rehash.keys.first.equal?(s))

  # --- default value / default proc survive order-changing operations
  (h = Hash.new(7); h[:a] = 1; h.delete(:a); h[:a] = 2; [h.keys, h[:zz]])
  (h = Hash.new { |x, k| x[k] = k.to_s }; h[:b]; h[:a]; h.keys)
  (h = Hash.new { |x, k| x[k] = k.to_s }; h[:b]; h.delete(:b); h[:b]; h.keys)
  (h = Hash.new(0); 5.times { |i| h[i] += 1 }; h.keys)
  (h = Hash.new(0); h.default)
  (h = Hash.new(0); h[:a] = 1; h.dup.default)
  (h = Hash.new { 1 }; h.dup.default_proc.class)
  (h = Hash.new(5); g = {a: 1}; g.replace(h); g.default)

  # --- frozen hashes
  ({a: 1}.freeze.keys)
  (h = {a: 1, b: 2}.freeze; (h.delete(:a) rescue "#{$!.class}"))
  (h = {a: 1, b: 2}.freeze; begin; h[:c] = 1; rescue Exception; "#{$!.class}"; end)
  (h = {a: 1, b: 2}.freeze; (h.shift rescue "#{$!.class}"))
  (h = {a: 1, b: 2}.freeze; (h.rehash rescue "#{$!.class}"))
  (h = {a: 1, b: 2}.freeze; (h.replace(c: 1) rescue "#{$!.class}"))
  (h = {a: 1, b: 2}.freeze; (h.compare_by_identity rescue "#{$!.class}"))
  (h = {a: 1, b: 2}.freeze; h.dup.tap { |d| d[:c] = 3 }.keys)
  (h = {a: 1, b: 2}.freeze; h.frozen?)
  (h = {a: 1, b: 2}.freeze; h.dup.frozen?)
  (h = {a: 1, b: 2}.freeze; h.clone.frozen?)
  (h = {a: 1, b: 2}.freeze; h.clone(freeze: false).frozen?)

  # --- Marshal round-trips in order
  (Marshal.load(Marshal.dump({c: 1, a: 2, b: 3})).keys)
  (h = {c: 1, a: 2, b: 3}; h.delete(:a); h[:a] = 9; Marshal.load(Marshal.dump(h)).keys)
  (h = {}; 30.times { |i| h[i] = i }; 30.times { |i| h.delete(i) if i.odd? }; Marshal.load(Marshal.dump(h)).keys)
  (h = Hash.new(4); h[:a] = 1; Marshal.load(Marshal.dump(h)).default)
  (h = {}.compare_by_identity; h[:a] = 1; Marshal.load(Marshal.dump(h)).compare_by_identity?)

  # --- keyword arguments are Hashes too
  (def kw(**o); o.keys; end; kw(b: 1, a: 2, c: 3))
  (def kw2(**o); o.keys; end; h = {z: 1, y: 2}; kw2(**h))
  (def kw3(a:, b:); [a, b]; end; kw3(b: 2, a: 1))
  (def kw4(**o); o; end; kw4(b: 1, a: 2).to_a)
  (def kw5(*a, **o); [a, o.keys]; end; kw5(1, 2, z: 3, y: 4))
  (def kw6(**o); o.delete(:a); o[:a] = 9; o.keys; end; kw6(a: 1, b: 2))

  # --- Struct is backed by the same machinery
  (Struct.new(:c, :a, :b).new(1, 2, 3).to_h.keys)
  (Struct.new(:c, :a, :b).new(1, 2, 3).each.to_a)
  (Struct.new(:a, :b).new(1, 2).to_h { |k, v| [v, k] }.keys)
  (s = Struct.new(:a, :b).new(1, 2); s.to_h.tap { |h| h.delete(:a); h[:a] = 9 }.keys)

  # --- ENV is a Hash-alike
  (ENV.to_h.class)
  (ENV.to_h.keys == ENV.keys)
  (ENV.to_hash.keys == ENV.keys)

  # --- nested and self-referential
  (h = {a: {b: {c: 1}}}; h[:a][:b].delete(:c); h[:a][:b][:c] = 2; h.inspect)
  (h = {}; h[:self] = h; h[:other] = 1; h.keys)
  (h = {a: 1, b: 2}; h.delete(:a); h.each { |k, v| }; h.keys)

  # --- mutation during iteration must not silently reorder the snapshot
  (h = {a: 1, b: 2, c: 3}; r = []; h.each_key { |k| r << k }; r)
  (h = {a: 1, b: 2, c: 3}; h.each { |k, v| h[k] = v * 2 }; [h.keys, h.values])
  (h = {a: 1, b: 2, c: 3}; h.keys.each { |k| h.delete(k) if k == :b }; h.keys)

  # --- big randomised sequences (deterministic seed)
  (r = Random.new(1234); h = {}; 2000.times { k = r.rand(200); r.rand(2) == 0 ? h.delete(k) : h[k] = 1 }; [h.size, h.keys])
  (r = Random.new(99); h = {}; 5000.times { k = r.rand(500); case r.rand(3) when 0 then h.delete(k) when 1 then h[k] = 1 else h[k] = 2 end }; [h.size, h.keys.first(30), h.keys.last(30)])
  (r = Random.new(7); h = {}; 3000.times { |i| h[r.rand(1000)] = i; h.shift if i % 3 == 0 }; [h.size, h.keys.first(20)])
  (r = Random.new(42); h = {}; 4000.times { |i| k = "k#{r.rand(300)}"; r.rand(4) == 0 ? h.delete(k) : h[k] = i }; [h.size, h.keys.first(20), h.keys.last(20)])
  (r = Random.new(5); h = {}; 1000.times { |i| h[[r.rand(50), r.rand(50)]] = i }; 500.times { h.shift }; h.rehash; [h.size, h.keys.first(10)])
RUBY

EXPRESSIONS.each do |source|
  begin
    value = eval(source)
    puts "#{source} => #{value.inspect}"
  rescue Exception => e
    puts "#{source} !! #{e.class}: #{e.message}"
  end
end

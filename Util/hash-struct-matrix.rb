# Expression matrix for the Hash / Struct / Comparable surface.
#
# Run the same file through CRuby and through IronRuby and diff the output:
#
#   ruby   hash-struct-matrix.rb > /tmp/cruby.txt
#   ./ir.sh hash-struct-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
#
# Every line is "<source> => <result>" or "<source> !! <ExceptionClass>: <message>",
# so a diff points straight at the expression that behaves differently.

EXPRESSIONS = <<~'RUBY'.lines.map(&:strip).reject { |l| l.empty? || l.start_with?("#") }
  # --- Hash: lookup and comparison protocol
  {a: 1}.eql?({a: 1})
  {1 => "x"}.eql?({1.0 => "x"})
  {a: 1}.hash == {a: 1}.hash
  {0 => 2, 11 => 1}.hash == {11 => 1, 0 => 2}.hash
  {a: 2, b: 2}.hash == {a: 7, b: 7}.hash
  ({:a => :a}.hash == {:b => :b}.hash)
  (h = {}; h[:x] = h; h.hash == {x: h}.hash)
  (h = {}; h[:a] = h; h.inspect)
  {b: 1}.include?("b")
  {b: 1}["b"]
  :b.eql?("b")
  "b".eql?(:b)

  # --- Hash: set comparison
  {a: 1} < {a: 1, b: 2}
  {a: 1} <= {a: 1}
  {a: 1, b: 2} > {a: 1}
  {a: 1} >= {a: 2}
  {a: 1} > 1
  {a: 1} > nil

  # --- Hash: combinators
  {a: 1}.to_proc.call(:a)
  {a: 1}.to_proc.lambda?
  {a: 1}.to_proc.arity
  {a: 1, b: nil}.compact
  {a: 1, b: nil}.compact!
  {a: 1}.compact!
  {a: 1, b: 2}.select! { |k, v| v > 1 }
  {a: 1, b: 2}.select! { true }
  {a: 1}.merge
  {a: 1}.merge({b: 2}, {c: 3})
  {a: 1}.merge!({b: 2}, {c: 3})
  {a: 1}.transform_keys({a: :A})
  {a: 1, b: 2}.transform_keys({a: :A}) { |k| k.to_s }
  {a: 1, b: 2, c: 3, d: 4}.transform_keys!(&:succ)
  {a: 1, b: 2, c: 3, d: 4}.transform_keys! { |_| :a }
  {a: 1}.transform_values! { |v| v + 1 }
  {a: 1, b: 2}.slice(:a)
  {a: 1, b: 2}.except(:a)
  Hash.new(1).except(:a).default
  Hash.new(1).reject { false }.default
  Hash.new(1).compact.default
  {}.shift
  Hash.new(5).shift
  {a: 1}.to_h.equal?({a: 1}.to_h)
  (h = {a: 1}; h.to_h.equal?(h))
  {a: 1}.assoc(:a)
  {1 => :v}.assoc(1.0)
  {a: 1}.rassoc(1)
  {a: 1, b: 2}.dig(:a)
  {a: 1}.dig(:a, :b)

  # --- Hash: defaults, KeyError, conversions
  (h = {}; h.default_proc = ->(hh, k) { 42 }; h[:x])
  (h = Hash.new(7); h.default_proc = ->(hh, k) { 1 }; h.default)
  (h = Hash.new(1); h.send(:initialize); h.default)
  ({}.fetch(:x) rescue "#{$!.class}: #{$!.message}")
  (begin; {}.fetch(:x); rescue KeyError => e; [e.key, e.receiver]; end)
  (begin; {}.fetch_values(:x); rescue KeyError => e; [e.key, e.receiver]; end)
  Hash.try_convert(1)
  (Hash[[:a]] rescue "#{$!.class}: #{$!.message}")
  (Hash[[[:a, :b, :c]]] rescue "#{$!.class}: #{$!.message}")

  # --- Hash: compare_by_identity
  {}.compare_by_identity?
  {}.compare_by_identity.compare_by_identity?
  (h = {}.compare_by_identity; h[[1]] = :c; h[[1]] = :d; h.values)
  (h = {}.compare_by_identity; s = +"foo"; h[s] = 1; h[s] = 2; [h.size, h.keys.first.equal?(s)])
  (h = {}.compare_by_identity; h[:a] = 1; h[:a] = 2; h.values)
  ({1 => 2} == {}.compare_by_identity.tap { |h| h[1] = 2 })
  ({} == {}.compare_by_identity)
  {}.compare_by_identity.dup.compare_by_identity?
  {}.compare_by_identity.select { true }.compare_by_identity?
  {}.compare_by_identity.reject { false }.compare_by_identity?
  {}.compare_by_identity.except.compare_by_identity?
  {}.compare_by_identity.transform_values { |v| v }.compare_by_identity?
  {}.compare_by_identity.transform_keys { |k| k }.compare_by_identity?
  Hash.ruby2_keywords_hash?({})
  Hash.ruby2_keywords_hash?(Hash.ruby2_keywords_hash({}))

  # --- Struct
  (s = Struct.new(:a, :b).new(1, 2); s.to_h)
  (s = Struct.new(:a, :b).new(1, 2); s.deconstruct)
  (s = Struct.new(:a, :b).new(1, 2); s.deconstruct_keys(nil))
  (s = Struct.new(:a, :b).new(1, 2); s.deconstruct_keys([:a]))
  (s = Struct.new(:a, :b).new(1, 2); s.deconstruct_keys([0]))
  (s = Struct.new(:a, :b).new(1, 2); s.deconstruct_keys([:a, :b, :c]))
  (s = Struct.new(:a, :b).new(1, 2); s.deconstruct_keys(1) rescue "#{$!.class}: #{$!.message}")
  (s = Struct.new(:a, :b).new(1, 2); s.dig(:a))
  (s = Struct.new(:a, :b).new(1, 2); s.dig(:a, :b) rescue "#{$!.class}")
  (s = Struct.new(:a, :b).new(1, 2); s.values_at(0..3))
  (s = Struct.new(:a, :b).new(1, 2); s.values_at(1..))
  (s = Struct.new(:a, :b).new(1, 2); s.values_at(..1))
  (s = Struct.new(:a, :b).new(1, 2); s.values_at(5) rescue "#{$!.class}: #{$!.message}")
  (s = Struct.new(:a, :b).new(1, 2); s.each.to_a)
  (s = Struct.new(:a, :b).new(1, 2); a = []; s.each_pair { |p| a << p }; a)
  Struct.new(:a).new("").to_s
  Struct.new(:a, keyword_init: true).keyword_init?
  Struct.new(:a, keyword_init: false).keyword_init?
  Struct.new(:a).keyword_init?
  Struct.new(:a, keyword_init: 1).keyword_init?
  Struct.new(:a, :b, keyword_init: true).new(a: 1, b: 2).to_a
  (Struct.new(:a, keyword_init: true).new(z: 1) rescue "#{$!.class}: #{$!.message}")
  (Struct.new(:foo, :foo) rescue "#{$!.class}: #{$!.message}")
  Struct.new.is_a?(Class)
  (Struct.new(:"method_1?").new(1).send(:"method_1?"))
  (Struct.new(:"a-b").new(1)["a-b"])
  (c = Struct.new(:a, :members); c.new(1, 2).members)

  # --- Comparable
  1.clamp(2, 3)
  5.clamp(1..3)
  5.clamp(..3)
  1.clamp(2..)
  (1.clamp(3, 2) rescue "#{$!.class}")
  (1.clamp(1...3) rescue "#{$!.class}")
  1.clamp(nil, nil)
  (Class.new { include Comparable; def <=>(o); 0.0; end }.new == 1)
  (Class.new { include Comparable; def <=>(o); nil; end }.new == 1)
  (Class.new { include Comparable; def <=>(o); raise TypeError, "x"; end }.new == 1 rescue "#{$!.class}")
  (Class.new { include Comparable; def <=>(o); "abc"; end }.new == 1 rescue "#{$!.class}")

  # --- method identity (aliases)
  Hash.instance_method(:size) == Hash.instance_method(:length)
  Hash.instance_method(:store) == Hash.instance_method(:[]=)
  Hash.instance_method(:key?) == Hash.instance_method(:include?)
  Hash.instance_method(:filter) == Hash.instance_method(:select)
  Struct.instance_method(:values) == Struct.instance_method(:to_a)
  Struct.instance_method(:inspect) == Struct.instance_method(:to_s)
RUBY

EXPRESSIONS.each do |source|
  begin
    value = eval(source)
    puts "#{source} => #{value.inspect}"
  rescue Exception => e
    puts "#{source} !! #{e.class}: #{e.message}"
  end
end

# Differential matrix for Array: receivers x arguments x blocks, one line each.
#   ruby Util/array2-matrix.rb            # CRuby reference
#   ./ir.sh Util/array2-matrix.rb         # IronRuby
# Diff the two outputs; every differing line is a bug.

def show(label)
  v = begin
        r = yield
        r.inspect
      rescue Exception => e
        "!#{e.class}: #{e.message}"
      end
  puts "#{label.ljust(64)} #{v}"
end

# Range#inspect of an endless/beginless range differs between the two (a
# separate bug), so build argument labels by hand rather than with #inspect.
def lbl(x)
  if x.is_a?(Range)
    "(#{x.begin ? x.begin.inspect : ''}#{x.exclude_end? ? '...' : '..'}#{x.end ? x.end.inspect : ''})"
  else
    x.inspect
  end
end

class ToAry
  def initialize(v) = @v = v
  def to_ary = @v
  def inspect = "ToAry(#{@v.inspect})"
end

class ToA
  def initialize(v) = @v = v
  def to_a = @v
  def inspect = "ToA(#{@v.inspect})"
end

class ToInt
  def initialize(v) = @v = v
  def to_int = @v
  def inspect = "ToInt(#{@v.inspect})"
end

class MMAry
  def respond_to_missing?(name, priv = false) = name == :to_ary
  def method_missing(name, *a)
    return [7, 8] if name == :to_ary
    super
  end
  def inspect = "MMAry"
end

class NoAry
  def respond_to?(name, priv = false) = name == :to_ary
  def inspect = "NoAry"
end

class RaiseCmp
  include Comparable
  def <=>(other) = raise "cmp boom"
  def inspect = "RaiseCmp"
end

RECEIVERS = {
  "[]"          => -> { [] },
  "[1,2,3]"     => -> { [1, 2, 3] },
  "[1,2,3,4,5]" => -> { [1, 2, 3, 4, 5] },
  "[nil,1,nil]" => -> { [nil, 1, nil] },
  "[[1,2],[3]]" => -> { [[1, 2], [3]] },
}

# ---------------------------------------------------------------- index reads
INDEX_ARGS = [
  [0], [1], [2], [4], [-1], [-2], [-6], [5],
  [0, 0], [0, 1], [0, 3], [0, 9], [1, 2], [3, 0], [3, 1], [-1, 1], [-1, 9],
  [-6, 1], [5, 1], [6, 1], [0, -1], [2, -1],
  [0..0], [0..1], [0..-1], [1..-2], [-2..-1], [2..1], [0...0], [0...-1],
  [1..], [..1], [..-1], [3..], [5..], [6..], [-9..], [0..99], [-9..-8],
  [ToInt.new(1)], [ToInt.new(-1)], [ToInt.new(1), ToInt.new(2)],
  [1.7], [-1.7], [nil], ["x"], [1, nil],
]

RECEIVERS.each do |rn, rf|
  INDEX_ARGS.each do |args|
    a = args.map { |x| lbl(x) }.join(", ")
    show("#{rn}[#{a}]") { rf.call[*args] }
    show("#{rn}.slice(#{a})") { rf.call.slice(*args) }
    show("#{rn}.slice!(#{a})") { r = rf.call; x = r.slice!(*args); [x, r] }
    show("#{rn}.values_at(#{a})") { rf.call.values_at(*args) }
    show("#{rn}.fetch(#{a})") { rf.call.fetch(*args) } if args.size == 1
    show("#{rn}.dig(#{a})") { rf.call.dig(*args) } if args.size == 1
  end
end

# ------------------------------------------------------------------ index write
[[0, 9], [2, 9], [5, 9], [-1, 9], [-6, 9], [ToInt.new(1), 9]].each do |i, v|
  show("[1,2,3][#{i.inspect}] = #{v}") { r = [1, 2, 3]; r[i] = v; r }
end
[[0, 0, 9], [0, 2, 9], [1, 9, 9], [-1, 1, 9], [5, 2, 9], [0, -1, 9]].each do |i, l, v|
  show("[1,2,3][#{i},#{l}] = #{v}") { r = [1, 2, 3]; r[i, l] = v; r }
end
[[0..0, 9], [0..1, 9], [0..-1, 9], [2..1, 9], [1.., 9], [..1, 9], [5..6, 9], [-9..0, 9]].each do |i, v|
  show("[1,2,3][#{lbl(i)}] = #{v}") { r = [1, 2, 3]; r[i] = v; r }
end
show("[1,2,3][0,2] = [9,9]") { r = [1, 2, 3]; r[0, 2] = [9, 9]; r }
show("[1,2,3][0,2] = nil") { r = [1, 2, 3]; r[0, 2] = nil; r }
show("frozen[0] = 9 arg coerce order") { r = [1, 2, 3].freeze; r[ToInt.new(0)] = 9 }
show("frozen[bad] = 9") { r = [1, 2, 3].freeze; r["x"] = 9 }

# -------------------------------------------------------------------- mutators
FROZEN_OPS = {
  "pop"        => -> (a) { a.pop },
  "pop(1)"     => -> (a) { a.pop(1) },
  "shift"      => -> (a) { a.shift },
  "shift(1)"   => -> (a) { a.shift(1) },
  "clear"      => -> (a) { a.clear },
  "push"       => -> (a) { a.push },
  "concat"     => -> (a) { a.concat },
  "map!"       => -> (a) { a.map! { |x| x } },
  "collect!"   => -> (a) { a.collect! { |x| x } },
  "select!"    => -> (a) { a.select! { |x| true } },
  "reject!"    => -> (a) { a.reject! { |x| false } },
  "delete_if"  => -> (a) { a.delete_if { |x| false } },
  "keep_if"    => -> (a) { a.keep_if { |x| true } },
  "uniq!"      => -> (a) { a.uniq! },
  "flatten!"   => -> (a) { a.flatten! },
  "compact!"   => -> (a) { a.compact! },
  "sort!"      => -> (a) { a.sort! },
  "reverse!"   => -> (a) { a.reverse! },
  "rotate!"    => -> (a) { a.rotate! },
  "shuffle!"   => -> (a) { a.shuffle! },
  "fill(0)"    => -> (a) { a.fill(0) },
  "insert(0,1)" => -> (a) { a.insert(0, 1) },
  "delete(1)"  => -> (a) { a.delete(1) },
  "delete_at(0)" => -> (a) { a.delete_at(0) },
  "replace([])" => -> (a) { a.replace([]) },
  "slice!(0)"  => -> (a) { a.slice!(0) },
  "sort_by!"   => -> (a) { a.sort_by! { |x| x } },
  "shift(0)"   => -> (a) { a.shift(0) },
  "pop(0)"     => -> (a) { a.pop(0) },
  "fill"       => -> (a) { a.fill { |i| i } },
}
FROZEN_OPS.each do |name, op|
  show("[].freeze.#{name}") { op.call([].freeze) }
  show("[1,2].freeze.#{name}") { op.call([1, 2].freeze) }
end

# ---------------------------------------------------------- to_ary / to_a users
CONV = [ToAry.new([1, 2]), ToAry.new(nil), ToAry.new(5), ToA.new([1, 2]), MMAry.new, NoAry.new, 5, nil, "s"]
CONV.each do |o|
  show("Array.try_convert(#{o.inspect})") { Array.try_convert(o) }
  show("Array(#{o.inspect})") { Array(o) }
  show("[1].concat(#{o.inspect})") { [1].concat(o) }
  show("[1] + #{o.inspect}") { [1] + o }
  show("[1] - #{o.inspect}") { [1] - o }
  show("[1] & #{o.inspect}") { [1] & o }
  show("[1] | #{o.inspect}") { [1] | o }
  show("[1].product(#{o.inspect})") { [1].product(o) }
  show("[1].replace(#{o.inspect})") { [1].replace(o) }
  show("[1].zip(#{o.inspect})") { [1].zip(o) }
  show("[[1,#{o.inspect}]].assoc(1)") { [[1, o]].assoc(1) }
  show("[#{o.inspect}].assoc(1)") { [o].assoc(1) }
  show("[#{o.inspect}].rassoc(2)") { [o].rassoc(2) }
  show("[#{o.inspect}].flatten") { [o].flatten }
  show("[#{o.inspect}].join") { [o].join }
  show("[#{o.inspect}].to_h") { [o].to_h }
  show("[1].union(#{o.inspect})") { [1].union(o) }
  show("[1].difference(#{o.inspect})") { [1].difference(o) }
  show("[1].intersection(#{o.inspect})") { [1].intersection(o) }
  show("[1].intersect?(#{o.inspect})") { [1].intersect?(o) }
end

# --------------------------------------------------------------- int coercions
INTS = [ToInt.new(2), ToInt.new(-1), ToInt.new(nil), 2, -1, nil, "2", 2.7, 2**70]
INTS.each do |n|
  show("[1,2,3].first(#{n.inspect})") { [1, 2, 3].first(n) }
  show("[1,2,3].last(#{n.inspect})") { [1, 2, 3].last(n) }
  show("[1,2,3].take(#{n.inspect})") { [1, 2, 3].take(n) }
  show("[1,2,3].drop(#{n.inspect})") { [1, 2, 3].drop(n) }
  show("[1,2,3].pop(#{n.inspect})") { r = [1, 2, 3]; [r.pop(n), r] }
  show("[1,2,3].shift(#{n.inspect})") { r = [1, 2, 3]; [r.shift(n), r] }
  show("[1,2,3].rotate(#{n.inspect})") { [1, 2, 3].rotate(n) }
  show("[1,2,3] * #{n.inspect}") { [1, 2, 3] * n }
  show("[1,2,3].sample(#{n.inspect})") { x = [1, 2, 3].sample(n); x.is_a?(Array) ? x.sort : x.class }
  show("[1,2,3].combination(#{n.inspect})") { [1, 2, 3].combination(n).to_a }
  show("[1,2,3].permutation(#{n.inspect})") { [1, 2, 3].permutation(n).to_a }
  show("[1,2,3].each_slice(#{n.inspect})") { [1, 2, 3].each_slice(n).to_a }
  show("[1,2,3].each_cons(#{n.inspect})") { [1, 2, 3].each_cons(n).to_a }
  show("Array.new(#{n.inspect})") { Array.new(n) }
  show("[[1,[2]]].flatten(#{n.inspect})") { [[1, [2]]].flatten(n) }
end

# ------------------------------------------------------------------------ fill
show("[1,2,3].fill(9)") { [1, 2, 3].fill(9) }
show("[1,2,3].fill(9,1)") { [1, 2, 3].fill(9, 1) }
show("[1,2,3].fill(9,1,1)") { [1, 2, 3].fill(9, 1, 1) }
show("[1,2,3].fill(9,-1)") { [1, 2, 3].fill(9, -1) }
show("[1,2,3].fill(9,-9)") { [1, 2, 3].fill(9, -9) }
show("[1,2,3].fill(9,-9,2)") { [1, 2, 3].fill(9, -9, 2) }
show("[1,2,3].fill(9,5)") { [1, 2, 3].fill(9, 5) }
show("[1,2,3].fill(9,1,-1)") { [1, 2, 3].fill(9, 1, -1) }
show("[1,2,3].fill(9,0..1)") { [1, 2, 3].fill(9, 0..1) }
show("[1,2,3].fill(9,-9..1)") { [1, 2, 3].fill(9, -9..1) }
show("[1,2,3].fill(9,1..)") { [1, 2, 3].fill(9, 1..) }
show("[1,2,3].fill(9,..1)") { [1, 2, 3].fill(9, ..1) }
show("[1,2,3].fill(9,2..1)") { [1, 2, 3].fill(9, 2..1) }
show("[1,2,3].fill{|i| i}") { [1, 2, 3].fill { |i| i } }
show("[1,2,3].fill(1){|i| i}") { [1, 2, 3].fill(1) { |i| i } }
show("[1,2,3].fill(1,1){|i| i}") { [1, 2, 3].fill(1, 1) { |i| i } }
show("[1,2,3].fill(0..1){|i| i}") { [1, 2, 3].fill(0..1) { |i| i } }
show("[1,2,3].fill(1,2,3){|i| i}") { [1, 2, 3].fill(1, 2, 3) { |i| i } }
show("[1,2,3].fill()") { [1, 2, 3].fill }
show("[1,2,3].fill(1,2,3,4)") { [1, 2, 3].fill(1, 2, 3, 4) }

# ------------------------------------------------------------- blocks that jump
show("map with break") { [1, 2, 3].map { |x| break :b if x == 2; x } }
show("each with break") { [1, 2, 3].each { |x| break :b if x == 2 } }
show("select with break") { [1, 2, 3].select { |x| break :b if x == 2; true } }
show("reject! with break") { r = [1, 2, 3]; [r.reject! { |x| break :b if x == 2; false }, r] }
show("select! with break") { r = [1, 2, 3]; [r.select! { |x| break :b if x == 2; true }, r] }
show("delete_if with break") { r = [1, 2, 3]; [r.delete_if { |x| break :b if x == 2; true }, r] }
show("keep_if with break") { r = [1, 2, 3]; [r.keep_if { |x| break :b if x == 2; false }, r] }
show("map! with break") { r = [1, 2, 3]; [r.map! { |x| break :b if x == 2; 0 }, r] }
show("uniq with break") { [1, 2, 3].uniq { |x| break :b if x == 2; x } }
show("sort_by! with break") { r = [1, 2, 3]; [r.sort_by! { |x| break :b if x == 2; x }, r] }
show("each_index with break") { [1, 2, 3].each_index { |x| break :b if x == 2 } }
show("map with next") { [1, 2, 3].map { |x| next :n if x == 2; x } }
show("delete_if with next") { r = [1, 2, 3]; [r.delete_if { |x| next true if x == 2; false }, r] }
show("delete with block") { [1, 2, 3].delete(9) { :nope } }
show("fetch with block") { [1, 2, 3].fetch(9) { |i| i } }
show("find_index no block no arg") { [1, 2, 3].find_index.class }
show("rindex no block no arg") { [1, 2, 3].rindex.class }
show("index no block no arg") { [1, 2, 3].index.class }
show("find_index enum next") { [1, 2, 3].find_index.each { |x| x == 2 } }
show("rindex enum next") { [1, 2, 3].rindex.each { |x| x == 2 } }

# ------------------------------------------------------------------ recursion
show("self-ref inspect") { a = [1]; a << a; a.inspect }
show("self-ref to_s") { a = [1]; a << a; a.to_s }
show("self-ref flatten") { a = [1]; a << a; a.flatten }
show("self-ref join") { a = [1]; a << a; a.join(",") }
show("self-ref hash is Integer") { a = [1]; a << a; a.hash.is_a?(Integer) }
show("self-ref hash equal") { a = [1]; a << a; b = [1]; b << b; a.hash == b.hash }
show("self-ref ==") { a = [1]; a << a; b = [1]; b << b; a == b }
show("self-ref eql?") { a = [1]; a << a; b = [1]; b << b; a.eql?(b) }
show("self-ref uniq") { a = [1, 1]; a << a; a.uniq }
show("self-ref uniq!") { a = [1, 1]; a << a; a.uniq!; a }
show("hash of [] is Integer") { [].hash.is_a?(Integer) }
show("hash [[]] != [[[]]]") { [[]].hash != [[[]]].hash }
show("hash [1,2] == [1,2]") { [1, 2].hash == [1, 2].hash }
show("hash [[1],[2]] vs [[1,2]]") { [[1], [2]].hash != [[1, 2]].hash }
show("recursive through hash") do
  a = []
  h = {a => 1}
  a << h
  b = []
  hb = {b => 1}
  b << hb
  a.hash == b.hash
end

# -------------------------------------------------------------------- sorting
show("sort with raising <=>") { [RaiseCmp.new, RaiseCmp.new].sort }
show("sort mixed types") { [1, "a"].sort }
show("sort block returns nil") { [1, 2].sort { |a, b| nil } }
show("min mixed") { [1, "a"].min }
show("max mixed") { [1, "a"].max }
show("sort! with break") { r = [3, 1, 2]; [r.sort! { |a, b| break :b }, r] }
show("min(n) on array") { [3, 1, 2].min(2) }
show("max(n) on array") { [3, 1, 2].max(2) }
show("min(nil)") { [3, 1, 2].min(nil) }
show("max(nil)") { [3, 1, 2].max(nil) }
show("Array#max owner") { Array.instance_method(:max).owner }
show("Array#min owner") { Array.instance_method(:min).owner }
show("Array#sum owner") { Array.instance_method(:sum).owner }
show("sum of floats") { ([0.1] * 10).sum }
show("sum inf nan") { [Float::INFINITY, -Float::INFINITY].sum.nan? }
show("sum nan + int") { [Float::NAN, 1].sum.nan? }
show("sum strings") { ["a", "b"].sum("") }
show("sum init mismatch") { [1, 2].sum("") }

# ---------------------------------------------------------------- misc/output
show("[].inspect encoding") { [].inspect.encoding }
show("[1].inspect encoding") { [1].inspect.encoding }
show('["é"].inspect encoding') { ["é"].inspect.encoding }
show("[].join encoding") { [].join.encoding }
show("[1,2].join(nil)") { [1, 2].join(nil) }
show("[1,[2,[3]]].join") { [1, [2, [3]]].join("-") }
show("self-ref join raises") { a = [1]; a << a; a.join("-") }
show("[1,2].join(ToStr)") { [1, 2].join(Object.new) }
show("[nil].join") { [nil].join }

show("combination(2).size") { [1, 2, 3].combination(2).size }
show("combination(9).size") { [1, 2, 3].combination(9).size }
show("combination(-1).size") { [1, 2, 3].combination(-1).size }
show("permutation(2).size") { [1, 2, 3].permutation(2).size }
show("permutation.size") { [1, 2, 3].permutation.size }
show("permutation(9).size") { [1, 2, 3].permutation(9).size }
show("[].permutation(0).size") { [].permutation(0).size }
show("repeated_combination(2).size") { [1, 2, 3].repeated_combination(2).size }
show("repeated_permutation(2).size") { [1, 2, 3].repeated_permutation(2).size }
show("each_slice(2).size") { [1, 2, 3].each_slice(2).size }
show("each_cons(2).size") { [1, 2, 3].each_cons(2).size }
show("product size") { [1, 2].product([3, 4]) }
show("product with block") { r = []; [[1, 2].product([3, 4]) { |x| r << x }, r] }
show("cycle(2).size") { [1, 2].cycle(2).size }
show("cycle.size") { [1, 2].cycle.size }
show("each_entry.size") { [1, 2].each_entry.size }
show("map.size") { [1, 2].map.size }
show("select.size") { [1, 2].select.size }

show("concat multiple") { [1].concat([2], [3]) }
show("concat none") { [1].concat }
show("concat self twice") { a = [1, 2]; a.concat(a, a) }
show("push none") { [1].push }
show("unshift none") { [1].unshift }
show("append") { [1].append(2, 3) }
show("prepend") { [1].prepend(2, 3) }

show("delete_if modified in block") { a = [1, 2, 3]; a.delete_if { |x| a.pop if x == 1; false }; a }
show("reject! modified in block") { a = [1, 2, 3]; a.reject! { |x| a.pop if x == 1; false }; a }
show("each modified in block") { a = [1, 2, 3]; r = []; a.each { |x| r << x; a.pop if x == 1 }; r }
show("map! modified in block") { a = [1, 2, 3]; a.map! { |x| a.pop if x == 1; x }; a }

show("flatten depth nil") { [[1, [2, [3]]]].flatten(nil) }
show("flatten! no change") { [1, 2].flatten! }
show("flatten recursive") { a = [1]; a << a; a.flatten }
show("flatten! recursive") { a = [1]; a << a; a.flatten! }

show("pack @") { [1].pack("@") }
show("pack empty") { [].pack("") }
show("uniq with nil block result") { [1, 2, 3].uniq { |x| nil } }
show("uniq! no dupes") { [1, 2].uniq! }
show("bsearch find-min") { [0, 4, 7, 10].bsearch { |x| x >= 4 } }
show("bsearch find-any") { [0, 4, 7, 10].bsearch { |x| 4 <=> x } }
show("bsearch bad block") { [0, 4].bsearch { |x| "x" } }
show("bsearch_index") { [0, 4, 7, 10].bsearch_index { |x| x >= 7 } }
show("sum with block") { [1, 2].sum { |x| x * 2 } }
show("assoc non-array") { [1, [2, 3]].assoc(2) }
show("rassoc non-array") { [1, [2, 3]].rassoc(3) }
show("Array#hash frozen dup") { [1, 2].hash == [1, 2].dup.hash }

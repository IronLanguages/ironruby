# Differential matrix for external enumeration: Enumerator#next, #peek, #next_values,
# #peek_values, #feed and #rewind over the built-in iteration sources IronRuby walks with a
# cursor instead of a fiber (Array, Hash, Range, Integer, String, Struct), and over the ones that
# keep the fiber (generators, lazy chains, redefined methods...).
#   ruby Util/enum-next-matrix.rb            # CRuby reference
#   ./ir.sh Util/enum-next-matrix.rb         # IronRuby
# Diff the two outputs.

def show(label)
  v = begin
        yield.inspect
      rescue Exception => e
        "!#{e.class}: #{e.message}"
      end
  puts "#{label.ljust(52)} #{v}"
end

# A generator's #each answers its Yielder, whose inspect shows an address.
def result_of(stop)
  r = stop.result
  r.is_a?(Enumerator::Yielder) ? Enumerator::Yielder : r
end

# Everything the enumerator gives out, then what StopIteration says about the end.
def drain(e, limit = 20)
  out = []
  limit.times do
    out << e.next
  end
  out << :more
rescue StopIteration => s
  out << [:stop, s.message, result_of(s)]
end

def drain_values(e, limit = 20)
  out = []
  limit.times do
    out << e.peek_values << e.next_values
  end
  out
rescue StopIteration => s
  out << [:stop, result_of(s)]
end

S = Struct.new(:a, :b)
class SubArray < Array; end
class SubHash < Hash; end
class EachArray < Array
  def each
    yield :own
    self
  end
end
class RewindArray < Array
  def rewind
    $rewound = (($rewound || 0) + 1)
  end
end

sources = {
  "[1, 2, 3].each"              => -> { [1, 2, 3].each },
  "[].each"                     => -> { [].each },
  "[1, 2, 3].to_enum"           => -> { [1, 2, 3].to_enum },
  "[1, 2, 3].each_entry"        => -> { [1, 2, 3].each_entry },
  "[1, 2, 3].each_with_index"   => -> { [1, 2, 3].each_with_index },
  "[1, 2, 3].reverse_each"      => -> { [1, 2, 3].reverse_each },
  "[1, 2, 3].each_index"        => -> { [1, 2, 3].each_index },
  "[[1, 2], [3, 4]].each"       => -> { [[1, 2], [3, 4]].each },
  "[1, 2].map"                  => -> { [1, 2].map },
  "[1, 2].each_slice(1)"        => -> { [1, 2].each_slice(1) },
  "[1, 2].each.with_index(1)"   => -> { [1, 2].each.with_index(1) },
  "[1, 2].lazy.map"             => -> { [1, 2].lazy.map { |x| x * 2 } },
  "[1, 2].each + [3]"           => -> { [1, 2].each + [3] },
  "SubArray[1, 2].each"         => -> { SubArray[1, 2].each },
  "EachArray[1, 2].each"        => -> { EachArray[1, 2].each },
  "[1, 2].freeze.each"          => -> { [1, 2].freeze.each },
  "(1..3).each"                 => -> { (1..3).each },
  "(1...3).each"                => -> { (1...3).each },
  "(3..1).each"                 => -> { (3..1).each },
  "(1..).each"                  => -> { (1..).each },
  "(2**31 - 2..2**31 + 1).each" => -> { (2**31 - 2..2**31 + 1).each },
  "(2**64..2**64 + 1).each"     => -> { (2**64..2**64 + 1).each },
  "(1.0..3).each"               => -> { (1.0..3).each },
  "(..3).each"                  => -> { (..3).each },
  "('a'..'c').each"             => -> { ('a'..'c').each },
  "(1..3.5).each"               => -> { (1..3.5).each },
  "{a: 1, b: 2}.each"           => -> { {a: 1, b: 2}.each },
  "{a: 1, b: 2}.each_pair"      => -> { {a: 1, b: 2}.each_pair },
  "{a: 1, b: 2}.each_key"       => -> { {a: 1, b: 2}.each_key },
  "{a: 1, b: 2}.each_value"     => -> { {a: 1, b: 2}.each_value },
  "{}.each"                     => -> { {}.each },
  "SubHash.each"                => -> { h = SubHash.new; h[:x] = 1; h.each },
  "3.times"                     => -> { 3.times },
  "0.times"                     => -> { 0.times },
  "-2.times"                    => -> { -2.times },
  "(2**64).times (first)"       => -> { (2**64).times },
  "1.upto(3)"                   => -> { 1.upto(3) },
  "3.upto(1)"                   => -> { 3.upto(1) },
  "1.upto(2.5)"                 => -> { 1.upto(2.5) },
  "(2**31 - 2).upto(2**31)"     => -> { (2**31 - 2).upto(2**31) },
  "3.downto(1)"                 => -> { 3.downto(1) },
  "1.downto(3)"                 => -> { 1.downto(3) },
  "(-2**31 + 1).downto(-2**31 - 1)" => -> { (-2**31 + 1).downto(-2**31 - 1) },
  "'héllo'.each_char"           => -> { 'héllo'.each_char },
  "'héllo'.each_byte"           => -> { 'héllo'.each_byte },
  "''.each_char"                => -> { ''.each_char },
  "\"\\xff\\xfe\".b.each_char"  => -> { "a\xffb".each_char },
  "'ab'.encode('UTF-16LE').each_char" => -> { 'ab'.encode('UTF-16LE').each_char },
  "S.new(1, 2).each"            => -> { S.new(1, 2).each },
  "S.new(1, 2).each_pair"       => -> { S.new(1, 2).each_pair },
  "Enumerator.new"              => -> { Enumerator.new { |y| y << 1; y.yield 2, 3; y.yield; :gen } },
  "Enumerator.new(lazy)"        => -> { Enumerator.new { |y| y << 1 << 2 }.lazy },
}

sources.each do |name, make|
  show("#{name}: next") { drain(make.call, 6) }
  show("#{name}: values") { drain_values(make.call, 6) }
  show("#{name}: peek twice") { e = make.call; [e.peek, e.peek, e.next] }
  show("#{name}: rewind") { e = make.call; a = [e.next]; e.rewind; a << e.next }
  show("#{name}: after end") do
    e = make.call
    drain(e, 6)
    a = (e.next rescue $!)
    b = (e.next rescue $!)
    c = (e.peek rescue $!)
    # the first StopIteration is the cause of every later one
    [a.class, result_of(a), b.class, b.equal?(a), a.cause.class, a.cause.equal?(b.cause), c.class]
  end
  show("#{name}: rewind after end") { e = make.call; drain(e, 6); e.rewind; e.next }
end

# ---- mutation during an external iteration ----

show("array: push while walking") { a = [1, 2]; e = a.each; r = [e.next]; a << 3; r << e.next << e.next; r }
show("array: shift while walking") { a = [1, 2, 3]; e = a.each; r = [e.next]; a.shift; r << e.next; r << (e.next rescue $!.class) }
show("array: clear while walking") { a = [1, 2, 3]; e = a.each; r = [e.next]; a.clear; r << (e.next rescue $!.result) }
show("array: replace while peeked") { a = [1, 2, 3]; e = a.each; r = [e.peek]; a.replace([7, 8, 9]); r << e.next << e.next; r }
show("array: push before first next") { a = [1]; e = a.each; a << 2; drain(e) }
show("array: reverse_each shrink") { a = [1, 2, 3, 4]; e = a.reverse_each; r = [e.next]; a.pop(3); r << (e.next rescue $!.class) << (e.next rescue $!.class); r }
show("array: reverse_each grow") { a = [1, 2, 3]; e = a.reverse_each; r = [e.next]; a << 4; r << e.next << e.next; r }
show("array: each_index grow") { a = [1]; e = a.each_index; r = [e.next]; a << 2; r << e.next; r }
show("array: each_with_index push") { a = [1]; e = a.each_with_index; r = [e.next]; a << 2; r << e.next; r }
show("array: frozen later") { a = [1, 2]; e = a.each; r = [e.next]; a.freeze; r << e.next; r }

show("hash: add during") { h = {a: 1}; e = h.each; e.next; h[:b] = 2 }
show("hash: add after peek") { h = {a: 1}; e = h.each; e.peek; h[:b] = 2 }
show("hash: add before first next") { h = {a: 1}; e = h.each; h[:b] = 2; drain(e) }
show("hash: update during") { h = {a: 1, b: 2}; e = h.each; r = [e.next]; h[:b] = 20; r << e.next; r }
show("hash: delete ahead") { h = {a: 1, b: 2, c: 3}; e = h.each_key; r = [e.next]; h.delete(:b); r << e.next << (e.next rescue $!.class); r }
show("hash: delete current") { h = {a: 1, b: 2}; e = h.each_value; r = [e.next]; h.delete(:a); r << e.next; r }
show("hash: shift during") { h = {a: 1, b: 2, c: 3}; e = h.each; r = [e.next]; h.shift; r << e.next; r }
show("hash: clear during") { h = {a: 1, b: 2}; e = h.each; r = [e.next]; h.clear; r << (e.next rescue $!.result); r }
show("hash: delete all then add") { h = {a: 1}; e = h.each; e.next; h.delete(:a); h[:z] = 1 }
show("hash: rehash during") { h = {a: 1}; e = h.each; e.next; h.rehash }
show("hash: compare_by_identity during") { h = {a: 1}; e = h.each; e.next; h.compare_by_identity }
show("hash: replace during") { h = {a: 1}; e = h.each; e.next; h.replace({b: 1}) }
show("hash: merge! existing during") { h = {a: 1}; e = h.each; e.next; h.merge!(a: 5) }
show("hash: add after last next") { h = {a: 1}; e = h.each; e.next; h[:b] = 2 }
show("hash: add after end") { h = {a: 1}; e = h.each; drain(e); h[:b] = 2; h }
show("hash: add after rewind") { h = {a: 1, b: 2}; e = h.each; e.next; e.rewind; h[:c] = 3; h }
show("hash: add after dropping it") { h = {a: 1}; e = h.each; e.next; e = nil; GC.start; h.store(:b, 2) }
show("hash: two enumerators") { h = {a: 1}; e1 = h.each; e2 = h.each; e1.next; e2.next; drain(e1); (h[:b] = 2 rescue $!.class) }
show("hash: frozen later") { h = {a: 1, b: 2}; e = h.each; r = [e.next]; h.freeze; r << e.next; r }

show("string: each_char mutate") { s = +"abc"; e = s.each_char; r = [e.next]; s.replace("xyz!"); r << e.next << e.next << (e.next rescue $!.result); r }
show("string: each_char mutate before") { s = +"abc"; e = s.each_char; s << "d"; drain(e) }
show("string: each_byte mutate") { s = +"abc"; e = s.each_byte; r = [e.next]; s.replace("xyz!"); r << e.next << e.next << e.next; r }
show("string: each_byte shrink") { s = +"abc"; e = s.each_byte; r = [e.next]; s.replace("x"); r << (e.next rescue $!.result); r }
show("struct: each mutate") { s = S.new(1, 2); e = s.each; r = [e.next]; s.b = 5; r << e.next; r }

# ---- what decides the iteration: looked up when it starts ----

show("singleton each before next") { a = [1, 2]; e = a.each; def a.each; yield :single; self; end; drain(e) }
show("singleton each after next") { a = [1, 2]; e = a.each; r = [e.next]; def a.each; yield :single; self; end; r << e.next; r }
show("singleton each on hash") { h = {a: 1}; def h.each; yield :single; self; end; drain(h.each) }
show("singleton times") { n = 2**70; drain(n.times, 3) }
show("singleton each on struct") { s = S.new(1, 2); def s.each; yield :single; self; end; drain(s.to_enum) }
show("singleton each on enumerator") { e = [1, 2].each; def e.each; yield :enum; :r; end; drain(e) }
show("singleton rewind on receiver") { a = [1, 2]; def a.rewind; $rw = :called; end; e = a.each; e.next; $rw = nil; e.rewind; [$rw, e.next] }
show("private rewind on receiver") { a = [1, 2]; class << a; private def rewind; $rw = :private; end; end; e = a.each; e.next; $rw = nil; e.rewind; [$rw, e.next] }
show("respond_to_missing rewind") { a = [1]; def a.respond_to_missing?(m, p = false) = m == :rewind || super; def a.method_missing(m, *) = m == :rewind ? ($rw = :mm) : super; $rw = nil; e = a.each; e.next; e.rewind; $rw }
show("subclass rewind") { $rewound = 0; a = RewindArray[1, 2]; e = a.each; e.next; e.rewind; e.rewind; [$rewound, e.next] }
show("feed on each") { e = [1, 2, 3].each; e.next; e.feed :x; r = [(e.feed(:y) rescue $!.class)]; r << e.next; e.feed :z; r << e.next; r << (e.next rescue $!.result); r }
show("feed before first next") { e = [1, 2].each; e.feed :x; r = [e.next, (e.feed(:y) rescue $!.class)]; r << e.next; e.feed(:w); r }
show("feed after end") { e = [1].each; e.next; e.feed :x; r = [(e.next rescue $!.result)]; r << (e.feed(:y) rescue $!.class); r }
show("feed on map") { e = [1, 2].map; e.next; e.feed :a; e.next; e.feed :b; (e.next rescue $!.result) }
show("feed on generator") { e = Enumerator.new { |y| [y.yield(1), y.yield(2)] }; e.next; e.feed :a; e.next; (e.next rescue $!.result) }
show("frozen enumerator next") { e = [1].each.freeze; e.next }
show("frozen enumerator peek") { e = [1].each.freeze; e.peek }
show("frozen enumerator rewind") { e = [1].each.freeze; e.rewind; :ok }
show("frozen enumerator feed") { e = [1].each.freeze; e.feed(1); :ok }
show("dup while walking") { e = [1, 2].each; e.next; e.dup }
show("dup before") { e = [1, 2].each; d = e.dup; [e.next, d.next, d.next, e.next] }
show("dup after end") { e = [1].each; drain(e); e.dup.next }
show("clone while peeked") { e = {a: 1}.each; e.peek; e.clone }
show("size while walking") { e = [1, 2, 3].each; e.next; e.size }
show("inspect while walking") { e = (1..3).each; e.next; e.inspect }
show("next across threads") { e = [1, 2].each; e.next; Thread.new { e.next rescue $! }.value.then { |x| [x.class, x.message] } }
show("peeked next across threads") { e = [1, 2].each; e.peek; Thread.new { e.next rescue $!.class }.value }
show("end across threads") { e = [1].each; e.next; (e.next rescue nil); Thread.new { e.next rescue $!.class }.value }
show("first next in thread") { e = [1, 2].each; [Thread.new { e.next }.value, (e.next rescue $!.class)] }
show("rewind then other thread") { e = [1, 2].each; e.next; e.rewind; Thread.new { e.next }.value }
show("next across fibers") { e = [1, 2, 3].each; r = [e.next]; Fiber.new { r << e.next }.resume; r << e.next; r }
show("hash next across threads") { h = {a: 1, b: 2}; e = h.each; e.next; Thread.new { e.next rescue $!.class }.value }
show("each during external") { a = [1, 2]; e = a.each; e.next; r = []; e.each { |x| r << x }; r << e.next; r }
show("loop rescues StopIteration") { e = [1, 2].each; r = []; x = loop { r << e.next }; [r, x] }
show("loop result hash") { e = {a: 1}.each; loop { e.next } }
show("zip uses next") { [1, 2, 3].zip((4..5).each) }
show("external with_index") { e = [1, 2].each_with_index; [e.next, e.next_values, (e.peek rescue $!.result)] }

class Array
  alias_method :__matrix_orig_each__, :each
  def each(&b)
    return to_enum(:each) unless b
    __matrix_orig_each__ { |x| b.call(x * 10) }
  end
end
show("Array#each redefined") { drain([1, 2].each) }
show("Array#each redefined, each_with_index") { drain([1, 2].each_with_index) }
class Array
  alias_method :each, :__matrix_orig_each__
end
show("Array#each aliased back") { drain([1, 2].each) }
class Hash
  alias_method :__matrix_orig_each_key__, :each_key
  def each_key
    return to_enum(:each_key) unless block_given?
    __matrix_orig_each_key__ { |k| yield k.to_s }
  end
end
show("Hash#each_key redefined") { drain({a: 1}.each_key) }
class Integer
  alias_method :__matrix_orig_times__, :times
  def times
    return to_enum(:times) unless block_given?
    __matrix_orig_times__ { |i| yield(-i) }
  end
end
show("Integer#times redefined") { drain(2.times) }
class Integer
  alias_method :times, :__matrix_orig_times__
end
show("Integer#times restored") { drain(2.times) }
module PrependEach
  def each
    return to_enum(:each) unless block_given?
    super { |x| yield [:p, x] }
  end
end
class SubArray
  prepend PrependEach
end
show("prepended each on subclass") { drain(SubArray[1].each) }
class Range
  def each
    return to_enum(:each) unless block_given?
    yield :range
    self
  end
end
show("Range#each redefined") { drain((1..2).each) }

# Last: `using` reaches every call after it in the file.
module RefinedArrayEach
  refine Array do
    def each
      yield :refined
      self
    end
  end
end
using RefinedArrayEach
show("refined each, enumerator") { drain([1, 2].each) }
show("refined each, to_enum") { drain([1, 2].to_enum) }

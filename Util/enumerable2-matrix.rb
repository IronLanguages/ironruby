# Differential matrix for Enumerable: receivers x arguments x blocks, one line each.
#   ruby Util/enumerable2-matrix.rb            # CRuby reference
#   ./ir.sh Util/enumerable2-matrix.rb         # IronRuby
# Diff the two outputs; every differing line is a bug.

def show(label)
  v = begin
        yield.inspect
      rescue Exception => e
        "!#{e.class}: #{e.message}"
      end
  puts "#{label.ljust(56)} #{v}"
end

# An Enumerable that is nothing but #each: no size, no Array shortcuts.
class Plain
  include Enumerable
  def initialize(*list) = @list = list
  def each(*args)
    @list.each { |x| yield(x, *args) }
    self
  end
  def inspect = "Plain"
end

# Yields several values per iteration, the case Enumerable has to pack.
class Multi
  include Enumerable
  def each
    yield 1, 2
    yield 3, 4
    self
  end
  def inspect = "Multi"
end

# Yields nothing at all.
class Nothing
  include Enumerable
  def each
    yield
    yield
    self
  end
  def inspect = "Nothing"
end

class Sized
  include Enumerable
  def each = [1, 2, 3, 4].each { |x| yield x }
  def size = 4
  def inspect = "Sized"
end

RECEIVERS = {
  "[]"      => -> { [] },
  "Plain"   => -> { Plain.new(3, 1, 2, 1) },
  "Multi"   => -> { Multi.new },
  "Nothing" => -> { Nothing.new },
  "Sized"   => -> { Sized.new },
  "Hash"    => -> { {a: 1, b: 2} },
}

# ---------------------------------------------------------- block-less results
NO_BLOCK = %w[
  chunk_while slice_when each_entry each_with_index each_with_object group_by
  partition sort_by min_by max_by minmax_by flat_map filter_map find detect
  find_index find_all select reject collect map take_while drop_while
  each_cons each_slice zip cycle chunk reverse_each sum uniq to_set tally
  each_index
]
RECEIVERS.each do |rn, rf|
  NO_BLOCK.each do |m|
    args = case m
           when "each_with_object" then [0]
           when "each_cons", "each_slice" then [2]
           when "zip" then [[9, 8]]
           else []
           end
    show("#{rn}.#{m} class") { rf.call.send(m, *args).class }
    show("#{rn}.#{m} size") { rf.call.send(m, *args).size }
  end
end

# ------------------------------------------------------------------- iteration
RECEIVERS.each do |rn, rf|
  show("#{rn}.map 1-arg") { rf.call.map { |x| x } }
  show("#{rn}.map 2-arg") { rf.call.map { |x, y| [x, y] } }
  show("#{rn}.map splat") { rf.call.map { |*x| x } }
  show("#{rn}.map arity") { r = []; rf.call.map { |a, b| r << 0 }; r.size }
  show("#{rn}.each_entry") { rf.call.each_entry.to_a }
  show("#{rn}.to_a") { rf.call.to_a }
  show("#{rn}.entries") { rf.call.entries }
  show("#{rn}.sort_by") { rf.call.sort_by { |x| x.hash } .size }
  show("#{rn}.group_by") { rf.call.group_by { |x| x.class }.keys.map(&:to_s).sort }
  show("#{rn}.flat_map") { rf.call.flat_map { |x| x } }
  show("#{rn}.tally") { rf.call.tally.size }
  show("#{rn}.uniq") { rf.call.uniq.size }
  show("#{rn}.compact") { rf.call.compact.size }
  show("#{rn}.first") { rf.call.first }
  show("#{rn}.first(2)") { rf.call.first(2) }
  show("#{rn}.count") { rf.call.count }
  show("#{rn}.include?(1)") { rf.call.include?(1) }
  show("#{rn}.each_with_index.to_a") { rf.call.each_with_index.to_a }
  show("#{rn}.each_with_object([]).to_a") { rf.call.each_with_object([]).to_a }
  show("#{rn}.zip([9,8])") { rf.call.zip([9, 8]) }
  show("#{rn}.chunk_while") { rf.call.chunk_while { |a, b| true }.to_a }
  show("#{rn}.slice_when") { rf.call.slice_when { |a, b| true }.to_a }
  show("#{rn}.chunk") { rf.call.chunk { |x| x.class }.to_a }
  show("#{rn}.slice_before(1)") { rf.call.slice_before(1).to_a }
  show("#{rn}.slice_after(1)") { rf.call.slice_after(1).to_a }
  show("#{rn}.each_cons(2)") { rf.call.each_cons(2).to_a }
  show("#{rn}.each_slice(2)") { rf.call.each_slice(2).to_a }
  show("#{rn}.each_cons(2){} ret") { rf.call.each_cons(2) { |x| } }
  show("#{rn}.each_slice(2){} ret") { rf.call.each_slice(2) { |x| } }
  show("#{rn}.min_by") { rf.call.min_by { |x| x.hash } .class }
  show("#{rn}.inject(:+)") { rf.call.inject(:+) }
  show("#{rn}.sum") { rf.call.sum }
  show("#{rn}.filter_map") { rf.call.filter_map { |x| x } }
  show("#{rn}.to_h") { rf.call.to_h { |*a| [a.first, 1] } }
end

# ---------------------------------------------------------------------- chunk
show("chunk drops nil") { [1, 2, 3, 2, 1].chunk { |x| x == 2 ? nil : 1 }.to_a }
show("chunk drops _separator") { [1, 2, 3, 2, 1].chunk { |x| x == 2 ? :_separator : 1 }.to_a }
show("chunk _alone") { [1, 1, 2, 2].chunk { |x| x == 1 ? :_alone : x }.to_a }
show("chunk other underscore") { [1, 2].chunk { |x| :_arbitrary }.to_a }
show("chunk no block") { [1, 2].chunk.class }
show("chunk lazy") { [1, 2].chunk { |x| x }.class }
show("chunk_while no block") { [1, 2].chunk_while }
show("slice_when no block") { [1, 2].slice_when }
show("slice_before both") { [1, 2].slice_before(1) { |x| true } }
show("slice_before neither") { [1, 2].slice_before }
show("slice_after both") { [1, 2].slice_after(1) { |x| true } }
show("slice_after neither") { [1, 2].slice_after }
show("slice_before 2 args") { [1, 2].slice_before(1, 2) }
show("slice_after 2 args") { [1, 2].slice_after(1, 2) }
show("slice_before enum") { [1, 2].slice_before(1).class }
show("slice_after enum") { [1, 2].slice_after(1).class }

# ----------------------------------------------------------------- min/max/n
show("min(nil)") { Plain.new(3, 1, 2).min(nil) }
show("max(nil)") { Plain.new(3, 1, 2).max(nil) }
show("min_by(nil)") { Plain.new(3, 1, 2).min_by(nil) { |x| x } }
show("max_by(nil)") { Plain.new(3, 1, 2).max_by(nil) { |x| x } }
show("min(2)") { Plain.new(3, 1, 2).min(2) }
show("max(2)") { Plain.new(3, 1, 2).max(2) }
show("min(-1)") { Plain.new(3, 1, 2).min(-1) }
show("minmax_by size") { Sized.new.minmax_by.size }
show("minmax_by class") { Sized.new.minmax_by.class }

# --------------------------------------------------------------------- tally
show("tally hash") { [1, 1, 2].tally({1 => 10}) }
show("tally hash frozen") { [1].tally({}.freeze) }
show("tally hash empty frozen") { [].tally({}.freeze) }
show("tally hash bad value") { [1].tally({1 => "x"}) }
show("tally multi") { Multi.new.tally }

# ----------------------------------------------------------------------- zip
class EnumConv
  include Enumerable
  def each = [1, 2].each { |x| yield x }
  def to_enum(*) = [1, 2].each
  def inspect = "EnumConv"
end
show("zip to_enum-able") { [1, 2].zip(EnumConv.new) }
show("zip Multi") { Multi.new.zip(Multi.new) }
show("zip non-enumerable") { [1].zip(Object.new) }
show("zip nil") { [1].zip(nil) }
show("Plain.zip range") { Plain.new(1, 2).zip(1..2) }

# ---------------------------------------------------------------- inject/etc
show("inject bad symbol") { [1, 2].inject(Object.new) }
show("inject bad symbol 2") { [1, 2].inject(0, Object.new) }
show("inject str name") { [1, 2].inject("+") }
show("inject 3 args") { [1, 2].inject(0, :+, :+) }
show("inject empty") { [].inject(:+) }
show("inject block+sym") { [1, 2].inject(0, :+) { |a, b| 99 } }

# ------------------------------------------------------------------ grep / $~
show("grep sets $~") { ["a", "b"].grep(/b/) { $~ && $~[0] } }
show("grep_v sets $~") { ["a", "b"].grep_v(/b/) { $~.nil? } }
show("grep no block") { ["a", "b"].grep(/b/) }
show("grep pattern class") { [1, "a"].grep(String) }

# ----------------------------------------------------------- extra each args
show("each_entry extra args") { Plain.new(1).each_entry(:x, :y) { |a| a }.inspect && :ok }
show("each_with_index extra") { r = []; Plain.new(1).each_with_index(:x) { |a, i| r << [a, i] }; r }
show("to_h extra args") { Plain.new(1).to_h(:x) { |a| [a, 1] } }
show("flat_map to_ary bad") do
  o = Object.new
  def o.to_ary = 1
  [1, o].flat_map { |x| x }
end

# ------------------------------------------------------------------- to_set
require 'set'
show("to_set") { [1, 2].to_set.class }
show("to_set subclass") { c = Class.new(Set); [1, 2].to_set(c).class == c }

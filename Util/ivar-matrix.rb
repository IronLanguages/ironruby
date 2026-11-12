# Differential matrix for instance variables (the shape-based storage in
# Src/Ruby/Runtime/RubyInstanceData.cs and InstanceVariableShape.cs). Run under CRuby and
# under ./ir.sh and diff:
#
#   ruby Util/ivar-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/ivar-matrix.rb > /tmp/ir.txt
#   diff /tmp/mri.txt /tmp/ir.txt
#
# They must be identical.
#
# Every kind of receiver - a plain object, a subclass's, a module and a class (class-level
# @x), a singleton class, a String, an Array, a Hash, a Struct, an exception, an object with
# 1 and with 100 variables, objects of one class that got their variables in different
# orders, and objects pushed past the shape tree's limits ("too complex") - goes through the
# same operations: add, read (by `@x` and by instance_variable_get), defined?, remove,
# the order of #instance_variables, freeze, dup, clone and Marshal.

def t(label)
  print label.ljust(64), ' => '
  begin
    v = yield
    puts v.inspect.gsub(/0x\h+/, '0xXX')
  rescue Exception => e
    puts "#{e.class}: #{e.message.gsub(/0x\h+/, '0xXX')}"
  end
end

class Plain
  def initialize(a = 1, b = 2)
    @a = a
    @b = b
  end
  def get_a = @a
  def get_b = @b
  def get_c = @c
  def set_a(v) = (@a = v)
  def set_c(v) = (@c = v)
  def def_c = defined?(@c)
end

class Sub < Plain
  def initialize
    super(10, 20)
    @s = 30
  end
  def get_s = @s
end

module Mod
  @m1 = :one
  @m2 = :two
  def self.m1 = @m1
  def self.set_m3(v) = (@m3 = v)
end

class Klass
  @k = 1
  class << self
    attr_accessor :k, :k2
  end
end

Pair = Struct.new(:x, :y)
Point = Data.define(:x, :y)

class MyError < StandardError
  def initialize(msg = "boom")
    super
    @detail = :d
  end
  attr_reader :detail
end

def targets
  single = Object.new
  class << single
    @on_singleton_class = 1
  end
  single.instance_variable_set(:@on_object, 2)

  {
    "plain" => Plain.new,
    "subclass" => Sub.new,
    "module" => Mod,
    "class" => Klass,
    "singleton class" => single.singleton_class,
    "object with singleton" => single,
    "string" => +"str",
    "array" => [1, 2],
    "hash" => { k: 1 },
    "struct" => Pair.new(1, 2),
    "exception" => MyError.new,
    "object (no ivars)" => Object.new,
  }
end

# ---------------------------------------------------------------- basic operations

targets.each do |name, obj|
  t("#{name}: instance_variables") { obj.instance_variables }
  t("#{name}: set @n1, @n2, @n3") { [obj.instance_variable_set(:@n1, 1), obj.instance_variable_set("@n2", 2), obj.instance_variable_set(:@n3, 3)] }
  t("#{name}: instance_variables after set") { obj.instance_variables }
  t("#{name}: get @n2 / @missing") { [obj.instance_variable_get(:@n2), obj.instance_variable_get(:@missing)] }
  t("#{name}: defined? @n1 / @missing") { [obj.instance_variable_defined?(:@n1), obj.instance_variable_defined?(:@missing)] }
  t("#{name}: overwrite @n1") { obj.instance_variable_set(:@n1, :x); obj.instance_variable_get(:@n1) }
  t("#{name}: remove @n1") { obj.remove_instance_variable(:@n1) }
  t("#{name}: after remove") { [obj.instance_variables, obj.instance_variable_get(:@n1), obj.instance_variable_defined?(:@n1)] }
  t("#{name}: remove @n1 again") { obj.remove_instance_variable(:@n1) }
  t("#{name}: re-add @n1 goes last") { obj.instance_variable_set(:@n1, 11); obj.instance_variables }
  t("#{name}: remove middle @n3") { [obj.remove_instance_variable(:@n3), obj.instance_variables] }
  t("#{name}: values in order") { obj.instance_variables.map { |v| obj.instance_variable_get(v) } }
  t("#{name}: bad name") { obj.instance_variable_set(:bad, 1) }
  t("#{name}: bad name get") { obj.instance_variable_get("@") }
  t("#{name}: dup ivars") { d = obj.dup; [d.instance_variables, d.instance_variables.map { |v| d.instance_variable_get(v) }] } unless obj.is_a?(Module) && name != "singleton class"
  t("#{name}: dup is independent") { d = obj.dup; d.instance_variable_set(:@n2, :changed); [obj.instance_variable_get(:@n2), d.instance_variable_get(:@n2)] } unless obj.is_a?(Module)
  t("#{name}: clone ivars") { c = obj.clone; [c.instance_variables, c.instance_variable_get(:@n2)] } unless name == "singleton class"
end

# ---------------------------------------------------------------- @x syntax, compiled sites

t("plain: @x reads") { p = Plain.new; [p.get_a, p.get_b, p.get_c] }
t("plain: @x write existing") { p = Plain.new; p.set_a(5); [p.get_a, p.instance_variables] }
t("plain: @x add") { p = Plain.new; p.set_c(7); [p.get_c, p.instance_variables, p.def_c] }
t("plain: defined?(@c) before") { Plain.new.def_c }
t("plain: many objects, one site") do
  objs = 50.times.map { |i| Plain.new(i, -i) }
  objs.each_with_index { |o, i| o.set_c(i * 2) if i.even? }
  objs.map { |o| [o.get_a, o.get_c] }.flatten.sum { |v| v.to_i }
end
t("plain: site sees differently shaped objects") do
  a = Plain.new; b = Plain.new; b.set_c(1); c = Plain.new; c.remove_instance_variable(:@a)
  [a, b, c, a, b, c].map { |o| [o.get_a, o.get_b, o.get_c] }
end
t("subclass: @x from superclass method") { s = Sub.new; [s.get_a, s.get_b, s.get_s, s.instance_variables] }
t("module: class-level @x") { [Mod.m1, Mod.set_m3(3), Mod.instance_variables] }
t("class: attr_accessor on class") { Klass.k2 = 5; [Klass.k, Klass.k2, Klass.instance_variables] }
t("struct: members are not ivars") { Pair.new(1, 2).instance_variables }
t("data: members are not ivars") { Point.new(x: 1, y: 2).instance_variables }
t("data: ivar alongside members is frozen") { Point.new(x: 1, y: 2).instance_variable_set(:@z, 1) }
t("exception: ivars") { e = MyError.new; [e.detail, e.instance_variables, e.message] }

# ---------------------------------------------------------------- freeze

t("frozen plain: set") { Plain.new.freeze.instance_variable_set(:@a, 1) }
t("frozen plain: @x write") { Plain.new.freeze.set_a(1) }
t("frozen plain: @x add") { Plain.new.freeze.set_c(1) }
t("frozen plain: remove") { Plain.new.freeze.remove_instance_variable(:@a) }
t("frozen plain: read") { p = Plain.new.freeze; [p.get_a, p.instance_variable_get(:@b), p.instance_variables] }
t("frozen string: set") { "abc".dup.freeze.instance_variable_set(:@a, 1) }
t("frozen array: set") { [1].freeze.instance_variable_set(:@a, 1) }
t("frozen hash: remove") { h = {}; h.instance_variable_set(:@a, 1); h.freeze; h.remove_instance_variable(:@a) }
t("frozen module: set") { m = Module.new.freeze; m.instance_variable_set(:@a, 1) }
t("frozen: dup unfrozen, keeps ivars") { p = Plain.new.freeze; d = p.dup; [d.frozen?, d.instance_variables, d.set_c(3), d.get_c] }
t("frozen: clone frozen") { c = Plain.new.freeze.clone; [c.frozen?, c.instance_variables] }
t("frozen: clone(freeze: false)") { c = Plain.new.freeze.clone(freeze: false); [c.frozen?, c.set_a(9)] }
t("integer: set") { 1.instance_variable_set(:@a, 1) }
t("symbol: set") { :sym.instance_variable_set(:@a, 1) }
t("nil: set") { nil.instance_variable_set(:@a, 1) }
t("integer: get / ivars") { [1.instance_variable_get(:@a), 1.instance_variables] }

# ---------------------------------------------------------------- singleton classes

t("singleton: class << obj; @x") do
  o = Object.new
  class << o
    @in_singleton = :s
  end
  [o.instance_variables, o.singleton_class.instance_variables, o.singleton_class.instance_variable_get(:@in_singleton)]
end
t("singleton: object with singleton methods keeps ivars") do
  o = Plain.new
  def o.extra = @a + @b
  o.set_c(3)
  [o.extra, o.instance_variables, o.clone.extra, o.clone.instance_variables]
end

# ---------------------------------------------------------------- 1 and 100 ivars

t("one ivar") { o = Object.new; o.instance_variable_set(:@only, 1); [o.instance_variables, o.dup.instance_variables, Marshal.load(Marshal.dump(o)).instance_variables] }
t("100 ivars: add") do
  o = Object.new
  100.times { |i| o.instance_variable_set(:"@v#{i}", i) }
  [o.instance_variables.size, o.instance_variables.first(3), o.instance_variables.last(3), o.instance_variable_get(:@v73)]
end
t("100 ivars: remove evens, order kept") do
  o = Object.new
  100.times { |i| o.instance_variable_set(:"@v#{i}", i) }
  50.times { |i| o.remove_instance_variable(:"@v#{i * 2}") }
  [o.instance_variables.size, o.instance_variables.first(4), o.instance_variables.map { |v| o.instance_variable_get(v) }.sum]
end
t("100 ivars: re-add goes last") do
  o = Object.new
  100.times { |i| o.instance_variable_set(:"@v#{i}", i) }
  o.remove_instance_variable(:@v10)
  o.instance_variable_set(:@v10, :back)
  [o.instance_variables.last(2), o.instance_variable_get(:@v10)]
end
t("100 ivars: dup / clone / marshal") do
  o = Object.new
  100.times { |i| o.instance_variable_set(:"@v#{i}", i * i) }
  d = o.dup; c = o.clone; m = Marshal.load(Marshal.dump(o))
  [d, c, m].map { |x| [x.instance_variables == o.instance_variables, x.instance_variables.sum { |v| x.instance_variable_get(v) }] }
end
t("100 ivars on a string") do
  s = +"s"
  100.times { |i| s.instance_variable_set(:"@v#{i}", i) }
  s.remove_instance_variable(:@v0)
  [s.instance_variables.size, s.instance_variables.first(2), s.dup.instance_variables.size, Marshal.load(Marshal.dump(s)).instance_variables.last]
end
t("100 ivars on a class") do
  c = Class.new
  100.times { |i| c.instance_variable_set(:"@v#{i}", i) }
  [c.instance_variables.size, c.instance_variable_get(:@v99), c.remove_instance_variable(:@v50), c.instance_variables.size]
end

# ---------------------------------------------------------------- different orders, one class

class Ordered
  NAMES = %i[@p @q @r @s @t]
end
t("different orders on one class") do
  perms = Ordered::NAMES.permutation.to_a
  objs = perms.map do |perm|
    o = Ordered.new
    perm.each_with_index { |n, i| o.instance_variable_set(n, i) }
    o
  end
  ok = objs.each_with_index.all? do |o, k|
    o.instance_variables == perms[k] && perms[k].each_with_index.all? { |n, i| o.instance_variable_get(n) == i }
  end
  [objs.size, ok, objs[7].instance_variables, objs[7].dup.instance_variables, Marshal.load(Marshal.dump(objs[7])).instance_variables]
end
t("different orders: remove and re-add") do
  o = Ordered.new
  %i[@t @s @r].each { |n| o.instance_variable_set(n, n) }
  o.remove_instance_variable(:@t)
  o.instance_variable_set(:@t, :again)
  o.instance_variable_set(:@p, :p)
  [o.instance_variables, o.instance_variables.map { |n| o.instance_variable_get(n) }]
end

# Many different names after a common prefix: past the shape tree's per-node limit the
# objects fall back to a dictionary. Nothing about that may be visible.
t("too many variations after one prefix") do
  objs = 400.times.map do |i|
    o = Ordered.new
    o.instance_variable_set(:@prefix, i)
    o.instance_variable_set(:"@var#{i}", i * 2)
    o.instance_variable_set(:@after, -i)
    o
  end
  last = objs.last
  last.remove_instance_variable(:@prefix)
  last.instance_variable_set(:@prefix, :back)
  d = last.dup
  m = Marshal.load(Marshal.dump(last))
  [objs.all? { |o| o.instance_variables.size == 3 && o.instance_variable_get(:@after) <= 0 },
   objs[300].instance_variables, last.instance_variables, d.instance_variables, m.instance_variables,
   last.instance_variable_get(:@var399), (last.freeze.instance_variable_set(:@x, 1) rescue $!.class)]
end
t("dynamic names on one object, removed again") do
  o = Object.new
  1000.times { |i| o.instance_variable_set(:"@k#{i}", i) }
  990.times { |i| o.remove_instance_variable(:"@k#{i}") }
  [o.instance_variables, o.instance_variables.map { |v| o.instance_variable_get(v) }.sum]
end

# ---------------------------------------------------------------- Marshal

t("marshal: plain") { m = Marshal.load(Marshal.dump(Plain.new(3, 4))); [m.class, m.instance_variables, m.get_a, m.get_b] }
t("marshal: plain after remove/re-add") do
  p = Plain.new; p.remove_instance_variable(:@a); p.set_a(:z)
  m = Marshal.load(Marshal.dump(p)); [Marshal.dump(p) == Marshal.dump(m), m.instance_variables]
end
t("marshal: string with ivars") { s = +"abc"; s.instance_variable_set(:@x, 1); m = Marshal.load(Marshal.dump(s)); [m, m.instance_variables, m.instance_variable_get(:@x)] }
t("marshal: array with ivars") { a = [1]; a.instance_variable_set(:@x, [2]); m = Marshal.load(Marshal.dump(a)); [m, m.instance_variables, m.instance_variable_get(:@x)] }
t("marshal: hash with ivars") { h = { a: 1 }; h.instance_variable_set(:@x, :y); m = Marshal.load(Marshal.dump(h)); [m, m.instance_variables] }
t("marshal: dump bytes plain") { Marshal.dump(Plain.new(1, 2)) }
t("marshal: dump bytes string ivar") { s = +"a"; s.instance_variable_set(:@q, 1); Marshal.dump(s) }
t("marshal: struct with ivar") { s = Pair.new(1, 2); s.instance_variable_set(:@extra, 3); m = Marshal.load(Marshal.dump(s)); [m.to_a, m.instance_variables] }
t("marshal: exception") { m = Marshal.load(Marshal.dump(MyError.new("hi"))); [m.message, m.detail] }

# ---------------------------------------------------------------- inspect, ObjectSpace

t("inspect shows ivars in order") { p = Plain.new; p.set_c(3); p.remove_instance_variable(:@a); p.set_a(1); p.inspect }
t("object_id stable across ivar changes") do
  o = Plain.new; id = o.object_id
  o.set_c(1); 20.times { |i| o.instance_variable_set(:"@z#{i}", i) }; o.remove_instance_variable(:@a)
  [o.object_id == id, o.dup.object_id != id, o.clone.object_id != id]
end
t("each_object sees them") { ObjectSpace.each_object(Class).include?(Plain) }

# Differential checks for block scope reuse.
#
# A block whose body needs its RubyScope but defines no closure reuses the scope (and its locals
# tuple) of the previous call through the same BlockParam, unless something captured the scope:
# a Binding, a Proc or method defined in it, a nested scope (eval, class body), a TracePoint
# event. Every block body below makes calls, so it takes that path; the output must match CRuby:
#
#   ruby   Util/block-scope-reuse-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/block-scope-reuse-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
#
# (also with -X:NoJIT -X:NoOSR, which keeps the blocks in the interpreter)

# Differential checks for block scope reuse (every block body here makes calls)
# Differential checks for block scope reuse (every block body here makes calls)
bs = []
[1, 2, 3].each { |x| y = x * 2; bs << binding }
p bs.map { |b| b.local_variable_get(:y) }
p bs.map { |b| b.local_variable_get(:x) }

r = []
[1, 2, 3].each { |x| r << eval("defined?(zz)").inspect; r << eval("zz = x * 10; zz") }
p r

r = []
[1, 2].each { |x| r << local_variables.inspect; w = x.succ; r << local_variables.inspect }
p r

r = []
[1, 2, 3].each { |x| y ||= x.succ; r << y }
p r

procs = []
[1, 2, 3].each { |x| y = x * 3; procs << -> { y } }
p procs.map(&:call)

ps = []
[1, 2, 3].each { |x| y = x + 100; ps << eval("proc { y }") }
p ps.map(&:call)

ps = []
[1, 2, 3].each { |x| y = x.to_s; ps << binding.local_variable_get(:y) }
p ps

class DefIn
  [1, 2].each { |x| define_method("m#{x}") { x } }
  [3, 4].each { |x| y = x * 2; class_eval "def k#{x}; #{y}; end" }
end
p [DefIn.new.m1, DefIn.new.m2, DefIn.new.k3, DefIn.new.k4]

$b = []
tp = TracePoint.new(:line) { |t| $b << t.binding if t.path == __FILE__ }
tp.enable
[5, 6].each { |x|
  q = x * 7
  q.to_s
}
tp.disable
p $b.map { |b| b.local_variable_defined?(:q) ? b.local_variable_get(:q) : :undef }

r = []
%w[a1 b2 c3].each { |s| s =~ /\d/; r << $~[0] }
p r, $~ && $~[0]

def m_given; r = []; [1, 2].each { |x| r << [block_given?, __method__, x.succ] }; r; end
p m_given, m_given {}

r = []
[1, 2, 3, 4].each { |x| next if x.even?; r << x * 2 }
p r
p [1, 2, 3, 4].each { |x| break x * 100 if x == 3; x.succ }

r = []
cnt = 0
[1, 2].each { |x| cnt += 1; r << x * 2; redo if cnt == 1 }
p r

r = []
[1, 2, 3].each { |x|
  begin
    raise ArgumentError, "x#{x}" if x.odd?
    r << x.succ
  rescue => e
    r << e.message
  end
}
p r

p Thread.new { [1, 2, 3].map { |x| x * 2 } }.value

def tw; yield 1; yield 2; yield 3; end
r = []
tw { |x| r << x * 2 }
p r

def rec(n, &b); return if n == 0; yield n; rec(n - 1, &b); end
r = []
rec(4) { |x| r << x * 3 }
p r

fib = Hash.new { |h, n| h[n] = n < 2 ? n : h[n - 1] + h[n - 2] }
p fib[40]

e = [1, 2, 3].each
r = []
loop { v = e.next; r << v * 2 }
p r
p [1, 2, 3].map.with_index { |x, i| x * i }
p [1, 2, 3].each_slice(2).map { |a| a.sum }

o = Object.new
r = []
[1, 2].each { |x| o.instance_exec(x) { |y| @v = y * 2 }; r << o.instance_variable_get(:@v) }
p r

class Dm
  define_method(:dm) { |x| x * 2 + 1 }
end
p (1..3).map { |i| Dm.new.dm(i) }

p [3, 1, 2].sort { |a, b| b <=> a }
p [1, 2, 3].inject { |s, x| s + x * 2 }
p catch(:t) { [1, 2, 3].each { |x| throw :t, x * 9 if x == 2 } }

r = []
[1, 2].each { |x| r << self.to_s; r << x.frozen? }
p r

r = []
[[1, 2], [3, 4]].each { |a, b| c = a * b; r << c }
p r

r = []
[1, 2].each { |x| [10, 20].each { |y| r << x * y } }
p r

# a binding taken in iteration 1, iteration 2 must not overwrite it
bb = nil
[1, 2].each { |x| z = x * 5; bb ||= binding }
p bb.local_variable_get(:z)

# lambda with calls
l = ->(x) { x * 2 }
p (1..3).map { |i| l.(i) }
p (1..3).map(&l)

# Symbol#to_proc inside a block
p [[1, 2], [3]].map { |a| a.map(&:to_s) }

# eval with binding from the block, then reuse
bnd = []
[1, 2].each { |x| bnd << binding if x == 1; y = x.succ }
p bnd[0].local_variable_get(:y)

# raise inside, caller info
r = []
[1, 2].each { |x| r << caller(0).first[/in .*/] }
p r.uniq.size

# instance_variable / global in body
$g = 0
[1, 2, 3].each { |x| $g += x * 2; @iv = $g }
p $g, @iv

# Proc#call reuse
pr = proc { |x| x * 2 + (x > 1 ? 1 : 0) }
p (1..4).map { |i| pr.call(i) }

r = []
[1, 2].each { |x| q = x * 4; r << send(:binding).local_variable_get(:q) }
p r

def each2; i = 0; while i < 3; yield i; i += 1; end; end
r = []
each2 { |x| r << x * 2 }
p r
def walk(n, &b); i = 0; while i < 2; yield n; walk(n - 1, &b) if n > 0; i += 1; end; end
r = []
walk(2) { |x| r << x * 10 }
p r
# block via yield in loop capturing binding on some iterations
bs = []
each2 { |x| y = x * 3; bs << binding if x != 1 }
p bs.map { |b| b.local_variable_get(:y) }
# external enumeration over a yielding method
e = to_enum(:each2)
p [e.next * 2, e.next * 2, e.next * 2]
gen = Enumerator.new { |y| [1, 2, 3].each { |x| y << x * 5 } }
p gen.next, gen.next, gen.to_a
# lazy
p (1..Float::INFINITY).lazy.map { |x| x * 2 }.select { |x| x % 3 == 0 }.first(3)
# zip/each_with_index etc.
p [1, 2, 3].each_with_index.map { |x, i| x * i }
p [1, 2, 3].each_with_object([]) { |x, a| a << x * 2 }
# fibers
f = Fiber.new { [1, 2, 3].each { |x| Fiber.yield x * 7 }; :done }
p [f.resume, f.resume, f.resume, f.resume]
# threads using same proc
pr = proc { |x| x * 2 }
p 4.times.map { |i| Thread.new { (1..1000).map(&pr).sum } }.map(&:value)
# block params with defaults/splat/kw
p [[1, 2], [3, 4]].map { |a, b = 5, *c| [a * 1, b, c] }
p [1, 2].map { |x, k: 3| x * k }
# numbered/it params
p [1, 2, 3].map { _1 * 2 }, [1, 2, 3].map { it * 3 }
# block-local vars
y = 10
p [1, 2].map { |x; y| y = x * 2; y }, y
# $_ in block
r = []
["a\n", "b\n"].each { |l| $_ = l; r << $_.chomp.upcase }
p r
# self switching
class K; def run; [1, 2].map { |x| self.class.name + x.to_s }; end; end
p K.new.run
p 3.times.map { |i| instance_exec(i) { |j| j * 2 } }
# __method__ / block_given? in define_method
class K2; define_method(:z) { [1, 2].map { |x| [__method__, x * 2] } }; end
p K2.new.z
# eval of local after previous iteration's eval
r = []
[1, 2].each { |x| r << (eval("defined?(qq)") || :none); eval("qq = 1"); }
p r
# ObjectSpace / GC
p [1, 2].map { |x| GC.start if x == 1; x * 2 }
# exception backtrace in reused scope
def raiser(x) = raise("e#{x}")
p [1, 2].map { |x| begin; raiser(x); rescue => e; e.backtrace[1][/block.*/]; end }
# local_variable_set via binding then next iteration
r = []
[1, 2].each { |x| b = binding; b.local_variable_set(:nn, x * 2); r << b.local_variable_get(:nn) }
p r
# proc from method(:...) in block
p [1, 2].map { |x| method(:Integer).call(x.to_s) * 2 }
# retry inside block
cnt = 0
p [1, 2].map { |x| begin; cnt += 1; raise "x" if cnt == 1; x * 2; rescue; retry; end }

# locals of a reused scope start out nil again (in a method: its own locals)
def locals_part
  r = []
  [1, 2, 3].each { |x| z = (z || 0) + x; r << z }
  p r
  r = []
  [1, 2, 3].each { |x| if x.odd?; w = x * 2; end; r << w }
  p r
  r = []
  [1, 2, 3].each { |x| a = (a || []) << x.succ; b ||= x; r << [a, b] }
  p r
  r = []
  [1, 2, 3].each { |x| a1 = x; a2 = a1 + 1; a3 = a2 + 1; a4 = a3 + 1; a5 = a4 + 1; a6 = a5 + 1; a7 = a6 + 1; a8 = a7 + 1; a9 = (a9 || 0) + a8; r << a9 }
  p r
  r = []
  [[1, 2], [3, 4]].each { |a, (b, c)| d = (d || 10) + a.to_i; r << [a, b, c, d] }
  p r
  r = []
  [1, 2].each { |x, y = ((y || 5) + 1)| r << [x, y.inspect] }
  p r
  r = []
  [1, 2, 3].each_with_index { |x, i| t = (t.to_i) + x * i; r << t }
  p r
  r = []
  [1, 2].each { |x| i = 0; while i < 2; k = (k || 0) + 1; i += 1; end; r << k }
  p r
  cnt = 0
  r = []
  [1, 2].each { |x| q = (q || 0) + 1; cnt += 1; r << q; redo if cnt == 1 }
  p r
  r = []
  h = {a: 1, b: 2}
  h.each { |k, v| s = (s || "") + k.to_s * v; r << s }
  p r
  r = []
  3.times { |i| case i when 0 then m = :zero when 2 then r << m.inspect end; r << m.inspect }
  p r
  r = []
  [1, 2].each { |x| begin; e = (e || 0) + x; raise "x" if x == 1; rescue; f = (f || 0) + 1; end; r << [e, f] }
  p r
end
locals_part

# one proc called through many BlockParams (each call makes its own scope)
class ReuseD
  define_method(:dbl) { |x| y = x * 2; y + (z ||= 0) }
  define_method(:bnd) { |x| y = x * 3; binding }
end
class ReuseE
  include Enumerable
  def initialize(a); @a = a; end
  def each; @a.each { |x| yield x }; self; end
end

def proc_part
  # one proc, called through many BlockParams
  pr = proc { |x| y = x * 2; Thread.pass if x % 97 == 0; y + 1 }
  p 4.times.map { |t| Thread.new { s = 0; 20000.times { |i| s += pr.call(i + t) }; s } }.map(&:value)

  fact = proc { |n| n <= 1 ? 1 : n * fact.call(n - 1) }
  p fact.call(25)

  d = ReuseD.new
  p (1..5).map { |i| d.dbl(i) }
  bs = (1..3).map { |i| d.bnd(i) }
  p bs.map { |b| b.local_variable_get(:y) }

  l = ->(x) { t = x.to_s; t * 2 }
  p (1..3).map { |i| l.(i) }, (1..3).map(&l)

  pr = proc { |k| to_s + k.to_s }
  p [1, 2, 3].map { |i| i.instance_exec(i * 10, &pr) }

  $bs = []
  pr2 = proc { |n| v = n * 7; $bs << binding if n.odd?; pr2.call(n - 1) if n > 0; v }
  p pr2.call(4), $bs.map { |b| b.local_variable_get(:v) }

  pr3 = proc { |x| a = x; Fiber.yield a if x == 1; a * 10 }
  f = Fiber.new { pr3.call(1) }
  p f.resume
  p pr3.call(2), pr3.call(3)
  p f.resume

  e = ReuseE.new([3, 1, 2])
  p e.map { |x| x * 2 }, e.select { |x| x.odd? }, e.sort_by { |x| -x }, e.inject { |s, x| s + x }
  r = []
  e.each { |x| w = (w || 0) + x; r << w }
  p r
  p e.each_slice(2).map { |a| a.sum }

  # a proc whose call raises: its scope is not kept, the next call works
  pr4 = proc { |x| q = x.to_s; raise "boom" if x == 2; q * 2 }
  p [1, 2, 3].map { |i| begin; pr4.call(i); rescue => ex; ex.message; end }

  # break / next out of a proc-called block
  p [1, 2, 3, 4].each { |x| y = x * 2; break y if x == 3 }
  pr5 = proc { |x| next x * 100 if x.even?; x.to_s }
  p (1..4).map { |i| pr5.call(i) }

  # same proc used as block of different methods
  blk = proc { |x| x.to_s * 2 }
  p [1, 2].map(&blk), { a: 1 }.map(&blk), (1..2).each_with_object([]) { |x, acc| acc << blk.call(x) }

  # lambda with nested return
  lr = ->(x) { [1, 2].each { |y| return y * x if y == 2 }; :none }
  p lr.(5), lr.(6)

  # a proc that captures its scope only on some calls
  $keep = []
  pk = proc { |x| zz = x * 9; $keep << binding if x == 2; zz }
  p (1..4).map { |i| pk.call(i) }, $keep.map { |b| b.local_variable_get(:zz) }
end
proc_part

# procs over a block's scope made without a block literal in it: eval, Method#to_proc,
# Symbol#to_proc, instance_eval/class_eval strings, a define_method'd method's binding
def capture_part
  r = []
  [1, 2, 3].each { |x| y = x * 4; r << eval("proc { [x, y] }") }
  p r.map(&:call)
  r = []
  [1, 2, 3].each { |x| y = x * 5; r << [method(:p).to_proc, y] }
  p r.map { |m, y| [m.lambda?, y] }
  r = []
  [1, 2, 3].each { |x| y = x.to_s; r << [y.method(:upcase).to_proc, :succ.to_proc, y] }
  p r.map { |up, su, y| [up.call, su.call(y), y] }
  r = []
  %w[a b c].each { |s| t = s * 2; r << s.instance_eval("proc { [self, t] }") }
  p r.map(&:call)
  r = []
  [1, 2, 3].each { |x| w = x * 6; r << Object.class_eval("lambda { w + x }") }
  p r.map(&:call)
  r = []
  [1, 2, 3].each { |x| v = x * 7; r << eval("binding") }
  p r.map { |b| [b.local_variable_get(:x), b.local_variable_get(:v)] }
end
capture_part

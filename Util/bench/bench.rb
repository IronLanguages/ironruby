# Micro and medium benchmarks for comparing IronRuby with CRuby.
#
#   ruby Util/bench/bench.rb <name> [scale]
#   ./ir.sh Util/bench/bench.rb --list
#
# Prints the elapsed seconds of the timed section only, so interpreter start-up stays out
# of the number. The scale factor multiplies the iteration counts. Util/bench/run.sh runs
# the whole set against both interpreters and reports ratios.
#
# Note that the benchmarks below drive their loops from a block at the top level, which the
# DLR interpreter never compiles (it only compiles a lambda after N *invocations*, and there
# is no on-stack replacement). That is deliberate: it is the shape a Ruby script's main loop
# has. Wrap a loop in a method and call it 32+ times to measure compiled code instead.

SCALE = (ARGV[1] || "1").to_f

def n(base) = (base * SCALE).to_i

BENCH = {}
def bench(name, &b) = BENCH[name] = b

# ---------------- call paths ----------------

class CallTarget
  def empty; end
  def a1(x); x; end
  def a2(x, y); x; end
  def a3(x, y, z); x; end
  def kw(a: 1, b: 2); a; end
  def yielder; yield; end
  def yielder3; yield 1, 2, 3; end
  def takes_blk(&b); b; end
  def calls_blk(&b); b.call; end
  attr_accessor :prop
  def ivar_get; @iv; end
  def ivar_set(v); @iv = v; end
  def ivar_incr; @iv += 1; end
end

bench("call_empty") do
  o = CallTarget.new; i = 0; m = n(2_000_000)
  while i < m; o.empty; i += 1; end
end

bench("call_1arg") do
  o = CallTarget.new; i = 0; m = n(2_000_000)
  while i < m; o.a1(i); i += 1; end
end

bench("call_2arg") do
  o = CallTarget.new; i = 0; m = n(2_000_000)
  while i < m; o.a2(i, i); i += 1; end
end

bench("call_3arg") do
  o = CallTarget.new; i = 0; m = n(2_000_000)
  while i < m; o.a3(i, i, i); i += 1; end
end

bench("call_kwargs") do
  o = CallTarget.new; i = 0; m = n(1_000_000)
  while i < m; o.kw(a: i, b: 2); i += 1; end
end

bench("call_self") do
  i = 0; m = n(2_000_000)
  def self.selfm; end
  while i < m; selfm; i += 1; end
end

bench("yield_0") do
  o = CallTarget.new; i = 0; m = n(2_000_000)
  while i < m; o.yielder { }; i += 1; end
end

bench("yield_3") do
  o = CallTarget.new; i = 0; m = n(1_000_000)
  while i < m; o.yielder3 { |a, b, c| a }; i += 1; end
end

bench("block_pass") do
  o = CallTarget.new; blk = proc { }; i = 0; m = n(1_000_000)
  while i < m; o.takes_blk(&blk); i += 1; end
end

bench("proc_call") do
  p1 = proc { |x| x }; i = 0; m = n(2_000_000)
  while i < m; p1.call(i); i += 1; end
end

bench("lambda_call") do
  p1 = lambda { |x| x }; i = 0; m = n(2_000_000)
  while i < m; p1.call(i); i += 1; end
end

bench("blk_call_in_method") do
  o = CallTarget.new; blk = proc { }; i = 0; m = n(1_000_000)
  while i < m; o.calls_blk(&blk); i += 1; end
end

bench("attr_get") do
  o = CallTarget.new; o.prop = 1; i = 0; m = n(2_000_000); s = 0
  while i < m; s = o.prop; i += 1; end
end

bench("attr_set") do
  o = CallTarget.new; i = 0; m = n(2_000_000)
  while i < m; o.prop = i; i += 1; end
end

bench("ivar_get") do
  o = CallTarget.new; o.ivar_set(1); i = 0; m = n(2_000_000)
  while i < m; o.ivar_get; i += 1; end
end

bench("ivar_set") do
  o = CallTarget.new; i = 0; m = n(2_000_000)
  while i < m; o.ivar_set(i); i += 1; end
end

CONST_X = 42
module ConstHolder; DEEP = 7; end

bench("const_lookup") do
  i = 0; m = n(2_000_000); s = 0
  while i < m; s = CONST_X; i += 1; end
end

bench("const_scoped") do
  i = 0; m = n(2_000_000); s = 0
  while i < m; s = ConstHolder::DEEP; i += 1; end
end

$gvar = 5
bench("gvar_get") do
  i = 0; m = n(2_000_000); s = 0
  while i < m; s = $gvar; i += 1; end
end

bench("gvar_set") do
  i = 0; m = n(2_000_000)
  while i < m; $gvar = i; i += 1; end
end

# ---------------- arithmetic / control flow ----------------

bench("int_arith") do
  i = 0; s = 0; m = n(3_000_000)
  while i < m; s = s + i * 2 - 1; i += 1; end
end

bench("float_arith") do
  i = 0; s = 0.0; m = n(3_000_000)
  while i < m; s = s + i * 1.5 - 0.25; i += 1; end
end

bench("cmp_branch") do
  i = 0; s = 0; m = n(3_000_000)
  while i < m
    if i < 100 then s += 1 elsif i > 1000 then s += 2 else s += 3 end
    i += 1
  end
end

bench("while_loop") do
  i = 0; m = n(3_000_000)
  while i < m; i += 1; end
end

bench("times_loop") do
  s = 0
  n(3_000_000).times { |i| s += 1 }
end

bench("each_loop") do
  a = (0...1000).to_a; s = 0; r = n(3_000)
  r.times { a.each { |x| s += x } }
end

bench("upto_loop") do
  s = 0
  1.upto(n(3_000_000)) { |i| s += 1 }
end

bench("case_when") do
  i = 0; s = 0; m = n(2_000_000)
  while i < m
    case i % 5
    when 0 then s += 1
    when 1 then s += 2
    when 2 then s += 3
    when 3 then s += 4
    else s += 5
    end
    i += 1
  end
end

bench("case_when_class") do
  vals = [1, "x", 2.0, :s, nil]; i = 0; s = 0; m = n(500_000)
  while i < m
    case vals[i % 5]
    when Integer then s += 1
    when String then s += 2
    when Float then s += 3
    when Symbol then s += 4
    else s += 5
    end
    i += 1
  end
end

bench("str_interp") do
  i = 0; m = n(500_000); s = nil
  while i < m; s = "a#{i}b#{i + 1}c"; i += 1; end
end

bench("str_concat") do
  i = 0; m = n(500_000)
  while i < m; s = "a" + i.to_s + "b"; i += 1; end
end

bench("array_index") do
  a = (0...100).to_a; i = 0; m = n(2_000_000); s = 0
  while i < m; s = a[i % 100]; i += 1; end
end

bench("hash_lookup") do
  h = {}; 100.times { |k| h[k] = k }
  i = 0; m = n(2_000_000); s = 0
  while i < m; s = h[i % 100]; i += 1; end
end

# ---------------- exceptions / dynamic dispatch ----------------

class MyErr < StandardError; end

bench("raise_rescue") do
  i = 0; m = n(100_000); s = 0
  while i < m
    begin
      raise MyErr, "x"
    rescue MyErr
      s += 1
    end
    i += 1
  end
end

bench("rescue_nothrow") do
  i = 0; m = n(2_000_000); s = 0
  while i < m
    begin
      s += 1
    rescue MyErr
      s += 2
    end
    i += 1
  end
end

class MM
  def method_missing(nm, *a); a.size; end
  def respond_to_missing?(nm, p = false) = true
end

bench("method_missing") do
  o = MM.new; i = 0; m = n(500_000)
  while i < m; o.nosuch(1); i += 1; end
end

class SBase; def go(x); x; end; end
class SMid < SBase; def go(x); super; end; end
class SLeaf < SMid; def go(x); super; end; end

bench("super_call") do
  o = SLeaf.new; i = 0; m = n(1_000_000)
  while i < m; o.go(i); i += 1; end
end

module M1; def deep; 1; end; end
module M2; include M1; end
module M3; include M2; end
module M4; include M3; end
module M5; include M4; end
class DeepBase; include M5; end
class D1 < DeepBase; end
class D2 < D1; end
class D3 < D2; end
class D4 < D3; end
class D5 < D4; end

bench("deep_hierarchy") do
  o = D5.new; i = 0; m = n(2_000_000)
  while i < m; o.deep; i += 1; end
end

bench("singleton_call") do
  o = Object.new
  def o.sing; 1; end
  i = 0; m = n(2_000_000)
  while i < m; o.sing; i += 1; end
end

bench("module_function_call") do
  i = 0; m = n(2_000_000)
  while i < m; Math.sqrt(4.0); i += 1; end
end

bench("send_call") do
  o = CallTarget.new; i = 0; m = n(1_000_000)
  while i < m; o.send(:a1, i); i += 1; end
end

bench("respond_to") do
  o = CallTarget.new; i = 0; m = n(1_000_000)
  while i < m; o.respond_to?(:a1); i += 1; end
end

bench("fiber_switch") do
  m = n(200_000)
  f = Fiber.new { loop { Fiber.yield 1 } }
  i = 0
  while i < m; f.resume; i += 1; end
end

# Enumerator#next over an Array, over a generator block, and Fiber creation.
bench("enum_next_array") do
  a = Array.new(1_000) { |k| k }
  e = a.each
  i = 0; m = n(100_000)
  while i < m
    e.next
    i += 1
    e.rewind if i % 1_000 == 0
  end
end

bench("enum_next_gen") do
  e = Enumerator.new { |y| k = 0; loop { y << k; k += 1 } }
  i = 0; m = n(50_000)
  while i < m; e.next; i += 1; end
end

bench("fiber_new") do
  i = 0; m = n(5_000)
  while i < m; Fiber.new { i }.resume; i += 1; end
end

bench("proc_new_call") do
  i = 0; m = n(500_000)
  while i < m; (proc { |x| x }).call(i); i += 1; end
end

# ---------------- medium ----------------

bench("fib") do
  def fib(k) = k < 2 ? k : fib(k - 1) + fib(k - 2)
  fib(29 + (SCALE > 1 ? 2 : 0))
end

bench("ackermann") do
  def ack(mm, k)
    if mm == 0 then k + 1
    elsif k == 0 then ack(mm - 1, 1)
    else ack(mm - 1, ack(mm, k - 1))
    end
  end
  ack(3, 7)
end

bench("mandelbrot") do
  size = 200
  sum = 0
  (0...size).each do |y|
    ci = 2.0 * y / size - 1.0
    (0...size).each do |x|
      cr = 2.0 * x / size - 1.5
      zr = 0.0; zi = 0.0; k = 0
      while k < 50 && zr * zr + zi * zi <= 4.0
        t = zr * zr - zi * zi + cr
        zi = 2.0 * zr * zi + ci
        zr = t
        k += 1
      end
      sum += k
    end
  end
end

bench("nbody") do
  bodies = Array.new(5) { |k| [k * 1.0, k * 2.0, k * 3.0, 0.0, 0.0, 0.0, 1.0 / (k + 1)] }
  s = 0.0
  20_000.times do
    bodies.each do |b1|
      bodies.each do |b2|
        dx = b1[0] - b2[0]; dy = b1[1] - b2[1]; dz = b1[2] - b2[2]
        d2 = dx * dx + dy * dy + dz * dz
        s += d2
      end
    end
  end
end

bench("sort_strings") do
  a = Array.new(20_000) { |k| "k#{(k * 7919) % 20000}" }
  30.times { a.sort }
end

bench("string_build") do
  2_000.times do
    s = +""
    200.times { |k| s << "x#{k}" }
  end
end

bench("map_loop") do
  a = (0...1000).to_a; r = n(2_000)
  r.times { a.map { |x| x + 1 } }
end

bench("select_loop") do
  a = (0...1000).to_a; r = n(2_000)
  r.times { a.select { |x| x > 500 } }
end

bench("any_loop") do
  a = (0...1000).to_a; r = n(2_000)
  r.times { a.any? { |x| x > 5000 } }
end

class CallTarget
  def yield_upto(k)
    i = 0
    while i < k; yield i; i += 1; end
  end
end

bench("yield_loop") do
  o = CallTarget.new; s = 0; r = n(2_000)
  r.times { o.yield_upto(1000) { |x| s += x } }
end

# A tokenizer's inner loop: StringScanner scan/skip with Regexps and Strings, group access.
bench("strscan_tokens") do
  require "strscan"
  src = (%q({"name": "Team \\"A\\"", "score": [1, 22.5, -3e2], "ok": true, "x": null}, ) * 200).b
  r = n(60)
  r.times do
    s = StringScanner.new(src)
    until s.eos?
      if s.skip(/\s+/) then next
      elsif s.scan(/"((?:[^"\\]|\\.)*)"/n) then s[1]
      elsif s.scan(/-?\d+(\.\d+)?([eE][-+]?\d+)?/) then (s[1] || s[2]) ? Float(s[0]) : Integer(s[0])
      elsif s.skip("true") || s.skip("null") then nil
      else s.getch
      end
    end
  end
end

# a literal block handed to a library method that takes a BlockParam (ruby-bench send_cfunc_block)
bench("block_literal_cfunc") do
  a = []; i = 0; m = n(2_000_000)
  while i < m; a.reverse_each { }; i += 1; end
end

class NewTarget
  def initialize(left = nil, right = nil)
    @left = left; @right = right; @i = 0; @j = 0
  end
end

bench("object_new_init") do
  i = 0; m = n(2_000_000); x = nil
  while i < m; x = NewTarget.new(x, nil); x = nil if i % 64 == 0; i += 1; end
end

# ---------------- keyword parameters ----------------

class KwPoint
  def initialize(x:, y: 0)
    @x = x
    @y = y
  end
end

bench("new_kwargs") do
  i = 0; m = n(1_000_000)
  while i < m; KwPoint.new(x: i, y: 2); i += 1; end
end

# ---------------- single-call blocks whose body makes a call ----------------

class YieldOnce
  def one(x); yield x; end
end

# a Ruby method yielding once to a new literal block (block scope reuse must cost it nothing)
bench("yield_once_call") do
  o = YieldOnce.new; i = 0; m = n(2_000_000)
  while i < m; o.one(i) { |y| y * 2 }; i += 1; end
end

# a library method calling a new literal block once
bench("then_call") do
  i = 0; m = n(2_000_000)
  while i < m; i.then { |y| y * 2 }; i += 1; end
end

# ---------------------------------------------------------------- optional parameters

class OptTarget
  def opt(x, y = x); y; end
  def opt_sym(x, mode = :fast); mode; end
  def kw_expr(a:, b: a); b; end
  def kw_omit(a, k: nil); a.itself; end
end

# a call leaving out an optional parameter whose default is an expression
bench("call_opt_default") do
  o = OptTarget.new; i = 0; m = n(2_000_000)
  while i < m; o.opt(i); i += 1; end
end

# the same with a Symbol default
bench("call_opt_sym") do
  o = OptTarget.new; i = 0; m = n(2_000_000)
  while i < m; o.opt_sym(i); i += 1; end
end

# a keyword call leaving out a keyword whose default is an expression
bench("call_kw_default") do
  o = OptTarget.new; i = 0; m = n(1_000_000)
  while i < m; o.kw_expr(a: i); i += 1; end
end

# a call that passes none of the keywords of a method the JIT does not specialize
bench("call_kw_omitted") do
  o = OptTarget.new; i = 0; m = n(1_000_000)
  while i < m; o.kw_omit(i); i += 1; end
end

name = ARGV[0]
unless BENCH.key?(name)
  if name == "--list"
    puts BENCH.keys
    exit
  end
  warn "unknown bench: #{name}"
  exit 1
end
b = BENCH[name]
t = Time.now
b.call
printf("%.6f\n", Time.now - t)

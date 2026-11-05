# Differential matrix for the loops of the core iterators that -X:OSR outlines together with their
# block (Src/Ruby/Runtime/Osr/BlockLoopSite.cs): Integer#times, #upto, #downto (Fixnum bounds) and
# Array#each, #map, #select, #any?, #all?, #none?. Run it under CRuby and under ./ir.sh with OSR on and off, and diff:
#
#   ruby Util/block-loop-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/block-loop-matrix.rb > /tmp/osr.txt
#   ./ir.sh -X:NoOSR Util/block-loop-matrix.rb > /tmp/noosr.txt
#   diff /tmp/mri.txt /tmp/osr.txt && diff /tmp/mri.txt /tmp/noosr.txt
#
# Every loop runs well past the threshold (2000 yields) before it reaches the case it probes, so
# the typed copy is what meets it: an accumulator leaving Int32 and Int64, an element of another
# type, an Array changed under the loop, a deopt after a store, a jump, a local the block reads
# before it assigns it, a redefinition, a refinement, a TracePoint, a Thread#raise. To see which
# blocks are outlined:
#
#   IR_OSR_VERBOSE=1 ./ir.sh Util/block-loop-matrix.rb 2>&1 >/dev/null | grep -a 'osr\]'

def t(label)
  print label.ljust(60), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    puts e.class
  end
end

N = 6000

# ---------------------------------------------------------------- 1. the four iterators

t('times: s += i') { s = 0; N.times { |i| s += i }; s }
t('times: no parameter') { s = 0; N.times { s += 2 }; s }
t('times: value') { N.times { |i| i } }
t('upto: s += i') { s = 0; 1.upto(N) { |i| s += i }; s }
t('upto: value') { 3.upto(N) { |i| i } }
t('downto: s += i') { s = 0; N.downto(-5) { |i| s += i }; s }
t('downto: value') { N.downto(1) { } }
t('upto: to Int32 max - 1') { s = 0; (2**31 - 3000).upto(2**31 - 2) { |i| s += 1 }; s }
t('downto: to Int32 min') { s = 0; (-2**31 + 3000).downto(-2**31) { |i| s += 1 }; s }
t('each: s += x') { a = (0...N).to_a; s = 0; a.each { |x| s += x }; s }
t('each: value is the receiver') { a = (0...N).to_a; a.each { |x| x }.equal?(a) }
t('each: Floats') { a = Array.new(N) { |i| i * 0.25 }; s = 0.0; a.each { |x| s += x }; s }
t('times: Float accumulator') { f = 0.0; N.times { |i| f += i * 0.5 }; f }

# ---------------------------------------------------------------- 2. types moving under the copy

t('times: out of Int32') { s = 0; N.times { |i| s += 1_000_000 }; s }
t('times: out of Int64') { s = 2**62; N.times { |i| s += 2**50 }; s }
t('times: into Float') { s = 0; N.times { |i| s += (i == 5000 ? 0.5 : 1) }; s }
t('each: a Float element') { a = Array.new(N) { |i| i == 5000 ? 1.5 : i }; s = 0; a.each { |x| s += x }; s }
t('each: a nil element') { a = Array.new(N) { |i| i == 5000 ? nil : i }; s = 0; a.each { |x| s += x }; s }
t('each: after the TypeError') { a = Array.new(N) { |i| i == 5000 ? nil : i }; s = 0; (a.each { |x| s += x } rescue s) }
t('each: a String element') { a = Array.new(N) { |i| i == 5000 ? "x" : i }; s = 0; (a.each { |x| s += x } rescue [$!.class, s]) }
t('each: Array subclass') { c = Class.new(Array); a = c.new((0...N).to_a); s = 0; a.each { |x| s += x }; s }
t('each: frozen') { a = (0...N).to_a.freeze; s = 0; a.each { |x| s += x }; s }
t('each: elements that are Arrays') { s = 0; ([[1, 2]] * N).each { |x| s += x.size }; s }
t('times: ZeroDivisionError') { s = 0; (N.times { |i| s += 10 / (N - 1 - i) } rescue [$!.class, s]) }

def raise_in_block
  s = 0
  N.times { |i| s += 10 / (N - 1 - i) }
rescue ZeroDivisionError => e
  e.backtrace_locations[0, 2].map { |l| l.label }
end
t('times: backtrace of an error in the block') { raise_in_block }

# ---------------------------------------------------------------- 3. stores

t('each: a[i] = in the block') do
  a = (0...N).to_a; b = Array.new(N, 0)
  a.each { |x| b[x] = x * 2 }
  b.sum
end
t('each: store, then a deopt in the same iteration') do
  a = Array.new(N, 10); b = Array.new(N) { |i| i == 5000 ? 1.5 : 1 }; s = 0
  N.times { |i| a[i] = a[i] + 1; s += b[i] }
  [s, a.sum]
end
t('each: pop under the loop') { a = (0...N).to_a; s = 0; a.each { |x| s += x; a.pop if x == 5000 }; [s, a.size] }
t('each: element written under the loop') { a = (0...N).to_a; s = 0; a.each { |x| a[x + 1] = 0 if x == 5000 && x + 1 < a.size; s += x }; s }

# ---------------------------------------------------------------- 4. what a `while' does differently

t('times: break') { N.times { |i| break i * 2 if i == 5000 } }
t('times: next') { c = 0; N.times { |i| next if i.odd?; c += 1 }; c }
t('times: redo') { c = 0; r = false; N.times { |i| c += 1; (r = true; redo) if i == 5000 && !r }; c }
def ret_from_block; N.times { |i| return i if i == 5000 }; :no; end
t('times: return') { ret_from_block }
t('times: a while with break in the block') { s = 0; N.times { |i| j = 0; while true; break if j == 3; s += j; j += 1; end }; s }
t('times: block local assigned first') { s = 0; N.times { |i| x = i * 2; y = x + 1; s += y - x }; s }
t('times: block local maybe assigned') { r = []; N.times { |i| x = 5 if i == 0; r << x if i < 3 }; r }
t('times: block local assigned after a read') { r = []; N.times { |i| r << defined?(x).inspect if i < 2; x = 1 }; r }
t('times: |i; x|') { s = 0; N.times { |i; x| x = i; s += x }; s }
t('times: param shadows an outer local') { i = 42; s = 0; N.times { |i| s += i }; [i, s] }
t('times: last value') { last = nil; N.times { |i| last = i }; last }
t('times: |a, b|') { s = 0; N.times { |a, b| s += a; s += 1 if b.nil? }; s }
t('each: |a, b| over pairs') { s = 0; ([[1, 2]] * N).each { |a, b| s += a + b }; s }
t('each: |a,| over pairs') { s = 0; ([[1, 2]] * N).each { |a,| s += a }; s }
t('times: lambda') { s = 0; l = ->(i) { s += i }; N.times(&l); s }
t('times: a proc given to two iterators') { s = 0; pr = proc { |x| s += x }; 3.times { N.times(&pr) }; (0...N).to_a.each(&pr); s }

class Counter
  def initialize; @k = 3; end
  def run; s = 0; N.times { |i| s += @k }; s; end
end
t('times: instance variable of self') { Counter.new.run }

# ---------------------------------------------------------------- 5. redefinitions, refinements

class Integer
  alias_method :bl_times, :times
  def times; yield 99; self; end
end
t('times: redefined') { s = 0; N.times { |i| s += i }; s }
class Integer
  alias_method :times, :bl_times
end
t('times: restored') { s = 0; N.times { |i| s += i }; s }

module BlockLoopPlus
  refine Integer do
    def +(o) 7 end
  end
end
module BlockLoopRefined
  using BlockLoopPlus
  def self.go; s = 0; N.times { |i| s += 1 }; s; end
end
t('times: refined Integer#+') { BlockLoopRefined.go }
t('times: unrefined Integer#+') { s = 0; N.times { |i| s += 1 }; s }

def plus_loop; s = 0; N.times { |i| s += 1 }; s; end
t('times: before Integer#+ is redefined') { plus_loop }
class Integer
  alias_method :bl_plus, :+
  def +(o) bl_plus(o).bl_plus(1) end
end
t('times: Integer#+ redefined') { plus_loop }
class Integer
  alias_method :+, :bl_plus
end
t('times: Integer#+ restored') { plus_loop }

# ---------------------------------------------------------------- 7. map, select, any?, all?, none?

A = (0...N).to_a
t('map: x + 1') { r = A.map { |x| x + 1 }; [r.size, r.sum, r.first(2), r.last] }
t('map: a Boolean') { A.map { |x| x > 3000 }.count(true) }
t('map: a Float') { A.map { |x| x * 0.5 }.sum }
t('map: locals of its own') { A.map { |x| y = x * 2; y - 1 }.last(2) }
t('map: a Float element') { Array.new(N) { |i| i == 5000 ? 1.5 : i }.map { |x| x + 1 }[4999, 3] }
t('map: a nil element') { Array.new(N) { |i| i == 5000 ? nil : i }.map { |x| x + 1 } }
t('map: break') { A.map { |x| break 42 if x == 5000; x } }
t('map: next') { A.map { |x| next 0 if x.odd?; x }.sum }
t('map: no parameter') { c = 0; [A.map { c += 1 }.last, c] }
t('map: writes the receiver') { m = (0...N).to_a; [m.map { |x| m[x] = -1 if x == 4000; x }.sum, m.sum] }
t('map: an Array literal') { A.map { |x| [x] }.last }
t('map: frozen') { A.dup.freeze.map { |x| x }.size }
t('select: x > 500') { r = A.select { |x| x > 500 }; [r.size, r.sum, r.first(2)] }
t('select: truthy elements') { Array.new(N) { |i| i == 5000 ? nil : i }.select { |x| x }.size }
t('select: a String element') { Array.new(N) { |i| i == 5000 ? "s" : i }.select { |x| x > 10 } }
t('select: % 3') { r = A.select { |x| x % 3 == 0 }; [r.size, r.last] }
t('select: the element after a write') { m = (0...N).to_a; m.select { |x| m[x + 1] = -5 if x == 4000; x < 0 } }
t('select: no parameter') { s = 0; [A.select { s += 1; true }.size, s] }
t('any?') { [A.any? { |x| x > 5000 }, A.any? { |x| x > 50000 }] }
t('all?') { [A.all? { |x| x < 5000 }, A.all? { |x| x >= 0 }] }
t('none?') { [A.none? { |x| x == 4500 }, A.none? { |x| x < 0 }] }
t('any?: stops at the first') { c = 0; [A.any? { |x| c += 1; x == 4000 }, c] }
t('all?: a nil element') { Array.new(N) { |i| i == 5000 ? nil : i }.all? { |x| x } }
t('any?: a String element') { Array.new(N) { |i| i == 5000 ? "s" : i }.any? { |x| x > N } }
t('any?: break') { A.any? { |x| break :b if x == 4000; false } }
t('all?: next') { A.all? { |x| next true if x.odd?; x < 3000 } }

# ---------------------------------------------------------------- 6. TracePoint, threads

def traced(n); s = 0; n.times { |i|
  s += 1 }; s; end
traced(N)
t('times: TracePoint :line') do
  lines = 0
  tp = TracePoint.new(:line) { |tr| lines += 1 if tr.lineno == __LINE__ - 4 }
  tp.enable { traced(100) }
  lines
end
t('times: TracePoint :b_call') { calls = 0; tp = TracePoint.new(:b_call) { calls += 1 }; tp.enable { traced(100) }; calls }

t('times: Thread#raise') do
  th = Thread.new { s = 0; loop { 1_000_000.times { |i| s += 1 } } }
  th.report_on_exception = false
  sleep 0.1
  th.raise(RuntimeError, "stop")
  (th.join; :joined) rescue $!.message
end
t('times: Thread#kill') do
  th = Thread.new { s = 0; 100_000_000.times { |i| s += 1 }; s }
  sleep 0.1
  th.kill
  th.join.status
end
t('times: enumerator') { [5.times.to_a, 3.upto(6).to_a, 6.downto(3).to_a] }

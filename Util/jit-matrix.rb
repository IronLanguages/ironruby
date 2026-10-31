# Differential matrix for the method JIT (-X:JIT, Src/Ruby/Runtime/Jit/).
# Run under CRuby and under ./ir.sh - with the JIT on and off - and diff:
#
#   ruby Util/jit-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT Util/jit-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# All three must be identical. The JIT is only allowed to be faster, never different.
#
# Every method below is called well past the JIT's hotness threshold (40 calls) with
# the argument types it is meant to be specialized for, and THEN probed - with values
# that cross the edges the specialized code has guards for: Int32 -> Int64 growth,
# Int64 -> Bignum overflow (a deopt), a zero divisor (a deopt), an argument of a type
# it was not specialized for, the method redefined, a subclass overriding a method it
# calls on self, a singleton method, a refinement, and Integer#+ itself redefined.
#
# Which of these actually get specialized is not observable from Ruby, so it is not
# part of the output. To see it:
#
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]'
#
# The methods named sp_* are shapes the JIT is expected to SPECIALIZE; those named
# no_* are shapes it is expected to REJECT (the output must still be right). A sp_*
# method showing up as rejected there means a construct regressed, not a wrong answer.

def t(label)
  print label.ljust(56), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    # The class, not the message: which exception a specialized body raises is the JIT's
    # business, and IronRuby's wording of a few messages (ZeroDivisionError's, the quote
    # style of NoMethodError's) differs from CRuby's with the JIT off just the same.
    puts e.class
  end
end

HOT = 120

# Calls the block HOT times so that the method it calls turns hot.
def warm
  i = 0
  while i < HOT
    yield i
    i += 1
  end
end

# ---------------------------------------------------------------- 1. explicit return

def sp_fib(n)
  if n < 2
    return n
  end
  return sp_fib(n - 1) + sp_fib(n - 2)
end

def sp_bare(n)
  return if n > 5
  n * 2
end

def sp_ret_in_while(n)
  while n > 0
    return n if n == 7
    n -= 1
  end
  -1
end

def sp_elsif(n)
  if n < 0
    return -1
  elsif n == 0
    return 0
  elsif n < 10
    n
  else
    return 100
  end
end

def sp_unless(n)
  unless n > 3
    return n + 1000
  end
  n
end

def sp_ternary_return(n)
  n > 10 ? (return n * 2) : n - 1
end

def sp_return_bool(n)
  return n > 3
end

def sp_after_return(n)
  return n
  n + 1
end

def sp_float_rec(x)
  return x if x < 1.0
  sp_float_rec(x / 2.0)
end

puts '--- 1. explicit return ---'
warm { |i| sp_fib(i % 15) }
t('fib(0)')  { sp_fib(0) }
t('fib(1)')  { sp_fib(1) }
t('fib(20)') { sp_fib(20) }
t('fib(30)') { sp_fib(30) }
t('fib keyed in a Hash') { { sp_fib(10) => :x }[55] }
t('fib(10).equal?(55)') { sp_fib(10).equal?(55) }
t('fib(2.0) - a Float after Int profiling') { sp_fib(2.5) }
t('fib("x") - a String after Int profiling') { sp_fib("x") }
t('fib(nil)') { sp_fib(nil) }
warm { |i| sp_bare(i % 10) }
t('bare return') { [sp_bare(1), sp_bare(5), sp_bare(6), sp_bare(100)] }
warm { |i| sp_ret_in_while(i % 12) }
t('return inside a while') { [sp_ret_in_while(10), sp_ret_in_while(3), sp_ret_in_while(0), sp_ret_in_while(7)] }
warm { |i| sp_elsif(i - 20) }
t('return from elsif arms') { [-5, 0, 3, 50].map { |v| sp_elsif(v) } }
warm { |i| sp_unless(i % 8) }
t('return from unless') { [0, 3, 4, 9].map { |v| sp_unless(v) } }
warm { |i| sp_ternary_return(i % 20) }
t('return inside a ternary') { [5, 11, 2**31].map { |v| sp_ternary_return(v) } }
warm { |i| sp_return_bool(i % 8) }
t('return a comparison') { [1, 4].map { |v| sp_return_bool(v) } }
warm { |i| sp_after_return(i) }
t('code after return never runs') { sp_after_return(9) }
warm { |i| sp_float_rec(i * 1.5) }
t('Float recursion with return') { [sp_float_rec(100.0), sp_float_rec(0.5), sp_float_rec(1e300)] }

# ---------------------------------------------------------------- 2. what a join keeps

def sp_int_or_float(n)
  if n > 3
    return 1.5
  end
  n
end

def sp_float_or_int_tern(n)
  n > 3 ? 1.5 : n
end

def sp_int_or_long(n)
  return n * 100000 if n > 0
  n
end

def sp_ret_after_assign(n)
  x = n * 2
  return x if x > 10
  x + 0.5
end

puts
puts '--- 2. a join never changes a value\'s class ---'
warm { |i| sp_int_or_float(i % 6) }
t('Integer or Float, via return') { [2, 5].map { |v| sp_int_or_float(v) } }
warm { |i| sp_float_or_int_tern(i % 6) }
t('Integer or Float, via ternary') { [2, 5].map { |v| sp_float_or_int_tern(v) } }
warm { |i| sp_int_or_long(i % 5) }
t('Int32 or Int64') { [0, 1, 30000, -4].map { |v| sp_int_or_long(v) } }
t('Int32 or Int64 - classes') { [0, 30000].map { |v| sp_int_or_long(v).class } }
warm { |i| sp_ret_after_assign(i % 9) }
t('Integer or Float after an assignment') { [2, 20].map { |v| sp_ret_after_assign(v) } }

# ---------------------------------------------------------------- 3. overflow

def sp_sq(n)
  return n * n
end

def sp_add(a, b)
  return a + b
end

def sp_sub(a, b)
  a - b
end

def sp_poly(n)
  x = n * 3
  x = x + 7
  return x * x
end

def sp_narrow(n)
  # a long result stored back into an Int local
  x = 0
  x = n * 1000
  x
end

puts
puts '--- 3. overflow out of Int32 and out of Int64 ---'
warm { |i| sp_sq(i) }
t('sq(46340) still Int32') { sp_sq(46340) }
t('sq(46341) out of Int32') { sp_sq(46341) }
t('sq(3037000499) fits Int64') { sp_sq(3037000499) }
t('sq(3037000500) out of Int64') { sp_sq(3037000500) }
t('sq(-2**31)') { sp_sq(-2**31) }
t('sq(2**62)') { sp_sq(2**62) }
t('sq(2**64) Bignum argument') { sp_sq(2**64) }
t('sq(1.5)') { sp_sq(1.5) }
warm { |i| sp_add(i, i * 7) }
t('add Int32 max + 1') { sp_add(2**31 - 1, 1) }
t('add Int32 min - 1') { sp_add(-2**31, -1) }
t('add Int64 max + 1') { sp_add(2**63 - 1, 1) }
t('add Int64 min + -1') { sp_add(-2**63, -1) }
t('add Int64 max + Int64 max') { sp_add(2**63 - 1, 2**63 - 1) }
t('add Int + Float') { sp_add(1, 0.5) }
t('add String + String') { sp_add('a', 'b') }
warm { |i| sp_add(2**40 + i, i) }
t('add re-profiled on Int64: max + 1') { sp_add(2**63 - 1, 1) }
t('add re-profiled on Int64: small') { sp_add(3, 4) }
t('add re-profiled on Int64: small class') { sp_add(3, 4).class }
warm { |i| sp_sub(i, 1) }
t('sub Int64 min - 1') { sp_sub(-2**63, 1) }
t('sub 0 - Int64 min') { sp_sub(0, -2**63) }
warm { |i| sp_poly(i) }
t('poly(10)') { sp_poly(10) }
t('poly(2**20)') { sp_poly(2**20) }
t('poly(2**31 - 1)') { sp_poly(2**31 - 1) }
t('poly(2**40)') { sp_poly(2**40) }
warm { |i| sp_narrow(i) }
t('narrow(2**21) out of Int32') { sp_narrow(2**21) }
t('narrow(-2**21)') { sp_narrow(-2**21) }

# ---------------------------------------------------------------- 3b. deopting entries, large bodies

def sp_always_deopts(n)
  # out of Int64 for every n but 0, so the entry deopts until it is retired
  x = n * 65536
  x = x * 65536
  x * 65536 * 65536
end

def sp_rarely_deopts(n)
  # out of Int64 only for n > 2097151
  n * n * n
end

# A body far past the old 400-node limit.
eval("def sp_big(x)\n  s = x\n" + (0...120).map { |i| "  s = s + (s / 7) - #{i}\n" }.join + "  s\nend")

puts
puts '--- 3b. an entry that keeps deopting, one that rarely does, a large body ---'
warm { |i| sp_always_deopts(i) }
warm { |i| sp_always_deopts(i) }
t('always-deopting entry, after many deopts') { sp_always_deopts(3) }
t('always-deopting entry, the one value that fits') { sp_always_deopts(0) }
t('always-deopting entry, negative') { sp_always_deopts(-5) }
r = 0
warm { |i| r ^= sp_rarely_deopts(i == 100 || i == 110 ? 3_000_000 + i : i) }
t('rarely-deopting entry, mixed results') { r }
t('rarely-deopting entry, small') { sp_rarely_deopts(1000) }
t('rarely-deopting entry, out of Int64') { sp_rarely_deopts(2_097_152) }
warm { |i| sp_big(i) }
t('big body, small') { sp_big(1000) }
t('big body, out of Int64') { sp_big(2**60) }
t('big body, Bignum argument') { sp_big(2**70) }
t('big body, Float argument') { sp_big(1.5) }

# ---------------------------------------------------------------- 3c. a retired entry tried again, threads

def sp_phase(x)
  # out of Int64 for x >= 2**40: the entry is retired while setup passes such values, and tried
  # again once the calls with small ones have gone on for a while
  a = x * 3 + 1
  b = a * 5 - x
  c = b * 7 + a
  d = c - b * 2
  d * 4096 + a
end

def sp_threads(n)
  # specialized by whichever of several threads reaches the threshold first
  n * n * n * n
end

puts
puts '--- 3c. a retired entry tried again; threads warming one method ---'
r = 0
200.times { |i| r ^= sp_phase(2**60 + i) }
t('phase 1: every call out of Int64') { r }
r = 0
i = 0
while i < 300_000
  r += sp_phase(i & 1023)
  i += 1
end
t('phase 2: 300k small calls') { r }
def sp_phase_bump; 1; end
sp_phase_bump
def sp_phase_bump; 2; end   # a method table change: the entry tried again is stale now
t('after a method table change') { [sp_phase(5), sp_phase(2**50)] }
r = 0
warm { |k| r += sp_phase(k) }
t('specialized again') { r }
t('out of Int64 again') { sp_phase(2**62) }
r = 0
200.times { |k| r ^= sp_phase(k.even? ? 2**61 + k : k) }
t('phases mixed') { r }
ths = (0...6).map { |k| Thread.new { s = 0; 3000.times { |j| s ^= sp_threads(k * 1_000_000 + j) }; s } }
t('threads, results') { ths.map(&:value) }
ths = (0...6).map { |k| Thread.new { s = 0; 3000.times { |j| s += sp_threads(j & 255) }; s } }
t('threads, small results') { ths.map(&:value) }

# ---------------------------------------------------------------- 3d. deopting now and then: windows, probes

def sp_sometimes(x)
  # out of Int64 for x >= 2**40: deopting on some calls, the entry has its deopts timed, the
  # generic body timed in a probe, and is kept or retired by what they cost
  a = x * 7 + 3
  a * 4096 * 4096 + x
end

def sp_kw_sometimes(a, k: 3)
  # the same through a keyword method's direct entry
  a * k * 65536 * 65536
end

def sp_div_sometimes(a, b)
  # a zero divisor deopts, and the generic body re-run then raises
  a * 3 + a / b
end

puts
puts '--- 3d. deopting now and then: windows, probes, threads ---'
[2, 5, 9, 33].each do |k|
  r = 0
  i = 0
  while i < 20_000
    r += sp_sometimes(i % k == 0 ? 2**41 + i : i)
    i += 1
  end
  t("1 call in #{k} out of Int64") { r }
end
t('then small') { sp_sometimes(12345) }
t('then out of Int64') { sp_sometimes(2**50) }
ths = (0...4).map { |k| Thread.new { s = 0; 20_000.times { |j| s += sp_sometimes(j % (k + 2) == 0 ? 2**42 + j : j) }; s } }
t('threads deopting at different rates') { ths.map(&:value) }
r = 0
i = 0
while i < 20_000
  r += sp_kw_sometimes(i, k: (i % 4 == 0 ? 2**30 : 3))
  i += 1
end
t('keyword method, 1 call in 4 out of Int64') { r }
t('keyword method, default') { sp_kw_sometimes(7) }
t('keyword method, out of Int64') { sp_kw_sometimes(2**20, k: 2**20) }
r = 0
errors = 0
i = 0
while i < 20_000
  begin
    r += sp_div_sometimes(i, i % 3 == 0 ? 0 : 7)
  rescue ZeroDivisionError
    errors += 1
  end
  i += 1
end
t('zero divisor on 1 call in 3') { [r, errors] }
t('zero divisor, then fine') { sp_div_sometimes(100, 7) }

# ---------------------------------------------------------------- 4. division

def sp_div(a, b)
  return a / b
end

def sp_mod(a, b)
  return a % b
end

def sp_fdiv(a, b)
  return a / b
end

puts
puts '--- 4. division ---'
warm { |i| sp_div(i * 7 + 1, (i % 5) + 1) }
t('div 7 / 2') { sp_div(7, 2) }
t('div -7 / 2 floors') { sp_div(-7, 2) }
t('div 7 / -2 floors') { sp_div(7, -2) }
t('div by zero') { sp_div(1, 0) }
t('div 0 / 0') { sp_div(0, 0) }
t('div Int32 min / -1') { sp_div(-2**31, -1) }
t('div Int64 min / -1') { sp_div(-2**63, -1) }
t('div Int64 by zero') { sp_div(2**40, 0) }
warm { |i| sp_mod(i * 7 + 1, (i % 5) + 1) }
t('mod -7 % 2') { sp_mod(-7, 2) }
t('mod 7 % -2') { sp_mod(7, -2) }
t('mod by zero') { sp_mod(5, 0) }
t('mod Int32 min % -1') { sp_mod(-2**31, -1) }
warm { |i| sp_fdiv(i * 1.5, (i % 5) + 0.5) }
t('Float / 0.0') { sp_fdiv(1.0, 0.0) }
t('-Float / 0.0') { sp_fdiv(-1.0, 0.0) }
t('0.0 / 0.0 is NaN') { sp_fdiv(0.0, 0.0).nan? }
t('Float / Integer 0') { sp_fdiv(1.0, 0) }

# ---------------------------------------------------------------- 5. redefinition

def sp_redef(n)
  return n * 2
end

def sp_rec_redef(n)
  return 0 if n == 0
  sp_rec_redef(n - 1) + 1
end

puts
puts '--- 5. a method redefined after it was specialized ---'
warm { |i| sp_redef(i) }
t('before redefinition') { sp_redef(21) }
def sp_redef(n)
  return n * 3
end
t('after redefinition') { sp_redef(21) }
warm { |i| sp_redef(i) }
t('after redefinition, hot again') { sp_redef(21) }
warm { |i| sp_rec_redef(i % 10) }
t('recursive, before') { sp_rec_redef(10) }
alias sp_rec_orig sp_rec_redef
def sp_rec_redef(n)
  n == 5 ? 1000 : sp_rec_orig(n)
end
t('recursive, self-call now reaches the new body') { sp_rec_redef(10) }
t('the old body through its alias') { sp_rec_orig(10) }

# ---------------------------------------------------------------- 6. subclasses / singletons

class JitBase
  def cnt(n)
    return 0 if n == 0
    cnt(n - 1) + 1
  end
end

class JitPlain < JitBase
end

class JitLater < JitBase
end

puts
puts '--- 6. overriding a self-called method ---'
jb = JitBase.new
warm { |i| jb.cnt(i % 10) }
t('base cnt(10)') { jb.cnt(10) }
t('subclass without override') { JitPlain.new.cnt(10) }
class JitLater
  def cnt(n)
    n == 3 ? 1000 : super
  end
end
t('subclass overriding after specialization') { JitLater.new.cnt(10) }
t('base still right') { jb.cnt(10) }
jl = JitLater.new
warm { |i| jl.cnt(i % 10) }
t('subclass, hot') { jl.cnt(10) }
warm { |i| jb.cnt(i % 10) }
single = JitBase.new
def single.cnt(n)
  n == 2 ? -50 : super
end
t('singleton override') { single.cnt(10) }
t('base after the singleton') { jb.cnt(10) }

jp = JitPlain.new
warm { |i| jp.cnt(i % 10) }
t('profiled on a subclass instance') { jp.cnt(10) }
class JitPlain
  def cnt(n)
    n == 4 ? 7000 : super
  end
end
t('that subclass then overrides') { jp.cnt(10) }
t('base unaffected') { jb.cnt(10) }

# ---------------------------------------------------------------- 7. shapes it rejects

def no_ivar_then_overflow(n)
  @no_last = n
  return n * n
end

def no_nil_local(n)
  if n > 3
    x = 1
  end
  return x
end

def no_block_return(n)
  [1, 2, 3].each { |x| return x * 10 if x == n }
  0
end

def no_multi_return(n)
  return n, n + 1
end

def no_splat_return(n)
  return *[n, n]
end

def no_osr_return(n)
  i = 0
  while i < n
    return i if i == 5000
    i += 1
  end
  -1
end

puts
puts '--- 7. shapes the JIT rejects must still be right ---'
warm { |i| no_ivar_then_overflow(2**40 + i) }
t('ivar write, then Int64 overflow') { [no_ivar_then_overflow(2**40), no_ivar_then_overflow(2**33)] }
t('the ivar holds the last value') { @no_last }
warm { |i| no_nil_local(i % 6) }
t('local nil on the path that skipped it') { [no_nil_local(1), no_nil_local(5)] }
warm { |i| no_block_return(i % 4) }
t('return from inside a block') { [0, 1, 2, 3].map { |v| no_block_return(v) } }
warm { |i| no_multi_return(i) }
t('return of two values') { no_multi_return(4) }
warm { |i| no_splat_return(i) }
t('return of a splat') { no_splat_return(4) }
t('return from inside a hot loop (OSR)') { [no_osr_return(100000), no_osr_return(10)] }

# ---------------------------------------------------------------- 8b. keyword parameters

# Like t, but an ArgumentError's message is part of the answer: a keyword method's entry hands
# every call the prologue would complain about to the generic body, and the wording of the
# complaint is what shows that it did. (The few probes that use t instead are arity errors
# IronRuby words differently from CRuby with the JIT off as well: MRI appends the required
# keywords to them.)
def kt(label)
  print label.ljust(56), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue ArgumentError => e
    puts "ArgumentError: #{e.message}"
  rescue Exception => e
    puts e.class
  end
end

def sp_kw_add(left:, right:)
  left + right
end

def sp_kw_opt(a: 1, b: 2.5)
  a * b
end

def sp_kw_mixed(x, k:, j: 10)
  x - k * j
end

def sp_kw_obj(a, k: nil)
  k
end

def sp_kw_ret(n:, m: 0)
  return 0 if n < m
  n * 2
end

def sp_kw_assign(a:, b: 1)
  a = a + b
  a * 2
end

def no_kw_rest(a:, **rest)
  a + rest.size
end

def no_kw_default_expr(a:, b: a + 1)
  a + b
end

def no_kw_default_string(a:, b: "s")
  a
end

puts
puts '--- 8b. keyword parameters ---'
warm { |i| sp_kw_add(left: i, right: 1) }
kt('kw add') { sp_kw_add(left: 2, right: 3) }
kt('kw add, keywords in the other order') { sp_kw_add(right: 3, left: 20) }
kt('kw add Int32 max + 1') { sp_kw_add(left: 2**31 - 1, right: 1) }
kt('kw add Int64 max + 1') { sp_kw_add(left: 2**63 - 1, right: 1) }
kt('kw add Float') { sp_kw_add(left: 1.5, right: 1) }
kt('kw add String') { sp_kw_add(left: 'a', right: 'b') }
kt('kw add nil') { sp_kw_add(left: nil, right: 1) }
kt('kw add missing one') { sp_kw_add(left: 1) }
kt('kw add missing both') { sp_kw_add }
kt('kw add unknown') { sp_kw_add(left: 1, right: 2, up: 3) }
kt('kw add two unknown') { sp_kw_add(left: 1, right: 2, up: 3, down: 4) }
kt('kw add missing and unknown') { sp_kw_add(left: 1, up: 3) }
kt('kw add positional Hash') { sp_kw_add({ left: 1, right: 2 }) rescue $!.class }
kt('kw add String key') { sp_kw_add(left: 1, right: 2, 'x' => 3) }
kt('kw add **hash') { h = { left: 4, right: 5 }; [sp_kw_add(**h), h] }
kt('kw add **hash + literal') { h = { left: 4 }; [sp_kw_add(**h, right: 6), h] }
kt('kw add **{} extra') { sp_kw_add(left: 1, right: 2, **{}) }
kt('kw add **hash with a default proc') { h = Hash.new { 99 }; h[:left] = 1; sp_kw_add(**h, right: 2) }
kt('kw add via send') { send(:sp_kw_add, left: 7, right: 8) }
kt('kw add via Method#call') { method(:sp_kw_add).call(left: 7, right: 9) }
kt('kw add via public_send splat') { a = [{ left: 1, right: 1 }]; sp_kw_add(*a) rescue $!.class }
warm { |i| sp_kw_add(left: i * 0.5, right: 0.25) }
kt('kw add re-profiled on Float') { sp_kw_add(left: 1.5, right: 0.25) }
kt('kw add re-profiled on Float, Integer') { sp_kw_add(left: 1, right: 2) }

warm { |i| sp_kw_opt(a: i) }
kt('kw opt none') { sp_kw_opt }
kt('kw opt a') { sp_kw_opt(a: 4) }
kt('kw opt b') { sp_kw_opt(b: 3) }
kt('kw opt both') { sp_kw_opt(b: 2, a: 7) }
kt('kw opt a Float') { sp_kw_opt(a: 0.5) }
kt('kw opt a nil') { sp_kw_opt(a: nil) }
kt('kw opt unknown') { sp_kw_opt(c: 1) }
kt('kw opt positional') { sp_kw_opt(1) }
kt('kw opt positional Hash') { sp_kw_opt({ a: 1 }) }
kt('kw opt nil') { sp_kw_opt(nil) }
warm { |i| sp_kw_opt }
kt('kw opt hot without keywords') { sp_kw_opt }
kt('kw opt hot without keywords, given one') { sp_kw_opt(b: 4) }

warm { |i| sp_kw_mixed(i, k: 2) }
kt('kw mixed') { sp_kw_mixed(5, k: 1) }
kt('kw mixed with optional') { sp_kw_mixed(5, k: 1, j: 2) }
t('kw mixed missing positional') { sp_kw_mixed(k: 1) }
kt('kw mixed missing keyword') { sp_kw_mixed(5) }
t('kw mixed extra positional') { sp_kw_mixed(5, 6, k: 1) }
t('kw mixed positional Hash') { sp_kw_mixed(5, { k: 1 }) }
t('kw mixed nil for the keywords') { sp_kw_mixed(5, nil) }
kt('kw mixed overflow') { sp_kw_mixed(-2**62, k: 2**62, j: 2) }

warm { |i| sp_kw_obj('x', k: i) }
kt('kw obj') { sp_kw_obj('y', k: 5) }
kt('kw obj no keyword') { sp_kw_obj('y') }
kt('kw obj string-keyed hash') { sp_kw_obj('a' => 1) }
kt('kw obj hash and keyword') { sp_kw_obj({ 'a' => 1 }, k: 2) }

warm { |i| sp_kw_ret(n: i - 60) }
kt('kw return') { [sp_kw_ret(n: -1), sp_kw_ret(n: 4), sp_kw_ret(n: 4, m: 5)] }
warm { |i| sp_kw_assign(a: i) }
kt('kw local reassigned') { [sp_kw_assign(a: 3), sp_kw_assign(a: 3, b: 4)] }

warm { |i| no_kw_rest(a: i, z: 1) }
kt('kw **rest') { [no_kw_rest(a: 1), no_kw_rest(a: 1, b: 2, c: 3)] }
warm { |i| no_kw_default_expr(a: i) }
kt('kw default reads a parameter') { [no_kw_default_expr(a: 1), no_kw_default_expr(a: 1, b: 5)] }
warm { |i| no_kw_default_string(a: i) }
kt('kw String default') { no_kw_default_string(a: 1) }

# A keyword initializer: an instance variable store is a typed body too.
class KwIvars
  def initialize(x:, y: 0)
    @x = x
    @y = y
  end

  def pair
    [@x, @y]
  end
end
class KwIvarsSub < KwIvars
  def initialize(x:, z: 5)
    super(x: x, y: z)
  end
end

warm { |i| KwIvars.new(x: i) }
kt('kw initialize') { KwIvars.new(x: 1, y: 2).pair }
kt('kw initialize with the default') { KwIvars.new(x: 1).pair }
kt('kw initialize with Float and String') { KwIvars.new(x: 1.5, y: 'b').pair }
kt('kw initialize missing') { KwIvars.new(y: 1) }
kt('kw initialize unknown') { KwIvars.new(x: 1, w: 1) }
kt('kw initialize of a frozen object') { o = KwIvars.new(x: 1).freeze; o.send(:initialize, x: 2) }
kt('kw initialize via super') { KwIvarsSub.new(x: 3).pair }

# A receiverless keyword call goes straight into the specialized body when it can
# (JitKeywordCall): what it resolves to has to follow the receiver's class.
class KwCaller
  def sp_kw_scale(x, by: 2)
    x * by
  end
  private :sp_kw_scale

  def go(x)
    sp_kw_scale(x, by: 3)
  end

  def go_missing(x)
    sp_kw_not_here(x, by: 3)
  end

  def method_missing(name, *args, **kw)
    [name, args, kw]
  end
end
class KwCallerSub < KwCaller
  def sp_kw_scale(x, by: 2)
    [:sub, x, by]
  end
end

kc = KwCaller.new
warm { |i| kc.go(i) }
kt('kw call into a private method') { kc.go(5) }
kt('kw call, a Float') { kc.go(0.5) }
kt('kw call, overflow') { kc.go(2**62) }
kt('kw call, a String') { kc.go('ab') }
kt('kw call, overridden in a subclass') { KwCallerSub.new.go(5) }
kt('kw call, the superclass again') { kc.go(5) }
kt('kw call to method_missing') { kc.go_missing(1) }
def kc.sp_kw_scale(x, by: 2)
  :singleton
end
kt('kw call, a singleton method') { kc.go(5) }
kt('kw call, another instance') { KwCaller.new.go(5) }

def sp_kw_add(left:, right:)
  left - right
end
kt('kw add redefined') { sp_kw_add(left: 2, right: 3) }
warm { |i| sp_kw_add(left: i, right: 1) }
kt('kw add redefined, hot again') { sp_kw_add(left: 2, right: 3) }
def sp_kw_add(left:, right: 100)
  left + right
end
kt('kw add redefined with a default') { sp_kw_add(left: 2) }

# ---------------------------------------------------------------- 8. Integer#+ redefined

def sp_plus(a, b)
  return a + b
end

def sp_lt(a, b)
  return a < b
end

puts
puts '--- 8. Integer#+ and Integer#< redefined ---'
warm { |i| sp_plus(i, 1) }
warm { |i| sp_lt(i, 50) }
t('plus before') { sp_plus(2, 3) }
t('lt before') { sp_lt(2, 3) }
class Integer
  alias_method :__jit_matrix_plus, :+
  alias_method :__jit_matrix_lt, :<
  def +(o)
    o == 3 ? :plus_redefined : __jit_matrix_plus(o)
  end
  def <(o)
    o == 3 ? :lt_redefined : __jit_matrix_lt(o)
  end
end
t('plus after redefinition') { sp_plus(2, 3) }
t('lt after redefinition') { sp_lt(2, 3) }
warm { |i| sp_plus(i, 1) }
warm { |i| sp_lt(i, 50) }
t('plus after redefinition, hot again') { sp_plus(2, 3) }
t('lt after redefinition, hot again') { sp_lt(2, 3) }
t('fib after redefinition') { sp_fib(12) }
# First turning hot AFTER the redefinition: the operator the body would inline is not
# Integer's built-in one any more.
def no_plus_late(a, b)
  return a + b
end
def no_lt_late(a, b)
  return a < b
end
def no_minus_late(a, b)
  return a - b
end
warm { |i| no_plus_late(i, 1) }
warm { |i| no_lt_late(i, 50) }
t('plus first specialized after redefinition') { no_plus_late(2, 3) }
t('lt first specialized after redefinition') { no_lt_late(2, 3) }
class Float
  alias_method :__jit_matrix_fmul, :*
  def *(o)
    o == 2.0 ? :fmul_redefined : __jit_matrix_fmul(o)
  end
end
def no_fmul_late(a, b)
  return a * b
end
warm { |i| no_fmul_late(i * 0.5, 3.0) }
t('Float#* first specialized after redefinition') { no_fmul_late(1.5, 2.0) }
class Integer
  def -(o)
    :minus_redefined
  end
end
warm { |i| no_minus_late(i, 1) }
t('Integer#- replaced outright') { no_minus_late(9, 1) }
# `!x' is a call of x's #!, not an operator.
def no_not_early(n)
  return !n
end
def no_not_late(n)
  return !n
end
warm { |i| no_not_early(i) }
t('!Integer before') { no_not_early(5) }
class Integer
  def !
    :bang_redefined
  end
end
t('!Integer after redefinition') { no_not_early(5) }
warm { |i| no_not_late(i) }
t('!Integer first specialized after redefinition') { no_not_late(5) }
class Integer
  remove_method :!
end
t('!Integer restored') { [no_not_early(5), no_not_late(5)] }
class Integer
  alias_method :+, :__jit_matrix_plus
  alias_method :<, :__jit_matrix_lt
end
t('plus restored') { sp_plus(2, 3) }
t('plus_late restored') { no_plus_late(2, 3) }
class Float
  alias_method :*, :__jit_matrix_fmul
end

# ---------------------------------------------------------------- 9. refinements (must come last)

def sp_before_refine(n)
  return n + 1
end

puts
puts '--- 9. a refinement appears (must come last: once any refinement exists the JIT stands down) ---'
warm { |i| sp_before_refine(i) }
t('specialized before any refinement') { sp_before_refine(41) }
module JitRefine
  refine Integer do
    def +(o)
      "refined(#{self}, #{o})"
    end
  end
end

# Module#using in a class body is active to the end of that body, and nowhere else.
class JitRefinedUser
  using JitRefine
  def sp_in_refined_scope(n)
    return n + 1
  end
end
jr = JitRefinedUser.new
warm { |i| jr.sp_in_refined_scope(i) }
t('a method defined where + is refined') { jr.sp_in_refined_scope(41) }
t('the one defined before still unrefined') { sp_before_refine(41) }
warm { |i| sp_before_refine(i) }
t('and stays unrefined when hot again') { sp_before_refine(41) }


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

# `a && b' / `a || b' as a value is one of the operands, not true/false; as a condition only
# their truth counts.
def sp_and_value(a)
  a && a + 1
end

def sp_and_assigned(a)
  b = a && a * 2
  b
end

def sp_cmp_and_value(a)
  a > 0 && a + 1
end

def sp_cmp_or_value(a)
  a > 0 || a - 1
end

def sp_or_value(a)
  a || 7
end

def sp_float_and_value(a)
  a > 1.0 && a * 2.0
end

def sp_bool_and_or(a)
  (a > 0 && a < 10) || a == -5
end

def sp_and_condition(a)
  if a > 0 && a + 1
    :yes
  else
    :no
  end
end

def sp_or_overflow(a)
  a > 0 && a * a * a
end

def sp_opt_and(a, b = a && a + 1)
  b
end

def sp_opt_or(a, b = (a || 7))
  b
end

def sp_opt_cmp_and(a, b = (a > 0 && a))
  b
end

def sp_kw_and(a, k: a && a * 3)
  k
end

puts
puts '--- 2b. && and || keep the operand ---'
warm { |i| sp_and_value(i); sp_and_assigned(i); sp_cmp_and_value(i - 60); sp_cmp_or_value(i - 60); sp_or_value(i) }
t('n && n + 1') { [sp_and_value(1), sp_and_value(2**62)] }
t('b = n && n * 2') { sp_and_assigned(3) }
t('n > 0 && n + 1') { [sp_cmp_and_value(4), sp_cmp_and_value(-4)] }
t('n > 0 || n - 1') { [sp_cmp_or_value(4), sp_cmp_or_value(-4)] }
t('n || 7') { [sp_or_value(4), sp_or_value(nil), sp_or_value(false)] }
warm { |i| sp_float_and_value(i * 0.1); sp_bool_and_or(i - 60); sp_and_condition(i - 60); sp_or_overflow(i - 60) }
t('x > 1.0 && x * 2.0') { [sp_float_and_value(1.5), sp_float_and_value(0.5)] }
t('(a && b) || c on Bools') { [-5, 0, 5, 50].map { |v| sp_bool_and_or(v) } }
t('&& as a condition') { [sp_and_condition(3), sp_and_condition(-3)] }
t('&& with an overflowing right operand') { [sp_or_overflow(2**30), sp_or_overflow(-1), sp_or_overflow(2**20), sp_or_overflow(3)] }
warm { |i| sp_opt_and(i); sp_opt_or(i); sp_opt_cmp_and(i - 60); sp_kw_and(i) }
t('optional default n && n + 1') { [sp_opt_and(1), sp_opt_and(1, :given), sp_opt_and(nil)] }
t('optional default (n || 7)') { [sp_opt_or(1), sp_opt_or(nil), sp_opt_or(false, 2)] }
t('optional default (n > 0 && n)') { [sp_opt_cmp_and(5), sp_opt_cmp_and(-5)] }
t('keyword default n && n * 3') { [sp_kw_and(2), sp_kw_and(2, k: 0), sp_kw_and(nil)] }

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

def sp_kw_default_expr(a:, b: a + 1)
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
warm { |i| sp_kw_default_expr(a: i) }
kt('kw default reads a parameter') { [sp_kw_default_expr(a: 1), sp_kw_default_expr(a: 1, b: 5)] }
warm { |i| no_kw_default_string(a: i) }
kt('kw String default') { no_kw_default_string(a: 1) }

# Keyword defaults the typed body evaluates itself, in declaration order.
def sp_kw_sym(a, mode: :fast)
  mode
end

def sp_kw_list(x:, list: [x])
  list
end

def sp_kw_chain(a:, b: a * 2, c: b + 1)
  a + b + c
end

def sp_kw_ivar_default(x:, y: @kw_default)
  y
end

def sp_kw_seen(a:, b: (@kw_seen = a))
  b
end

def sp_kw_square(a:, b: a * a)
  b
end

def sp_kw_div(a:, b: 10 / a)
  b
end

def no_kw_assign_default(a:, b: (t = a), c: 1)
  [b, c, t]
end

warm { |i| sp_kw_sym(i) }
kt('kw Symbol default') { [sp_kw_sym(1), sp_kw_sym(1, mode: :slow), sp_kw_sym(1).equal?(:fast)] }
warm { |i| sp_kw_list(x: i) }
kt('kw Array default') { [sp_kw_list(x: 1), sp_kw_list(x: 1, list: 2)] }
kt('kw Array default is a new Array each time') { sp_kw_list(x: 1).equal?(sp_kw_list(x: 1)) }
warm { |i| sp_kw_chain(a: i); sp_kw_chain(a: i, c: 1) }
kt('kw chain') { [sp_kw_chain(a: 1), sp_kw_chain(a: 1, b: 5), sp_kw_chain(a: 1, c: 7), sp_kw_chain(a: 1, b: 5, c: 7)] }
kt('kw chain, Float b') { sp_kw_chain(a: 1, b: 0.5) }
kt('kw chain, String a') { sp_kw_chain(a: 'x') }
kt('kw chain, overflow') { sp_kw_chain(a: 2**62) }
kt('kw chain, unknown') { sp_kw_chain(a: 1, d: 2) }
kt('kw chain, missing') { sp_kw_chain(b: 1) }
warm { |i| sp_kw_ivar_default(x: i) }
kt('kw ivar default, unset') { sp_kw_ivar_default(x: 1) }
@kw_default = :set
kt('kw ivar default, set') { [sp_kw_ivar_default(x: 1), sp_kw_ivar_default(x: 1, y: 2)] }
warm { |i| sp_kw_seen(a: i) }
kt('kw default writes an ivar') { [sp_kw_seen(a: 7), @kw_seen, sp_kw_seen(a: 8, b: 9), @kw_seen] }
warm { |i| sp_kw_square(a: i) }
kt('kw default overflows') { [sp_kw_square(a: 3), sp_kw_square(a: 2**40), sp_kw_square(a: 2**40, b: 1)] }
warm { |i| sp_kw_div(a: i + 1) }
kt('kw default divides by zero') { sp_kw_div(a: 0) }
kt('kw default divides, given') { sp_kw_div(a: 0, b: 1) }
warm { |i| no_kw_assign_default(a: i) }
kt('kw default assigns a local') { [no_kw_assign_default(a: 1), no_kw_assign_default(a: 1, b: 2)] }

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

# ... and leaving out a keyword whose default the typed body evaluates
class KwDefaultCaller
  def sp_kw_two(x, a: 1, b: a + x)
    x * a - b
  end

  def go(x)
    sp_kw_two(x, a: 5)
  end

  def go_both(x)
    sp_kw_two(x, b: 2, a: 3)
  end
end
kd = KwDefaultCaller.new
warm { |i| kd.go(i); kd.go_both(i) }
kt('kw call leaving out an expression default') { [kd.go(4), kd.go_both(4)] }
kt('kw call leaving out an expression default, Float') { kd.go(0.5) }
kt('kw call leaving out an expression default, overflow') { kd.go(2**61) }

# A keyword call with an explicit receiver goes direct only to a public method.
class KwTarget
  def sp_kw_pub(x, by: 2)
    x * by
  end

  def sp_kw_priv(x, by: 2)
    x * by
  end
  private :sp_kw_priv

  def sp_kw_prot(x, by: 2)
    x * by
  end
  protected :sp_kw_prot

  def call_prot(o, x)
    o.sp_kw_prot(x, by: 3)
  end
end
class KwTargetSub < KwTarget
  def sp_kw_pub(x, by: 2)
    [:sub, x, by]
  end
end
class KwGhost
  def method_missing(name, *args, **kw)
    [name, args, kw]
  end
end
def kw_recv_call(o, x)
  o.sp_kw_pub(x, by: 3)
end
def kw_recv_priv(o, x)
  o.sp_kw_priv(x, by: 3)
end
def kw_recv_prot(o, x)
  o.sp_kw_prot(x, by: 3)
end
def kw_recv_safe(o, x)
  o&.sp_kw_pub(x, by: 3)
end
$kw_order = []
def kw_recv_order(o)
  ($kw_order << :recv; o).sp_kw_pub(($kw_order << :arg; 4), by: ($kw_order << :kw; 5))
end
kr = KwTarget.new
warm { |i| kw_recv_call(kr, i); kw_recv_safe(kr, i) }
kt('kw receiver call') { kw_recv_call(kr, 5) }
kt('kw receiver call, Float') { kw_recv_call(kr, 0.5) }
kt('kw receiver call, overflow') { kw_recv_call(kr, 2**62) }
kt('kw receiver call, a String') { kw_recv_call(kr, 'ab') }
kt('kw receiver call, a subclass') { kw_recv_call(KwTargetSub.new, 5) }
kt('kw receiver call, the class again') { kw_recv_call(kr, 5) }
kt('kw receiver call to method_missing') { kw_recv_call(KwGhost.new, 5) }
kt('kw receiver call on nil') { kw_recv_call(nil, 5) }
kt('kw receiver safe call') { [kw_recv_safe(kr, 5), kw_recv_safe(nil, 5)] }
warm { |i| kr.send(:sp_kw_priv, i, by: 1); kr.call_prot(kr, i) }
kt('kw receiver call, private') { kw_recv_priv(kr, 5) }
kt('kw receiver call, protected from outside') { kw_recv_prot(kr, 5) }
kt('kw receiver call, protected from inside') { kr.call_prot(KwTarget.new, 5) }
warm { |i| kw_recv_order(kr) }
$kw_order = []
kt('kw receiver call, evaluation order') { [kw_recv_order(kr), $kw_order] }
$kw_order = []
kt('kw receiver call, evaluation order on overflow') { [kw_recv_order(Class.new(KwTarget) { def sp_kw_pub(x, by: 2) = x * by * 2**62 }.new), $kw_order] }
def kr.sp_kw_pub(x, by: 2)
  :singleton
end
kt('kw receiver call, a singleton method') { kw_recv_call(kr, 5) }
class KwTarget
  def sp_kw_pub(x, by: 2)
    x - by
  end
end
kt('kw receiver call, redefined') { kw_recv_call(KwTarget.new, 5) }

# `private :m' (or protected) in a subclass, a singleton class or an including class hides an
# inherited public m from a receiver call - the direct one too, once m is specialized - and
# `public :m' there shows an inherited private one.
class KwHidePriv < KwTarget; private :sp_kw_pub; end
class KwHideProt < KwTarget; protected :sp_kw_pub; end
class KwShowPub < KwTarget; public :sp_kw_priv; end
kw_hidden = KwTarget.new
class << kw_hidden; private :sp_kw_pub; end
def kw_hide_priv(o, x) = o.sp_kw_pub(x, by: 3)
def kw_hide_prot(o, x) = o.sp_kw_pub(x, by: 3)
def kw_hide_single(o, x) = o.sp_kw_pub(x, by: 3)
def kw_show_pub(o, x) = o.sp_kw_priv(x, by: 3)
kw_open = KwTarget.new
warm { |i| kw_hide_priv(kw_open, i); kw_hide_prot(kw_open, i); kw_hide_single(kw_open, i); kw_show_pub(KwShowPub.new, i) }
kt('kw receiver call, made private in a subclass') { kw_hide_priv(KwHidePriv.new, 5) }
kt('kw receiver call, made protected in a subclass') { kw_hide_prot(KwHideProt.new, 5) }
kt('kw receiver call, made private in a singleton class') { kw_hide_single(kw_hidden, 5) }
kt('kw receiver call, made public in a subclass') { kw_show_pub(KwShowPub.new, 5) }
kt('kw receiver call, the public one again') { [kw_hide_priv(kw_open, 5), kw_hide_single(kw_open, 5)] }
class KwFactory
  def self.sp_kw_build(x, by: 2)
    x * by
  end
end
class KwFactorySub < KwFactory; private_class_method :sp_kw_build; end
def kw_build(c, x)
  c.sp_kw_build(x, by: 3)
end
warm { |i| kw_build(KwFactory, i) }
kt('kw receiver call, private_class_method in a subclass') { kw_build(KwFactorySub, 5) }
kt('kw receiver call, the class method again') { kw_build(KwFactory, 5) }
module KwApi
  def sp_kw_api(x, by: 2)
    x * by
  end
end
class KwApiOpen; include KwApi; end
class KwApiHidden; include KwApi; private :sp_kw_api; end
def kw_api(o, x)
  o.sp_kw_api(x, by: 3)
end
warm { |i| kw_api(KwApiOpen.new, i) }
kt('kw receiver call, module method made private') { kw_api(KwApiHidden.new, 5) }
kt('kw receiver call, module method still public') { kw_api(KwApiOpen.new, 5) }

# A site whose callee has no direct entry - a library method, a Ruby method the JIT does not
# take - stops trying after a few calls. Whatever it meets after that it still calls the
# ordinary way: another class's method, the callee redefined into one the JIT takes.
def kw_recv_round(o)
  o.round(half: :even)
end
class KwRounder
  def round(half: :up)
    [:rounder, half]
  end
end
warm { |i| kw_recv_round(2.5) }
kt('kw receiver call into a library method') { kw_recv_round(2.5) }
kt('kw receiver call, then a Ruby method') { kw_recv_round(KwRounder.new) }
kt('kw receiver call, then library methods again') { [kw_recv_round(3.5), kw_recv_round(Rational(5, 2))] }
class KwDeclined
  def no_kw_str(x, by: 2)
    "#{x}" * by
  end
end
def kw_recv_declined(o, x)
  o.no_kw_str(x, by: 2)
end
kd = KwDeclined.new
warm { |i| kw_recv_declined(kd, i) }
kt('kw receiver call, declined method') { kw_recv_declined(kd, 5) }
kt('kw receiver call, declined method, a String') { kw_recv_declined(kd, 'ab') }
class KwDeclined
  def no_kw_str(x, by: 2)
    x * by
  end
end
warm { |i| kw_recv_declined(kd, i) }
kt('kw receiver call, declined method redefined') { kw_recv_declined(kd, 5) }
kt('kw receiver call, declined method redefined, Float') { kw_recv_declined(kd, 0.5) }
kt('kw receiver call, declined method redefined, overflow') { kw_recv_declined(kd, 2**62) }
# A callee whose entry deopts until it is retired: while it cools down it has no direct entry,
# and the receiver site waits for it to come back rather than going quiet (JitStub.IsCooling).
class KwCooling
  def sp_kw_cool(x, by: 3)
    x * by * x * by
  end
end
def kw_recv_cool(o, x)
  o.sp_kw_cool(x, by: 3)
end
kc = KwCooling.new
warm { |i| kw_recv_cool(kc, i) }
r = 0
20.times { |j| warm { |i| r += kw_recv_cool(kc, 2_000_000_000 + 120 * j + i) } }
kt('kw receiver call, callee retired for deopting') { r }
r = 0
20.times { warm { |i| r += kw_recv_cool(kc, i) } }
kt('kw receiver call, callee tried again') { [r, kw_recv_cool(kc, 5), kc.sp_kw_cool(5, by: 2), kc.sp_kw_cool(5)] }

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

# ---------------------------------------------------------------- 8c. optional parameters

# A default that is not a nil/true/false/Integer/Float literal: one typed body per number of
# optional arguments a call passes, each evaluating the omitted defaults itself, in order.
def sp_opt_param(a, b = a)
  a + b
end

def sp_opt_chain(a, b = a + 1, c = b * 2)
  a + b + c
end

def sp_opt_sym(a, mode = :fast)
  mode
end

def sp_opt_array(a, b = [a])
  b
end

def sp_opt_ivar(a, b = @opt_default)
  b
end

def sp_opt_ivar_write(a, b = (@opt_seen = a))
  b
end

def sp_opt_square(a, b = a * a)
  b
end

def sp_opt_rec(n, acc = 0)
  return acc if n == 0
  sp_opt_rec(n - 1, acc + n)
end

def sp_opt_div(a, b = 10 / a)
  b
end

def no_opt_self(a, b = b)
  b
end

class OptInit
  def initialize(x, y = x, z = :none)
    @x = x
    @y = y
    @z = z
  end

  def triple
    [@x, @y, @z]
  end
end

puts
puts '--- 8c. optional parameters ---'
warm { |i| sp_opt_param(i); sp_opt_param(i, 1) }
t('opt default reads a parameter') { [sp_opt_param(3), sp_opt_param(3, 4)] }
t('opt default, Float') { [sp_opt_param(1.5), sp_opt_param(1.5, 2)] }
t('opt default, String') { [sp_opt_param('a'), sp_opt_param('a', 'b')] }
t('opt default, overflow') { sp_opt_param(2**62) }
t('opt default, too many') { sp_opt_param(1, 2, 3) }
t('opt default, too few') { sp_opt_param }
warm { |i| sp_opt_chain(i); sp_opt_chain(i, 2); sp_opt_chain(i, 2, 3) }
t('opt chain') { [sp_opt_chain(1), sp_opt_chain(1, 5), sp_opt_chain(1, 5, 7)] }
t('opt chain, Float in the middle') { sp_opt_chain(1, 0.5) }
warm { |i| sp_opt_sym(i) }
t('opt Symbol default') { [sp_opt_sym(1), sp_opt_sym(1, :slow), sp_opt_sym(1).equal?(:fast)] }
warm { |i| sp_opt_array(i) }
t('opt Array default') { [sp_opt_array(1), sp_opt_array(1, 2)] }
t('opt Array default is a new Array each time') { sp_opt_array(1).equal?(sp_opt_array(1)) }
warm { |i| sp_opt_ivar(i) }
t('opt ivar default, unset') { sp_opt_ivar(1) }
@opt_default = 42
t('opt ivar default, set') { [sp_opt_ivar(1), sp_opt_ivar(1, 2)] }
warm { |i| sp_opt_ivar_write(i) }
t('opt default writes an ivar') { [sp_opt_ivar_write(7), @opt_seen, sp_opt_ivar_write(8, 9), @opt_seen] }
warm { |i| sp_opt_square(i) }
t('opt default overflows') { [sp_opt_square(3), sp_opt_square(2**40), sp_opt_square(2**40, 1)] }
warm { |i| sp_opt_rec(i % 20); sp_opt_rec(i % 20, 1) }
t('opt self-recursion') { [sp_opt_rec(10), sp_opt_rec(10, 5), sp_opt_rec(100)] }
warm { |i| sp_opt_div(i + 1) }
t('opt default divides by zero') { sp_opt_div(0) }
t('opt default divides, given') { sp_opt_div(0, 1) }
warm { |i| no_opt_self(i) }
t('opt default reads itself') { [no_opt_self(1), no_opt_self(1, 2)] }
warm { |i| OptInit.new(i); OptInit.new(i, 2); OptInit.new(i, 2, 3) }
t('opt initialize') { [OptInit.new(1).triple, OptInit.new(1, 2).triple, OptInit.new(1, 2, 3).triple] }
t('opt initialize of a frozen object') { o = OptInit.new(1).freeze; o.send(:initialize, 5) }
def sp_opt_param(a, b = a * 10)
  a - b
end
t('opt redefined') { [sp_opt_param(3), sp_opt_param(3, 1)] }
warm { |i| sp_opt_param(i) }
t('opt redefined, hot again') { [sp_opt_param(3), sp_opt_param(3, 1)] }
class OptInitSub < OptInit
  def initialize(x, y = x + 100)
    super
  end
end
warm { |i| OptInitSub.new(i) }
t('opt zsuper passes the defaulted value') { [OptInitSub.new(1).triple, OptInitSub.new(1, 2).triple] }

# Per-count bodies are one specialization (JitStub.Specialization): their deopts count towards
# retiring the entry, and the entry is cooled down and tried again like any other.
def sp_opt_deopts(a, b = a * a)
  # out of Int64 for a >= 55109 when b is left out
  b * b
end
warm { |i| sp_opt_deopts(i); sp_opt_deopts(i, i) }
r = 0
20.times { |j| warm { |i| r += sp_opt_deopts(100_000 + 120 * j + i) } }
t('opt per-count entry that keeps deopting') { r }
t('opt per-count entry, small again') { [sp_opt_deopts(3), sp_opt_deopts(3, 4), sp_opt_deopts(-7)] }
t('opt per-count entry, out of Int64, given') { sp_opt_deopts(1, 2**40) }

def sp_opt_plus(a, b = 1 + a)
  b
end
warm { |i| sp_opt_plus(i) }

# ---------------------------------------------------------------- 7b. case/when, Array/Hash elements, constants

class JitStr < String; end
class JitArr < Array; end
class JitHash < Hash
  def [](k)
    :subclass_element
  end
end
module JitCaseMod; end
class JitPlain; end
JIT_LIMIT = 10
JIT_CASE_CLS = String
JIT_MIX = [1, "x", 2.0, :s, nil, [1], { a: 1 }, 1..2, 2**70, 2**40, true, false, JitPlain.new, JitStr.new("y")]

def sp_case_class(v)
  case v
  when Integer then 1
  when String then 2
  when Float then 3
  when Symbol then 4
  when nil then 5
  when Array, Hash then 6
  when Comparable then 7
  when JitCaseMod then 8
  else 9
  end
end

def sp_case_int(n)
  case n
  when 0 then :zero
  when 1..5 then :small
  when 6, 7 then :six_seven
  when 8...JIT_LIMIT then :below_limit
  when JIT_LIMIT then :limit
  when Integer then :int
  else :other
  end
end

def sp_case_float(x)
  case x
  when 1 then :one
  when 0..2 then :zero_two
  when Float then :float
  else :other
  end
end

# a range when reached by subjects that are not numbers: `1 <=> v' is nil for those that have no
# #coerce, which the specialized test answers itself; one with a #coerce (or a method_missing or a
# respond_to? that might find one) has it called, as many times as the generic code calls it
def sp_case_range(v)
  case v
  when 1..5 then :small
  when 0...100 then :below_hundred
  when Integer then :int
  when String then :str
  else :other
  end
end
JIT_RANGE_WARM = [3, 50, 200, "x", :s, nil, [1], { a: 1 }, true, false, 2.5, 7.5]
$jit_coerced = 0

# Integer whens reached by subjects of every kind: `L === v' is `v == L', which an Array, a Hash and
# a Range answer false for an Integer (it has no to_ary or to_hash) - unless their == is someone else's
def sp_case_lit(v)
  case v
  when 0 then :zero
  when 1, 2 then :one_two
  when Array then :array
  when Hash then :hash
  when Range then :range
  else :other
  end
end
JIT_LIT_WARM = [0, 1, 2, 3, [1], { 1 => 2 }, 1..2, nil, :s, "x", true, 2.0, 2**40, [], {}]
$jit_eq = 0

def sp_case_sym(s)
  case s
  when :add then 1
  when :sub then 2
  when :mul, :div then 3
  else 0
  end
end

def sp_case_str(s)
  case s
  when "foo" then 1
  when "bar", "baz" then 2
  when "é" then 3
  else 0
  end
end

def sp_case_cond(n)
  case
  when n < 0 then -1
  when n == 0 then 0
  else 1
  end
end

def sp_case_const(v)
  case v
  when JIT_CASE_CLS then :the_class
  else :other
  end
end

def sp_case_value(n)
  r = case n
      when 1 then 10
      when 2 then 2.5
      else n * 2
      end
  r
end

module JitOuter
  K = :outer
  LIMIT = 3
  class JitInner
    def sp_lexical(n)
      case n
      when 0...LIMIT then K
      else :no
      end
    end
  end
end

def sp_qualified(v)
  case v
  when JitOuter::JitInner then 1
  when ::String then 2
  else JitOuter::LIMIT + 10
  end
end

def sp_hget(h, k)
  h[k]
end

def sp_hkey(h, k)
  h.key?(k)
end

def sp_hfetch(h, k)
  h.fetch(k, :none)
end

def sp_hfetch1(h, k)
  h.fetch(k)
end

def sp_hincr(h, k, n)
  h[k] += 1
  n * n
end

def sp_hset(h, k, v)
  h[k] = v
end

def sp_aget(a, i)
  a[i]
end

def sp_aset(a, i, v)
  a[i] = v
end

def sp_asum(a, n)
  i = 0
  s = 0
  while i < n
    s += a[i]
    i += 1
  end
  s
end

def sp_scaled(a, i)
  a[i] * 2.5 + (a[i] < 3 ? 1 : 0)
end

def sp_hsum(h, a)
  i = 0
  s = 0
  while i < 4
    s += h[i] + a[i]
    i += 1
  end
  s
end

puts
puts '--- 7b. case/when, Array/Hash elements, constants ---'
warm { |i| sp_case_class(JIT_MIX[i % JIT_MIX.size]) }
t('case of each kind') { JIT_MIX.map { |v| sp_case_class(v) } }
t('case of subclass, Object, Module, singleton') { [JitStr.new("a"), Object.new, Kernel, Comparable].map { |v| sp_case_class(v) } }
ext = "e".dup; ext.extend(JitCaseMod)
eobj = Object.new; eobj.extend(JitCaseMod)
t('case of objects extended with a module') { [sp_case_class(ext), sp_case_class(eobj)] }
warm { |i| sp_case_int(i % 12) }
t('case of an Integer') { (-1..12).map { |n| sp_case_int(n) } }
t('case of an Integer, other types') { [sp_case_int(2**40), sp_case_int(2**70), sp_case_int(3.0), sp_case_int("3"), sp_case_int(nil)] }
warm { |i| sp_case_float(i * 0.5) }
t('case of a Float') { [0.0, 1.0, 1.5, 2.0, 2.5, -0.5, 0.0 / 0.0].map { |x| sp_case_float(x) } }
t('case of a Float, other types') { [sp_case_float(1), sp_case_float(2), sp_case_float(:x)] }
warm { |i| sp_case_range(JIT_RANGE_WARM[i % JIT_RANGE_WARM.size]) }
t('case of a range, each kind') { JIT_MIX.map { |v| sp_case_range(v) } }
t('case of a range, Floats and edges') { [0, 1, 5, 6, 99, 100, -1, 0.5, 1.0, 5.0, 5.5, 99.5].map { |v| sp_case_range(v) } }
warm { |i| sp_case_lit(JIT_LIT_WARM[i % JIT_LIT_WARM.size]) }
t('case of Integer literals, each kind') { JIT_MIX.map { |v| sp_case_lit(v) } + JIT_LIT_WARM.map { |v| sp_case_lit(v) } }
t('case of Integer literals, subclasses') { [JitArr.new, JitHash.new, JitStr.new("1"), 2**70, 1.0, 0.0, -0.0].map { |v| sp_case_lit(v) } }
warm { |i| sp_case_sym([:add, :sub, :mul, :div, :mod][i % 5]) }
t('case of a Symbol') { [:add, :sub, :mul, :div, :mod, "add", nil, 1].map { |s| sp_case_sym(s) } }
warm { |i| sp_case_str(["foo", "bar", "baz", "qux"][i % 4]) }
t('case of a String') { ["foo", "bar", "baz", "qux", "é", "é".b, "foo".b, JitStr.new("foo"), :foo, nil, 1, 2.0, true].map { |s| sp_case_str(s) } }
warm { |i| sp_case_cond(i - 60) }
t('case without a subject') { [sp_case_cond(-5), sp_case_cond(0), sp_case_cond(5)] }
warm { |i| sp_case_const(["a", 1][i % 2]) }
t('case of a constant') { [sp_case_const("a"), sp_case_const(1)] }
warm { |i| sp_case_value(i % 4) }
t('case value of mixed types') { (0..4).map { |n| sp_case_value(n) } }
lex = JitOuter::JitInner.new
warm { |i| lex.sp_lexical(i % 5) }
t('a lexical constant') { (0..4).map { |n| lex.sp_lexical(n) } }
JitOuter::JitInner::K = :inner
t('a lexical constant shadowed') { (0..4).map { |n| lex.sp_lexical(n) } }
warm { |i| lex.sp_lexical(i % 5) }
t('a lexical constant shadowed, hot again') { (0..4).map { |n| lex.sp_lexical(n) } }
warm { |i| sp_qualified([lex, "s", 1][i % 3]) }
t('qualified and global constants') { [sp_qualified(lex), sp_qualified("s"), sp_qualified(1)] }

# the methods === stands for, redefined mid-run
class << Integer
  def ===(o)
    o == :magic_integer || super
  end
end
t('Integer.=== redefined') { [sp_case_class(:magic_integer), sp_case_class(5), sp_case_int(:magic_integer)] }
warm { |i| sp_case_class(JIT_MIX[i % JIT_MIX.size]) }
t('Integer.=== redefined, hot again') { [sp_case_class(:magic_integer), sp_case_class(5)] }
class << Integer
  remove_method :===
end
t('Integer.=== restored') { [sp_case_class(:magic_integer), sp_case_class(5)] }
class Module
  alias_method :__jit_matrix_eqq, :===
  def ===(o)
    o == :magic_module ? true : __jit_matrix_eqq(o)
  end
end
t('Module#=== redefined') { [sp_case_class(:magic_module), sp_case_class("s"), sp_case_const(:magic_module)] }
class Module
  alias_method :===, :__jit_matrix_eqq
end
t('Module#=== restored') { [sp_case_class(:magic_module), sp_case_class("s")] }
class Integer
  alias_method :__jit_matrix_ieqq, :===
  def ===(o)
    o == :magic_int ? true : __jit_matrix_ieqq(o)
  end
end
t('Integer#=== redefined') { [sp_case_int(:magic_int), sp_case_int(0), sp_case_float(:magic_int)] }
class Integer
  alias_method :===, :__jit_matrix_ieqq
end
t('Integer#=== restored') { [sp_case_int(:magic_int), sp_case_int(0)] }
class Symbol
  alias_method :__jit_matrix_seqq, :===
  def ===(o)
    o == "add" ? true : __jit_matrix_seqq(o)
  end
end
t('Symbol#=== redefined') { [sp_case_sym("add"), sp_case_sym(:sub)] }
class Symbol
  alias_method :===, :__jit_matrix_seqq
end
t('Symbol#=== restored') { [sp_case_sym("add"), sp_case_sym(:sub)] }
class Range
  alias_method :__jit_matrix_reqq, :===
  def ===(o)
    o == 100 ? true : __jit_matrix_reqq(o)
  end
end
t('Range#=== redefined') { [sp_case_int(100), sp_case_int(3)] }
class Range
  alias_method :===, :__jit_matrix_reqq
end
t('Range#=== restored') { [sp_case_int(100), sp_case_int(3)] }
# what `1 <=> v' asks a subject Range#=== cannot compare: its #coerce, a method_missing standing in for
# one, a respond_to? asked about one - each defined mid-run on a class whose instances the range test
# answered false for itself, then hot again, then taken away
class Symbol
  def coerce(n)
    $jit_coerced += 1
    nil
  end
end
t('Symbol#coerce defined') { [JIT_MIX.map { |v| sp_case_range(v) }, $jit_coerced] }
warm { |i| sp_case_range(JIT_RANGE_WARM[i % JIT_RANGE_WARM.size]) }
t('Symbol#coerce defined, hot again') { [JIT_MIX.map { |v| sp_case_range(v) }, $jit_coerced > 0] }
class Symbol
  remove_method :coerce
end
$jit_coerced = 0
t('Symbol#coerce removed') { [JIT_MIX.map { |v| sp_case_range(v) }, $jit_coerced] }
jit_coercing = "y".dup
def jit_coercing.coerce(n)
  $jit_coerced += 1
  [n, 50]
end
t('a String with a singleton coerce') { [sp_case_range(jit_coercing), sp_case_range("x"), $jit_coerced] }
warm { |i| sp_case_range(JIT_RANGE_WARM[i % JIT_RANGE_WARM.size]) }
$jit_coerced = 0
t('a String with a singleton coerce, hot again') { [sp_case_range(jit_coercing), sp_case_range("x"), $jit_coerced] }
module JitCountCoerce
  def method_missing(name, *args)
    $jit_coerced += 1 if name == :coerce
    super
  end
end
class NilClass
  include JitCountCoerce
end
class Array
  include JitCountCoerce
end
$jit_coerced = 0
t('a method_missing included into NilClass and Array') { [[nil, true, :s, [1]].map { |v| sp_case_range(v) }, $jit_coerced] }
warm { |i| sp_case_range(JIT_RANGE_WARM[i % JIT_RANGE_WARM.size]) }
$jit_coerced = 0
t('a method_missing included, hot again') { [[nil, true, :s, [1]].map { |v| sp_case_range(v) }, $jit_coerced] }
module JitCountCoerce
  remove_method :method_missing
end
$jit_coerced = 0
t('the included method_missing removed') { [[nil, true, :s, [1]].map { |v| sp_case_range(v) }, $jit_coerced] }
class Hash
  def respond_to?(name, priv = false)
    $jit_coerced += 1 if name == :coerce
    super
  end
end
$jit_coerced = 0
t('Hash#respond_to? defined') { [sp_case_range({ a: 1 }), sp_case_range([1]), $jit_coerced] }
warm { |i| sp_case_range(JIT_RANGE_WARM[i % JIT_RANGE_WARM.size]) }
$jit_coerced = 0
t('Hash#respond_to? defined, hot again') { [sp_case_range({ a: 1 }), sp_case_range([1]), $jit_coerced] }
class Hash
  remove_method :respond_to?
end
$jit_coerced = 0
t('Hash#respond_to? removed') { [sp_case_range({ a: 1 }), $jit_coerced] }
class Array
  alias_method :__jit_matrix_aeq, :==
  def ==(o)
    $jit_eq += 1 if o.is_a?(Integer)
    __jit_matrix_aeq(o)
  end
end
t('Array#== redefined') { [[[1], [], { 1 => 2 }, 1..2].map { |v| sp_case_lit(v) }, $jit_eq] }
warm { |i| sp_case_lit(JIT_LIT_WARM[i % JIT_LIT_WARM.size]) }
$jit_eq = 0
t('Array#== redefined, hot again') { [[[1], [], { 1 => 2 }, 1..2].map { |v| sp_case_lit(v) }, $jit_eq] }
class Array
  alias_method :==, :__jit_matrix_aeq
end
$jit_eq = 0
t('Array#== restored') { [[[1], [], { 1 => 2 }].map { |v| sp_case_lit(v) }, $jit_eq] }
jit_eq_hash = { 1 => 2 }
def jit_eq_hash.==(o)
  $jit_eq += 1
  o == 2 || super
end
t('a Hash with a singleton ==') { [sp_case_lit(jit_eq_hash), sp_case_lit({ 1 => 2 }), $jit_eq] }
warm { |i| sp_case_lit(JIT_LIT_WARM[i % JIT_LIT_WARM.size]) }
$jit_eq = 0
t('a Hash with a singleton ==, hot again') { [sp_case_lit(jit_eq_hash), sp_case_lit({ 1 => 2 }), $jit_eq] }
class Range
  def ==(o)
    o == 0 || super
  end
end
t('Range#== redefined') { [sp_case_lit(1..2), sp_case_lit(3..4), sp_case_lit([1])] }
warm { |i| sp_case_lit(JIT_LIT_WARM[i % JIT_LIT_WARM.size]) }
t('Range#== redefined, hot again') { [sp_case_lit(1..2), sp_case_lit(3..4), sp_case_lit([1])] }
class Range
  remove_method :==
end
t('Range#== removed') { [sp_case_lit(1..2), sp_case_lit([1])] }
t('<=> of an Integer and objects without #coerce') { [1 <=> nil, 1 <=> "a", 1 <=> :a, 1.5 <=> [1], "a" <=> 1, (1 + nil rescue $!.class), (1 < "a" rescue $!.class)] }
class String
  alias_method :__jit_matrix_streqq, :===
  def ===(o)
    o == :foo ? true : __jit_matrix_streqq(o)
  end
end
t('String#=== redefined') { [sp_case_str(:foo), sp_case_str("bar")] }
class String
  alias_method :===, :__jit_matrix_streqq
end
t('String#=== restored') { [sp_case_str(:foo), sp_case_str("bar")] }
class Float
  include JitCaseMod
end
t('a module included into Float') { [sp_case_class(2.0), sp_case_float(2.5)] }
Object.send(:remove_const, :JIT_CASE_CLS)
JIT_CASE_CLS = Integer
t('a when constant reassigned') { [sp_case_const("a"), sp_case_const(1)] }
warm { |i| sp_case_const(["a", 1][i % 2]) }
t('a when constant reassigned, hot again') { [sp_case_const("a"), sp_case_const(1)] }

# the methods the literal whens' === call in turn, redefined
def sp_case_mixed(v)
  case v
  when String then 1
  when :x then 2
  when 1 then 3
  when 1..3 then 4
  when "s" then 5
  when nil then 6
  else 7
  end
end
class JitEqAll
  def ==(o) = true
end
class JitEqRaise
  def ==(o) = raise("boom")
end
class JitToStr
  def to_str = "s"
  def ==(o) = (o == "s" || o == 1)
end
warm { |i| sp_case_mixed([1, "s", :x, nil, 2.5][i % 5]) }
t('objects with their own ==') { [sp_case_mixed(JitEqAll.new), (sp_case_mixed(JitEqRaise.new) rescue $!.message)] }
t('an object with to_str') { sp_case_mixed(JitToStr.new) }
t('Floats, Bignums and Int64s against literal whens') { [sp_case_mixed(1.0), sp_case_mixed(1.5), sp_case_mixed(3.0), sp_case_mixed(3.5), sp_case_mixed(2**70), sp_case_mixed(2**40)] }
t('Rational and Complex against literal whens') { [sp_case_mixed(1r), sp_case_mixed(1i), sp_case_mixed(Rational(3, 2))] }
class Integer
  alias_method :__jit_matrix_ieq, :==
  def ==(o) = (o == :int_eq_magic) ? true : __jit_matrix_ieq(o)
end
t('Integer#== redefined') { [sp_case_mixed(:int_eq_magic), sp_case_mixed(1)] }
class Integer
  alias_method :==, :__jit_matrix_ieq
end
t('Integer#== restored') { [sp_case_mixed(:int_eq_magic), sp_case_mixed(1)] }
warm { |i| sp_case_mixed([1, "s", :x, nil, 2.5][i % 5]) }
class Float
  alias_method :__jit_matrix_feq, :==
  def ==(o) = (o == 1) ? :never : __jit_matrix_feq(o)
end
t('Float#== redefined') { [sp_case_mixed(1.0), sp_case_mixed(1)] }
class Float
  alias_method :==, :__jit_matrix_feq
end
class Integer
  alias_method :__jit_matrix_icmp, :<=>
  def <=>(o) = (o == 77) ? 0 : __jit_matrix_icmp(o)
end
t('Integer#<=> redefined') { [sp_case_mixed(77), sp_case_mixed(2)] }
class Integer
  alias_method :<=>, :__jit_matrix_icmp
end
t('Integer#<=> restored') { [sp_case_mixed(77), sp_case_mixed(2)] }
class NilClass
  def ===(o) = (o == :nil_magic) || super
end
t('NilClass#=== defined') { [sp_case_mixed(:nil_magic), sp_case_mixed(nil)] }
class NilClass
  remove_method :===
end
class Symbol
  def to_str = "s"
end
t('Symbol#to_str defined') { [sp_case_mixed(:y), sp_case_mixed(:x)] }
class Symbol
  remove_method :to_str
end
t('Symbol#to_str removed') { [sp_case_mixed(:y), sp_case_mixed(:x)] }

# a when after the one that matches is not evaluated
def sp_case_once(v)
  case v
  when Integer then :int
  when JitMatrixUndefinedConstant then :never
  else :other
  end
end
warm { |i| sp_case_once(i) }
t('a when constant that is never reached') { [sp_case_once(1), (sp_case_once("x") rescue $!.class)] }

h = { 1 => :one, 2 => :two, :s => :sym, "str" => :string, nil => :nil_key, 2.5 => :float }
warm { |i| sp_hget(h, i % 4) }
t('Hash#[]') { [1, 2, 3, :s, "str", nil, 2.5, 2**40].map { |k| sp_hget(h, k) } }
t('Hash#[] defaults') { [sp_hget(Hash.new(0), 1), sp_hget(Hash.new { |hh, k| k * 2 }, 21), sp_hget(Hash.new(:d).compare_by_identity, 1)] }
ih = {}.compare_by_identity
ks = "k"
ih[ks] = :same; ih[1] = :one
t('Hash#[] compare_by_identity') { [sp_hget(ih, ks), sp_hget(ih, "k"), sp_hget(ih, 1)] }
t('Hash#[] frozen, subclass, Array, other') { [sp_hget(h.dup.freeze, 1), sp_hget(JitHash[1, 2], 1), sp_hget([5, 6, 7], -1), sp_hget("abc", 1)] }
warm { |i| sp_hkey(h, i % 4) }
t('Hash#key?') { [1, 3, :s, "str", nil, 2.5].map { |k| sp_hkey(h, k) } }
warm { |i| sp_hfetch(h, i % 4) }
t('Hash#fetch with a default') { [1, 3, "str", nil].map { |k| sp_hfetch(h, k) } }
warm { |i| sp_hfetch1(h, 1 + i % 2) }
t('Hash#fetch') { [sp_hfetch1(h, 1), sp_hfetch1(h, :s)] }
t('Hash#fetch of a missing key') { sp_hfetch1(h, 99) }
counts = Hash.new(0)
warm { |i| sp_hincr(counts, i % 3, 3) }
t('Hash#[] += 1') { counts }
t('Hash#[] += 1, then the body deopts') { [sp_hincr(counts, 0, 2**40), sp_hincr(counts, 7, 2**40), counts] }
t('Hash#[] += 1 on a frozen hash') { sp_hincr(counts.dup.freeze, 0, 1) }
t('Hash#[] += 1 on a Float element') { c = { 0 => 1.5 }; sp_hincr(c, 0, 2); c }
hs = {}
warm { |i| sp_hset(hs, i % 5, i) }
t('Hash#[]=') { hs }
key = "fresh"
t('Hash#[]= of a String key') { sp_hset(hs, key, 1); [hs.keys.last.frozen?, hs.keys.last.equal?(key), key.frozen?] }
t('Hash#[]= frozen, identity, subclass') { ihs = {}.compare_by_identity; sp_hset(ihs, key, 2); [(sp_hset({}.freeze, 1, 1) rescue $!.class), ihs.keys[0].equal?(key), sp_hset(JitHash.new, 1, 2)] }
a = [10, 20, 30]
warm { |i| sp_aget(a, i % 5 - 2) }
t('Array#[]') { (-4..4).map { |i| sp_aget(a, i) } }
t('Array#[] of a subclass, a long index') { [sp_aget(JitArr[1, 2], 0), sp_aget(a, 2**40)] }
warm { |i| sp_aset(a, i % 3, i) }
t('Array#[]=') { [sp_aset(a, 1, :x), sp_aset(a, -1, :y), sp_aset(a, 5, :z), a] }
t('Array#[]= frozen') { sp_aset([1].freeze, 0, 2) }
warm { |i| sp_hsum({ 0 => 1, 1 => 2, 2 => 3, 3 => 4 }, [1, 1, 1, 1]) }
t('Hash and Array reads in a loop') { sp_hsum({ 0 => 1, 1 => 2, 2 => 3, 3 => 4 }, [1, 1, 1, 1]) }
t('... of a Float element') { sp_hsum({ 0 => 1, 1 => 2.5, 2 => 3, 3 => 4 }, [1, 1, 1, 1]) }
warm { |i| sp_asum([1, 2, 3, i], 4) }
t('Array sum') { [sp_asum([1, 2, 3, 4], 4), sp_asum([1, 2.5, 3], 3), sp_asum([2**62, 2**62], 2), sp_asum([1, nil], 2)] }
warm { |i| sp_scaled([1, 2, 3, i], i % 4) }
t('Array elements times a Float') { [sp_scaled([1, 2.5, 4], 0), sp_scaled([1, 2.5, 4], 1), sp_scaled([1, 2.5, 4], 2), sp_scaled([2**62], 0), sp_scaled(["x"], 0)] }
class Hash
  alias_method :__jit_matrix_aref, :[]
  def [](k)
    k == :magic_key ? :redefined : __jit_matrix_aref(k)
  end
end
t('Hash#[] redefined') { [sp_hget(h, :magic_key), sp_hget(h, 1)] }
warm { |i| sp_hget(h, i % 4) }
t('Hash#[] redefined, hot again') { [sp_hget(h, :magic_key), sp_hget(h, 1)] }
class Hash
  alias_method :[], :__jit_matrix_aref
end
t('Hash#[] restored') { [sp_hget(h, :magic_key), sp_hget(h, 1)] }
class Hash
  alias_method :__jit_matrix_default, :default
  def default(*k)
    k == [:nope] ? :redefined_default : __jit_matrix_default(*k)
  end
end
t('Hash#default redefined') { [sp_hget(h, :nope), sp_hget(Hash.new(0), 1), sp_hget(Hash.new(0), :nope)] }
class Hash
  alias_method :default, :__jit_matrix_default
end
t('Hash#default restored') { [sp_hget(h, :nope), sp_hget(Hash.new(0), 1)] }
class Hash
  alias_method :__jit_matrix_aset, :[]=
  def []=(k, v)
    __jit_matrix_aset(k, [:wrapped, v])
  end
end
t('Hash#[]= redefined') { x = {}; sp_hset(x, 1, 2); x }
class Hash
  alias_method :[]=, :__jit_matrix_aset
end
t('Hash#[]= restored') { x = {}; sp_hset(x, 1, 2); x }
class Hash
  alias_method :__jit_matrix_key, :key?
  def key?(k)
    k == :magic_key ? :redefined_key : __jit_matrix_key(k)
  end
end
t('Hash#key? redefined') { [sp_hkey(h, :magic_key), sp_hkey(h, 1)] }
warm { |i| sp_hkey(h, i % 4) }
t('Hash#key? redefined, hot again') { [sp_hkey(h, :magic_key), sp_hkey(h, 1)] }
class Hash
  alias_method :key?, :__jit_matrix_key
end
t('Hash#key? restored') { [sp_hkey(h, :magic_key), sp_hkey(h, 1)] }
class Hash
  alias_method :__jit_matrix_fetch, :fetch
  def fetch(k, *d)
    k == :magic_key ? :redefined_fetch : __jit_matrix_fetch(k, *d)
  end
end
t('Hash#fetch redefined') { [sp_hfetch(h, :magic_key), sp_hfetch(h, 1), sp_hfetch1(h, :magic_key)] }
warm { |i| sp_hfetch(h, i % 4) }
t('Hash#fetch redefined, hot again') { [sp_hfetch(h, :magic_key), sp_hfetch(h, 3)] }
class Hash
  alias_method :fetch, :__jit_matrix_fetch
end
t('Hash#fetch restored') { [sp_hfetch(h, :magic_key), sp_hfetch(h, 3)] }
class Integer
  alias_method :__jit_matrix_ieql, :eql?
  def eql?(o)
    o == :magic_eql || __jit_matrix_ieql(o)
  end
end
t('Integer#eql? redefined') { [sp_hget(h, 1), sp_hkey(h, 2), sp_hget({ 1 => :one }, 1)] }
class Integer
  alias_method :eql?, :__jit_matrix_ieql
end
# the methods above have been redefined under them and given up on: fresh ones
def sp_hget2(h, k)
  h[k]
end

def sp_hkey2(h, k)
  h.key?(k)
end

def sp_hfetch2(h, k)
  h.fetch(k, :none)
end

def sp_hset2(h, k, v)
  h[k] = v
end

def sp_hincr2(h, k, n)
  h[k] += 1
  n * n
end
warm { |i| sp_hget2(h, i % 4); sp_hkey2(h, i % 4); sp_hfetch2(h, i % 4); sp_hset2(hs, i % 5, i); sp_hincr2(counts, i % 3, 3) }
class JitDefaultHash < Hash; end
dsub = JitDefaultHash.new { |hh, k| "made #{k}" }
dsub[1] = :one
t('a Hash subclass with a default proc') { [sp_hget2(dsub, 1), sp_hget2(dsub, 2), sp_hkey2(dsub, 2), sp_hfetch2(dsub, 2), sp_hset2(dsub, 3, 4), dsub] }
dlate = { 1 => :one }
t('default= and default_proc= on a hash the code has seen') { a = [sp_hget2(dlate, 2)]; dlate.default = :late; a << sp_hget2(dlate, 2); dlate.default_proc = proc { |hh, k| k * 10 }; a << sp_hget2(dlate, 2) << sp_hfetch2(dlate, 2); a }
t('a default proc that writes the hash') { dw = Hash.new { |hh, k| hh[k] = k * 3 }; [sp_hget2(dw, 2), sp_hincr2(dw, 5, 2), dw] }
t('a mutable default value') { dv = Hash.new([]); x = sp_hget2(dv, 1); x << 1; [dv, dv.default, sp_hget2(dv, 2)] }
t('keys: nil, Float, Bignum, Array, Int64') { x = {}; [nil, 2.5, 2**70, [1], 2**40, -0.0].each_with_index { |k, j| sp_hset2(x, k, j) }; [nil, 2.5, 2**70, [1], 2**40, 0.0, 1.0].map { |k| [sp_hget2(x, k), sp_hkey2(x, k)] } }
t('keys: 1 and 1.0') { x = { 1 => :i, 1.0 => :f }; [sp_hget2(x, 1), sp_hget2(x, 1.0), sp_hkey2(x, 1.0), sp_hfetch2(x, 2.0)] }
t('keys: String encodings') { x = { "é" => 1, "a" => 2 }; [sp_hget2(x, "é"), sp_hget2(x, "é".b), sp_hget2(x, "a".b), sp_hkey2(x, "é".b), sp_hget2(x, "a".encode("UTF-16LE"))] }
t('keys: String subclass') { x = { "a" => 1 }; k = JitStr.new("a"); sp_hset2(x, JitStr.new("b"), 2); [sp_hget2(x, k), sp_hkey2(x, k), x.keys.map(&:class), x.keys[0].frozen?] }
t('keys: Symbol and String of one name') { x = { a: 1, "a" => 2 }; [sp_hget2(x, :a), sp_hget2(x, "a"), sp_hkey2(x, :b)] }
t('existing String key kept') { k1 = "q".dup; x = { k1 => 1 }; sp_hset2(x, "q".dup, 2); [x, x.keys[0].equal?(k1)] }
t('an update keeps the order') { x = { a: 1, b: 2 }; sp_hset2(x, :a, 3); sp_hset2(x, :c, 4); x.to_a }

# outlined loops (OSR): a while loop that runs long enough to be replaced
def osr_case_mix(vals, n)
  counts = [0, 0, 0, 0, 0, 0]
  i = 0
  while i < n
    case vals[i % vals.size]
    when Integer then counts[0] += 1
    when String then counts[1] += 1
    when Float, Comparable then counts[2] += 1
    when :s then counts[3] += 1
    when nil, false then counts[4] += 1
    else counts[5] += 1
    end
    i += 1
  end
  counts
end

def osr_hash_count(keys, n, big, huge)
  h = Hash.new(0)
  acc = 0
  i = 0
  while i < n
    h[keys[i % keys.size]] += 1
    x = (i == big) ? huge : 1
    acc += x * 4
    i += 1
  end
  [h, acc]
end

def osr_hash_read(h, n)
  s = 0
  miss = 0
  i = 0
  while i < n
    v = h[i % 7]
    if v
      s += v
    else
      miss += 1
    end
    i += 1
  end
  [s, miss]
end

def osr_fetch_key(h, n)
  s = 0
  i = 0
  while i < n
    s += h.fetch(i % 5, 100) if h.key?(i % 3)
    i += 1
  end
  s
end

puts
puts '--- 7c. the same in outlined loops ---'
t('OSR case of each kind') { osr_case_mix(JIT_MIX, 20000) }
t('OSR case of extended strings and objects') { osr_case_mix([ext, eobj, 1, "a"], 20000) }
t('OSR Hash#[] += 1') { osr_hash_count([1, 2, 3, :a, "b"], 20000, -1, 2**62) }
t('OSR Hash#[] += 1, deopt mid-loop') { osr_hash_count([1, 2, 3, :a, "b"], 20000, 7001, 2**62) }
t('OSR Hash#[] += 1, odd keys') { osr_hash_count([1, JitPlain, 2.5, nil, [1]], 20000, -1, 2**62) }
t('OSR Hash#[] reads') { osr_hash_read({ 0 => 1, 1 => 2, 3 => 4, 5 => 6 }, 20000) }
t('OSR Hash#[] reads, default') { osr_hash_read(Hash.new(3), 20000) }
t('OSR Hash#[] reads, default proc') { osr_hash_read(Hash.new { |hh, k| k }, 20000) }
t('OSR Hash#key? and #fetch') { osr_fetch_key({ 0 => 1, 1 => 2, 4 => 7 }, 20000) }
class Symbol
  alias_method :__jit_matrix_seqq2, :===
  def ===(o)
    o == :t ? true : __jit_matrix_seqq2(o)
  end
end
t('OSR case after Symbol#=== redefined') { osr_case_mix([:t, :s, 1], 20000) }
class Symbol
  alias_method :===, :__jit_matrix_seqq2
end
t('OSR case after Symbol#=== restored') { osr_case_mix([:t, :s, 1], 20000) }

# element writes a deopt takes back (JitUndoLog): the generic body re-runs the call, so every write
# of the specialized run has to be gone first - appends that grew the hash, writes made by an inner
# self-call, Array and Hash writes mixed, the frozen copy of a String key
def sp_hfill(h, base, m)
  h[base] = 1
  h[base + 1] = 2
  h[base + 2] = 3
  m * m
end

def sp_hrec(h, n, m)
  h[n] = n
  if n > 0
    sp_hrec(h, n - 1, m)
  else
    m * m
  end
end

def sp_hswap(h, a, k, m)
  h[k] = a[0]
  a[0] = k
  h[k] += 1
  m * m
end

def sp_hstr(h, k, m)
  h[k] = 1
  m * m
end

def osr_hfill(h, n, at, huge)
  acc = 0
  i = 0
  while i < n
    h[i] = i
    acc += (i == at ? huge : 1) * 4
    i += 1
  end
  acc
end

module JitLateModule; end

def sp_case_late(v)
  case v
  when JitLateModule then :late
  when Comparable then :cmp
  else :none
  end
end

puts
puts '--- 7d. element writes taken back by a deopt; modules included late ---'
hf = {}
warm { |i| sp_hfill(hf, i * 3, 3) }
t('appends that grow the hash, then a deopt') { r = (0...40).map { |j| sp_hfill(hf, 1000 + j * 3, (j % 7 == 3) ? 2**40 : 2) }; [r.uniq, hf.size, hf.to_a.last(4), hf.keys.each_slice(37).map(&:first)] }
t('... and lookups after the take-backs') { [(0..1200).count { |k| hf.key?(k) }, hf[1110], hf[1111], hf[359]] }
hrec = {}
warm { |i| sp_hrec(hrec, i % 5, 3) }
t('writes of inner self-calls, deopt in the innermost') { x = {}; [sp_hrec(x, 6, 2**40), x.to_a] }
t('... into a hash that has keys') { [sp_hrec(hrec, 9, 2**40), hrec.to_a] }
warm { |i| sp_hswap({}, [1], i, 3) }
t('Array and Hash writes, then a deopt') { h2 = { 5 => 1 }; a2 = [7]; [sp_hswap(h2, a2, 5, 2**40), h2.dup, a2.dup, sp_hswap(h2, a2, 6, 2**40), h2, a2] }
warm { |i| sp_hstr({}, "k#{i}", 3) }
t('a String key copied, then a deopt') { k = "zz".dup; x = {}; [sp_hstr(x, k, 2**40), x.keys[0].frozen?, x.keys[0].equal?(k), k.frozen?, x] }
t('a value replaced while the hash is iterated') { x = { 1 => 2, 3 => 4 }; x.each { |kk, vv| sp_hstr(x, 1, 2) }; x }
t('OSR appends that grow the hash, deopt mid-loop') { x = {}; [osr_hfill(x, 30000, 20001, 2**62), x.size, x.keys.first(3), x.keys.last(3), x[20001], x[20002]] }
lv = [1, "s", 2.5, :sym, nil, [1], { a: 1 }, 1..2]
warm { |i| sp_case_late(lv[i % lv.size]) }
t('case of a module before it is included') { lv.map { |v| sp_case_late(v) } }
class String; include JitLateModule; end
t('... included into String') { lv.map { |v| sp_case_late(v) } }
warm { |i| sp_case_late(lv[i % lv.size]) }
class Array; include JitLateModule; end
t('... and into Array, hot again') { lv.map { |v| sp_case_late(v) } }
sx = "ext".dup.extend(Comparable)
ox = Object.new.extend(JitLateModule)
t('... on extended objects') { [sp_case_late(sx), sp_case_late(ox), sp_case_late(Object.new)] }

# Array#fetch: the fetch intrinsic answers for an exact Array as well as an exact Hash, so a
# receiver the profile cannot see (an instance variable) does not deopt on every call
class JitHolder
  def initialize(l) = @list = l
  def [](i)
    @list.fetch(i)
  end
  def dflt(i)
    @list.fetch(i, :dflt)
  end
end

def sp_afetch(a, i)
  a.fetch(i)
end

def sp_afetch_d(a, i)
  a.fetch(i, :none)
end

def sp_afsum(a, n)
  s = 0
  i = 0
  while i < n
    s += a.fetch(i)
    i += 1
  end
  s
end

def osr_afetch(a, n, d)
  s = 0
  i = 0
  while i < n
    s += a.fetch(i % (a.size + 2), d)
    i += 1
  end
  s
end

puts
puts '--- 7e. Array#fetch ---'
fa = [10, 20, 30]
fho = JitHolder.new(fa)
fhh = JitHolder.new({ 0 => :h0, "k" => :hk })
warm { |i| fho[i % 3]; fho.dflt(i % 5); sp_afetch(fa, i % 3); sp_afetch_d(fa, i % 5 - 2) }
t('Array#fetch through an instance variable') { [fho[0], fho[-1], fho[2], (fho[3] rescue $!.class), (fho[-4] rescue $!.class), fho.dflt(7), fho.dflt(-3)] }
t('... a Hash through the same one') { [fhh[0], fhh["k"], (fhh[1] rescue $!.class), fhh.dflt(1), fhh.dflt("k")] }
t('Array#fetch of odd indexes') { [sp_afetch(fa, 1.9), (sp_afetch(fa, nil) rescue $!.class), (sp_afetch(fa, "1") rescue $!.class), (sp_afetch_d(fa, 2**70) rescue $!.class)] }
t('Array#fetch with a default') { (-5..5).map { |i| sp_afetch_d(fa, i) } }
t('Array#fetch of a subclass, a frozen and an empty array') { [sp_afetch(JitArr[1, 2], 1), sp_afetch([5].freeze, 0), sp_afetch_d([], 0), (sp_afetch([], 0) rescue $!.class)] }
warm { |i| sp_afsum([1, 2, 3, i], 4) }
t('Array#fetch in a loop') { [sp_afsum([1, 2, 3, 4], 4), (sp_afsum([1, 2], 3) rescue $!.class), sp_afsum([1, 2.5], 2), (sp_afsum([1, nil], 2) rescue $!.class)] }
t('OSR Array#fetch with a default') { [osr_afetch((0...100).to_a, 30000, 7), osr_afetch([1, 2, 3], 30000, 2.5)] }
class Array
  alias_method :__jit_matrix_afetch, :fetch
  def fetch(i, *d)
    i == 99 ? :redefined_fetch : __jit_matrix_afetch(i, *d)
  end
end
t('Array#fetch redefined') { [sp_afetch(fa, 99), sp_afetch(fa, 0), fho[99], fho[1], sp_afetch_d(fa, 99)] }
warm { |i| fho[i % 3]; sp_afetch(fa, i % 3) }
t('Array#fetch redefined, hot again') { [sp_afetch(fa, 99), sp_afetch(fa, 0), fho[99]] }
class Array
  alias_method :fetch, :__jit_matrix_afetch
end
t('Array#fetch restored') { [(sp_afetch(fa, 99) rescue $!.class), sp_afetch(fa, 0), (fho[99] rescue $!.class)] }
warm { |i| fho[i % 3]; sp_afetch(fa, i % 3) }
# last for Array#fetch: a singleton fetch on one Array stops the intrinsic answering for Arrays
sfa = [1, 2]
def sfa.fetch(i) = :singleton_fetch
t('Array#fetch with a singleton fetch') { [sp_afetch(sfa, 0), sp_afetch(fa, 0), fho[0], JitHolder.new(sfa)[0]] }
warm { |i| fho[i % 3]; sp_afetch(fa, i % 3) }
t('... hot again') { [sp_afetch(sfa, 0), sp_afetch(fa, 0), fho[0], fhh[0], JitHolder.new(sfa)[0]] }

# a module that one Hash is extended with: its key? and fetch stop those intrinsics for good (Hash#[] stays)
def sp_hget3(h, k)
  h[k]
end

def sp_hkey3(h, k)
  h.key?(k)
end

def sp_hfetch3(h, k)
  h.fetch(k, :none)
end
warm { |i| sp_hget3(h, i % 4); sp_hkey3(h, i % 4); sp_hfetch3(h, i % 4) }
he = { 1 => :a, 2 => :b }
he.extend(Module.new {
  def key?(k) = :module_key
  def fetch(k, *d) = :module_fetch
})
t('Hash#key? and #fetch from an extending module') { [sp_hget3(he, 1), sp_hkey3(he, 1), sp_hfetch3(he, 1), sp_hget3(h, 1), sp_hkey3(h, 1), sp_hfetch3(h, 1)] }
warm { |i| sp_hget3(h, i % 4); sp_hkey3(h, i % 4); sp_hfetch3(h, i % 4) }
t('... hot again') { [sp_hget3(he, 1), sp_hkey3(he, 1), sp_hfetch3(he, 1), sp_hget3(h, 1), sp_hkey3(h, 1), osr_fetch_key(he, 20000)] }
# a String key with a singleton eql?, which the lookup asks
seql = "str".dup
def seql.eql?(o) = false
warm { |i| sp_hget3(h, "str") }
t('a String key with a singleton eql?') { [sp_hget3(h, seql), sp_hget3(h, "str"), sp_hget3(h, "str".dup)] }

# last: a singleton [] on one Hash stops the Hash intrinsics for good (Array's stay)
warm { |i| sp_hget(h, i % 4) }
sh = { 1 => :a }
def sh.[](k)
  :singleton_element
end
t('Hash#[] with a singleton []') { [sp_hget(sh, 1), sp_hget(h, 1)] }
warm { |i| sp_hget(h, i % 4) }
warm { |i| sp_aget(a, i % 3) }
t('Hash#[] with a singleton [], hot again') { [sp_hget(sh, 1), sp_hget(h, 1), sp_aget([4, 5], 1)] }
t('OSR Hash#[] reads after a singleton []') { [osr_hash_read({ 0 => 1, 1 => 2 }, 20000), osr_hash_read(sh, 20000)] }

# ---------------------------------------------------------------- 7f. Integer vs Float, stores, tracing

# An Integer and a Float compare exactly (rb_integer_float_cmp): a long past 2**53 is not the
# double it rounds to. x is profiled as a long, f as a Float.
def sp_lf_cmp(x, f)
  r = 0
  r += 1 if x == f
  r += 2 if x < f
  r += 4 if x <= f
  r += 8 if x > f
  r += 16 if x >= f
  r += 32 if x != f
  r += 64 if f == x
  r += 128 if f < x
  r += 256 if f <= x
  r += 512 if f > x
  r += 1024 if f >= x
  r += 2048 if f != x
  r
end

# the same with the Integer an element, which is speculated to be a number next to the Float
def sp_ef_cmp(a, f)
  r = 0
  r += 1 if a[0] == f
  r += 2 if a[0] < f
  r += 4 if a[0] <= f
  r += 8 if a[0] > f
  r += 16 if a[0] >= f
  r += 32 if a[0] != f
  r += 64 if f == a[0]
  r += 128 if f < a[0]
  r += 256 if f <= a[0]
  r += 512 if f > a[0]
  r += 1024 if f >= a[0]
  r += 2048 if f != a[0]
  r
end

# an outlined loop with an object slot (r holds nil when it turns hot) comparing a long to a Float
def osr_lf_cmp(big, fl, n)
  i = 0
  r = nil
  while i < n
    x = big + (i & 0)
    r = (x == fl)
    i += 1
  end
  r
end

# a parameter and a local only ever stored: carried as the boxes they are
def sp_store_param(a, n, v)
  i = 0
  while i < n
    a[i] = v
    i += 1
  end
  a
end

def sp_store_local(a, n, k)
  i = 0
  while i < n
    v = i * k
    a[i] = v
    i += 1
  end
  a
end

def sp_hash_ii(h, n)
  i = 0
  while i < n
    h[i] = i
    i += 1
  end
  h
end

def osr_store(a, n, v)
  i = 0
  while i < n
    a[i % 7] = v
    i += 1
  end
  a
end

def osr_store_local(a, n, k)
  i = 0
  while i < n
    w = i * k
    a[i % 7] = w
    i += 1
  end
  a
end

puts
puts '--- 7f. Integer against Float, values only stored, TracePoint and Coverage ---'
warm { |i| sp_lf_cmp(2**40 + i, 1.5 * i); sp_ef_cmp([i * 10**12], 1.5) }
LF_PAIRS = [[2**53 + 1, 2.0**53], [2**53 + 1, 2.0**53 + 2], [-(2**53 + 1), -(2.0**53)], [2**53, 2.0**53], [2**62, 2.0**62],
  [2**62 + 1, 2.0**62], [2**63 - 1, 2.0**63], [-2**63, -(2.0**63)], [-2**63 + 1, -(2.0**63)], [2**53 + 1, 1e15 + 0.5],
  [-(2**53 + 1), -1e15 - 0.5], [2**53 + 1, Float::NAN], [2**53 + 1, Float::INFINITY], [2**53 + 1, -Float::INFINITY],
  [2**40, 2.0**40], [2**40, 2.0**40 + 0.5], [5, 5.0], [5, Float::NAN], [2**62, 1e300], [-2**62, -1e300]]
t('Integer <=> Float, the Integer a long') { LF_PAIRS.map { |x, f| sp_lf_cmp(x, f) } }
t('... the Integer an element') { LF_PAIRS.map { |x, f| sp_ef_cmp([x], f) } }
t('... the element a Float or not a number') { [sp_ef_cmp([2.0**53], 2.0**53), sp_ef_cmp([Float::NAN], 1.0), (sp_ef_cmp([nil], 1.0) rescue $!.class), (sp_ef_cmp([2**70], 1.0) rescue $!.class)] }
t('OSR long == Float, r nil at entry') { [osr_lf_cmp(2**53 + 1, 2.0**53, 30000), osr_lf_cmp(2**53, 2.0**53, 30000), osr_lf_cmp(7, 7.0, 30000)] }
warm { |i| sp_store_param([0, 0, 0], 3, i * 1000); sp_store_local([0, 0, 0], 3, i); sp_hash_ii({}, 3) }
t('a parameter only stored') { [sp_store_param([0, 0], 2, 5000), sp_store_param([0, 0], 2, 2.5), sp_store_param([0], 1, "s"), sp_store_param([0], 1, nil), sp_store_param([0], 1, 2**70)] }
t('a local only stored') { [sp_store_local([0, 0, 0], 3, 7), sp_store_local([0, 0, 0], 3, 2**40), sp_store_local([0, 0], 2, 1.5)] }
t('h[i] = i') { h2 = sp_hash_ii({}, 2000); [h2.size, h2[1999], h2.keys.sum, h2.values.sum, h2.keys.all? { |k| h2[k] == k }] }
t('OSR a slot only stored') { [osr_store([0] * 7, 30000, 5000), osr_store([0] * 7, 30000, 2.5), osr_store([0] * 7, 30000, :s)] }
t('OSR a local only stored') { [osr_store_local([0] * 7, 30000, 3), osr_store_local([0] * 7, 30000, 2**40), osr_store_local([0] * 7, 30000, 0.5)] }
# A store of a value the method only stores allocates no box of its own (IronRuby: the CLR's
# allocation counter; CRuby just runs the block).
def jit_alloc
  return (yield; :ok) unless defined?(System::GC)
  b0 = System::GC.GetAllocatedBytesForCurrentThread
  yield
  b = System::GC.GetAllocatedBytesForCurrentThread - b0
  b < 8000 ? :ok : b
end
sa = [0] * 1000
warm { |i| sp_store_param(sa, 1000, 5000) }
osr_store(sa, 30000, 5000)
# (the outlined loop is entered at once now; 1000 iterations keep the generic loop's i among the cached boxes)
t('1000 stores of a parameter allocate nothing per store') { [jit_alloc { sp_store_param(sa, 1000, 5000) }, jit_alloc { osr_store(sa, 1000, 5000) }] }

# A core method made private before a method using it turns hot: called with a receiver, it raises.
def sp_priv_get(h, k)
  h[k]
end

def sp_priv_key(h, k)
  h.key?(k)
end

def sp_priv_add(a, b)
  a + b
end
pr = []
class Hash
  private :[], :key?
end
HOT.times { |i| pr[0] = (sp_priv_get({ 1 => 2 }, 1) rescue $!.class); pr[1] = (sp_priv_key({ 1 => 2 }, 1) rescue $!.class) }
class Hash
  public :[], :key?
end
class Integer
  private :+
end
HOT.times { |i| pr[2] = (sp_priv_add(i, 1) rescue $!.class) }
class Integer
  public :+
end
t('Hash#[], #key? and Integer#+ made private') { pr }
warm { |i| sp_priv_get({ 1 => 2 }, 1); sp_priv_key({ 1 => 2 }, 1); sp_priv_add(i, 1) }
t('... public again, hot') { [sp_priv_get({ 1 => 2 }, 1), sp_priv_key({ 1 => 2 }, 1), sp_priv_add(1, 2)] }

# TracePoint: a method the JIT specialized delivers :call, :line and :return to an enabled TracePoint,
# :c_call and :line alone included (neither of which changes the method version)
def sp_tr_kind(v)
  case v
  when Integer then 1
  else 2
  end
end

def sp_tr_get(h, k)
  h[k]
end

def sp_tr_add(a, b)
  a + b
end

def osr_tr_loop(n)
  i = 0
  s = 0
  while i < n
    s += i
    i += 1
  end
  s
end
TR_NAMES = [:sp_tr_kind, :sp_tr_get, :sp_tr_add, :osr_tr_loop]
th = { 1 => 2 }
warm { |i| sp_tr_kind(i); sp_tr_get(th, 1); sp_tr_add(i, 1) }
osr_tr_loop(30000)
[[:call, :line, :return, :c_call], [:line], [:call], [:c_call]].each do |events|
  counts = Hash.new(0)
  tp = TracePoint.new(*events) do |tr|
    if tr.event == :c_call
      counts[[:c_call, tr.method_id]] += 1 if tr.method_id == :[] && tr.path == __FILE__
    elsif TR_NAMES.include?(tr.method_id) && tr.path == __FILE__
      counts[[tr.event, tr.method_id]] += 1
    end
  end
  tp.enable
  200.times { |i| sp_tr_kind(i); sp_tr_get(th, 1); sp_tr_add(i, 1) }
  osr_tr_loop(3000)
  tp.disable
  t("TracePoint #{events.inspect} on specialized methods") { counts.sort_by(&:inspect) }
  warm { |i| sp_tr_kind(i); sp_tr_get(th, 1); sp_tr_add(i, 1) }
  osr_tr_loop(30000)
end
t('... specialized again') { [sp_tr_kind(1), sp_tr_get(th, 1), sp_tr_add(1, 2), osr_tr_loop(30000)] }

# Coverage: a method in a file loaded under Coverage.start counts its lines whether or not it is hot
require 'coverage'
cov_file = "/tmp/jit-matrix-cov-#{$$}.rb"
File.write(cov_file, <<~RUBY)
  def cov_kind(v)
    case v
    when Integer then 1
    when String then 2
    else 3
    end
  end
  def cov_add(a, b)
    a + b
  end
  def cov_loop(n)
    i = 0
    while i < n
      i += 1
    end
    i
  end
RUBY
Coverage.start(lines: true)
load cov_file
2000.times { |i| cov_kind(i); cov_kind("s"); cov_add(i, 1) }
cov_loop(50000)
t('Coverage of hot methods and loops') { Coverage.result[cov_file][:lines] }
File.delete(cov_file)

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
t('opt default after + redefinition') { [sp_opt_plus(3), sp_opt_plus(1), sp_opt_plus(3, 0)] }
warm { |i| sp_opt_plus(i) }
t('opt default after + redefinition, hot again') { [sp_opt_plus(3), sp_opt_plus(1)] }
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

def sp_case_before_refine(v)
  case v
  when Integer then :int
  when 1..5 then :range
  when :s then :sym
  when String then :str
  else :other
  end
end

def sp_hash_before_refine(h, k)
  h[k]
end

puts
puts '--- 9. a refinement appears (must come last: once any refinement exists the JIT stands down) ---'
warm { |i| sp_before_refine(i) }
warm { |i| sp_case_before_refine([1, "s", :s, 2.0][i % 4]) }
warm { |i| sp_hash_before_refine({ 1 => 2 }, i % 2) }
t('specialized before any refinement') { sp_before_refine(41) }
t('case and Hash#[] specialized before any refinement') { [1, "s", :s, 2.0].map { |v| sp_case_before_refine(v) } + [sp_hash_before_refine({ 1 => 2 }, 1)] }
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

# refinements of the methods case/when and the Hash intrinsics stand for
module JitRefineCase
  refine Integer.singleton_class do
    def ===(o)
      o == :refined_int || super
    end
  end
  refine Hash do
    def [](k)
      k == :refined_key ? :refined_element : super
    end
  end
  refine Symbol do
    def ===(o)
      o == "s" || super
    end
  end
end

class JitRefinedCaseUser
  using JitRefineCase
  def sp_case_refined(v)
    case v
    when Integer then :int
    when :s then :sym
    else :other
    end
  end

  def sp_hash_refined(h, k)
    h[k]
  end

  def osr_refined(h, n)
    i = 0
    s = 0
    while i < n
      s += 1 if h[:refined_key] == :refined_element
      s += 2 if (case :refined_int when Integer then true else false end)
      i += 1
    end
    s
  end
end
jrc = JitRefinedCaseUser.new
warm { |i| jrc.sp_case_refined([1, :s, "s", :refined_int][i % 4]); jrc.sp_hash_refined({ 1 => 2 }, i % 2) }
t('case/when where === is refined') { [1, :s, "s", :refined_int, 2.0].map { |v| jrc.sp_case_refined(v) } }
t('Hash#[] where it is refined') { [jrc.sp_hash_refined({ 1 => 2 }, 1), jrc.sp_hash_refined({ 1 => 2 }, :refined_key)] }
t('an outlined loop where both are refined') { jrc.osr_refined({}, 20000) }
t('outside the refined scope') { [:refined_int, "s", 1].map { |v| sp_case_before_refine(v) } + [sp_hash_before_refine({}, :refined_key)] }
warm { |i| sp_case_before_refine([1, "s", :s, 2.0][i % 4]); sp_hash_before_refine({ 1 => 2 }, i % 2) }
t('outside the refined scope, hot again') { [:refined_int, "s", 1].map { |v| sp_case_before_refine(v) } + [sp_hash_before_refine({}, :refined_key)] }


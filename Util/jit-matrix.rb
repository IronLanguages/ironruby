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
  a > 0 && a * 2**40
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
t('&& with an overflowing right operand') { [sp_or_overflow(2**30), sp_or_overflow(-1)] }
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

def sp_opt_plus(a, b = 1 + a)
  b
end
warm { |i| sp_opt_plus(i) }

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


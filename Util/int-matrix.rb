# Differential probe for the Fixnum/Bignum -> Integer unification.
#
#   ruby  int-matrix.rb            > /tmp/m-cruby.txt
#   python3 int-matrix.py /tmp/m-ir.txt     # drives ir, surviving stack overflows
#   diff /tmp/m-cruby.txt /tmp/m-ir.txt
#
# Every line is "<n>\t<expr> => <result or error class: message>".  A stack
# overflow kills the whole process rather than raising, so each line is numbered
# and flushed: the driver restarts the script past whatever index died.
#
# ARGV[0] = index to start from (default 0).

RECEIVERS = ['0', '1', '-7', '2**100', '-(2**100)', '2**62']
ARGS = ['1', '3', '-3', '2**100', '2.5', '0', 'Rational(3,2)', '"x"', 'nil', 'Object.new']
BINOPS = %w[+ - * / % ** & | ^ << >> < <= > >= <=> == eql? equal? div divmod modulo quo fdiv remainder gcd lcm coerce]
UNOPS = %w[-@ ~ abs to_s to_i to_f to_r hash size succ pred next zero? odd? even? integer? ord inspect
           numerator denominator floor ceil round truncate]

exprs = []
RECEIVERS.each do |r|
  UNOPS.each do |op|
    exprs << (op == '-@' ? "-(#{r})" : "(#{r}).#{op}")
  end
  BINOPS.each do |op|
    ARGS.each do |a|
      exprs << (op =~ /\A[a-z]/ ? "(#{r}).#{op}(#{a})" : "(#{r}) #{op} (#{a})")
    end
  end
end
%w[0 1 2**100 -(2**100)].each do |r|
  exprs << "(#{r}).class"
  exprs << "(#{r}).instance_of?(Integer)"
  exprs << "(#{r}).is_a?(Integer)"
  exprs << "(#{r}).is_a?(Numeric)"
  exprs << "(#{r}).is_a?(Comparable)"
  exprs << "Integer === (#{r})"
  exprs << "Marshal.load(Marshal.dump(#{r}))"
end
exprs << "Integer.ancestors"
exprs << "Integer.superclass"
exprs << "defined?(Fixnum)"
exprs << "defined?(Bignum)"
exprs << "Integer.instance_methods(false).sort.size"

start = (ARGV[0] || 0).to_i
start.upto(exprs.size - 1) do |i|
  e = exprs[i]
  begin
    out = eval(e).inspect
  rescue Exception => ex
    out = "#{ex.class}: #{ex.message}"
  end
  out = out.to_s.gsub(/0x[0-9a-fA-F]+/, '0xXXXX')
  $stdout.puts("#{i}\t#{e} => #{out}")
  $stdout.flush
end

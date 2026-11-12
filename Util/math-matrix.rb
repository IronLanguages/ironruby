# Differential matrix for Math: every Math function against a grid of inputs
# that includes the domain edges, NaN, the infinities and the non-Float
# arguments the specs coerce.  Run under CRuby to record, then under ir.sh and
# diff; or run with `compare <file>` to print only the rows that differ.
#
#   ruby   Util/math-matrix.rb > /tmp/math-cruby.txt
#   ./ir.sh Util/math-matrix.rb > /tmp/math-ir.txt
#   ruby   Util/math-matrix.rb compare /tmp/math-cruby.txt < /tmp/math-ir.txt

INF = Float::INFINITY
NAN = 0.0 / 0.0

class Coercible
  def to_f; 1.0; end
end
class NotCoercible
end

ONE_ARG = %w[
  acos acosh asin asinh atan atanh cbrt cos cosh erf erfc exp
  gamma lgamma log log2 log10 sin sinh sqrt tan tanh frexp
]
TWO_ARG = %w[atan2 hypot ldexp log]

VALUES = [
  0, 1, -1, 2, -2, 0.0, -0.0, 1.0, -1.0, 0.5, -0.5, 2.0, -2.0,
  1.5, -1.5, 10.0, 100.0, 1e-16, -1e-16, 1e300,
  INF, -INF, NAN,
  Rational(1, 2), Rational(-1, 2), Complex(1, 0), 10**20, -(10**20),
  "1", "0.1", "foo", nil, :sym, Coercible.new, NotCoercible.new, [1], 1..2
]

def show(v)
  case v
  when Float
    return "NaN" if v.nan?
    v.inspect
  when Array then "[" + v.map { |x| show(x) }.join(", ") + "]"
  else v.inspect
  end
end

def label(v)
  case v
  when Coercible then "Coercible"
  when NotCoercible then "NotCoercible"
  when Float then v.nan? ? "NaN" : v.inspect
  else v.inspect
  end
end

def call(m, *args)
  show(Math.send(m, *args))
rescue Exception => e
  msg = e.message.to_s.split("\n").first
  "#{e.class}: #{msg}"
end

def emit
  ONE_ARG.each do |m|
    next unless Math.respond_to?(m)
    VALUES.each { |v| puts "#{m}(#{label(v)})\t#{call(m, v)}" }
  end
  TWO_ARG.each do |m|
    next unless Math.respond_to?(m)
    VALUES.each do |a|
      [0, 1, 2.0, -1, NAN, INF, "1", nil].each { |b| puts "#{m}(#{label(a)}, #{label(b)})\t#{call(m, a, b)}" }
    end
  end
  puts "Math::DomainError\t#{(Math::DomainError.ancestors[0, 4].inspect rescue $!.class)}"
  puts "Math::E\t#{Math::E}"
  puts "Math::PI\t#{Math::PI}"
end

if ARGV[0] == "compare"
  want = {}
  File.readlines(ARGV[1]).each { |l| k, v = l.chomp.split("\t", 2); want[k] = v }
  got = {}
  $stdin.readlines.each { |l| k, v = l.chomp.split("\t", 2); got[k] = v }
  bad = 0
  want.each do |k, v|
    g = got[k]
    next if g == v
    bad += 1
    puts "%-34s want %-42s got %s" % [k, v, g.inspect]
  end
  puts "#{bad} of #{want.size} differ"
else
  emit
end

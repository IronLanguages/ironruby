# Differential matrix for the numeric tower's binary operators.
#
# Every (receiver, operator, argument) triple in the grid below is evaluated and
# the answer - or the exception class and message - recorded, together with the
# protocol messages the operator sent to the argument.  The message trace is
# the point: most of the coercion specs check *which* of #coerce, #to_int,
# #to_f, #to_r was called, not only what came back.
#
#   ruby    Util/coerce-matrix.rb > /tmp/coerce-cruby.txt
#   ./ir.sh Util/coerce-matrix.rb > /tmp/coerce-ir.txt
#   ruby    Util/coerce-matrix.rb compare /tmp/coerce-cruby.txt < /tmp/coerce-ir.txt

BIG = 2**64
INF = Float::INFINITY
NAN = 0.0 / 0.0

# Records the protocol messages an operator sends it and answers plausible
# values, so that the operator gets as far as it can before failing.
class Probe
  @@log = []
  def self.log; @@log; end
  def self.reset; @@log = []; end
  def initialize(kind) = @kind = kind
  def coerce(o)
    @@log << :coerce
    @kind == :coerce ? [o, 2] : (raise NoMethodError, "undefined method `coerce'")
  end
  def to_int
    @@log << :to_int
    @kind == :to_int ? 2 : (raise NoMethodError, "undefined method `to_int'")
  end
  def to_i; @@log << :to_i; 2; end
  def to_f; @@log << :to_f; 2.0; end
  def to_r; @@log << :to_r; Rational(2, 1); end
  def to_c; @@log << :to_c; Complex(2, 0); end
  def inspect = "Probe(#{@kind})"
end

class CoerceOnly < Probe; def initialize = super(:coerce); end
class IntOnly    < Probe; def initialize = super(:to_int);  end
class Nothing    < Probe; def initialize = super(:none);   end

RECEIVERS = {
  "1"     => 1,
  "-3"    => -3,
  "0"     => 0,
  "BIG"   => BIG,
  "-BIG"  => -BIG,
  "2.5"   => 2.5,
  "-0.0"  => -0.0,
  "INF"   => INF,
  "NAN"   => NAN,
  "(3/4)" => Rational(3, 4),
  "(1+2i)" => Complex(1, 2),
  "(2+0i)" => Complex(2, 0),
}

ARGS = {
  "2"      => 2,
  "0"      => 0,
  "-2"     => -2,
  "BIG"    => BIG,
  "2.0"    => 2.0,
  "0.0"    => 0.0,
  "-0.0"   => -0.0,
  "INF"    => INF,
  "NAN"    => NAN,
  "(1/2)"  => Rational(1, 2),
  "(0/1)"  => Rational(0, 1),
  "(1+1i)" => Complex(1, 1),
  "(2+0i)" => Complex(2, 0),
  "nil"    => nil,
  "'2'"    => "2",
  "coerce" => :COERCE,
  "to_int" => :TO_INT,
  "none"   => :NONE,
}

OPS = %w[+ - * / % ** div modulo divmod fdiv quo remainder <=> == < <= > >= & | ^ << >> coerce]

def show(v)
  case v
  when Float then v.nan? ? "NaN" : v.inspect
  when Array then "[" + v.map { |x| show(x) }.join(", ") + "]"
  else v.inspect
  end
end

def arg_for(k)
  case ARGS[k]
  when :COERCE then CoerceOnly.new
  when :TO_INT then IntOnly.new
  when :NONE   then Nothing.new
  else ARGS[k]
  end
end

# CRuby 4.0.6 segfaults on a few cells - (2**64) | <object with #coerce> is
# one - so under CRuby each (receiver, operator) group is recorded in a child
# process and a group that dies is simply left out of the reference.
FORK = (defined?(RUBY_ENGINE) ? RUBY_ENGINE : "ruby") == "ruby" && Process.respond_to?(:fork)

def emit
  RECEIVERS.each do |rk, r|
    OPS.each do |op|
      if FORK
        read, write = IO.pipe
        pid = fork do
          read.close
          $stdout.reopen(write)
          emit_group(rk, r, op)
          $stdout.flush
          exit!(0)
        end
        write.close
        out = read.read
        read.close
        Process.wait(pid)
        print out
        next
      end
      emit_group(rk, r, op)
    end
  end
end

def emit_group(rk, r, op)
      ARGS.each_key do |ak|
        a = arg_for(ak)
        Probe.reset
        begin
          out = show(r.__send__(op, a))
        rescue Exception => e
          out = "#{e.class}: #{e.message.to_s.split("\n").first}"
        end
        trace = Probe.log.empty? ? "" : " <#{Probe.log.join(',')}>"
        puts "#{rk} #{op} #{ak}\t#{out}#{trace}"
      end
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
    puts "%-22s want %-52s got %s" % [k, v, g.inspect]
  end
  puts "#{bad} of #{want.size} differ"
else
  emit
end

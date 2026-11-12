# Cross-checks String#unpack / String#unpack1 against CRuby, directive by
# directive, modifier by modifier.
#
#   ruby     Util/unpack-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/unpack-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt | grep -c '^-'
#
# Every row is one (format, input) pair printed with a *literal* label, so a diff
# line names exactly which combination disagrees.  Nothing in the harness leans
# on the code under test: inputs are built from Integer#chr on ASCII-8BIT and
# results are rendered byte by byte rather than through #inspect, so a wrong
# String#inspect or a wrong Encoding cannot disguise a right unpack (or the
# reverse).
#
# Floats are rendered to 17 significant digits, which is enough to distinguish
# every double, and NaN/Infinity are spelled out - "%.17g" of a NaN differs
# between platforms.

def bytes_of(str)
  out = +""
  str.each_byte { |b| out << b.to_s(16).rjust(2, "0") }
  out
end

def repr(value)
  case value
  when nil     then "nil"
  when Integer then value.to_s
  when Float
    if value.nan?
      "NaN"
    elsif value.infinite?
      value < 0 ? "-Inf" : "+Inf"
    else
      format("%.17g", value)
    end
  when String  then "<" + value.encoding.name + ":" + bytes_of(value) + ">"
  when Array   then "[" + value.map { |v| repr(v) }.join(",") + "]"
  else value.class.to_s
  end
end

def show(label)
  result = repr(yield)
rescue Exception => e
  # Messages matter here: ruby/spec matches on several of them.
  result = e.class.to_s + ": " + e.message.to_s
ensure
  puts label + " => " + result
end

# --------------------------------------------------------------------------
# Inputs.  Named so a diff line says which bytes were involved.
# --------------------------------------------------------------------------

def s(*bytes)
  str = +""
  bytes.each { |b| str << b.chr }
  str.force_encoding(Encoding::BINARY)
end

INPUTS = {
  "empty"   => s(),
  "one"     => s(1),
  "abc"     => s(97, 98, 99),
  "asc8"    => s(97, 98, 99, 100, 101, 102, 103, 104),
  "hi8"     => s(0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87),
  "ff8"     => s(255, 255, 255, 255, 255, 255, 255, 255),
  "seq16"   => s(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15),
  "nul"     => s(0, 0, 0, 0, 0, 0, 0, 0),
  "pad"     => s(97, 98, 32, 32, 0, 0),
  "mixed"   => s(1, 0, 0, 0, 255, 255, 255, 255),
}

def each_input
  INPUTS.each { |name, str| yield name, str }
end

# --------------------------------------------------------------------------
# Directives.  The integer ones carry the full cross product of the endianness
# and native-size modifiers, in both orders, because MRI accepts them in either
# and ruby/spec asserts every combination (spec/core/string/unpack/i_spec.rb
# alone is 266 examples of exactly this).
# --------------------------------------------------------------------------

INTEGER_BASE = %w[C c S s L l Q q I i J j N n V v]

MODIFIERS = ["", "<", ">", "!", "_", "<!", "!<", "<_", "_<", ">!", "!>", ">_", "_>"]

COUNTS = ["", "1", "2", "3", "*", "0"]

INTEGER_BASE.each do |base|
  MODIFIERS.each do |mod|
    COUNTS.each do |count|
      fmt = base + mod + count
      each_input do |name, str|
        show("INT " + fmt.ljust(6) + " " + name) { str.unpack(fmt) }
      end
    end
  end
end

# --------------------------------------------------------------------------
# Everything else, one directive at a time.
# --------------------------------------------------------------------------

OTHER = %w[
  A a Z B b H h U w u M m D d F f E e G g P p @ x X
]

OTHER.each do |base|
  COUNTS.each do |count|
    fmt = base + count
    each_input do |name, str|
      show("OTH " + fmt.ljust(6) + " " + name) { str.unpack(fmt) }
    end
  end
end

# --------------------------------------------------------------------------
# Multi-directive formats, whitespace, comments and the things MRI rejects.
# --------------------------------------------------------------------------

COMBOS = [
  "CC", "C2C", "a3C", "Ca3", "NnC", "C x C", "C\tC", "C\nC",
  "C # a comment\nC", "%", "C%", "R", "r", "k", "K", "Y", "y", "T", "t",
  "1", "C-1", "@2C", "@*C", "x*C", "X1", "CX1C", "C!", "a*", "Z*", "A*",
  "w*", "U*", "B*", "b*", "H*", "h*", "M*", "m*", "m0", "m1", "u*",
  "D*", "d*", "E*", "e*", "F*", "f*", "G*", "g*", "P*", "p*",
  "C<", "c<", "A<", "N<", "n>", "v<", "V>", "U<", "w<", "@<",
  "S!<>", "S<>", "l>>", "l<<",
]

COMBOS.each do |fmt|
  each_input do |name, str|
    show("CMB " + fmt.inspect.ljust(20) + " " + name) { str.unpack(fmt) }
  end
end

# --------------------------------------------------------------------------
# unpack1, and the offset: keyword (Ruby 3.1).
# --------------------------------------------------------------------------

%w[C S> N a3 A3 Z3 B8 H2 w U m a* C*].each do |fmt|
  each_input do |name, str|
    show("ONE " + fmt.ljust(6) + " " + name) { str.unpack1(fmt) }
  end
end

[0, 1, 2, 3, 8, 16].each do |off|
  each_input do |name, str|
    show("OFF unpack  " + off.to_s.ljust(3) + " " + name) { str.unpack("C*", offset: off) }
    show("OFF unpack1 " + off.to_s.ljust(3) + " " + name) { str.unpack1("C", offset: off) }
  end
end

# --------------------------------------------------------------------------
# Argument handling.
# --------------------------------------------------------------------------

show("ARG unpack nil")      { "abc".unpack(nil) }
show("ARG unpack int")      { "abc".unpack(1) }
show("ARG unpack symbol")   { "abc".unpack(:C) }
show("ARG unpack to_str")   {
  o = Object.new
  def o.to_str; "C*"; end
  "abc".unpack(o)
}
show("ARG unpack no args")  { "abc".unpack }
show("ARG unpack1 no args") { "abc".unpack1 }
show("ARG offset negative") { "abc".unpack("C", offset: -1) }
show("ARG offset too big")  { "abc".unpack("C", offset: 4) }
show("ARG offset at end")   { "abc".unpack("C", offset: 3) }

# --------------------------------------------------------------------------
# Result encodings.  unpack's string directives each have a fixed answer for
# what encoding the pieces come back in, independent of the receiver's.
# --------------------------------------------------------------------------

SOURCES = [
  ["binary", s(97, 98, 99, 100)],
  ["utf8",   "abcd".dup.force_encoding(Encoding::UTF_8)],
  ["ascii",  "abcd".dup.force_encoding(Encoding::US_ASCII)],
]

SOURCES.each do |sname, src|
  %w[a2 A2 Z2 B4 b4 H2 h2 M m u U C2 w].each do |fmt|
    show("ENC " + fmt.ljust(4) + " " + sname) { src.unpack(fmt) }
  end
end

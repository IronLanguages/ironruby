# Cross-checks Array#pack against CRuby, directive by directive, modifier by
# modifier, count by count.
#
#   ruby     Util/pack-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/pack-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt | grep -c '^-'
#
# Same shape as Util/format-matrix.rb and Util/file-path-matrix.rb: every case
# carries a *literal* label, so a diff line names exactly which combination
# disagrees and no part of the harness depends on the code under test (no
# #inspect of the result, no nested pack).
#
# Output is rendered byte by byte - printable ASCII verbatim, everything else as
# <hh> - followed by the encoding, because pack's result encoding is part of its
# contract and a raw binary result would otherwise make the diff unreadable.
#
# Expected differences, as of the last run (62 lines out of 26,520):
#
#   37  "coerce ..." - the TypeError for a value that cannot become an Integer
#       reads "can't convert X into Integer" where CRuby says "no implicit
#       conversion of X into Integer".  That wording lives in
#       Protocols.CastToInteger and belongs to the Fixnum/Integer unification
#       branch, not here.
#   24  "flt nan" - .NET's NaN has the sign bit set and MRI's does not, so the
#       packed bytes differ in the top bit.  ruby/spec accepts either sign
#       (spec/core/array/pack/shared/float.rb lists both).
#    1  the format string echoed in "unknown pack directive '\x00' in ..." -
#       CRuby escapes the NUL, this prints it raw.
#
# Anything beyond those is a real difference.

def repr(value)
  return "nil" if value.nil?
  out = String.new
  value.each_byte do |b|
    out << ((b >= 0x20 && b < 0x7f) ? b.chr : ("<" + b.to_s(16).rjust(2, "0") + ">"))
  end
  "|" + out + "|" + value.encoding.name
end

def show(label, format, args)
  result = begin
    repr(args.pack(format))
  rescue Exception => e            # IronRuby can surface raw CLR exceptions here
    e.class.to_s + ": " + e.message.gsub(/0x[0-9a-f]+/, "0xXX")
  end
  puts label + " " + format.inspect + " => " + result
end

# --------------------------------------------------------------------------
# Axes
# --------------------------------------------------------------------------

# Every directive MRI documents, plus a batch that must be rejected.
INT_DIRECTIVES   = %w[C c S s L l Q q J j I i N n V v U w]
FLOAT_DIRECTIVES = %w[D d F f E e G g]
STR_DIRECTIVES   = %w[A a Z B b H h u M m]
MISC_DIRECTIVES  = %w[@ X x]
BAD_DIRECTIVES   = %w[K k R r T t W y Y z O o $ ? ! ~ ^ & 0 1 = + - . , : ; [ ] { } < >]

# "!" and "_" mean native size; "<" and ">" force endianness.
INT_MODIFIERS = ["", "!", "_", "<", ">", "!<", "!>", "_<", "_>", "<<", "><"]
COUNTS        = ["", "0", "1", "2", "3", "*"]

INTS = [
  ["0",        0],
  ["1",        1],
  ["-1",       -1],
  ["127",      127],
  ["128",      128],
  ["-128",     -128],
  ["255",      255],
  ["256",      256],
  ["-256",     -256],
  ["32767",    32767],
  ["-32768",   -32768],
  ["65535",    65535],
  ["2**31-1",  2147483647],
  ["-2**31",   -2147483648],
  ["2**32-1",  4294967295],
  ["2**63-1",  9223372036854775807],
  ["-2**63",   -9223372036854775808],
  ["2**64-1",  18446744073709551615],
  ["big",      123456789012345678901234567890],
]

FLOATS = [
  ["0.0",   0.0],
  ["-0.0",  -0.0],
  ["1.0",   1.0],
  ["-1.0",  -1.0],
  ["0.5",   0.5],
  ["1.5",   1.5],
  ["1e10",  1e10],
  ["1e-10", 1e-10],
  ["1e100", 1e100],
  ["inf",   Float::INFINITY],
  ["-inf",  -Float::INFINITY],
  ["nan",   Float::NAN],
  ["int7",  7],
]

STRINGS = [
  ["empty",   ""],
  ["a",       "a"],
  ["abc",     "abc"],
  ["abcdefg", "abcdefg"],
  ["nul",     "a\0b"],
  ["high",    "\xff\xfe"],
  ["utf8",    "é"],
  ["binary",  "abc".b],
]

BIT_STRINGS = [
  ["empty", ""],
  ["0",     "0"],
  ["1",     "1"],
  ["0101",  "0101"],
  ["1111",  "1111"],
  ["10101010", "10101010"],
  ["abc",   "abc"],
  ["long",  "0101010101010101"],
]

HEX_STRINGS = [
  ["empty", ""],
  ["0",     "0"],
  ["f",     "f"],
  ["ff",    "ff"],
  ["0123",  "0123"],
  ["abcdef", "abcdef"],
  ["zz",    "zz"],
]

# --------------------------------------------------------------------------
# Integer directives x modifiers x counts
# --------------------------------------------------------------------------

INT_DIRECTIVES.each do |directive|
  INT_MODIFIERS.each do |modifier|
    COUNTS.each do |count|
      format = directive + modifier + count
      INTS.each { |label, value| show("int " + label, format, [value]) }
      show("int multi", format, [1, 2, 3])
      show("int short", format, [])
    end
  end
end

# --------------------------------------------------------------------------
# Float directives
# --------------------------------------------------------------------------

FLOAT_DIRECTIVES.each do |directive|
  COUNTS.each do |count|
    format = directive + count
    FLOATS.each { |label, value| show("flt " + label, format, [value]) }
    show("flt multi", format, [1.0, 2.0, 3.0])
    show("flt short", format, [])
  end
end

# --------------------------------------------------------------------------
# String directives
# --------------------------------------------------------------------------

%w[A a Z].each do |directive|
  ["", "0", "1", "3", "5", "10", "*"].each do |count|
    format = directive + count
    STRINGS.each { |label, value| show("str " + label, format, [value]) }
    show("str short", format, [])
  end
end

%w[B b].each do |directive|
  ["", "0", "1", "4", "8", "16", "*"].each do |count|
    format = directive + count
    BIT_STRINGS.each { |label, value| show("bit " + label, format, [value]) }
  end
end

%w[H h].each do |directive|
  ["", "0", "1", "2", "4", "8", "*"].each do |count|
    format = directive + count
    HEX_STRINGS.each { |label, value| show("hex " + label, format, [value]) }
  end
end

%w[u M m].each do |directive|
  ["", "0", "1", "2", "45", "*"].each do |count|
    format = directive + count
    STRINGS.each { |label, value| show("enc " + label, format, [value]) }
    show("enc long", format, ["x" * 100])
  end
end

# --------------------------------------------------------------------------
# @, X, x and %
# --------------------------------------------------------------------------

MISC_DIRECTIVES.each do |directive|
  ["", "0", "1", "3", "*"].each do |count|
    show("misc", directive + count, [])
    show("misc-after", "a" + directive + count, ["hello"])
    show("misc-before", directive + count + "a", ["hello"])
  end
end

# --------------------------------------------------------------------------
# Unknown directives must be rejected, not silently skipped
# --------------------------------------------------------------------------

BAD_DIRECTIVES.each do |directive|
  show("bad", directive, [1])
  show("bad-count", directive + "3", [1])
  show("bad-after", "C" + directive, [1])
end

# --------------------------------------------------------------------------
# Comments, whitespace and NUL inside a format string
# --------------------------------------------------------------------------

["C # comment\nC", "C#c\nC", " C ", "C\tC", "C\nC", "C\0C", "#only comment",
 "", " ", "\n", "C  *", "C 3"].each do |format|
  show("fmt", format, [65, 66])
end

# --------------------------------------------------------------------------
# Coercion of the argument
# --------------------------------------------------------------------------

class ToInt
  def to_int; 65; end
end

class ToStr
  def to_str; "str"; end
end

class ToF
  def to_f; 1.5; end
end

class NoConv
end

%w[C c S L N V w U].each do |directive|
  show("coerce to_int", directive, [ToInt.new])
  show("coerce to_str", directive, [ToStr.new])
  show("coerce none", directive, [NoConv.new])
  show("coerce nil", directive, [nil])
  show("coerce str", directive, ["7"])
  show("coerce float", directive, [1.9])
end

%w[D f E g].each do |directive|
  show("coerce to_f", directive, [ToF.new])
  show("coerce none", directive, [NoConv.new])
  show("coerce nil", directive, [nil])
  show("coerce str", directive, ["1.5"])
end

%w[A a Z B H u m].each do |directive|
  show("coerce to_str", directive, [ToStr.new])
  show("coerce none", directive, [NoConv.new])
  show("coerce nil", directive, [nil])
  show("coerce int", directive, [7])
end

# --------------------------------------------------------------------------
# Result encoding: pack decides it from the directives it used
# --------------------------------------------------------------------------

[["C", [65]], ["c*", [65, 66]], ["a3", ["abc"]], ["A3", ["abc"]],
 ["U", [65]], ["U", [0x3042]], ["U*", [65, 0x3042]],
 ["m", ["abc"]], ["M", ["abc"]], ["u", ["abc"]],
 ["w", [1]], ["N", [1]], ["B8", ["01010101"]], ["H2", ["ff"]],
 ["a*", ["é"]], ["a*", ["abc".b]], ["CU", [65, 66]], ["UC", [65, 66]],
 ["x", []], ["@2", []], ["", []],
].each { |format, args| show("enc-of", format, args) }

# --------------------------------------------------------------------------
# Multi-directive formats and argument accounting
# --------------------------------------------------------------------------

[["CC", [1, 2]], ["CC", [1]], ["C*C", [1, 2, 3]], ["C2C2", [1, 2, 3, 4]],
 ["a3C", ["ab", 1]], ["Ca3", [1, "ab"]], ["NnC", [1, 2, 3]],
 ["C*", []], ["a*", []], ["A*", [""]], ["Z*", [""]],
 ["C0", [1]], ["a0", ["abc"]], ["x0", []],
 ["C*", [1, 2, 3, 4, 5]], ["S*", [1, 2, 3]], ["w*", [1, 128, 16384]],
].each { |format, args| show("multi", format, args) }

# --------------------------------------------------------------------------
# The buffer keyword (3.0)
# --------------------------------------------------------------------------

[["C", [65], ""], ["C", [65], "xxx"], ["C", [65], "x"],
 ["@3C", [65], "abcdef"], ["a*", ["hi"], "ZZZZ"]].each do |format, args, buffer|
  result = begin
    repr(args.pack(format, buffer: buffer.dup))
  rescue Exception => e
    e.class.to_s + ": " + e.message
  end
  puts "buffer " + format.inspect + " " + buffer.inspect + " => " + result
end

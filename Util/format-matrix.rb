# Cross-checks Kernel#format / #sprintf / String#% against CRuby, directive by
# directive, flag by flag.
#
#   ruby     Util/format-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/format-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt | grep -c '^-'
#
# Every combination of flags x width x precision x directive x argument is
# printed with a *literal* label for the argument, so a diff line names exactly
# which combination disagrees and no part of the harness depends on the very
# code under test (no #inspect of floats, no nested sprintf).
#
# Results are rendered byte by byte: printable ASCII verbatim, everything else
# as <hh>.  That keeps the output diffable even when a formatter emits raw
# binary, which is what stops mspec from printing a tally for
# spec/core/string/modulo_spec.rb.
#
# Expected differences, as of the last run (31 lines out of 110,106).  The
# reference here is CRuby 3.3.8, the `ruby` on this box, but IronRuby targets
# 4.0, so some rows differ on purpose:
#
#   16  truncated specifiers - "%-", "%#", "% ", "%+", "%0", "%1$", "%\n",
#       "%<foo>".  3.3 let several of these fall through to a literal '%';
#       3.4 made them all errors and ruby/spec asserts the 3.4 behaviour
#       (spec/core/kernel/shared/sprintf.rb:494).  IronRuby follows the spec.
#   12  "%p" of an object with a custom #to_s.  Kernel#inspect still has Ruby
#       1.8's fallback to #to_s; removing it also rewrites NoMethodError
#       messages, so it is deferred to that work (see KernelOps.Inspect).
#    2  "can't convert X into Fixnum" where CRuby says "no implicit conversion
#       of X into Integer" - Fixnum/Integer unification.
#    1  Rational#inspect renders "Rational(3, 2)" instead of "(3/2)".
#
# Anything beyond those is a real formatting difference.

def repr(value)
  return "nil" if value.nil?
  out = String.new
  value.each_byte do |b|
    if b >= 0x20 && b < 0x7f
      out << b.chr
    else
      out << "<" << b.to_s(16).rjust(2, "0") << ">"
    end
  end
  out
end

def show(label, fmt, args)
  begin
    # Object ids differ run to run and between implementations, so "#<Foo:0x...>" from
    # "%p" is masked rather than reported as a difference.
    result = "|" + repr(format(fmt, *args)).gsub(/0x[0-9a-f]+/, "0xXX") + "|"
  rescue StandardError => e
    result = e.class.to_s + ": " + e.message
  end
  puts label + " " + fmt + " => " + result
end

# --------------------------------------------------------------------------
# Flag / width / precision axes
# --------------------------------------------------------------------------

FLAGS = [
  "", "-", "+", " ", "0", "#",
  "-+", "- ", "-0", "+0", " 0", "#0", "#-", "# ", "+#", "0+", "0-", "-#0+",
]

WIDTHS      = ["", "1", "5", "12"]
INT_PRECS   = ["", ".", ".0", ".3", ".8"]
FLOAT_PRECS = ["", ".", ".0", ".1", ".6", ".14"]
STR_PRECS   = ["", ".", ".0", ".1", ".4"]

# --------------------------------------------------------------------------
# Argument sets, each with a literal label so the harness never calls #inspect
# --------------------------------------------------------------------------

INTS = [
  ["0",      0],
  ["1",      1],
  ["-1",     -1],
  ["7",      7],
  ["-7",     -7],
  ["10",     10],
  ["-10",    -10],
  ["255",    255],
  ["-255",   -255],
  ["2**31-1", 2147483647],
  ["-2**31", -2147483648],
  ["big",    1234567890123456789012345678901234567890],
  ["-big",   -1234567890123456789012345678901234567890],
]

FLOATS = [
  ["0.0",      0.0],
  ["-0.0",     -0.0],
  ["1.0",      1.0],
  ["-1.0",     -1.0],
  ["0.5",      0.5],
  ["-0.5",     -0.5],
  ["1.5",      1.5],
  ["2.5",      2.5],
  ["123.456",  123.456],
  ["-123.456", -123.456],
  ["1e10",     1e10],
  ["1e-10",    1e-10],
  ["1e100",    1e100],
  ["1e-100",   1e-100],
  ["9.999999", 9.999999],
  ["0.0001",   0.0001],
  ["1e15",     1e15],
  ["inf",      Float::INFINITY],
  ["-inf",     -Float::INFINITY],
  ["nan",      Float::NAN],
]

STRINGS = [
  ["empty",  ""],
  ["a",      "a"],
  ["hello",  "hello"],
  ["hw",     "hello world"],
]

OBJECTS = [
  ["nil",    nil],
  ["true",   true],
  ["false",  false],
  ["sym",    :sym],
  ["ary",    [1, 2]],
  ["str",    "s"],
]

CHARS = [
  ["65",     65],
  ["0",      0],
  ["10",     10],
  ["str-a",  "a"],
  ["str-ab", "ab"],
  ["955",    955],
]

# --------------------------------------------------------------------------
# The matrix
# --------------------------------------------------------------------------

%w[d i u b B o x X].each do |directive|
  FLAGS.each do |flag|
    WIDTHS.each do |width|
      INT_PRECS.each do |prec|
        fmt = "%" + flag + width + prec + directive
        INTS.each { |label, v| show(label, fmt, [v]) }
      end
    end
  end
end

%w[e E f g G a A].each do |directive|
  FLAGS.each do |flag|
    WIDTHS.each do |width|
      FLOAT_PRECS.each do |prec|
        fmt = "%" + flag + width + prec + directive
        FLOATS.each { |label, v| show(label, fmt, [v]) }
      end
    end
  end
end

# Integers handed to float directives, and floats handed to integer ones.
%w[e f g d i x].each do |directive|
  ["", "-", "+", "0", "#"].each do |flag|
    ["", "8"].each do |width|
      ["", ".2"].each do |prec|
        fmt = "%" + flag + width + prec + directive
        INTS.each { |label, v| show("int:" + label, fmt, [v]) }
        FLOATS.each { |label, v| show("flt:" + label, fmt, [v]) }
      end
    end
  end
end

%w[s p].each do |directive|
  FLAGS.each do |flag|
    WIDTHS.each do |width|
      STR_PRECS.each do |prec|
        fmt = "%" + flag + width + prec + directive
        STRINGS.each { |label, v| show(label, fmt, [v]) }
        OBJECTS.each { |label, v| show(label, fmt, [v]) }
      end
    end
  end
end

FLAGS.each do |flag|
  WIDTHS.each do |width|
    fmt = "%" + flag + width + "c"
    CHARS.each { |label, v| show(label, fmt, [v]) }
  end
end

# --------------------------------------------------------------------------
# Literal percent, unknown directives, truncated specifiers
# --------------------------------------------------------------------------

["%%", "%5%", "%-5%", "%.3%", "a%%b", "%", "%q", "%5", "%-", "%.", "%#",
 "% ", "%+", "%0", "%*", "%1$", "%{", "%<", "%\n", "%d%", "100%"].each do |fmt|
  show("literal", fmt, [])
  show("literal1", fmt, [1])
end

# --------------------------------------------------------------------------
# Star width / precision
# --------------------------------------------------------------------------

%w[d s f x b].each do |directive|
  ["%*", "%-*", "%0*", "%*.", "%.*", "%*.*", "%-*.*", "%+*.*"].each do |stem|
    fmt = stem.end_with?(".") ? stem + "3" + directive : stem + directive
    stars = fmt.count("*")
    [[-5, 2], [0, 0], [3, 1], [8, 4], [-8, -2]].each do |a, b|
      args = stars == 2 ? [a, b, 42] : [a, 42]
      args = stars == 2 ? [a, b, "abc"] : [a, "abc"] if directive == "s"
      args = stars == 2 ? [a, b, 1.5] : [a, 1.5] if directive == "f"
      show("star" + a.to_s + "," + b.to_s, fmt, args)
    end
  end
end

# --------------------------------------------------------------------------
# Absolute argument indexes
# --------------------------------------------------------------------------

["%1$s", "%2$s", "%1$s %2$s", "%2$s %1$s", "%1$s %1$s", "%1$d", "%2$05d",
 "%1$-8s|", "%3$s", "%0$s", "%1$s %s", "%s %1$s", "%1$*2$d"].each do |fmt|
  show("idx", fmt, ["a", "b"])
  show("idxn", fmt, [1, 2])
end

# --------------------------------------------------------------------------
# Named references: %{name} and %<name>directive
# --------------------------------------------------------------------------

NAMED = { foo: "bar", num: 42, flt: 1.5 }

["%{foo}", "%{num}", "%{foo}%{num}", "a %{foo} b", "%{missing}",
 "%<foo>s", "%<num>d", "%<num>05d", "%<num>x", "%<flt>.2f", "%<flt>8.3e",
 "%<missing>s", "%<foo>d", "%{foo}%<num>d", "%<num>s %{foo}",
 "%{foo", "%<foo", "%<foo>", "%{}", "%<>s"].each do |fmt|
  show("named", fmt, [NAMED])
  show("named-noarg", fmt, [])
end

# --------------------------------------------------------------------------
# Argument-count behaviour
# --------------------------------------------------------------------------

[["%s", []], ["%s %s", ["a"]], ["%d", []], ["%*d", [5]],
 ["%s", ["a", "b"]], ["", ["a"]], ["abc", ["a"]]].each do |fmt, args|
  show("count", fmt, args)
end

# --------------------------------------------------------------------------
# Coercion: to_int / to_ary / to_s / to_str on the argument
# --------------------------------------------------------------------------

class ToInt
  def to_int; 42; end
  def to_s; "ToInt#to_s"; end
end

class ToI
  def to_i; 7; end
  def to_s; "ToI#to_s"; end
end

class ToStr
  def to_str; "str!"; end
  def to_s; "ToStr#to_s"; end
end

class ToS
  def to_s; "tos!"; end
end

class ToAry
  def to_ary; [1, 2]; end
  def to_s; "ToAry#to_s"; end
end

class ToF
  def to_f; 2.5; end
  def to_s; "ToF#to_s"; end
end

class NoTos
  undef_method :to_s rescue nil
end

COERCE = [
  ["to_int", ToInt.new],
  ["to_i",   ToI.new],
  ["to_str", ToStr.new],
  ["to_s",   ToS.new],
  ["to_ary", ToAry.new],
  ["to_f",   ToF.new],
]

%w[d i u b o x X e f g s p c].each do |directive|
  COERCE.each do |label, obj|
    show("coerce:" + label, "%" + directive, [obj])
    show("coerce:" + label, "%8.2" + directive, [obj])
  end
end

# Width and precision arguments are coerced with to_int.
show("starcoerce", "%*d", [ToInt.new, 1])
show("starcoerce", "%.*d", [ToInt.new, 1])
show("starcoerce", "%*d", [ToI.new, 1])

# --------------------------------------------------------------------------
# Numeric strings and other types fed to numeric directives
# --------------------------------------------------------------------------

[["str-num", "12"], ["str-hex", "0x1f"], ["str-bad", "abc"], ["nil", nil],
 ["true", true], ["sym", :s], ["ary", [1]], ["rational", Rational(3, 2)],
 ["bigflt", 1e20]].each do |label, v|
  %w[d i u b o x X e f g s p].each do |directive|
    show("odd:" + label, "%" + directive, [v])
  end
end

# --------------------------------------------------------------------------
# A handful of real-world composites
# --------------------------------------------------------------------------

[["%05.2f%%", [3.14159]],
 ["[%-10s][%10s]", ["ab", "cd"]],
 ["%s=%d (%#x, %#o, %#b)", ["n", 255, 255, 255, 255]],
 ["%+.3e / %+.3E", [1234.5678, 1234.5678]],
 ["%.10g|%.10G", [0.000012345, 123456789012.0]],
 ["%c%c%c", [72, 105, 33]],
 ["%-+8.3f|", [-1.5]],
 ["% d/% d", [5, -5]],
 ["%08.3f", [-1.5]],
 ["%#020x", [-255]],
].each { |fmt, args| show("composite", fmt, args) }

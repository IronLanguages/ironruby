# Cross-checks String#encode / String#encode! option handling against CRuby.
#
#   ruby     Util/encode-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/encode-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt | grep -c '^-'
#
# #encode's behaviour is almost entirely in its options - :invalid, :undef,
# :replace, :fallback, :xml and the newline decorators - and each of them only
# shows up on input the plain conversion would refuse.  Every row is one
# (source, to, from, options) combination with a literal label, and results are
# rendered byte by byte with their encoding so that neither String#inspect nor
# the Encoding table can disguise a wrong answer.

def bytes_of(str)
  out = +""
  str.each_byte { |b| out << b.to_s(16).rjust(2, "0") }
  out
end

def repr(value)
  case value
  when nil    then "nil"
  when String then "<" + value.encoding.name + ":" + bytes_of(value) + ">"
  else value.class.to_s
  end
end

def show(label)
  result = repr(yield)
rescue Exception => e
  result = e.class.to_s + ": " + e.message.to_s
ensure
  puts label + " => " + result
end

def b(*bytes)
  str = +""
  bytes.each { |x| str << x.chr }
  str.force_encoding(Encoding::BINARY)
end

def in_enc(str, name)
  str.dup.force_encoding(Encoding.find(name))
end

# --------------------------------------------------------------------------
# Sources: valid, invalid-in-its-own-encoding, and undefined-in-the-target.
# --------------------------------------------------------------------------

SOURCES = {
  "ascii"      => in_enc(+"hello", "UTF-8"),
  "accented"   => in_enc(b(0xc3, 0xa9, 0x67), "UTF-8"),     # "ég"
  "cjk"        => in_enc(b(0xe6, 0x97, 0xa5), "UTF-8"),     # "日"
  "invalid"    => in_enc(b(0x61, 0xff, 0x62), "UTF-8"),     # a, bad byte, b
  "invalid2"   => in_enc(b(0xff, 0xfe), "UTF-8"),
  "truncated"  => in_enc(b(0x61, 0xe3, 0x81), "UTF-8"),
  "newlines"   => in_enc(+"a\nb\r\nc\rd", "UTF-8"),
  "xmlish"     => in_enc(+"a<b>c&d\"e'f", "UTF-8"),
  "xml-undef"  => in_enc(b(0x3c, 0xe6, 0x97, 0xa5, 0x3e), "UTF-8"),
  "binary"     => b(0x61, 0xff),
  "empty"      => in_enc(+"", "UTF-8"),
}

TARGETS = ["UTF-8", "US-ASCII", "ISO-8859-1", "UTF-16BE", "EUC-JP", "BINARY"]

# --------------------------------------------------------------------------
# Plain conversion, with and without an explicit source encoding.
# --------------------------------------------------------------------------

SOURCES.each do |sname, src|
  TARGETS.each do |to|
    show("PLAIN #{sname.ljust(10)} -> #{to.ljust(10)}") { src.dup.encode(to) }
    show("PLAIN! #{sname.ljust(9)} -> #{to.ljust(10)}") { src.dup.encode!(to) }
    show("FROM  #{sname.ljust(10)} -> #{to.ljust(10)} from BINARY") { src.dup.encode(to, "BINARY") }
  end
  show("NOARG #{sname.ljust(10)}") { src.dup.encode }
end

# --------------------------------------------------------------------------
# :invalid and :undef, with and without :replace.
# --------------------------------------------------------------------------

OPTION_SETS = [
  ["invalid-replace",  { invalid: :replace }],
  ["undef-replace",    { undef: :replace }],
  ["both-replace",     { invalid: :replace, undef: :replace }],
  ["replace-str",      { invalid: :replace, undef: :replace, replace: "?" }],
  ["replace-empty",    { invalid: :replace, undef: :replace, replace: "" }],
  ["replace-long",     { invalid: :replace, undef: :replace, replace: "<?>" }],
  ["invalid-nil",      { invalid: nil }],
  ["undef-nil",        { undef: nil }],
  ["invalid-bogus",    { invalid: :bogus }],
  ["undef-bogus",      { undef: :bogus }],
]

SOURCES.each do |sname, src|
  TARGETS.each do |to|
    OPTION_SETS.each do |oname, opts|
      show("OPT #{sname.ljust(10)} -> #{to.ljust(10)} #{oname.ljust(16)}") { src.dup.encode(to, **opts) }
    end
  end
end

# --------------------------------------------------------------------------
# :xml
# --------------------------------------------------------------------------

["xmlish", "xml-undef", "ascii", "accented"].each do |sname|
  src = SOURCES[sname]
  [:text, :attr, :bogus].each do |mode|
    ["UTF-8", "US-ASCII"].each do |to|
      show("XML #{sname.ljust(10)} -> #{to.ljust(9)} #{mode.inspect.ljust(7)}") { src.dup.encode(to, xml: mode) }
      show("XMLU #{sname.ljust(9)} -> #{to.ljust(9)} #{mode.inspect.ljust(7)}") { src.dup.encode(to, xml: mode, undef: :replace) }
    end
  end
end

# --------------------------------------------------------------------------
# Newline decorators.
# --------------------------------------------------------------------------

src = SOURCES["newlines"]
[:universal_newline, :cr_newline, :crlf_newline].each do |opt|
  show("NL #{opt.to_s.ljust(18)} true ") { src.dup.encode("UTF-8", opt => true) }
  show("NL #{opt.to_s.ljust(18)} false") { src.dup.encode("UTF-8", opt => false) }
end
show("NL newline-universal") { src.dup.encode("UTF-8", newline: :universal) }
show("NL newline-crlf     ") { src.dup.encode("UTF-8", newline: :crlf) }
show("NL newline-cr       ") { src.dup.encode("UTF-8", newline: :cr) }
show("NL newline-bogus    ") { src.dup.encode("UTF-8", newline: :bogus) }

# --------------------------------------------------------------------------
# :fallback - a Hash, a Proc, a Method, or any object answering to #[].
# --------------------------------------------------------------------------

FALLBACK_SRC = in_enc(b(0xe6, 0x97, 0xa5, 0x61), "UTF-8")   # "日a"

show("FB hash-hit  ") { FALLBACK_SRC.dup.encode("US-ASCII", fallback: { "日" => "X" }) }
show("FB hash-miss ") { FALLBACK_SRC.dup.encode("US-ASCII", fallback: { "zzz" => "X" }) }
show("FB hash-empty") { FALLBACK_SRC.dup.encode("US-ASCII", fallback: {}) }
show("FB proc      ") { FALLBACK_SRC.dup.encode("US-ASCII", fallback: proc { |c| "<" + bytes_of(c) + ">" }) }
show("FB lambda    ") { FALLBACK_SRC.dup.encode("US-ASCII", fallback: lambda { |c| "L" }) }
show("FB proc-nil  ") { FALLBACK_SRC.dup.encode("US-ASCII", fallback: proc { |c| nil }) }

class FallbackObject
  def [](key); "O"; end
end
show("FB object    ") { FALLBACK_SRC.dup.encode("US-ASCII", fallback: FallbackObject.new) }

def fallback_method(key); "M"; end
show("FB method    ") { FALLBACK_SRC.dup.encode("US-ASCII", fallback: method(:fallback_method)) }
show("FB bogus     ") { FALLBACK_SRC.dup.encode("US-ASCII", fallback: 1) }

# --------------------------------------------------------------------------
# Argument handling.
# --------------------------------------------------------------------------

class ToStrEncoding
  def to_str; "US-ASCII"; end
end
class ToHashOptions
  def to_hash; { undef: :replace }; end
end

show("ARG to_str target") { SOURCES["cjk"].dup.encode(ToStrEncoding.new, undef: :replace) }
show("ARG to_str source") { SOURCES["cjk"].dup.encode("UTF-8", ToStrEncoding.new, undef: :replace) }
show("ARG to_hash opts ") { SOURCES["cjk"].dup.encode("US-ASCII", ToHashOptions.new) }
show("ARG bad target   ") { SOURCES["ascii"].dup.encode("no-such-encoding") }
show("ARG nil target   ") { SOURCES["ascii"].dup.encode(nil) }
show("ARG int target   ") { SOURCES["ascii"].dup.encode(1) }
show("ARG frozen       ") { "abc".freeze.encode!("US-ASCII") }
show("ARG same enc     ") { SOURCES["invalid"].dup.encode("UTF-8") }
show("ARG same enc opts") { SOURCES["invalid"].dup.encode("UTF-8", invalid: :replace) }

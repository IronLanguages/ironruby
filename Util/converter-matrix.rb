# Cross-checks the Converter class of Encoding against CRuby.  (Writing its full
# name on the first line would make Ruby read "coding::Converter" as a magic
# encoding comment, which is why this paragraph spells it the long way round.)
#
#   ruby     Util/converter-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/converter-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt | grep -c '^-'
#
# Encoding::Converter is the machine String#encode and IO transcoding are both
# built on, and almost all of its behaviour is in what it does with input it
# cannot convert: which bytes it blames, which it hands back to be read again,
# what status it stops with and what it leaves in the buffers.  Every row prints
# the status, both buffers byte by byte and the whole of #primitive_errinfo, so
# that a converter which produces the right bytes by the wrong route still shows
# up as a difference.

def bytes_of(str)
  out = +""
  str.each_byte { |b| out << b.to_s(16).rjust(2, "0") }
  out
end

def repr(value)
  case value
  when nil    then "nil"
  when String then "<" + value.encoding.name + ":" + bytes_of(value) + ">"
  when Array  then "[" + value.map { |v| repr(v) }.join(" ") + "]"
  when Symbol then value.inspect
  else value.inspect
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
# Construction, and what a converter says about itself.
# --------------------------------------------------------------------------

PAIRS = [
  ["UTF-8", "US-ASCII"],
  ["UTF-8", "ISO-8859-1"],
  ["UTF-8", "UTF-16BE"],
  ["UTF-8", "EUC-JP"],
  ["UTF-8", "BINARY"],
  ["ISO-8859-1", "UTF-8"],
  ["UTF-16BE", "US-ASCII"],
  ["BINARY", "UTF-8"],
  ["UTF-8", "UTF-8"],
  ["UTF-8", "no-such-encoding"],
]

PAIRS.each do |from, to|
  show("NEW #{from.ljust(12)} -> #{to.ljust(16)}") { Encoding::Converter.new(from, to).inspect }
  show("SRC #{from.ljust(12)} -> #{to.ljust(16)}") { Encoding::Converter.new(from, to).source_encoding }
  show("DST #{from.ljust(12)} -> #{to.ljust(16)}") { Encoding::Converter.new(from, to).destination_encoding }
  show("CPATH #{from.ljust(10)} -> #{to.ljust(16)}") { Encoding::Converter.new(from, to).convpath }
  show("SEARCH #{from.ljust(9)} -> #{to.ljust(16)}") { Encoding::Converter.search_convpath(from, to) }
  show("REPL #{from.ljust(11)} -> #{to.ljust(16)}") { Encoding::Converter.new(from, to).replacement }
end

show("NEW decorator      ") { Encoding::Converter.new("", "", universal_newline: true).convpath }
show("NEW newline univ   ") { Encoding::Converter.new("UTF-8", "UTF-8", newline: :universal).convpath }
show("NEW newline crlf   ") { Encoding::Converter.new("UTF-8", "UTF-8", newline: :crlf).convpath }
show("NEW newline cr     ") { Encoding::Converter.new("UTF-8", "UTF-8", newline: :cr).convpath }
show("NEW flags int      ") { Encoding::Converter.new("UTF-8", "US-ASCII", Encoding::Converter::UNDEF_REPLACE).replacement }
show("NEW invalid int    ") { Encoding::Converter.new("UTF-8", "US-ASCII", Encoding::Converter::INVALID_REPLACE).replacement }
show("NEW opts replace   ") { Encoding::Converter.new("UTF-8", "US-ASCII", replace: "!").replacement }
show("NEW opts undef     ") { Encoding::Converter.new("UTF-8", "US-ASCII", undef: :replace).replacement }
show("NEW opts bogus     ") { Encoding::Converter.new("UTF-8", "US-ASCII", undef: :bogus).replacement }
show("NEW convpath arg   ") { Encoding::Converter.new([["UTF-8", "UTF-16BE"]]).convpath }

show("REPL= ascii        ") { ec = Encoding::Converter.new("UTF-8", "US-ASCII"); ec.replacement = "?!"; ec.replacement }
show("REPL= undef        ") { ec = Encoding::Converter.new("UTF-8", "US-ASCII"); ec.replacement = "é"; ec.replacement }
show("REPL default 16be  ") { Encoding::Converter.new("UTF-8", "UTF-16BE").replacement }

# --------------------------------------------------------------------------
# #convert and #finish - the simple interface, which raises on anything it
# cannot convert unless it was built to replace.
# --------------------------------------------------------------------------

CONVERT_SOURCES = {
  "ascii"     => in_enc(+"hello", "UTF-8"),
  "accented"  => in_enc(b(0xc3, 0xa9, 0x67), "UTF-8"),
  "cjk"       => in_enc(b(0xe6, 0x97, 0xa5), "UTF-8"),
  "invalid"   => in_enc(b(0x61, 0xff, 0x62), "UTF-8"),
  "truncated" => in_enc(b(0x61, 0xe3, 0x81), "UTF-8"),
  "newlines"  => in_enc(+"a\nb\r\nc\rd", "UTF-8"),
  "xmlish"    => in_enc(+"a<b>c&d\"e'f", "UTF-8"),
  "xmlundef"  => in_enc(b(0x3c, 0xe6, 0x97, 0xa5, 0x3e), "UTF-8"),
  "empty"     => in_enc(+"", "UTF-8"),
}

CONVERTERS = [
  ["plain    ", ["UTF-8", "US-ASCII"], {}],
  ["undef    ", ["UTF-8", "US-ASCII"], { undef: :replace }],
  ["invalid  ", ["UTF-8", "US-ASCII"], { invalid: :replace }],
  ["both     ", ["UTF-8", "US-ASCII"], { invalid: :replace, undef: :replace }],
  ["repl     ", ["UTF-8", "US-ASCII"], { invalid: :replace, undef: :replace, replace: "<>" }],
  ["to16be   ", ["UTF-8", "UTF-16BE"], { undef: :replace }],
  ["universal", ["UTF-8", "ISO-8859-1"], { universal_newline: true }],
  ["crlf     ", ["UTF-8", "ISO-8859-1"], { crlf_newline: true }],
  ["cr       ", ["UTF-8", "ISO-8859-1"], { cr_newline: true }],
  ["xml text ", ["UTF-8", "US-ASCII"], { xml: :text }],
  ["xml attr ", ["UTF-8", "US-ASCII"], { xml: :attr }],
]

CONVERT_SOURCES.each do |sname, src|
  CONVERTERS.each do |cname, args, opts|
    show("CONV #{sname.ljust(10)} #{cname}") { Encoding::Converter.new(*args, **opts).convert(src.dup) }
    show("FIN  #{sname.ljust(10)} #{cname}") do
      ec = Encoding::Converter.new(*args, **opts)
      out = ec.convert(src.dup) rescue nil
      [out, ec.finish]
    end
  end
end

# --------------------------------------------------------------------------
# #primitive_convert - the full interface, where the interesting answers are
# the status and what is left behind in the two buffers.
# --------------------------------------------------------------------------

PRIMITIVE_SOURCES = {
  "ascii"      => in_enc(+"hello", "UTF-8"),
  "accented"   => in_enc(b(0xc3, 0xa9, 0x67), "UTF-8"),
  "cjk"        => in_enc(b(0xe6, 0x97, 0xa5, 0x61), "UTF-8"),
  "invalid"    => in_enc(b(0x61, 0xff, 0x62), "UTF-8"),
  "invalid2"   => in_enc(b(0xff, 0xfe), "UTF-8"),
  "truncated"  => in_enc(b(0x61, 0xe3, 0x81), "UTF-8"),
  "midtrunc"   => in_enc(b(0xe3, 0x81, 0x61), "UTF-8"),
  "surrogate"  => in_enc(b(0xed, 0xa0, 0x80), "UTF-8"),
  "overlong"   => in_enc(b(0xe0, 0x80), "UTF-8"),
  "unstartable"=> in_enc(b(0xf5, 0x80, 0x80), "UTF-8"),
  "newlines"   => in_enc(+"a\nb\r\nc\rd", "UTF-8"),
  "xmlish"     => in_enc(+"a<b>c&d\"e'f", "UTF-8"),
  "xmlundef"   => in_enc(b(0x3c, 0xe6, 0x97, 0xa5, 0x3e), "UTF-8"),
  "empty"      => in_enc(+"", "UTF-8"),
}

# Note the plain positional arguments: IronRuby has no keyword arguments, so a
# trailing Hash in this script would be indistinguishable from one, and the
# matrix would measure that rather than the converter.
def primitive_row(label, args, src, converter_options, offset, limit, flags)
  show(label) do
    ec = Encoding::Converter.new(*args, **converter_options)
    source = src.dup
    dest = +""
    dest.force_encoding(Encoding::BINARY)
    status = ec.primitive_convert(source, dest, offset, limit, flags)
    [status, dest, source, ec.primitive_errinfo]
  end
end

PRIMITIVE_SOURCES.each do |sname, src|
  CONVERTERS.each do |cname, args, copts|
    primitive_row("PRIM #{sname.ljust(12)} #{cname}", args, src, copts, nil, nil, 0)
  end
  primitive_row("PRIM #{sname.ljust(12)} partial  ", ["UTF-8", "US-ASCII"], src, {},
                nil, nil, Encoding::Converter::PARTIAL_INPUT)
  primitive_row("PRIM #{sname.ljust(12)} afterout ", ["UTF-8", "US-ASCII"], src, {},
                nil, nil, Encoding::Converter::AFTER_OUTPUT)
  [0, 1, 2, 3].each do |limit|
    primitive_row("PRIM #{sname.ljust(12)} limit#{limit}   ", ["UTF-8", "US-ASCII"], src, {}, nil, limit, 0)
  end
end

# Feeding the same converter twice, which is the only way a character split
# across two reads is visible.
show("SPLIT two calls") do
  ec = Encoding::Converter.new("UTF-8", "UTF-16BE")
  dest = +""
  dest.force_encoding(Encoding::BINARY)
  first = ec.primitive_convert(in_enc(b(0x61, 0xe6), "UTF-8"), dest, nil, nil,
                               Encoding::Converter::PARTIAL_INPUT)
  second = ec.primitive_convert(in_enc(b(0x97, 0xa5), "UTF-8"), dest)
  [first, second, dest]
end

show("SPLIT crlf across calls") do
  ec = Encoding::Converter.new("UTF-8", "UTF-8", universal_newline: true)
  dest = +""
  dest.force_encoding(Encoding::BINARY)
  first = ec.primitive_convert(in_enc(+"a\r", "UTF-8"), dest, nil, nil,
                               Encoding::Converter::PARTIAL_INPUT)
  second = ec.primitive_convert(in_enc(+"\nb", "UTF-8"), dest)
  [first, second, dest]
end

# --------------------------------------------------------------------------
# Error recovery: #putback and #insert_output after a failed conversion.
# --------------------------------------------------------------------------

show("PUTBACK all") do
  ec = Encoding::Converter.new("UTF-8", "US-ASCII")
  source = in_enc(b(0x61, 0xe3, 0x81, 0x62), "UTF-8")
  dest = +""
  dest.force_encoding(Encoding::BINARY)
  status = ec.primitive_convert(source, dest)
  [status, ec.putback, dest, source]
end

show("PUTBACK n  ") do
  ec = Encoding::Converter.new("UTF-8", "US-ASCII")
  source = in_enc(b(0x61, 0xe3, 0x81, 0x62), "UTF-8")
  dest = +""
  dest.force_encoding(Encoding::BINARY)
  ec.primitive_convert(source, dest)
  [ec.putback(0), ec.putback(1)]
end

show("INSERT after undef") do
  ec = Encoding::Converter.new("UTF-8", "US-ASCII")
  source = in_enc(b(0xe6, 0x97, 0xa5, 0x61), "UTF-8")
  dest = +""
  dest.force_encoding(Encoding::BINARY)
  status = ec.primitive_convert(source, dest)
  ec.insert_output("[X]")
  [status, ec.primitive_convert(source, dest), dest]
end

show("INSERT undef char  ") do
  ec = Encoding::Converter.new("UTF-8", "US-ASCII")
  ec.insert_output("é")
end

show("LAST_ERROR clean   ") { Encoding::Converter.new("UTF-8", "US-ASCII").last_error }
show("ERRINFO clean      ") { Encoding::Converter.new("UTF-8", "US-ASCII").primitive_errinfo }

[["undef", b(0xe6, 0x97, 0xa5)], ["invalid", b(0x61, 0xff)], ["incomplete", b(0x61, 0xe3, 0x81)]].each do |name, raw|
  show("LAST_ERROR #{name.ljust(11)}") do
    ec = Encoding::Converter.new("UTF-8", "US-ASCII")
    dest = +""
    dest.force_encoding(Encoding::BINARY)
    ec.primitive_convert(in_enc(raw, "UTF-8"), dest)
    e = ec.last_error
    info = [e.class.to_s, e.message, e.source_encoding_name, e.destination_encoding_name]
    if e.respond_to?(:error_bytes)
      info << e.error_bytes << e.readagain_bytes << e.incomplete_input?
    else
      info << e.error_char
    end
    info
  end
end

# --------------------------------------------------------------------------
# Class methods that do not need a converter instance.
# --------------------------------------------------------------------------

["UTF-8", "US-ASCII", "UTF-16BE", "UTF-16LE", "UTF-32BE", "ISO-8859-1", "EUC-JP", "BINARY"].each do |name|
  show("ASCIICOMPAT #{name.ljust(12)}") { Encoding::Converter.asciicompat_encoding(name) }
end
show("ASCIICOMPAT bogus       ") { Encoding::Converter.asciicompat_encoding("no-such-encoding") }
show("SEARCH same             ") { Encoding::Converter.search_convpath("UTF-8", "UTF-8") }
show("SEARCH decorated        ") { Encoding::Converter.search_convpath("UTF-8", "UTF-8", universal_newline: true) }

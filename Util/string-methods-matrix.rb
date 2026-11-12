# Cross-checks the String methods IronRuby is missing, or has only in part,
# against CRuby.
#
#   ruby     Util/string-methods-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/string-methods-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt | grep -c '^-'
#
# One row per (method, receiver, arguments), each with a *literal* label, so a
# diff line names exactly which call disagrees.  Receivers are built from
# Integer#chr and results are rendered byte by byte with their encoding, so
# neither a wrong String#inspect nor a wrong Encoding can disguise a right
# answer or the reverse.
#
# Mutating methods get a fresh receiver per row and report both the return value
# and the receiver afterwards, because several of them differ from CRuby only in
# what they leave behind.

def bytes_of(str)
  out = +""
  str.each_byte { |b| out << b.to_s(16).rjust(2, "0") }
  out
end

def repr(value)
  case value
  when nil     then "nil"
  when true    then "true"
  when false   then "false"
  when Integer then value.to_s
  when String  then "<" + value.encoding.name + ":" + bytes_of(value) + ">"
  when Array   then "[" + value.map { |v| repr(v) }.join(",") + "]"
  when Symbol  then ":" + value.to_s
  else
    # Complex, Rational and friends: class plus to_s is enough to diff on and
    # does not depend on the receiver's #inspect.
    value.class.to_s + "(" + (value.to_s rescue "?") + ")"
  end
end

def show(label)
  result = repr(yield)
rescue Exception => e
  result = e.class.to_s + ": " + e.message.to_s
ensure
  puts label + " => " + result
end

# A mutating call: report the answer and the receiver it left behind.
def show_bang(label, receiver)
  value = yield receiver
  result = repr(value) + " self=" + repr(receiver)
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

def u(str)
  str.dup.force_encoding(Encoding::UTF_8)
end

# --------------------------------------------------------------------------
# Receivers.
# --------------------------------------------------------------------------

RECEIVERS = {
  "empty"    => +"",
  "hello"    => +"hello",
  "hello.wo" => +"hello.world",
  "dots"     => +"a.b.c",
  "utf8"     => u("héllo"),
  "cjk"      => u("日本語テキスト"),
  "binary"   => b(0x61, 0xff, 0x62),
  "badutf8"  => u(+"a\xffb"),
  "nul"      => b(0x61, 0x00, 0x62),
  "mixedcase"=> +"Hello World",
  "accents"  => u("Ärger"),
  "combining"=> u("égal"),
  "precomp"  => u("égal"),
}

def each_receiver
  RECEIVERS.each { |name, str| yield name, str.dup }
end

# --------------------------------------------------------------------------
# partition / rpartition - both a String and a Regexp separator, and the
# separator that is not there at all.
# --------------------------------------------------------------------------

SEPARATORS = [
  ["str-dot",   "."],
  ["str-l",     "l"],
  ["str-empty", ""],
  ["str-miss",  "zz"],
  ["str-multi", "lo"],
  ["re-dot",    /\./],
  ["re-l",      /l+/],
  ["re-any",    /./],
  ["re-miss",   /zz/],
  ["re-empty",  //],
]

%i[partition rpartition].each do |meth|
  each_receiver do |rname, str|
    SEPARATORS.each do |sname, sep|
      show("#{meth} #{rname.ljust(9)} #{sname.ljust(9)}") { str.send(meth, sep) }
    end
    show("#{meth} #{rname.ljust(9)} #{'nil'.ljust(9)}") { str.send(meth, nil) }
    show("#{meth} #{rname.ljust(9)} #{'int'.ljust(9)}") { str.send(meth, 1) }
  end
end

# --------------------------------------------------------------------------
# prepend - zero, one and several arguments, and a frozen receiver.
# --------------------------------------------------------------------------

each_receiver do |rname, str|
  show_bang("prepend #{rname.ljust(9)} none ", str.dup) { |s| s.prepend }
  show_bang("prepend #{rname.ljust(9)} one  ", str.dup) { |s| s.prepend("X") }
  show_bang("prepend #{rname.ljust(9)} two  ", str.dup) { |s| s.prepend("X", "Y") }
  show_bang("prepend #{rname.ljust(9)} utf8 ", str.dup) { |s| s.prepend(u("é")) }
  show_bang("prepend #{rname.ljust(9)} nil  ", str.dup) { |s| s.prepend(nil) }
end
show("prepend frozen") { "abc".freeze.prepend("x") }

# --------------------------------------------------------------------------
# casecmp?  - the Unicode-aware sibling of casecmp.
# --------------------------------------------------------------------------

CASE_PAIRS = [
  ["abc", "abc"], ["abc", "ABC"], ["ABC", "abc"], ["abc", "abd"], ["abc", "ab"],
  ["", ""], ["Hello", "hello"], ["HELLO WORLD", "hello world"],
  ["ÄRGER", "ärger"], ["Straße", "STRASSE"], ["ǅ", "ǆ"], ["ǅ", "Ǆ"],
  ["İ", "i"], ["ﬁ", "FI"],
]

CASE_PAIRS.each do |a, bb|
  show("casecmp?  #{a.inspect.ljust(14)} #{bb.inspect.ljust(14)}") { u(a).casecmp?(u(bb)) }
  show("casecmp   #{a.inspect.ljust(14)} #{bb.inspect.ljust(14)}") { u(a).casecmp(u(bb)) }
end
show("casecmp? non-string") { "abc".casecmp?(1) }
show("casecmp  non-string") { "abc".casecmp(1) }
show("casecmp? incompatible") { b(0xff).casecmp?(u("é")) }

# --------------------------------------------------------------------------
# byteindex / byterindex - byte offsets, not character ones, and a Regexp
# needle as well as a String one.
# --------------------------------------------------------------------------

NEEDLES = [
  ["str-l",  "l"],
  ["str-lo", "lo"],
  ["str-e",  "e"],
  ["str-mi", "zz"],
  ["str-emp", ""],
  ["re-l",   /l/],
  ["re-ll",  /l+/],
  ["re-any", /./],
  ["re-mi",  /zz/],
]

OFFSETS = [nil, 0, 1, 2, 3, -1, -3, 100, -100]

%i[byteindex byterindex].each do |meth|
  each_receiver do |rname, str|
    NEEDLES.each do |nname, needle|
      OFFSETS.each do |off|
        label = "#{meth} #{rname.ljust(9)} #{nname.ljust(8)} #{off.inspect.ljust(5)}"
        show(label) { off.nil? ? str.send(meth, needle) : str.send(meth, needle, off) }
      end
    end
  end
end

# --------------------------------------------------------------------------
# bytesplice - byte-level replacement, both arities.
# --------------------------------------------------------------------------

BYTESPLICE_3 = [[0, 0], [0, 1], [1, 1], [1, 2], [0, 3], [2, 0], [3, 0], [-1, 1], [-2, 2], [5, 1], [0, 99], [-99, 1], [1, -1]]

each_receiver do |rname, str|
  BYTESPLICE_3.each do |idx, len|
    show_bang("bytesplice3 #{rname.ljust(9)} #{idx.to_s.rjust(3)},#{len.to_s.rjust(3)} ", str.dup) { |s| s.bytesplice(idx, len, "XY") }
  end
  show_bang("bytesplice5 #{rname.ljust(9)} 0,1,src 1,1 ", str.dup) { |s| s.bytesplice(0, 1, "abcd", 1, 1) }
  show_bang("bytesplice-range #{rname.ljust(9)} 0..1 ", str.dup) { |s| s.bytesplice(0..1, "XY") }
  show_bang("bytesplice-range #{rname.ljust(9)} 1... ", str.dup) { |s| s.bytesplice(1...3, "XY") }
end
show("bytesplice frozen") { "abc".freeze.bytesplice(0, 1, "x") }

# --------------------------------------------------------------------------
# scrub / scrub! - replacing the bytes that are not valid in the encoding.
# --------------------------------------------------------------------------

SCRUB_SOURCES = {
  "valid-utf8"  => u("héllo"),
  "lone-ff"     => u(+"a\xffb"),
  "truncated"   => u(+"a\xe3\x81"),
  "overlong"    => u(+"\xc0\xafx"),
  "all-bad"     => u(+"\xff\xfe\xfd"),
  "binary"      => b(0xff, 0xfe),
  "ascii-bad"   => (+"a\xffb").force_encoding(Encoding::US_ASCII),
  "empty"       => u(+""),
}

SCRUB_SOURCES.each do |sname, src|
  show("scrub  #{sname.ljust(11)} default") { src.dup.scrub }
  show("scrub  #{sname.ljust(11)} string ") { src.dup.scrub("?") }
  show("scrub  #{sname.ljust(11)} empty  ") { src.dup.scrub("") }
  show("scrub  #{sname.ljust(11)} block  ") { src.dup.scrub { |bad| "<" + bytes_of(bad) + ">" } }
  show("valid? #{sname.ljust(11)}        ") { src.valid_encoding? }
  show_bang("scrub! #{sname.ljust(11)} default", src.dup) { |s| s.scrub! }
end

# --------------------------------------------------------------------------
# undump - the inverse of dump.
# --------------------------------------------------------------------------

UNDUMP_INPUTS = [
  '"abc"', '""', '"\\n"', '"\\t"', '"\\\\"', '"\\""', '"\\x00"', '"\\xFF"',
  '"\\u00e9"', '"\\u{1F600}"', '"a\\u0000b"', '"abc".force_encoding("UTF-8")',
  'abc', '"abc', 'abc"', '"a"+"b"', '"\\M-a"', '"\\C-a"', '"\\e"', '"\\s"',
  '"\\u{}"', '"\\u{41 42}"', '"\\0"',
]

UNDUMP_INPUTS.each do |src|
  show("undump #{src.inspect.ljust(34)}") { src.undump }
end
each_receiver do |rname, str|
  show("dump-undump #{rname.ljust(9)}") { str.dump.undump }
end

# --------------------------------------------------------------------------
# unicode_normalize / unicode_normalized?
# --------------------------------------------------------------------------

NORM_FORMS = [nil, :nfc, :nfd, :nfkc, :nfkd, :bogus]
NORM_SOURCES = {
  "precomp"  => u("égal"),
  "combining"=> u("égal"),
  "ligature" => u("ﬁ"),
  "roman"    => u("Ⅷ"),
  "ascii"    => u("abc"),
  "empty"    => u(""),
}

NORM_SOURCES.each do |sname, src|
  NORM_FORMS.each do |form|
    show("normalize  #{sname.ljust(10)} #{form.inspect.ljust(6)}") { form ? src.dup.unicode_normalize(form) : src.dup.unicode_normalize }
    show("normalized? #{sname.ljust(10)} #{form.inspect.ljust(6)}") { form ? src.unicode_normalized?(form) : src.unicode_normalized? }
  end
end
show("normalize binary") { b(0xff).unicode_normalize }
show("normalized? binary") { b(0xff).unicode_normalized? }

# --------------------------------------------------------------------------
# append_as_bytes - concatenation that never transcodes.
# --------------------------------------------------------------------------

each_receiver do |rname, str|
  show_bang("append_as_bytes #{rname.ljust(9)} str ", str.dup) { |s| s.append_as_bytes("ab") }
  show_bang("append_as_bytes #{rname.ljust(9)} utf8", str.dup) { |s| s.append_as_bytes(u("é")) }
  show_bang("append_as_bytes #{rname.ljust(9)} int ", str.dup) { |s| s.append_as_bytes(65) }
  show_bang("append_as_bytes #{rname.ljust(9)} many", str.dup) { |s| s.append_as_bytes("a", 66, u("é")) }
  show_bang("append_as_bytes #{rname.ljust(9)} none", str.dup) { |s| s.append_as_bytes }
  show_bang("append_as_bytes #{rname.ljust(9)} nil ", str.dup) { |s| s.append_as_bytes(nil) }
end

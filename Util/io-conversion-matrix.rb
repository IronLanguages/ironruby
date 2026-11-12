# Cross-checks IO transcoding - the conversion a stream does on the way in and
# on the way out - against CRuby.
#
#   ruby     Util/io-conversion-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/io-conversion-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt | grep -c '^-'
#
# A stream carries two encodings and a set of conversion options, and every
# combination of them shows up somewhere between the bytes on disk and the
# String the reader gets, or between the String the writer hands over and the
# bytes that land on disk. Each row prints the bytes rather than the text, so a
# conversion that did not happen cannot hide behind a String that looks right.

require "tmpdir"

DIR = Dir.mktmpdir("io-conv")
PATH = File.join(DIR, "sample")

def show(label)
  result = yield
  result = result.inspect
rescue Exception => e
  result = e.class.to_s + ": " + e.message.to_s
ensure
  puts label + " => " + result
end

def written(*args, **options)
  File.open(PATH, *args, **options) { |f| yield f }
  File.binread(PATH).bytes
end

def stored(bytes)
  File.binwrite(PATH, bytes.pack("C*"))
end

UTF8 = "héllo"          # one character that only ISO-8859-1 and UTF-8 share
CJK  = "日"              # one that US-ASCII and ISO-8859-1 have no room for
NL   = "a\nb\r\nc\rd"

# --------------------------------------------------------------------------
# Writing: the text is converted into the stream's external encoding.
# --------------------------------------------------------------------------

["w:utf-8", "w:iso-8859-1", "w:us-ascii", "w:utf-16be", "w:binary", "w", "wb"].each do |mode|
  show("W #{mode.ljust(14)} accented") { written(mode) { |f| f.write UTF8 } }
  show("W #{mode.ljust(14)} cjk     ") { written(mode) { |f| f.write CJK } }
  show("W #{mode.ljust(14)} ascii   ") { written(mode) { |f| f.write "abc" } }
  show("W #{mode.ljust(14)} binary  ") { written(mode) { |f| f.write "a\xff".b } }
  show("W #{mode.ljust(14)} puts    ") { written(mode) { |f| f.puts "abc" } }
  show("W #{mode.ljust(14)} print   ") { written(mode) { |f| f.print "abc" } }
  show("W #{mode.ljust(14)} shovel  ") { written(mode) { |f| f << "abc" } }
  show("W #{mode.ljust(14)} multi   ") { written(mode) { |f| f.write "a", "b" } }
  show("W #{mode.ljust(14)} count   ") { File.open(PATH, mode) { |f| f.write "abc" } }
end

WRITE_OPTIONS = [
  ["undef      ", { undef: :replace }],
  ["undef+repl ", { undef: :replace, replace: "_" }],
  ["invalid    ", { invalid: :replace }],
  ["crlf       ", { crlf_newline: true }],
  ["cr         ", { cr_newline: true }],
  ["universal  ", { universal_newline: true }],
]

WRITE_OPTIONS.each do |name, options|
  ["w:utf-8", "w:us-ascii", "w"].each do |mode|
    show("WOPT #{mode.ljust(11)} #{name} accented") { written(mode, **options) { |f| f.write UTF8 } }
    show("WOPT #{mode.ljust(11)} #{name} newlines") { written(mode, **options) { |f| f.write NL } }
  end
end

# --------------------------------------------------------------------------
# Reading: the bytes are read as the external encoding and handed back in the
# internal one.
# --------------------------------------------------------------------------

SOURCES = {
  "utf8"     => [0x68, 0xc3, 0xa9, 0x6c],
  "cjk"      => [0xe6, 0x97, 0xa5],
  "invalid"  => [0x61, 0xff, 0x62],
  "newlines" => [0x61, 0x0d, 0x0a, 0x62, 0x0d, 0x63, 0x0a],
}

READ_MODES = ["r", "rb", "r:utf-8", "r:utf-8:iso-8859-1", "r:utf-8:us-ascii",
              "r:binary:utf-8", "r:utf-8:utf-8", "r:iso-8859-1:utf-8"]

SOURCES.each do |sname, bytes|
  READ_MODES.each do |mode|
    stored(bytes)
    show("R #{sname.ljust(9)} #{mode.ljust(20)} read ") { File.open(PATH, mode) { |f| s = f.read; [s.encoding.name, s.bytes] } }
    stored(bytes)
    show("R #{sname.ljust(9)} #{mode.ljust(20)} gets ") { File.open(PATH, mode) { |f| s = f.gets; s && [s.encoding.name, s.bytes] } }
    stored(bytes)
    show("R #{sname.ljust(9)} #{mode.ljust(20)} lines") { File.open(PATH, mode) { |f| f.readlines.map { |l| [l.encoding.name, l.bytes] } } }
    stored(bytes)
    show("R #{sname.ljust(9)} #{mode.ljust(20)} getc ") { File.open(PATH, mode) { |f| c = f.getc; c && [c.encoding.name, c.bytes] } }
  end
end

READ_OPTIONS = [
  ["invalid    ", { invalid: :replace }],
  ["undef      ", { undef: :replace }],
  ["both+repl  ", { invalid: :replace, undef: :replace, replace: "#" }],
  ["universal  ", { universal_newline: true }],
  ["newline    ", { newline: :universal }],
]

READ_OPTIONS.each do |name, options|
  SOURCES.each do |sname, bytes|
    ["r:utf-8:us-ascii", "r:utf-8:iso-8859-1", "r:utf-8", "r"].each do |mode|
      stored(bytes)
      show("ROPT #{sname.ljust(9)} #{mode.ljust(18)} #{name}") do
        File.open(PATH, mode, **options) { |f| s = f.read; [s.encoding.name, s.bytes] }
      end
    end
  end
end

# --------------------------------------------------------------------------
# The encodings given as options rather than in the mode string.
# --------------------------------------------------------------------------

stored([0x68, 0xc3, 0xa9, 0x6c])
show("OPT external       ") { File.open(PATH, external_encoding: "utf-8") { |f| [f.external_encoding.to_s, f.internal_encoding.inspect] } }
show("OPT internal       ") { File.open(PATH, internal_encoding: "iso-8859-1") { |f| s = f.read; [s.encoding.name, s.bytes] } }
show("OPT both           ") { File.open(PATH, external_encoding: "utf-8", internal_encoding: "iso-8859-1") { |f| s = f.read; [s.encoding.name, s.bytes] } }
show("OPT internal dash  ") { File.open(PATH, external_encoding: "utf-8", internal_encoding: "-") { |f| f.internal_encoding.inspect } }
show("OPT encoding pair  ") { File.open(PATH, encoding: "utf-8:iso-8859-1") { |f| s = f.read; [s.encoding.name, s.bytes] } }
show("OPT twice          ") { File.open(PATH, "r:utf-8", external_encoding: "utf-8") { |f| f.external_encoding.to_s } }
show("OPT mode option    ") { File.open(PATH, mode: "r:utf-8:iso-8859-1") { |f| s = f.read; [s.encoding.name, s.bytes] } }
show("OPT mode twice     ") { File.open(PATH, "r", mode: "r") { |f| f.external_encoding.to_s } }

# --------------------------------------------------------------------------
# set_encoding after the fact, and binmode taking it all away again.
# --------------------------------------------------------------------------

show("SET pair           ") { File.open(PATH) { |f| f.set_encoding("utf-8:iso-8859-1"); s = f.read; [s.encoding.name, s.bytes] } }
show("SET two args       ") { File.open(PATH) { |f| f.set_encoding("utf-8", "iso-8859-1"); s = f.read; [s.encoding.name, s.bytes] } }
show("SET with options   ") { File.open(PATH) { |f| f.set_encoding("utf-8", "us-ascii", undef: :replace); s = f.read; [s.encoding.name, s.bytes] } }
stored([0x61, 0x0d, 0x0a, 0x62])
show("SET newline        ") { File.open(PATH) { |f| f.set_encoding("utf-8", "iso-8859-1", newline: :universal); f.read.bytes } }
show("SET newline binmode") { File.open(PATH) { |f| f.set_encoding("utf-8", "iso-8859-1", newline: :universal); f.binmode; f.read.bytes } }

at_exit { require "fileutils"; FileUtils.remove_entry(DIR) }

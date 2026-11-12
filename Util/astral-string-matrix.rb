# Cross-checks every positional String, Symbol, MatchData, Regexp and StringScanner
# method against CRuby on strings with characters above U+FFFF.
#
#   ruby     Util/astral-string-matrix.rb > /tmp/astral.mri
#   ./ir.sh  Util/astral-string-matrix.rb > /tmp/astral.ir
#   diff -u  /tmp/astral.mri /tmp/astral.ir
#
# IronRuby keeps a String's characters as UTF-16, so a character above U+FFFF is two
# CLR chars (a surrogate pair). Ruby counts it as one character, so every index that
# goes into or comes out of a String has to be translated - and a regex has to match,
# step over and stop at a pair as a whole. A row that disagrees names the call.
#
# The receivers mix ASCII, BMP (é, 日本), astral (😀 U+1F600, 𝒳 U+1D4B3, 𠀀 U+20000),
# ZWJ sequences, regional-indicator flags, combining marks and a variation selector,
# plus the same text in BINARY, UTF-16LE and UTF-32LE, and pure ASCII/BMP controls.
#
# Results are rendered as code points (or bytes for a broken or binary string) with
# their encoding, so neither a wrong #inspect nor a wrong Encoding can hide a
# difference.

require "strscan"
require "stringio"

def repr(value)
  case value
  when nil, true, false, Integer then value.inspect
  when String
    enc = value.encoding
    if enc == Encoding::BINARY || !value.valid_encoding?
      "#{enc.name}:<" + value.bytes.map { |b| "%02x" % b }.join(" ") + ">"
    else
      "#{enc.name}:[" + value.codepoints.map { |c| c < 0x80 && c > 0x20 ? c.chr : "U+%04X" % c }.join(" ") + "]"
    end
  when Symbol then ":" + repr(value.to_s)
  when Array  then "[" + value.map { |v| repr(v) }.join(", ") + "]"
  when Hash   then "{" + value.map { |k, v| "#{repr(k)}=>#{repr(v)}" }.join(", ") + "}"
  when MatchData then "#<MatchData #{repr(value.to_a)} begin=#{value.begin(0)}>"
  when Range  then "#{repr(value.begin)}..#{repr(value.end)}"
  else value.class.to_s + "(" + value.inspect + ")"
  end
end

def show(label)
  result = repr(yield)
rescue Exception => e
  result = e.class.to_s + ": " + e.message.to_s.sub(/ for .*/, "")
ensure
  puts label + " => " + result
end

# A mutating call on a fresh copy: the answer and the receiver it left behind.
def show_bang(label, str)
  s = str.dup
  value = yield s
  result = repr(value) + " self=" + repr(s)
rescue Exception => e
  result = e.class.to_s + ": " + e.message.to_s.sub(/ for .*/, "")
ensure
  puts label + " => " + result
end

FAMILY = "\u{1F468}‍\u{1F469}‍\u{1F467}"
FLAGS  = "\u{1F1EF}\u{1F1F5}\u{1F1FA}\u{1F1F8}"

STRINGS = {
  "ascii"    => "hello world",
  "bmp"      => "héllo 日本",
  "one"      => "\u{1F600}",
  "a-e-b"    => "a\u{1F600}b",
  "mixed"    => "é日本\u{1F600}𝒳\u{20000}x",
  "three"    => "\u{1F600}\u{1F600}\u{1F600}",
  "family"   => "#{FAMILY} family",
  "flags"    => "#{FLAGS}!",
  "combine"  => "é\u{1F600}ä",
  "vs16"     => "❤️\u{1F600}",
  "lines"    => "x\u{1F600}\n\u{1F600}y\n\u{1F600}",
  "words"    => "\u{1F600} ab \u{1D4B3}c d\u{20000}e ",
  "empty"    => "",
}

KEYS = STRINGS.keys

def each_str
  STRINGS.each { |k, s| yield k, s }
end

# ---------------------------------------------------------------- String reading
each_str do |k, s|
  show("#{k}.length") { s.length }
  show("#{k}.size") { s.size }
  show("#{k}.bytesize") { s.bytesize }
  show("#{k}.chars") { s.chars }
  show("#{k}.codepoints") { s.codepoints }
  show("#{k}.grapheme_clusters") { s.grapheme_clusters }
  show("#{k}.reverse") { s.reverse }
  show("#{k}.chr") { s.chr }
  show("#{k}.chop") { s.chop }
  show("#{k}.ord") { s.ord }
  show("#{k}.sum") { s.sum }
  show("#{k}.hash==dup") { s.hash == s.dup.hash }
  show("#{k}.inspect.length") { s.inspect.length }
  show("#{k}.dump") { s.dump }
  show("#{k}.unpack(U*)") { s.unpack("U*") }
  show("#{k}.each_char.to_a") { s.each_char.to_a }
  show("#{k}.lines") { s.lines }
  show("#{k}.upcase") { s.upcase }
  show("#{k}.capitalize") { s.capitalize }
  show("#{k}.swapcase") { s.swapcase }
  show("#{k}.succ") { s.succ }
  show("#{k}.squeeze") { s.squeeze }
  show("#{k}.count(\\u{1F600})") { s.count("\u{1F600}") }
  show("#{k}.delete(\\u{1F600})") { s.delete("\u{1F600}") }
  show("#{k}.tr(\\u{1F600},X)") { s.tr("\u{1F600}", "X") }
  show("#{k}.tr(a-z,\\u{1F600})") { s.tr("a-z", "\u{1F600}") }
  show("#{k}.center(12,*)") { s.center(12, "*") }
  show("#{k}.ljust(12,\\u{1F600}.)") { s.ljust(12, "\u{1F600}.") }
  show("#{k}.rjust(12,\\u{1D4B3})") { s.rjust(12, "\u{1D4B3}") }
  show("#{k}.%-12s|") { "%-12s|" % s }
  show("#{k}.%12s|") { "%12s|" % s }
  show("#{k}.%.3s|") { "%.3s|" % s }
  show("#{k}.start_with?(first)") { s.start_with?(s[0].to_s) }
  show("#{k}.end_with?(last)") { s.end_with?(s[-1].to_s) }
  show("#{k}.delete_prefix(first)") { s.delete_prefix(s[0].to_s) }
  show("#{k}.delete_suffix(last)") { s.delete_suffix(s[-1].to_s) }
  show("#{k}.to_sym.length") { s.to_sym.length }
  show("#{k}.to_sym[1]") { s.to_sym[1] }
  show("#{k}.to_sym[1,2]") { s.to_sym[1, 2] }
  show("#{k}.to_sym =~ /b/") { s.to_sym =~ /b/ }
  show("#{k}.to_sym.slice(-1)") { s.to_sym.slice(-1) }

  # indexing by character
  n = s.length
  [-n - 1, -n, -2, -1, 0, 1, 2, 3, n - 1, n, n + 1].uniq.each do |i|
    show("#{k}[#{i}]") { s[i] }
    [0, 1, 2, 5].each do |len|
      show("#{k}[#{i},#{len}]") { s[i, len] }
    end
    show("#{k}[#{i}..]") { s[i..] }
    show("#{k}[..#{i}]") { s[..i] }
    show("#{k}[#{i}...-1]") { s[i...-1] }
    show("#{k}.slice(#{i},2)") { s.slice(i, 2) }
    show("#{k}.byteslice(#{i},3)") { s.byteslice(i, 3) }
    show("#{k}.getbyte(#{i})") { s.getbyte(i) }
    show("#{k}.index(\"\",#{i})") { s.index("", i) }
    show("#{k}.rindex(\"\",#{i})") { s.rindex("", i) }
    show("#{k}.index(/./,#{i})") { s.index(/./, i) }
    show("#{k}.rindex(/./,#{i})") { s.rindex(/./, i) }
    show("#{k}.index(/$/,#{i})") { s.index(/$/, i) }
    show("#{k}.match(/.(.)/,#{i})") { m = s.match(/.(.)/, i); m && [m.to_a, m.begin(0), m.end(1), m.pre_match] }
    show("#{k}.match?(/./,#{i})") { s.match?(/./, i) }
    show("#{k}.byteindex(\"\",#{i})") { s.byteindex("", i) }
    show_bang("#{k}.slice!(#{i})", s) { |t| t.slice!(i) }
    show_bang("#{k}.slice!(#{i},2)", s) { |t| t.slice!(i, 2) }
    show_bang("#{k}[#{i}]=Z", s) { |t| t[i] = "Z" }
    show_bang("#{k}[#{i},2]=\\u{1D4B3}", s) { |t| t[i, 2] = "\u{1D4B3}" }
    show_bang("#{k}[#{i}..]=", s) { |t| t[i..] = "" }
    show_bang("#{k}.insert(#{i},\\u{20000})", s) { |t| t.insert(i, "\u{20000}") }
    show_bang("#{k}.bytesplice(#{i},1,-)", s) { |t| t.bytesplice(i, 1, "-") }
    show_bang("#{k}.setbyte(#{i},65)", s) { |t| t.setbyte(i, 65) }
  end

  # substrings of the receiver itself, and of the characters it is made of
  probes = (s.chars + ["b", "x", "\u{1F600}", "\u{1F600}b", "日本", "\n", " ", FAMILY]).uniq
  probes.each do |p|
    pl = p.inspect
    show("#{k}.index(#{pl})") { s.index(p) }
    show("#{k}.index(#{pl},2)") { s.index(p, 2) }
    show("#{k}.index(#{pl},-2)") { s.index(p, -2) }
    show("#{k}.rindex(#{pl})") { s.rindex(p) }
    show("#{k}.rindex(#{pl},2)") { s.rindex(p, 2) }
    show("#{k}.rindex(#{pl},-3)") { s.rindex(p, -3) }
    show("#{k}.byteindex(#{pl})") { s.byteindex(p) }
    show("#{k}.byterindex(#{pl})") { s.byterindex(p) }
    show("#{k}[#{pl}]") { s[p] }
    show("#{k}.include?(#{pl})") { s.include?(p) }
    show("#{k}.partition(#{pl})") { s.partition(p) }
    show("#{k}.rpartition(#{pl})") { s.rpartition(p) }
    show("#{k}.split(#{pl})") { s.split(p) }
    show("#{k}.split(#{pl},2)") { s.split(p, 2) }
    show("#{k}.split(#{pl},-1)") { s.split(p, -1) }
    show("#{k}.count(#{pl})") { s.count(p) }
    show("#{k}.squeeze(#{pl})") { s.squeeze(p) }
    show("#{k}.sub(#{pl},<>)") { s.sub(p, "<>") }
    show("#{k}.gsub(#{pl},<>)") { s.gsub(p, "<>") }
    show("#{k}.scan(#{pl})") { s.scan(p) }
    show("#{k}.start_with?(#{pl})") { s.start_with?(p) }
    show("#{k}.each_line(#{pl})") { s.each_line(p).to_a }
    show("#{k}.chomp(#{pl})") { s.chomp(p) }
    show_bang("#{k}[#{pl}]=Q", s) { |t| t[p] = "Q" }
    show_bang("#{k}.slice!(#{pl})", s) { |t| t.slice!(p) }
    show_bang("#{k}.sub!(#{pl}){$~.begin(0)}", s) { |t| t.sub!(p) { $~.begin(0).to_s } }
  end
end

# ---------------------------------------------------------------- Regexp
REGEXPS = [
  /b/, /./, /./m, /.\z/, /\A.\z/, /\A..\z/, /(.)(.)/, /.+/, /.*/, /.*?b/, /.+?/, /.{2}/, /.{1,3}/,
  /x*/, //, /\b/, /\B/, /$/, /^/, /\z/, /\s/, /\S+/, /\w+/, /\W/, /\W+/, /[^a]/, /[^a]+/,
  /[^\s]+/, /\X/, /\p{Emoji}/, /\p{So}+/, /\u{1F600}/, /\u{1F600}+/, /[\u{1F600}𝒳]/, /(?<e>\u{1F600})(?<r>.)?/,
  /(?<=\u{1F600})./, /(?<!\u{1F600})b/, /(?=b)/, /b|\u{1F600}/, /(\u{1F600})\1/, /\h/, /\R/, /\n./m,
  /./i, /(?m:.)./, /[[:alpha:]]+/, /\p{Han}+/, /(?:.)/, /[.]/, /.(?!.)/, /(?<x>.)\k<x>/,
]

each_str do |k, s|
  REGEXPS.each do |re|
    r = re.inspect
    show("#{k} =~ #{r}") { s =~ re }
    show("#{k}.match(#{r})") do
      m = s.match(re)
      m && [m.to_a, m.size, (0...m.size).map { |g| [m.begin(g), m.end(g), m.offset(g), m.byteoffset(g)] },
            m.pre_match, m.post_match, m.values_at(0, -1), m.captures, m.named_captures,
            (0...m.size).map { |g| m.match_length(g) }]
    end
    show("#{k}.scan(#{r})") { s.scan(re) }
    show("#{k}.scan(#{r}) offsets") { a = []; s.scan(re) { a << $~.offset(0) }; a }
    show("#{k}.gsub(#{r}) block") { s.gsub(re) { "<#{$~.begin(0)}:#{$&}>" } }
    show("#{k}.gsub(#{r},[\\0])") { s.gsub(re, '[\0]') }
    show("#{k}.gsub(#{r},hash)") { s.gsub(re, "\u{1F600}" => "S", "b" => "B") }
    show("#{k}.sub(#{r}) $`$'") { s.sub(re) { "(#{$`.length},#{$'.length})" } }
    show("#{k}.split(#{r})") { s.split(re) }
    show("#{k}.split(#{r},-1)") { s.split(re, -1) }
    show("#{k}.split(#{r},2)") { s.split(re, 2) }
    show("#{k}.index(#{r})") { s.index(re) }
    show("#{k}.index(#{r},3)") { s.index(re, 3) }
    show("#{k}.rindex(#{r})") { s.rindex(re) }
    show("#{k}.rindex(#{r},3)") { s.rindex(re, 3) }
    show("#{k}.byteindex(#{r})") { s.byteindex(re) }
    show("#{k}.byterindex(#{r})") { s.byterindex(re) }
    show("#{k}[#{r}]") { s[re] }
    show("#{k}[#{r},1]") { s[re, 1] }
    show("#{k}.slice(#{r},-1)") { s.slice(re, -1) }
    show("#{k}.partition(#{r})") { s.partition(re) }
    show("#{k}.rpartition(#{r})") { s.rpartition(re) }
    show("#{k}.start_with?(#{r})") { s.start_with?(re) }
    show("#{r}.match(#{k},2)") { m = re.match(s, 2); m && [m.to_a, m.offset(0)] }
    show("#{r}.match?(#{k},-2)") { re.match?(s, -2) }
    show("#{r} =~ #{k} $~") { re =~ s; $~ && [$~.begin(0), $~.end(0), $1] }
    show("#{r}.match(#{k}).deconstruct") { m = re.match(s); m && m.deconstruct }
    show_bang("#{k}[#{r}]=Q", s) { |t| t[re] = "Q" }
    show_bang("#{k}.slice!(#{r})", s) { |t| t.slice!(re) }
    show_bang("#{k}.gsub!(#{r}){$~.end(0)}", s) { |t| t.gsub!(re) { $~.end(0).to_s } }
  end
end

# named-group helpers on the richest subject
m = STRINGS["mixed"].match(/(?<a>日)(?<b>.)(?<c>.)(?<d>.)/)
show("named begin/end") { %w[a b c d].map { |n| [m.begin(n), m.end(n), m.offset(n), m.byteoffset(n)] } }
show("named begin(sym)") { m.begin(:c) }
show("named [] / values") { [m[:c], m["d"], m.values_at(:b, :d)] }
show("$~ after =~") { STRINGS["mixed"] =~ /𝒳(.)/; [$~.begin(0), $~.begin(1), $`, $', $1, Regexp.last_match(1), $~.pre_match.length] }

# ---------------------------------------------------------------- StringScanner
each_str do |k, s|
  show("#{k} ss getch/pos/charpos") do
    ss = StringScanner.new(s)
    a = []
    until ss.eos?
      c = ss.getch
      a << [c, ss.pos, ss.charpos]
    end
    a
  end
  show("#{k} ss scan(/./) positions") do
    ss = StringScanner.new(s)
    a = []
    while (t = ss.scan(/./m))
      a << [t, ss.pos, ss.charpos, ss.pre_match, ss.matched_size]
    end
    a << ss.rest
    a
  end
  show("#{k} ss scan(/x*/)") do
    ss = StringScanner.new(s)
    a = []
    8.times do
      a << [ss.scan(/x*/), ss.pos, ss.charpos]
      ss.getch
    end
    a
  end
  show("#{k} ss scan_until(/b/)") do
    ss = StringScanner.new(s)
    [ss.scan_until(/b/), ss.pos, ss.charpos, ss.pre_match, ss.post_match, ss.matched, ss.rest, ss.rest_size]
  end
  show("#{k} ss skip_until(/\\u{1F600}/)") do
    ss = StringScanner.new(s)
    [ss.skip_until(/\u{1F600}/), ss.pos, ss.charpos, ss.check(/./), ss.peek(4), ss.exist?(/b/), ss.search_full(/b/, false, false)]
  end
  show("#{k} ss captures") do
    ss = StringScanner.new(s)
    ss.scan_until(/(.)(.)/) && [ss[0], ss[1], ss[2], ss.captures, ss.values_at(0, 1), ss.pre_match, ss.post_match, ss.charpos]
  end
  show("#{k} ss get_byte/unscan") do
    ss = StringScanner.new(s)
    a = [ss.get_byte, ss.pos, ss.charpos]
    ss.unscan
    a << ss.pos
    ss.scan(/./)
    a << ss.pos << ss.charpos
    ss.unscan
    a << ss.pos
    ss.pos = [s.bytesize, 5].min
    a << ss.charpos << ss.rest
    a
  end
  show("#{k} ss beginning_of_line?") do
    ss = StringScanner.new(s)
    a = []
    until ss.eos?
      ss.getch
      a << ss.beginning_of_line?
    end
    a
  end
  show("#{k} ss scan_byte/peek_byte") do
    ss = StringScanner.new(s)
    [ss.peek_byte, ss.scan_byte, ss.pos, ss.charpos]
  end
end

# ---------------------------------------------------------------- StringIO
each_str do |k, s|
  show("#{k} StringIO getc") do
    io = StringIO.new(s)
    a = []
    while (c = io.getc)
      a << [c, io.pos]
    end
    a
  end
  show("#{k} StringIO read/ungetc") do
    io = StringIO.new(s.dup)
    c = io.getc
    io.ungetc(c) if c
    [io.pos, io.read(3), io.pos, io.gets, io.each_char.to_a]
  end
end

# ---------------------------------------------------------------- other encodings
bin = "a\u{1F600}b".b
show("binary length") { bin.length }
show("binary [1]") { bin[1] }
show("binary [1,4]") { bin[1, 4] }
show("binary index(b)") { bin.index("b") }
show("binary =~ /b/n") { bin =~ /b/n }
show("binary scan(/./n)") { bin.scan(/./n).size }
show("binary match(/.b/n).offset") { bin.match(/.b/n).offset(0) }
show("binary split('')") { bin.split("").size }
show("latin1 index") { "a\xE9b".force_encoding("ISO-8859-1").index("b".encode("ISO-8859-1")) }
show("broken length") { "a\xF0\x9F\x98b".length }
show("broken [1]") { "a\xF0\x9F\x98b"[1] }
show("broken index") { "a\xF0\x9F\x98b".index("b") }
show("broken chars") { "a\xF0\x9F\x98b".chars }
show("broken+astral [3]") { "\u{1F600}\xFFb"[2] }
show("broken+astral index") { "\u{1F600}\xFFb".index("b") }
%w[UTF-16LE UTF-16BE UTF-32LE].each do |e|
  u = "a\u{1F600}b\u{1D4B3}c".encode(e)
  show("#{e} length") { u.length }
  show("#{e} [1]") { u[1] }
  show("#{e} [2,3]") { u[2, 3] }
  show("#{e} chars") { u.chars }
  show("#{e} index(b)") { u.index("b".encode(e)) }
  show("#{e} rindex(c)") { u.rindex("c".encode(e)) }
  show("#{e} reverse") { u.reverse }
end

# ---------------------------------------------------------------- building astral strings
show("<< then index") { t = +"ab"; t << "\u{1F600}" << "c"; [t.length, t.index("c"), t[3], t[2]] }
show("concat long") { t = "\u{1F600}x" * 300; [t.length, t.index("x", 401), t[599], t.rindex("\u{1F600}"), t[450, 3]] }
show("long astral scan") { t = "\u{1F600}ab" * 200; [t.scan(/b/).size, t =~ /b\z/, t.index("a", 500), t.rindex("a", 300)] }
show("long gsub offsets") { t = "\u{1F600}ab" * 100; t.gsub(/b/) { $~.begin(0).to_s }.length }
show("mutate then index") { t = "\u{1F600}" * 100 + "x"; t.index("x"); t[0] = "y"; [t.index("x"), t[1], t.length] }
show("long []= then index") do
  # a long string keeps a character index; []= of one BMP character for another keeps it valid
  t = "ab\u{1F600}c" * 200
  a = []
  250.times do |j|
    t[j * 3] = (j % 7 == 0) ? "\u{1F600}" : "Z"
    a << t[j * 3 + 1] << t.index("c", j * 2)
  end
  a + [t.length, t[745, 5], t.rindex("\u{1F600}"), t.byteindex("c", 1000)]
end
show("long slice! then index") { t = "ab\u{1F600}c" * 100; t.slice!(5, 3); t.slice!(300); [t.length, t[290, 4], t.index("\u{1F600}", 200)] }
show("mutate to bmp") { t = "a\u{1F600}b"; t.index("b"); t[1] = "c"; [t.index("b"), t.length] }
show("mutate to astral") { t = +"acb"; t.index("b"); t[1] = "\u{1F600}"; [t.index("b"), t.length, t[2]] }
show("setbyte") { t = +"a\u{1F600}b"; t.setbyte(0, 0x7a); [t.index("b"), t[1]] }
show("force_encoding") { t = "a\u{1F600}b".b; t.force_encoding("UTF-8"); [t.index("b"), t[1], t =~ /b/] }
show("each_char index") { t = "a\u{1F600}b\u{1F600}c"; t.each_char.map { |c| t.index(c) } }
show("s[s.index(b)]") { t = "a\u{1F600}b"; t[t.index("b")] }
show("sprintf %c") { "%c|%-3c|" % [0x1F600, 0x1D4B3] }
show("Integer#chr") { 0x1F600.chr("UTF-8").length }
show("string * 3") { ("\u{1F600}" * 3).length }
show("each_grapheme_cluster") { "#{FAMILY}x#{FLAGS}é".each_grapheme_cluster.map(&:length) }
show("unicode_normalize") { "é\u{1F600}".unicode_normalize(:nfc).length }

# Cross-checks File's pure path methods against CRuby, one path shape at a time.
#
#   ruby     Util/file-path-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/file-path-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt
#
# Same shape as Util/format-matrix.rb: every case prints a *literal* label, so a
# diff line names exactly which input disagrees, and nothing in the harness
# depends on the code under test (no #inspect of the result, no File.join to
# build the inputs).  Results are rendered byte by byte - printable ASCII
# verbatim, everything else as <hh> - so a wrong encoding or a stray NUL shows
# up as a diff rather than as unprintable output.
#
# Only methods that are pure string manipulation are included; anything that
# touches the filesystem would make the output depend on the machine.
#
# Expected differences, as of the last run (7 lines out of 1440):
#
#   6  "incompat ..." - CRuby and IronRuby disagree about *which* encodings are
#      ASCII-incompatible (IronRuby exposes the EBCDIC code pages .NET has), so
#      the two runs iterate different lists.  The behaviour under test - that a
#      non-ASCII-compatible path raises Encoding::CompatibilityError - matches.
#   1  File.join("a", :b) - IronRuby's to_path/to_str conversion accepts a
#      Symbol where CRuby raises TypeError.  That is a conversion-protocol bug,
#      not a path bug.
#
# Anything beyond those is a real difference.

def repr(value)
  case value
  when nil    then "nil"
  when true   then "true"
  when false  then "false"
  when Integer then value.to_s
  when Array  then "[" + value.map { |v| repr(v) }.join(", ") + "]"
  when String
    out = String.new
    value.each_byte do |b|
      out << ((b >= 0x20 && b < 0x7f) ? b.chr : ("<" + b.to_s(16).rjust(2, "0") + ">"))
    end
    "|" + out + "|" + value.encoding.name
  else value.class.to_s
  end
end

def show(label, &block)
  result = begin
    repr(block.call)
  rescue StandardError => e
    e.class.to_s + ": " + e.message.gsub(/0x[0-9a-f]+/, "0xXX")
  end
  puts label + " => " + result
end

# --------------------------------------------------------------------------
# Path shapes.  Each entry is [literal label, path].
# --------------------------------------------------------------------------

PATHS = [
  ["empty",          ""],
  ["dot",            "."],
  ["dotdot",         ".."],
  ["dot-slash",      "./"],
  ["dotdot-slash",   "../"],
  ["slash",          "/"],
  ["slash-dot",      "/."],
  ["2slash",         "//"],
  ["3slash",         "///"],
  ["5slash",         "/////"],
  ["foo",            "foo"],
  ["slash-foo",      "/foo"],
  ["foo-slash",      "foo/"],
  ["slash-foo-slash", "/foo/"],
  ["2slash-foo-2slash", "//foo//"],
  ["5slash-foo-bar-slash", "/////foo/bar/"],
  ["foo-bar",        "foo/bar"],
  ["slash-foo-bar",  "/foo/bar"],
  ["deep",           "/foo/bar/baz"],
  ["repeated-inner", "/holy///schnikies//w00t.bin"],
  ["trailing-dot",   "/foo/."],
  ["trailing-dotslash", "/foo/./"],
  ["dotdot-tail",    "/foo/../."],
  ["rel-dotdot",     "foo/../"],
  ["rel-dot-b",      "./b/./"],
  ["ext",            "/foo/bar.txt"],
  ["ext-only",       ".txt"],
  ["ext-dot-end",    "foo."],
  ["ext-dot-end2",   "foo.."],
  ["ext-hidden",     ".config"],
  ["ext-hidden2",    ".config.d"],
  ["ext-multi",      "a.b.c"],
  ["ext-slash-dot",  "/.x"],
  ["backslash",      "foo\\bar"],
  ["backslash-lead", "\\foo"],
  ["backslash-abs",  "/foo\\bar"],
  ["backslash-mix",  "foo/bar\\baz"],
  ["drive",          "C:/foo/bar"],
  ["drive-bs",       "C:\\foo\\bar"],
  ["space",          " foo "],
  ["tilde-only",     "~"],
  ["tilde-slash",    "~/x"],
  ["utf8",           "/\u00e9t\u00e9/caf\u00e9.txt"],
  ["nul",            "a\0b"],
]

SUFFIXES = [["none", nil], ["txt", ".txt"], ["star", ".*"], ["bar", "bar"], ["empty", ""]]

# --------------------------------------------------------------------------
# dirname, with and without a level
# --------------------------------------------------------------------------

PATHS.each do |label, path|
  show("dirname " + label) { File.dirname(path) }
  [0, 1, 2, 3, 100].each do |level|
    show("dirname#{level} " + label) { File.dirname(path, level) }
  end
end
show("dirname-negative") { File.dirname("/a/b", -1) }
show("dirname-to_int") do
  o = Object.new
  def o.to_int; 2; end
  File.dirname("/a/b/c/d", o)
end

# --------------------------------------------------------------------------
# basename (bare and with every suffix), extname, split
# --------------------------------------------------------------------------

PATHS.each do |label, path|
  show("basename " + label) { File.basename(path) }
  SUFFIXES.each do |slabel, suffix|
    next if suffix.nil?
    show("basename " + label + " sfx:" + slabel) { File.basename(path, suffix) }
  end
  show("extname " + label) { File.extname(path) }
  show("split " + label) { File.split(path) }
end

# --------------------------------------------------------------------------
# join
# --------------------------------------------------------------------------

JOIN_PARTS = ["", "/", "//", "a", "a/", "/a", "a//", "\\a", "a\\", ".", "..", "a\0b"]

JOIN_PARTS.each do |a|
  show("join1 " + a.inspect) { File.join(a) }
  JOIN_PARTS.each do |b|
    show("join2 " + a.inspect + "," + b.inspect) { File.join(a, b) }
  end
end
show("join-none") { File.join }
show("join-nested") { File.join("a", ["b", "c"], "d") }
show("join-nested-deep") { File.join(["a", ["b", ["c"]]]) }
show("join-recursive") do
  a = ["a"]
  a << a
  File.join(a)
end
show("join-to_str") do
  o = Object.new
  def o.to_str; "x"; end
  File.join("a", o)
end
show("join-to_path") do
  o = Object.new
  def o.to_path; "p"; end
  File.join("a", o)
end
show("join-symbol") { File.join("a", :b) }
show("join-int") { File.join("a", 1) }

# --------------------------------------------------------------------------
# expand_path / absolute_path / absolute_path?, against a fixed base
# --------------------------------------------------------------------------

BASES = [["nil", nil], ["slash", "/"], ["base", "/base"], ["base-slash", "/base/"],
         ["rel", "rel"], ["dot", "."], ["empty", ""]]

PATHS.each do |label, path|
  next if path.include?("\0")
  BASES.each do |blabel, base|
    show("expand " + label + " base:" + blabel) do
      base.nil? ? File.expand_path(path, "/anchor") : File.expand_path(path, base)
    end
  end
  show("absolute? " + label) { File.absolute_path?(path) }
  show("absolute " + label) { File.absolute_path(path, "/anchor") }
end

# --------------------------------------------------------------------------
# Encodings: every path method must hand back the encoding it was given.
# --------------------------------------------------------------------------

%w[US-ASCII UTF-8 EUC-JP Shift_JIS ISO-8859-1 BINARY].each do |enc|
  p = "/foo/bar.txt".encode(enc) rescue next
  show("enc dirname " + enc)  { File.dirname(p) }
  show("enc basename " + enc) { File.basename(p) }
  show("enc extname " + enc)  { File.extname(p) }
  show("enc expand " + enc)   { File.expand_path(p) }
  show("enc join " + enc)     { File.join(p, p) }
  show("enc split " + enc)    { File.split(p) }
  show("enc to_path " + enc)  { File.path(p) }
end

# Non-ASCII-compatible encodings must be rejected outright.
Encoding.list.reject(&:ascii_compatible?).reject(&:dummy?).each do |enc|
  p = "/foo/bar".encode(enc) rescue next
  show("incompat dirname " + enc.name)  { File.dirname(p) }
  show("incompat basename " + enc.name) { File.basename(p) }
  show("incompat path " + enc.name)     { File.path(p) }
end

# --------------------------------------------------------------------------
# fnmatch
# --------------------------------------------------------------------------

FNM_FLAGS = [
  ["0", 0],
  ["NOESCAPE", File::FNM_NOESCAPE],
  ["PATHNAME", File::FNM_PATHNAME],
  ["DOTMATCH", File::FNM_DOTMATCH],
  ["CASEFOLD", File::FNM_CASEFOLD],
  ["EXTGLOB", File::FNM_EXTGLOB],
  ["PATH|DOT", File::FNM_PATHNAME | File::FNM_DOTMATCH],
]

FNM_CASES = [
  ["*", "cat"], ["*", ".cat"], ["*", "cat/dog"], ["*/*", "cat/dog"],
  ["**/*", "a/b/c"], ["**", "a/b"], ["c*", "cat"], ["c?t", "cat"],
  ["c\\?t", "c?t"], ["c\\?t", "cat"], ["[a-z]", "q"], ["[a-z]", "Q"],
  ["[^a-z]", "Q"], ["[!a-z]", "Q"], ["[]]", "]"], ["[/]", "/"],
  ["a[/]b", "a/b"], ["?", "/"], ["*", "/"], ["a?b", "a/b"],
  ["*.txt", "a.txt"], ["*.txt", ".a.txt"], [".*", ".a"],
  ["{a,b}", "a"], ["{a,b}", "b"], ["{a,b}", "{a,b}"],
  ["a{b,c}d", "abd"], ["*{a,b}", "xb"],
  ["CAT", "cat"], ["cat", "CAT"],
  ["[[:alpha:]]", "a"], ["[[:digit:]]", "a"],
  ["\\", "\\"], ["[", "["], ["a**b", "ab"], ["a**b", "a/b"],
]

FNM_CASES.each do |pattern, path|
  FNM_FLAGS.each do |flabel, flags|
    show("fnmatch " + pattern.inspect + " " + path.inspect + " " + flabel) do
      File.fnmatch(pattern, path, flags)
    end
  end
end
show("fnmatch-to_int") do
  o = Object.new
  def o.to_int; File::FNM_PATHNAME; end
  File.fnmatch("a/b", "a/b", o)
end
show("fnmatch-bad-flags") { File.fnmatch("a", "a", "x") }

# --------------------------------------------------------------------------
# path / to_path coercion
# --------------------------------------------------------------------------

show("path-string") { File.path("a/b") }
show("path-nul") { File.path("a\0b") }
show("path-to_path") do
  o = Object.new
  def o.to_path; "from_to_path"; end
  File.path(o)
end
show("path-to_path-nul") do
  o = Object.new
  def o.to_path; "a\0b"; end
  File.path(o)
end
show("path-to_path-nonstring") do
  o = Object.new
  def o.to_path; 1; end
  File.path(o)
end
show("path-int") { File.path(1) }
show("path-nil") { File.path(nil) }

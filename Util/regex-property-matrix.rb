# Differential matrix for Onigmo character properties in regexps: \p{...}, \P{...},
# \p{^...}, POSIX brackets, and the character-class operations over them.
#
#   ruby    Util/regex-property-matrix.rb > /tmp/prop.mri
#   ./ir.sh Util/regex-property-matrix.rb > /tmp/prop.ir
#   diff /tmp/prop.mri /tmp/prop.ir
#
# Every property name Onigmo accepts is taken from the generated table in
# Src/Ruby/Builtins/UnicodeProperties.Generated.cs (made from CRuby's own name2ctype.h,
# see Util/gen-unicode-properties.rb), so the list is exactly CRuby's. Each is used plain,
# negated both ways, inside a class, negated inside a class, intersected, subtracted,
# unioned with astral members, quantified and case-insensitive, and scanned over a spread
# of probe strings: ASCII, every major script, BMP and astral letters, digits, '#', '*',
# emoji with and without variation selectors, modifiers, ZWJ sequences, flags, keycaps,
# private use, unassigned and noncharacters. A line shows the matches as code points, so
# a match that splits a surrogate pair (only possible under IronRuby) shows up as such.
#
# Pass a substring to restrict the names: ruby Util/regex-property-matrix.rb emoji
#
# ICASE=1 adds every property and POSIX class under /i. That compares case folding more
# than properties: .NET's IgnoreCase folds per UTF-16 code unit with its own equivalence
# table, so it misses Onigmo's non-BMP case pairs (U+10400/U+10428) and a few BMP folds
# (U+017F LONG S to 's'), and it applies K/KELVIN SIGN where Onigmo's [[:ascii:]]/i does not.
ICASE = ENV["ICASE"]

GENERATED = File.expand_path("../Src/Ruby/Builtins/UnicodeProperties.Generated.cs", __dir__)
names_block = File.read(GENERATED)[/_names = new string\[\] \{(.*?)\};/m, 1]
NAMES = names_block.scan(/"([^"]*)"/).flatten
NAMES.select! { |n| n.include?(ARGV[0]) } if ARGV[0]

PROBES = [
  "a", "Z", "0", "9", "_", "#", "*", " ", "\t", "\n", "!", "$", "+", "^", "`", "~", "\x7f", "\u0000",
  "\u{a0}", "\u{e9}", "\u{df}", "\u{b2}", "\u{bd}", "\u{d7}", "\u{130}", "\u{131}", "\u{17f}", "\u{1c5}",
  "\u{301}", "\u{378}", "\u{3b1}", "\u{3a9}", "\u{3f4}", "\u{436}", "\u{4c1}", "\u{5d0}", "\u{628}", "\u{663}", "\u{915}", "\u{93f}",
  "\u{e01}", "\u{10d0}", "\u{1100}", "\u{16a0}", "\u{1e9e}", "\u{200b}", "\u{200c}", "\u{200d}", "\u{2028}",
  "\u{2029}", "\u{2060}", "\u{20ac}", "\u{2122}", "\u{212a}", "\u{2160}", "\u{2190}", "\u{2603}", "\u{263a}",
  "\u{263a}\u{fe0f}", "\u{2764}", "\u{2764}\u{fe0f}", "\u{2b50}", "\u{3000}", "\u{3005}", "\u{3042}", "\u{30a2}",
  "\u{30fc}", "\u{4e00}", "\u{6f22}", "\u{ac00}", "\u{e000}", "\u{fb01}", "\u{fe0f}", "\u{feff}", "\u{ff10}",
  "\u{ff21}", "\u{ff66}", "\u{fffd}", "\u{ffff}",
  "\u{10000}", "\u{10330}", "\u{10400}", "\u{10428}", "\u{1d400}", "\u{1d7ce}", "\u{1f1ef}\u{1f1f5}",
  "\u{1f308}", "\u{1f3fb}", "\u{1f44d}", "\u{1f44d}\u{1f3fd}", "\u{1f600}", "\u{1f602}", "\u{1f9d1}",
  "\u{1f468}\u{200d}\u{1f469}\u{200d}\u{1f467}", "\u{1f3f3}\u{fe0f}\u{200d}\u{1f308}", "1\u{fe0f}\u{20e3}",
  "#\u{fe0f}\u{20e3}", "*\u{20e3}", "\u{1fae8}", "\u{1fa8a}", "\u{20000}", "\u{2000b}", "\u{2a6d6}", "\u{30000}",
  "\u{e0001}", "\u{e0067}", "\u{e0100}", "\u{f0000}", "\u{10fffd}", "\u{10ffff}", "\u{1ffff}",
  "a\u{1f600}b", "\u{1f600}\u{1f600}", "x\u{301}y",
]

def show(matches)
  return "-" if matches.empty?
  matches.map { |m| m.codepoints.map { |c| c.to_s(16) }.join("+") }.join(",")
end

def try(label, source, options = 0)
  re = Regexp.new(source, options)
  puts "#{label}\t" + PROBES.map { |s| m = []; s.scan(re) { m << $~[0] }; show(m) }.join(" ")
rescue Exception => e
  puts "#{label}\t#{e.class}: #{e.message}"
end

puts "== every property =="
NAMES.each do |n|
  try("#{n} plain", "\\p{#{n}}")
  try("#{n} P", "\\P{#{n}}")
  try("#{n} caret", "\\p{^#{n}}")
  try("#{n} P-caret", "\\P{^#{n}}")
  try("#{n} class", "[\\p{#{n}}]")
  try("#{n} negclass", "[^\\p{#{n}}]")
  try("#{n} inter", "[\\p{#{n}}&&[^\\d#*]]")
  try("#{n} subtract", "[\\p{#{n}}&&[^a-z\\u{1f600}\\u{20000}-\\u{2ffff}]]")
  try("#{n} inter-astral", "[\\p{#{n}}&&[\\u{10000}-\\u{1ffff}\\u3042]]")
  try("#{n} inter-neg", "[^\\p{#{n}}&&\\p{^#{n}}]")
  try("#{n} union", "[a\\p{#{n}}\\u{1f4a9}\\u{e0001}]")
  try("#{n} neg-union", "[^\\p{#{n}}\\u{1f600}]")
  try("#{n} plus", "\\p{#{n}}+")
  try("#{n} P-plus", "\\P{#{n}}+")
  try("#{n} neg-lazy", "[^\\p{#{n}}]+?")
  try("#{n} P-count", "\\P{#{n}}{2}")
  try("#{n} icase", "\\p{#{n}}", Regexp::IGNORECASE) if ICASE
end

puts "== spellings =="
%W[Emoji emoji EMOJI Emoji_Presentation EmojiPresentation emoji-presentation Emoji\ Presentation
   Extended_Pictographic ExtPict Alpha alpha ALPHA Alphabetic Word Punct Graph Print Space XDigit
   Han Hani HAN Hiragana Hira Latin Latn Greek Grek In_Greek In_Greek_and_Coptic InGreekAndCoptic
   L Letter Lu Uppercase_Letter Lc L& Any Assigned Cn Unassigned Age=6.0 age=6.0 AGE=6.0 Age=17.0
   IsGreek Is_Greek Script=Greek sc=Grek gc=L Foo Alph\u{e4} _ - \  Emoji\ Component Regional_Indicator
   Grapheme_Cluster_Break=Extend InCB=Linker Common Zyyy Inherited Unknown Zzzz].each do |n|
  try("spell {#{n}}", "\\p{#{n}}")
end

puts "== POSIX brackets =="
%w[alnum alpha ascii blank cntrl digit graph lower print punct space upper xdigit word Alpha foo].each do |n|
  try("[[:#{n}:]]", "[[:#{n}:]]")
  try("[[:^#{n}:]]", "[[:^#{n}:]]")
  try("(?a)[[:#{n}:]]", "(?a)[[:#{n}:]]")
  try("(?a)[[:^#{n}:]]", "(?a)[[:^#{n}:]]")
  try("(?u)[[:#{n}:]]", "(?u)[[:#{n}:]]")
  try("[[:#{n}:]&&\\p{^Emoji}]", "[[:#{n}:]&&\\p{^Emoji}]")
  try("(?a)\\p{#{n}}", "(?a)\\p{#{n}}")
  try("[[:#{n}:]]/i", "[[:#{n}:]]", Regexp::IGNORECASE) if ICASE
end

puts "== shorthands =="
%w[\w \W \d \D \s \S \h \H].each do |e|
  ["", "(?a)", "(?u)", "(?d)"].each do |mode|
    try("#{mode}#{e}", "#{mode}#{e}")
    try("#{mode}[#{e}]", "#{mode}[#{e}]")
    try("#{mode}[^#{e}]", "#{mode}[^#{e}]")
    try("#{mode}[#{e}&&\\p{Emoji}]", "#{mode}[#{e}&&\\p{Emoji}]")
    try("#{mode}#{e}+", "#{mode}#{e}+")
    try("#{mode}[^#{e}]+?", "#{mode}[^#{e}]+?")
    # (no loop that can match empty: IronRuby's scan steps over an empty match by one UTF-16
    # code unit, not one character, which is a separate matter)
    try("#{mode}#{e}++", "#{mode}#{e}++")
  end
end

puts "== combinations =="
[
  "[\\p{Emoji}&&[^\\d#*]]", "[\\p{Emoji}&&[^\\d#*]]+", "[\\p{Emoji}--]", "\\p{Emoji_Presentation}\\ufe0f?",
  "\\p{Extended_Pictographic}(?:\\u200d\\p{Extended_Pictographic})*", "[\\p{Han}\\p{Hiragana}\\p{Katakana}]+",
  "[^\\p{L}\\p{M}\\p{N}]", "[\\p{L}&&\\p{^Latin}]", "[\\p{Greek}&&\\p{Lu}]", "[\\p{Greek}&&\\p{Lu}]/i",
  "[[:alpha:]&&[^\\p{Latin}]]", "[\\P{Emoji}&&[^\\P{Emoji_Presentation}]]", "[^[^\\p{Emoji}]]",
  "[^\\P{Emoji}]", "[^\\p{Emoji}&&[^\\p{Emoji}]]", "[\\p{Any}&&[^\\p{Assigned}]]",
  "[\\u{1f600}-\\u{1f64f}&&\\p{Emoji_Presentation}]", "[^\\u{1f600}-\\u{1f64f}&&\\p{Emoji}]",
  "[\\u{10000}-\\u{10ffff}&&[^\\p{Emoji}]]", "(?:\\p{Emoji}\\ufe0f?){2,}", "\\p{Emoji}{2}",
  "\\p{Emoji}*?b", "(?<e>\\p{Emoji})\\k<e>", "\\A\\p{Emoji}+\\z", "\\p{Regional_Indicator}{2}",
  "[\\p{Emoji_Modifier_Base}]\\p{Emoji_Modifier}", "[a-z\\p{Emoji}&&[^\\p{ASCII}]]",
  "[[:^alpha:]&&[:^digit:]&&\\P{Emoji}]", "[\\p{Emoji}\\w]", "[\\p{Emoji}\\W]", "[\\p{Emoji}&&\\w]",
  "[\\p{Emoji}&&\\W]", "(?i)[\\p{Emoji}&&\\p{L}]", "[^a]+[^b]+", "[^a]+?b", "[^a]*[^b]",
  "\\W+\\p{Emoji}", "[^a]**", "[^a]{2,}", "[^x]+?[^y]+?", "(?:[^a]+)+b", "[^\"]*\"", "\\P{ASCII}+", "[\\p{Emoji}&&[\\u0000-\\uffff]]",
]. each do |src|
  if src.end_with?("/i")
    try(src, src.chomp("/i"), Regexp::IGNORECASE)
  else
    try(src, src)
  end
end

# Regenerates UnicodeCaseMapping.Data.cs.
#
#   ruby Src/Ruby/Builtins/UnicodeCaseMapping.Data.Generator.rb > Src/Ruby/Builtins/UnicodeCaseMapping.Data.cs
#
# WHY A TABLE AT ALL
#
# .NET's Rune.ToUpperInvariant / Rune.ToLowerInvariant are *simple* (1:1)
# mappings.  Ruby applies Unicode's *full* mappings, where one character can
# become several - "ss".upcase is "SS", "İ".downcase is "i̇" - so the
# expansions have to come from data.  .NET also has no case *folding* and no
# *titlecase* API at all, and its ICU can lag the Unicode version Ruby was
# built against.
#
# WHERE THE DATA COMES FROM
#
# The oracle is CRuby itself (`ruby` on PATH), asked character by character.
# That is deliberate: it makes the table agree with the interpreter we are
# trying to be compatible with, Unicode version included, rather than with
# whatever release of UnicodeData.txt/SpecialCasing.txt/CaseFolding.txt someone
# happened to download.  Only the *differences* from what .NET already answers
# are emitted, which is why the tables are a few hundred entries rather than
# the ~3000 characters that have any case mapping at all.
#
# The .NET side of the comparison is read from a dump produced by a throwaway
# console app (Rune.ToUpperInvariant / ToLowerInvariant over 0..0x10FFFF); the
# path is given by DOTNET_CASE_DUMP, tab-separated "cp<TAB>upper<TAB>lower" in
# hex, one line per character that .NET maps at all.  Entries in this file are
# therefore *corrections* to .NET, and always in the direction of CRuby.

dump = ENV["DOTNET_CASE_DUMP"] || "/tmp/casemapgen/dotnet.txt"
abort "missing .NET dump #{dump}" unless File.exist?(dump)

net = {}
File.foreach(dump) do |line|
  cp, up, lo = line.chomp.split("\t").map { |x| x.to_i(16) }
  net[cp] = [up, lo]
end

upper = {}   # cp -> full uppercase, where it differs from Rune.ToUpperInvariant
lower = {}   # cp -> full lowercase, where it differs from Rune.ToLowerInvariant
fold  = {}   # cp -> full case folding, where it differs from the lowercase above
title = {}   # cp -> titlecase, where it differs from "upcase, then downcase all but the first"
swap  = {}   # cp -> swapcase, where it differs from "downcase if that moves, else upcase"

ruby = {}
(0..0x10FFFF).each do |cp|
  next if cp >= 0xD800 && cp <= 0xDFFF
  c = begin
    cp.chr(Encoding::UTF_8)
  rescue RangeError
    next
  end
  up, down, fld, tit, sw = c.upcase, c.downcase, c.downcase(:fold), c.capitalize, c.swapcase
  next if up == c && down == c && fld == c && tit == c && sw == c
  ruby[cp] = [up.codepoints, down.codepoints, fld.codepoints, tit.codepoints, sw.codepoints]
end

(ruby.keys | net.keys).sort.each do |cp|
  n_up, n_lo = net[cp] || [cp, cp]
  r_up, r_down, r_fold, r_title, r_swap = ruby[cp] || [[cp], [cp], [cp], [cp], [cp]]
  upper[cp] = r_up   if r_up != [n_up]
  lower[cp] = r_down if r_down != [n_lo]
  fold[cp]  = r_fold if r_fold != r_down
  # The titlecase of a character is normally its uppercase with everything past
  # the first character lowercased ("ß" -> "SS" -> "Ss").  The exceptions
  # are the DZ/LJ/NJ digraphs, which have a dedicated titlecase form, and the
  # scripts whose lowercase has no titlecase at all (Georgian Mkhedruli).
  derived = r_up.each_with_index.flat_map { |ch, i| i.zero? ? [ch] : (ruby[ch] ? ruby[ch][1] : [ch]) }
  title[cp] = r_title if r_title != derived
  # Swapcase moves a character in whichever direction it can go.  Asking
  # "does it have a lowercase, else does it have an uppercase" rather than
  # asking Rune.IsUpper/IsLower matters for the characters that are cased
  # without being letters - Roman numerals, circled letters.  What it does not
  # cover is the titlecase (Lt) characters, which MRI swapcases componentwise:
  # "Ǆ" (a single character) swaps to "dŽ" (two).
  derived = r_down != [cp] ? r_down : (r_up != [cp] ? r_up : [cp])
  swap[cp] = r_swap if r_swap != derived
end

def cs_string(cps)
  '"' + cps.flat_map { |cp|
    if cp > 0xFFFF
      v = cp - 0x10000
      [0xD800 + (v >> 10), 0xDC00 + (v & 0x3FF)]
    else
      [cp]
    end
  }.map { |u| "\\u%04X" % u }.join + '"'
end

def emit(name, table, comment)
  puts
  puts "        // #{comment}"
  puts "        // #{table.size} entries."
  puts "        private static readonly int[]/*!*/ _#{name}Keys = {"
  table.keys.each_slice(10) { |slice| puts "            " + slice.map { |k| "0x%04X," % k }.join(" ") }
  puts "        };"
  puts
  puts "        private static readonly string[]/*!*/ _#{name}Values = {"
  table.each_slice(4) { |slice| puts "            " + slice.map { |_, v| cs_string(v) + "," }.join(" ") }
  puts "        };"
end

crlf = $stdout
def crlf.write(s) super(s.gsub("\n", "\r\n")) end

puts <<~HEAD.chomp
  /* ****************************************************************************
   *
   * Case mapping data for UnicodeCaseMapping.  GENERATED - do not edit by hand;
   * run UnicodeCaseMapping.Data.Generator.rb, which explains where it comes from.
   *
   * ***************************************************************************/

  namespace IronRuby.Builtins {
      public static partial class UnicodeCaseMapping {
HEAD

emit("Upper", upper, "Full uppercase mappings that are not what Rune.ToUpperInvariant answers.")
emit("Lower", lower, "Full lowercase mappings that are not what Rune.ToLowerInvariant answers.")
emit("Fold", fold, "Full case foldings that are not the character's full lowercase mapping.")
emit("Title", title, "Titlecase mappings that are not \"uppercase, rest lowercased\".")
emit("Swap", swap, "Swapcase mappings that are not \"lowercase if that moves, else uppercase\".")

puts <<~TAIL
      }
  }
TAIL

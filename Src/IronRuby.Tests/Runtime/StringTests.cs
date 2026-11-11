/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using IronRuby.Builtins;
using System.Text;

namespace IronRuby.Tests {
    public partial class Tests {
        public void StringsPlus() {
            AssertExceptionThrown<InvalidOperationException>(delegate() {
                CompilerTest(@"
puts 'foo' + nil
");
            });
        }

        public void Strings0() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts ""foo""
puts ""foo"" 'bar' ""baz""
");
            }, @"
foo
foobarbaz
");
        }

        public void Strings1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts ""foo#{""bar""}baz""
");
            }, @"
foobarbaz
");
        }

        public void Strings2() {
            TestOutput(@"
puts ""foo#{1;2;3}baz""
", @"
foo3baz
", 2);
        }

        public void Strings3() {
            TestOutput(@"
class String; def to_s; 'S'; end; end
class Integer; def to_s; 'N'; end; end

p """"
puts ""#{1}""
puts ""#{1}-""
puts ""-#{1}""
puts ""-#{1}-""
puts ""#{1}#{1}""
puts ""#{1}+#{1}""
puts ""-#{1}+#{1}""
puts ""-#{1}+#{1}-""

puts ""-#{x = 'bob'}-""
", @"
""""
N
N-
-N
-N-
NN
N+N
-N+N
-N+N-
-bob-
", 1);
        }

        public void Strings4() {
            AssertOutput(delegate() {
                CompilerTest(@"
p ""#{nil}""
p ""#{nil}-""
p ""-#{nil}""
p ""-#{nil}-""
p ""#{nil}#{nil}""
p ""-#{nil}+#{nil}-""
");
            }, @"
""""
""-""
""-""
""--""
""""
""-+-""
");
        }

        public void Strings5() {
            AssertOutput(delegate() {
                CompilerTest(@"
class String; def to_s; 'S'; end; end
class Integer; def to_s; 'N'; end; end

puts :""#{1}""
puts :""#{1}-""
puts :""-#{1}""
puts :""-#{1}-""
puts :""#{1}#{1}""
puts :""#{1}+#{1}""
puts :""-#{1}+#{1}""
puts :""-#{1}+#{1}-""

puts ""-#{x = 'bob'}-""
", 1, 0);
            }, @"
N
N-
-N
-N-
NN
N+N
-N+N
-N+N-
-bob-
");
        }

        public void Strings6() {
            TestOutput(@"
p :""#{}""
p :""#{}#{}""
p :""#{}#{''}#{}""

p :""#{nil}a""
p :""a#{nil}""
p :""a#{nil}b""
p :""a#{nil}b#{nil}c""
", @"
:""""
:""""
:""""
:a
:a
:ab
:abc
");
        }

        public void Strings7() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 'foobarbaz'[3,3]
");
            }, "bar");
        }

        public void Strings8() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 'foo  bar'.split
");
            }, @"
foo
bar
");
        }

        /// <summary>
        /// Mixing incompatible encodings: a \u escape makes a literal UTF-8, so a non-ASCII byte of
        /// a binary source cannot join it in the same literal (a syntax error) or later (+).
        /// </summary>
        public void Strings9() {
            var log = new LoggingErrorSink();
            CompilerTest("# encoding: ascii-8bit\n\"\\u03a3\\xff\"", log);
            Assert(log.Errors.Count == 1 && log.Errors[0].Message.Contains("UTF-8 mixed within ASCII-8BIT source"), DescribeLog(log));

            TestOutput(@"
begin
  ""\xff"".force_encoding(Encoding::BINARY) + ""\u03a3""
rescue => e
  p e.class
end
", @"
Encoding::CompatibilityError
");
        }

        /// <summary>
        /// Embedded string does call "to_s" w/o calling "respond_to?" first.
        /// </summary>
        public void ToSConversion1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class X
  def respond_to? name
    puts name
  end
  
  def to_s
    'TO_S'
  end

  puts ""#{new}""
end
");
            }, @"
TO_S
");
        }

        public void ToSConversion2() {
            AssertOutput(delegate() {
                CompilerTest(@"
class NilClass
  def to_s; 'NULL1'; end
  puts ""#{nil}""

  def to_s; 'NULL2'; end
  puts ""#{nil}""
end

class SubString < String
  def to_s; 'XXX'; end
  puts ""#{new 'SUB'}""
end
");
            }, @"
NULL1
NULL2
SUB
");
        }

        public void Symbols1() {

            RubySymbol a, b;
            a = Context.CreateSymbol(MutableString.CreateBinary(Encoding.ASCII.GetBytes("foo"), RubyEncoding.Binary), false);
            b = Context.CreateSymbol(MutableString.CreateMutable("foo", RubyEncoding.UTF8), false);
            Assert(a.Equals(b));
        }
        
        [Options(NoRuntime = true)]
        private void Inspect2() {
            const char sq = '\'';

            var sjisEncoding = RubyEncoding.SJIS;
            // あ
            var sjisWide = new byte[] { 0x82, 0xa0 };
            // \u{12345} in UTF-8:
            var utf8 = new byte[] { 0xF0, 0x92, 0x8D, 0x85 };
            // \u{12345} in UTF-16: U+d808 U+df45 
            var utf16 = Encoding.UTF8.GetString(utf8);

            string s;

            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(utf8, RubyEncoding.Binary), false, sq).ToString();
            Assert(s == @"'\xF0\x92\x8D\x85'");

            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(utf8, RubyEncoding.Binary), true, sq).ToString();
            Assert(s == @"'\xF0\x92\x8D\x85'");

            // #inspect prints characters the encoding can represent (Ruby >= 2.0);
            // only #dump escapes them all.
            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(utf8, RubyEncoding.UTF8), false, sq).ToString();
            Assert(s == sq + utf16 + sq);

            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(utf8, RubyEncoding.UTF8), true, sq).ToString();
            Assert(s == @"'\u{12345}'");

            // incomplete character:
            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.Create("\ud808\udf45\ud808", RubyEncoding.UTF8), false, sq).ToString();
            Assert(s == sq + utf16 + @"\u{d808}" + sq);
            
            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(sjisWide, sjisEncoding), false, sq).ToString();
            Assert(s == @"'\x82\xA0'");

            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(sjisWide, sjisEncoding), true, sq).ToString();
            Assert(s == @"'\x82\xA0'");
        }

        /// <summary>
        /// #start_with? and #end_with? insist on a character boundary the way MRI does, which is not the
        /// same test for both on bytes that are invalid UTF-8: #end_with? needs the suffix to start on a
        /// lead byte, #start_with? steps over the character the offset is in (a truncated sequence
        /// counting with the length its lead byte announces). Neither walks the string from its start
        /// any more, and a single byte encoding answers without looking. String#sum adds in 64 bits.
        /// </summary>
        public void String_CharacterBoundaries1() {
            TestOutput(@"
require 'ruby4'
u = ->(s) { s.dup.force_encoding(""UTF-8"") }
[[""\xA9\xA9"", ""\xA9""], [""\xA9"" * 6, ""\xA9"" * 5], [""\xC3\xA9"", ""\xC3""], [""\xC3\xA9"", ""\xA9""],
 [""\xE3\x81"", ""\xE3""], [""\xE3\x81"", ""\x81""], [""\xED\xA0\x80"", ""\xED""], [""\xC3\xC3\xA9"", ""\xC3""],
 [""\xC3\xC3\xA9"", ""\xC3\xA9""], [""\xF0\x9F\x98"", ""\xF0\x9F""], [""xx\xBF"", ""\xBF""], [""\xE3\x81\x82\x81"", ""\xE3\x81\x82""],
 [""aé"", ""é""], [""éa"", ""a""]].each do |s, a|
  p [u.(s).start_with?(u.(a)), u.(s).end_with?(u.(a))]
end
b = (""x"" * 1000 + ""yz"").b
p [b.end_with?(""yz""), b.end_with?(""y""), b.start_with?(""xx""), b.force_encoding(""ISO-8859-1"").end_with?(""z"")]
p [""abc"".sum, ""abc"".sum(0), ""abc"".sum(1), (""\xFF"" * 1000).b.sum(8), (""\xFF"" * 20000).b.sum(64), (""\xFF"" * 20000).b.sum(16), ""x"".sum(100), """".sum]
", @"
[true, false]
[false, false]
[false, false]
[false, false]
[false, false]
[false, false]
[true, false]
[true, false]
[false, true]
[false, false]
[false, false]
[true, false]
[false, true]
[false, true]
[true, false, true, true]
[294, 294, 0, 24, 5100000, 53728, 120, 0]
");
        }

        /// <summary>
        /// U+FFFF is an ordinary character once a string has been mutated into its char-array form;
        /// it used to be mistaken for a decoder placeholder and #chars raised.
        /// </summary>
        public void String_NonCharacter1() {
            TestOutput(@"
s = ""a\uFFFFb"".dup
s << ""c""
s[0] = ""x""
p s.length, s.chars.map(&:ord), s.each_char.count
t = ""ab\u{1F600}"".dup
t[0] = ""Z""
p t.chars.map(&:ord)
", @"
4
[120, 65535, 98, 99]
4
[90, 98, 128512]
");
        }

        /// <summary>
        /// #count, #delete and #squeeze answer an ASCII character from a bitmask CharacterSelector
        /// computes when it is parsed, negation and intersection included; anything else goes
        /// through the selector's sets and ranges as before.
        /// </summary>
        /// <summary>
        /// A carry out of a wrapped digit or letter skips over a separator to the next
        /// alphanumeric only when the two are of the same kind (rb_str_succ).
        /// </summary>
        public void String_SuccSeparators1() {
            TestOutput(@"
p ""a-9"".succ, ""1.9"".succ, ""a.z"".succ, ""9-z"".succ, ""Az-9"".succ, ""Zz"".succ, ""zz-99"".succ, ""a-b-9"".succ
", @"
""a-10""
""2.0""
""b.a""
""9-aa""
""Az-10""
""AAa""
""zz-100""
""a-b-10""
");
        }

        public void String_CountDeleteSqueeze1() {
            TestOutput(@"
require 'ruby4'
s = ""hello world""
p [s.count(""lo""), s.count(""a-y""), s.count(""^l""), s.count(""lo"", ""o""), s.count(""^""), s.count(""\\^""), ""a^b"".count(""^""), ""x-y"".count(""x-""), s.count(""a-z"", ""^aeiou""), s.count(""^a-z"")]
e = ""héllo wörld""
p [e.count(""é-ö""), e.count(""^l""), e.delete(""é"").size, e.delete(""^é"").bytes, e.squeeze.size, ""a\u{1F600}\u{1F600}b"".count(""\u{1F600}""), ""a\u{1F600}\u{1F600}b"".squeeze.size]
p [s.delete(""l""), s.delete(""a-z""), s.delete(""^l""), s.delete(""lo"", ""o""), ""aaabbb  ccc"".squeeze, ""aaabbb"".squeeze(""a""), ""aa"".squeeze!(""b"")]
x = +""abc""
p [x.delete!(""x""), x.delete!(""b""), x]
y = ""\xFF\xFF\x80"".b
p [y.count(""\xFF"".b), y.delete(""\x80"".b).bytes, y.squeeze.bytes]
p((""abc"".count(""c-a"") rescue $!.message))
", @"
[5, 10, 8, 2, 0, 0, 1, 2, 7, 1]
[2, 8, 10, [195, 169], 10, 2, 3]
[""heo word"", "" "", ""lll"", ""hell wrld"", ""ab c"", ""abbb"", nil]
[nil, ""ac"", ""ac""]
[2, [255, 255], [255, 128]]
""invalid range \""c-a\"" in string transliteration""
");
        }

        /// <summary>
        /// #count, #delete and #squeeze read the characters in place, from a string held as bytes as well as one
        /// held as characters (#89). #squeeze! and #delete! check in MRI's order: #delete! answers nil for an empty
        /// receiver before anything else, both set up the character sets before refusing a frozen receiver, and
        /// #squeeze! refuses a frozen one even with nothing to squeeze. The expected output is CRuby 4.0's.
        /// </summary>
        public void String_CountDeleteSqueeze2() {
            TestOutput(@"
require 'ruby4'
class S < String; end
bin = [0x61, 0x61, 0x20, 0x20, 0x62, 0xff, 0xff].pack(""C*"")
asc = [0x61, 0x61, 0x6c, 0x6c, 0x20, 0x20, 0x62].pack(""C*"").force_encoding(""UTF-8"")
lat = ""caf\xE9\xE9  x"".force_encoding(""ISO-8859-1"")
p [bin.count(""a\xff"".b), bin.delete(""a"").bytes, bin.squeeze.bytes, bin.squeeze(""\xff"".b).bytes, bin.delete(""^a"").bytes]
p [asc.count(""l ""), asc.delete(""l""), asc.squeeze, asc.squeeze(""l""), asc.delete(""a-z""), asc.count(""^a-z"")]
p [lat.count(""\xE9"".force_encoding(""ISO-8859-1"")), lat.squeeze.bytes, lat.delete("" "").bytes, lat.squeeze.encoding]
p [S.new(""aab"").squeeze.class, S.new(""aab"").delete(""z"").class, S.new(""ab"").squeeze.class]
x = +""aabbcc""; p [x.squeeze!, x, x.squeeze!, x.delete!(""z""), x.delete!(""b""), x]
p [("""".delete rescue $!), """".delete!, """".freeze.delete!(""a""), ("""".freeze.squeeze! rescue $!.class)]
p [(""a"".freeze.delete!(""z"") rescue $!.class), (""a"".freeze.squeeze!(""z"") rescue $!.class), (""a"".freeze.delete!(""z-a"") rescue $!.class), (""a"".freeze.squeeze!(""z-a"") rescue $!.class)]
s = ""ab"" * 3000; p [s.count(""a""), s.delete(""a"").size, (s + s.reverse).squeeze.size, (""x"" * 5000).squeeze]
y = +""aab""; z = y.squeeze; y << ""b""; p [z, y]
", @"
[4, [32, 32, 98, 255, 255], [97, 32, 98, 255], [97, 97, 32, 32, 98, 255], [97, 97]]
[4, ""aa  b"", ""al b"", ""aal  b"", ""  "", 2]
[2, [99, 97, 102, 233, 32, 120], [99, 97, 102, 233, 233, 120], #<Encoding:ISO-8859-1>]
[String, String, String]
[""ac"", ""ac"", nil, nil, ""ac"", ""ac""]
["""", nil, nil, FrozenError]
[FrozenError, FrozenError, ArgumentError, ArgumentError]
[3000, 3000, 11999, ""x""]
[""ab"", ""aabb""]
");
        }

        /// <summary>
        /// #count, #delete and #squeeze read ASCII characters through a table, count a single
        /// character with a vectorized search and skip the non-ASCII test when no non-ASCII
        /// character can be selected (#89). The expected output is CRuby 4.0's.
        /// </summary>
        public void String_CountDeleteSqueeze3() {
            TestOutput(@"
require 'ruby4'
s = ""hello world, all well "" * 60
u = ""héllo wörld ああ "" * 40
b = (""hello\xff\xffworld  "".b) * 50
p [s.count(""l""), s.count(""lo""), s.count(""a-y""), s.count(""^l""), s.count(""a-z"", ""^l""), s.count(""^a-k"", ""lo""), s.count(""l"", ""^あ"")]
p [s.delete(""l"").size, s.delete(""lo"").size, s.delete(""^l"").size, s.delete(""a-z"", ""^l"").size, s.delete("" "")[0, 20]]
p [s.squeeze.size, s.squeeze(""l"").size, s.squeeze(""a-z"").size, s.squeeze(""^l"").size, s.squeeze[0, 20]]
p [u.count(""l""), u.count(""é""), u.count(""^l""), u.count(""lあ""), u.count(""あ"", ""^a""), u.count(""a-z"", ""\u0000-ÿ"")]
p [u.delete(""l"")[0, 12], u.delete(""あ"").size, u.delete(""^あ"").size, u.squeeze(""あ"")[0, 12], u.squeeze[0, 12]]
p [b.count(""l""), b.count(""\xff"".b), b.count(""^l""), b.count(""\x80-\xff"".b), b.delete(""\xff"".b).size, b.squeeze.size, b.squeeze(""\xff"".b).size, b.squeeze("" "").bytes.first(14)]
t = +""aaaああbbb""; p [t.squeeze!(""あ""), t, t.delete!(""b""), t, t.squeeze!(""^a""), t]
p [""xyz"".count(""x""), """".count(""x""), ""x"".squeeze, ""xx"".squeeze, ""ああ"".squeeze, ""aab"".count(""a"", ""a""), ""aあ"".count(""a-あ"")]
", @"
[420, 540, 1020, 900, 600, 540, 420]
[900, 780, 420, 720, ""helloworld,allwellhe""]
[1140, 1140, 1140, 1320, ""helo world, al wel h""]
[120, 40, 480, 200, 80, 320]
[""héo wörd ああ "", 520, 80, ""héllo wörld "", ""hélo wörld あ""]
[150, 100, 550, 100, 600, 550, 650, [104, 101, 108, 108, 111, 255, 255, 119, 111, 114, 108, 100, 32, 104]]
[""aaaあ"", ""aaaあ"", ""aaaあ"", ""aaaあ"", nil, ""aaaあ""]
[1, 0, ""x"", ""x"", ""あ"", 2, 2]
");
        }

        /// <summary>
        /// String#dump writes a non-ASCII byte of a string outside UTF-8 as \xHH, as MRI's rb_str_dump does. A string
        /// that had been read by character (#length, #inspect) wrote its characters inspect's way ("\x{A4A2}"), or an
        /// invalid byte as the byte itself, which made CRuby's test_m17n_comb fail on its own descriptions.
        /// </summary>
        public void String_DumpNonUnicode1() {
            TestOutput(@"
s = ""\xA4\xA2"".dup.force_encoding('EUC-JP')
s.length
puts s.dump
t = ""\xA1"".dup.force_encoding('EUC-JP')
t.inspect
puts t.dump
u = ""\x82\xA0#{'#'}{\""\\"".dup.force_encoding('Shift_JIS')
u.size
puts u.dump
", @"
""\xA4\xA2""
""\xA1""
""\x82\xA0\#{\""\\""
");
        }

        /// <summary>
        /// String#&lt;=&gt; with an object that cannot be a String asks it the other way round and answers -rb_cmpint of its
        /// answer, as MRI's rb_invcmp does: -1, 0 or 1, whatever number came back. 2**100 came back as -(2**100).
        /// </summary>
        public void String_CompareInverse1() {
            TestOutput(@"
def cmp(answer)
  o = Object.new
  o.define_singleton_method(:<=>) { |_| answer }
  'foo' <=> o
rescue => e
  e
end
p cmp(2**100), cmp(-(2**100)), cmp(1.5), cmp(0), cmp(nil), cmp('x')
", @"
-1
1
-1
0
nil
#<ArgumentError: comparison of String with 0 failed>
");
        }
    }
}
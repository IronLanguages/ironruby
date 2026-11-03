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
using System.Text.RegularExpressions;
using IronRuby.Builtins;
using System.Text;
using System.Diagnostics;

namespace IronRuby.Tests {
    public partial class Tests {
        public void Regex1() {
            AssertOutput(delegate() {
                CompilerTest(@"
r = /foo/imx
puts r.to_s
puts r.inspect

puts s = /xx#{r}xx#{r}xx/i.to_s
puts t = /yy#{s}yy/.to_s
");
            }, @"
(?mix:foo)
/foo/mix
(?i-mx:xx(?mix:foo)xx(?mix:foo)xx)
(?-mix:yy(?i-mx:xx(?mix:foo)xx(?mix:foo)xx)yy)
");
        }

        public void Regex2() {
            TestOutput(@"
puts(/#{/a/}/)
puts(/#{nil}#{/a/}#{nil}/)
puts(/#{/a/}b/)
puts(/b#{/a/}/)
", @"
(?-mix:a)
(?-mix:a)
(?-mix:(?-mix:a)b)
(?-mix:b(?-mix:a))
");
        }

        [Options(NoRuntime = true)]
        public void RegexTransform1() {
            TestCorrectPatternTranslation(@"", @"");

            // escapes
            TestCorrectPatternTranslation(@"\\", @"\\");
            TestCorrectPatternTranslation(@"\_", @"_");
            // An octal escape is decoded to the character it denotes, the same way \x is.
            TestCorrectPatternTranslation(@"abc\0\01\011", "abc\u0000\u0001\\\t");
            TestCorrectPatternTranslation(@"\n\t\r\f\v\a\e\b\A\B\Z\z", "\\\n\\\t\\\r\f\v\a\u001B\\b\\A\\B\\Z\\z");
            TestCorrectPatternTranslation(@"[\n\t\r\f\v\a\e\b\A\B\Z\z]", "[\\\n\\\t\\\r\f\v\a\u001B\bABZz]");
            TestCorrectPatternTranslation(@"\G", RubyRegexOptions.NONE, @"\G", true);
            
            // meta-characters
            TestCorrectPatternTranslation(@"\xd", "\\\u000d");
            TestCorrectPatternTranslation(@"\xdz", "\\\u000dz");
            TestCorrectPatternTranslation(@"\*", @"\*");
            TestCorrectPatternTranslation(@"\[", @"\[");
            TestCorrectPatternTranslation(@"\#", @"\#");
            TestCorrectPatternTranslation(@"\0", "\u0000");
            TestCorrectPatternTranslation(@"\x09\x0a\x0d\x20\u0009\u000a\u000d\u0020\u{9 a d 20}",
                                           "\\\u0009\\\u000a\\\u000d\\\u0020\\\u0009\\\u000a\\\u000d\\\u0020\\\u0009\\\u000a\\\u000d\\\u0020");
            TestCorrectPatternTranslation(@"[a\-z]", @"[a\-z]");

            // Unicode escapes
            TestCorrectPatternTranslation(@"\u002a", @"\*");
            TestCorrectPatternTranslation(@"\u005b", @"\[");
            TestCorrectPatternTranslation(@"\u{005b}", @"\[");
            TestCorrectPatternTranslation(@"\u{5b 2a}", @"\[\*");

            // POSIX classes and properties are spelled out from Onigmo's own tables (see
            // UnicodeProperties), so these check what the translation matches rather than its
            // text; every expectation was checked against CRuby 4.0.6. A class with members
            // outside the BMP gets surrogate-pair alternatives, which the non-BMP characters here
            // exercise.
            TestClassTranslation(@"[x[:alnum:]y]", "xyaZ5\u00e9\u0663\U0001D400", "-_ \U0001F600");
            TestClassTranslation(@"[a[^:alnum:]b]", "abxZ5-\U0001F600", ":lnum");
            TestClassTranslation(@"[[:alpha:]]", "aZ\u00e9\u03b1\u6f22\U00020000", "5_- \U0001F600");
            TestClassTranslation(@"[[:^alpha:]]", "5_- \U0001F600", "aZ\u00e9\u6f22\U00020000");
            TestClassTranslation(@"[[:ascii:]]", "\u0000A\u007f", "\u0080\u00e9\U0001F600");
            TestClassTranslation(@"[[:^ascii:]]", "\u0080\u00e9\U0001F600", "\u0000A\u007f");
            TestClassTranslation(@"[[:blank:]]", " \u0009\u3000", "\u000aa");
            TestClassTranslation(@"[[:cntrl:]]", "\u0000\u001f\u007f\u0085", "a \u00a0");
            TestClassTranslation(@"[[:digit:]]", "09\u0663\uff10\U0001D7CE", "a\u00b2\u00bd");
            TestClassTranslation(@"[[:^digit:]]", "a\u00b2\U0001F600", "0\u0663\U0001D7CE");
            TestClassTranslation(@"[[:lower:]]", "a\u00df\u03b1\U00010428", "A1\U00010400");
            TestClassTranslation(@"[[:punct:]]", "!$+<^`|~\u00bf", "a1 ");
            TestClassTranslation(@"[[:space:]]", " \u0009\u000a\u000b\u000c\u000d\u0085\u00a0\u2028\u3000", "a\u200b");
            TestClassTranslation(@"[[:upper:]]", "A\u03a9\U00010400", "a1\U00010428");
            TestClassTranslation(@"[[:xdigit:]]", "09afAF", "gG\uff10");
            TestClassTranslation(@"[[:word:]]", "a_1\u00e9\u0301\u200c\U0001D400", "-! \U0001F600");
            TestClassTranslation(@"(?a)[[:alpha:]]", "aZ", "\u00e9\u03b1\U00020000");
            TestClassTranslation(@"(?a)[[:^alpha:]]", "1\u00e9\u3042\U00020000", "aZ");
            TestClassTranslation(@"\p{L}", "a\u00e9\u6f22\U0001D400", "1_ ");
            TestClassTranslation(@"\P{L}", "1_ \U0001F600", "a\u00e9\u6f22\U0001D400");
            TestClassTranslation(@"\p{^L}", "1_ \U0001F600", "a\u00e9\u6f22\U0001D400");
            TestClassTranslation(@"\P{^L}", "a\u00e9\u6f22\U0001D400", "1_ ");
            TestClassTranslation(@"\p{Alnum}", "a1\u0663\U0001D400", "-_\U0001F600");
            TestClassTranslation(@"[a\p{Alnum}b]", "ab1\U0001D400", "-_\U0001F600");
            TestClassTranslation(@"[^\p{Alnum}]", "-_ \U0001F600", "a1\U0001D400");
            TestClassTranslation(@"[\P{Alnum}]", "-_ \U0001F600", "a1\U0001D400");
            TestClassTranslation(@"[\p{Alnum}-]", "a1-", "_ ");
            TestClassTranslation(@"\p{Punct}", "!_\u00bf", "$+a");
            TestClassTranslation(@"\p{Word}", "a_\u0301\U0001D400", "-\U0001F600");
            TestClassTranslation(@"\p{XDigit}", "0aF", "g\uff10");
            TestClassTranslation(@"\p{Emoji}", "#*09\u00a9\u263a\U0001F600", "a \u00e9");
            TestClassTranslation(@"\P{Emoji}", "a\u00e9\U00010400", "#0\u263a\U0001F600");
            TestClassTranslation(@"[\p{Emoji}&&[^\d#*]]", "\u00a9\u263a\U0001F600\U0001F44D", "#*09a");
            TestClassTranslation(@"[^\p{Emoji}&&[^\d#*]]", "#*0a\U00010400", "\u00a9\u263a\U0001F600");
            TestClassTranslation(@"[\p{Emoji}&&[^\u{1f600}-\u{1f64f}]]", "\u263a\U0001F44D1", "\U0001F600\U0001F64Fa");
            TestClassTranslation(@"\p{Emoji_Presentation}", "\u231a\U0001F600", "\u263a1");
            TestClassTranslation(@"\p{Extended_Pictographic}", "\u00a9\u263a\U0001F600\U0001FFFD", "1#");
            TestClassTranslation(@"\p{Han}", "\u6f22\u3005\U00020000", "a\u3042");
            TestClassTranslation(@"\p{Hiragana}", "\u3042\u309d", "\u30a2\u309b");
            TestClassTranslation(@"\p{Greek}", "\u03b1\u1f00\U00010140", "a\u03e2");
            TestClassTranslation(@"\p{In_Greek_and_Coptic}", "\u03b1\u03e2", "\u1f00a");
            TestClassTranslation(@"\p{Latin}", "a\u00e9\u1e00\uff21", "\u03b11");
            TestClassTranslation(@"\p{Any}", "\u0000a\uffff\U0001F600\U0010FFFF", "");
            TestClassTranslation(@"\p{Assigned}", "a\U0001F600", "\u0378\U0010FFFF");
            TestClassTranslation(@"\p{Age=6.0}", "a\U0001F601", "\U0001F600\U0001F97A");
            TestClassTranslation(@"\p{alpha}", "a\u00e9", "1");
            TestClassTranslation(@"\p{ ALPHA }", "a\u00e9", "1");
            TestClassTranslation(@"\p{Lc}", "aA\u01c5", "\u02b01");
            TestClassTranslation(@"\p{Cs}", "", "a\U0001F600");

            // the rendering itself, for small sets
            TestCorrectPatternTranslation(@"[[:xdigit:]]", "[0-9A-Fa-f]");
            TestCorrectPatternTranslation(@"\p{XDigit}", "[0-9A-Fa-f]");
            TestCorrectPatternTranslation(@"[[:^xdigit:]]", "(?:[\0-\uffff-[0-9A-Fa-f\\ud800-\\udfff]]|[\\ud800-\\udbff][\\udc00-\\udfff])");
            TestCorrectPatternTranslation(@"\p{Emoji_Modifier}", "(?:\\ud83c[\\udffb-\\udfff])");
            // a * or + loop over a class holding every non-BMP character stays a class loop,
            // and may not stop between the halves of a surrogate pair
            TestCorrectPatternTranslation(@"\P{XDigit}*", "[\0-\uffff-[0-9A-Fa-f]]*(?<![\\ud800-\\udbff])");
            TestCorrectPatternTranslation(@"[^a]+?", "[\0-\uffff-[a]]+?(?<![\\ud800-\\udbff])");
       
            // possessive quantifiers
            TestCorrectPatternTranslation(@"xyza*+", @"xyz(?>a*)");
            TestCorrectPatternTranslation(@"x[a-b]*+", @"x(?>[a-b]*)");
            TestCorrectPatternTranslation(@"x[a-b]*+*+", @"x(?>(?>[a-b]*)*)");
            TestCorrectPatternTranslation(@"x[a-b]{1,2}+", @"x(?:[a-b]{1,2})+");
            TestCorrectPatternTranslation(@"x{1,2,*+", @"x{1,2(?>,*)");
            TestCorrectPatternTranslation(@"x{1,2*+", @"x{1,(?>2*)");
            TestCorrectPatternTranslation(@"x{1,*+", @"x{1(?>,*)");
            TestCorrectPatternTranslation(@"x{,*+", @"x{(?>,*)");
            TestCorrectPatternTranslation(@"x{1*+", @"x{(?>1*)");
            TestCorrectPatternTranslation(@"x{*+", @"x(?>{*)");

            // ranges
            TestCorrectPatternTranslation("[a-z]", "[a-z]");
            TestCorrectPatternTranslation(@"[\u{40}-z]", "[\u0040-z]");
            TestCorrectPatternTranslation(@"[\u{40}-\u{60}]", "[\u0040-\u0060]");
            TestCorrectPatternTranslation(@"[\x40-\x60]", "[\u0040-\u0060]");
            TestCorrectPatternTranslation(@"[\001-\7]", "[\u0001-\u0007]");
            TestCorrectPatternTranslation(@"[\u{2a}-\u{2b}]", "[\\*-\\+]");
            TestCorrectPatternTranslation(@"[x-]", @"[x\-]");
            TestCorrectPatternTranslation(@"[---]", @"[\--\-]");
            TestCorrectPatternTranslation(@"[\u{1 2 40}-\u{60 3 4}]", "[\u0001\u0002\u0040-\u0060\u0003\u0004]");
            TestCorrectPatternTranslation(@"[\u{1 2 40}-\u0060]", "[\u0001\u0002\u0040-\u0060]");
            TestCorrectPatternTranslation(@"[\u{1 2 40}-z]", "[\u0001\u0002\u0040-z]");
            TestCorrectPatternTranslation(@"[\x3f-\u{40 1 2}]", "[\\\u003f-\u0040\u0001\u0002]");
            // \w is ASCII only in Ruby unless (?u) is in effect, so it is expanded rather than
            // handed to .NET, whose \w is Unicode aware. Checked against CRuby 4.0.6:
            //   /[\w-]+/.match("a-\u3042")  =>  "a-"
            TestCorrectPatternTranslation(@"[\w-]", @"[a-zA-Z0-9_\-]");

            // character set operations
            TestCorrectPatternTranslation("[a-z&&d-e]", "[de]");
            TestCorrectPatternTranslation("[a-z&&[d-e&&e-f]]", "[e]");
            TestCorrectPatternTranslation("[a-z&&^[b[^c]]]", "[abd-z]");
            TestCorrectPatternTranslation("[a-z&&[^b[^c]]]", "[c]");
            TestCorrectPatternTranslation("[[^a-z][e-f][^b-q]]", "(?:[\0-\uffff-[b-dg-q\\ud800-\\udfff]]|[\\ud800-\\udbff][\\udc00-\\udfff])");
            TestCorrectPatternTranslation("[&&d-e]", "[a-[a]]");
            TestCorrectPatternTranslation("[a-z&&[d-e&&e-f]x&&^[b[^c]]]", "[ex]");
            TestCorrectPatternTranslation("[^[a-b][c-d][^e-f]&&[a-z&&[^d-e]]]", "(?:[\0-\uffff-[a-cg-z\\ud800-\\udfff]]|[\\ud800-\\udbff][\\udc00-\\udfff])");

            // groups
            TestCorrectPatternTranslation("((((a))))", "((((a))))");
            TestCorrectPatternTranslation("(?<name>a)", "(?<name>a)");
            TestCorrectPatternTranslation("(?:a)", "(?:a)");
            TestCorrectPatternTranslation("(?:a)", "(?:a)");
            TestCorrectPatternTranslation("(?mix-mix)", "(?six-six)");
            TestCorrectPatternTranslation("(?mix-mix:)", "(?six-six:)");
            TestCorrectPatternTranslation("(?mix-mix:a)", "(?six-six:a)");
            TestCorrectPatternTranslation("(?-mix:a)", "(?-six:a)");
            TestCorrectPatternTranslation("(?m:a)", "(?s:a)");
            TestCorrectPatternTranslation("(?mi:a)", "(?si:a)");
            TestCorrectPatternTranslation("(?m)", "(?s)");
            // In Ruby "name1-name2" is just a group name (CRuby: names => ["name2", "name1-name2"]),
            // not a .NET balancing group, so it is given a .NET-safe name.
            TestCorrectPatternTranslation("(?<name2>)(?<name1-name2>a)", "(?<name2>)(?<__irnname1_002dname2>a)");
            TestCorrectPatternTranslation("(?'name2')(?'name1-name2'a)", "(?'name2')(?'__irnname1_002dname2'a)");
            TestCorrectPatternTranslation("(?=)", "(?=)");
            TestCorrectPatternTranslation("(?=x)", "(?=x)");
            TestCorrectPatternTranslation("(?<=)", "(?<=)");
            TestCorrectPatternTranslation("(?<=x)", "(?<=x)");
            TestCorrectPatternTranslation("(?<!)", "(?<!)");
            TestCorrectPatternTranslation("(?<!x)", "(?<!x)");
            TestCorrectPatternTranslation("(?>)", "(?>)");
            TestCorrectPatternTranslation("(?>x)", "(?>x)");
            TestCorrectPatternTranslation("(?>(?=(?<!f)(o)(o))(?<bar>))", "(?>(?=(?<!f)(?:o)(?:o))(?<bar>))");
            
            // backreferences:
            // A numbered backreference is invalid once the pattern declares a named group, so
            // the two forms cannot appear together. Both halves checked against CRuby 4.0.6:
            //   Regexp.new("(x) (?'name') \\k<1>")  =>  numbered backref/call is not allowed. (use name)
            TestCorrectPatternTranslation(@"(x) (y) \k<1> \k'2'", @"(x) (y) \k<1> \k<2>");
            // once a pattern has a named group its plain groups do not capture (Onigmo)
            TestCorrectPatternTranslation(@"(x) (?'name') \k<name> \k'name'", @"(?:x) (?'name') \k<name> \k<name>");
            // a backreference inside the group it refers to never matches (Onigmo resets the group)
            TestCorrectPatternTranslation(@"(a\1?){2}", @"(a(?!)?){2}");

            // subexpression calls: the called group is copied under its own number, nested groups too
            TestCorrectPatternTranslation(@"((a)b)\g<1>", @"((a)b)(?<1>(?<2>a)b)");

            // error: TestCorrectPatternTranslation("(?<a)b>c)", "(?<a)b>c)");
        }

        // Translates a class and checks, with the .NET regex, that it matches each character of
        // members as a whole and none of those of nonMembers (a surrogate pair is one character).
        private void TestClassTranslation(string/*!*/ pattern, string/*!*/ members, string/*!*/ nonMembers) {
            bool hasGAnchor;
            string actual = RegexpTransformer.Transform(pattern, RubyRegexOptions.NONE, out hasGAnchor);
            var regex = new Regex(@"\A(?:" + actual + @")\z", RubyRegex.ToClrOptions(RubyRegexOptions.NONE));
            for (int i = 0; i < members.Length; i += Char.IsHighSurrogate(members[i]) ? 2 : 1) {
                string c = members.Substring(i, Char.IsHighSurrogate(members[i]) ? 2 : 1);
                Assert(regex.IsMatch(c), pattern + " should match U+" + Char.ConvertToUtf32(c, 0).ToString("X4"));
            }
            for (int i = 0; i < nonMembers.Length; i += Char.IsHighSurrogate(nonMembers[i]) ? 2 : 1) {
                string c = nonMembers.Substring(i, Char.IsHighSurrogate(nonMembers[i]) ? 2 : 1);
                Assert(!regex.IsMatch(c), pattern + " should not match U+" + Char.ConvertToUtf32(c, 0).ToString("X4"));
            }
            Assert(!hasGAnchor);
        }

        //[DebuggerHidden]
        private void TestCorrectPatternTranslation(string/*!*/ pattern, string/*!*/ expected) {
            TestCorrectPatternTranslation(pattern, RubyRegexOptions.NONE, expected, false);
        }

        //[DebuggerHidden]
        private void TestCorrectPatternTranslation(string/*!*/ pattern, RubyRegexOptions options, string/*!*/ expected, bool expectedGAnchor) {
            bool hasGAnchor;
            string actual = RegexpTransformer.Transform(pattern, options, out hasGAnchor);
            // a class with members outside the BMP gets a surrogate-pair alternative after it (see
            // TestClassTranslation); the BMP part is what these expectations spell out
            if (actual != expected && actual.StartsWith("(?:" + expected + "|", StringComparison.Ordinal) && actual.EndsWith(")", StringComparison.Ordinal)) {
                expected = actual;
            }
            AreEqual(expected, actual);
            new Regex(expected);
            Assert(hasGAnchor == expectedGAnchor);
        }

        [Options(NoRuntime = true)]
        public void RegexTransform2() {
            string p = @"^
        ([a-zA-Z][-+.a-zA-Z\d]*):                     (?# 1: scheme)
        (?:
           ((?:[-_.!~*'()a-zA-Z\d;?:@&=+$,]|%[a-fA-F\d]{2})(?:[-_.!~*'()a-zA-Z\d;/?:@&=+$,\[\]]|%[a-fA-F\d]{2})*)              (?# 2: opaque)
        |
           (?:(?:
             //(?:
                 (?:(?:((?:[-_.!~*'()a-zA-Z\d;:&=+$,]|%[a-fA-F\d]{2})*)@)?  (?# 3: userinfo)
                   (?:((?:(?:(?:[a-zA-Z\d](?:[-a-zA-Z\d]*[a-zA-Z\d])?)\.)*(?:[a-zA-Z](?:[-a-zA-Z\d]*[a-zA-Z\d])?)\.?|\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}|\[(?:(?:[a-fA-F\d]{1,4}:)*(?:[a-fA-F\d]{1,4}|\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})|(?:(?:[a-fA-F\d]{1,4}:)*[a-fA-F\d]{1,4})?::(?:(?:[a-fA-F\d]{1,4}:)*(?:[a-fA-F\d]{1,4}|\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}))?)\]))(?::(\d*))?))?(?# 4: host, 5: port)
               |
                 ((?:[-_.!~*'()a-zA-Z\d$,;+@&=+]|%[a-fA-F\d]{2})+)           (?# 6: registry)
               )
             |
             (?!//))                              (?# XXX: '//' is the mark for hostport)
             (/(?:[-_.!~*'()a-zA-Z\d:@&=+$,]|%[a-fA-F\d]{2})*(?:;(?:[-_.!~*'()a-zA-Z\d:@&=+$,]|%[a-fA-F\d]{2})*)*(?:/(?:[-_.!~*'()a-zA-Z\d:@&=+$,]|%[a-fA-F\d]{2})*(?:;(?:[-_.!~*'()a-zA-Z\d:@&=+$,]|%[a-fA-F\d]{2})*)*)*)?              (?# 7: path)
           )(?:\?((?:[-_.!~*'()a-zA-Z\d;/?:@&=+$,\[\]]|%[a-fA-F\d]{2})*))?           (?# 8: query)
        )
        (?:\#((?:[-_.!~*'()a-zA-Z\d;/?:@&=+$,\[\]]|%[a-fA-F\d]{2})*))?            (?# 9: fragment)
      $";

            // ^ is expanded: .NET's Multiline ^ also matches the empty line it considers a
            // trailing \n to open, which Ruby has no equivalent of.
            string e = @"(?:\A|(?<=\n)(?!\z))
        ([a-zA-Z][\-+.a-zA-Z0-9]*):                     
        (?:
           ((?:[\-_.!~*'()a-zA-Z0-9;?:@&=+$,]|%[a-fA-F0-9]{2})(?:[\-_.!~*'()a-zA-Z0-9;/?:@&=+$,\[\]]|%[a-fA-F0-9]{2})*)              
        |
           (?:(?:
             //(?:
                 (?:(?:((?:[\-_.!~*'()a-zA-Z0-9;:&=+$,]|%[a-fA-F0-9]{2})*)@)?  
                   (?:((?:(?:(?:[a-zA-Z0-9](?:[\-a-zA-Z0-9]*[a-zA-Z0-9])?)\.)*(?:[a-zA-Z](?:[\-a-zA-Z0-9]*[a-zA-Z0-9])?)\.?|[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}|\[(?:(?:[a-fA-F0-9]{1,4}:)*(?:[a-fA-F0-9]{1,4}|[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3})|(?:(?:[a-fA-F0-9]{1,4}:)*[a-fA-F0-9]{1,4})?::(?:(?:[a-fA-F0-9]{1,4}:)*(?:[a-fA-F0-9]{1,4}|[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}))?)\]))(?::([0-9]*))?))?
               |
                 ((?:[\-_.!~*'()a-zA-Z0-9$,;+@&=+]|%[a-fA-F0-9]{2})+)           
               )
             |
             (?!//))                              
             (/(?:[\-_.!~*'()a-zA-Z0-9:@&=+$,]|%[a-fA-F0-9]{2})*(?:;(?:[\-_.!~*'()a-zA-Z0-9:@&=+$,]|%[a-fA-F0-9]{2})*)*(?:/(?:[\-_.!~*'()a-zA-Z0-9:@&=+$,]|%[a-fA-F0-9]{2})*(?:;(?:[\-_.!~*'()a-zA-Z0-9:@&=+$,]|%[a-fA-F0-9]{2})*)*)*)?              
           )(?:\?((?:[\-_.!~*'()a-zA-Z0-9;/?:@&=+$,\[\]]|%[a-fA-F0-9]{2})*))?           
        )
        (?:\#((?:[\-_.!~*'()a-zA-Z0-9;/?:@&=+$,\[\]]|%[a-fA-F0-9]{2})*))?            
      $";
            
            bool hasGAnchor;
            string t = RegexpTransformer.Transform(p, RubyRegexOptions.Extended | RubyRegexOptions.Multiline, out hasGAnchor);
            Assert(e == t);
            Assert(!hasGAnchor);
            new Regex(t);
        }

        public void RegexEscape1() {
            string[] patterns = new string[] {
                @"", 
                @"",

                @"\", 
                @"\\",

                @"(*)", 
                @"\(\*\)",

                "$_^_|_[_]_(_)_\\_._#_-_{_}_*_+_?_\t_\n_\r_\f_\v_\a_\b",
                @"\$_\^_\|_\[_\]_\(_\)_\\_\._\#_\-_\{_\}_\*_\+_\?_\t_\n_\r_\f_\v_" + "\a_\b"
            };

            for (int i = 0; i < patterns.Length; i += 2) {
                string expected = patterns[i + 1];
                string actual = RubyRegex.Escape(patterns[i]);
                Assert(actual == expected);
            }
        }

        public void RegexCondition1() {
            AssertOutput(delegate() {
                CompilerTest(@"
$_ = 'foo'
if /(foo)/ then
  puts $1
end
", 1, 0);
            }, @"
foo
");
        }

        public void RegexCondition2() {
            AssertOutput(delegate() {
                CompilerTest(@"
z = /foo/
puts(z =~ 'xxxfoo')

class Regexp
  def =~ a
    '=~'    
  end
end

z = /foo/
puts(z =~ 'foo')

puts(/foo/ =~ 'xxxfoo')     # a literal calls a redefined Regexp#=~ too (MRI 1.9+)
");
            }, @"
3
=~
=~
");
        }
        
#if OBSOLETE //? 
        [Options(NoRuntime = true)]
        public void RegexEncoding1() {
            MatchData m;
            // the k-coding of the pattern string is irrelevant:
            foreach (var pe in new[] {  RubyEncoding.Binary }) {
                var p = MutableString.CreateBinary(new byte[] { 0x82, 0xa0, (byte)'{', (byte)'2', (byte)'}' }, pe);

                var r = new RubyRegex(p, RubyRegexOptions.NONE);
                var rs = new RubyRegex(p, RubyRegexOptions.SJIS);

                // the k-coding of the string is irrelevant:
                foreach (var se in new[] { RubyEncoding.Binary }) {
                    var s = MutableString.CreateBinary(new byte[] { 0x82, 0xa0,  0xa0 }, se);
                    var t = MutableString.CreateBinary(new byte[] { 0x82, 0xa0,  0xa0,  0x82, 0xa0,  0xa0, 0xff }, se);
                    var u = MutableString.CreateBinary(new byte[] { 0x82, 0xa0,  0x82, 0xa0,  0x82, 0xa0 }, se);

                    // /あ{2}/ does not match "あ\xa0"
                    m = r.Match(RubyEncoding.KCodeSJIS, s);
                    Assert(m == null);

                    // /\x82\xa0{2}/ matches "[ \x82\xa0\xa0 ] \x82\xa0\xa0\xff"
                    m = r.Match(null, s);
                    Assert(m != null && m.Index == 0);

                    // /\x82\xa0{2}/ matches "\x82\xa0\xa0 [ \x82\xa0\xa0 ] \xff" starting from byte #1:
                    m = r.Match(null, t, 1, false);
                    Assert(m != null && m.Index == 3 && m.Length == 3);

                    // /あ{2}/s does not match "あ\xa0", current KCODE is ignored
                    m = rs.Match(null, s);
                    Assert(m == null);

                    // /あ{2}/s does not match "あ\xa0", current KCODE is ignored
                    m = rs.Match(RubyEncoding.KCodeUTF8, s);
                    Assert(m == null);

                    // /あ{2}/s matches "ああ\xff", current KCODE is ignored
                    m = rs.Match(RubyEncoding.KCodeUTF8, u, 2, false);
                    Assert(m != null && m.Index == 2 && m.Length == 4);


                    // /あ{2}/ does not match "あ\xa0あ\xa0"
                    m = r.LastMatch(RubyEncoding.KCodeSJIS, t);
                    Assert(m == null);

                    // /\x82\xa0{2}/ matches "\x82\xa0\xa0 [ \x82\xa0\xa0 ] \xff"
                    m = r.LastMatch(null, t);
                    Assert(m != null && m.Index == 3);

                    // /あ{2}/s does not match "あ\xa0あ\xa0", current KCODE is ignored
                    m = rs.LastMatch(null, t);
                    Assert(m == null);

                    // /あ{2}/s does not match "あ\xa0あ\xa0", current KCODE is ignored
                    m = rs.LastMatch(RubyEncoding.KCodeUTF8, t);
                    Assert(m == null);
                }
            }
        }

        [Options(NoRuntime = true)]
        public void RegexEncoding2() {
            var SJIS = RubyEncoding.KCodeSJIS.StrictEncoding;

            // 1.9 encodings:
            var invalidUtf8 = MutableString.CreateBinary(new byte[] { 0x80 }, RubyEncoding.UTF8);
            AssertExceptionThrown<ArgumentException>(() => new RubyRegex(invalidUtf8, RubyRegexOptions.NONE));

            // LastMatch

            MatchData m;
            var u = MutableString.CreateBinary(SJIS.GetBytes("あああ"), RubyEncoding.KCodeSJIS);
            var p = MutableString.CreateBinary(SJIS.GetBytes("あ{2}"), RubyEncoding.KCodeSJIS);

            var rs = new RubyRegex(p, RubyRegexOptions.SJIS);

            // /あ{2}/ matches "あああ", the resulting index is in bytes:
            m = rs.LastMatch(null, u);
            Assert(m != null && m.Index == 2);

            rs = new RubyRegex(MutableString.CreateBinary(SJIS.GetBytes("あ")), RubyRegexOptions.SJIS);

            // "start at" in the middle of a character:
            m = rs.LastMatch(null, u, 0);
            Assert(m != null && m.Index == 0);

            m = rs.LastMatch(null, u, 1);
            Assert(m != null && m.Index == 0);

            m = rs.LastMatch(null, u, 2);
            Assert(m != null && m.Index == 2);

            m = rs.LastMatch(null, u, 3);
            Assert(m != null && m.Index == 2);

            // Split
            
            u = MutableString.CreateBinary(SJIS.GetBytes("あちあちあ"), RubyEncoding.UTF8);
            rs = new RubyRegex(MutableString.CreateBinary(SJIS.GetBytes("ち")), RubyRegexOptions.SJIS);
            var parts = rs.Split(null, u);
            Assert(parts.Length == 3);
            foreach (var part in parts) {
                Assert(part.Encoding == RubyEncoding.KCodeSJIS);
                Assert(part.ToString() == "あ");
            }

            // groups

            rs = new RubyRegex(MutableString.CreateBinary(SJIS.GetBytes("ち(a(あ+)(b+))+あ")), RubyRegexOptions.SJIS);
            u = MutableString.CreateBinary(SJIS.GetBytes("ちaああbaあbbbあ"));

            m = rs.Match(null, u);
            Assert(m.GroupCount == 4);

            int s, l;
            Assert(m.GetGroupStart(0) == (s = 0));
            Assert(m.GetGroupLength(0) == (l = u.GetByteCount()));
            Assert(m.GetGroupEnd(0) == s + l);

            // the group has 2 captures, the last one is its value:
            Assert(m.GetGroupStart(1) == (s = SJIS.GetByteCount("ちaああb")));
            Assert(m.GetGroupLength(1) == (l = SJIS.GetByteCount("aあbbb")));
            Assert(m.GetGroupEnd(1) == s + l);

            // the group has 2 captures, the last one is its value:
            Assert(m.GetGroupStart(2) == (s = SJIS.GetByteCount("ちaああba")));
            Assert(m.GetGroupLength(2) == (l = SJIS.GetByteCount("あ")));
            Assert(m.GetGroupEnd(2) == s + l);

            // the group has 2 captures, the last one is its value:
            Assert(m.GetGroupStart(3) == (s = SJIS.GetByteCount("ちaああbaあ")));
            Assert(m.GetGroupLength(3) == (l = SJIS.GetByteCount("bbb")));
            Assert(m.GetGroupEnd(3) == s + l);
        }
#endif
    }
}



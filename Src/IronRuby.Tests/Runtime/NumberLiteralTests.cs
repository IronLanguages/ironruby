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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Tests {
    /// <summary>
    /// Integer and Bignum literals as the runtime parses them (String#to_i, Integer()), how a source's encoding is detected from its magic comment and preamble, and a first-run timing. Kept from the legacy parser's tests when that parser was removed.
    /// </summary>
    public partial class Tests {
        // manual test, measures initialization and JIT times (hence it must be the first run)
        public void Scenario_Startup() {
            Stopwatch w = new Stopwatch();
            w.Start();
            Engine.CreateScriptSourceFromString("def foo; puts 2 + 2; end").Execute(Engine.CreateScope());
            w.Stop();
            Console.WriteLine("> total time: {0}ms", w.ElapsedMilliseconds);
        }

        public void ParseBigInts1() {
            TestBigInt("00010010000_00100000_01001000_00010000_00000010_01001000_11111111_00010000_00100000_00000011", 2);
            TestBigInt("000100_afe1_1231_4980_6FEA_1470_5100_afe1_1231_4980_6FEA_1471", 16);
            TestBigInt("65_6465477756_5454111111_1112365414_65_4365414564_5463215467_3215456713", 8);
            TestBigInt("645456600_000798789_789798798_798789798_798765461_237676891_968734198_467346797", 10);
            TestBigInt("hhs15489231dfu89765460z", 36);
            TestBigInt("lafkhalkdhflkghalgAKD12", 28);

            for (int i = 3; i <= 36; i++) {
                TestBigInt("00010010000_00100000_01001000_00010000_00000010_01001000_11111111_00010000_00100000_00000011", i, 0);
            }
        }

        public void ParseIntegers1() {
            IntegerValue x;
            Assert((x = Tokenizer.ParseInteger("", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("", 16)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("    ", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("-", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("+", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("0", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("00", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("0x", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("0x", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("-0x", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("+0x", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("  1234   ", 0)).Equals(1234));
            Assert((x = Tokenizer.ParseInteger("  1_2_3_4   ", 0)).Equals(1234));
            Assert((x = Tokenizer.ParseInteger("  _1234   ", 0)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("  12a34   ", 0)).Equals(12));
            Assert((x = Tokenizer.ParseInteger("  1_2__34   ", 0)).Equals(12));
            Assert((x = Tokenizer.ParseInteger("  -1_2", 0)).Equals(-12));
            Assert((x = Tokenizer.ParseInteger("0x1234", 0)).Equals(0x1234));
            Assert((x = Tokenizer.ParseInteger("0x1234", 10)).Equals(0));
            Assert((x = Tokenizer.ParseInteger("0b102", 0)).Equals(2));
            Assert((x = Tokenizer.ParseInteger("1000_000000_0000000000", 0)).Bignum.ToString() == "10000000000000000000");
            Assert((x = Tokenizer.ParseInteger("1000000_000000_0000000", 16)).Bignum.ToString() == "75557863725914323419136");
            Assert((x = Tokenizer.ParseInteger("0x1000000_000000_0000000", 0)).Bignum.ToString() == "75557863725914323419136");
            Assert((x = Tokenizer.ParseInteger("0b1000000_000000_0000000", 0)).Equals(524288));
            Assert((x = Tokenizer.ParseInteger("-0b1000000_000000_0000000", 0)).Equals(-524288));
        }

        // encodings suported in preamble:
        private static readonly string[] preambleEncodingNames =
            new[] { "ASCII-8BIT", "ASCII", "BINARY", "US-ASCII", "UTF-8", "EUC-JP", "SJIS", "SHIFT_JIS", "LOCALE", "FILESYSTEM" };

        private void Encoding1() {
            foreach (var name in preambleEncodingNames) {
                var encoding = Context.GetEncodingByRubyName(name);
                Assert(encoding != null);

                // the encoding must be an identity on ASCII characters:
                Assert(RubyEncoding.AsciiIdentity(encoding));
            }

            foreach (var info in Encoding.GetEncodings()) {
                var encoding = info.GetEncoding();
                
                // doesn't blow up (the method checks itself):
                RubyEncoding.AsciiIdentity(encoding);

                //Console.WriteLine("case " + info.CodePage + ": // " + encoding.EncodingName);
            }

        }

        private void Encoding2() {
            // The magic comment may be on the second line after a #! line that runs ruby; after any
            // other #! line neither MRI nor prism looks there.
            var source1 = Context.CreateSourceUnit(new BinaryContentProvider(BinaryEncoding.Instance.GetBytes(
@"#!/usr/bin/env ruby
# enCoding = ascii-8BIT

p __ENCODING__
")), null, Encoding.UTF8, SourceCodeKind.File);

            using (var reader = source1.GetReader()) {
                Assert(reader.Encoding == BinaryEncoding.Instance);
            }

            // The test used to expect "#<Encoding:ASCII-8BIT>", which no Ruby since 3.4 prints.
            // Ruby 3.4 renamed the encoding to BINARY and made #inspect - and only #inspect,
            // #name still answers "ASCII-8BIT" - show both spellings:
            //
            //   $ ruby -v
            //   ruby 4.0.6 (2026-07-14 revision 03b6d3f889) +PRISM [x86_64-linux]
            //   $ printf '#!/usr/bin/env ruby\n# enCoding = ascii-8BIT\n\np __ENCODING__\n' > /tmp/e.rb
            //   $ ruby /tmp/e.rb
            //   #<Encoding:BINARY (ASCII-8BIT)>
            //
            // ruby/spec asserts the same thing - spec/core/encoding/inspect_spec.rb, under
            // ruby_version_is "3.4", special-cases ASCII-8BIT to "#<Encoding:BINARY (ASCII-8BIT)>".
            AssertOutput(() => source1.Execute(), @"#<Encoding:BINARY (ASCII-8BIT)>");

            // default hosted encoding is UTF8:
            var source2 = Context.CreateSnippet("p __ENCODING__", SourceCodeKind.Expression);
            AssertOutput(() => source2.Execute(), @"#<Encoding:UTF-8>");
        }

        private void Encoding4() {
            var enc = Engine.Execute<RubyEncoding>(@"eval('# encoding: SJIS
__ENCODING__
')");
            Assert(enc == RubyEncoding.SJIS);
        }

        private void Encoding_Host1() {
            Encoding_HostHelper(Encoding.UTF8, "\u0394", true);
            Encoding_HostHelper(Encoding.UTF32, "\u0394", false);
        }

        private void Encoding_HostHelper(Encoding/*!*/ encoding, string/*!*/ specialChars, bool shouldSucceed) {
            var preamble = "# coding = " + encoding.WebName + "\r\n";
            var src = "$X = '" + specialChars + "'";

            var content = new List<byte>();
            content.AddRange(BinaryEncoding.Instance.GetBytes(preamble));
            content.AddRange(encoding.GetBytes(src));

            var source = Engine.CreateScriptSource(new BinaryContentProvider(content.ToArray()), null);
            
            // test encoding:
            try {
                var detectedEncoding = source.DetectEncoding();
                Assert(encoding.WebName == detectedEncoding.WebName);
            } catch (IOException) {
                Assert(!shouldSucceed);
            }

            // test content:
            try {
                var code = source.GetCode();
                Assert(StringComparer.Ordinal.Equals(code, preamble + src));
            } catch (IOException) {
                Assert(!shouldSucceed);
            }
        }

        // Ruby preamble overrides the encoding's preamble (BOM)
        public void Encoding_Host2() {
            var src = "# encoding: ASCII-8BIT\r\n$X = '\u0394'";
            var binsrc = Encoding.UTF8.GetBytes(src);

            var content = new List<byte>();
            content.AddRange(Encoding.UTF8.GetPreamble());
            content.AddRange(binsrc);
            var source = Engine.CreateScriptSource(new BinaryContentProvider(content.ToArray()), null);
            var encoding = source.DetectEncoding();
            Assert(encoding == BinaryEncoding.Instance);
            var actualCode = source.GetCode();

            // \u0394 is encoded in 2 bytes, which are represented by 2 characters in binary encoding:
            Assert(actualCode.Length == src.Length + 1);
            Assert(actualCode.Length == binsrc.Length);
        }

        private void TestBigInt(string/*!*/ number, int @base) {
            TestBigInt(number, @base, 10);
        }

        private void TestBigInt(string/*!*/ number, int @base, int repeat) {
            Stopwatch optimizedTime = new Stopwatch();
            Stopwatch universalTime = new Stopwatch();

            StringBuilder n = new StringBuilder(number);
            for (int j = 0; j < repeat; j++) {
                n.Append(number);
            }
            number = n.ToString();

            for (int i = 0; i < number.Length - 1; i++) {
                string sub = number.Substring(i);
                string s = sub.Replace("_", "").ToUpper();
                BigInteger b;
                Tokenizer.BignumParser p = new Tokenizer.BignumParser();
                p.Position = 0;
                p.Buffer = s.ToCharArray();

                optimizedTime.Start();
                b = p.Parse(s.Length, @base);
                optimizedTime.Stop();

                Assert(b.ToString(@base) == s.TrimStart('0'));

                p.Position = 0;

                universalTime.Start();
                b = p.ParseDefault(s.Length, (uint)@base);
                universalTime.Stop();

                Assert(b.ToString(@base) == s.TrimStart('0'));
            }

            if (repeat != 0) {
                Console.WriteLine("{0}: optimized = {1}ms, universal = {2}ms",
                    @base,
                    optimizedTime.ElapsedMilliseconds,
                    universalTime.ElapsedMilliseconds);
            }
        }
    }
}

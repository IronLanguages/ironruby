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
using System.Diagnostics;
using System.Collections.Generic;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using System.Numerics;
using IronRuby.Compiler;

namespace IronRuby.Tests {
    public partial class Tests {
        public void BigInteger1() {
            var bi = BigInteger.Zero;
            Assert(bi.GetWordCount() == 1);
            Assert(bi.GetWords().ValueEquals(new uint[] { 0 }));
            Assert(bi.GetByteCount() == 1);
            Assert(bi.ToByteArray().ValueEquals(new byte[] { 0 }));
            Assert(bi.GetBitCount() == 1);

            bi = BigInteger.One;
            Assert(bi.GetWordCount() == 1);
            Assert(bi.GetWords().ValueEquals(new uint[] { 1 }));
            Assert(bi.GetByteCount() == 1);
            Assert(bi.ToByteArray().ValueEquals(new byte[] { 1 }));
            Assert(bi.GetBitCount() == 1);
        }

        /// <summary>
        /// An Integer in the Int64 range is a long whichever way it was made. Integer literals and
        /// Float#to_i / floor / ceil / round / truncate (and rounding to a negative number of digits)
        /// used to leave a BigInteger there, which is not found as a Hash key holding the same value
        /// computed by arithmetic: {3 * 1_000_000_000 => 1}[3_000_000_000] was nil.
        /// </summary>
        public void Integer_Normalization1() {
            TestOutput(@"
k = 3 * 1_000_000_000
p({k => 1}[3_000_000_000], {3_000_000_000 => 1}[k], {-k => 1}[-3_000_000_000], 0xB2D05E00.eql?(k))
p [3e9.to_i, 3e9.floor, 3e9.ceil, 3e9.round, 3e9.truncate, (3e9 + 0.5).floor].map { |x| {k => 1}[x] }
p [3e9.round(-1), 3e9.floor(-1), 3e9.ceil(-1), 3e9.truncate(-1), 3_000_000_004.round(-1)].map { |x| x.eql?(k) }
p 1e15.floor, -1e15.ceil, (2.0**62).to_i, (2.0**63).to_i, (-2.0**63).to_i, 2147483648.0.to_i, -2147483649.0.to_i
", @"
1
1
1
true
[1, 1, 1, 1, 1, 1]
[true, true, true, true, true]
1000000000000000
-1000000000000000
4611686018427387904
9223372036854775808
-9223372036854775808
2147483648
-2147483649
");
        }

        /// <summary>
        /// Integer#to_s(radix) for Int32, Int64 and Bignum values, power-of-two and other radices.
        /// </summary>
        public void Integer_ToStringRadix1() {
            TestOutput(@"
p 255.to_s(2), 255.to_s(16), -255.to_s(36), 0.to_s(7), (2**31).to_s(3), (-2**63).to_s(2), (2**63-1).to_s(36)
p (2**64).to_s(16), (-(2**100 + 12345)).to_s(2), (3**50).to_s(7), (-(10**30)).to_s(36), (2**70 - 1).to_s(8), (2**200).to_s(32)
p (2**1000 + 1).to_s(5) == (2**1000 + 1).to_s(5).to_i(5).to_s(5), (2**1000 + 1).to_s(5).to_i(5) == 2**1000 + 1
", @"
""11111111""
""ff""
""-73""
""0""
""12112122212110202102""
""-1000000000000000000000000000000000000000000000000000000000000000""
""1y2p0ij32e8e7""
""10000000000000000""
""-10000000000000000000000000000000000000000000000000000000000000000000000000000000000000011000000111001""
""13632140121616200362302616562""
""-2oy99wnkl1c76diocq9s""
""177777777777777777777777""
""10000000000000000000000000000000000000000""
true
true
");
        }

        /// <summary>
        /// String#unpack('w'): a BER-compressed integer cut off by the end of the data is not read.
        /// </summary>
        public void Pack_BerTruncated1() {
            TestOutput(@"
p ""\x81"".force_encoding(""BINARY"").unpack(""w*""), ""\x80\x80"".force_encoding(""BINARY"").unpack(""w""), ""\x01\x81"".force_encoding(""BINARY"").unpack(""w*""), ""\x01\x81"".force_encoding(""BINARY"").unpack(""w2C""), """".unpack(""w"")
p ""\x05\x80\x80\x81\x01"".force_encoding(""BINARY"").unpack(""w*""), [1, 2**64, 300].pack(""w*"").unpack(""w*"")
", @"
[]
[]
[1]
[1, nil]
[]
[5, 129]
[1, 18446744073709551616, 300]
");
        }

        /// <summary>
        /// <<, >> and ~ on an Int64 receiver, and Int32 shifts that overflow into the Int64 range.
        /// </summary>
        public void Integer_Int64Shifts1() {
            TestOutput(@"
l = 2**40 + 3
p l << 3, l >> 3, -l >> 3, ~l, ~-l, l << 23, l << 24, -l << 23, -l << 24, l >> 64, -l >> 100, l << -3, -l << -64
p 1 << 40, -1 << 62, -1 << 63, 1 << 63, 3 << 61, 3 << 62, (1 << 40).eql?(2**40), {2**40 => 1}[1 << 40]
", @"
8796093022232
137438953472
-137438953473
-1099511627780
1099511627778
9223372036879941632
18446744073759883264
-9223372036879941632
-18446744073759883264
0
-1
137438953472
-1
1099511627776
-4611686018427387904
-9223372036854775808
9223372036854775808
6917529027641081856
13835058055282163712
true
1
");
        }

        /// <summary>
        /// An Integer too large for a Float meets a Float: MRI converts it (to Infinity), with an
        /// "Integer out of Float range" warning under -w, in every operator that coerces - and
        /// divmod, div and % then answer as Float#divmod etc. would for Infinity, not with a CLR
        /// OverflowException. A divmod quotient that is not finite is named in the error.
        /// </summary>
        public void Integer_OutOfFloatRange1() {
            TestOutput(@"
$VERBOSE = true
$w = []
def Warning.warn(m, **) = $w << m[/warning: (.*)/, 1]
b = 10**400
p [b + 1.0, b - 1.0, b * 1.0, b / 1.0, b ** 1.0, b % 1.0, b.modulo(1.0), b.remainder(1.0), b.coerce(1.0), Float(b), b.fdiv(1.0), b <=> 1.0]
p $w.size, $w.uniq
p((b.divmod(1.0) rescue $!), (b.div(1.0) rescue $!), (b.div(0.0) rescue $!), (b % 0.0 rescue $!), $w.size)
inf = 1.0 / 0
p((inf.divmod(1.0) rescue $!), ((-inf).divmod(1.0) rescue $!), (b.divmod(inf) rescue $!))
p (2**70).divmod(2.5), (-2**70).divmod(2.5), (2**70).div(2.5), (2**70) % -2.5
", @"
[Infinity, Infinity, Infinity, Infinity, Infinity, NaN, NaN, NaN, [1.0, Infinity], Infinity, Infinity, 1]
10
[""Integer out of Float range""]
#<FloatDomainError: Infinity>
#<FloatDomainError: Infinity>
#<ZeroDivisionError: divided by 0>
#<ZeroDivisionError: divided by 0>
13
#<FloatDomainError: Infinity>
#<FloatDomainError: -Infinity>
#<FloatDomainError: NaN>
[472236648286964547584, 1.5]
[-472236648286964547584, 1.0]
472236648286964547584
-1.0
", 0, 14);
        }

        private BigInteger ParseBigInt(string str) {
            int i = 0;
            return Tokenizer.ParseInteger(str, 0, ref i).Bignum;
        }

        [Options(NoRuntime = true)]
        public void GcdLcm1() {
            // Everything here fits in an Int64, which is now the representation for an Integer
            // that has outgrown Int32; BigInteger starts past 2**63.
            const long _4611686016279904256 = 4611686016279904256L;

            Assert((int)Integer.Gcd(-1, -1) == 1 && (int)Integer.Lcm(-1, -1) == 1);
            Assert((int)Integer.Gcd(0, -1)  == 1 && (int)Integer.Lcm(0, -1)  == 0);
            Assert((int)Integer.Gcd(-1, 0)  == 1 && (int)Integer.Lcm(-1, 0)  == 0);
            Assert((int)Integer.Gcd(0, 0)   == 0 && (int)Integer.Lcm(0, 0)   == 0);
            Assert((int)Integer.Gcd((BigInteger)0, 0) == 0 && (int)Integer.Lcm((BigInteger)0, 0) == 0);

            Assert((int)Integer.Gcd(Int32.MaxValue, Int32.MinValue) == 1);
            Assert((long)Integer.Lcm(Int32.MaxValue, Int32.MinValue) == _4611686016279904256);

            Assert((int)Integer.Gcd(Int32.MinValue, Int32.MaxValue) == 1);
            Assert((long)Integer.Lcm(Int32.MinValue, Int32.MaxValue) == _4611686016279904256);

            Assert((long)Integer.Gcd(Int32.MinValue, Int32.MinValue) == -(long)Int32.MinValue);
            Assert((long)Integer.Lcm(Int32.MinValue, Int32.MinValue) == -(long)Int32.MinValue);

            Assert((int)Integer.Gcd(Int32.MaxValue, Int32.MaxValue) == Int32.MaxValue);
            Assert((int)Integer.Lcm(Int32.MaxValue, Int32.MaxValue) == Int32.MaxValue);

            Assert((int)Integer.Gcd(Int32.MaxValue, -1) == 1);
            Assert((int)Integer.Lcm(Int32.MaxValue, -1) == Int32.MaxValue);

            Assert((int)Integer.Gcd(Int32.MinValue, -1) == 1);
            Assert((long)Integer.Lcm(Int32.MinValue, -1) == -(long)Int32.MinValue);
        }
    }
}
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
        /// pack and unpack read a format once and reuse its directives. A format with an error in it still raises it,
        /// afresh each time, after the directives before it have run (a TypeError there comes first), and unpack1 stops
        /// before it as MRI's does. Expected output is CRuby 4.0's.
        /// </summary>
        public void Pack_CachedFormats1() {
            TestOutput(@"
require 'ruby4'
def show(label)
  r = yield
  puts ""#{label}: #{r.inspect}""
rescue => e
  puts ""#{label}: #{e.class}: #{e.message}""
end
class ToI; def to_int = 258; end
%w[C n N v V s> L< q> Q< J].each do |f|
  show(f) { [0, -1, 258, 2**31, -2**40, 3.7, ToI.new].map { |v| [v].pack(f).bytes } }
end
2.times do
  show(""NnC"") { [[1, 2, 3].pack(""NnC"").bytes, ""\x00\x00\x00\x01\x00\x02\x03"".b.unpack(""NnC"")] }
  show(""Cy"") { [1, 2].pack(""Cy"") }
  show(""TypeError first"") { [""x""].pack(""Cy"") }
  show(""unpack Cy"") { ""ab"".unpack(""Cy"") }
  show(""unpack1 Cy"") { ""ab"".unpack1(""Cy"") }
  show(""unpack1 yC"") { ""ab"".unpack1(""yC"") }
  show(""<>"") { [1].pack(""s<>"") }
  show(""@"") { [[1, 2].pack(""C@C"").bytes, ""abc"".unpack(""C@C""), [1].pack(""@"").bytes, ""abc"".unpack(""@C"")] }
end
e1 = ([1].pack(""Cy"") rescue $!)
e2 = ([1].pack(""Cy"") rescue $!)
p e1.equal?(e2)
", @"
C: [[0], [255], [2], [0], [0], [3], [2]]
n: [[0, 0], [255, 255], [1, 2], [0, 0], [0, 0], [0, 3], [1, 2]]
N: [[0, 0, 0, 0], [255, 255, 255, 255], [0, 0, 1, 2], [128, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 3], [0, 0, 1, 2]]
v: [[0, 0], [255, 255], [2, 1], [0, 0], [0, 0], [3, 0], [2, 1]]
V: [[0, 0, 0, 0], [255, 255, 255, 255], [2, 1, 0, 0], [0, 0, 0, 128], [0, 0, 0, 0], [3, 0, 0, 0], [2, 1, 0, 0]]
s>: [[0, 0], [255, 255], [1, 2], [0, 0], [0, 0], [0, 3], [1, 2]]
L<: [[0, 0, 0, 0], [255, 255, 255, 255], [2, 1, 0, 0], [0, 0, 0, 128], [0, 0, 0, 0], [3, 0, 0, 0], [2, 1, 0, 0]]
q>: [[0, 0, 0, 0, 0, 0, 0, 0], [255, 255, 255, 255, 255, 255, 255, 255], [0, 0, 0, 0, 0, 0, 1, 2], [0, 0, 0, 0, 128, 0, 0, 0], [255, 255, 255, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 3], [0, 0, 0, 0, 0, 0, 1, 2]]
Q<: [[0, 0, 0, 0, 0, 0, 0, 0], [255, 255, 255, 255, 255, 255, 255, 255], [2, 1, 0, 0, 0, 0, 0, 0], [0, 0, 0, 128, 0, 0, 0, 0], [0, 0, 0, 0, 0, 255, 255, 255], [3, 0, 0, 0, 0, 0, 0, 0], [2, 1, 0, 0, 0, 0, 0, 0]]
J: [[0, 0, 0, 0, 0, 0, 0, 0], [255, 255, 255, 255, 255, 255, 255, 255], [2, 1, 0, 0, 0, 0, 0, 0], [0, 0, 0, 128, 0, 0, 0, 0], [0, 0, 0, 0, 0, 255, 255, 255], [3, 0, 0, 0, 0, 0, 0, 0], [2, 1, 0, 0, 0, 0, 0, 0]]
NnC: [[0, 0, 0, 1, 0, 2, 3], [1, 2, 3]]
Cy: ArgumentError: unknown pack directive 'y' in 'Cy'
TypeError first: TypeError: no implicit conversion of String into Integer
unpack Cy: ArgumentError: unknown unpack directive 'y' in 'Cy'
unpack1 Cy: 97
unpack1 yC: ArgumentError: unknown unpack directive 'y' in 'yC'
<>: RangeError: Can't use both '<' and '>'
@: [[1, 2], [97, 97], [0], [97]]
NnC: [[0, 0, 0, 1, 0, 2, 3], [1, 2, 3]]
Cy: ArgumentError: unknown pack directive 'y' in 'Cy'
TypeError first: TypeError: no implicit conversion of String into Integer
unpack Cy: ArgumentError: unknown unpack directive 'y' in 'Cy'
unpack1 Cy: 97
unpack1 yC: ArgumentError: unknown unpack directive 'y' in 'yC'
<>: RangeError: Can't use both '<' and '>'
@: [[1, 2], [97, 97], [0], [97]]
false
");
        }

        /// <summary>
        /// Kernel#Integer has overloads for a String, a Fixnum and a Float alone, beside the general one with the base and
        /// exception: keyword; Integer() and Float() still answer and raise as MRI's do for every kind of argument.
        /// Expected output is CRuby 4.0's.
        /// </summary>
        public void Kernel_IntegerOverloads1() {
            TestOutput(@"
require 'ruby4'
def show(label)
  r = yield
  puts ""#{label} => #{r.inspect}""
rescue Exception => e
  puts ""#{label} !! #{e.class}: #{e.message}""
end
class SubS < String; def to_str = ""99""; def to_i = 98; end
class ToInt; def to_int = 7; end
class ToF; def to_f = 2.5; end
inputs = [""12"", "" 12 "", ""0x1A"", ""0b11"", ""0o17"", ""017"", ""1_000"", ""1__0"", ""12abc"", """", "" "", ""-0"", ""+5"", ""1e3"", ""1.5"", ""\u0000"", ""1\u00002"",
          ""12\n"", SubS.new(""33""), 12, -7, 2**40, 12.5, -12.5, 1e20, Float::NAN, Float::INFINITY, -Float::INFINITY, nil, true, ToInt.new, ToF.new, :sym, [1], ""0x"".encode(""UTF-16LE"")]
inputs.each do |v|
  label = v.is_a?(SubS) ? ""SubS"" : v.is_a?(ToInt) ? ""ToInt"" : v.is_a?(ToF) ? ""ToF"" : v.inspect
  show(""Integer(#{label})"") { Integer(v) }
  show(""Integer(#{label}, exception: false)"") { Integer(v, exception: false) }
  show(""Integer(#{label}, 8)"") { Integer(v, 8) }
  show(""Float(#{label})"") { Float(v) }
  show(""Float(#{label}, exception: false)"") { Float(v, exception: false) }
  show(""Kernel.Integer(#{label})"") { Kernel.Integer(v) }
end
show(""Integer()"") { Integer() }
show(""Float()"") { Float() }
show(""Integer(1, 2, 3)"") { Integer(""1"", 2, 3) }
show(""Integer({})"") { Integer({}) }
show(""Integer(s, **{})"") { Integer(""5"", **{}) }
show(""Integer('5', foo: 1)"") { Integer(""5"", foo: 1) }
show(""Float('5', foo: 1)"") { Float(""5"", foo: 1) }
show(""Float(1.0, foo: 1)"") { Float(1.0, foo: 1) }
show(""method arity"") { [method(:Integer).arity, method(:Float).arity, method(:Integer).parameters, method(:Float).parameters] }
show(""frozen literal"") { Integer(""42"".freeze) }
show(""binary"") { [Integer(""12"".b), Float(""1.5"".b)] }
", @"
Integer(""12"") => 12
Integer(""12"", exception: false) => 12
Integer(""12"", 8) => 10
Float(""12"") => 12.0
Float(""12"", exception: false) => 12.0
Kernel.Integer(""12"") => 12
Integer("" 12 "") => 12
Integer("" 12 "", exception: false) => 12
Integer("" 12 "", 8) => 10
Float("" 12 "") => 12.0
Float("" 12 "", exception: false) => 12.0
Kernel.Integer("" 12 "") => 12
Integer(""0x1A"") => 26
Integer(""0x1A"", exception: false) => 26
Integer(""0x1A"", 8) !! ArgumentError: invalid value for Integer(): ""0x1A""
Float(""0x1A"") => 26.0
Float(""0x1A"", exception: false) => 26.0
Kernel.Integer(""0x1A"") => 26
Integer(""0b11"") => 3
Integer(""0b11"", exception: false) => 3
Integer(""0b11"", 8) !! ArgumentError: invalid value for Integer(): ""0b11""
Float(""0b11"") !! ArgumentError: invalid value for Float(): ""0b11""
Float(""0b11"", exception: false) => nil
Kernel.Integer(""0b11"") => 3
Integer(""0o17"") => 15
Integer(""0o17"", exception: false) => 15
Integer(""0o17"", 8) => 15
Float(""0o17"") !! ArgumentError: invalid value for Float(): ""0o17""
Float(""0o17"", exception: false) => nil
Kernel.Integer(""0o17"") => 15
Integer(""017"") => 15
Integer(""017"", exception: false) => 15
Integer(""017"", 8) => 15
Float(""017"") => 17.0
Float(""017"", exception: false) => 17.0
Kernel.Integer(""017"") => 15
Integer(""1_000"") => 1000
Integer(""1_000"", exception: false) => 1000
Integer(""1_000"", 8) => 512
Float(""1_000"") => 1000.0
Float(""1_000"", exception: false) => 1000.0
Kernel.Integer(""1_000"") => 1000
Integer(""1__0"") !! ArgumentError: invalid value for Integer(): ""1__0""
Integer(""1__0"", exception: false) => nil
Integer(""1__0"", 8) !! ArgumentError: invalid value for Integer(): ""1__0""
Float(""1__0"") !! ArgumentError: invalid value for Float(): ""1__0""
Float(""1__0"", exception: false) => nil
Kernel.Integer(""1__0"") !! ArgumentError: invalid value for Integer(): ""1__0""
Integer(""12abc"") !! ArgumentError: invalid value for Integer(): ""12abc""
Integer(""12abc"", exception: false) => nil
Integer(""12abc"", 8) !! ArgumentError: invalid value for Integer(): ""12abc""
Float(""12abc"") !! ArgumentError: invalid value for Float(): ""12abc""
Float(""12abc"", exception: false) => nil
Kernel.Integer(""12abc"") !! ArgumentError: invalid value for Integer(): ""12abc""
Integer("""") !! ArgumentError: invalid value for Integer(): """"
Integer("""", exception: false) => nil
Integer("""", 8) !! ArgumentError: invalid value for Integer(): """"
Float("""") !! ArgumentError: invalid value for Float(): """"
Float("""", exception: false) => nil
Kernel.Integer("""") !! ArgumentError: invalid value for Integer(): """"
Integer("" "") !! ArgumentError: invalid value for Integer(): "" ""
Integer("" "", exception: false) => nil
Integer("" "", 8) !! ArgumentError: invalid value for Integer(): "" ""
Float("" "") !! ArgumentError: invalid value for Float(): "" ""
Float("" "", exception: false) => nil
Kernel.Integer("" "") !! ArgumentError: invalid value for Integer(): "" ""
Integer(""-0"") => 0
Integer(""-0"", exception: false) => 0
Integer(""-0"", 8) => 0
Float(""-0"") => -0.0
Float(""-0"", exception: false) => -0.0
Kernel.Integer(""-0"") => 0
Integer(""+5"") => 5
Integer(""+5"", exception: false) => 5
Integer(""+5"", 8) => 5
Float(""+5"") => 5.0
Float(""+5"", exception: false) => 5.0
Kernel.Integer(""+5"") => 5
Integer(""1e3"") !! ArgumentError: invalid value for Integer(): ""1e3""
Integer(""1e3"", exception: false) => nil
Integer(""1e3"", 8) !! ArgumentError: invalid value for Integer(): ""1e3""
Float(""1e3"") => 1000.0
Float(""1e3"", exception: false) => 1000.0
Kernel.Integer(""1e3"") !! ArgumentError: invalid value for Integer(): ""1e3""
Integer(""1.5"") !! ArgumentError: invalid value for Integer(): ""1.5""
Integer(""1.5"", exception: false) => nil
Integer(""1.5"", 8) !! ArgumentError: invalid value for Integer(): ""1.5""
Float(""1.5"") => 1.5
Float(""1.5"", exception: false) => 1.5
Kernel.Integer(""1.5"") !! ArgumentError: invalid value for Integer(): ""1.5""
Integer(""\u0000"") !! ArgumentError: invalid value for Integer(): ""\u0000""
Integer(""\u0000"", exception: false) => nil
Integer(""\u0000"", 8) !! ArgumentError: invalid value for Integer(): ""\u0000""
Float(""\u0000"") !! ArgumentError: string for Float contains null byte
Float(""\u0000"", exception: false) => nil
Kernel.Integer(""\u0000"") !! ArgumentError: invalid value for Integer(): ""\u0000""
Integer(""1\u00002"") !! ArgumentError: invalid value for Integer(): ""1\u00002""
Integer(""1\u00002"", exception: false) => nil
Integer(""1\u00002"", 8) !! ArgumentError: invalid value for Integer(): ""1\u00002""
Float(""1\u00002"") !! ArgumentError: string for Float contains null byte
Float(""1\u00002"", exception: false) => nil
Kernel.Integer(""1\u00002"") !! ArgumentError: invalid value for Integer(): ""1\u00002""
Integer(""12\n"") => 12
Integer(""12\n"", exception: false) => 12
Integer(""12\n"", 8) => 10
Float(""12\n"") => 12.0
Float(""12\n"", exception: false) => 12.0
Kernel.Integer(""12\n"") => 12
Integer(SubS) => 33
Integer(SubS, exception: false) => 33
Integer(SubS, 8) => 27
Float(SubS) => 33.0
Float(SubS, exception: false) => 33.0
Kernel.Integer(SubS) => 33
Integer(12) => 12
Integer(12, exception: false) => 12
Integer(12, 8) !! ArgumentError: base specified for non string value
Float(12) => 12.0
Float(12, exception: false) => 12.0
Kernel.Integer(12) => 12
Integer(-7) => -7
Integer(-7, exception: false) => -7
Integer(-7, 8) !! ArgumentError: base specified for non string value
Float(-7) => -7.0
Float(-7, exception: false) => -7.0
Kernel.Integer(-7) => -7
Integer(1099511627776) => 1099511627776
Integer(1099511627776, exception: false) => 1099511627776
Integer(1099511627776, 8) !! ArgumentError: base specified for non string value
Float(1099511627776) => 1099511627776.0
Float(1099511627776, exception: false) => 1099511627776.0
Kernel.Integer(1099511627776) => 1099511627776
Integer(12.5) => 12
Integer(12.5, exception: false) => 12
Integer(12.5, 8) !! ArgumentError: base specified for non string value
Float(12.5) => 12.5
Float(12.5, exception: false) => 12.5
Kernel.Integer(12.5) => 12
Integer(-12.5) => -12
Integer(-12.5, exception: false) => -12
Integer(-12.5, 8) !! ArgumentError: base specified for non string value
Float(-12.5) => -12.5
Float(-12.5, exception: false) => -12.5
Kernel.Integer(-12.5) => -12
Integer(1.0e+20) => 100000000000000000000
Integer(1.0e+20, exception: false) => 100000000000000000000
Integer(1.0e+20, 8) !! ArgumentError: base specified for non string value
Float(1.0e+20) => 1.0e+20
Float(1.0e+20, exception: false) => 1.0e+20
Kernel.Integer(1.0e+20) => 100000000000000000000
Integer(NaN) !! FloatDomainError: NaN
Integer(NaN, exception: false) => nil
Integer(NaN, 8) !! ArgumentError: base specified for non string value
Float(NaN) => NaN
Float(NaN, exception: false) => NaN
Kernel.Integer(NaN) !! FloatDomainError: NaN
Integer(Infinity) !! FloatDomainError: Infinity
Integer(Infinity, exception: false) => nil
Integer(Infinity, 8) !! ArgumentError: base specified for non string value
Float(Infinity) => Infinity
Float(Infinity, exception: false) => Infinity
Kernel.Integer(Infinity) !! FloatDomainError: Infinity
Integer(-Infinity) !! FloatDomainError: -Infinity
Integer(-Infinity, exception: false) => nil
Integer(-Infinity, 8) !! ArgumentError: base specified for non string value
Float(-Infinity) => -Infinity
Float(-Infinity, exception: false) => -Infinity
Kernel.Integer(-Infinity) !! FloatDomainError: -Infinity
Integer(nil) !! TypeError: can't convert nil into Integer
Integer(nil, exception: false) => nil
Integer(nil, 8) !! ArgumentError: base specified for non string value
Float(nil) !! TypeError: can't convert nil into Float
Float(nil, exception: false) => nil
Kernel.Integer(nil) !! TypeError: can't convert nil into Integer
Integer(true) !! TypeError: can't convert true into Integer
Integer(true, exception: false) => nil
Integer(true, 8) !! ArgumentError: base specified for non string value
Float(true) !! TypeError: can't convert true into Float
Float(true, exception: false) => nil
Kernel.Integer(true) !! TypeError: can't convert true into Integer
Integer(ToInt) => 7
Integer(ToInt, exception: false) => 7
Integer(ToInt, 8) !! ArgumentError: base specified for non string value
Float(ToInt) !! TypeError: can't convert ToInt into Float
Float(ToInt, exception: false) => nil
Kernel.Integer(ToInt) => 7
Integer(ToF) !! TypeError: can't convert ToF into Integer
Integer(ToF, exception: false) => nil
Integer(ToF, 8) !! ArgumentError: base specified for non string value
Float(ToF) => 2.5
Float(ToF, exception: false) => 2.5
Kernel.Integer(ToF) !! TypeError: can't convert ToF into Integer
Integer(:sym) !! TypeError: can't convert Symbol into Integer
Integer(:sym, exception: false) => nil
Integer(:sym, 8) !! ArgumentError: base specified for non string value
Float(:sym) !! TypeError: can't convert Symbol into Float
Float(:sym, exception: false) => nil
Kernel.Integer(:sym) !! TypeError: can't convert Symbol into Integer
Integer([1]) !! TypeError: can't convert Array into Integer
Integer([1], exception: false) => nil
Integer([1], 8) !! ArgumentError: base specified for non string value
Float([1]) !! TypeError: can't convert Array into Float
Float([1], exception: false) => nil
Kernel.Integer([1]) !! TypeError: can't convert Array into Integer
Integer(""0x"") !! Encoding::CompatibilityError: ASCII incompatible encoding: UTF-16LE
Integer(""0x"", exception: false) !! Encoding::CompatibilityError: ASCII incompatible encoding: UTF-16LE
Integer(""0x"", 8) !! Encoding::CompatibilityError: ASCII incompatible encoding: UTF-16LE
Float(""0x"") !! Encoding::CompatibilityError: ASCII incompatible encoding: UTF-16LE
Float(""0x"", exception: false) !! Encoding::CompatibilityError: ASCII incompatible encoding: UTF-16LE
Kernel.Integer(""0x"") !! Encoding::CompatibilityError: ASCII incompatible encoding: UTF-16LE
Integer() !! ArgumentError: wrong number of arguments (given 0, expected 1..2)
Float() !! ArgumentError: wrong number of arguments (given 0, expected 1)
Integer(1, 2, 3) !! ArgumentError: wrong number of arguments (given 3, expected 1..2)
Integer({}) !! TypeError: can't convert Hash into Integer
Integer(s, **{}) => 5
Integer('5', foo: 1) !! ArgumentError: unknown keyword: :foo
Float('5', foo: 1) !! ArgumentError: unknown keyword: :foo
Float(1.0, foo: 1) !! ArgumentError: unknown keyword: :foo
method arity => [-2, -2, [[:req, :arg], [:opt, :base], [:key, :exception]], [[:req, :arg], [:key, :exception]]]
frozen literal => 42
binary => [12, 1.5]
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
        /// Integer#** past 2**31 result bits (#89). MRI computes 2 ** (2**31), and refuses ("exponent is too large")
        /// only when the base's bit length times the exponent passes 2**34 - for a Fixnum base, the base and
        /// exponent int_pow has reached when it gives up squaring in a long, which is why 2 ** (2**34 - 1) is
        /// refused. IronRuby refused anything past 2**31 bits. The expected output is CRuby 4.0.9's; on .NET 9
        /// and later a result past the BigInteger cap (about 2**31 bits) is NoMemoryError, as for a shift.
        /// </summary>
        public void Integer_HugePower1() {
            TestOutput(@"
def e; yield; rescue ArgumentError, NoMemoryError => x; [x.class, x.message]; end
p e { 2 ** (2**34) }, e { 2 ** (2**34 - 1) }, e { (2**64) ** (2**28 + 1) }, e { 3 ** (2**34) }, e { 2.pow(2**34) }
p 3 ** 40, (-3) ** 41, 2 ** 63, (-2) ** 63, 7 ** 2**10 == (7 ** 2**9) ** 2, (-(2**64)) ** 3 == -(2**192)
", @"
[ArgumentError, ""exponent is too large""]
[ArgumentError, ""exponent is too large""]
[ArgumentError, ""exponent is too large""]
[ArgumentError, ""exponent is too large""]
[ArgumentError, ""exponent is too large""]
12157665459056928801
-36472996377170786403
9223372036854775808
-9223372036854775808
true
true
");
            if (Environment.Version.Major >= 9) {
                TestOutput(@"
def e; yield; rescue NoMemoryError => x; x.class; end
p e { 2 ** (2**31) }, e { 4 ** (2**30) }
", @"
NoMemoryError
NoMemoryError
");
                return;
            }
            TestOutput(@"
b = 2 ** (2**31)
p b.bit_length, b == 1 << 2**31, 4 ** (2**30) == b, ((-2) ** (2**31 + 1)) == -(b << 1)
", @"
2147483649
true
true
true
");
        }

        /// <summary>
        /// Numeric#step with a Float among self, the limit and the step loops in C# (#89), with MRI's
        /// ruby_float_step_size and ruby_float_step: n = floor((end - beg) / unit + err) and beg + i * unit, the last
        /// value held to end. Everything else still goes to the prelude (which these tests run without). The
        /// expected output is CRuby 4.0.9's.
        /// </summary>
        public void Numeric_FloatStep1() {
            TestOutput(@"
def s; a = []; r = yield(a); [r, a]; end
p s { |a| 1.0.step(2.0, 0.25) { |x| a << x } }
p s { |a| 1.step(2.0, 0.5) { |x| a << x } }
p s { |a| 1.0.step(by: 0.5, to: 2) { |x| a << x } }
p s { |a| 2.0.step(1.0, -0.3) { |x| a << x } }
p s { |a| 1.1.step(1.5, 0.1) { |x| a << x } }
p s { |a| 0.0.step(1.0, 1.0 / 3) { |x| a << x } }
p s { |a| 1e16.step(1e16 + 4, 1.0) { |x| a << x } }
p s { |a| 1.0.step(2.0, 1.0 / 0) { |x| a << x } }
p s { |a| 1.0.step(0.0 / 0, 1) { |x| a << x } }
p s { |a| 1.0.step(2.0, 0.5) { |x| a << x; break nil } }
p s { |a| 1.0.step(2.0, 0.5) { |x| a << x; break 7 if x > 1 } }
p s { |a| 0.1.step(1.0, 0.1) { |x| a << x } }[1].size, 1.0.method(:step).owner
", @"
[1.0, [1.0, 1.25, 1.5, 1.75, 2.0]]
[1, [1.0, 1.5, 2.0]]
[1.0, [1.0, 1.5, 2.0]]
[2.0, [2.0, 1.7, 1.4, 1.1]]
[1.1, [1.1, 1.2000000000000002, 1.3, 1.4000000000000001, 1.5]]
[0.0, [0.0, 0.3333333333333333, 0.6666666666666666, 1.0]]
[1.0e+16, [1.0e+16, 1.0e+16, 1.0000000000000002e+16, 1.0000000000000004e+16, 1.0000000000000004e+16, 1.0000000000000004e+16]]
[1.0, [1.0]]
[1.0, []]
[nil, [1.0]]
[7, [1.0, 1.5]]
10
Numeric
");
        }

        /// <summary>
        /// Range#step over Fixnum and Float ends with a Float among them loops in C# (#89), as the prelude's
        /// ArithmeticSequence.__step_each__ did. The expected output is CRuby 4.0.9's.
        /// </summary>
        public void Range_FloatStep1() {
            TestOutput(@"
def s; a = []; r = yield(a); [r.class, a]; end
p s { |a| (1.0..2.0).step(0.25) { |x| a << x } }
p s { |a| (1.0...2.0).step(0.25) { |x| a << x } }
p s { |a| (1..2.0).step { |x| a << x } }
p s { |a| (1.0..3).step(1) { |x| a << x } }
p s { |a| (2.0..1.0).step(-0.5) { |x| a << x } }
p s { |a| (1.0..1.5).step(0.1) { |x| a << x } }
p s { |a| (1.0...1.5).step(0.1) { |x| a << x } }
p s { |a| (1.0..2.0).step(0.5) { |x| a << x; break 9 } }
", @"
[Range, [1.0, 1.25, 1.5, 1.75, 2.0]]
[Range, [1.0, 1.25, 1.5, 1.75]]
[Range, [1.0, 2.0]]
[Range, [1.0, 2.0, 3.0]]
[Range, [2.0, 1.5, 1.0]]
[Range, [1.0, 1.1, 1.2, 1.3, 1.4, 1.5]]
[Range, [1.0, 1.1, 1.2, 1.3, 1.4]]
[Integer, [1.0]]
");
        }

        /// <summary>
        /// complex.c's string scanner (String#to_c, Kernel#Complex) is C# now (#89): the texts it reads, its
        /// backing up over a trailing or doubled underscore, polar and rational forms, and the strict caller's
        /// trailing-garbage check. String#to_c of plain Integer parts is answered in C#; the parts as CRuby 4.0.9
        /// reads them. The texts left for str2num (complex.rb, which these tests run without) are the scanner's.
        /// </summary>
        public void Complex_StringScan1() {
            TestOutput(@"
def c(s) = (z = s.to_c; [z.real, z.imaginary])
p c(""1+2i""), c(""-i""), c("" 2j ""), c(""""), c(""7__9""), c(""1_i""), c(""1+x""), c(""-12_345-6i""), c(""1\0+2i"")
p c(""\xff1+2i""), c(""1+2i\xff""), c(""1+2i\xff"".dup.force_encoding(""BINARY"")), c(""１+2i""), c(""1+2iü"".dup.force_encoding(""BINARY"").force_encoding(""UTF-8""))
p Complex.__ir_scan__(""1.5e3-2/3i"", false), Complex.__ir_scan__(""1@2"", false), Complex.__ir_scan__(""1e+i"", false)
p Complex.__ir_scan__(""7__9"", true)[0], Complex.__ir_scan__(""1+2i x"", true)[0], Complex.__ir_scan__(""1+2i "", true)[0]
", @"
[1, 2]
[0, -1]
[0, 2]
[0, 0]
[7, 0]
[1, 0]
[1, 0]
[-12345, -6]
[1, 0]
[0, 0]
[1, 2]
[1, 2]
[0, 0]
[1, 2]
[true, 2, ""1.5e3"", ""-2/3""]
[true, 3, 1, 2]
[false, 0, ""1e"", nil]
false
false
true
");
        }

        /// <summary>
        /// Kernel#Float's decimal parse takes the exact fast path (at most 15 significant digits and a power of
        /// ten up to 22) and otherwise Double.TryParse over a stack buffer (#89); both round as strtod does.
        /// The expected output is CRuby 4.0.9's.
        /// </summary>
        public void Kernel_FloatParse1() {
            TestOutput(@"
p Float(""1.5""), Float("" 1_000.25 ""), Float(""0.30000000000000004""), Float(""9007199254740993""), Float(""1e22""), Float(""1e23""), Float(""3e-23"")
p Float(""1."" + ""0"" * 30), Float(""0."" + ""0"" * 30 + ""1""), Float(""-0.0""), Float(""4.9e-324""), Float(""1e400""), Float(""0x1.8p1""), (Float(""1__0"") rescue $!)
", @"
1.5
1000.25
0.30000000000000004
9.007199254740992e+15
1.0e+22
1.0e+23
3.0e-23
1.0
1.0e-31
-0.0
5.0e-324
Infinity
3.0
#<ArgumentError: invalid value for Float(): ""1__0"">
");
        }

        /// <summary>
        /// String#split with a Regexp leaves $~ where MRI's rb_reg_search leaves it (#89): nil after the search
        /// that finds no more, the last match when a limit stops it, and untouched when it does not search -
        /// a limit of 1, // (characters) and / / (split at a space). The expected output is CRuby 4.0.9's.
        /// </summary>
        public void String_SplitMatchData1() {
            TestOutput(@"
""zz"" =~ /z/; ""a,b"".split(/,/); p $~
""zz"" =~ /z/; ""a,b,c"".split(/,/, 2); p $~, $`
""zz"" =~ /z/; ""abc"".split(//); p $~
""zz"" =~ /z/; ""a b"".split(/ /); p $~
""zz"" =~ /z/; """".split(/,/); p $~
""zz"" =~ /z/; ""a,b"".split(/,/, 1); p $~
""zz"" =~ /z/; ""a,b"".split(/,/) { }; p $~
", @"
nil
#<MatchData "","">
""a""
#<MatchData ""z"">
#<MatchData ""z"">
nil
#<MatchData ""z"">
nil
");
        }

        /// <summary>
        /// A shift width an Int32 does not hold (#89). MRI allocates 1 &lt;&lt; 2**31 (a 256 MB Integer); IronRuby
        /// raised RangeError. Shifts back by such widths, and bit references at such indexes, reach the real bits.
        /// From 2**32 on, a non-MRI implementation may say "shift width too big" (ruby/spec's left_shift_spec).
        /// The expected output is CRuby 4.0's, except for the 2**32 line (CRuby would try to allocate 512 MB).
        /// .NET 9 and later cap a BigInteger at about 2**31 bits, so there such results are the NoMemoryError MRI
        /// raises for an allocation that fails, not a raw OverflowException.
        /// </summary>
        public void Integer_HugeShifts1() {
            if (Environment.Version.Major >= 9) {
                TestOutput(@"
def e; yield; rescue NoMemoryError => x; x.class; end
p e { 1 << 2**31 }, e { 1 << (2**31 - 33) }, e { (2**64) << (2**31 + 1) }, e { 3 >> -(2**31) }
p 7 << -(2**31), -7 << -(2**31), 2**40 << -(2**31), -(2**40) >> 2**31, 1 >> 2**64, -1 >> 2**64, 0 << 2**64
p((1 << 2**32 rescue $!), (-1 >> -(2**64) rescue $!))
", @"
NoMemoryError
NoMemoryError
NoMemoryError
NoMemoryError
0
-1
0
-1
0
-1
0
#<RangeError: shift width too big>
#<RangeError: shift width too big>
");
                return;
            }
            TestOutput(@"
def big(x) = [x.bit_length, x & 0xffff, x >> (x.bit_length - 8)]
b = 1 << 2**31
p big(b), big(-3 << 2**31), big(3 >> -(2**31)), big((2**64 + 3) << (2**31 + 1))
p b >> 2**31, (-b - 1) >> 2**31, (-b) >> 2**31, b >> (2**31 + 1), (b + 5) << -(2**31), (-b + 5) << -(2**31)
p b[2**31], b[2**31 - 1], (-b)[2**31 + 5], (-b - 1)[2**31], (b + 1)[2**64], (-b)[2**64]
p 7 << -(2**31), -7 << -(2**31), 2**40 << -(2**31), -(2**40) >> 2**31, 1 >> 2**64, -1 >> 2**64, 0 << 2**64
p((1 << 2**32 rescue $!), (-1 >> -(2**64) rescue $!), (b << 2**70 rescue $!))
", @"
[2147483649, 0, 128]
[2147483650, 0, -192]
[2147483650, 0, 192]
[2147483714, 0, 128]
1
-2
-1
0
1
-1
1
0
1
0
0
1
0
-1
0
-1
0
-1
0
#<RangeError: shift width too big>
#<RangeError: shift width too big>
#<RangeError: shift width too big>
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
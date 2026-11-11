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
        /// refused. IronRuby refused anything past 2**31 bits. The expected output is CRuby 4.0.9's, except that
        /// a result past .NET's BigInteger cap (about 2**31 bits) is NoMemoryError, as for a shift.
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
            TestOutput(@"
def e; yield; rescue NoMemoryError => x; x.class; end
p e { 2 ** (2**31) }, e { 4 ** (2**30) }
", @"
NoMemoryError
NoMemoryError
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
p Complex.__ir_scan__(""1.5e3-2/3i"", false), Complex.__ir_scan__(""1@2"", false), Complex.__ir_scan__(""1e+i"", false), Complex.__ir_scan__(""1+2/0x"", false)
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
[false, 0, 1, ""+2/0""]
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
        /// A Regexp whose source is a single space splits as that source taken literally, whatever its
        /// options (MRI's rb_str_split_m, SPLIT_TYPE_STRING): / /x splits at each space, as / / does,
        /// although its pattern matches only the empty string. IronRuby split "a  b" by / /x into
        /// ["a", " ", " ", "b"] (it came up in #188). / /x does not search, so $~ is left alone. Two spaces under /x
        /// are a regexp again. An encoding error names the source's encoding, US-ASCII. The expected
        /// output is CRuby 4.0.9's.
        /// </summary>
        public void String_SplitSpaceRegexp1() {
            TestOutput(@"
p ""a  b"".split(/ /x)
p "" a  b "".split(/ /x)
p "" a  b "".split(/ /x, -1)
p ""a b c"".split(/ /x, 2)
p ""a\tb\n c"".split(/ /x)
p ""a  b"".split(/ /ix)
p ""a  b"".split(Regexp.new("" "", Regexp::EXTENDED))
p ""a  b"".split(/  /x)
p ""a b"".split(/ /), ""a b"".split("" ""), ""a  b"".split("" "")
""zz"" =~ /z/; ""a b"".split(/ /x); p $~
p ""a b"".split(/ /x) { |s| print s, "","" }
(""a b"".encode(""UTF-16LE"").split(/ /x) rescue p $!)
", @"
[""a"", """", ""b""]
["""", ""a"", """", ""b""]
["""", ""a"", """", ""b"", """"]
[""a"", ""b c""]
[""a\tb\n"", ""c""]
[""a"", """", ""b""]
[""a"", """", ""b""]
[""a"", "" "", "" "", ""b""]
[""a"", ""b""]
[""a"", ""b""]
[""a"", ""b""]
#<MatchData ""z"">
a,b,""a b""
#<Encoding::CompatibilityError: incompatible character encodings: UTF-16LE and US-ASCII>
");
        }

        /// <summary>
        /// A shift width an Int32 does not hold (#89). MRI allocates 1 &lt;&lt; 2**31 (a 256 MB Integer); IronRuby
        /// raised RangeError. Shifts back by such widths, and bit references at such indexes, reach the real bits.
        /// From 2**32 on, a non-MRI implementation may say "shift width too big" (ruby/spec's left_shift_spec).
        /// The expected output is CRuby 4.0's, except for the 2**32 line (CRuby would try to allocate 512 MB).
        /// .NET caps a BigInteger at about 2**31 bits (since .NET 9), so such results are the NoMemoryError MRI
        /// raises for an allocation that fails, not a raw OverflowException.
        /// </summary>
        public void Integer_HugeShifts1() {
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

        /// <summary>
        /// Integer#[] is C# for an Integer index and for a Fixnum index and length; every other form goes to the
        /// prelude's ports of MRI's int_aref1 and int_aref2. Covers 5[32] (C#'s shift count is mod 32, so it read bit 0),
        /// a nil length (a TypeError), a zero length (no bits), and an empty, equal or reversed Range.
        /// Expected output is CRuby 4.0's.
        /// </summary>
        public void Integer_ElementReference1() {
            TestOutput(@"
require 'ruby4'
class ToInt; def initialize(v) = @v = v; def to_int = @v; def inspect = ""ToInt(#{@v})""; end
def show(label)
  r = yield
  puts ""#{label} => #{r.inspect}""
rescue Exception => e
  puts ""#{label} !! #{e.class}: #{e.message}""
end
[5, -5, 0x7fffffff, -0x80000000, 2**40 + 5, -(2**70) + 12345].each do |v|
  [0, 2, 31, 32, 33, 63, 64, 100, -1, 2**40, -(2**40), 1.9, -0.5, 31.5, Float::NAN, nil, ToInt.new(3), ToInt.new(2**70)].each do |i|
    show(""#{v}[#{i.inspect}]"") { v[i] }
  end
  [[2, 3], [0, 0], [3, -1], [-3, 4], [-40, 50], [60, 10], [1, 63], [1, 70], [1.5, 2], [2, nil], [nil, 2], [""1"", 2], [2, ToInt.new(3)], [1..2, 3]].each do |i, l|
    show(""#{v}[#{i.inspect}, #{l.inspect}]"") { v[i, l] }
  end
  [1..3, 2...2, 3..3, 5..2, -3..2, 40..30, 2.., ..3, ...0, nil..nil, 1.5..3.5].each do |r|
    show(""#{v}[#{r.inspect}]"") { v[r] }
  end
end
show(""5[]"") { 5[] }
show(""5[1, 2, 3]"") { 5[1, 2, 3] }
show(""arity"") { [5.method(:[]).arity, 5.method(:[]).parameters] }
show(""send"") { [5.send(:[], 2), 5.public_send(:[], 0, 3), [0, 1, 2].map(&5.method(:[]))] }
", @"
5[0] => 1
5[2] => 1
5[31] => 0
5[32] => 0
5[33] => 0
5[63] => 0
5[64] => 0
5[100] => 0
5[-1] => 0
5[1099511627776] => 0
5[-1099511627776] => 0
5[1.9] => 0
5[-0.5] => 1
5[31.5] => 0
5[NaN] !! FloatDomainError: NaN
5[nil] !! TypeError: no implicit conversion of nil into Integer
5[ToInt(3)] => 0
5[ToInt(1180591620717411303424)] => 0
5[2, 3] => 1
5[0, 0] => 0
5[3, -1] => 0
5[-3, 4] => 8
5[-40, 50] => 5497558138880
5[60, 10] => 0
5[1, 63] => 2
5[1, 70] => 2
5[1.5, 2] => 2
5[2, nil] !! TypeError: no implicit conversion of nil into Integer
5[nil, 2] !! TypeError: no implicit conversion of nil into Integer
5[""1"", 2] !! TypeError: no implicit conversion of String into Integer
5[2, ToInt(3)] => 1
5[1..2, 3] !! TypeError: no implicit conversion of Range into Integer
5[1..3] => 2
5[2...2] => 0
5[3..3] => 0
5[5..2] => 0
5[-3..2] => 40
5[40..30] => 0
5[2..] => 1
5[..3] !! ArgumentError: The beginless range for Integer#[] results in infinity
5[...0] => 0
5[nil..nil] !! ArgumentError: comparison of NilClass with 0 failed
5[1.5..3.5] => 2
-5[0] => 1
-5[2] => 0
-5[31] => 1
-5[32] => 1
-5[33] => 1
-5[63] => 1
-5[64] => 1
-5[100] => 1
-5[-1] => 0
-5[1099511627776] => 1
-5[-1099511627776] => 0
-5[1.9] => 1
-5[-0.5] => 1
-5[31.5] => 1
-5[NaN] !! FloatDomainError: NaN
-5[nil] !! TypeError: no implicit conversion of nil into Integer
-5[ToInt(3)] => 1
-5[ToInt(1180591620717411303424)] => 1
-5[2, 3] => 6
-5[0, 0] => 0
-5[3, -1] => -1
-5[-3, 4] => 8
-5[-40, 50] => 1120402348703744
-5[60, 10] => 1023
-5[1, 63] => 9223372036854775805
-5[1, 70] => 1180591620717411303421
-5[1.5, 2] => 1
-5[2, nil] !! TypeError: no implicit conversion of nil into Integer
-5[nil, 2] !! TypeError: no implicit conversion of nil into Integer
-5[""1"", 2] !! TypeError: no implicit conversion of String into Integer
-5[2, ToInt(3)] => 6
-5[1..2, 3] !! TypeError: no implicit conversion of Range into Integer
-5[1..3] => 5
-5[2...2] => 0
-5[3..3] => 1
-5[5..2] => -1
-5[-3..2] => 24
-5[40..30] => -1
-5[2..] => -2
-5[..3] !! ArgumentError: The beginless range for Integer#[] results in infinity
-5[...0] => 0
-5[nil..nil] !! ArgumentError: comparison of NilClass with 0 failed
-5[1.5..3.5] => 5
2147483647[0] => 1
2147483647[2] => 1
2147483647[31] => 0
2147483647[32] => 0
2147483647[33] => 0
2147483647[63] => 0
2147483647[64] => 0
2147483647[100] => 0
2147483647[-1] => 0
2147483647[1099511627776] => 0
2147483647[-1099511627776] => 0
2147483647[1.9] => 1
2147483647[-0.5] => 1
2147483647[31.5] => 0
2147483647[NaN] !! FloatDomainError: NaN
2147483647[nil] !! TypeError: no implicit conversion of nil into Integer
2147483647[ToInt(3)] => 1
2147483647[ToInt(1180591620717411303424)] => 0
2147483647[2, 3] => 7
2147483647[0, 0] => 0
2147483647[3, -1] => 268435455
2147483647[-3, 4] => 8
2147483647[-40, 50] => 1124800395214848
2147483647[60, 10] => 0
2147483647[1, 63] => 1073741823
2147483647[1, 70] => 1073741823
2147483647[1.5, 2] => 3
2147483647[2, nil] !! TypeError: no implicit conversion of nil into Integer
2147483647[nil, 2] !! TypeError: no implicit conversion of nil into Integer
2147483647[""1"", 2] !! TypeError: no implicit conversion of String into Integer
2147483647[2, ToInt(3)] => 7
2147483647[1..2, 3] !! TypeError: no implicit conversion of Range into Integer
2147483647[1..3] => 7
2147483647[2...2] => 0
2147483647[3..3] => 1
2147483647[5..2] => 67108863
2147483647[-3..2] => 56
2147483647[40..30] => 0
2147483647[2..] => 536870911
2147483647[..3] !! ArgumentError: The beginless range for Integer#[] results in infinity
2147483647[...0] => 0
2147483647[nil..nil] !! ArgumentError: comparison of NilClass with 0 failed
2147483647[1.5..3.5] => 7
-2147483648[0] => 0
-2147483648[2] => 0
-2147483648[31] => 1
-2147483648[32] => 1
-2147483648[33] => 1
-2147483648[63] => 1
-2147483648[64] => 1
-2147483648[100] => 1
-2147483648[-1] => 0
-2147483648[1099511627776] => 1
-2147483648[-1099511627776] => 0
-2147483648[1.9] => 0
-2147483648[-0.5] => 0
-2147483648[31.5] => 1
-2147483648[NaN] !! FloatDomainError: NaN
-2147483648[nil] !! TypeError: no implicit conversion of nil into Integer
-2147483648[ToInt(3)] => 0
-2147483648[ToInt(1180591620717411303424)] => 1
-2147483648[2, 3] => 0
-2147483648[0, 0] => 0
-2147483648[3, -1] => -268435456
-2147483648[-3, 4] => 0
-2147483648[-40, 50] => 0
-2147483648[60, 10] => 1023
-2147483648[1, 63] => 9223372035781033984
-2147483648[1, 70] => 1180591620716337561600
-2147483648[1.5, 2] => 0
-2147483648[2, nil] !! TypeError: no implicit conversion of nil into Integer
-2147483648[nil, 2] !! TypeError: no implicit conversion of nil into Integer
-2147483648[""1"", 2] !! TypeError: no implicit conversion of String into Integer
-2147483648[2, ToInt(3)] => 0
-2147483648[1..2, 3] !! TypeError: no implicit conversion of Range into Integer
-2147483648[1..3] => 0
-2147483648[2...2] => 0
-2147483648[3..3] => 0
-2147483648[5..2] => -67108864
-2147483648[-3..2] => 0
-2147483648[40..30] => -1
-2147483648[2..] => -536870912
-2147483648[..3] => 0
-2147483648[...0] => 0
-2147483648[nil..nil] !! ArgumentError: comparison of NilClass with 0 failed
-2147483648[1.5..3.5] => 0
1099511627781[0] => 1
1099511627781[2] => 1
1099511627781[31] => 0
1099511627781[32] => 0
1099511627781[33] => 0
1099511627781[63] => 0
1099511627781[64] => 0
1099511627781[100] => 0
1099511627781[-1] => 0
1099511627781[1099511627776] => 0
1099511627781[-1099511627776] => 0
1099511627781[1.9] => 0
1099511627781[-0.5] => 1
1099511627781[31.5] => 0
1099511627781[NaN] !! FloatDomainError: NaN
1099511627781[nil] !! TypeError: no implicit conversion of nil into Integer
1099511627781[ToInt(3)] => 0
1099511627781[ToInt(1180591620717411303424)] => 0
1099511627781[2, 3] => 1
1099511627781[0, 0] => 0
1099511627781[3, -1] => 137438953472
1099511627781[-3, 4] => 8
1099511627781[-40, 50] => 5497558138880
1099511627781[60, 10] => 0
1099511627781[1, 63] => 549755813890
1099511627781[1, 70] => 549755813890
1099511627781[1.5, 2] => 2
1099511627781[2, nil] !! TypeError: no implicit conversion of nil into Integer
1099511627781[nil, 2] !! TypeError: no implicit conversion of nil into Integer
1099511627781[""1"", 2] !! TypeError: no implicit conversion of String into Integer
1099511627781[2, ToInt(3)] => 1
1099511627781[1..2, 3] !! TypeError: no implicit conversion of Range into Integer
1099511627781[1..3] => 2
1099511627781[2...2] => 0
1099511627781[3..3] => 0
1099511627781[5..2] => 34359738368
1099511627781[-3..2] => 40
1099511627781[40..30] => 1
1099511627781[2..] => 274877906945
1099511627781[..3] !! ArgumentError: The beginless range for Integer#[] results in infinity
1099511627781[...0] => 0
1099511627781[nil..nil] !! ArgumentError: comparison of NilClass with 0 failed
1099511627781[1.5..3.5] => 2
-1180591620717411291079[0] => 1
-1180591620717411291079[2] => 0
-1180591620717411291079[31] => 0
-1180591620717411291079[32] => 0
-1180591620717411291079[33] => 0
-1180591620717411291079[63] => 0
-1180591620717411291079[64] => 0
-1180591620717411291079[100] => 1
-1180591620717411291079[-1] => 0
-1180591620717411291079[1099511627776] => 1
-1180591620717411291079[-1099511627776] => 0
-1180591620717411291079[1.9] => 0
-1180591620717411291079[-0.5] => 1
-1180591620717411291079[31.5] => 0
-1180591620717411291079[NaN] !! RangeError: float NaN out of range of integer
-1180591620717411291079[nil] !! TypeError: no implicit conversion from nil to integer
-1180591620717411291079[ToInt(3)] => 1
-1180591620717411291079[ToInt(1180591620717411303424)] !! RangeError: bignum too big to convert into 'long'
-1180591620717411291079[2, 3] => 6
-1180591620717411291079[0, 0] => 0
-1180591620717411291079[3, -1] => -147573952589676411385
-1180591620717411291079[-3, 4] => 8
-1180591620717411291079[-40, 50] => 62672162783232
-1180591620717411291079[60, 10] => 0
-1180591620717411291079[1, 63] => 6172
-1180591620717411291079[1, 70] => 590295810358705657884
-1180591620717411291079[1.5, 2] => 0
-1180591620717411291079[2, nil] !! TypeError: no implicit conversion of nil into Integer
-1180591620717411291079[nil, 2] !! TypeError: no implicit conversion of nil into Integer
-1180591620717411291079[""1"", 2] !! TypeError: no implicit conversion of String into Integer
-1180591620717411291079[2, ToInt(3)] => 6
-1180591620717411291079[1..2, 3] !! TypeError: no implicit conversion of Range into Integer
-1180591620717411291079[1..3] => 4
-1180591620717411291079[2...2] => 0
-1180591620717411291079[3..3] => 1
-1180591620717411291079[5..2] => -36893488147419102847
-1180591620717411291079[-3..2] => 8
-1180591620717411291079[40..30] => -1073741824
-1180591620717411291079[2..] => -295147905179352822770
-1180591620717411291079[..3] !! ArgumentError: The beginless range for Integer#[] results in infinity
-1180591620717411291079[...0] => 0
-1180591620717411291079[nil..nil] !! ArgumentError: comparison of NilClass with 0 failed
-1180591620717411291079[1.5..3.5] => 4
5[] !! ArgumentError: wrong number of arguments (given 0, expected 1..2)
5[1, 2, 3] !! ArgumentError: wrong number of arguments (given 3, expected 1..2)
arity => [-1, [[:rest]]]
send => [1, 5, [1, 0, 1]]
");
        }

        /// <summary>
        /// Integer.sqrt of a large Integer (Integer.SqrtRem, the Karatsuba square root, and its small-operand base case).
        /// Expected output is CRuby 4.0's.
        /// </summary>
        public void Integer_SqrtLarge1() {
            TestOutput(@"
require 'ruby4'
[2**62, 2**62 + 1, 2**64 - 1, 2**70, 2**70 - 1, 10**40 + 7, (10**20)**2 - 1, (10**20)**2, (2**500 + 3)**2 - 1, (2**500 + 3)**2,
 2**999, 2**1000 + 1, 2**2000, (2**1023 - 1)**2, (2**1023)**2 - 1, (2**1100 + 12345)**2 - 1, (2**1100 + 12345)**2].each do |n|
  r = Integer.sqrt(n)
  puts ""#{n.bit_length}: #{r.bit_length} #{r % 1_000_000_007} #{r * r <= n && (r + 1) * (r + 1) > n}""
end
srand(7)
ok = (1..300).all? do |i|
  k = rand(2**(i * 7)) + 1
  Integer.sqrt(k * k) == k && Integer.sqrt(k * k - 1) == k - 1 && Integer.sqrt(k * k + 2 * k) == k
end
p ok
p((Integer.sqrt(-(2**70)) rescue $!.class))
", @"
63: 32 147483634 true
63: 32 147483634 true
64: 32 294967267 true
71: 36 359738130 true
70: 35 359738129 true
133: 67 4900 true
133: 67 4899 true
133: 67 4900 true
1001: 501 390483009 true
1001: 501 390483010 true
1000: 500 563322577 true
1001: 501 390483007 true
2001: 1001 688423210 true
2046: 1023 406367295 true
2046: 1023 406367295 true
2201: 1101 466440651 true
2201: 1101 466440652 true
true
Math::DomainError
");
        }

        /// <summary>
        /// Integer#pow(b, m) of three Fixnums squares and multiplies in a long; 0.pow(x, m) is 0 for any modulus (0
        /// included) unless x is 0, as MRI's rb_int_powm shortcuts it. Expected output is CRuby 4.0's.
        /// </summary>
        public void Integer_PowModulo1() {
            TestOutput(@"
def show(label)
  r = yield
  puts ""#{label} => #{r.inspect}""
rescue Exception => e
  puts ""#{label} !! #{e.class}: #{e.message}""
end
[[12345, 5, 1000], [12345, 5, -1000], [-12345, 5, 1000], [-12345, 3, -7], [2, 10**9, 1_000_000_007], [3, 2**31 - 1, 2**31 - 1],
 [-(2**31), 2**31 - 1, -(2**31)], [2**31 - 1, 2**31 - 1, 2**31 - 1], [7, 0, 1], [7, 0, -1], [7, 0, 13], [0, 0, 13], [0, 0, 0],
 [0, 5, 0], [0, 5, -3], [0, 2**70, 0], [5, 3, 0], [5, -1, 7], [5, 1.5, 7], [5, 2, 7.0], [5, 2, nil], [2**70, 5, 1000], [5, 2**70, 1009], [5, 3, 2**70]].each do |a, b, m|
  show(""#{a}.pow(#{b.inspect}, #{m.inspect})"") { a.pow(b, m) }
end
", @"
12345.pow(5, 1000) => 625
12345.pow(5, -1000) => -375
-12345.pow(5, 1000) => 375
-12345.pow(3, -7) => -1
2.pow(1000000000, 1000000007) => 140625001
3.pow(2147483647, 2147483647) => 3
-2147483648.pow(2147483647, -2147483648) => 0
2147483647.pow(2147483647, 2147483647) => 0
7.pow(0, 1) => 0
7.pow(0, -1) => 0
7.pow(0, 13) => 1
0.pow(0, 13) => 1
0.pow(0, 0) !! ZeroDivisionError: divided by 0
0.pow(5, 0) => 0
0.pow(5, -3) => 0
0.pow(1180591620717411303424, 0) => 0
5.pow(3, 0) !! ZeroDivisionError: divided by 0
5.pow(-1, 7) !! RangeError: Integer#pow() 1st argument cannot be negative when 2nd argument specified
5.pow(1.5, 7) !! TypeError: Integer#pow() 2nd argument not allowed unless a 1st argument is integer
5.pow(2, 7.0) !! TypeError: Integer#pow() 2nd argument not allowed unless all arguments are integers
5.pow(2, nil) !! TypeError: Integer#pow() 2nd argument not allowed unless all arguments are integers
1180591620717411303424.pow(5, 1000) => 624
5.pow(1180591620717411303424, 1009) => 993
5.pow(3, 1180591620717411303424) => 125
");
        }

        /// <summary>
        /// Kernel#Integer parses the digits in a long while they fit, and past that in long chunks folded into a BigInteger;
        /// the general overload (eight parameters) is called without the runtime's tail call helpers. Expected output is
        /// CRuby 4.0's.
        /// </summary>
        public void Kernel_IntegerParse1() {
            TestOutput(@"
%w[0 123 -123 +7 0x1A 0b11 0o17 017 1_000 1__0 12x 360287970189639679 360287970189639680 9223372036854775807 9223372036854775808
   -9223372036854775808 -9223372036854775809 123456789012345678901234567890 -123456789012345678901234567890123456789012345678901234567890
   0xffffffffffffffffffffffffffffff zzzzzzzzzzzzzzzzzzzz].each do |s|
  a = (Integer(s) rescue $!.class)
  puts ""#{s} => #{a.inspect} #{Integer(s, exception: false).inspect} #{(Integer(s, 36) rescue $!.class).inspect} #{(Integer(s, 16) rescue $!.class).inspect}""
end
p Integer(12.5, exception: false), Integer(nil, exception: false), Integer(""1"", 2, exception: false)
", @"
0 => 0 0 0 0
123 => 123 123 1371 291
-123 => -123 -123 -1371 -291
+7 => 7 7 7 7
0x1A => 26 26 42814 26
0b11 => 3 3 14293 2833
0o17 => 15 15 31147 ArgumentError
017 => 15 15 43 23
1_000 => 1000 1000 46656 4096
1__0 => ArgumentError nil ArgumentError ArgumentError
12x => ArgumentError nil 1401 ArgumentError
360287970189639679 => 360287970189639679 360287970189639679 907301047451005346972029605 996306460323103348345
360287970189639680 => 360287970189639680 360287970189639680 907301047451005346972029632 996306460323103348352
9223372036854775807 => 9223372036854775807 9223372036854775807 93419446956697119914106100423 43132194673522839607303
9223372036854775808 => 9223372036854775808 9223372036854775808 93419446956697119914106100424 43132194673522839607304
-9223372036854775808 => -9223372036854775808 -9223372036854775808 -93419446956697119914106100424 -43132194673522839607304
-9223372036854775809 => -9223372036854775809 -9223372036854775809 -93419446956697119914106100425 -43132194673522839607305
123456789012345678901234567890 => 123456789012345678901234567890 123456789012345678901234567890 1436287679432363134028105837355561937436483460 94522879687365475552814062743484560
-123456789012345678901234567890123456789012345678901234567890 => -123456789012345678901234567890123456789012345678901234567890 -123456789012345678901234567890123456789012345678901234567890 -70196661532208757042783977637701616117055871610586212648024968891418249178413935920798572420 -125642457922655546520478558874028399442089214994038133908467238479231120
0xffffffffffffffffffffffffffffff => 1329227995784915872903807060280344575 1329227995784915872903807060280344575 1633777235354469464643074861723322472080662705883 1329227995784915872903807060280344575
zzzzzzzzzzzzzzzzzzzz => ArgumentError nil 13367494538843734067838845976575 ArgumentError
12
nil
1
");
        }

        /// <summary>
        /// A Bignum's bit read converts a Float index as NUM2LONG does: 2.0**63 is past a long's range (a RangeError), -2.0**63
        /// is not. Expected output is CRuby 4.0's.
        /// </summary>
        public void Integer_ElementReferenceFloatBound1() {
            TestOutput(@"
require 'ruby4'
[9223372036854775808.0, -9223372036854775808.0, 9223372036854774784.0, -9223372036854777856.0, 1e19, -1e19].each do |f|
  [2**70 + 5, -(2**70) + 5].each do |v|
    r = begin; v[f]; rescue => e; ""#{e.class}: #{e.message}""; end
    puts ""#{v}[#{f}] => #{r}""
  end
end
", @"
1180591620717411303429[9.223372036854776e+18] => RangeError: float 9.223372037e+18 out of range of integer
-1180591620717411303419[9.223372036854776e+18] => RangeError: float 9.223372037e+18 out of range of integer
1180591620717411303429[-9.223372036854776e+18] => 0
-1180591620717411303419[-9.223372036854776e+18] => 0
1180591620717411303429[9.223372036854775e+18] => 0
-1180591620717411303419[9.223372036854775e+18] => 1
1180591620717411303429[-9.223372036854778e+18] => RangeError: float -9.223372037e+18 out of range of integer
-1180591620717411303419[-9.223372036854778e+18] => RangeError: float -9.223372037e+18 out of range of integer
1180591620717411303429[1.0e+19] => RangeError: float 1e+19 out of range of integer
-1180591620717411303419[1.0e+19] => RangeError: float 1e+19 out of range of integer
1180591620717411303429[-1.0e+19] => RangeError: float -1e+19 out of range of integer
-1180591620717411303419[-1.0e+19] => RangeError: float -1e+19 out of range of integer
");
        }

        /// <summary>
        /// Integer.SqrtRem (Integer.sqrt above 2**62): the root s and remainder r of n must satisfy s*s + r == n and
        /// 0 &lt;= r &lt;= 2s, around the sizes where the recursion splits differently (bit lengths 4k - 1 .. 4k + 2, the
        /// small-operand limit at 106 bits), at perfect squares and their neighbours, at powers of two, and up to a
        /// million bits.
        /// </summary>
        public void Integer_SqrtRem1() {
            var random = new Random(89);
            void Check(BigInteger n) {
                BigInteger s = IronRuby.Builtins.Integer.SqrtRem(n, out BigInteger r);
                Assert(s * s + r == n && r.Sign >= 0 && r <= 2 * s);
            }
            BigInteger RandomBits(int bits) {
                var bytes = new byte[(bits + 7) / 8 + 1];
                random.NextBytes(bytes);
                bytes[bytes.Length - 1] = 0;
                BigInteger value = new BigInteger(bytes) & ((BigInteger.One << bits) - 1);
                return value | (BigInteger.One << (bits - 1));
            }
            for (int bits = 1; bits <= 600; bits++) {
                Check((BigInteger.One << bits) - 1);
                Check(BigInteger.One << bits);
                Check((BigInteger.One << bits) + 1);
                for (int i = 0; i < 20; i++) {
                    Check(RandomBits(bits));
                    BigInteger k = RandomBits(bits);
                    Check(k * k - 1);
                    Check(k * k);
                    Check(k * k + 1);
                    Check(k * k + 2 * k);
                    Check(k * k + 2 * k + 1);
                }
            }
            foreach (int bits in new[] { 4095, 4096, 4097, 4098, 10_000, 100_000, 1_000_000 }) {
                BigInteger k = RandomBits(bits / 2);
                Check(RandomBits(bits));
                Check(k * k - 1);
                Check(k * k);
                Check(BigInteger.One << bits);
            }
        }

        /// <summary>
        /// Integer#to_s of a Bignum (ClrInteger.ToRadixString) hands a radix that is not a power of two to the DLR's
        /// subquadratic MathUtils.ToString, and in radix 10 changes over to it from BigInteger.ToString at a size that
        /// depends on the runtime. Around powers of the radix, on both sides of the change-over and with both signs, it
        /// must give what dividing out a few digits at a time gives.
        /// </summary>
        public void Integer_ToRadixStringLarge1() {
            var random = new Random(89);
            for (int radix = 2; radix <= 36; radix++) {
                var values = new System.Collections.Generic.List<BigInteger> {
                    BigInteger.Zero, BigInteger.One, long.MaxValue, (BigInteger)long.MaxValue + 1, ulong.MaxValue,
                };
                foreach (int exponent in new[] { 13, 40, 64, 1000, 4000 }) {
                    BigInteger power = BigInteger.Pow(radix, exponent);
                    values.Add(power - 1);
                    values.Add(power);
                    values.Add(power + 1);
                }
                int[] sizes = (radix == 10 || radix == 7) ? new[] { 65, 100, 1500, 6000, 40000, 100000 } : new[] { 65, 100, 1500, 6000 };
                foreach (int bits in sizes) {
                    byte[] bytes = new byte[bits / 8 + 1];
                    random.NextBytes(bytes);
                    bytes[bytes.Length - 1] = 0;
                    values.Add(new BigInteger(bytes));
                }

                foreach (BigInteger value in values) {
                    string expected = ReferenceRadixString(value, radix);
                    Assert(ClrInteger.ToRadixString(value, radix) == expected);
                    if (!value.IsZero) {
                        Assert(ClrInteger.ToRadixString(-value, radix) == "-" + expected);
                    }
                }
            }
        }

        // the digits of a non-negative value, lower case, found by dividing out as many digits as fit in an int at a time
        private static string ReferenceRadixString(BigInteger value, int radix) {
            if (value.IsZero) {
                return "0";
            }
            int chunkDigits = 1;
            long chunk = radix;
            while (chunk * radix <= int.MaxValue) {
                chunk *= radix;
                chunkDigits++;
            }
            var parts = new System.Collections.Generic.List<string>();
            while (!value.IsZero) {
                value = BigInteger.DivRem(value, chunk, out BigInteger remainder);
                var digits = new char[chunkDigits];
                long part = (long)remainder;
                for (int i = chunkDigits - 1; i >= 0; i--) {
                    digits[i] = "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(part % radix)];
                    part /= radix;
                }
                parts.Add(new string(digits));
            }
            parts.Reverse();
            return string.Concat(parts).TrimStart('0');
        }

        /// <summary>
        /// format's %d, %x, %X, %o, %b, %B and %f of Bignums take Integer#to_s's digits (they divided by the radix once per
        /// digit); Integer#digits of a Bignum reads them too, and splits by powers of a radix above 36. Expected output is
        /// CRuby 4.0's.
        /// </summary>
        public void Integer_FormatAndDigitsLarge1() {
            TestOutput(@"
x = 2**70 + 0xabc
[x, -x, 7**40, -(7**40)].each do |v|
  puts format(""%d|%x|%X|%o|%b|%B|%#x|%+x|% o|%.30d|%30x|%-30b|"", v, v, v, v, v, v, v, v, v, v, v, v)
end
puts format(""%f|%.2f"", 3**50, -(3**50))
def naive(n, b); r = []; while n > 0; n, d = n.divmod(b); r << d; end; r; end
srand 89
vals = [0, 1, 2**64 - 1, 2**64, 10**40, 10**40 - 1] + [100, 1000, 3000].map { |bits| rand(2**bits) | (1 << (bits - 1)) }
ok = [2, 7, 10, 16, 36, 37, 1000, 2**32, 2**64 - 1, 2**64, 2**70 + 1].all? do |b|
  vals.all? { |v| v.digits(b) == (v.zero? ? [0] : naive(v, b)) }
end
p ok
p (10**40).digits(10**20), (2**128).digits(2**64), 0.digits(2**70)
", @"
1180591620717411306172|400000000000000abc|400000000000000ABC|200000000000000000005274|10000000000000000000000000000000000000000000000000000000000101010111100|10000000000000000000000000000000000000000000000000000000000101010111100|0x400000000000000abc|+400000000000000abc| 200000000000000000005274|000000001180591620717411306172|            400000000000000abc|10000000000000000000000000000000000000000000000000000000000101010111100|
-1180591620717411306172|..fbffffffffffffff544|..FBFFFFFFFFFFFFFF544|..7577777777777777777772504|..101111111111111111111111111111111111111111111111111111111111010101000100|..101111111111111111111111111111111111111111111111111111111111010101000100|0x..fbffffffffffffff544|-400000000000000abc|-200000000000000000005274|-000000001180591620717411306172|         ..fbffffffffffffff544|..101111111111111111111111111111111111111111111111111111111111010101000100|
6366805760909027985741435139224001|139e862f1509ba9c74345f78771c1|139E862F1509BA9C74345F78771C1|23475030570520467247072064276741670701|10011100111101000011000101111000101010000100110111010100111000111010000110100010111110111100001110111000111000001|10011100111101000011000101111000101010000100110111010100111000111010000110100010111110111100001110111000111000001|0x139e862f1509ba9c74345f78771c1|+139e862f1509ba9c74345f78771c1| 23475030570520467247072064276741670701|6366805760909027985741435139224001| 139e862f1509ba9c74345f78771c1|10011100111101000011000101111000101010000100110111010100111000111010000110100010111110111100001110111000111000001|
-6366805760909027985741435139224001|..fec6179d0eaf645638bcba08788e3f|..FEC6179D0EAF645638BCBA08788E3F|..754302747207257310530705713501036107077|..101100011000010111100111010000111010101111011001000101011000111000101111001011101000001000011110001000111000111111|..101100011000010111100111010000111010101111011001000101011000111000101111001011101000001000011110001000111000111111|0x..fec6179d0eaf645638bcba08788e3f|-139e862f1509ba9c74345f78771c1|-23475030570520467247072064276741670701|-6366805760909027985741435139224001|..fec6179d0eaf645638bcba08788e3f|..101100011000010111100111010000111010101111011001000101011000111000101111001011101000001000011110001000111000111111|
717897987691852588770249.000000|-717897987691852588770249.00
true
[0, 0, 1]
[0, 0, 1]
[0]
");
        }

        /// <summary>
        /// Range#size is C# (RangeOps.Size) for an Integer beginning with an Integer or no end, and a nil or Float beginning;
        /// the prelude's __size__ answers the rest. Like MRI's range_size it reads the endpoints, not #begin. Expected output
        /// is CRuby 4.0's.
        /// </summary>
        public void Range_Size1() {
            TestOutput(@"
require 'ruby4'
def show(l); v = begin; yield; rescue => e; ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
show(""1..10"") { (1..10).size }
show(""1...10"") { (1...10).size }
show(""10..1"") { (10..1).size }
show(""5...5"") { (5...5).size }
show(""-3..3"") { (-3..3).size }
show(""min..max"") { (-2**31..2**31 - 1).size }
show(""1.."") { (1..).size }
show(""1..."") { (1...).size }
show(""big..big"") { (2**64..2**65).size }
show(""1..big"") { (1..2**64).size }
show(""big...1"") { (2**64...1).size }
show(""big.."") { (2**64..).size }
show(""1..2.5"") { (1..2.5).size }
show(""1...3.0"") { (1...3.0).size }
show(""1..inf"") { (1..Float::INFINITY).size }
show(""1..1r"") { (1..1r).size }
show(""1.0..2.0"") { (1.0..2.0).size }
show(""1.5.."") { (1.5..).size }
show(""..1"") { (..1).size }
show(""nil..nil"") { (nil..nil).size }
show(""a..z"") { (""a""..""z"").size }
show(""time"") { t = Time.at(0); (t..t).size }
show(""sub"") { Class.new(Range) { def begin; 100; end }.new(1, 3).size }
show(""enum"") { (1..4).each_slice(2).size }
", @"
1..10: 10
1...10: 9
10..1: 0
5...5: 0
-3..3: 7
min..max: 4294967296
1..: Infinity
1...: Infinity
big..big: 18446744073709551617
1..big: 18446744073709551616
big...1: 0
big..: Infinity
1..2.5: 2
1...3.0: 2
1..inf: Infinity
1..1r: 1
1.0..2.0: ""TypeError: can't iterate from Float""
1.5..: ""TypeError: can't iterate from Float""
..1: ""TypeError: can't iterate from NilClass""
nil..nil: ""TypeError: can't iterate from NilClass""
a..z: nil
time: ""TypeError: can't iterate from Time""
sub: 3
enum: 2
");
        }

        /// <summary>
        /// Integer() and Float() with exception: from a JIT keyword call site go past the keyword Hash (KernelOps.IntegerKeywordEntry,
        /// FloatKeywordEntry); what they leave to the ordinary call - errors, a bad keyword, a base that raises - must come out as it did,
        /// and a redefined Kernel#Integer must be called instead while it is there. Expected output is CRuby 4.0's.
        /// </summary>
        public void Kernel_ConversionKeywordEntry1() {
            TestOutput(@"
def int(v) = Integer(v, exception: false)
def int16(v, b) = Integer(v, b, exception: false)
def flt(v) = Float(v, exception: false)
def int!(v) = begin; Integer(v, exception: true); rescue => e; ""#{e.class}: #{e.message}""; end
def flt!(v) = begin; Float(v, exception: true); rescue => e; ""#{e.class}: #{e.message}""; end
vals = [""12"", "" -1_0 "", ""0x1f"", """", ""1__2"", ""z"", ""1.5"", ""1e2"", ""1\0"", nil, true, 2.9, 0.0 / 0.0, 2**64, 7]
2.times do
  puts vals.map { |v| [int(v), flt(v), int!(v), flt!(v)].inspect }
  puts [int16(""ff"", 16), int16("""", 1).inspect, int16(""12"", 37)].inspect rescue puts ""#{$!.class}: #{$!.message}""
  puts [int16(""11"", 2), int16(""z"", 36), int16(""9"", 8)].inspect
end
puts begin; Integer(""1"", exception: 1); rescue => e; ""#{e.class}: #{e.message}""; end
puts begin; Float(""1"", exception: nil); rescue => e; ""#{e.class}: #{e.message}""; end
puts begin; Integer(""1"", foo: false); rescue => e; ""#{e.class}: #{e.message}""; end
module Kernel
  alias_method :saved_Integer, :Integer
  def Integer(*a, **k) = ""mine #{a.inspect} #{k.inspect}""
end
puts int(""5"")
module Kernel
  remove_method :Integer
  alias_method :Integer, :saved_Integer
end
puts int(""6"")
def ki(s) = Kernel.Integer(s, exception: false)
3.times { ki(""1"") }
Kernel.send(:private_class_method, :Integer)
puts begin; ki(""5""); rescue NoMethodError; ""NoMethodError""; end
", @"
[12, 12.0, 12, 12.0]
[-10, -10.0, -10, -10.0]
[31, 31.0, 31, 31.0]
[nil, nil, ""ArgumentError: invalid value for Integer(): \""\"""", ""ArgumentError: invalid value for Float(): \""\""""]
[nil, nil, ""ArgumentError: invalid value for Integer(): \""1__2\"""", ""ArgumentError: invalid value for Float(): \""1__2\""""]
[nil, nil, ""ArgumentError: invalid value for Integer(): \""z\"""", ""ArgumentError: invalid value for Float(): \""z\""""]
[nil, 1.5, ""ArgumentError: invalid value for Integer(): \""1.5\"""", 1.5]
[nil, 100.0, ""ArgumentError: invalid value for Integer(): \""1e2\"""", 100.0]
[nil, nil, ""ArgumentError: invalid value for Integer(): \""1\\u0000\"""", ""ArgumentError: string for Float contains null byte""]
[nil, nil, ""TypeError: can't convert nil into Integer"", ""TypeError: can't convert nil into Float""]
[nil, nil, ""TypeError: can't convert true into Integer"", ""TypeError: can't convert true into Float""]
[2, 2.9, 2, 2.9]
[nil, NaN, ""FloatDomainError: NaN"", NaN]
[18446744073709551616, 1.8446744073709552e+19, 18446744073709551616, 1.8446744073709552e+19]
[7, 7.0, 7, 7.0]
ArgumentError: invalid radix 1
[3, 35, nil]
[12, 12.0, 12, 12.0]
[-10, -10.0, -10, -10.0]
[31, 31.0, 31, 31.0]
[nil, nil, ""ArgumentError: invalid value for Integer(): \""\"""", ""ArgumentError: invalid value for Float(): \""\""""]
[nil, nil, ""ArgumentError: invalid value for Integer(): \""1__2\"""", ""ArgumentError: invalid value for Float(): \""1__2\""""]
[nil, nil, ""ArgumentError: invalid value for Integer(): \""z\"""", ""ArgumentError: invalid value for Float(): \""z\""""]
[nil, 1.5, ""ArgumentError: invalid value for Integer(): \""1.5\"""", 1.5]
[nil, 100.0, ""ArgumentError: invalid value for Integer(): \""1e2\"""", 100.0]
[nil, nil, ""ArgumentError: invalid value for Integer(): \""1\\u0000\"""", ""ArgumentError: string for Float contains null byte""]
[nil, nil, ""TypeError: can't convert nil into Integer"", ""TypeError: can't convert nil into Float""]
[nil, nil, ""TypeError: can't convert true into Integer"", ""TypeError: can't convert true into Float""]
[2, 2.9, 2, 2.9]
[nil, NaN, ""FloatDomainError: NaN"", NaN]
[18446744073709551616, 1.8446744073709552e+19, 18446744073709551616, 1.8446744073709552e+19]
[7, 7.0, 7, 7.0]
ArgumentError: invalid radix 1
[3, 35, nil]
ArgumentError: expected true or false as exception: 1
ArgumentError: expected true or false as exception: nil
ArgumentError: unknown keyword: :foo
mine [""5""] {exception: false}
6
NoMethodError
");
        }
    }
}
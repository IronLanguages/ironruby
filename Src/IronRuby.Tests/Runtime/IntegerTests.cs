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
        /// Integer.sqrt of a large Integer starts Newton's method from the double's root of its leading bits, raised above
        /// the true root. Expected output is CRuby 4.0's.
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
    }
}
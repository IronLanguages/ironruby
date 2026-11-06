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
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Tests {

    public class DefaultProtocolTester : LibraryInitializer {
        protected override void LoadModules() {
            DefineGlobalModule("Tests", typeof(DefaultProtocolTester), (int)ModuleRestrictions.NoUnderlyingType, null, (module) => {
                    DefineLibraryMethod(module, "to_int_to_str", (int)RubyMethodAttributes.PublicSingleton, new[] {
                        LibraryOverload.Reflect(new Func<RubyModule, Union<int, MutableString>, RubyArray>(ToIntToStr)),
                    });
                    DefineLibraryMethod(module, "to_str_to_int", (int)RubyMethodAttributes.PublicSingleton, new[] {
                        LibraryOverload.Reflect(new Func<RubyModule, Union<MutableString, int>, RubyArray>(ToStrToInt)),
                    });
                }, 
                null, RubyModule.EmptyArray);
        }

        public static RubyArray ToIntToStr(RubyModule/*!*/ self, [DefaultProtocol]Union<int, MutableString> value) {
            return RubyOps.MakeArray2(value.First, value.Second);
        }

        public static RubyArray ToStrToInt(RubyModule/*!*/ self, [DefaultProtocol]Union<MutableString, int> value) {
            return RubyOps.MakeArray2(value.First, value.Second);
        }
    }

    public partial class Tests {
        /// <summary>
        /// A library method's [Keywords] parameter is not one MRI counts: a call with too few or too many positional
        /// arguments - a trailing keyword Hash aside, a Hash passed positionally included - raises "wrong number of
        /// arguments" with MRI's counts (Integer() was "expected 1..3", String.new("a", "b") a TypeError), whether the call
        /// gets a rule, is splatted or would be made without a rule.
        /// </summary>
        public void LibraryKeywordsArity1() {
            TestOutput(@"
def t
  r = yield
  r.inspect
rescue => e
  ""#{e.class}: #{e.message}""
end
puts t { Integer() }, t { Integer(exception: false) }, t { Integer(""1"", 2, 3, exception: false) }, t { Integer(""1"", 2, {}) }
puts t { Float() }, t { Float(1, {exception: false}) }, t { Float(""x"", exception: false) }, t { Integer(""z"", 36, exception: false) }
puts t { String.new(""a"", ""b"") }, t { String.new(""a"", {encoding: ""BINARY""}) }, t { String.new(""a"", encoding: ""BINARY"").encoding }
puts t { ""a"".encode(1, 2, 3) }, t { ""\xff"".encode(""UTF-8"", ""BINARY"", invalid: :replace, undef: :replace) == ""\uFFFD"" }
puts t { [1].pack }, t { [1].pack(""C"", {buffer: +""""}) }, t { [1].pack(""C"", buffer: +""x"") }
puts t { ""ab"".unpack }, t { ""ab"".unpack(""C"", {offset: 1}) }, t { ""ab"".unpack1(""C"", 1) }, t { ""ab"".unpack1(""C"", offset: 1) }
puts t { 2.5.round(1, 2) }, t { 25.round(-1, half: :even) }, t { 2.5.round(half: :even) }
puts t { Regexp.new(""a"", nil, ""n"") }, t { Regexp.new }, t { Regexp.new(""a"", ""i"", timeout: 2).timeout }, t { Regexp.new(/a/, timeout: 2).timeout }
puts t { Regexp.new(""a"", foo: 1) }, t { Marshal.load }, t { Marshal.load(Marshal.dump(+""s""), freeze: true).frozen? }
puts t { Hash.new(1, 2) }, t { Hash.new(1, capacity: 2)[0] }
a = [""1"", 2, 3]
puts t { Integer(*a) }, t { Integer(*[""zz""], **{exception: false}) }, t { Integer(*[""zz"", {exception: false}]) }
def s(*a, **k) = String.new(*a, **k)
puts t { s(""a"", ""b"") }, t { s(""a"", encoding: ""BINARY"").encoding }, t { s(""a"", ""b"") }
def u(x) = ""ab"".unpack1(""C"", x)
puts t { u(1) }, t { u(1) }, t { u(1) }
", @"
ArgumentError: wrong number of arguments (given 0, expected 1..2)
ArgumentError: wrong number of arguments (given 0, expected 1..2)
ArgumentError: wrong number of arguments (given 3, expected 1..2)
ArgumentError: wrong number of arguments (given 3, expected 1..2)
ArgumentError: wrong number of arguments (given 0, expected 1)
ArgumentError: wrong number of arguments (given 2, expected 1)
nil
35
ArgumentError: wrong number of arguments (given 2, expected 0..1)
ArgumentError: wrong number of arguments (given 2, expected 0..1)
#<Encoding:BINARY (ASCII-8BIT)>
ArgumentError: wrong number of arguments (given 3, expected 0..2)
true
ArgumentError: wrong number of arguments (given 0, expected 1)
ArgumentError: wrong number of arguments (given 2, expected 1)
""x\u0001""
ArgumentError: wrong number of arguments (given 0, expected 1)
ArgumentError: wrong number of arguments (given 2, expected 1)
ArgumentError: wrong number of arguments (given 2, expected 1)
98
ArgumentError: wrong number of arguments (given 2, expected 0..1)
20
2
ArgumentError: wrong number of arguments (given 3, expected 1..2)
ArgumentError: wrong number of arguments (given 0, expected 1..2)
2.0
2.0
ArgumentError: unknown keyword: :foo
ArgumentError: wrong number of arguments (given 0, expected 1..2)
true
ArgumentError: wrong number of arguments (given 2, expected 0..1)
1
ArgumentError: wrong number of arguments (given 3, expected 1..2)
nil
TypeError: no implicit conversion of Hash into Integer
ArgumentError: wrong number of arguments (given 2, expected 0..1)
#<Encoding:BINARY (ASCII-8BIT)>
ArgumentError: wrong number of arguments (given 2, expected 0..1)
ArgumentError: wrong number of arguments (given 2, expected 1)
ArgumentError: wrong number of arguments (given 2, expected 1)
ArgumentError: wrong number of arguments (given 2, expected 1)
");
        }

        /// <summary>
        /// The prelude's methods that take keywords count their positional arguments as MRI does: Enumerable#min_by(1, 2)
        /// raised nothing (it made an Enumerator), and IO#set_encoding, IO.pipe and Dir.children took a Hash passed
        /// positionally as their options.
        /// </summary>
        public void PreludeKeywordsArity1() {
            TestOutput(@"
require 'ruby4'
def t
  r = yield
  r.inspect
rescue => e
  ""#{e.class}: #{e.message}""
end
puts t { [3, 1].min_by(1, 2) }, t { [3, 1].max_by(1, 2) { _1 } }, t { [3, 1, 2].min_by(2) { _1 } }, t { [3, 1].each.max_by(1, 2) }
puts t { Dir.children(""."", ""x"") }, t { Dir.children(""."", encoding: ""UTF-8"").class }, t { Dir.each_child(""."", {}) }
puts t { $stdout.set_encoding }, t { $stdout.set_encoding(""UTF-8"", nil, {}) }, t { $stdout.set_encoding(nil, nil, newline: :lf).class }
puts t { IO.pipe(1, 2, 3) }, t { IO.pipe(""UTF-8"", ""UTF-16LE"", invalid: :replace).first.internal_encoding }, t { IO.popen }
puts t { IO.readlines }, t { IO.readlines(1, 2, 3, 4) }
", @"
ArgumentError: wrong number of arguments (given 2, expected 0..1)
ArgumentError: wrong number of arguments (given 2, expected 0..1)
[1, 2]
ArgumentError: wrong number of arguments (given 2, expected 0..1)
ArgumentError: wrong number of arguments (given 2, expected 1)
Array
#<Enumerator: Dir:each_child(""."", {})>
ArgumentError: wrong number of arguments (given 0, expected 1..2)
ArgumentError: wrong number of arguments (given 3, expected 1..2)
IO
ArgumentError: wrong number of arguments (given 3, expected 0..2)
#<Encoding:UTF-16LE>
ArgumentError: wrong number of arguments (given 0, expected 1..2)
ArgumentError: wrong number of arguments (given 0, expected 1..3)
ArgumentError: wrong number of arguments (given 4, expected 1..3)
");
        }

        /// <summary>
        /// A Hash passed as keywords to a library method that takes none - an attribute writer, a Struct member's writer, a
        /// block without keyword parameters - is a positional argument there, which it may keep, and a kept one no longer says
        /// it is keyword arguments: a.push(x: 1) followed by f(a[0]) passed f a positional Hash, not keywords (MRI's C methods
        /// keep no mark on the hash at all). Kernel#send, Proc#call and the like still pass keywords on as keywords.
        /// </summary>
        public void LibraryKeywordsStored1() {
            TestOutput(@"
def f(h = nil, **kw) = [h, kw]
a = []
a.push(x: 1)
a.<<(x: 2)
a.unshift(x: 3)
a.insert(1, x: 4)
a.[]=(4, x: 5)
3.times { a.push(1, x: 6) }
h = {}
h.store(:k, x: 7)
o = Object.new
o.instance_variable_set(:@v, x: 8)
c = Class.new { attr_accessor :v }.new
c.send(:v=, x: 9)
s = Struct.new(:m).new
s.send(:m=, x: 10)
p a.map { |e| f(e) }
p f(h[:k]), f(o.instance_variable_get(:@v)), f(c.v), f(s.m)
p f(a.send(:push, x: 11).last), f(a.method(:push).call(x: 12).last), f(a.public_send(:fetch, 99, x: 13))
def y1 = yield(1, x: 14)
p y1 { |a, b| f(b) }, y1 { |a, b = 2| f(b) }, y1 { |a, *r| f(r[0]) }, f(proc { |h| h }.call(x: 15)), f(->(h) { h }.(x: 16))
def kw(**k) = k
p send(:kw, x: 1), method(:kw).call(x: 2), proc { |**k| k }.call(x: 3), instance_exec(x: 4) { |**k| k }
p Class.new { def initialize(**k) = @k = k; attr_reader :k }.new(x: 5).k, Integer(""z"", exception: false), y1 { |a, b = 2, **k| [b, k] }
", @"
[[{x: 3}, {}], [{x: 4}, {}], [{x: 1}, {}], [{x: 2}, {}], [{x: 5}, {}], [1, {}], [{x: 6}, {}], [1, {}], [{x: 6}, {}], [1, {}], [{x: 6}, {}]]
[{x: 7}, {}]
[{x: 8}, {}]
[{x: 9}, {}]
[{x: 10}, {}]
[{x: 11}, {}]
[{x: 12}, {}]
[{x: 13}, {}]
[{x: 14}, {}]
[{x: 14}, {}]
[{x: 14}, {}]
[{x: 15}, {}]
[{x: 16}, {}]
{x: 1}
{x: 2}
{x: 3}
{x: 4}
{x: 5}
nil
[2, {x: 14}]
");
        }

        /// <summary>
        /// A block with up to four plain parameters has the keyword mark taken off by its dispatcher (BlockDispatcher.Positional),
        /// one with a rest, a block or a post parameter by its prologue: whichever parameter the keyword Hash lands on.
        /// </summary>
        public void BlockKeywordsPositional1() {
            TestOutput(@"
def f(h = nil, **kw) = [h, kw]
def y1 = yield(x: 1)
def y2 = yield(1, x: 2)
def ys(*a) = yield(*a)
r = []
y1 { |a, b, c, d| r << f(a) }
y1 { |a, b, c, d, e| r << f(a) }
y2 { |a, b, c| r << f(b) }
y1 { |a, &b| r << f(a) }
y2 { |a, *b, c| r << f(c) }
y2 { |a, (b, c)| r << f(b) }
ys(1, {x: 3}) { |a, h| r << f(h) }
ys(1, **{x: 4}) { |a, h| r << f(h) }
r << [[1, {x: 6}]].map { |a, h| f(h) }
o = Object.new
o.define_singleton_method(:m) { |a, h| h }
r << f(o.m(1, x: 7))
y1 { |h, **nil| r << h } rescue r << $!.class
p r
", @"
[[{x: 1}, {}], [{x: 1}, {}], [{x: 2}, {}], [{x: 1}, {}], [{x: 2}, {}], [{x: 2}, {}], [{x: 3}, {}], [{x: 4}, {}], [[{x: 6}, {}]], [{x: 7}, {}], ArgumentError]
");
        }

        /// <summary>
        /// Keywords given to Enumerator::Yielder#yield are keywords of the block it yields to, as in MRI.
        /// </summary>
        public void PreludeKeywordsYielder1() {
            TestOutput(@"
require 'ruby4'
r = nil
Enumerator.new { |y| y.yield(x: 1) }.each { |**k| r = k }
p r
Enumerator.new { |y| y.yield(x: 2) }.each { |h = nil, **k| r = [h, k] }
p r
", @"
{x: 1}
[nil, {x: 2}]
");
        }

        /// <summary>
        /// A Symbol#to_proc proc passes keywords on to the method it calls, and a Hash passed positionally stays positional.
        /// </summary>
        public void SymbolProcKeywords1() {
            TestOutput(@"
o = Object.new
def o.m(*a, **k) = [a, k]
pr = :m.to_proc
p pr.call(o, x: 1), pr.call(o, {x: 1}), pr.call(o, 1, x: 2), pr.call(o)
def o.n(h) = h
r = :n.to_proc.call(o, x: 1)
def f(h = nil, **kw) = [h, kw]
p f(r), Hash.ruby2_keywords_hash?(r)
", @"
[[], {x: 1}]
[[{x: 1}], {}]
[[1], {x: 2}]
[[], {}]
[{x: 1}, {}]
false
");
        }

        /// <summary>
        /// Kernel#Integer and #Float as MRI's rb_convert_to_integer and rb_convert_to_float: a base only with a
        /// String or #to_str, #to_int (whose errors and non-Integer answers are ignored) before #to_str before
        /// #to_i, private conversion methods called, exception: false answering nil for any failure but a bad
        /// keyword or an ASCII-incompatible String, and the Float #to_f answers handed back as they are.
        /// </summary>
        public void Kernel_IntegerFloat1() {
            TestOutput(@"
def t
  r = yield
  r.inspect
rescue => e
  ""#{e.class}: #{e.message}""
end
ti = Object.new; def ti.to_int = 7
ts = Object.new; def ts.to_str = ""0x1f""
tii = Object.new; def tii.to_i = 9
tis = Object.new; def tis.to_int = ""x""; def tis.to_str = ""12""
tir = Object.new; def tir.to_int = raise(""boom""); def tir.to_i = 3
tbi = Object.new; def tbi.to_i = ""s""
tbs = Object.new; def tbs.to_str = 5
tpi = Object.new; class << tpi; private def to_int = 4; end
p [Integer(""0x1A""), Integer("" 12 "", 8), Integer(""z"", 36), Integer(12, 0), Integer(1.9), Integer(-1.9), Integer(2**70)]
p [Integer(ti), Integer(ts), Integer(ts, 16), Integer(tii), Integer(tis), Integer(tir), Integer(tpi)]
p [Integer(""x"", exception: false), Integer(nil, exception: false), Integer((0.0 / 0), exception: false), Integer(tbi, exception: false), Integer(Object.new, 16, exception: false)]
puts t { Integer(nil) }, t { Integer(true) }, t { Integer(:a) }, t { Integer((1.0 / 0)) }, t { Integer(tbi) }, t { Integer(tbs) }
puts t { Integer(12, 8) }, t { Integer(nil, 8) }, t { Integer(""1__0"") }, t { Integer(""10"", 37) }
puts t { Integer(""1"", exception: nil) }, t { Integer(""1"", foo: 1) }, t { Integer(""12"".encode(""UTF-16LE""), exception: false) }
tf = Object.new; def tf.to_f = 2.5
tbf = Object.new; def tbf.to_f = ""x""
trf = Object.new; def trf.to_f = raise(""boom"")
n = 0.0 / 0; tn = Object.new; tn.define_singleton_method(:to_f) { n }
p [Float(""1_000.5""), Float(3), Float(tf), Float(""x"", exception: false), Float(""1\0"", exception: false), Float(trf, exception: false), Float(nil, exception: false)]
p [Float(n).equal?(n), Float(tn).equal?(n)]
puts t { Float(nil) }, t { Float(false) }, t { Float(:s) }, t { Float(tbf) }, t { Float(""1\0"") }, t { Float("""") }, t { Float(""1"", exception: 1) }
p [Kernel.Integer(""0b11""), Kernel.Float(""2""), method(:Integer).arity, method(:Float).arity]
", @"
[26, 10, 35, 12, 1, -1, 1180591620717411303424]
[7, 31, 31, 9, 12, 3, 4]
[nil, nil, nil, nil, nil]
TypeError: can't convert nil into Integer
TypeError: can't convert true into Integer
TypeError: can't convert Symbol into Integer
FloatDomainError: Infinity
TypeError: can't convert Object to Integer (Object#to_i gives String)
TypeError: can't convert Object to String (Object#to_str gives Integer)
ArgumentError: base specified for non string value
ArgumentError: base specified for non string value
ArgumentError: invalid value for Integer(): ""1__0""
ArgumentError: invalid radix 37
ArgumentError: expected true or false as exception: nil
ArgumentError: unknown keyword: :foo
Encoding::CompatibilityError: ASCII incompatible encoding: UTF-16LE
[1000.5, 3.0, 2.5, nil, nil, nil, nil]
[true, true]
TypeError: can't convert nil into Float
TypeError: can't convert false into Float
TypeError: can't convert Symbol into Float
TypeError: can't convert Object to Float (Object#to_f gives String)
ArgumentError: string for Float contains null byte
ArgumentError: invalid value for Float(): """"
ArgumentError: expected true or false as exception: 1
[3, 2.0, -2, -2]
");
        }

        public void ToIntegerConversion1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def to_i
    1000000000000
  end
end

class D
  def to_int
    1
  end
end

class E
  def respond_to? name
    puts name
    false
  end
end

puts Integer(123)
puts Integer(1230000000000000)
puts Integer(C.new)
puts Integer(D.new)
puts Integer(E.new) rescue puts $!
");
            }, @"
123
1230000000000000
1000000000000
1
to_int
to_str
to_i
can't convert E into Integer
");
        }

        private const string StrIntDeclarations = @"
require $protocol_tester

class A
  def to_str
    'A'
  end
end

class B
  def to_int
    1
  end
end

class C
  def to_str
    'C'
  end

  def to_int
    2
  end
end

class E
  def respond_to? name
    puts name
    false
  end
end
";
        
        public void ToIntToStrConversion1() {
            Context.DefineGlobalVariable("protocol_tester", MutableString.CreateAscii(typeof(DefaultProtocolTester).AssemblyQualifiedName));

            Engine.Execute(StrIntDeclarations);

            AssertOutput(delegate() {
                CompilerTest(@"
p Tests.to_int_to_str(123)
p Tests.to_int_to_str('xxx')
p Tests.to_int_to_str(A.new)
p Tests.to_int_to_str(B.new)
p Tests.to_int_to_str(C.new)
p Tests.to_int_to_str(E.new) rescue puts $!
");
            }, @"
[123, nil]
[0, ""xxx""]
[0, ""A""]
[1, nil]
[2, nil]
to_int
to_str
no implicit conversion of E into String
");

            AssertOutput(delegate() {
                CompilerTest(@"
p Tests.to_str_to_int(123)
p Tests.to_str_to_int('xxx')
p Tests.to_str_to_int(A.new)
p Tests.to_str_to_int(B.new)
p Tests.to_str_to_int(C.new)
p Tests.to_str_to_int(E.new) rescue puts $!
");
            }, @"
[nil, 123]
[""xxx"", 0]
[""A"", 0]
[nil, 1]
[""C"", 0]
to_str
to_int
no implicit conversion of E into Integer
");
        }

        public void ConvertToFixnum1() {
            AssertOutput(() => CompilerTest(@"
class Float
  def to_int
    2
  end
end

p Array.new(4.3004, 1)                     # implicit conversion, to_int not called 
[][8e19] rescue p $!                       # overflow -> RangeError

include System
[Byte, SByte, Int16, UInt16, UInt32, UInt64, Single, Decimal, Int64].each_with_index do |n,i|
  p Array.new(n.new(i), i)
end
"), @"
[1, 1, 1, 1]
#<RangeError: float * out of range of integer>
[]
[1]
[2, 2]
[3, 3, 3]
[4, 4, 4, 4]
[5, 5, 5, 5, 5]
[6, 6, 6, 6, 6, 6]
[7, 7, 7, 7, 7, 7, 7]
[8, 8, 8, 8, 8, 8, 8, 8]
", OutputFlags.Match);
        }

        /// <summary>
        /// Kernel#Array first tries to_ary and then to_a. We need to invalidate the cache when to_ary is added.
        /// </summary>
        public void ProtocolCaching1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def to_a
    puts 'to_a'
    [1]
  end
end

obj = C.new
Array(obj)

class C
  def to_ary
    puts 'to_ary'
    [2]
  end
end

Array(obj)
");
            }, @"
to_a
to_ary
");
        }

        /// <summary>
        /// We need to invalidate the cache when respond_to? is added.
        /// </summary>
        public void ProtocolCaching2() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def to_f
    puts 'C:to_f'
    1.0
  end
end

obj = C.new
Float(obj)

class C
  def respond_to? name
    puts name
    false
  end
end

Float(obj) rescue puts 'error'
");
            }, @"
C:to_f
to_f
error
");

        }

        /// <summary>
        /// We need to invalidate the cache when to_xxx is removed.
        /// </summary>
        public void ProtocolCaching3() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def to_f
    puts 'C:to_f'
    1.0
  end
end

obj = C.new
Float(obj)

class C
  remove_method(:to_f)
end

Float(obj) rescue puts 'error'
");
            }, @"
C:to_f
error
");

        }

        /// <summary>
        /// Caching of to_s conversion.
        /// </summary>
        public void ProtocolCaching4() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C; end
c = C.new

puts ""(#{c})""

module Kernel
  remove_method :to_s
  
  def method_missing name
    'mm:to_s'
  end
end

puts ""(#{c})""

class C; def to_s; 'C:to_s'; end; end

puts ""(#{c})""

class Array; def to_s; 'Array:to_s'; end; end
class C; def to_s; [1,2]; end; end

puts ""(#{c})""
");
            }, @"
(#<C:0x*>)
(mm:to_s)
(C:to_s)
(#<C:0x*>)
", OutputFlags.Match);

        }

        /// <summary>
        /// CompositeConversionAction.Make hands out one action per runtime and conversion, and MakeShared - what serialized
        /// code calls - the shared one, made of shared component conversions (it used to throw NullReferenceException).
        /// </summary>
        public void CompositeConversionActions1() {
            foreach (CompositeConversion conversion in Enum.GetValues(typeof(CompositeConversion))) {
                var shared = CompositeConversionAction.MakeShared(conversion);
                Assert(shared != null && shared.Context == null);
                Assert(ReferenceEquals(shared, CompositeConversionAction.MakeShared(conversion)));

                var action = CompositeConversionAction.Make(Context, conversion);
                Assert(action != null && ReferenceEquals(action.Context, Context));
                Assert(ReferenceEquals(action, CompositeConversionAction.Make(Context, conversion)));
                Assert(ReferenceEquals(action, Context.MetaBinderFactory.CompositeConversion(conversion)));
                Assert(ReferenceEquals(shared, CompositeConversionAction.MakeShared(conversion)));
                Assert(!ReferenceEquals(action, shared));
            }
        }

        /// <summary>
        /// Error and warning wording checked against CRuby 4.0.
        /// </summary>
        public void ErrorMessages_Ruby40() {
            AssertOutput(delegate() {
                Engine.CreateScriptSourceFromString(@"
def t
  r = yield
  puts ""=> #{r.inspect}""
rescue Exception => e
  puts ""#{e.class}: #{e.message}""
end
class BadInt; def to_int; 'x'; end; end
class BadIO; def to_io; 1; end; end
t { [1, 2][BadInt.new] }
t { 'a'.split(1) }
t { 'a'.scan(1) }
t { IO.select([BadIO.new]) }
t { IO.select([1]) }
t { sleep('a') }
t { sleep(-1) }
t { 'abc'.sub }
t { 'abc'.sub { } }
t { 'abc'.dup.sub!('a') }
t { 'abc'.dup.sub!('a', 'b') { } }
t { 'abc'.sub('a', 'b', 'c') }
t { 1.round(1, 2) }
t { ""\xff"".force_encoding(""BINARY"") + ""é"" }
t { /é/ =~ ""\xff"".force_encoding(""BINARY"") }
def f1(a, k:); end
def f2(a, b = 1, k:, j:); end
t { f1(1, 2) }
t { f1(k: 1) }
t { f2(k: 1, j: 2) }
t { Module.new.class_eval { raise 'x' } rescue $!.backtrace.grep(/Module#/) }
t { Module.new.class_exec { raise 'x' } rescue $!.backtrace.grep(/Module#/) }
$stderr = $stdout
X = 1
X = 2
$VERBOSE = true
t { Float('1e400') }
t { '-1e-400'.to_f }
", "ErrorMessages.rb", Microsoft.Scripting.SourceCodeKind.File).Execute();
            }, @"
TypeError: can't convert BadInt to Integer (BadInt#to_int gives String)
TypeError: wrong argument type Integer (expected Regexp)
TypeError: wrong argument type Integer (expected Regexp)
TypeError: can't convert BadIO to IO (BadIO#to_io gives Integer)
TypeError: no implicit conversion of Integer into IO
TypeError: can't convert String into time interval
ArgumentError: time interval must not be negative
ArgumentError: wrong number of arguments (given 0, expected 2)
ArgumentError: wrong number of arguments (given 0, expected 1..2)
ArgumentError: wrong number of arguments (given 1, expected 2)
=> ""bbc""
ArgumentError: wrong number of arguments (given 3, expected 2)
ArgumentError: wrong number of arguments (given 2, expected 0..1)
Encoding::CompatibilityError: incompatible character encodings: BINARY (ASCII-8BIT) and UTF-8
Encoding::CompatibilityError: incompatible encoding regexp match (UTF-8 regexp with BINARY (ASCII-8BIT) string)
ArgumentError: wrong number of arguments (given 2, expected 1; required keyword: k)
ArgumentError: wrong number of arguments (given 0, expected 1; required keyword: k)
ArgumentError: wrong number of arguments (given 0, expected 1..2; required keywords: k, j)
=> [""ErrorMessages.rb:30:in 'Module#class_eval'""]
=> [""ErrorMessages.rb:31:in 'Module#class_exec'""]
ErrorMessages.rb:*: warning: already initialized constant X
ErrorMessages.rb:33: warning: previous definition of X was here
<internal:kernel>:196: warning: Float 1e400 out of range
=> Infinity
ErrorMessages.rb:37: warning: Float -1e-400 out of range
=> -0.0
", OutputFlags.Match);
        }
    }
}

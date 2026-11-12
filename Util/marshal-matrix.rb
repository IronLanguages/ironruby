#!/usr/bin/env ruby
# Differential matrix for Marshal.
#
#   ruby        Util/marshal-matrix.rb > /tmp/m-cruby.txt
#   ./ir.sh     Util/marshal-matrix.rb > /tmp/m-ir.txt
#   diff /tmp/m-cruby.txt /tmp/m-ir.txt
#
# Each line is:  <label> TAB <dump bytes, inspected> TAB <round-trip result>
# so a single diff shows both a wire-format difference and a load difference.

def hex(s)
  s.bytes.map { |b| "%02x" % b }.join
end

def show(label, &blk)
  obj = begin
    blk.call
  rescue Exception => e
    puts "#{label}\tBUILD-ERR #{e.class}"
    return
  end
  d = begin
    Marshal.dump(obj)
  rescue Exception => e
    puts "#{label}\tDUMP-ERR #{e.class}: #{e.message}"
    return
  end
  rt = begin
    v = Marshal.load(d)
    begin
      "#{v.inspect} (#{v.class}#{v.respond_to?(:encoding) ? " " + v.encoding.to_s : ""})"
    rescue Exception => e
      "INSPECT-ERR #{e.class}"
    end
  rescue Exception => e
    "LOAD-ERR #{e.class}: #{e.message}"
  end
  puts "#{label}\t#{hex(d)}\t#{rt}"
end

# ---------------------------------------------------------------- header
show("header") { 42 }
puts "MAJOR\t#{Marshal::MAJOR_VERSION}"
puts "MINOR\t#{Marshal::MINOR_VERSION}"

# ---------------------------------------------------------------- immediates
show("nil") { nil }
show("true") { true }
show("false") { false }

# ---------------------------------------------------------------- fixnum
[0, 1, -1, 2, 122, 123, 124, -123, -124, -125, 255, 256, 257, -255, -256, -257,
 0xffff, 0x10000, -0xffff, -0x10000, 0xffffff, 0x1000000, -0xffffff, -0x1000000,
 0x3fffffff, 0x40000000, -0x40000000, -0x40000001,
 2**30 - 1, 2**30, 2**31 - 1, 2**31, 2**62, -(2**62)].each do |i|
  show("fixnum #{i}") { i }
end

# ---------------------------------------------------------------- bignum
[2**64, -(2**64), 2**64 + 1, 2**100, -(2**100), 2**128 - 1,
 0x1_0000_0000_0000_0000, 12345678901234567890,
 2**32, 2**33, 2**48, 2**80 + 12345].each do |i|
  show("bignum #{i}") { i }
end

# ---------------------------------------------------------------- float
[0.0, -0.0, 1.0, -1.0, 1.1, 0.5, 1.0 / 3, 1e10, 1e100, 1e-100, 1e300,
 Float::INFINITY, -Float::INFINITY, Float::NAN, Float::MAX, Float::MIN,
 Float::EPSILON, 0.1 + 0.2, 123456789.123456789, 2.0**64, 5e-324].each_with_index do |f, i|
  show("float #{i}") { f }
end

# ---------------------------------------------------------------- symbol
show("sym plain") { :abc }
show("sym op") { :"+" }
show("sym empty") { :"" }
show("sym utf8") { :"héllo" }
show("sym space") { :"a b" }
show("sym dup in array") { [:abc, :abc, :def, :abc] }
show("sym binary") { "\xff".dup.force_encoding("BINARY").to_sym }

# ---------------------------------------------------------------- string
show("str empty") { "" }
show("str ascii") { "abc" }
show("str utf8") { "héllo" }
show("str binary") { "abc".dup.force_encoding("BINARY") }
show("str binary hi") { "\xff\xfe".dup.force_encoding("BINARY") }
show("str usascii") { "abc".dup.force_encoding("US-ASCII") }
show("str sjis") { "abc".dup.force_encoding("Shift_JIS") }
show("str eucjp") { "abc".dup.force_encoding("EUC-JP") }
show("str utf16") { "ab".dup.force_encoding("UTF-16LE") }
show("str iso8859") { "abc".dup.force_encoding("ISO-8859-1") }
show("str with nul") { "a\0b" }
show("str long") { "x" * 300 }
show("str ivar") { s = "abc".dup; s.instance_variable_set(:@foo, 1); s }
show("str frozen") { "abc".freeze }
show("str dup") { s = "abc".dup; [s, s] }

class StrSub < String; end
show("str subclass") { StrSub.new("abc") }
show("str subclass ivar") { s = StrSub.new("abc"); s.instance_variable_set(:@a, 2); s }

# ---------------------------------------------------------------- array / hash
show("array empty") { [] }
show("array simple") { [1, 2, 3] }
show("array nested") { [1, [2, [3]]] }
show("array mixed") { [nil, true, 1.5, "s", :sym, [1]] }
show("array cyclic") { a = []; a << a; a }
show("hash empty") { {} }
show("hash simple") { { 1 => 2, "a" => "b" } }
show("hash default") { Hash.new(0) }
show("hash default 2") { h = Hash.new(5); h[1] = 2; h }
show("hash cyclic") { h = {}; h[:self] = h; h }
show("hash compare_by_identity") { h = {}.compare_by_identity; h["a"] = 1; h }
show("hash ivar") { h = {}; h.instance_variable_set(:@x, 1); h }

class ArrSub < Array; end
class HashSub < Hash; end
show("array subclass") { ArrSub.new([1, 2]) }
show("hash subclass") { h = HashSub.new; h[1] = 2; h }

# ---------------------------------------------------------------- regexp
show("regexp plain") { /abc/ }
show("regexp i") { /abc/i }
show("regexp m") { /abc/m }
show("regexp x") { /abc/x }
show("regexp imx") { /abc/imx }
show("regexp utf8") { /hé/ }
show("regexp binary") { Regexp.new("\xff".dup.force_encoding("BINARY")) }
class ReSub < Regexp; end
show("regexp subclass") { ReSub.new("abc") }

# ---------------------------------------------------------------- range
show("range int") { 1..2 }
show("range excl") { 1...2 }
show("range str") { "a".."z" }
show("range beginless") { ..5 }
show("range endless") { 1.. }
class RangeSub < Range; end
show("range subclass") { RangeSub.new(1, 2) }

# ---------------------------------------------------------------- struct
S1 = Struct.new(:a, :b)
show("struct") { S1.new(1, 2) }
show("struct nil") { S1.new }
show("struct ivar") { s = S1.new(1, 2); s.instance_variable_set(:@z, 3); s }
S2 = Struct.new(:a) do
  def initialize(*) super; end
end
show("struct block") { S2.new(7) }
begin
  D1 = Data.define(:x, :y)
  show("data") { D1.new(x: 1, y: 2) }
rescue NameError
  puts "data\tNO-DATA"
end

# ---------------------------------------------------------------- objects
class Plain; end
class WithIvars
  def initialize; @a = 1; @b = "s"; end
end
show("object plain") { Plain.new }
show("object ivars") { WithIvars.new }
show("object anon") { Class.new.new }

module Ext1; end
show("object extended") { o = Plain.new; o.extend(Ext1); o }
show("object singleton method") { o = Plain.new; def o.foo; end; o }

# ---------------------------------------------------------------- _dump / _load
class UserDump
  def initialize(s) @s = s end
  attr_reader :s
  def _dump(depth) @s end
  def self._load(str) new(str) end
  def inspect; "#<UserDump #{@s.inspect}>" end
end
show("_dump") { UserDump.new("payload") }
show("_dump ivar") { u = UserDump.new("p"); u.instance_variable_set(:@extra, 9); u }

class UserDumpIvar
  def _dump(depth) s = "d".dup; s.instance_variable_set(:@iv, 42); s end
  def self._load(str) str end
end
show("_dump returns ivar string") { UserDumpIvar.new }

class BadDump
  def _dump(depth) 5 end
end
show("_dump non-string") { BadDump.new }

# ---------------------------------------------------------------- marshal_dump
class UserMarshal
  def initialize(v = 1) @v = v end
  def marshal_dump; @v end
  def marshal_load(v) @v = v end
  def inspect; "#<UserMarshal #{@v.inspect}>" end
end
show("marshal_dump") { UserMarshal.new }
show("marshal_dump array") { UserMarshal.new([1, "s", :y]) }
show("marshal_dump extended") { u = UserMarshal.new; u.extend(Ext1); u }
show("marshal_dump ivar ignored") { u = UserMarshal.new; u.instance_variable_set(:@other, 3); u }

# ---------------------------------------------------------------- class / module
show("class ref") { String }
show("module ref") { Comparable }
show("nested class ref") { Struct::Group rescue Process }
show("class in array") { [String, String] }
show("anon class") { Class.new }
show("anon module") { Module.new }

# ---------------------------------------------------------------- exceptions
show("exception") { RuntimeError.new("boom") }
show("standarderror") { StandardError.new("x") }
show("exception with backtrace") do
  begin
    raise "bt"
  rescue => e
    e
  end
end

# ---------------------------------------------------------------- numerics
show("rational") { Rational(1, 3) }
show("complex") { Complex(1, 2) }
show("rational big") { Rational(2**70, 3) }

# ---------------------------------------------------------------- time
show("time utc") { Time.at(0).utc }
show("time local fixed") { Time.at(123456789).utc }

# ---------------------------------------------------------------- links
show("shared string") { s = "x".dup; [s, s, s] }
show("shared array") { a = [1]; [a, a] }
show("shared hash key") { k = "k".dup; { k => k } }
show("shared object") { o = WithIvars.new; [o, o] }
show("shared bignum") { b = 2**70; [b, b] }
show("shared float") { f = 1.5; [f, f] }
show("shared symbol") { [:s, :s] }

# ---------------------------------------------------------------- errors
def err(label)
  yield
  puts "#{label}\tNO-ERROR"
rescue Exception => e
  puts "#{label}\t#{e.class}: #{e.message}"
end

err("dump proc") { Marshal.dump(proc {}) }
err("dump lambda") { Marshal.dump(lambda {}) }
err("dump method") { Marshal.dump(1.method(:+)) }
err("dump io") { Marshal.dump(STDOUT) }
err("dump mutexn") { Marshal.dump(Thread::Mutex.new) }
err("dump hash default proc") { Marshal.dump(Hash.new { |h, k| k }) }
err("dump anon class") { Marshal.dump(Class.new) }
err("dump singleton") { o = Object.new; def o.x; end; Marshal.dump(o) }
err("dump depth") { Marshal.dump([[[[1]]]], 3) }
err("dump depth ok") { Marshal.dump([[[[1]]]], 6) }

err("load truncated") { Marshal.load("\x04\b") }
err("load empty") { Marshal.load("") }
err("load garbage") { Marshal.load("\x04\bX") }
err("load bad version") { Marshal.load("\x05\b0") }
err("load bad minor") { Marshal.load("\x04\x63" + "0") }
err("load undefined class") { Marshal.load("\x04\bo:\x0FNoSuchKlass\x00") }
err("load nil arg") { Marshal.load(nil) }
err("load int arg") { Marshal.load(5) }
err("load symbol link bad") { Marshal.load("\x04\b;\x00") }
err("load object link bad") { Marshal.load("\x04\b@\x05") }

# ---------------------------------------------------------------- load options
err("load freeze") { p Marshal.load(Marshal.dump("abc"), freeze: true).frozen? }
err("load freeze array") { p Marshal.load(Marshal.dump([1, "a"]), freeze: true).frozen? }
err("load proc") do
  seen = []
  Marshal.load(Marshal.dump([1, "a", :b]), proc { |x| seen << x.inspect })
  p seen
end
err("load proc returns") do
  p Marshal.load(Marshal.dump([1, 2]), proc { |x| x })
end

# ---------------------------------------------------------------- round trips
def rt(label)
  o = yield
  v = Marshal.load(Marshal.dump(o))
  puts "#{label}\tRT\t#{v.inspect}\t#{v.class}\t#{o == v}"
rescue Exception => e
  puts "#{label}\tRT-ERR\t#{e.class}: #{e.message}"
end

rt("rt utf8") { "héllo" }
rt("rt encoding preserved") { "abc".dup.force_encoding("EUC-JP") }
rt("rt sym utf8") { :"héllo" }
rt("rt regexp utf8") { /hé/ }
rt("rt struct") { S1.new(1, 2) }
rt("rt range") { (1..5) }
rt("rt exception") { RuntimeError.new("z") }
rt("rt nested hash") { { a: { b: [1, 2, { c: 3 }] } } }
rt("rt big") { 2**200 }
rt("rt float") { 0.1 + 0.2 }
rt("rt class") { Hash }
rt("rt rational") { Rational(3, 4) }
rt("rt complex") { Complex(3, 4) }
rt("rt time") { Time.at(1234).utc }

# encoding of loaded strings
e = Marshal.load(Marshal.dump("abc"))
puts "enc ascii\t#{e.encoding}"
e = Marshal.load(Marshal.dump("héllo"))
puts "enc utf8\t#{e.encoding}"
e = Marshal.load(Marshal.dump("abc".dup.force_encoding("BINARY")))
puts "enc binary\t#{e.encoding}"
e = Marshal.load(Marshal.dump("abc".dup.force_encoding("EUC-JP")))
puts "enc eucjp\t#{e.encoding}"
e = Marshal.load(Marshal.dump(:"héllo"))
puts "enc sym\t#{e.encoding}"

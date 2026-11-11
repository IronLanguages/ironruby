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
using Microsoft.Scripting;

namespace IronRuby.Tests {
    public partial class Tests {
        
        public void Scenario_RubyHashes1A() {
            AssertOutput(delegate() {
                CompilerTest(@"
x = Hash.[](1, 'a', 2, 'b')
print x[1], x[2]
");
            }, @"ab");
        }

        public void Scenario_RubyHashes1B() {
            TestOutput(@"
puts Hash.[]()
", @"
{}
");
        }

        public void Scenario_RubyHashes1C() {
            AssertExceptionThrown<ArgumentException>(delegate() {
                CompilerTest(@"
puts Hash.[](1)
");
            });
        }

        public void Scenario_RubyHashes2() {
            AssertOutput(delegate() {
                CompilerTest(@"
x = { 1 => 'a' }
puts x[1]
");
            }, @"
a
");
        }

        public void Scenario_RubyHashes3() {
            TestOutput(@"
def foo(a,b,c)
  puts a, b, c
end

foo Hash.[](1 => :a), { 2 => :b }, 3 => :c
", @"
{1 => :a}
{2 => :b}
{3 => :c}
");
        }

        public void Scenario_RubyHashes4() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
    def []=(a,b)
      print a[1], a[2], b
    end
end

C.new[{1 => 'a', 2 => 'b'}] = 'c'    # Ruby 3.4+: without braces they are keywords, not allowed here
");
            }, @"abc");
        }

        public void Scenario_RubyHashes5() {
            TestOutput(@"
h = { a: 1, b: 2 }
puts h[:a], h[:b]

def foo *args
  p args[0][:a], args[0][:b] 
end

foo a: 1, b: 2
", @"
1
2
1
2
"
);
            // a list of expressions no longer supported:
            AssertExceptionThrown<SyntaxErrorException>(() => Engine.Execute(@"h = {1,2,3,4}"));
        }

        /// <summary>
        /// Hash#keys and #values: insertion order, deleted entries skipped, the nil key given back
        /// as nil, a fresh plain Array each time.
        /// </summary>
        public void Hash_KeysValues1() {
            TestOutput(@"
h = { nil => 1, 2 => nil, 'a' => :b }
p h.keys, h.values
h = {}
(1..10).each { |x| h[x] = x * 2 }
h.delete(3); h.delete(1); h[11] = 0
p h.keys, h.values
k = h.keys; k << :x
p h.size, {}.keys, {}.values
p({ 1 => nil }.invert.keys)
c = Class.new(Hash)
p c[1, 2].keys.class, c[1, 2].values.class
", @"
[nil, 2, ""a""]
[1, nil, :b]
[2, 4, 5, 6, 7, 8, 9, 10, 11]
[4, 8, 10, 12, 14, 16, 18, 20, 0]
9
[]
[]
[nil]
Array
Array
");
        }

        /// <summary>
        /// Equality comparer doesn't call 'eql?' if the values are reference-equal.
        /// </summary>
        public void EqualityComparer1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def eql? other
    puts 'eql?'
  end
end

c = C.new

hash = { c => 1 }
p hash[c]
");
            }, @"1");
        }
    
        /// <summary>
        /// A block may delete from the hash being iterated (the entry is then not visited) and
        /// replace values, but may not add a key: MRI raises, rather than rebuild the table under
        /// the iterator. Deleting used to surface a KeyNotFoundException.
        /// </summary>
        public void HashIteration_Mutation1() {
            TestOutput(@"
h = {a: 1, b: 2, c: 3}
seen = []
h.each { |k, v| seen << k; h.delete(:b) }
p seen, h.keys
h.each_key { |k| h.delete(:c) }
p h.keys
begin
  h.each { |k, v| h[:z] = 1 }
rescue RuntimeError => e
  puts e.message
end
h.each { |k, v| h[:a] = 10 }
p h.values
p h.delete_if { |k, v| h.delete(:a); false }.size
", @"
[:a, :c]
[:a, :c]
[:a]
can't add a new key into hash during iteration
[10]
0
");
        }

        /// <summary>
        /// A JIT-specialized `h[k] = v' of a new key while h is being iterated deopts, and the generic code raises
        /// the RuntimeError a new key during iteration is (JitRuntime.HashStoreInt/HashStoreObj).
        /// </summary>
        public void HashIteration_JitStore1() {
            TestOutput(@"
def jit_store_int(h, k) = (h[k] = 1)
def jit_store_obj(h, k) = (h[k] = 1)
h = {}
200.times { |i| jit_store_int(h, i); jit_store_obj(h, ""k#{i % 7}"") }
g = {1 => 1}
[[:jit_store_int, 99], [:jit_store_obj, 'new']].each do |m, k|
  begin
    g.each { |_, _| send(m, g, k) }
    p :no_error
  rescue RuntimeError => e
    puts e.message
  end
  g.each { |_, _| send(m, g, 1) }
  p g.to_a
end
", @"
can't add a new key into hash during iteration
[[1, 1]]
can't add a new key into hash during iteration
[[1, 1]]
");
        }

        /// <summary>
        /// A compare_by_identity hash finds an Integer key the JIT looks up unboxed with the hash code the
        /// comparer gives the boxed one - which for -2**31 is not the value itself.
        /// </summary>
        public void HashIdentity_JitFixnumKey1() {
            TestOutput(@"
def jit_get(h, k) = h[k]
def jit_key(h, k) = h.key?(k)
ih = {}.compare_by_identity
ih[-2**31] = :min
ih[2**31 - 1] = :max
ih[5] = :five
200.times { jit_get(ih, 5); jit_key(ih, 5) }
p [jit_get(ih, -2**31), jit_get(ih, 2**31 - 1), jit_get(ih, 5), jit_key(ih, -2**31), jit_get(ih, 6)]
", @"
[:min, :max, :five, true, nil]
");
        }

        /// <summary>
        /// Hash#fetch is the library method on master (no longer the prelude's): the JIT still specializes a
        /// method that calls it, and a miss still deopts to the generic call, which raises the KeyError.
        /// </summary>
        public void HashFetch_Jit1() {
            int specialized = IronRuby.Runtime.Jit.JitRuntime.Specialized;
            TestOutput(@"
def jit_fetch1(h, k) = h.fetch(k)
def jit_fetch2(h, k) = h.fetch(k, :d)
h = {1 => :one, :a => :sym, 'b' => :str}
200.times { |i| jit_fetch1(h, 1); jit_fetch2(h, i) }
p [jit_fetch1(h, :a), jit_fetch1(h, 'b'), jit_fetch2(h, 2), jit_fetch2(h, :a)]
", @"
[:sym, :str, :d, :sym]
");
            int specializedHere = IronRuby.Runtime.Jit.JitRuntime.Specialized - specialized;
            Assert(specializedHere >= 2, System.String.Format("methods specialized: {0} (expected 2)", specializedHere));
        }

        /// <summary>
        /// A Hash is iterated in place, entry by entry, rather than over a copy of its entries taken
        /// up front (which made {...}.first(n) and an early break O(size)): a key the block deletes
        /// before its turn is skipped, a value it replaces is seen replaced, #clear ends the walk, and
        /// #shift of an entry already visited changes nothing - as in MRI.
        /// </summary>
        public void HashIteration_InPlace1() {
            TestOutput(@"
h = {a: 1, b: 2, c: 3, d: 4}
seen = []
h.each { |k, v| seen << [k, v]; h.delete(:c) if k == :a; h[:d] = 40 if k == :b }
p seen
seen = []
h.each { |k, v| seen << k; h.clear }
p seen, h.size
g = {a: 1, b: 2, c: 3}
seen = []
g.each { |k, v| seen << k; g.shift if k == :a }
p seen, g.keys
g = {nil => 1, x: 2, y: 3}
p g.each_key.to_a, g.each_value.map { |v| v * 2 }
seen = []
g.each_value { |v| seen << v; g.delete(:y) }
p seen
p g.each { |k, v| break k }, g.first(2), g.first
big = {}
1000.times { |i| big[i] = i }
500.times { |i| big.delete(i * 2) }
p big.first(3), big.each_key.first(2)
", @"
[[:a, 1], [:b, 2], [:d, 40]]
[:a]
0
[:a, :b, :c]
[:b, :c]
[nil, :x, :y]
[2, 4, 6]
[1, 2]
nil
[[nil, 1], [:x, 2]]
[nil, 1]
[[1, 1], [3, 3], [5, 5]]
[1, 3]
");
        }

        /// <summary>
        /// Hash#inspect and #to_s in Ruby 3.4's format: a Symbol key as a label, quoted when it would not
        /// read back as one (MRI's symbol_key_needs_quote), any other key with a spaced rocket; a recursive
        /// Hash as {...}; US-ASCII until an element brings another encoding.
        /// </summary>
        public void Hash_Inspect1() {
            TestOutput(@"
p({1 => :a, foo: 1, ""a b"": 2, :""a="" => 3, :+ => 4, :@iv => 5, :$g => 6, :! => 7, :a? => 8, :b! => 9, :[] => 10, :A => 11, :""9x"" => 12, :"""" => 13, nil => nil, [1] => {x: 1}})
r = {}; r[:self] = r; r[r] = 1
puts r.to_s
k = Object.new; def k.inspect = ""K!""
p({k => k}, {}, Class.new(Hash).new.update(x: 1))
p [{a: 1}.inspect.encoding, {""a"" => 1}.to_s.encoding]
p Hash.instance_method(:to_s) == Hash.instance_method(:inspect)
", @"
{1 => :a, foo: 1, ""a b"": 2, ""a="": 3, ""+"": 4, ""@iv"": 5, ""$g"": 6, ""!"": 7, a?: 8, b!: 9, ""[]"": 10, A: 11, ""9x"": 12, """": 13, nil => nil, [1] => {x: 1}}
{self: {...}, {...} => 1}
{K! => K!}
{}
{x: 1}
[#<Encoding:US-ASCII>, #<Encoding:UTF-8>]
true
");
        }

        /// <summary>
        /// Ruby 4.0's Hash#merge/#merge!/#update (any number of hashes, #to_hash, frozen check first), #delete_if,
        /// #keep_if, #reject!, #select!/#filter! and #reject (an Enumerator without a block, a plain Hash without the
        /// default from #reject), #filter, and #shift answering nil for an empty hash without asking the default.
        /// </summary>
        public void Hash_MergeRejectShift1() {
            TestOutput(@"
def t
  r = yield
  r.inspect
rescue => e
  e.is_a?(FrozenError) ? ""FrozenError"" : ""#{e.class}: #{e.message}""
end
h = {a: 1, b: 2}
puts t { h.merge }, t { h.merge({c: 3}, {a: 9}) }, t { h.merge({a: 5}, {a: 7}) { |k, o, n| o + n } }
puts t { h.dup.merge! }, t { h.dup.update({c: 3}, {d: 4}) }, t { h.dup.update({a: 5}) { |k, o, n| [k, o, n] } }
puts t { {a: 1}.freeze.merge!({}) }, t { {a: 1}.freeze.update(b: 1) }, t { {a: 1}.merge(1) }
o = Object.new; def o.to_hash = {z: 26}
puts t { {a: 1}.merge(o) }
d = Hash.new(5); d[:a] = 1; m = d.merge(b: 2); p [m.default, m.class]
class SubH < Hash; end
s = SubH[a: 1]; p s.merge(b: 1).class, s.reject { false }.class
dp = Hash.new { |hh, k| k * 2 }; dp[:x] = 1
r = dp.reject { false }; p [r.default_proc.nil?, r.default]
puts t { h.delete_if.class }, t { h.reject.class }, t { h.dup.reject!.class }, t { h.dup.keep_if.class }
puts t { {a: 1}.freeze.delete_if { true } }, t { {a: 1}.freeze.delete_if.class }, t { {a: 1}.freeze.reject! { true } }, t { {a: 1}.freeze.reject!.class }
puts t { {a: 1}.freeze.keep_if { true } }
p({a: 1, b: 2}.reject { |k, v| v > 1 }, {a: 1, b: 2}.reject! { false }, {a: 1, b: 2}.delete_if { |k, v| v == 1 })
x = Hash.new(9); p x.shift, Hash.new { 1 }.shift, {a: 1, b: 2}.shift
puts t { {}.freeze.shift }, t { {a: 1}.freeze.shift }
p({a: 1}.filter { true }, {a: 1}.select { false })
p Hash.instance_method(:update) == Hash.instance_method(:merge!)
p Hash.instance_method(:merge).arity, Hash.instance_method(:merge!).arity
e = {a: 1, b: 2}.delete_if; p e.each { |k, v| v == 1 }
", @"
{a: 1, b: 2}
{a: 9, b: 2, c: 3}
{a: 13, b: 2}
{a: 1, b: 2}
{a: 1, b: 2, c: 3, d: 4}
{a: [:a, 1, 5], b: 2}
FrozenError
FrozenError
TypeError: no implicit conversion of Integer into Hash
{a: 1, z: 26}
[5, Hash]
SubH
Hash
[true, nil]
Enumerator
Enumerator
Enumerator
Enumerator
FrozenError
Enumerator
FrozenError
Enumerator
FrozenError
{a: 1}
nil
{b: 2}
nil
nil
[:a, 1]
FrozenError
FrozenError
{a: 1}
{}
true
-1
-1
{b: 2}
");
        }

        /// <summary>
        /// Hash#slice, #except and #key are HashOps' own: #slice and #except read the table itself (an
        /// overridden #[] or #key? takes no part, and neither does the default), keep the
        /// compare_by_identity flag and nothing else, and #key compares with the value as the receiver
        /// of ==.
        /// </summary>
        public void Hash_SliceExceptKey1() {
            TestOutput(@"
c = Class.new(Hash) { def [](k) = 99; def key?(k) = false }
h = c.new
h.update(a: 1, ""s"" => 2, nil => 3)
r = h.slice(:a, ""s"", nil, :zz)
p r.to_a, r.class, r.default
s = +""s""
k = {""s"" => 1}.slice(s).keys[0]
p [k.frozen?, k.equal?(s)]
i = {}.compare_by_identity
key = ""a""
i[key] = 1
p i.slice(key, ""a"").to_a, i.slice(key).compare_by_identity?
d = Hash.new(5)
d.update(a: 1, b: 2, nil => 3)
e = d.except(:a, nil, :zz)
p e.to_a, e.default, e.class, d.size
p i.except(""a"").size, i.except(key).size, i.except(key).compare_by_identity?
v = {a: 1, b: 2.0, nil => 3, c: nil, d: 2}
p [v.key(2), v.key(2.0), v.key(3), v.key(nil), v.key(9)]
w = Object.new
def w.==(x) = true
p({a: w, b: 1}.key(5))
", @"
[[:a, 1], [""s"", 2], [nil, 3]]
Hash
nil
[true, false]
[[""a"", 1]]
true
[[:b, 2]]
nil
Hash
3
1
0
true
[:b, :b, nil, :c, nil]
:a
");
        }

        /// <summary>
        /// delete_if, keep_if, reject!, select! and filter! delete each condemned pair once the block has spoken,
        /// after a frozen check (MRI's delete_if_i), and update/merge! store a pair the block decided after one: a
        /// block that froze the hash got its deletions or updates through. The block of update and transform_values!
        /// runs while the receiver is being iterated, so #rehash or a new key in it is an error.
        /// </summary>
        public void Hash_MutateInBlock1() {
            TestOutput(@"
require 'ruby4'
h = {a: 1, b: 2, c: 3}
p((h.delete_if { |k, _| h.freeze if k == :b; true } rescue $!.class), h)
h = {a: 1, b: 2, c: 3}
p((h.keep_if { h.freeze; false } rescue $!.class), h)
h = {a: 1, b: 2, c: 3}
p((h.update({a: 10, b: 20}) { |key, _, v2| key == :b && h.freeze; v2 } rescue $!.class), h)
a = {1 => 1}
p((a.update({1 => 2}) { a.rehash } rescue $!))
p((a.update({1 => 2}) { a[3] = 3 } rescue $!))
x = {a: 1, b: 2, c: 3}
p((x.transform_values! { |v| x.freeze if v == 2; v.succ } rescue $!.class), x)
y = {a: 1}
p((y.transform_values! { y.rehash } rescue $!))
h = {a: 1, b: 2}
p h.reject! { |k, _| h.delete(:b) if k == :a; false }, h
p({a: 1}.reject! { false }, {a: 1}.select! { true })
", @"
FrozenError
{b: 2, c: 3}
FrozenError
{a: 1, b: 2, c: 3}
FrozenError
{a: 10, b: 2, c: 3}
#<RuntimeError: rehash during iteration>
#<RuntimeError: can't add a new key into hash during iteration>
FrozenError
{a: 2, b: 2, c: 3}
#<RuntimeError: rehash during iteration>
{a: 1}
{a: 1}
nil
nil
");
        }
    }
}

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
{1=>:a}
{2=>:b}
{3=>:c}
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
  p g
end
", @"
can't add a new key into hash during iteration
{1 => 1}
can't add a new key into hash during iteration
{1 => 1}
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
    }
}

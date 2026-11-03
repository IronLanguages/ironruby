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

namespace IronRuby.Tests {
    /// <summary>
    /// Ruby protocols on CLR objects: delegates as callables, tuples and pairs as arrays, hash patterns
    /// over CLR members, IComparable&lt;T&gt; as Comparable, tasks and async streams.
    /// </summary>
    public partial class Tests {

        #region Delegates

        public static class ProtocolDelegates1 {
            public delegate string Formatter(string text, int width = 10, params object[] extra);
            public delegate bool Parser(string text, out int value);

            public static Func<int, int> Doubler() { return x => x * 2; }
            public static Func<int, int, int> Adder() { return (a, b) => a + b; }
            public static Func<string> Constant() { return () => "const"; }
            public static Comparison<int> Descending() { return (a, b) => b.CompareTo(a); }
            public static Predicate<string> IsEmpty() { return s => s.Length == 0; }
            public static Action<string> Recorder(List<string> log) { return s => log.Add(s); }
            public static Formatter MakeFormatter() { return (text, width, extra) => text.PadLeft(width) + extra.Length; }
            public static Parser MakeParser() { return (string text, out int value) => Int32.TryParse(text, out value); }

            public static int Apply(Func<int, int> f, int x) { return f(x); }
        }

        /// <summary>
        /// A delegate answers the calls a Proc answers, and they all go through Invoke, converting the arguments.
        /// </summary>
        public void ClrDelegateCall1() {
            Context.ObjectClass.SetConstant("D", Context.GetClass(typeof(ProtocolDelegates1)));
            TestOutput(@"
dbl = D.doubler
p dbl.invoke(5), dbl.call(5), dbl.(5), dbl[5], dbl.yield(5), dbl === 5
p D.constant.call, D.constant[]

log = System::Collections::Generic::List[System::String].new
D.recorder(log).call('x')
p log.to_a

case ''
when D.is_empty then puts 'empty'
end
p %w[a b].grep(D.is_empty)

p((D.adder.call(1) rescue $!))
p((D.adder.call(1, 2, 3) rescue $!))
p((dbl.call('x') rescue $!.class))
p dbl.respond_to?(:call), dbl.method(:call).call(4)
p dbl.is_a?(System::ICloneable), System::Delegate.include?(System::Runtime::Serialization::ISerializable)
", @"
10
10
10
10
10
10
'const'
'const'
['x']
empty
[]
#<ArgumentError: wrong number of arguments (given 1, expected 2)>
#<ArgumentError: wrong number of arguments (given 3, expected 2)>
TypeError
true
8
true
true
");
        }

        /// <summary>
        /// Arity and parameters describe Invoke the way Method#arity and Method#parameters describe a method.
        /// </summary>
        public void ClrDelegateSignature1() {
            Context.ObjectClass.SetConstant("D", Context.GetClass(typeof(ProtocolDelegates1)));
            TestOutput(@"
[D.doubler, D.adder, D.constant, D.descending, D.is_empty, D.make_formatter, D.make_parser].each do |d|
  p [d.arity, d.parameters, d.lambda?]
end
p D.make_formatter.call('ab', 4)
", @"
[1, [[:req, :arg]], true]
[2, [[:req, :arg1], [:req, :arg2]], true]
[0, [], true]
[2, [[:req, :x], [:req, :y]], true]
[1, [[:req, :obj]], true]
[-2, [[:req, :text], [:opt, :width], [:rest, :extra]], true]
[1, [[:req, :text]], true]
'  ab0'
");
        }

        /// <summary>
        /// to_proc makes a lambda that invokes the delegate, which is what &amp;delegate passes as a block.
        /// </summary>
        public void ClrDelegateToProc1() {
            Context.ObjectClass.SetConstant("D", Context.GetClass(typeof(ProtocolDelegates1)));
            TestOutput(@"
dbl = D.doubler
pr = dbl.to_proc
p pr.class, pr.lambda?, pr.arity, pr.parameters, pr.call(3)
p((pr.call(1, 2) rescue $!))
p((D.constant.to_proc.call(1) rescue $!))

p [1, 2, 3].map(&dbl)
def m; yield 5; end
p m(&dbl)
p [3, 1, 2].sort(&D.descending)
p [3, 1, 2].max(&D.descending)
p D.apply(dbl, 4), D.apply(dbl.to_proc, 4)

define_method(:twice, &dbl)
p twice(8)

# a delegate made from a Ruby block goes back to being a Ruby callable
f = System::Func[Integer, Integer].new { |x| x + 100 }
p f.invoke(1), f.call(1), f.to_proc.call(1), [1, 2].map(&f), f.arity
p D.apply(f.to_proc, 1)
", @"
Proc
true
1
[[:req, :arg]]
6
#<ArgumentError: wrong number of arguments (given 2, expected 1)>
#<ArgumentError: wrong number of arguments (given 1, expected 0)>
[2, 4, 6]
10
[3, 2, 1]
1
8
8
16
101
101
101
[101, 102]
1
101
");
        }

        /// <summary>
        /// curry, &gt;&gt; and &lt;&lt; are Proc's applied to the delegate's #to_proc, as Method's are.
        /// </summary>
        public void ClrDelegateCompose1() {
            Context.ObjectClass.SetConstant("D", Context.GetClass(typeof(ProtocolDelegates1)));
            TestOutput(@"
dbl = D.doubler
inc = ->(x) { x + 1 }
p((dbl >> inc).call(5), (dbl << inc).call(5), (inc >> dbl).call(5), (inc << dbl).call(5))
p((dbl >> dbl).call(1))
p((dbl >> inc).lambda?, (dbl << inc).lambda?, (dbl << proc { |x| x }).lambda?)
p((dbl >> 5 rescue $!))
p((dbl << nil rescue $!))

add = D.adder
p add.curry[1][2], add.curry(2)[3][4], add.curry.lambda?
p((add.curry(3) rescue $!))
p dbl.curry[21]
", @"
11
12
12
11
4
true
true
false
#<TypeError: callable object is expected>
#<TypeError: callable object is expected>
3
7
true
#<ArgumentError: wrong number of arguments (given 3, expected 2)>
42
");
        }

        #endregion

        #region Type groups

        public class ProtocolGroupHost1 {
            public class Box {
                public static int Total;
                public static string Label { get; set; }
                public static int Twice(int x) { return x * 2; }
                public static int Apply(Func<int, int> f, int x) { return f(x); }
            }

            public class Box<T> {
                public static string Kind() { return typeof(T).Name; }
            }

            public class Pair<T> { }
            public class Pair<T, U> { }
        }

        /// <summary>
        /// A constant naming both a type and its generic namesakes is a TypeGroup; calls the group does not
        /// define itself go on to the non-generic type.
        /// </summary>
        public void ClrTypeGroupForwarding1() {
            Context.ObjectClass.SetConstant("H", Context.GetClass(typeof(ProtocolGroupHost1)));
            TestOutput(@"
B = H::Box
p B.class
p B.twice(21), B.Twice(1), B.apply(System::Func[Integer, Integer].new { |x| x + 1 }, 1)
p B.respond_to?(:twice), B.respond_to?(:Twice), B.respond_to?(:nope)
p B.method(:twice).call(4)

B.total = 5
B.label = 'x'
p B.total, B.label

NB = B[0]
def NB.with_block
  yield 3
end
p B.with_block { |x| x * 2 }

# the group's own methods are not forwarded
p B.count, B[Integer].kind

p((B.nope rescue $!))
p((B.nope(1, 2) rescue [$!.class, $!.name, $!.args]))
p((H::Pair.nope rescue $!))
p H::Pair.respond_to?(:nope)

p System::Tuple.create(1, 2).item2
p System::Threading::Tasks::Task.from_result(5).result
", @"
Microsoft::Scripting::Actions::TypeGroup
42
2
2
true
true
false
8
5
'x'
6
2
'Int32'
#<NoMethodError: undefined method 'nope' for an instance of Microsoft::Scripting::Actions::TypeGroup>
[NoMethodError, :nope, [1, 2]]
#<NoMethodError: undefined method 'nope' for an instance of Microsoft::Scripting::Actions::TypeGroup>
false
2
5
");
        }

        #endregion

        #region Tuples and pairs

        public static class ProtocolTuples1 {
            public static (int Min, int Max) MinMax(int[] xs) {
                return (System.Linq.Enumerable.Min(xs), System.Linq.Enumerable.Max(xs));
            }

            public static (string, int, bool) Triple() { return ("a", 1, true); }
            public static Tuple<string, int> RefPair() { return Tuple.Create("b", 2); }
            public static (int, int, int, int, int, int, int, int, int) Nine() { return (1, 2, 3, 4, 5, 6, 7, 8, 9); }

            public static Dictionary<string, int> Dict() {
                return new Dictionary<string, int> { { "a", 1 }, { "b", 2 } };
            }

            public static Hashtable Table() {
                var result = new Hashtable();
                result["x"] = 10;
                return result;
            }
        }

        /// <summary>
        /// Every ValueTuple/Tuple converts to an Array (to_a, to_ary), so multiple assignment and a block's |a, b| take
        /// it apart.
        /// </summary>
        public void ClrTupleDestructuring1() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTuples1)));
            TestOutput(@"
mm = T.min_max(System::Array[Integer].new([3, 1, 2]))
min, max = mm
p [min, max]
p mm.to_a, mm.to_ary, mm.size, mm.length
p mm[0], mm[1], mm[-1], mm[-2], mm[2], mm[-3]
p [*mm], Array(mm)
[mm].each { |a, b| p a + b }
p [mm, mm].map { |a, b| a * b }

t = T.triple
p t.to_a, t.size
s, i, b = t
p s, i, b

p T.ref_pair.to_a
x, y = T.ref_pair
p x, y

nine = T.nine
p nine.size, nine.to_a, nine[8], nine[-9]
", @"
[1, 3]
[1, 3]
[1, 3]
2
2
1
3
3
1
nil
nil
[1, 3]
[1, 3]
4
[3, 3]
['a', 1, true]
3
'a'
1
true
['b', 2]
'b'
2
9
[1, 2, 3, 4, 5, 6, 7, 8, 9]
9
1
");
        }

        /// <summary>
        /// Array patterns go through #deconstruct, which hands CLR strings over as Ruby strings so that String matches them.
        /// </summary>
        public void ClrTuplePatterns1() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTuples1)));
            TestOutput(@"
mm = T.min_max(System::Array[Integer].new([3, 1, 2]))
case mm
in [a, b] then p [a, b]
end
p mm.deconstruct, T.triple.deconstruct

case T.triple
in [String => s, Integer => i, true] then p [s, i]
end

p((T.triple in [String, Integer]))
p((T.triple in [*, true]))

case T.nine
in [1, *rest, 9] then p rest
end

T.ref_pair => [name, count]
p [name, count]
", @"
[1, 3]
[1, 3]
[""a"", 1, true]
[""a"", 1]
false
true
[2, 3, 4, 5, 6, 7, 8]
[""b"", 2]
");
        }

        /// <summary>
        /// A dictionary enumerates KeyValuePairs (or DictionaryEntries), which convert to [key, value], so a block
        /// with |k, v| gets the key and the value.
        /// </summary>
        public void ClrKeyValuePair1() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTuples1)));
            TestOutput(@"
d = T.dict
r = []
d.each { |k, v| r << [k, v] }
p r
p d.map { |k, v| k.to_s + v.to_s }
p d.inject(0) { |sum, (k, v)| sum + v }
p d.sort_by { |k, v| -v }.map { |k, v| k.to_s }

pair = d.first
k, v = pair
p [k, v], pair.to_a, pair.to_ary

h = T.table
h.each { |k, v| p [k, v] }
e = h.first
p e.class, e.to_a
k, v = e
p [k, v]
", @"
[['a', 1], ['b', 2]]
[""a1"", ""b2""]
3
[""b"", ""a""]
['a', 1]
['a', 1]
['a', 1]
['x', 10]
System::Collections::DictionaryEntry
['x', 10]
['x', 10]
");
        }

        /// <summary>
        /// deconstruct_keys of a pair: {key:, value:}, with the keys asked for (the CLR names Key/Value included), or
        /// all of them for nil.
        /// </summary>
        public void ClrKeyValuePairPatterns1() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTuples1)));
            TestOutput(@"
require 'ruby4'
p T.dict.to_h

pair = T.dict.first
p pair.deconstruct_keys(nil), pair.deconstruct_keys([:key]), pair.deconstruct_keys([:Value, :nope, 1])
p pair.deconstruct_keys([]), pair.deconstruct_keys(['key'])
p((pair.deconstruct_keys(1) rescue $!))

case pair
in {key: String => key, value: Integer => value} then p [key, value]
end
case pair
in {key: 'a', **rest} then p rest
end
p((pair in {key: 'b'}))

e = T.table.first
p e.deconstruct, e.deconstruct_keys(nil), e.deconstruct_keys([:Key])
case e
in [String => k, v] then p [k, v]
end
case e
in {key:, value:} then p [key, value]
end
", @"
{'a' => 1, 'b' => 2}
{key: ""a"", value: 1}
{key: ""a""}
{Value: 1}
{}
{""key"" => ""a""}
#<TypeError: wrong argument type Integer (expected Array or nil)>
[""a"", 1]
{value: 1}
false
[""x"", 10]
{key: ""x"", value: 10}
{Key: ""x""}
[""x"", 10]
[""x"", 10]
");
        }

        #endregion

        #region Hash patterns

        public record ProtocolPoint1(int X, int Y);

        public record ProtocolPerson1(string FirstName, int Age) {
            public string Nick { get; init; }
        }

        public class ProtocolShape1 {
            public static int Count = 1;
            public string Name { get; set; }
            public int Sides;
            public int this[int index] { get { return index; } }
            public int WriteOnly { set { } }
            private int Hidden { get { return 0; } }
            internal int Internal { get { return 0; } }
        }

        public class ProtocolShapeDerived1 : ProtocolShape1 {
            public new string Name { get { return "derived"; } }
        }

        public class ProtocolThrows1 {
            public int Ok { get { return 1; } }
            public int Bad { get { throw new InvalidOperationException("bad getter"); } }
        }

        public struct ProtocolMoney1 {
            public decimal Amount;
            public string Currency;
            public ProtocolMoney1(decimal amount, string currency) { Amount = amount; Currency = currency; }
        }

        /// <summary>
        /// What a hash pattern passes to #deconstruct_keys, as MRI compiles it: the keys it names, or nil when it needs
        /// every key - for a **rest or **nil, and for `in {}`, which only matches when there are no keys at all.
        /// </summary>
        public void HashPatternKeyList1() {
            TestOutput(@"
class Recorder
  def deconstruct_keys(keys)
    p keys
    {a: 1, b: 2}
  end
end
r = Recorder.new
p((r in {}))
p((r in {a: 1}))
p((r in {a: 1, **nil}))
p((r in {a: 1, **rest}))
p((r in {a: 1, **}))
p((r in {**rest}))
p rest

S = Struct.new(:a)
p((S.new(1) in {}))
", @"
nil
false
[:a]
true
nil
false
nil
true
[:a]
true
nil
true
{:a=>1, :b=>2}
false
");
        }

        private void DefineHashPatternTypes() {
            Context.ObjectClass.SetConstant("Point", Context.GetClass(typeof(ProtocolPoint1)));
            Context.ObjectClass.SetConstant("Person", Context.GetClass(typeof(ProtocolPerson1)));
            Context.ObjectClass.SetConstant("Shape", Context.GetClass(typeof(ProtocolShape1)));
            Context.ObjectClass.SetConstant("Derived", Context.GetClass(typeof(ProtocolShapeDerived1)));
            Context.ObjectClass.SetConstant("Throws", Context.GetClass(typeof(ProtocolThrows1)));
            Context.ObjectClass.SetConstant("Money", Context.GetClass(typeof(ProtocolMoney1)));
        }

        /// <summary>
        /// A hash pattern matches a CLR object's public readable instance properties and fields, by CLR name or by
        /// Ruby (mangled) name.
        /// </summary>
        public void ClrHashPatterns1() {
            DefineHashPatternTypes();
            TestOutput(@"
pt = Point.new(1, 2)
case pt
in {X: 1, Y: y} then p y
end
case pt
in {x:, y:} then p [x, y]
end
case Person.new('Ann', 30)
in {first_name: String => n, age: Integer => a} then p [n, a]
end
case Person.new('Ann', 30)
in {FirstName: 'Ann', nick: nil} then p :clr_name
end
case Money.new(System::Decimal.new(5), 'CHF')
in {currency: 'CHF', amount:} then p amount.to_s
end
Point.new(5, 6) => {Y: yy}
p yy

case pt
in Point(x: 1, y:) then p [:const, y]
end
p((pt in {z: 1}))
p((pt in {x: 2}))
p((pt in {}))
p((pt in {x: 1, **nil}))
case pt
in {x: 1, **rest} then p rest
end
p((Object.new in {a: 1}))
p((1 in {a: 1}))
", @"
2
[1, 2]
[""Ann"", 30]
:clr_name
""5""
6
[:const, 2]
false
false
false
false
{:y=>2}
false
false
");
        }

        /// <summary>
        /// deconstruct_keys follows Struct#deconstruct_keys: nil gives every member under its Ruby name, an Array the
        /// members it names (keyed as asked), anything else is a TypeError. Indexers, static, non-public and write-only
        /// members are not part of it; a member hidden by `new' is reported once, with the derived class's value.
        /// </summary>
        public void ClrHashPatternKeys1() {
            DefineHashPatternTypes();
            TestOutput(@"
pt = Point.new(1, 2)
p pt.deconstruct_keys(nil)
p pt.deconstruct_keys([:x, :Y, :z, 3, 'x'])
p pt.deconstruct_keys([])
p((pt.deconstruct_keys(1) rescue $!))
p((pt.deconstruct_keys(:x) rescue $!))

p Person.new('Ann', 30).deconstruct_keys(nil)

s = Shape.new
s.name = 'tri'
s.sides = 3
p s.deconstruct_keys(nil)
p s.deconstruct_keys([:hidden, :internal, :write_only, :count, :item, :Item])
p Derived.new.deconstruct_keys(nil), Derived.new.deconstruct_keys([:name])

p Throws.new.deconstruct_keys([:ok])
p((Throws.new.deconstruct_keys([:bad]) rescue [$!.class, $!.message]))
p((Throws.new.deconstruct_keys(nil) rescue $!.message))
", @"
{:x=>1, :y=>2}
{:x=>1, :Y=>2, ""x""=>1}
{}
#<TypeError: wrong argument type Integer (expected Array or nil)>
#<TypeError: wrong argument type Symbol (expected Array or nil)>
{:first_name=>""Ann"", :age=>30, :nick=>nil}
{:name=>""tri"", :sides=>3}
{}
{:name=>""derived"", :sides=>0}
{:name=>""derived""}
{:ok=>1}
[TypeError, ""bad getter""]
""bad getter""
");
        }

        /// <summary>
        /// Only CLR classes that no library defines get #deconstruct_keys this way: Ruby objects and the built-in
        /// classes do not, and a Ruby definition (in the class or in a module included into it) takes precedence.
        /// </summary>
        public void ClrHashPatternScope1() {
            DefineHashPatternTypes();
            TestOutput(@"
require 'ruby4'
p [Object.new, 'x', 1, 1.5, [], :s, nil, true, 1..2, System::Char.new('c'), Struct.new(:a).new(1), {}].map { |x| x.respond_to?(:deconstruct_keys) }
p Point.new(1, 2).respond_to?(:deconstruct_keys), Point.ancestors.include?(IronRuby::Clr::PatternMatching)
p Point.instance_method(:deconstruct_keys).owner

class RubyShape < Shape
  attr_accessor :extra
end
r = RubyShape.new
r.name = 'sub'
r.extra = 1
p r.deconstruct_keys([:name, :extra])

module MyKeys
  def deconstruct_keys(keys)
    { x: :mine }
  end
end
Point.send(:include, MyKeys)
p Point.new(1, 2).deconstruct_keys(nil)

class Shape
  def deconstruct_keys(keys)
    { name: :reopened }
  end
end
p Shape.new.deconstruct_keys(nil)
", @"
[false, false, false, false, false, false, false, false, false, false, true, true]
true
true
IronRuby::Clr::PatternMatching
{name: ""sub""}
{x: :mine}
{name: :reopened}
");
        }

        #endregion

        #region IComparable<T>

        public class ProtocolVersion1 : IComparable<ProtocolVersion1> {
            public readonly int Major, Minor;
            public ProtocolVersion1(int major, int minor) { Major = major; Minor = minor; }

            public int CompareTo(ProtocolVersion1 other) {
                return Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);
            }

            public override string ToString() { return Major + "." + Minor; }
        }

        public struct ProtocolMeters1 : IComparable<ProtocolMeters1> {
            public readonly double Value;
            public ProtocolMeters1(double value) { Value = value; }
            public int CompareTo(ProtocolMeters1 other) { return Value.CompareTo(other.Value); }
        }

        // compares its length with an int or with a string's length
        public class ProtocolLength1 : IComparable<int>, IComparable<string> {
            public readonly int Length;
            public ProtocolLength1(int length) { Length = length; }
            public int CompareTo(int other) { return Length.CompareTo(other); }
            public int CompareTo(string other) { return Length.CompareTo(other.Length); }
        }

        public class ProtocolAnimal1 : IComparable<ProtocolAnimal1> {
            public int Rank;
            public int CompareTo(ProtocolAnimal1 other) { return Rank.CompareTo(other.Rank); }
        }

        public class ProtocolDog1 : ProtocolAnimal1 { }

        public class ProtocolBothComparable1 : IComparable, IComparable<ProtocolBothComparable1> {
            public int Value;
            public int CompareTo(ProtocolBothComparable1 other) { return Value.CompareTo(other.Value); }

            public int CompareTo(object other) {
                var typed = other as ProtocolBothComparable1;
                if (typed == null) {
                    throw new ArgumentException("Object must be of type ProtocolBothComparable1.");
                }
                return CompareTo(typed);
            }
        }

        /// <summary>
        /// A type implementing only IComparable&lt;T&gt; is Comparable: &lt;=&gt; calls CompareTo, and everything Comparable
        /// and Array build on it works.
        /// </summary>
        public void ClrGenericComparable1() {
            Context.ObjectClass.SetConstant("V", Context.GetClass(typeof(ProtocolVersion1)));
            TestOutput(@"
require 'ruby4'
v1, v2, v3 = V.new(1, 2), V.new(1, 10), V.new(2, 0)
p v1 <=> v2, v2 <=> v1, v1 <=> V.new(1, 2)
p v1 < v2, v1 > v3, v1 <= v1, v3 >= v2, v1 == V.new(1, 2), v1 != v2
p v2.between?(v1, v3), v1.between?(v2, v3), v3.clamp(v1, v2).to_s
p [v3, v1, v2].max.to_s, [v3, v1, v2].min.to_s, [v3, v1, v2].sort.map(&:to_s), [v3, v1, v2].minmax.map(&:to_s)
p V.ancestors.include?(Comparable), V.include?(System::IComparable[V])
", @"
-1
1
0
true
false
true
true
true
true
true
false
""1.10""
""2.0""
""1.2""
[""1.2"", ""1.10"", ""2.0""]
[""1.2"", ""2.0""]
true
true
");
        }

        /// <summary>
        /// &lt;=&gt; is nil for what cannot be compared - nil, an object that is no IComparable&lt;T&gt;'s T - and then the
        /// Comparable operators raise. The T is picked by the argument; a subclass instance is a T too.
        /// </summary>
        public void ClrGenericComparable2() {
            Context.ObjectClass.SetConstant("V", Context.GetClass(typeof(ProtocolVersion1)));
            Context.ObjectClass.SetConstant("Meters", Context.GetClass(typeof(ProtocolMeters1)));
            Context.ObjectClass.SetConstant("Len", Context.GetClass(typeof(ProtocolLength1)));
            Context.ObjectClass.SetConstant("Animal", Context.GetClass(typeof(ProtocolAnimal1)));
            Context.ObjectClass.SetConstant("Dog", Context.GetClass(typeof(ProtocolDog1)));
            Context.ObjectClass.SetConstant("Both", Context.GetClass(typeof(ProtocolBothComparable1)));
            TestOutput(@"
v = V.new(1, 2)
p v <=> nil, v <=> 1, v <=> 'x', v <=> Object.new
p((v < 1 rescue $!.class))
p(([v, 1].max rescue $!.class))

p Meters.new(1.5) <=> Meters.new(2.5), Meters.new(2.5) > Meters.new(1.5), [Meters.new(3), Meters.new(1)].min.value

len = Len.new(3)
p len <=> 2, len <=> 3, len <=> 'abcd', len <=> 1.5

a = Animal.new
a.rank = 2
d = Dog.new
d.rank = 1
p d <=> a, a <=> d, d < a

b1 = Both.new
b1.value = 1
b2 = Both.new
b2.value = 2
p b1 <=> b2, b1 <=> 1, b1 <=> nil
", @"
nil
nil
nil
nil
ArgumentError
ArgumentError
-1
true
1.0
1
0
-1
nil
-1
1
true
-1
nil
nil
");
        }

        #endregion

        #region Tasks and async streams

        public static class ProtocolTasks1 {
            public static int StreamsDisposed;

            public static async System.Threading.Tasks.Task<int> AddLater(int a, int b) {
                await System.Threading.Tasks.Task.Delay(1);
                return a + b;
            }

            public static async System.Threading.Tasks.Task NothingLater() {
                await System.Threading.Tasks.Task.Yield();
            }

            public static async System.Threading.Tasks.Task<int> FailLater() {
                await System.Threading.Tasks.Task.Yield();
                throw new InvalidOperationException("boom");
            }

            public static System.Threading.Tasks.Task<string> Done(string value) {
                return System.Threading.Tasks.Task.FromResult(value);
            }

            public static System.Threading.Tasks.Task Cancelled() {
                return System.Threading.Tasks.Task.FromCanceled(new System.Threading.CancellationToken(true));
            }

            public static System.Threading.Tasks.Task FailTwice() {
                return System.Threading.Tasks.Task.WhenAll(
                    System.Threading.Tasks.Task.Run(() => { throw new InvalidOperationException("a"); }),
                    System.Threading.Tasks.Task.Run(() => { throw new ArgumentException("b"); })
                );
            }

            public static System.Threading.Tasks.Task<int> Run(Func<int> f) {
                return System.Threading.Tasks.Task.Run(f);
            }

            public static async IAsyncEnumerable<int> Stream(int count) {
                try {
                    for (int i = 0; i < count; i++) {
                        await System.Threading.Tasks.Task.Yield();
                        yield return i;
                    }
                } finally {
                    StreamsDisposed++;
                }
            }

            public static async IAsyncEnumerable<string> FailingStream() {
                yield return "first";
                await System.Threading.Tasks.Task.Yield();
                throw new InvalidOperationException("stream failed");
            }
        }

        /// <summary>
        /// Task#value waits and returns the result, nil for a task without one (an async Task method returns an internal
        /// Task&lt;VoidTaskResult&gt; subclass).
        /// </summary>
        public void ClrTaskValue1() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTasks1)));
            TestOutput(@"
t = T.add_later(2, 3)
p t.value, t.Result, t.value
p T.nothing_later.value, T.cancelled.is_completed
p T.done('x').value
p T.run(System::Func[Integer].new { 6 * 7 }).value
p System::Threading::Tasks::Task.completed_task.value
p T.add_later(1, 1).respond_to?(:value)
p T.done('x').is_a?(System::IDisposable), System::Threading::Tasks::Task[0].include?(System::IAsyncResult)
", @"
5
5
5
nil
true
'x'
42
nil
true
true
true
");
        }

        /// <summary>
        /// A failed task raises what it failed with, unwrapped from the AggregateException when there is just one;
        /// a cancelled one raises its TaskCanceledException. Result keeps raising the AggregateException.
        /// </summary>
        public void ClrTaskValue2() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTasks1)));
            TestOutput(@"
begin
  T.fail_later.value
rescue => e
  p e.class, e.message
end
begin
  T.fail_later.Result
rescue Exception => e
  p e.class
end
p((T.cancelled.value rescue $!.class))
begin
  T.fail_twice.value
rescue Exception => e
  p e.class, e.inner_exceptions.count
end
begin
  T.run(System::Func[Integer].new { raise IndexError, 'from ruby' }).value
rescue IndexError => e
  p e.class, e.message
end
", @"
TypeError
""boom""
System::AggregateException
System::Threading::Tasks::TaskCanceledException
System::AggregateException
2
IndexError
""from ruby""
");
        }

        /// <summary>
        /// An IAsyncEnumerable is Enumerable; its enumerator is disposed however the iteration ends.
        /// </summary>
        public void ClrAsyncEnumerable1() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTasks1)));
            TestOutput(@"
p T.stream(3).to_a, T.streams_disposed
p T.stream(100).first(2), T.streams_disposed
p T.stream(4).map { |x| x * 10 }, T.stream(5).select(&:odd?), T.stream(4).inject(:+), T.stream(3).include?(2)
p T.stream(5).each_slice(2).to_a
p T.stream(3).each.to_a
r = []
p T.stream(2).each { |x| r << x }.class == T.stream(0).class, r
p T.stream(1).is_a?(Enumerable)
p((T.failing_stream.to_a rescue [$!.class, $!.message]))
x = []
p((T.failing_stream.each { |s| x << s } rescue x))
", @"
[0, 1, 2]
1
[0, 1]
2
[0, 10, 20, 30]
[1, 3]
6
true
[[0, 1], [2, 3], [4]]
[0, 1, 2]
true
[0, 1]
true
[TypeError, ""stream failed""]
['first']
");
        }

        #endregion
    }
}

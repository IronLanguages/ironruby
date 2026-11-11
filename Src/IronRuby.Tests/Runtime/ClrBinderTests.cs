/* ****************************************************************************
 *
 * How Ruby calls bind to CLR methods: keywords by name, blocks and Method
 * objects as delegates, out parameters as Arrays, Arrays and Hashes as typed
 * collections.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;

namespace IronRuby.Tests {
    public partial class Tests {
        public class BinderNamed {
            public static string Greet(string name, string greeting = "Hello", int times = 1) {
                return String.Join(" ", System.Linq.Enumerable.Repeat(greeting + ", " + name, times));
            }

            public static string Abc(int a, int b = 2, int c = 3) { return a + "," + b + "," + c; }
            public static string Abc(string s, int b = 2) { return "s:" + s + "," + b; }
            public static string FirstLast(string firstName, string lastName) { return lastName + ", " + firstName; }
            public static string Options(IDictionary<object, object> options) { return "options:" + options.Count; }
            public static string Count(object value) { return "object"; }
            public static string Format(string format, params object[] args) { return String.Format(format, args); }
        }

        public class BinderWidget {
            public string Title { get; set; }
            public int Width { get; set; }
            public bool Visible;
            public string Fixed { get { return "fixed"; } }

            public BinderWidget() { }
            public BinderWidget(string title) { Title = title; }

            public override string ToString() { return Title + "/" + Width + "/" + Visible; }
        }

        public struct BinderPoint {
            public int X { get; set; }
            public int Y { get; set; }
            public override string ToString() { return X + "," + Y; }
        }

        public struct BinderPoint2 {
            public readonly int X, Y;
            public BinderPoint2(int x, int y) { X = x; Y = y; }
            public override string ToString() { return X + "," + Y; }
        }

        public void ClrKeywordArguments1() {
            Context.ObjectClass.SetConstant("N", Context.GetClass(typeof(BinderNamed)));
            TestOutput(@"
puts N.greet('Bob')
puts N.greet('Bob', greeting: 'Hi')
puts N.greet('Bob', times: 2, greeting: 'Yo')
puts N.greet(name: 'Ann')
puts N.first_last(last_name: 'Doe', first_name: 'John')
puts N.FirstLast(lastName: 'Roe', firstName: 'Jane')
puts N.abc(1, c: 9)
puts N.abc('x', b: 5)

# different keywords at one call site:
[{b: 7}, {c: 8}, {b: 1, c: 1}].each { |opts| puts N.abc(1, **opts) }
opts = {greeting: 'Hey'}
puts N.greet('Al', **opts)

# keywords no parameter takes go to a parameter that takes a Hash as it is:
puts N.options(a: 1, b: 2)
puts N.count(a: 1)
puts N.format('{0}', k: 1).class

begin
  N.abc(1, zz: 3)
rescue ArgumentError => e
  puts e.message
end

begin
  N.abc(1, zz: 3, yy: 4)
rescue ArgumentError => e
  puts e.message
end

begin
  N.greet('Bob', times: 'x')
rescue TypeError => e
  puts e.class
end
", @"
Hello, Bob
Hi, Bob
Yo, Bob Yo, Bob
Hello, Ann
Doe, John
Roe, Jane
1,2,9
s:x,5
1,7,3
1,2,8
1,1,1
Hey, Al
options:2
object
System::String
unknown keyword: :zz
unknown keywords: :zz, :yy
TypeError
");
        }

        /// <summary>
        /// Keywords passed to new set what no constructor parameter takes, like a C# object initializer.
        /// </summary>
        public void ClrKeywordArguments2() {
            Context.ObjectClass.SetConstant("W", Context.GetClass(typeof(BinderWidget)));
            Context.ObjectClass.SetConstant("P", Context.GetClass(typeof(BinderPoint)));
            Context.ObjectClass.SetConstant("P2", Context.GetClass(typeof(BinderPoint2)));
            TestOutput(@"
puts W.new(title: 't', width: 3, visible: true)
puts W.new('u', width: 5)
puts W.new(Title: 'v')

class SubWidget < W; end
puts SubWidget.new(title: 's', width: 1).inspect.include?('SubWidget')
puts SubWidget.new(title: 's', width: 1).Title

puts P.new(x: 1, y: 2)
puts P.new
puts P2.new(y: 4, x: 3)

begin
  W.new(fixed: 'x')
rescue Exception
  puts 'read-only'
end
", @"
t/3/True
u/5/False
v/0/False
true
s
1,2
0,0
3,4
read-only
");
        }

        public class BinderCallbacks {
            public static int Run(Func<int> f) { return f(); }
            public static int RunWith(int x, Func<int, int> f, int y = 0) { return f(x) + y; }
            public static string Many(Action a) { a(); return "action"; }
            public static string Many(Func<int> f) { return "func:" + f(); }
            public static string Arity(Func<int, int> f) { return "one:" + f(10); }
            public static string Arity(Func<int, int, int> f) { return "two:" + f(10, 20); }
            public static object TakeObject(object o) { return o; }
            public static string NoDelegate(int x) { return "no delegate " + x; }
            public static void Loop(Action<int> each) { for (int i = 0; i < 5; i++) each(i); }
            public static IEnumerable<int> Numbers(int count) { for (int i = 0; i < count; i++) yield return i; }
            public static string Pick() { return "none"; }
            public static string Pick(Func<int, int> f) { return "int"; }
            public static string Pick(Func<int, double> f) { return "double"; }
            public static void Each(IEnumerable<int> items, Action<int> action) { foreach (var item in items) action(item); }
        }

        public void ClrBlockToDelegate1() {
            Context.ObjectClass.SetConstant("CB", Context.GetClass(typeof(BinderCallbacks)));
            TestOutput(@"
include System::Collections::Generic

list = List[Integer].new([3, 1, 4, 1, 5])
r = []
list.ForEach { |x| r << x }
p r
list.Sort { |a, b| b <=> a }
p list.to_a
p list.FindAll { |x| x > 3 }.to_a
p list.ConvertAll { |x| x * 10 }.to_a
puts list.Exists { it == 4 }
list.RemoveAll { _1 == 1 }
p list.to_a

puts CB.run { 42 }
puts CB.run_with(4, y: 5) { |x| x + 6 }

# Action and Func both fit: the one that keeps the block's value
puts CB.many { 1 }

# the delegate whose parameters match the block's
puts CB.arity { |a| a + 1 }
puts CB.arity { |a, b| a + b }

# a block with fewer parameters than the delegate passes ignores the rest
r = []
CB.loop { r << :x }
p r.size

# a block that fits no delegate parameter is unused, as for any method
puts CB.no_delegate(1) { :unused }
puts CB.take_object(:o) { :unused }

# a Proc and a Method as explicit delegate arguments
puts CB.run_with(1, proc { |x| x * 3 })
def triple(x) = x * 3
puts CB.run_with(2, method(:triple))
", @"
[3, 1, 4, 1, 5]
[5, 4, 3, 1, 1]
[5, 4]
[50, 40, 30, 10, 10]
true
[5, 4, 3]
42
15
func:1
one:11
two:30
5
no delegate 1
o
3
6
");
        }

        /// <summary>
        /// break, next and exceptions in a block that a CLR method calls back as a delegate.
        /// </summary>
        public void ClrBlockToDelegate2() {
            Context.ObjectClass.SetConstant("CB", Context.GetClass(typeof(BinderCallbacks)));
            TestOutput(@"
r = []
x = CB.loop { |i| break :stopped if i == 3; r << i }
p r, x

r = []
CB.loop { |i| next if i.odd?; r << i }
p r

begin
  CB.loop { |i| raise 'boom' if i == 2 }
rescue => e
  puts e.message
end

# a block of another arity at the same call site binds anew
[proc { |a| a }, proc { |a, b| b }].each { |pr| puts CB.arity(&pr) }

r = []
CB.each(CB.numbers(3)) { |i| r << i }
p r

# a block that fits two delegate overloads equally is an error, not silently dropped for Pick()
puts CB.pick
begin
  CB.pick { |x| x }
rescue => e
  puts e.message.include?('pass the delegate itself')
end
puts CB.pick(System::Func[Integer, Integer].new { |x| x })
", @"
[0, 1, 2]
:stopped
[0, 2, 4]
boom
one:10
two:20
[0, 1, 2]
none
true
int
");
        }

        public void ClrMethodToDelegate1() {
            Context.ObjectClass.SetConstant("CB", Context.GetClass(typeof(BinderCallbacks)));
            TestOutput(@"
def twice(x) = x * 2
def add(a, b) = a + b
def any(*args) = args.size

puts CB.arity(method(:twice))
puts CB.arity(method(:add))
puts CB.run_with(1, method(:any))
p CB.run_with(1, 2.method(:+))

begin
  CB.run(method(:twice))
rescue TypeError => e
  puts e.class
end
", @"
one:20
two:30
1
3
TypeError
");
        }

        public class BinderSignatures {
            public static string Single(string firstName, int count = 1, params object[] rest) { return firstName; }
            public static string Overloaded(int a) { return "int"; }
            public static string Overloaded(string a, string b) { return "strings"; }
            public void WithOut(string value, out int length) { length = value.Length; }
        }

        /// <summary>
        /// Method#parameters names the parameters of a CLR method that is not overloaded.
        /// </summary>
        public void ClrMethodParameters1() {
            Context.ObjectClass.SetConstant("S", Context.GetClass(typeof(BinderSignatures)));
            TestOutput(@"
p S.method(:Single).parameters
p S.method(:single).arity
p S.method(:Overloaded).parameters
p S.instance_method(:WithOut).parameters
", @"
[[:req, :first_name], [:opt, :count], [:rest, :rest]]
-2
[[:req], [:rest]]
[[:req, :value]]
");
        }

        /// <summary>
        /// A type group (System::IComparable names IComparable and IComparable&lt;T&gt;) stands for its non-generic
        /// member where a module is expected.
        /// </summary>
        public void ClrTypeGroupAsModule1() {
            TestOutput(@"
class Ver
  include System::IComparable
  attr_reader :n
  def initialize(n); @n = n; end
  def compare_to(other); n <=> other.n; end
end

p Ver.ancestors.include?(Ver.ancestors[1])
list = System::Collections::ArrayList.new
[3, 1, 2].each { |n| list.add(Ver.new(n)) }
list.sort
p list.map { |v| v.n }
p Ver.new(1).is_a?(System::IComparable)
p Ver.include?(System::IComparable)

begin
  Class.new { include System::Func }
rescue TypeError => e
  puts e.class
end
", @"
true
[1, 2, 3]
true
true
TypeError
");
        }

        public static class BinderGenerics {
            public static List<T> Filter<T>(IEnumerable<T> items, Func<T, bool> predicate) { return items.Where(predicate).ToList(); }
            public static List<TResult> Map<T, TResult>(IEnumerable<T> items, Func<T, TResult> selector) { return items.Select(selector).ToList(); }
            public static T Apply<T>(Func<T, T> f) { return f(default(T)); }
            public static string TypeOf<T>(IEnumerable<T> items, Action<T> each) { foreach (var item in items) each(item); return typeof(T).Name; }
        }

        /// <summary>
        /// A block passed for a delegate whose parameters use a type parameter leaves the type parameter to the other
        /// arguments: Filter&lt;T&gt;(List&lt;int&gt;, Func&lt;T, bool&gt;) infers T = int, not Object.
        /// </summary>
        public void ClrGenericDelegateInference1() {
            Context.ObjectClass.SetConstant("G", Context.GetClass(typeof(BinderGenerics)));
            LegacyExtensionsTestOutput(@"
list = System::Collections::Generic::List[Integer].new([3, 1, 4])
p G.filter(list) { |x| x > 1 }.to_a
p G.filter(list, ->(x) { x > 3 }).to_a
p G.map(list) { |x| x.to_s * 2 }.to_a
r = []
puts G.type_of(list) { |x| r << x }
p r
p G.apply { |x| x }

load_assembly 'System.Linq'
using_clr_extensions System::Linq
p list.where { it > 1 }.to_a
p list.order_by { -it }.to_a
p list.Select(->(x, i) { x * i }).to_a
", @"
[3, 4]
[4]
[""33"", ""11"", ""44""]
Int32
[3, 1, 4]
nil
[3, 4]
[4, 3, 1]
[0, 1, 8]
");
        }

        public static class BinderRanges {
            public static string Slice(string s, Range range) { return s[range]; }
            public static char At(string s, Index index) { return s[index]; }
            public static int[] Take(int[] items, Range range) { return items[range]; }
        }

        /// <summary>
        /// A Ruby Range of Integers passes for a System.Range and a negative index for one counted from the end,
        /// with Ruby's meaning: an inclusive end unless excluded, -1 the last element.
        /// </summary>
        public void ClrRangeIndexArguments1() {
            Context.ObjectClass.SetConstant("R", Context.GetClass(typeof(BinderRanges)));
            TestOutput(@"
['1..3', '1...3', '2..', '-3..-1', '1...-1', '0..0'].each do |s|
  r = eval(s)
  puts R.slice('hello', r) == 'hello'[r]
end
puts R.at('hello', -1)
puts R.at('hello', 0)
p R.take([1, 2, 3, 4], 1..2).to_a
begin
  R.slice('hello', 1.5..2)
rescue TypeError
  puts 'TypeError'
end
", @"
true
true
true
true
true
true
o
h
[2, 3]
TypeError
");
        }

        public class BinderConstants {
            public const int Answer = 42;
            public static readonly string Greeting = "hello";
            public static string Mutable = "mutable";
            public static int Counter { get { return 7; } }
            public enum Color { Red, Green }
        }

        /// <summary>
        /// CLR const and static readonly fields are constants of their class; properties and other static members are
        /// methods, and naming one as a constant says how to call it.
        /// </summary>
        public void ClrConstantFields1() {
            Context.ObjectClass.SetConstant("K", Context.GetClass(typeof(BinderConstants)));
            TestOutput(@"
p K::Answer
puts K::Greeting
p K::Color::Green == K::Color.Green
p K.Counter
p K.Mutable.to_s
['Mutable', 'Counter'].each do |name|
  begin
    K.const_get(name)
  rescue NameError => e
    puts e.message.end_with?(""BinderConstants.#{name})"")
  end
end
p System::Math::PI.round(2)
p System::String::Empty.to_s

# a type group qualifies a constant as its non-generic member:
p System::Nullable::GetUnderlyingType(System::Nullable[Integer].to_clr_type).Name.to_s
", @"
42
hello
true
7
""mutable""
true
true
3.14
""""
""Int32""
");
        }

        public static class BinderObjects {
            public static string TypeName(object value) { return value == null ? "null" : value.GetType().Name; }
            public static string TypeNames(params object[] values) { return String.Join(",", Array.ConvertAll(values, v => v == null ? "null" : v.GetType().Name)); }
            public static object Echo(object value) { return value; }
        }

        /// <summary>
        /// A Ruby String passed for a CLR method's Object parameter arrives as a System.String; other values as they are.
        /// </summary>
        public void ClrStringObjectArguments1() {
            Context.ObjectClass.SetConstant("O", Context.GetClass(typeof(BinderObjects)));
            TestOutput(@"
puts O.type_name('s')
puts O.type_name(:s)
puts O.type_name(1)
puts O.type_name([1])
puts O.type_names('a', 1, nil)

class MyString < String; end
puts O.type_name(MyString.new('x'))

list = System::Collections::ArrayList.new
list.add('x')
p list.contains('x'), list[0] == 'x', list.index_of('x')
p O.echo('abc') == 'abc'
p({'abc' => 1}[O.echo('abc')])

# Ruby library methods keep Ruby strings:
a = []
a.push('y')
p a[0].class
", @"
String
RubySymbol
Int32
RubyArray
String,Int32,null
String
true
true
0
true
1
String
");
        }

        public class BinderOut {
            public string Split(string value, out string head, out string tail) {
                head = value.Substring(0, 1);
                tail = value.Substring(1);
                return "split";
            }

            public void Swap(ref int a, ref int b) {
                int t = a; a = b; b = t;
            }
        }

        public class BinderPair {
            public int Left { get; private set; }
            public int Right { get; private set; }
            public BinderPair(int left, int right) { Left = left; Right = right; }
            public void Deconstruct(out int left, out int right) { left = Left; right = Right; }
        }

        /// <summary>
        /// Out and ref parameters come back as a Ruby Array, so a C# Deconstruct method makes array patterns match.
        /// </summary>
        public void ClrOutParameters1() {
            Context.ObjectClass.SetConstant("O", Context.GetClass(typeof(BinderOut)));
            Context.ObjectClass.SetConstant("Pair", Context.GetClass(typeof(BinderPair)));
            TestOutput(@"
r = O.new.split('abc')
p r.class, r.size
result, head, tail = r
puts result, head, tail
p O.new.swap(1, 2)

p System::Int32.TryParse('12')
ok, n = System::Int32.TryParse('x')
p ok, n

# array patterns match what #deconstruct returns if it is an Array
d = Pair.new(1, 2).deconstruct
p d, Array === d
", @"
Array
3
split
a
bc
[2, 1]
[true, 12]
false
0
[1, 2]
true
");
        }

        public class BinderCollections {
            public static int Sum(int[] items) { return items.Sum(); }
            public static int SumAll(IEnumerable<int> items) { return items.Sum(); }
            public static string Join(IList<string> items) { items.Add("!"); return String.Join("-", items); }
            public static string ReadOnly(IReadOnlyList<double> items) { return items.Count + ":" + items[0]; }
            public static string Keys(Dictionary<string, int> map) {
                return String.Join(",", map.Keys.OrderBy(k => k, StringComparer.Ordinal)) + "=" + map.Values.Sum();
            }
            public static string Lookup(IReadOnlyDictionary<string, object> map) { return map.Count.ToString(); }
        }

        /// <summary>
        /// A Ruby Array or Hash passed where a CLR method takes a typed collection is copied into one.
        /// </summary>
        public void ClrCollectionArguments1() {
            Context.ObjectClass.SetConstant("C", Context.GetClass(typeof(BinderCollections)));
            TestOutput(@"
puts C.sum([1, 2, 3])
puts C.sum([])
puts C.sum_all([4, 5])
a = ['a', :b]
puts C.join(a)
p a
puts C.read_only([1, 2.5])
puts C.keys({'a' => 1, b: 2})
puts C.lookup({x: 1, y: nil})
puts System::Collections::Generic::List[Integer].new([7, 8]).Count

class MyArray < Array; end
puts C.sum(MyArray[1, 1])

begin
  C.sum([1, 'x'])
rescue TypeError => e
  puts e.class
end

begin
  C.sum([2**80])
rescue RangeError => e
  puts e.class
end
", @"
6
0
9
a-b-!
[""a"", :b]
2:1
a,b=3
2
2
2
TypeError
RangeError
");
        }
    }
}

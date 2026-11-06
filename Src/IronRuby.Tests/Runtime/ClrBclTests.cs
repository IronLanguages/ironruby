/* ****************************************************************************
 *
 * Ruby code using BCL types: CLR arrays, System::Char and System::String, the
 * collections, numeric types, and the CLR-only additions IronRuby makes to Ruby
 * classes (clr_new, clr_member, to_clr_ref, IronRuby::Clr::Name).
 *
 * Ported from the old Tests/Interop/net mspec suite, which nothing ran any more.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections;

namespace IronRuby.Tests {
    public partial class Tests {
        public static class BclComparableConsumer {
            public static int Consume(IComparable comparable) {
                return comparable.CompareTo(1);
            }
        }

        public interface IBclTestList : IEnumerable {
        }

        public static class BclEnumerableConsumer {
            public static ArrayList Collect(IBclTestList list) {
                var result = new ArrayList();
                foreach (var item in list) {
                    result.Add(item);
                }
                return result;
            }
        }

        public class BclDecimalHolder {
            public decimal Value { get; set; }
        }

        /// <summary>
        /// CLR arrays: System::Array.of(T).new with a size, a fill value or a Ruby array, create_instance,
        /// multi-dimensional indexing, and a fixed length.
        /// </summary>
        public void ClrBclArrays1() {
            TestOutput(@"
a = System::Array.of(Integer).new(3)
p a.class, a.to_a, a == [0, 0, 0]
p System::Array.of(Integer).new(3, 7).to_a
p System::Array.create_instance(Integer.to_clr_type, 2).to_a
p System::Array.of(Object).new([1, 's']), System::Array.of(Integer).new([1, 2])
p System::Array[System::String].new(5)[0]

m = System::Array.CreateInstance(Integer.to_clr_type, 2, 3)
(m.get_lower_bound(0)..m.get_upper_bound(0)).each do |i|
  (m.get_lower_bound(1)..m.get_upper_bound(1)).each { |j| m.set_value(i * 100 + j * 10, i, j) }
end
p m.class, m[1, 2], m.get_value(1, 1)
m[0, 1] = 5
p m[0, 1]

begin
  System::Array.of(Integer).new([10])[10] = 1
rescue => e
  p e.class
end

# a CLR array is one argument, not a splat
obj = Object.new
def obj.foo; end
[System::Array[Integer].new(1, 1), System::Array[Object].new(0)].each do |arr|
  begin
    obj.send(:foo, arr)
  rescue ArgumentError => e
    puts e.message
  end
end
", @"
System::Int32[]
[0, 0, 0]
true
[7, 7, 7]
[0, 0]
[1, ""s""]
[1, 2]
nil
System::Int32[,]
120
110
5
System::IndexOutOfRangeException
wrong number of arguments (given 1, expected 0)
wrong number of arguments (given 1, expected 0)
");
        }

        /// <summary>
        /// System::Char is made from a one-character string or char array and inspects with a (Char) suffix.
        /// </summary>
        public void ClrBclChar1() {
            TestOutput(@"
p System::Char.new('a'), System::Char.new('a'.to_clr_string).class
p System::Char.new(System::Array.of(System::Char).new(1)).class
p System::Char.new(""\n"")
['', ''.to_clr_string, System::Array.of(System::Char).new(0)].each do |x|
  begin
    System::Char.new(x)
  rescue ArgumentError
    puts 'ArgumentError'
  end
end
", @"
'a' (Char)
System::Char
System::Char
'\n' (Char)
ArgumentError
ArgumentError
ArgumentError
");
        }

        /// <summary>
        /// System::String takes the read-only half of String's protocol and refuses the mutating half.
        /// </summary>
        public void ClrBclString1() {
            TestOutput(@"
s = 'a'.to_clr_string
p s.class, s.to_clr_string.equal?(s)
p s <=> 'b'.to_clr_string, s <=> 'b', s <=> Object.new
p s == 'a'.to_clr_string, s == 'b'.to_clr_string, s == 'a', s == 'b', s == Object.new
p ''.to_clr_string.empty?, s.empty?
p s.dump, ''.to_clr_string.dump
p ""some\nlines\"""".to_clr_string, 'a word'.to_clr_string
f = '%0.3f'.to_clr_string % 1.0
p f, f.class
str = 'string'.to_clr_string
p str.include?('rin'.to_clr_string), str.include?('rin'), str.include?(System::Char.new('i')), str.include?('x')
p s * 2, s * 0
begin
  s * -1
rescue ArgumentError
  puts 'ArgumentError'
end
begin
  s.chop!
rescue TypeError
  puts 'TypeError'
end
begin
  s[0] = 'b'
rescue TypeError
  puts 'TypeError'
end
begin
  s.no_such_method
rescue NoMethodError
  puts 'NoMethodError'
end
p System::String.new(System::Char.new('a'), 2), System::String.new(System::Array.of(System::Char).new(2, System::Char.new('a')))
p System::String.new(System::Array.of(System::Char).new(1), 0, 0) == System::String.empty

sb = System::Text::StringBuilder.new
sb.append('abc')
sb.append('def')
sb.append(100)
sb.insert(3, '012')
p sb.to_string, sb.length
sb.capacity = 20
p sb.capacity
", @"
System::String
true
-1
-1
nil
true
false
true
false
false
true
false
""'a'""
""''""
'some\nlines""'
'a word'
'1.000'
System::String
true
true
true
false
'aa'
''
ArgumentError
TypeError
TypeError
NoMethodError
'aa'
'aa'
true
'abc012def100'
12
20
");
        }

        /// <summary>
        /// Non-generic and generic BCL collections are Enumerable, compare with arrays, take Ruby additions,
        /// and IComparable types are Comparable.
        /// </summary>
        public void ClrBclCollections1() {
            TestOutput(@"
list = System::Collections::ArrayList.new
list.add 1
list.add 2
list << 3
p System::Collections::ArrayList.ancestors.include?(Enumerable)
b = []
list.each { |i| b << i }
p b, list.map { |i| i + 3 }, list.select(&:even?)

l1 = System::Collections::ArrayList.new
[1, 3, 2, 3].each { |e| l1.add e }
l2 = System::Collections::ArrayList.new
[5, 4, 3, 4, 6].each { |e| l2.add e }
p l1 == [1, 3, 2, 3], l1 | l2

class System::Collections::ArrayList
  def bcl_spec_total
    inject(0) { |a, i| a + i }
  end
end
p list.bcl_spec_total

d = System::Collections::Generic::Dictionary[Object, Object].new
d[:foo] = 'bar'
d[:baz] = 'raz'
p d[:foo], d[:baz], d.count

c = System::Collections::ObjectModel::ObservableCollection[String].new
c.add 'hello'
p c[0], c.class

v1, v2, v3 = System::Version.new(1, 0, 0, 0), System::Version.new(2, 0, 0, 0), System::Version.new(3, 0, 0, 0)
p v1 <=> v1, v1 <=> v2, v1 < v2, v2.between?(v1, v3), System::Version.ancestors.include?(Comparable)
", @"
true
[1, 2, 3]
[4, 5, 6]
[2]
true
[1, 3, 2, 5, 4, 6]
6
'bar'
'raz'
2
""hello""
System::Collections::ObjectModel::ObservableCollection[String]
0
-1
true
true
true
");
        }

        /// <summary>
        /// A Ruby class that includes System::IComparable is one to CLR code, and a Ruby class that includes a
        /// non-generic IEnumerable interface supplies its enumerator through get_enumerator, its own or Array's.
        /// </summary>
        public void ClrBclImplementation1() {
            Context.ObjectClass.SetConstant("ComparableConsumer", Context.GetClass(typeof(BclComparableConsumer)));
            Context.ObjectClass.SetConstant("EnumerableConsumer", Context.GetClass(typeof(BclEnumerableConsumer)));
            Context.ObjectClass.SetConstant("ITestList", Context.GetModule(typeof(IBclTestList)));
            TestOutput(@"
class ComparableProvider
  include System::IComparable
  def compare_to(other) = other <=> 0
end
p ComparableConsumer.consume(ComparableProvider.new)

class TestList
  include ITestList

  def initialize; @store = []; end
  def <<(val); @store << val; self; end
  def get_enumerator = Enumerator.new(@store)

  class Enumerator
    include System::Collections::IEnumerator
    def initialize(list); @list = list; @position = -1; end
    def move_next; @position += 1; valid?; end
    def reset; @position = -1; end
    def valid? = @position != -1 && @position < @list.length
    def current
      raise System::InvalidOperationException.new unless valid?
      @list[@position]
    end
  end
end

class TestListFromArray < Array
  include ITestList
  def get_enumerator = clr_member(:get_enumerator).call
end

class TestListMissing < Array
  include ITestList
end

p EnumerableConsumer.collect(TestList.new << 'a' << 'b').to_a
p EnumerableConsumer.collect(TestListFromArray.new << 'c').to_a
begin
  EnumerableConsumer.collect(TestListMissing.new << 'd')
rescue NoMethodError => e
  puts e.class
end
", @"
1
[""a"", ""b""]
[""c""]
NoMethodError
");
        }

        /// <summary>
        /// Ruby exception classes that are CLR exception types, System::Object, and conversions between Time and
        /// System::DateTime and between Ruby numbers and System::Decimal.
        /// </summary>
        public void ClrBclMappings1() {
            Context.ObjectClass.SetConstant("DecimalHolder", Context.GetClass(typeof(BclDecimalHolder)));
            TestOutput(@"
p Errno::EACCES == System::UnauthorizedAccessException
p Errno::ENOENT == System::IO::FileNotFoundException
p Errno::ENOTDIR == System::IO::DirectoryNotFoundException
p System::Object == Object, System::Int32 == Integer

t = Time.local(2000, 1, 1)
p System::DateTime.compare(t, t), (t - System::DateTime.new(2000, 1, 1)).class

p System::Decimal.new(0.5) == 0.5, System::Decimal.new(0.5) == 0
p System::Decimal.parse('1.5') == 1.5, System::Decimal.parse('1.5') == 1
h = DecimalHolder.new
h.value = 10
p h.value == 10
h.value = 1.0
p h.value == 1.0

p nil.GetType.ToString, nil.ToString, nil.GetHashCode
", @"
true
true
true
true
true
0
Float
true
false
true
false
true
true
'Microsoft.Scripting.Runtime.DynamicNull'
'nil'
0
");
        }

        /// <summary>
        /// Ranges over the small CLR integer types yield values of that type up to its MaxValue and Integers beyond,
        /// and Array#- tells a System::Byte 2 from an Integer 2.
        /// </summary>
        public void ClrBclNumerics1() {
            TestOutput(@"
[System::Byte, System::SByte, System::Int16, System::UInt16, System::UInt32, System::UInt64].each do |k|
  arr = (k.new(0)..k.new(4)).to_a
  two = k.new(2)
  max = k.max_value.to_i
  p [k, arr.map(&:class).uniq, (arr - [two]).include?(two), (arr - [2]).include?(two), (arr - [2.0]).include?(two),
     (k.new(max)..(max + 2)).to_a.map(&:class)]
end
", @"
[System::Byte, [System::Byte], false, true, true, [System::Byte, Integer, Integer]]
[System::SByte, [System::SByte], false, true, true, [System::SByte, Integer, Integer]]
[System::Int16, [System::Int16], false, true, true, [System::Int16, Integer, Integer]]
[System::UInt16, [System::UInt16], false, true, true, [System::UInt16, Integer, Integer]]
[System::UInt32, [System::UInt32], false, true, true, [System::UInt32, Integer, Integer]]
[System::UInt64, [System::UInt64], false, true, true, [System::UInt64, Integer, Integer]]
");
        }

        /// <summary>
        /// The CLR additions to Ruby classes: clr_new runs only the CLR constructor, clr_member finds CLR members
        /// by CLR or Ruby name, to_clr_ref makes a by-ref type, and IronRuby::Clr::Name matches like a String.
        /// </summary>
        public void ClrBclAdditions1() {
            TestOutput(@"
p Array.clr_new, Hash.clr_new, String.clr_new, Object.clr_new.class, Exception.clr_new.class
class BclPlainRubyClass; end
[BclPlainRubyClass, Class.new, Numeric].each do |k|
  begin
    k.clr_new
  rescue TypeError
    puts 'TypeError'
  end
end

o = Object.new
p o.clr_member(:Finalize).class, 1.clr_member(:Finalize).class, o.clr_member(:GetType).class, o.clr_member(:get_type).class
[:not_a_method, :inspect].each do |name|
  begin
    o.clr_member(name)
  rescue NameError
    puts 'NameError'
  end
end
n = Object.new
def n.to_str = 'get_type'
p o.clr_member(n).call == o.GetType

[Integer, System::String, Object, System::IComparable[Integer]].each do |k|
  p k.to_clr_ref == k.to_clr_type.make_by_ref_type.to_class
end
p [System, BclPlainRubyClass, BclPlainRubyClass.new.singleton_class, Class.new, Kernel, Module.new].map(&:to_clr_ref)
[-> { Integer.to_clr_ref.to_clr_ref }, -> { System::Array.of(Integer.to_clr_ref) }].each do |l|
  begin
    l.call
  rescue TypeError
    puts 'TypeError'
  end
end

N = IronRuby::Clr::Name
s = Object.new
def s.to_str = 'obj'
p N.new('foo').class, N.new(:a_sym).to_s, N.new(s).to_s, N.new('').to_s
h = N.new('hello')
p h.match(/(.)\1/)[0], h.match('(.)\1')[0], h.match('xx'), h.match(/\Gh/)[0], h.match(/\Go/)
[10, :ell].each do |x|
  begin
    h.match(x)
  rescue TypeError
    puts 'TypeError'
  end
end
h.match(/./)
p $~[0]
h.match(/X/)
p $~
r = N.new('rudder')
p r =~ /udder/, N.new('bean') =~ /bag/
m = Object.new
def m.=~(other) = [:called, other.to_s]
p r =~ m
begin
  r =~ 'x'
rescue TypeError
  puts 'TypeError'
end
", @"
[]
{}
""""
Object
Exception
TypeError
TypeError
TypeError
Method
Method
Method
Method
NameError
NameError
true
true
true
true
true
[nil, nil, nil, nil, nil, nil]
TypeError
TypeError
IronRuby::Clr::Name
""a_sym""
""obj""
""""
""ll""
""ll""
nil
""h""
nil
TypeError
TypeError
""h""
nil
1
nil
[:called, ""rudder""]
TypeError
");
        }
    }
}

/* ****************************************************************************
 *
 * Interop behaviour the old Tests/Interop/net mspec suite checked and nothing else did: Ruby
 * objects seen through .NET's equality and hashing, culture-independent number formatting,
 * delegates and events, Ruby classes implementing .NET interface events, changes to CLR types
 * and namespaces. That suite needed C# fixtures compiled from heredocs and a 2009 mspec, and was
 * deleted in #119; these use BCL types only.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

namespace IronRuby.Tests {
    public partial class Tests {
        /// <summary>
        /// .NET's Equals and GetHashCode of a Ruby object are its eql? and hash: any truthy eql? is equal,
        /// a Bignum or non-Integer hash still gives a hash code, Array and Hash compare by reference, and
        /// eql?/hash and Equals/GetHashCode may be defined separately. A singleton eql? or hash on a CLR
        /// object stays on the Ruby side; on a Ruby object .NET sees it. A Ruby subclass of a CLR class
        /// maps its own eql? and hash. (equality_spec.rb, hashing_spec.rb)
        /// </summary>
        public void ClrInterop_EqualityHashing1() {
            TestOutput(@"
EqC = System::Collections::Generic::EqualityComparer[Object].Default
def eq(a, b) = EqC.Equals(a, b)
def hc(a) = EqC.GetHashCode(a)

class WithEql; def initialize(v); @v = v; end; def eql?(o) = @v; end
p ['hello', 321, nil, false].map { |v| eq(WithEql.new(v), 123) }
o = Object.new
p eq([o], [o]), eq({o => o}, {o => o})
a, b = System::Text::StringBuilder.new, System::Text::StringBuilder.new
p [a.eql?(a) == eq(a, a), a.eql?(nil) == eq(a, nil), a.eql?(b) == eq(a, b)]
s = System::Text::StringBuilder.new; def s.eql?(x) = true
r = Object.new; def r.eql?(x) = true
p eq(s, System::Text::StringBuilder.new), eq(r, Object.new)
class EqlAndEquals; def eql?(o) = o == :ruby; def Equals(o) = o == :clr; end
x = EqlAndEquals.new
p [x.eql?(:ruby), eq(x, :clr), eq(x, :ruby)]

class WithHash; def initialize(h); @h = h; end; def hash = @h; end
k = Object.new
p hc(WithHash.new(5)), hc(WithHash.new(2**70 + 123)).class, hc(WithHash.new(k)) == System::Runtime::CompilerServices::RuntimeHelpers.get_hash_code(k)
arr = [1]; h1 = hc(arr); arr << 2
hash = {1 => 2}; h2 = hc(hash); hash.clear
p h1 == hc(arr), h2 == hc(hash)
s = System::Text::StringBuilder.new; def s.hash = super + 1
r = Object.new; def r.hash = super + 1
c = System::Text::StringBuilder.new
p c.hash == hc(c), s.hash != hc(s), r.hash == hc(r)
class HashAndGHC; def hash = 1; def GetHashCode = 2; end
p [HashAndGHC.new.hash, hc(HashAndGHC.new)]
class SubList < System::Collections::ArrayList; def hash = 234; def eql?(o) = o == :m; end
p hc(SubList.new), [eq(SubList.new, :m), eq(SubList.new, :x)]
", @"
[true, true, false, false]
false
false
[true, true, true]
false
true
[true, true, false]
5
Integer
true
true
true
true
true
true
[1, 2]
234
[true, false]
");
        }

        /// <summary>
        /// Ruby's number formatting and parsing do not follow the thread's .NET culture: under de-DE
        /// (decimal comma) and ja-JP, Float#to_s, format, Float(), String#to_f and BigDecimal are as under
        /// the invariant culture. (culture_spec.rb)
        /// </summary>
        public void ClrInterop_Culture1() {
            TestOutput(@"
require 'bigdecimal'
thread = System::Threading::Thread.CurrentThread
old = thread.CurrentCulture
['de-DE', 'ja-JP'].each do |name|
  thread.CurrentCulture = System::Globalization::CultureInfo.new(name, false)
  begin
    p [1.5.to_s, 1.5.inspect, '%.2f' % 1.5, Float('1.5'), '2.5'.to_f, 1e20.to_s, BigDecimal('1.25').to_s, BigDecimal('NaN').to_f.to_s]
  ensure
    thread.CurrentCulture = old
  end
end
", @"
[""1.5"", ""1.5"", ""1.50"", 1.5, 2.5, ""1.0e+20"", ""0.125e1"", ""NaN""]
[""1.5"", ""1.5"", ""1.50"", 1.5, 2.5, ""1.0e+20"", ""0.125e1"", ""NaN""]
");
        }

        /// <summary>
        /// A delegate made from a block, a Method or a lambda can be invoked again and again; a
        /// delegate type's new takes exactly one callable. An event handler added twice runs twice, and
        /// removing it more often than it was added is no error. (delegate/shared, events/)
        /// </summary>
        public void ClrInterop_DelegatesEvents1() {
            TestOutput(@"
r = []
d = System::Action[Integer].new { |x| r << x }
d.invoke(1); d.invoke(2)
System::Action[Integer].new(r.method(:<<)).invoke(3)
System::Action[Integer].new(lambda { |x| r << x }).invoke(4)
p r, System::Func[Integer, Integer].new(5.method(:+)).invoke(3)
p((System::Action.new rescue $!), (System::Action.new(1) rescue $!))

list = System::ComponentModel::BindingList[Integer].new
n = 0
h = System::ComponentModel::ListChangedEventHandler.new { |s, e| n += 1 }
list.list_changed.add h
list.list_changed.add h
list.add(1)
p n, list.list_changed.class
3.times { list.list_changed.remove h }
list.add(2)
p n
", @"
[1, 2, 3, 4]
8
#<ArgumentError: wrong number of arguments (given 0, expected 1)>
#<TypeError: can't convert Integer into System::Action>
2
IronRuby::Builtins::RubyEvent
2
");
        }

        /// <summary>
        /// A Ruby class implements a .NET interface's event with add_X/remove_X methods, or through
        /// method_missing, and .NET code subscribes, raises and unsubscribes through EventInfo.
        /// (interface/implementation_spec.rb)
        /// </summary>
        public void ClrInterop_InterfaceEvents1() {
            TestOutput(@"
INPC = System::ComponentModel::INotifyPropertyChanged
class Exposer
  include INPC
  attr_reader :handlers
  def initialize; @handlers = []; end
  def add_PropertyChanged(h) = @handlers << h
  def remove_PropertyChanged(h) = @handlers.delete(h)
end
class ExposerMM
  include INPC
  attr_reader :handlers
  def initialize; @handlers = []; end
  def method_missing(name, *args)
    case name.to_s
    when /add_PropertyChanged/i then @handlers << args[0]
    when /remove_PropertyChanged/i then @handlers.delete(args[0])
    else super
    end
  end
end
ev = INPC.to_clr_type.get_event('PropertyChanged')
[Exposer, ExposerMM].each do |k|
  obj = k.new
  hits = []
  h = System::ComponentModel::PropertyChangedEventHandler.new { |s, e| hits << e.property_name }  # a System::String, which inspects in single quotes
  ev.add_event_handler(obj, h)
  added = obj.handlers.size
  obj.handlers.each { |x| x.invoke(obj, System::ComponentModel::PropertyChangedEventArgs.new('x')) }
  ev.remove_event_handler(obj, h)
  p [added, hits, obj.handlers.size]
end
", @"
[1, ['x'], 0]
[1, ['x'], 0]
");
        }

        /// <summary>
        /// A Ruby class can include another interface only until its first instance exists; abstract and
        /// static CLR classes have no allocator; undef removes a CLR instance method, a static method and a
        /// method on one object's singleton class. (class/type_modification, class/abstract, class/static,
        /// class/removal)
        /// </summary>
        public void ClrInterop_TypeModification1() {
            TestOutput(@"
c = Class.new { include System::Collections::Generic::IList[Integer] }
c.new
p((c.class_eval { include System::IDisposable } rescue $!.class))
d = Class.new { include System::Collections::Generic::IList[Integer] }
d.class_eval { include System::IDisposable }
p d.new.is_a?(System::IDisposable)
p((System::IO::Stream.new rescue $!), (System::Math.new rescue $!))
class System::Text::StringBuilder
  undef append_line
end
p((System::Text::StringBuilder.new.append_line('x') rescue $!.class))
class << System::String
  undef is_null_or_empty
end
p((System::String.is_null_or_empty('') rescue $!.class))
sb = System::Text::StringBuilder.new
class << sb; undef to_string; end
p((sb.to_string rescue $!.class), System::Text::StringBuilder.new('y').to_string)
", @"
TypeError
true
#<TypeError: allocator undefined for System::IO::Stream>
#<TypeError: allocator undefined for System::Math>
NoMethodError
NoMethodError
NoMethodError
'y'
");
        }

        /// <summary>
        /// A CLR namespace is a Ruby module like any other: it takes methods (module_function ones too),
        /// Ruby classes and modules, loses them again with undef and remove_const, and mixed into a class
        /// it makes its types reachable by their short names. (namespaces/modification)
        /// </summary>
        public void ClrInterop_Namespaces1() {
            TestOutput(@"
module System::Collections
  def ns_bar; 1; end
  def nsf; 2; end
  module_function :nsf
  class RTest; end
  module RMod; def self.x; 3; end; end
end
c = Class.new { include System::Collections }
p c.new.ns_bar, System::Collections.nsf, System::Collections::RTest.new.class, System::Collections::RMod.x
module System::Collections; undef ns_bar; end
p((c.new.ns_bar rescue $!.class))
System::Collections.send(:remove_const, :RTest)
p((System::Collections::RTest rescue $!.class))
class RubyNsUser; def self.count; Generic::List[Integer].new.count; end; end
p((RubyNsUser.count rescue $!.class))
class RubyNsUser; include System::Collections; end
p RubyNsUser.count
", @"
1
2
System::Collections::RTest
3
NoMethodError
NameError
NameError
0
");
        }

        /// <summary>
        /// System::String#[] / #slice take what Ruby's String#[] takes: an index, a start and length, a Range, a Regexp
        /// with or without a group, a String or a System::String to look for. A Ruby String raised "no implicit
        /// conversion of String into Integer", and the empty slice at the end ("hello"[5, 1], "hello"[3..1]) gave the
        /// whole string (#140). An index gives a System::Char; the slices are System::Strings.
        /// (string/element_reference_spec.rb)
        /// </summary>
        public void ClrInterop_StringSlice1() {
            TestOutput(@"
h = 'hello'.to_clr_string; ht = 'hello there'.to_clr_string
p h[0], h[-1], h[20], h[1].class
p h[1, 3], h[1, 3].class, h[5, 1], h[6, 1], h[-3, 2], h[0, 0]
p h[1..3], h[3..1], h[4..10], h[5..], h[6..]
p h[/l+/], h[/z/], h[/(e)(l)/, 2]
p ht['lo'], ht['lo'].class, ht['bye'], ht.slice('the'), ht['lo'.to_clr_string]
p((h[nil] rescue $!.class), (h[{}] rescue $!.class))
p h[5, -1], h[6, -1]
", @"
'h' (Char)
'o' (Char)
nil
System::Char
'ell'
System::String
''
nil
'll'
''
'ell'
''
'o'
''
nil
'll'
nil
'l'
'lo'
System::String
nil
'the'
'lo'
TypeError
TypeError
nil
nil
");
        }

        /// <summary>
        /// #141: IronRuby::Clr::Name.new takes what a method name may be - a String, a Symbol, #to_str - and nil is a
        /// TypeError like any other non-name (System::ArgumentNullException leaked). A Method passed for a delegate
        /// whose parameters mention a type parameter leaves that type parameter to the other arguments, as a block,
        /// a lambda and a proc do: Array.FindAll(int[], 2.method(:<)) is FindAll&lt;int&gt; ("generic arguments could not
        /// be infered"). (Clr::Name new_spec, method/invocation/generic)
        /// </summary>
        public void ClrInterop_NameAndMethodInference1() {
            TestOutput(@"
N = IronRuby::Clr::Name
o = Object.new; def o.to_str; 'obj'; end
p N.new(:a_sym).to_s, N.new('str').to_s, N.new(o).to_s
p((N.new(nil) rescue $!), (N.new(1.0) rescue $!))
arr = System::Array[Integer].new([1, 2, 3, 4])
p System::Array.find_all(arr, 2.method(:<)).to_a, System::Array.find_all(arr) { |x| x > 2 }.to_a
p System::Array.find_all(arr, lambda { |x| x.odd? }).to_a, System::Array.find_all(arr, proc { |x| x.even? }).to_a
l = System::Collections::Generic::List[Integer].new([3, 1, 2])
def dbl(x); x * 2; end
p System::Linq::Enumerable.where(l, 1.method(:<)).to_a, System::Linq::Enumerable.select(l, method(:dbl)).to_a
", @"
""a_sym""
""str""
""obj""
#<TypeError: nil is not a symbol nor a string>
#<TypeError: 1.0 is not a symbol nor a string>
[3, 4]
[3, 4]
[1, 3]
[2, 4]
[3, 2]
[6, 2, 4]
");
        }
    }
}

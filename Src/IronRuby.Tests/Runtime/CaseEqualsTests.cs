
namespace IronRuby.Tests {
    public partial class Tests {
        /// <summary>
        /// Module#=== as `when' and `rescue' ask it. A call site binds a rule that answers it directly, which must agree
        /// with the method for every receiver of the class, for a receiver whose === is user-defined, and after Module#===
        /// is wrapped. The answers are CRuby's.
        /// </summary>
        public void CaseEquals1() {
            TestOutput(@"
module M; end
class A; include M; end
class B < A; end
class Foo; def self.===(x) = x == :foo; end
o = Object.new
class << o; end
p Integer === 1, Integer === 1.0, Numeric === 1.0, Comparable === 1, Kernel === o, BasicObject === 1
p A === B.new, M === B.new, B === A.new, Class === A, Module === M, Class === M, Module === Class
p Object === nil, NilClass === nil
p Foo === :foo, Foo === :bar
case :foo
when Foo then puts 'foo-matched'
else puts 'no'
end
case 5
when String then puts 's'
when Integer then puts 'int'
end
class Mod2 < Module; def ===(x) = :sub; end
p Mod2.new === 1
m = Module.new { def self.===(x) = :own }
p m === 1
class Module; alias old_case_eq ===; def ===(x) = [:wrapped, old_case_eq(x)]; end
p Integer === 2
", @"
true
false
true
true
true
true
true
true
false
true
true
false
true
true
true
true
false
foo-matched
int
:sub
:own
[:wrapped, true]
");
        }

        /// <summary>
        /// Rules bound before c_call tracing is switched on are rebound, so the library method's own rule reports
        /// each === while a TracePoint wants it.
        /// </summary>
        public void CaseEquals2() {
            TestOutput(@"
3.times { Integer === 1 }
events = []
tp = TracePoint.new(:c_call) { |t| events << t.method_id if t.method_id == :=== }
tp.enable { 3.times { Integer === 1 } }
p events
", @"
[:===, :===, :===]
");
        }

        /// <summary>
        /// is_a?, kind_of?, instance_of? and Module#=== of the values whose class RubyContext answers by type alone (nil,
        /// true, false, Integer, Float, Symbol, and plain String, Array and Hash): asked from call sites that have
        /// bound their rules already, they still see a singleton class made after that (extend), a module included
        /// into the class after that, and a subclass instance. The answers are CRuby's.
        /// </summary>
        public void CaseEquals3() {
            TestOutput(@"
module M; end
module N; end
def chk(o, m) = [o.is_a?(m), o.kind_of?(m), m === o, o.instance_of?(m)]
s = 'abc'; a = [1]; h = {a: 1}
vals = [s, a, h, 5, 2**40, 2**80, 1.5, :sym, nil, true]
3.times { vals.each { |v| raise v.inspect if chk(v, M) != [false, false, false, false] } }
s.extend(M); a.extend(M); h.extend(M)
p chk(s, M), chk(a, M), chk(h, M), chk('other', M), chk([], M), chk({}, M)
p chk(s, String), chk(a, Array), chk(h, Hash), s.class, a.class, h.class
Integer.include(N); Float.include(N); Symbol.include(N); String.include(N)
p chk(5, N), chk(2**40, N), chk(2**80, N), chk(1.5, N), chk(:a, N), chk('q', N), chk(s, N)
p chk(5, Integer), chk(2**80, Integer), chk(1.5, Float), chk(:a, Symbol), chk(nil, NilClass), chk(true, TrueClass)
nil.extend(M)
p chk(nil, M), chk(false, M)
class S2 < String; end
p chk(S2.new('x'), String), chk(S2.new('x'), S2)
p [5.class, (2**40).class, 1.5.class, :a.class, 'x'.class, [].class, {}.class, nil.class, false.class]
", @"
[true, true, true, false]
[true, true, true, false]
[true, true, true, false]
[false, false, false, false]
[false, false, false, false]
[false, false, false, false]
[true, true, true, true]
[true, true, true, true]
[true, true, true, true]
String
Array
Hash
[true, true, true, false]
[true, true, true, false]
[true, true, true, false]
[true, true, true, false]
[true, true, true, false]
[true, true, true, false]
[true, true, true, false]
[true, true, true, true]
[true, true, true, true]
[true, true, true, true]
[true, true, true, true]
[true, true, true, true]
[true, true, true, true]
[true, true, true, false]
[false, false, false, false]
[true, true, true, false]
[true, true, true, true]
[Integer, Integer, Float, Symbol, String, Array, Hash, NilClass, FalseClass]
");
        }

        /// <summary>
        /// Comparable#== as MRI's cmp_equal: a <=> result that is not nil is compared with 0 by asking it (rb_cmpint), and
        /// a <=> that compares the same pair again inside itself, or the default Kernel#<=> (which asks ==), gets false
        /// there rather than recursing until the stack runs out.
        /// </summary>
        public void ComparableEqual1() {
            TestOutput(@"
class Zero; def >(x) = false; def <(x) = false; end
class O; include Comparable; def <=>(o) = Zero.new; end
class S; include Comparable; def <=>(o) = 'x'; end
class Rec
  include Comparable
  attr_accessor :other
  def <=>(o) = (other == o.other) ? 0 : 1
end
a = Rec.new; b = Rec.new; a.other = a; b.other = b
p O.new == O.new
begin
  S.new == S.new
rescue ArgumentError => e
  p e.message
end
p a == b
c = Class.new { include Comparable }
p c.new == c.new
x = c.new
p x == x
p Comparable.instance_method(:==).parameters
", @"
true
""comparison of String with 0 failed""
false
false
true
[[:req]]
");
        }
    }
}

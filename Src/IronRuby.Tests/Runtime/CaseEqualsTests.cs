using System;
using IronRuby.Builtins;

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
    }
}

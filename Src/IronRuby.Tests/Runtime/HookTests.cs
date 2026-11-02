using System;

namespace IronRuby.Tests {
    public partial class Tests {
        /// <summary>
        /// method_added, const_added, inherited, singleton_method_added and the removed/undefined hooks. The runtime
        /// leaves a hook's call out while it would run the built-in empty method, so each user-defined one has to be
        /// found where a call would find it: on the class or its singleton class, inherited from a superclass's
        /// singleton class, in a module extended, included or prepended (into the singleton class, Module or Class),
        /// made by define_method or alias, or reached through method_missing once the hook is undefined. A removed hook
        /// goes back to the default. The expected output is CRuby 4.0's.
        /// </summary>
        public void Hooks1() {
            TestOutput(@"
$log = []
# 1. hook on the class itself
class A
  def self.method_added(n) = $log << [:A_added, n]
  def self.singleton_method_added(n) = $log << [:A_sadded, n]
  def self.method_removed(n) = $log << [:A_removed, n]
  def self.method_undefined(n) = $log << [:A_undefined, n]
  def self.const_added(n) = $log << [:A_const, n]
  def self.inherited(k) = $log << [:A_inherited, k.name]
  def foo; end
  def self.bar; end
  X = 1
  remove_method :foo
  def foo; end
  undef_method :foo
  attr_accessor :acc
  define_method(:dm) {}
  alias_method :dm2, :dm
end
class B < A
  def b; end
  Y = 2
end
class C < B; end
p $log; $log.clear

# 2. inherited singleton hooks reach subclasses; a plain class sees nothing
class D; def d; end; E = 1; def self.e; end; end
class F < D; end
p $log; $log.clear

# 3. prepend into a singleton class, with super into the default
module Pre
  def const_added(n) = ($log << [:Pre, self.name, n]; super)
end
class J; class << self; prepend Pre; end; K = 1; end
class L < J; M = 2; end
p $log; $log.clear

# 4. define_method / alias of a hook
class N
  define_singleton_method(:singleton_method_added) { |n| $log << [:N_dm, n] }
  def self.x; end
end
class O
  class << self
    def my_hook(n) = $log << [:O_alias, n]
    alias_method :singleton_method_removed, :my_hook
  end
  def self.y; end
  class << self; remove_method :y; end
end
p $log; $log.clear

# 5. extend with a hook module, objects' singleton methods
module SHook
  def singleton_method_added(n) = $log << [:SHook, n]
  def singleton_method_undefined(n) = $log << [:SHook_undef, n]
end
obj = Object.new
obj.extend(SHook)
def obj.zz; end
class << obj; undef_method :zz; end
plain = Object.new
def plain.qq; end
bo = BasicObject.new
def bo.rr; end
class BO < BasicObject
  def singleton_method_added(n) = ::Kernel.instance_method(:send).bind_call($log, :<<, [:BO, n])
end
bo2 = BO.new
def bo2.ss; end
p $log; $log.clear

# 6. undefined hook falls to method_missing
class P
  class << self
    undef_method :const_added
    def method_missing(name, *args) = ($log << [:P_mm, name, *args]; nil)
    def respond_to_missing?(*) = true
  end
  Q = 1
end
p $log; $log.clear

# 7. a removed user hook goes back to the default
class R
  def self.method_added(n) = $log << [:R, n]
  def a; end
  class << self; remove_method :method_added; end
  def b; end
end
p $log; $log.clear

# 8. visibility of the hook does not matter to the implicit call
class S
  class << self
    def method_added(n) = $log << [:S, n]
    private :method_added
  end
  def a; end
  class << self; protected :method_added; end
  def b; end
end
class T
  class << self; public :method_added, :const_added; end
  def a; end
  U = 1
end
p $log; $log.clear

# 9. Module.new / Class.new, hooks on the Module subclass
class MyMod < Module
  def const_added(n) = $log << [:MyMod, n]
end
m = MyMod.new
m.const_set(:V, 1)
Module.new { def x; end; W = 1 }
Class.new(A) { def k; end }
p $log.map { |e| e.map { |x| x.is_a?(Module) ? x.class : x } }; $log.clear

# 10. modules included into or prepended to Module and Class
module ModHook
  def method_added(n) = ($log << [:ModHook, self.name, n] if $hook3; super)
end
module ModHook2
  def const_added(n) = ($log << [:ModHook2, self.name, n] if $hook3; super)
end
module ClassHook
  def inherited(k) = ($log << [:ClassHook, self.name, k.name] if $hook3; super)
end
$hook3 = true
class Module; include ModHook2; end     # Module's own const_added comes first
class G; def g; end; G2 = 1; end
class Class; include ModHook; end       # ahead of Module#method_added for classes
class G; def g2; end; end
module H; def h; end; end
class Class; include ClassHook; end     # Class's own inherited comes first
class I < G; end
class Class; prepend ClassHook; end
class I2 < G; end
class Module; prepend ModHook2; end
module H; H2 = 1; end
$hook3 = false
p $log; $log.clear
", @"
[[:A_sadded, :singleton_method_added], [:A_sadded, :method_removed], [:A_sadded, :method_undefined], [:A_sadded, :const_added], [:A_sadded, :inherited], [:A_added, :foo], [:A_sadded, :bar], [:A_const, :X], [:A_removed, :foo], [:A_added, :foo], [:A_undefined, :foo], [:A_added, :acc], [:A_added, :acc=], [:A_added, :dm], [:A_added, :dm2], [:A_inherited, ""B""], [:A_added, :b], [:A_const, :Y], [:A_inherited, ""C""]]
[]
[[:Pre, ""J"", :K], [:Pre, ""L"", :M]]
[[:N_dm, :singleton_method_added], [:N_dm, :x], [:O_alias, :y]]
[[:SHook, :zz], [:SHook_undef, :zz], [:BO, :ss]]
[[:P_mm, :const_added, :Q]]
[[:R, :a]]
[[:S, :a], [:S, :b]]
[[:MyMod, :V], [:A_inherited, nil], [:A_added, :k]]
[[:ModHook, ""G"", :g2], [:ClassHook, ""G"", ""I2""], [:ModHook2, ""H"", :H2]]
");
        }

        /// <summary>
        /// CRuby reports a call of a default hook (Module#method_added, Class#inherited, ...) to a :c_call TracePoint.
        /// So must IronRuby, after the same hooks were left out while nothing was tracing, and it goes back to leaving
        /// them out once the TracePoint is disabled.
        /// </summary>
        public void Hooks2() {
            TestOutput(@"
# warm-up: the default hooks run untraced first
class W0; def a; end; X = 1; def self.b; end; end
class W1 < W0; end
hooks = %i[method_added method_removed method_undefined singleton_method_added singleton_method_removed
           singleton_method_undefined const_added inherited]
events = []
tp = TracePoint.new(:c_call, :c_return) { |t| events << [t.event, t.method_id, t.defined_class, (t.self.is_a?(Module) ? t.self.inspect : t.self.class)] if hooks.include?(t.method_id) }
tp.enable do
  class W0; def c; end; Y = 1; def self.d; end; remove_method :c; undef_method :a; end
  class W2 < W0; end
  o = Object.new
  def o.e; end
  o.singleton_class.send(:remove_method, :e)
  module WM; def f; end; end
end
events.each { |e| p e }
events.clear
class W3 < W0; def g; end; end
p events
", @"
[:c_call, :method_added, Module, ""W0""]
[:c_return, :method_added, Module, ""W0""]
[:c_call, :const_added, Module, ""W0""]
[:c_return, :const_added, Module, ""W0""]
[:c_call, :singleton_method_added, BasicObject, ""W0""]
[:c_return, :singleton_method_added, BasicObject, ""W0""]
[:c_call, :method_removed, Module, ""W0""]
[:c_return, :method_removed, Module, ""W0""]
[:c_call, :method_undefined, Module, ""W0""]
[:c_return, :method_undefined, Module, ""W0""]
[:c_call, :const_added, Module, ""Object""]
[:c_return, :const_added, Module, ""Object""]
[:c_call, :inherited, Class, ""W0""]
[:c_return, :inherited, Class, ""W0""]
[:c_call, :singleton_method_added, BasicObject, Object]
[:c_return, :singleton_method_added, BasicObject, Object]
[:c_call, :singleton_method_removed, BasicObject, Object]
[:c_return, :singleton_method_removed, BasicObject, Object]
[:c_call, :const_added, Module, ""Object""]
[:c_return, :const_added, Module, ""Object""]
[:c_call, :method_added, Module, ""WM""]
[:c_return, :method_added, Module, ""WM""]
[]
");
        }

        /// <summary>
        /// A class keeps what it found for each hook while its method version stays the same, so a hook that appears
        /// after the class was asked must bump that version: one added to a module already included into Class, Kernel's
        /// singleton_method_added, a superclass's singleton hook. A class whose method lookup cache is full keeps its
        /// answers too. The expected output is CRuby 4.0's.
        /// </summary>
        public void Hooks3() {
            TestOutput(@"
$log = []
class Foo; def a; end; X = 1; def self.s; end; end
class Bar < Foo; end
o = Object.new; def o.w; end

# 1. a hook added later to a module that is already included into Class
module LateM; end
class Class; include LateM; end
class Foo; def c; end; end
module LateM
  def method_added(n) = ($log << [:LateM, self, n]; super)
end
class Foo; def d; end; end
class Bar; def e; end; end
module LateM; remove_method :method_added; end
class Foo; def f; end; end
p $log; $log.clear

# 2. Kernel#singleton_method_added defined after singleton classes were asked
o2 = Object.new; def o2.x; end
module Kernel
  def singleton_method_added(n) = ($log << [:Kernel_sma, n] if $on)
end
$on = true
def o2.y; end
def o.z; end
class Foo; def self.t; end; end
$on = false
p $log; $log.clear

# 3. a superclass's singleton hook defined after the subclass was asked
class Sup; end
class Sub < Sup; def a; end; K1 = 1; end
def Sup.method_added(n) = $log << [:Sup, self, n]
def Sup.const_added(n) = $log << [:Sup_const, self, n]
class Sub; def b; end; K2 = 2; end
class << Sup; remove_method :method_added; end
class Sub; def c; end; K3 = 3; end
p $log; $log.clear

# 4. a class whose method lookup cache is full before its hooks are asked
Full = Class.new
300.times { |i| Full.respond_to?(:""zz#{i}"") }
Full.class_eval { def a; end }
def Full.method_added(n) = $log << [:Full, n]
Full.class_eval { def b; end; alias_method :c, :b }
class << Full; remove_method :method_added; end
Full.class_eval { def d; end }
class << Full; undef_method :method_added; end
begin
  Full.class_eval { def e; end }
rescue NoMethodError => e
  $log << [:nme, e.name]
end
p $log; $log.clear
", @"
[[:LateM, Foo, :d], [:LateM, Bar, :e]]
[[:Kernel_sma, :y], [:Kernel_sma, :z], [:Kernel_sma, :t]]
[[:Sup, Sub, :b], [:Sup_const, Sub, :K2], [:Sup_const, Sub, :K3]]
[[:Full, :b], [:Full, :c], [:nme, :method_added]]
");
        }

        /// <summary>
        /// A class's method lookup cache holds at most 256 names. A method found once it is full is not filed, but its
        /// version is reported all the same, since the method is registered for invalidation either way: #respond_to?
        /// folds the answer into a rule, and a hook's answer is kept (<see cref="Hooks3"/>). Both go stale with the
        /// method version. The expected output is CRuby 4.0's.
        /// </summary>
        public void MethodLookupCacheFull1() {
            TestOutput(@"
H = Class.new
300.times { |i| H.respond_to?(:""zz#{i}"") }
def H.foo; end
r = []
def chk(r) = r << [H.respond_to?(:foo), H.respond_to?(:bar), H.respond_to?(:inspect)]
3.times { chk(r) }
class << H; remove_method :foo; end
chk(r)
def H.bar; end
chk(r)
module M; def foo; end; end
H.extend(M)
chk(r)
class Object; def self.baz; end; end
r << H.respond_to?(:baz)
class << H; private :bar; end
chk(r)
class Module; undef_method :inspect; end rescue r << :err
chk(r)
p r
# respond_to? on instances of a class whose table is full
K = Class.new
o = K.new
300.times { |i| o.respond_to?(:""yy#{i}"") }
class K; def kk; end; end
s = []
3.times { s << o.respond_to?(:kk) }
class K; remove_method :kk; end
s << o.respond_to?(:kk)
class K; def kk; end; end
s << o.respond_to?(:kk)
p s
", @"
[[true, false, true], [true, false, true], [true, false, true], [false, false, true], [false, true, true], [true, true, true], true, [true, false, true], [true, false, false]]
[true, true, true, false, true]
");
        }

        /// <summary>
        /// A hook's call interns the name it is passed, and the runtime leaves the call out for a default hook: the name
        /// still has to be interned, since CRuby's Symbol.all_symbols lists every constant and method name. Constant names
        /// from eval'd code, const_set with a String and Struct.new reach no other place that would intern them. The
        /// expected output is CRuby 4.0's.
        /// </summary>
        public void Hooks4() {
            TestOutput(@"
interned = -> s { Symbol.all_symbols.any? { |y| y.to_s == s } }
names = []
n = ""Zq"" + ""EvalClass""; eval(""class #{n}; end""); names << n
n = ""Zq"" + ""EvalModule""; eval(""module #{n}; end""); names << n
n = ""Zq"" + ""EvalConst""; eval(""#{n} = 1""); names << n
n = ""Zq"" + ""CsetStr""; Object.const_set(n, 2); names << n
n = ""Zq"" + ""StructName""; Struct.new(n, :a); names << n
n = ""Zq"" + ""ModConst""; Module.new.const_set(n, 3); names << n
n = ""zq"" + ""_attr""; Class.new { attr_accessor n }; names << n << n + ""=""
n = ""zq"" + ""_sdef""; $o = Object.new; eval(""def $o.#{n}; end""); names << n
p names.map(&interned)
", @"
[true, true, true, true, true, true, true, true, true]
");
        }
    }
}

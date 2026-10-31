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
        [Options(Prism = true)]
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
        [Options(Prism = true)]
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
    }
}

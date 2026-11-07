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


namespace IronRuby.Tests {
    public partial class Tests {
        public void BasicObject1() {
            TestOutput(@"
class Class
  def f
    puts 'Class'
  end
end

class BasicObject
  class << self
    def f
      puts 'BO'
    end
  end
end

Object.f
", @"
BO
");            
        }

        /// <summary>
        /// Unqualified constant lookup doesn't fallback to Object on BasicObject and its subclasses.
        /// </summary>
        public void BasicObject2() {
            TestOutput(@"
module M
  R = 1
end

class BasicObject
  include ::M

  p defined?(R)

  X = 1
  p defined?(Object)
  class Z
    p defined?(Object)
  end
  
  module Q
    p defined?(Object)
  end
end

class BO < BasicObject
  p defined?(Object)  
end

class C
  p constants(true)
end
", @"
""constant""
nil
""constant""
""constant""
nil
[]
");
        }

        public void ClassDuplication1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  X = 'const'
  @@x = 'cls-var'
  def foo
    puts self.class 
    puts @@x
  end
end

D = C.dup
D.new.foo
puts D::X
");
            }, @"
D
cls-var
const
");
        }

        public void ClassDuplication2() {
            AssertOutput(delegate() {
                CompilerTest(@"
class Object
  def foo
    puts 'foo'
  end
end

O = Object.dup

class Object
  remove_method :foo
end

O.new.foo
");
            }, @"
foo
");
        }

        public void ClassDuplication3() {
            TestOutput(@"
S = String.dup
p S.ancestors
puts S.instance_methods(false) - String.instance_methods(false) == []
puts s = S.new('ghk')
puts s.reverse
", @"
[S, Comparable, Object, Kernel, BasicObject]
true
ghk
khg
");
        }

        /// <summary>
        /// BasicObject
        /// </summary>
        public void ClassDuplication4() {
            TestOutput(@"
O = Object.dup
p O.ancestors
p O.constants.include?(:Object)
BasicObject.dup rescue p $!
", @"
[O, Kernel, BasicObject]
true
#<TypeError: can't copy the root class>
");
        }

        public void ClassDuplication5() {
            AssertOutput(delegate() {
                CompilerTest(@"
require 'mscorlib'
p System::Collections.dup.constants.include?(:IEnumerable)
");
            }, @"
true
");
        }

        public void ClassDuplication6() {
            var output = @"
""foo""
[1, 2]
""default""
1..2
/foo/
nil
[:a, :b]
""foo""
";

            AssertOutput(delegate() {
                CompilerTest(@"
p String.dup.new('foo')
p Array.dup.new([1,2])
p Hash.dup.new('default')[1]
p Range.dup.new(1,2)
p Regexp.dup.new('foo')
p Module.dup.new.name
p Struct.dup.new(:a,:b).dup[1,2].dup.members
p Proc.dup.new { 'foo' }[]
");
            }, output);

            AssertOutput(delegate() {
                CompilerTest(@"
class S < String; end
class A < Array; end
class H < Hash; end
class R < Range; end
class RE < Regexp; end
class M < Module; end
class St < Struct; end
class P < Proc; end

p S.dup.new('foo')
p A.dup.new([1,2])
p H.dup.new('default')[1]
p R.dup.new(1,2)
p RE.dup.new('foo')
p M.dup.new.name
p St.dup.new(:a,:b).dup[1,2].dup.members
p P.dup.new { 'foo' }[]
");
            }, output);
        }

        public void ClassDuplication7() {
            TestOutput(@"
class C
  def foo
  end
end

D = C.dup
p D.instance_method(:foo)
", @"
#<UnboundMethod: D#foo() ClassDuplication7.rb:3>
");
        }

        public void Structs1() {
            AssertOutput(() => CompilerTest(@"
S = Struct.new(:f,:g)
class S
  alias set_f f=
end

s = S[1,2]
s.set_f rescue p $!
s.set_f(3)
puts s.f
s.set_f(4,5) rescue p $!
s.set_f(*[]) rescue p $!
s.set_f(*[6])
puts s.f
s.set_f(*[6,7]) rescue p $!
puts s.f(*[])
puts s.f(*[1]) rescue p $!
"
                ), @"
#<ArgumentError: wrong number of arguments (given 0, expected 1)>
3
#<ArgumentError: wrong number of arguments (given 2, expected 1)>
#<ArgumentError: wrong number of arguments (given 0, expected 1)>
6
#<ArgumentError: wrong number of arguments (given 2, expected 1)>
6
#<ArgumentError: wrong number of arguments (given 1, expected 0)>
");
        }

        public void MetaModules1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class Meta < Module
  def bar
    puts 'bar'
  end
end

M = Meta.new

module M
  def foo
    puts 'foo'
  end
end

class C
  include M
end

C.new.foo
M.bar
");
            }, @"
foo
bar
");
        }

        public void MetaModulesDuplication1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class Meta < Module
end

M = Meta.new
puts M.name
puts M.class

N = M.dup
puts N.name
puts N.class
");
            }, @"
M
Meta
N
Meta
");
        }

        /// <summary>
        /// Autoload removes the constant from the declaring module before it loads the target file.
        /// </summary>
        public void Autoload1() {
            var file = @"
class D 
  p defined? X
  X = 123
end
";
            using (_driver.MakeTempFile("file", ".rb", file)) {
                TestOutput(@"
class D
  autoload(:X, $file)
end

class C < D
  p X
end

", @"
nil
123
");
            }
        }

        /// <summary>
        /// Freezing and module initializers.
        /// Prism: 'yaml' is psych 5.3.1's own Ruby code now, and the legacy parser stops at its
        /// first keyword parameter ("syntax error, unexpected label") - the require failed that
        /// way before it could touch the frozen Object. CRuby 4.0.6 prints the same three lines.
        /// </summary>
        public void ModuleFreezing1() {
            TestOutput(@"
Float.freeze
puts defined? Float::EPSILON         # this should work, Float is a builtin
puts defined? 1.2.+

Object.freeze
begin
  require 'yaml'               # monkey-patches Object
rescue Exception
  p $!
end
", @"
constant
method
#<FrozenError: can't modify frozen Class: Object>
");
        }

        /// <summary>
        /// Tests recursive singleton freezing.
        /// </summary>
        public void ModuleFreezing2() {
            TestOutput(@"
[Module.new, Object.new].each do |obj|
  obj.freeze
  p obj.frozen?
  class << obj
    p frozen?
    class << self
      p frozen?
      class << self
        p frozen?
      end
    end
  end
end
", @"
true
true
true
true
true
true
true
true
");
        }

        public void InstanceVariables1() {
            TestOutput(@"
# class with no static instance variables
class A
end

# class with 1 static instance variable
class B < A
  def initialize
    @a = 1
  end
end

# class with 3 static instance variables (1 inherited)
class C < B
  def initialize
    super
    @b = 2
    @d = 3
  end
end

# class with 4 static instance variables (3 inherited)
class D < C
  def foo
    @c = @a + @b
  end

  def bar
    ""> #@d""                            # instance variable in string
  end
end

d = D.new
puts d.foo
puts d.bar
", @"
3
> 3
");
        }

        public void InstanceVariables2() {
            TestOutput(@"
module M
  attr_reader :m

  def setm
    @m = @a + @x
  end
end

class C
  include M

  def initialize
    @a, @b = 1, 2
  end

  def isdef
    defined?(@a)
  end
end

c = C.new
puts c.isdef
c.instance_variables.each { |x| p x.to_sym }
puts c.instance_variable_get(:@a)

c.instance_variable_set(:@a, 3)
puts c.instance_variable_get(:@a)

c.instance_variable_set(:@x, 10)
puts c.instance_variable_get(:@x)

c.setm
p c.m

p c.instance_variable_defined?(:@a)
p c.instance_variable_defined?(:@x)
p c.instance_variable_defined?(:@w)
", @"
instance-variable
:@a
:@b
1
3
10
13
true
true
false
");
        }

        /// <summary>
        /// Instance variable removal.
        /// </summary>
        public void InstanceVariables3() {
            TestOutput(@"
module M
  def dreport
    puts defined?(@a), defined?(@b), defined?(@c), defined?(@d), defined?(@w)
  end
end

class C
  include M

  def foo
    @a, @b = 1, 2
  end

  def sreport
    puts defined?(@a), defined?(@b)
  end
end

c = C.new
c.foo
c.instance_variable_set(:@c, 3)
c.instance_variable_set(:@d, 3)

p c.send(:remove_instance_variable, :@c)
p c.send(:remove_instance_variable, :@a)
c.send(:remove_instance_variable, :@c) rescue p $!
c.send(:remove_instance_variable, :@a) rescue p $!
c.send(:remove_instance_variable, :@w) rescue p $!
p c.instance_variable_get(:@a)
p c.instance_variable_get(:@c)
p c.instance_variable_get(:@w)
puts '---'
c.sreport
puts '---'
c.dreport
", @"
3
1
#<NameError: instance variable @c not defined>
#<NameError: instance variable @a not defined>
#<NameError: instance variable @w not defined>
nil
nil
nil
---

instance-variable
---

instance-variable

instance-variable

");
        }

        /// <summary>
        /// Object cloning.
        /// </summary>
        public void InstanceVariables4() {
            TestOutput(@"
class C 
  def foo
    @a, @b, @c = 1, 2, 3
  end

  def sreport
    p [@a, @b, @c]
  end

  def dreport
    eval('p [@a, @b, @c, @d]')
  end
end

c = C.new
c.foo

c.dup.sreport
c.dup.dreport
c.instance_variable_set(:@d, 4)
c.clone.sreport
c.clone.dreport
", @"
[1, 2, 3]
[1, 2, 3, nil]
[1, 2, 3]
[1, 2, 3, 4]
");
        }

        /// <summary>
        /// Subclasses of builtin types and CLR types.
        /// </summary>
        public void InstanceVariables5() {
            TestOutput(@"
class R < Range
  def foo
    @a = 1
    instance_variable_set(:@aa, 11)
  end
end

class S < String
  def foo
    @b = 2
    instance_variable_set(:@bb, 22)
  end
end

class T < System::Collections::ArrayList
  def foo
    @c = 3
    instance_variable_set(:@cc, 33)
  end
end

class U
  include System::IDisposable
  
  def foo
    @d = 4
    instance_variable_set(:@dd, 44)
  end
end

r, s, t, u = R.new(1,2), S.new('x'), T.new, U.new
r.foo
s.foo
t.foo
u.foo

puts r.instance_variable_get(:@a), s.instance_variable_get(:@b), t.instance_variable_get(:@c), u.instance_variable_get(:@d)
puts r.instance_variable_get(:@aa), s.instance_variable_get(:@bb), t.instance_variable_get(:@cc), u.instance_variable_get(:@dd)
", @"
1
2
3
4
11
22
33
44
");
        }

        /// <summary>
        /// Anonymous classes with a basse class that has static instance variables.
        /// </summary>
        public void InstanceVariables6() {
            TestOutput(@"
class C
  def initialize 
    @a = 1 
  end
end

D = Class.new(C)
p D.new.instance_variable_get(:@a)
", @"
1
");
        }

        public void InstanceVariables7() {
            TestOutput(@"
class C
  def initialize(value)
    @value = value
  end

  def foo(other)
    [@value, other.instance_eval{ @value }]
  end
end

a, b = C.new(1), C.new(2)
p a.foo(b)
", @"
[1, 2]
");
        }

        // An Integer holds no state at all, so it is frozen and an instance variable
        // cannot be set on it - IronRuby used to let one through, shared by every Integer
        // of the same value.
        public void InstanceVariables10() {
            AssertOutput(delegate() {
                CompilerTest(@"
class Integer
  def foo= a
    @x = a
  end
  def foo
    @x
  end
end

begin
  1.foo = 1
rescue => e
  puts e.class, e.message
end
puts 1.foo.inspect
");
            },
            @"
FrozenError
can't modify frozen Integer: 1
nil");
        }

        // Nor does a Float, for the same reason.
        public void InstanceVariables20() {
            TestOutput(@"
class Float
  def foo= a
    @x = a
  end
  def foo
    @x
  end
end

begin
  1.0.foo = 1
rescue => e
  puts e.class, e.message
end
puts 1.0.foo.inspect
", @"
FrozenError
can't modify frozen Float: 1.0
nil
");
        }
    
        /// <summary>
        /// instance_methods(false) lists what the module itself defines: not a prepended module's
        /// methods, and for a singleton class not the methods of the class behind it.
        /// </summary>
        public void InstanceMethods_NotInherited1() {
            TestOutput(@"
module P; def pm; end; def cm; end; end
class C; prepend P; def cm; end; def c2; end; end
class D < C; def dm; end; end
o = D.new
def o.sing; end
p C.instance_methods(false).sort, D.instance_methods(false), o.singleton_class.instance_methods(false)
p C.singleton_class.instance_methods(false)
class C; def self.cs; end; end
p C.singleton_class.instance_methods(false), D.singleton_class.instance_methods(false)
module M; def mm; end; private def mp; end; end
o.extend(M)
o.singleton_class.class_eval { private def spri; end; protected def spro; end }
class D; private def dpri; end; protected def dpro; end; end
p o.public_methods(false).sort, o.private_methods(false).sort, o.protected_methods(false).sort, o.methods(false).sort
p D.new.public_methods(false), D.new.private_methods(false), D.new.methods(false)
", @"
[:c2, :cm]
[:dm]
[:sing]
[]
[:cs]
[]
[:dm, :mm, :sing]
[:dpri, :mp, :spri]
[:dpro, :spro]
[:sing, :spro]
[:dm]
[:dpri]
[]
");
        }

        /// <summary>
        /// The method lists come out as MRI's: the CLR members of a built-in's underlying type (Object.ReferenceEquals,
        /// LocalJumpError#ExitValue, NilClass#GetType) and the __helpers__ a library defines are left out, though they
        /// stay callable. A CLR type lists its members, and a program's own __foo__ is listed.
        /// </summary>
        public void MethodLists_Unlisted1() {
            TestOutput(@"
class A; end
p A.singleton_methods, Object.singleton_methods(false)
p LocalJumpError.instance_methods(false).sort, SystemExit.instance_methods(false).sort
p Object.new.methods.grep(/\A[A-Z]/), nil.methods.grep(/\A[A-Z]/), A.methods.grep(/\A[A-Z]/)
p Kernel.private_instance_methods.grep(/\A__ir/), Kernel.singleton_methods.grep(/\A__ir/)
p Integer.singleton_methods.grep(/\A__/), Comparable.private_instance_methods.grep(/\A__/)
class B; def __foo__; end; private def __bar__; end; end
p B.instance_methods(false), B.private_instance_methods(false)
p Object.new.GetHashCode.is_a?(Integer), nil.GetType.is_a?(System::Type), Object.ReferenceEquals(nil, nil)
p System::Collections::ArrayList.instance_methods(false).include?(:add)
", @"
[]
[]
[:exit_value, :reason]
[:status, :success?]
[]
[]
[]
[]
[]
[]
[]
[:__foo__]
[:__bar__]
true
true
true
true
");
        }

        /// <summary>
        /// const_get and friends walk a scoped name as MRI does: a malformed path names the whole
        /// path, a bad segment just that segment, each only once the segments before it resolve;
        /// a segment after the first does not see Object's constants through the module's
        /// ancestry (rb_const_get_from). private_constant and friends check frozen-ness first.
        /// </summary>
        public void ConstantPaths1() {
            TestOutput(@"
X = 1; class C; Q = 1; end; module M; end
def t; yield; rescue => e; ""#{e.class}: #{e.message}""; end
p t { Object.const_get(""C::b"") }, t { Object.const_get(""::b"") }, t { Object.const_get(""C::"") }, t { Object.const_get(""C:::D"") }
p t { Object.const_get(""Zz::C:D"") }, t { Object.const_get(""X::Y"") }, t { Object.const_get("""") }, t { Object.const_get(:b) }
p t { Object.const_get(""C::String"") }, t { Object.const_get(""C::Kernel"") }, t { Object.const_get(""M::String"") }, Object.const_get(""Object::String""), C.const_get(""String"")
p Object.const_defined?(""C::String""), Object.const_defined?(""C::Q""), t { Object.const_defined?(""C::b"") }, Object.const_source_location(""C::String"")
module N; R = 1; end; N.freeze
p t { N.private_constant(:R) }, t { N.public_constant }, t { N.deprecate_constant(:R) }
p t { C.private_constant(:q) }, t { C.deprecate_constant(:q) }
", @"
""NameError: wrong constant name b""
""NameError: wrong constant name b""
""NameError: wrong constant name C::""
""NameError: wrong constant name C:::D""
""NameError: uninitialized constant Zz""
""TypeError: X::Y does not refer to class/module""
""NameError: wrong constant name ""
""NameError: wrong constant name b""
""NameError: uninitialized constant C::String""
""NameError: uninitialized constant C::Kernel""
""NameError: uninitialized constant M::String""
String
String
false
true
""NameError: wrong constant name b""
nil
""FrozenError: can't modify frozen Module: N""
""FrozenError: can't modify frozen Module: N""
""FrozenError: can't modify frozen Module: N""
""NameError: constant C::q not defined""
""NameError: constant C::q not defined""
");
        }

        /// <summary>
        /// Module#__hide_constant__ (IronRuby's own Ruby code keeps state MRI keeps in C in such constants): the
        /// code nested in the module reads the constant by name, as it reads a private one, while constant
        /// reflection - const_defined?, const_get, const_source_location, remove_const, the visibility setters,
        /// a qualified reference - finds nothing there. The helper itself is left out of the method lists.
        /// </summary>
        public void HiddenConstants1() {
            TestOutput(@"
module M
  A = 1; class K; end
  __hide_constant__ :A, :K
  def self.read = [A, K.name]
  class Inner; def read = A; end
end
module N; include M; end
def t; yield; rescue => e; ""#{e.class}: #{e.message}""; end
p M.read, M::Inner.new.read, M.constants.sort
p M.const_defined?(:A), M.const_defined?(:A, false), Object.const_defined?(""M::A""), N.const_defined?(:A)
p t { M::A }, t { M.const_get(:A) }, t { M.const_get(:A, false) }, t { N.const_get(:A) }, M.const_source_location(:A)
p t { M.send(:remove_const, :A) }, t { M.public_constant(:A) }, t { M.private_constant(:K) }, t { M.deprecate_constant(:A) }
p defined?(M::A), M.send(:__hide_constant__, :A).equal?(M), M.read
p M.private_methods.include?(:__hide_constant__), Module.private_instance_methods(false).include?(:__hide_constant__)
", @"
[1, ""M::K""]
1
[:Inner]
false
false
false
false
""NameError: uninitialized constant M::A""
""NameError: uninitialized constant M::A""
""NameError: uninitialized constant M::A""
""NameError: uninitialized constant N::A""
nil
""NameError: constant M::A not defined""
""NameError: constant M::A not defined""
""NameError: constant M::K not defined""
""NameError: constant M::A not defined""
nil
true
[1, ""M::K""]
false
false
");
        }

        /// <summary>
        /// What the prelude keeps for itself stays out of reflection, as MRI's C state does: its own constants
        /// are hidden (Process::SPAWN_*, GC::LATEST_GC_INFO, Random::MT, IO::BOMS__, an Errno class's Message),
        /// its instance variables use the hidden @__name__ form (Enumerator.new's generator, an
        /// ArithmeticSequence, Set, Random, KeyError, SignalException, StopIteration, Fiber, WeakKeyMap), the
        /// Enumerators without a block name their method (String#gsub, #upto, ObjectSpace.each_object), and
        /// methods are owned where MRI owns them (Rational/Complex#dup and #clone are Numeric's, Float has its
        /// #fdiv, #positive? and #negative?, Integer has Numeric's; nil, true and false have #===). Marshal
        /// still writes a Set's table as "@hash" and a KeyError's receiver and key, as MRI does, and reads them back.
        /// </summary>
        public void PreludeHiddenState1() {
            TestOutput(@"
require 'ruby4'
p Process.const_defined?(:SPAWN_DUP2), GC.const_defined?(:LATEST_GC_INFO), Enumerator.const_defined?(:SIZE_SAME_METHODS)
p Random.const_defined?(:MT), IO.constants.include?(:BOMS__), String.constants, Binding.constants, Errno::ENOENT.constants
p Errno::ENOENT.new.message, Class.new(Errno::EACCES).new('x').message
p Enumerator.new {}.instance_variables, (1..10).step(2).instance_variables, 1.step(10, 3).instance_variables
p Set[1].instance_variables, Set[1].dup.instance_variables, Random.new(1).instance_variables, Random.new(1).rand(100) == Random.new(1).rand(100)
p KeyError.new('x', receiver: nil).instance_variables, KeyError.new('x', receiver: nil).receiver, (KeyError.new('x').key rescue $!)
p SignalException.new('INT').instance_variables, SignalException.new('INT').signo, Interrupt.new.instance_variables
p (begin; [].each.next; rescue StopIteration => e; e.instance_variables; end), Fiber.new {}.instance_variables
p ObjectSpace::WeakKeyMap.new.instance_variables
p 'abc'.gsub(/b/), 'abc'.gsub('b').each { 'X' }, 'a'.upto('c'), 'a'.upto('c', true), ObjectSpace.each_object(Class).inspect
p Rational.instance_method(:dup).owner, Complex.instance_method(:clone).owner, (1r.clone(freeze: false) rescue $!), (1r.clone(freeze: 1) rescue $!)
p Float.instance_method(:fdiv).owner, Float.instance_method(:positive?).owner, Integer.instance_method(:positive?).owner, 1.5.fdiv(2), -0.0.negative?
p NilClass.instance_method(:===).owner, TrueClass.instance_method(:===).owner, nil === nil, false === nil, true === 1
p (1..10).step(3).begin, 1.step(10, 3).end, ((1..10) % 3).inspect, ((1..10) % 3).to_a
p Marshal.dump(Set[1]), Marshal.load(Marshal.dump(Set[1, 2])), Marshal.dump(KeyError.new('x', receiver: 1, key: 2))
k = Marshal.load(Marshal.dump(KeyError.new('x', receiver: 1, key: 2))); p k.receiver, k.key, k.instance_variables
", @"
false
false
false
false
false
[]
[]
[:Errno]
""No such file or directory""
""Permission denied - x""
[]
[]
[]
[]
[]
[]
true
[]
nil
#<ArgumentError: no key is available>
[]
2
[]
[]
[]
[]
#<Enumerator: ""abc"":gsub(/b/)>
""aXc""
#<Enumerator: ""a"":upto(""c"")>
#<Enumerator: ""a"":upto(""c"", true)>
""#<Enumerator: ObjectSpace:each_object(Class)>""
Numeric
Numeric
#<ArgumentError: can't unfreeze Rational>
#<ArgumentError: unexpected value for freeze: Integer>
Float
Float
Numeric
0.75
false
NilClass
TrueClass
true
false
false
1
10
""((1..10).%(3))""
[1, 4, 7, 10]
""\x04\bo:\bSet\x06:\n@hash}\x06i\x06TF""
Set[1, 2]
""\x04\bo:\rKeyError\t:\tmesgI\""\x06x\x06:\x06ET:\abt0:\rreceiveri\x06:\bkeyi\a""
1
2
[]
");
        }
    }
}

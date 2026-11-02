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

using IronRuby.Runtime;
using System;
namespace IronRuby.Tests {
    public partial class Tests {
        public void MainSingleton1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts self.to_s
");
            }, "main");
        }

        public void MainSingleton2() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  def foo
    puts 'foo'
  end
end

include M
foo
");
            }, "foo");
        }

        /// <summary>
        /// Ruby class.
        /// </summary>
        public void Singletons1A() {
            Singletons1_Test("C", null);
        }

        /// <summary>
        /// Object.
        /// </summary>
        public void Singletons1B() {
            Singletons1_Test("Object", null);
        }

        /// <summary>
        /// A subclass of a CLR class.
        /// </summary>
        public void Singletons1C() {
            Singletons1_Test("X", "< System::Collections::ArrayList");
        }

        /// <summary>
        /// A CLR class.
        /// </summary>
        public void Singletons1D() {
            Singletons1_Test("System::Collections::ArrayList", null);
        }

        public void Singletons1_Test(string/*!*/ className, string inherits) {
            TestOutput(String.Format(@"
class {0} {1}
end

x, y = {0}.new, {0}.new

class << x
  def foo; 1; end
end

class << y
  def foo; 2; end
end

p x.foo, y.foo
", className, inherits), 
@"
1
2
");
        }

        public void Singletons2() {
            TestOutput(@"
class C
  def i_C
    puts 'i_C'
  end

  def self.c_C
    puts 'c_C'
  end
end

x = C.new
y = C.new

class << x
  $x1 = self

  def i_x1
    puts 'i_x1'
  end

  def self.c_x1
    puts 'c_x1'
  end
end

x.i_x1
x.i_C

x.c_x1 rescue puts 'X'
x.c_C rescue puts 'X'
y.i_x1 rescue puts 'X'
",
@"
i_x1
i_C
X
X
X
");
        }

        public void Singletons3() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def foo
    puts 'C_foo'
  end   
end

class Class
  def foo
    puts 'Class_foo'
  end
end

x = C.new

class << x 
  $C1 = self
end

$C1.foo
");
            }, @"
Class_foo
");
        }

        public void SingletonCaching1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C  
  def foo
    puts 'foo'
  end
end

c = C.new
c.foo

class << c
  def foo
    puts 'bar'
  end
end
c.foo
");
            }, @"
foo
bar");
        }

        /// <summary>
        /// IRubyObjects.
        /// </summary>
        public void SingletonCaching2A() {
            AssertOutput(() => CompilerTest(@"
class C
end

foo = C.new
bar = C.new
def foo.to_s; 'ok'; end
puts bar.to_s
puts foo.to_s
"), @"
#<C:*>
ok
", OutputFlags.Match);
        }

        /// <summary>
        /// Object.
        /// </summary>
        public void SingletonCaching2B() {
            AssertOutput(() => CompilerTest(@"
foo = Object.new
bar = Object.new
def foo.to_s; 'ok'; end
puts bar.to_s
puts foo.to_s
"), @"
#<Object:*>
ok
", OutputFlags.Match);
        }

        /// <summary>
        /// CLR types.
        /// </summary>
        public void SingletonCaching2C() {
            TestOutput(@"
A = System::Collections::ArrayList
class A
  def to_s
    'base'
  end
end

foo, bar = A.new, A.new
puts bar.to_s
puts foo.to_s
def foo.to_s; 'singleton 1'; end
puts bar.to_s
puts foo.to_s
def foo.to_s; 'singleton 2'; end
[bar, foo, nil, 1].each { |x| puts x.to_s }
", @"
base
base
base
singleton 1
base
singleton 2

1
");
        }

        public void Scenario_ClassVariables_Singletons() {
            TestOutput(@"
class C
end

module M
  class << 'x'
    $Sx = self
    class << C
      $SC = self
      @@a = 1
      class_variable_set :@@ea, 1
    end
    @@b = 2
    class_variable_set :@@eb, 2
  end
  @@c = 3
  class_variable_set :@@em, 3
end

p M.class_variables.sort
p $SC.class_variables.sort
p $Sx.class_variables.sort
", @"
[:@@a, :@@b, :@@c, :@@em]
[:@@ea]
[:@@eb]
");
        }

        public void AllowedSingletons1() {
            TestOutput(@"
# a regexp literal is frozen, so defining a singleton method on one raises FrozenError,
# which is what CRuby 4.0.6 does too. Opening its singleton class still works, see
# AllowedSingletons2.
ok = ['x', true, false, nil]
error = [1 << 70, 1, 1.0, :foo, //]

ok.each do |x| 
  def x.foo; end 
end

error.each { |x| 
  begin
    def x.foo; end
  rescue
  else
    raise x.to_s
  end
}

puts 'ok'
",
@"ok"
);
        }

        public void AllowedSingletons2() {
            TestOutput(@"
# The same list as AllowedSingletons1 above, because the two questions are the same one:
# a Bignum and a Float hold no state to hang a singleton class on, whatever is being asked
# of it. CRuby 4.0.6 raises TypeError for all four.
ok = ['x', true, false, nil, //]
error = [1 << 70, 1.0, 1, :foo]

ok.each do |x| 
  class << x; end
end

error.each { |x| 
  begin
    class << x; end
  rescue
  else
    raise x.to_s
  end
}

puts 'ok'
",
@"ok"
);
        }

        public void SingletonMethodDefinitionOnSingletons1() {
            TestOutput(@"
def true.foo; 't'; end
def false.bar; 'f'; end
def nil.baz; 'n'; end

print true.foo, false.bar, nil.baz
", @"
tfn
");
        }

        /// <summary>
        /// Singleton(module)'s super-class is singleton(Module).
        /// </summary>
        public void ModuleSingletons1() {
            TestOutput(@"
class Class
  def self.c_c
    :c_c
  end 
end

class Module
  def self.c_m
    :c_m
  end 
end

module M
  class << self
    $SM = self
  end  
end 

$SM.method(:c_c) rescue p $!
$SM.c_c rescue p $!

p $SM.c_m
", @"
#<NameError: undefined method 'c_c' for class 'Class'>
#<NoMethodError: undefined method 'c_c' for class #<Class:M>>
:c_m
");
        }

        /// <summary>
        /// Cannot instantiate singleton(class).
        /// </summary>
        public void ClassSingletons1() {
            TestOutput(@"
class C; end
class << C
  p method(:new)
  new rescue p $!
end
", @"
#<Method: #<Class:#<Class:C>>(Class)#new(*)>
#<TypeError: can't create instance of virtual class>
");
        }

        private const string SingletonHelpers = @"
def get_singletons(cls, n)
  result = [cls]
  n.times do  
    cls = class << cls; self; end
    result << cls
  end
  result
end
";

        /// <summary>
        /// Class#superclass of a singleton class. The first level agrees with MRI now: the
        /// singleton class of a class has the singleton class of its superclass above it, and the
        /// singleton class of an object has the object's class. It used to answer the object's
        /// *metaclass* at every level, which is what this test recorded.
        ///
        /// The deeper levels build MRI's meta tower too (#&lt;Class:#&lt;Class:Object&gt;&gt; and so
        /// on); every superclass below is what CRuby 4.0 answers.
        /// </summary>
        public void DummySingletons1() {
            Engine.Execute(SingletonHelpers);
            AssertOutput(() =>
                CompilerTest(@"
[
  class C; self; end,
  module M; self; end,
  class MM < Module; new; end
].each do |c|
  get_singletons(c, 4).each do |s|
    printf '%-50s %s', s, s.superclass rescue print $!
    puts
  end
  puts
end
"), @"
C                                                  Object
#<Class:C>                                         #<Class:Object>
#<Class:#<Class:C>>                                #<Class:#<Class:Object>>
#<Class:#<Class:#<Class:C>>>                       #<Class:#<Class:#<Class:Object>>>
#<Class:#<Class:#<Class:#<Class:C>>>>              #<Class:#<Class:#<Class:#<Class:Object>>>>

undefined method 'superclass' for module M
#<Class:M>                                         Module
#<Class:#<Class:M>>                                #<Class:Module>
#<Class:#<Class:#<Class:M>>>                       #<Class:#<Class:Module>>
#<Class:#<Class:#<Class:#<Class:M>>>>              #<Class:#<Class:#<Class:Module>>>

undefined method 'superclass' for module #<MM:0x*>
#<Class:#<MM:0x*>>                           MM
#<Class:#<Class:#<MM:0x*>>>                  #<Class:MM>
#<Class:#<Class:#<Class:#<MM:0x*>>>>         #<Class:#<Class:MM>>
#<Class:#<Class:#<Class:#<Class:#<MM:0x*>>>>> #<Class:#<Class:#<Class:MM>>>
", OutputFlags.Match);
        }

        public void DummySingletons2() {
            Engine.Execute(SingletonHelpers);
            TestOutput(@"
class MetaModule < Module
end
MM = MetaModule.new

[Object, Module, Class, MetaModule].each do |c|
  s = class << c; self; end
  
  c.send(:define_method, :f) { c.name }
  s.send(:define_method, :f) { 'S(' + c.name + ')' }
end

[
  MM,
  module M; self; end,
  Module,
  MetaModule
].each do |c|
  get_singletons(c, 2).each do |s|                                      # the results differ from MRI for other values than 2, it seems like a bug in MRI
    printf '%-30s %s', s, s.f
    puts    
  end
  puts
end
", @"
MM                             MetaModule
#<Class:MM>                    S(MetaModule)
#<Class:#<Class:MM>>           S(Class)

M                              Module
#<Class:M>                     S(Module)
#<Class:#<Class:M>>            S(Class)

Module                         S(Module)
#<Class:Module>                S(Class)
#<Class:#<Class:Module>>       S(Class)

MetaModule                     S(MetaModule)
#<Class:MetaModule>            S(Class)
#<Class:#<Class:MetaModule>>   S(Class)
");
        }
        
    }
}

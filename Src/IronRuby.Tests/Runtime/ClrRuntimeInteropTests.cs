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
using System.Linq;

namespace IronRuby.Tests {
    public partial class Tests {
        #region Platform assemblies loaded on demand

        /// <summary>
        /// The class library is split into many assemblies on .NET Core. A CLR namespace member that lives
        /// in a platform assembly nothing has loaded yet is found without load_assembly.
        /// </summary>
        public void ClrPlatformAssemblies1() {
            Assert(!Context.DomainManager.GetLoadedAssemblyList().Contains(typeof(Console).Assembly));

            TestOutput(@"
p System::Console
p System::Linq::Enumerable.range(1, 3).to_a
s = System::Collections::Generic::Stack[Integer].new
s.push 1; s.push 2
p s.pop
d = System::Collections::Generic::SortedDictionary[String, Integer].new
d['b'] = 2; d['a'] = 1
p d.keys.to_a
p System::Collections::Concurrent::ConcurrentDictionary[String, Integer].new.count
p System::Text::RegularExpressions::Regex.new('b+').match('abbc').value
p System::Diagnostics::Process
p System::Xml::XmlDocument
p Microsoft::Win32::Registry
", @"
System::Console
[1, 2, 3]
2
[""a"", ""b""]
0
'bb'
System::Diagnostics::Process
System::Xml::XmlDocument
Microsoft::Win32::Registry
");

            // registered with the runtime like an explicitly loaded assembly:
            Assert(Context.DomainManager.GetLoadedAssemblyList().Contains(typeof(Console).Assembly));
        }

        /// <summary>
        /// defined?, const_defined? and const_get load the assembly the same way, a member that no platform assembly
        /// defines is still missing, and a removed member is not loaded back.
        /// </summary>
        public void ClrPlatformAssemblies2() {
            TestOutput(@"
def d; defined?(System::Console); end
p d, d
p System.const_defined?(:Console)
p System.const_get(:Console)
p defined?(System::NoSuchType), System.const_defined?(:NoSuchType)
2.times { System::NoSuchType rescue p $! }
System::Collections::Generic::NoSuchType rescue p $!
p System.constants.include?(:Console)
System.send(:remove_const, :Console)
p d, System.const_defined?(:Console)
", @"
""constant""
""constant""
true
System::Console
nil
false
#<NameError: uninitialized constant System::NoSuchType>
#<NameError: uninitialized constant System::NoSuchType>
#<NameError: uninitialized constant System::Collections::Generic::NoSuchType>
true
nil
false
");
        }

        /// <summary>
        /// Loading is restricted to CLR namespaces: the top level is an ordinary Ruby lookup, and a namespace
        /// path does not become a top-level constant.
        /// </summary>
        public void ClrPlatformAssemblies3() {
            TestOutput(@"
p defined?(Linq), defined?(Console)
p System::Linq
p defined?(Linq)
Linq rescue p $!
", @"
nil
nil
System::Linq
nil
#<NameError: uninitialized constant Linq>
");
        }

        /// <summary>
        /// An assembly loaded after a missing constant was cached at a defined? site makes the site see the new
        /// member (the load changes the constant version).
        /// </summary>
        public void ClrPlatformAssemblies4() {
            Context.ObjectClass.SetConstant("DummyLinqAssembly", typeof(System.Linq.Dummy).Assembly.FullName);
            TestOutput(@"
def d; defined?(System::Linq::Dummy); end
p d
load_assembly DummyLinqAssembly
p d
", @"
nil
""constant""
");
        }

        #endregion

        #region Default interface methods

        public interface IDefaultGreeter {
            string Name { get; }
            string Greet() => "Hello from " + Name;
            string Greet(string greeting) => greeting + " from " + Name;
            string Title => "Dr. " + Name;
        }

        public interface ILoudGreeter : IDefaultGreeter {
            string Shout() => Greet().ToUpperInvariant() + "!";
        }

        public class ClrDefaultGreeter : IDefaultGreeter {
            public string Name => "C#";
        }

        public class ClrOverridingGreeter : IDefaultGreeter {
            public string Name => "C#";
            public string Greet() => "Hi from C#";
        }

        public class ClrExplicitGreeter : IDefaultGreeter {
            public string Name => "C#";
            string IDefaultGreeter.Greet() => "Explicit hi";
        }

        public static class DefaultGreeterUser {
            public static string Greet(IDefaultGreeter g) => g.Greet();
            public static string Greet(IDefaultGreeter g, string greeting) => g.Greet(greeting);
            public static string Title(IDefaultGreeter g) => g.Title;
            public static string Shout(ILoudGreeter g) => g.Shout();
        }

        private void DefineDefaultGreeterConstants() {
            Context.ObjectClass.SetConstant("IGreeter", Context.GetModule(typeof(IDefaultGreeter)));
            Context.ObjectClass.SetConstant("ILoudGreeter", Context.GetModule(typeof(ILoudGreeter)));
            Context.ObjectClass.SetConstant("ClrGreeter", Context.GetClass(typeof(ClrDefaultGreeter)));
            Context.ObjectClass.SetConstant("ClrOverridingGreeter", Context.GetClass(typeof(ClrOverridingGreeter)));
            Context.ObjectClass.SetConstant("ClrExplicitGreeter", Context.GetClass(typeof(ClrExplicitGreeter)));
            Context.ObjectClass.SetConstant("User", Context.GetClass(typeof(DefaultGreeterUser)));
        }

        /// <summary>
        /// A Ruby class that includes an interface and doesn't implement a member with a default body gets the default
        /// body, called from C# through the interface as well as from Ruby.
        /// </summary>
        public void ClrDefaultInterfaceMethods1() {
            DefineDefaultGreeterConstants();
            TestOutput(@"
class G
  include IGreeter
  def name; 'Ruby'; end
end

g = G.new
puts User.greet(g), User.greet(g, 'Hey'), User.title(g)
puts g.greet, g.Greet, g.greet('Yo'), g.title
p g.respond_to?(:greet), G.instance_methods.map(&:to_s).include?('greet')
p IGreeter.instance_methods(false).map(&:to_s).sort
", @"
Hello from Ruby
Hey from Ruby
Dr. Ruby
Hello from Ruby
Hello from Ruby
Yo from Ruby
Dr. Ruby
true
true
[""greet"", ""title""]
");
        }

        /// <summary>
        /// A Ruby method overrides the default body for C# callers; super reaches the default body.
        /// </summary>
        public void ClrDefaultInterfaceMethods2() {
            DefineDefaultGreeterConstants();
            TestOutput(@"
class G
  include IGreeter
  def name; 'Ruby'; end
  def greet; super + '!'; end
  def title; 'Prof. ' + name; end
end

g = G.new
puts User.greet(g), g.greet, User.title(g)
", @"
Hello from Ruby!
Hello from Ruby!
Prof. Ruby
");
        }

        /// <summary>
        /// Default bodies of the interfaces the included one extends are found too.
        /// </summary>
        public void ClrDefaultInterfaceMethods3() {
            DefineDefaultGreeterConstants();
            TestOutput(@"
class G
  include ILoudGreeter
  def name; 'Ruby'; end
end

puts User.shout(G.new), G.new.shout, G.new.greet
", @"
HELLO FROM RUBY!
HELLO FROM RUBY!
Hello from Ruby
");
        }

        /// <summary>
        /// The default body calls members the Ruby class has to implement.
        /// </summary>
        public void ClrDefaultInterfaceMethods4() {
            DefineDefaultGreeterConstants();
            TestOutput(@"
class G
  include IGreeter
end

User.greet(G.new) rescue p $!.class, $!.name
G.new.greet rescue p $!.class, $!.name
", @"
NoMethodError
:Name
NoMethodError
:Name
");
        }

        /// <summary>
        /// A C# class that doesn't override a default interface method exposes it; an override or an explicit
        /// implementation still wins.
        /// </summary>
        public void ClrDefaultInterfaceMethods5() {
            DefineDefaultGreeterConstants();
            TestOutput(@"
c = ClrGreeter.new
puts c.greet, c.Greet, c.greet('Hey'), c.title
p c.respond_to?(:greet), c.methods.map(&:to_s).include?('greet')
puts ClrOverridingGreeter.new.greet, ClrOverridingGreeter.new.greet('Hey')
puts ClrExplicitGreeter.new.greet
", @"
Hello from C#
Hello from C#
Hey from C#
Dr. C#
true
true
Hi from C#
Hey from C#
Explicit hi
");
        }

        #endregion

        #region Overriding one overload of an overloaded virtual method

        public class Human {
            public string Log = "";
            public virtual void Sleep() { Sleep(8); }
            public virtual void Sleep(int hours) { Sleep(hours, "bed"); }
            public virtual void Sleep(int hours, string where) { Log += "Sleeping " + hours + " hours in " + where + ";"; }

            public virtual string Eat(string food) => "eat " + food;
            public virtual string Eat(string food, int count) => "eat " + count + " " + food;
            public string EatOne() => Eat("apple");
            public string EatTwo() => Eat("pear", 2);
        }

        public interface IAbstractOverloads {
            string Run(int a);
            string Run(int a, int b);
        }

        public static class AbstractOverloadsUser {
            public static string Run1(IAbstractOverloads x) => x.Run(1);
            public static string Run2(IAbstractOverloads x) => x.Run(1, 2);
        }

        /// <summary>
        /// A Ruby method that overrides an overloaded virtual method receives the virtual calls it can take; one it can't
        /// take by argument count goes to the CLR implementation of that overload.
        /// </summary>
        public void ClrOverloadOverride1() {
            Context.ObjectClass.SetConstant("Human", Context.GetClass(typeof(Human)));
            TestOutput(@"
class RubyDeveloper < Human
  def sleep
    super(14)
  end
end

d = RubyDeveloper.new
d.sleep
puts d.log

# called from Ruby the Ruby method replaced the name:
d.sleep(14) rescue p $!

class Napper < Human
  def sleep(hours)
    super(hours + 1)
  end
end

n = Napper.new
n.Sleep
puts n.log
", @"
Sleeping 14 hours in bed;
#<ArgumentError: wrong number of arguments (given 1, expected 0)>
Sleeping 9 hours in bed;
");
        }

        /// <summary>
        /// Optional and rest parameters count; an ArgumentError raised by a matching override propagates.
        /// </summary>
        public void ClrOverloadOverride2() {
            Context.ObjectClass.SetConstant("Human", Context.GetClass(typeof(Human)));
            TestOutput(@"
class A < Human
  def eat(food); ""A eats #{food}""; end
end
puts A.new.eat_one, A.new.eat_two

class B < Human
  def eat(*args); ""B eats #{args.inspect}""; end
end
puts B.new.eat_one, B.new.eat_two

class C < Human
  def eat(food, count = 1, extra = nil); ""C eats #{count} #{food}""; end
end
puts C.new.eat_one, C.new.eat_two

class D < Human
  def eat(food, count); raise ArgumentError, 'inner'; end
end
puts D.new.eat_one
D.new.eat_two rescue p $!

class E < Human
  define_method(:eat) { |food| ""E eats #{food}"" }
end
puts E.new.eat_one, E.new.eat_two
", @"
A eats apple
eat 2 pear
B eats ['apple']
B eats ['pear', 2]
C eats 1 apple
C eats 2 pear
eat apple
#<ArgumentError: inner>
E eats apple
eat 2 pear
");
        }

        /// <summary>
        /// An overload without an implementation has nothing to fall back to.
        /// </summary>
        public void ClrOverloadOverride3() {
            Context.ObjectClass.SetConstant("I", Context.GetModule(typeof(IAbstractOverloads)));
            Context.ObjectClass.SetConstant("User", Context.GetClass(typeof(AbstractOverloadsUser)));
            TestOutput(@"
class R
  include I
  def run(a); ""run #{a}""; end
end
puts User.run1(R.new)
User.run2(R.new) rescue p $!
", @"
run 1
#<ArgumentError: wrong number of arguments (given 2, expected 1)>
");
        }

        #endregion

        #region Name mangling: what #methods lists is callable

        public class IrregularNames {
            public string JrConnect() => "JrConnect";
            public string updateTransferData(int x) => "updateTransferData " + x;
            public string CreateQrCode(string s) => "CreateQrCode " + s;
            public string CreateQRCodeImage() => "CreateQRCodeImage";
            public string QrValue { get; set; } = "v";
            public string PyPy() => "PyPy";
            public static string UTF8 => "UTF8";
            public static string JrStatic() => "JrStatic";
            public int Xx = 5;
            public string IPAddress() => "IPAddress";
            public string SHA() => "SHA";
            public string SH() => "SH";
            public string GetID() => "GetID";
            public event Action OnJrEvent;
            public void Fire() { OnJrEvent?.Invoke(); }
            public virtual string JrVirtual() => "JrVirtual";
            public string CallJrVirtual() => JrVirtual();
            public static IrregularNames operator +(IrregularNames a, IrregularNames b) => a;
        }

        public class IrregularNamesSub : IrregularNames {
            public string JrConnect(int x) => "JrConnect " + x;
        }

        public class CollidingNames {
            public string CreateQRCode() => "CreateQRCode";
            public string CreateQrCode() => "CreateQrCode";
        }

        /// <summary>
        /// Mangling isn't one-to-one: JrConnect and JRConnect both mangle to jr_connect, which unmangles to JRConnect.
        /// A member is callable by the name #methods lists it under.
        /// </summary>
        public void ClrNameMangling1() {
            Context.ObjectClass.SetConstant("C", Context.GetClass(typeof(IrregularNames)));
            Context.ObjectClass.SetConstant("D", Context.GetClass(typeof(IrregularNamesSub)));
            TestOutput(@"
c = C.new
puts c.jr_connect, c.update_transfer_data(1), c.create_qr_code('x'), c.create_qr_code_image
puts c.qr_value
c.qr_value = 'w'
puts c.qr_value, c.py_py, C.ut_f8, C.jr_static, c.xx
c.xx = 6
puts c.xx, c.ip_address, c.SHA, c.sh, c.GetID
c.on_jr_event { puts 'fired' }
c.fire
puts c.method(:jr_connect).call, c.respond_to?(:update_transfer_data), c.JrConnect
puts D.new.jr_connect, D.new.jr_connect(2)

class R < C
  def jr_virtual; 'Ruby jr_virtual'; end
end
puts R.new.call_jr_virtual
", @"
JrConnect
updateTransferData 1
CreateQrCode x
CreateQRCodeImage
v
w
PyPy
UTF8
JrStatic
5
6
IPAddress
SHA
SH
GetID
fired
JrConnect
true
JrConnect
JrConnect
JrConnect 2
Ruby jr_virtual
");
        }

        /// <summary>
        /// Every CLR name #methods lists, on an instance or on a class, answers respond_to? and can be called; operators
        /// are listed for instances only, which is where they are invoked.
        /// </summary>
        public void ClrNameMangling2() {
            Context.ObjectClass.SetConstant("C", Context.GetClass(typeof(IrregularNames)));
            TestOutput(@"
def check(obj)
  obj.methods.select { |m| m.is_a?(IronRuby::Clr::Name) }.reject { |m| obj.respond_to?(m.to_s) && obj.method(m.to_s) }.map(&:to_s)
end

p check(C.new), check(C)
p check(System::Text::Encoding), check(System::Uri.new('http://x/')), check(System::DateTime), check(System::DateTime.new(1))
p C.new.methods.include?(:+), C.methods.include?(:+)
p C.new.methods.include?(:jr_connect), C.new.methods.include?(:update_transfer_data), C.methods.include?(:ut_f8)

# a name is listed once, for the member a call reaches (List#Count, not Enumerable#count):
l = System::Collections::Generic::List[Integer].new
names = l.methods.map(&:to_s)
p names.size == names.uniq.size, l.methods.find { |m| m == :count }.class
", @"
[]
[]
[]
[]
[]
[]
true
false
true
true
true
true
IronRuby::Clr::Name
");
        }

        /// <summary>
        /// When two members mangle to the same name, the one the name unmangles to wins.
        /// </summary>
        public void ClrNameMangling3() {
            Context.ObjectClass.SetConstant("C", Context.GetClass(typeof(CollidingNames)));
            TestOutput(@"
puts C.new.create_qr_code, C.new.CreateQrCode
", @"
CreateQRCode
CreateQrCode
");
        }

        /// <summary>
        /// IronRuby::Clr::Name.mangle and unmangle keep their rules; mangle is not one-to-one.
        /// </summary>
        public void ClrNameMangling4() {
            TestOutput(@"
N = IronRuby::Clr::Name
%w[JrConnect JRConnect updateTransferData CreateQRCode CreateQrCode IPAddress SHA SH Id ID GetID IPv6 Xx MyDB PyPy UTF8].each do |n|
  m = N.mangle(n)
  puts ""#{n} #{m.inspect} #{m ? N.unmangle(m).inspect : '-'}""
end
", @"
JrConnect ""jr_connect"" ""JRConnect""
JRConnect ""jr_connect"" ""JRConnect""
updateTransferData ""update_transfer_data"" ""UpdateTransferData""
CreateQRCode ""create_qr_code"" ""CreateQRCode""
CreateQrCode ""create_qr_code"" ""CreateQRCode""
IPAddress ""ip_address"" ""IPAddress""
SHA nil -
SH ""sh"" ""SH""
Id ""id"" ""Id""
ID nil -
GetID nil -
IPv6 ""i_pv6"" ""IPv6""
Xx ""xx"" ""XX""
MyDB ""my_db"" ""MyDB""
PyPy ""py_py"" nil
UTF8 ""ut_f8"" nil
");
        }

        #endregion

        #region Extension methods of instantiated generic interfaces

        /// <summary>
        /// An extension of an instantiated interface (Sum(this IEnumerable&lt;int&gt;)) applies to every type implementing it,
        /// next to the generic extensions of the interface definition (Sum&lt;T&gt;(this IEnumerable&lt;T&gt;, Func&lt;T, int&gt;)).
        /// </summary>
        public void ClrClosedInterfaceExtensions1() {
            LegacyExtensionsTestOutput(@"
L = System::Collections::Generic::List
l = L[Integer].new
l.add(3)
l.add(4)
p l.respond_to?(:average)

using_clr_extensions System::Linq
p l.method(:sum).owner
p l.sum, l.Sum, l.max, l.min, l.average
p l.sum(System::Func[Integer, Integer].new { |x| x * 2 })

p System::Array[Integer].new([5, 6]).sum
s = System::Collections::Generic::HashSet[Integer].new
s.add(1)
s.add(9)
p s.max
d = L[System::Double].new
d.add(1.5)
d.add(2.0)
p d.average
", @"
false
System::Collections::Generic::List[Integer]
7
7
4
3
3.5
14
11
9
1.75
");
        }

        /// <summary>
        /// Overloads of an extension spread over several interfaces are one group; an extension of an instantiation the
        /// type doesn't implement is not in it.
        /// </summary>
        public void ClrClosedInterfaceExtensions2() {
            Runtime.LoadAssembly(typeof(IronRubyTests.ClosedInterfaceExtensions.Extensions).Assembly);
            LegacyExtensionsTestOutput(@"
using_clr_extensions IronRubyTests::ClosedInterfaceExtensions
L = System::Collections::Generic::List
ints = L[Integer].new
ints.add(1)
strs = L[System::String].new
strs.add('a'.to_clr_string)

p ints.describe, strs.describe, System::Array[Integer].new(2).describe
p ints.describe(System::Func[Integer, System::String].new { |x| x.to_s })
p ints.total
strs.total rescue p $!.class
p L[Object].new.describe(System::Func[Object, System::String].new { |x| x.to_s })
", @"
'ints'
'strings'
'ints'
'generic 1'
1
NoMethodError
'generic 0'
");
        }

        #endregion
    }
}

namespace IronRubyTests.ClosedInterfaceExtensions {
    public static class Extensions {
        public static string Describe(this System.Collections.Generic.IEnumerable<int> xs) => "ints";
        public static string Describe(this System.Collections.Generic.IEnumerable<string> xs) => "strings";
        public static string Describe<T>(this System.Collections.Generic.IEnumerable<T> xs, Func<T, string> f) => "generic " + xs.Count();
        public static int Total(this System.Collections.Generic.IEnumerable<int> xs) => xs.Sum();
    }
}

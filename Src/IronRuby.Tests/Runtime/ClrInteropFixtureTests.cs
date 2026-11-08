/* ****************************************************************************
 *
 * Interop behaviour the old Tests/Interop/net mspec suite checked against C# fixtures it compiled
 * from heredocs: fields by visibility, Ruby classes implementing .NET interfaces (overloads, generic
 * methods, method_missing, explicit generic interfaces), interfaces of non-public classes, abstract
 * and optional-parameter constructors, and samples of the method binding matrices. The fixtures are
 * in IronRubyTests.InteropFixtures below; each test loads this assembly and includes that namespace.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IronRubyTests.InteropFixtures {
#pragma warning disable 414, 169, 649, 67
    #region Fields

    public class ClassWithFields {
        public string field = "field";
        public const string constField = "const";
        public readonly string readOnlyField = "readonly";
        public static string staticField = "static";
        public static readonly string staticReadOnlyField = "static readonly";

        private string privateField = "private field";
        private const string privateConstField = "private const";
        private readonly string privateReadOnlyField = "private readonly";
        private static string privateStaticField = "private static";
        private static readonly string privateStaticReadOnlyField = "private static readonly";

        protected string protectedField = "protected field";
        protected const string protectedConstField = "protected const";
        protected readonly string protectedReadOnlyField = "protected readonly";
        protected static string protectedStaticField = "protected static";
        protected static readonly string protectedStaticReadOnlyField = "protected static readonly";

        internal string internalField = "internal field";
    }

    internal class InternalClassWithMethods {
        internal int Foo() { return 1; }
        private int Bar() { return 2; }
    }

    #endregion

    #region Interfaces

    public interface IDoFoo {
        int Foo(string str);
        int Foo(int i);
        int Foo(string str, int i);
    }

    public interface IDoStuff {
        int StuffFoo(int foo);
        string StuffBar(int bar);
    }

    public static class ConsumeIDoFoo {
        public static int ConsumeFoo1(IDoFoo foo) { return foo.Foo("hello"); }
        public static int ConsumeFoo2(IDoFoo foo) { return foo.Foo(1); }
        public static int ConsumeFoo3(IDoFoo foo) { return foo.Foo("hello", 1); }
    }

    public static class ConsumeIDoStuff {
        public static int ConsumeStuffFoo(IDoStuff stuff) { return stuff.StuffFoo(1); }
        public static string ConsumeStuffBar(IDoStuff stuff) { return stuff.StuffBar(2); }
    }

    public interface IInterface { void m(); }

    public class ImplementsIInterface : IInterface {
        public void m() { }
    }

    public class DerivedFromImplementsIInterface : ImplementsIInterface { }

    public interface IHaveGenerics {
        T GenericsHere<T>(string arg1);
        T MoreGenericsHere<T, S>(S x);
    }

    public static class EatIHaveGenerics {
        public static string TestGenericsHere(IHaveGenerics ihg) { return ihg.GenericsHere<string>("test"); }
        public static string TestMoreGenericsHere(IHaveGenerics ihg) { return ihg.MoreGenericsHere<string, int>(1); }
    }

    public interface I1 { string M(); }
    public interface I2 { string M(); }
    public interface I3<T> { string M(); }
    public interface I4 { string M(int arg); }

    public class ClassI3Obj : I3<object> {
        string I3<object>.M() { return "I3<object>.M"; }
        public string M() { return "class M"; }
    }

    public class ClassI1I2I3Obj : I1, I2, I3<object> {
        string I1.M() { return "I1.M"; }
        string I2.M() { return "I2.M"; }
        string I3<object>.M() { return "I3<object>.M"; }
        public string M() { return "class M"; }
    }

    public class ClassI3_1<T> : I3<T> {
        string I3<T>.M() { return "I3<T>.M"; }
        public string M() { return "class M"; }
    }

    public class ClassI3_2<T> : I3<T> {
        string I3<T>.M() { return "I3<T>.M"; }
    }

    public class ClassI3ObjI3Int : I3<object>, I3<int> {
        string I3<object>.M() { return "I3<object>.M"; }
        string I3<int>.M() { return "I3<int>.M"; }
    }

    public class ClassI1I4 : I1, I4 {
        string I1.M() { return "I1.M"; }
        string I4.M(int arg) { return "I4.M"; }
    }

    public class PublicEventArgs : EventArgs { }
    internal class PrivateEventArgs : PublicEventArgs { }
    public delegate IPublicInterface PublicDelegateType(IPublicInterface sender, PublicEventArgs args);

    public interface IPublicInterface {
        IPublicInterface Hello { get; set; }
        void Foo(IPublicInterface f);
        IPublicInterface RetInterface();
        event PublicDelegateType MyEvent;
        IPublicInterface FireEvent(PublicEventArgs args);
        PublicEventArgs GetEventArgs();
    }

    internal class PrivateClass : IPublicInterface {
        public IPublicInterface Hello { get { return this; } set { } }
        public void Foo(IPublicInterface f) { }
        public IPublicInterface RetInterface() { return this; }
        public event PublicDelegateType MyEvent;
        public IPublicInterface FireEvent(PublicEventArgs args) { return MyEvent(this, args); }
        public PublicEventArgs GetEventArgs() { return new PrivateEventArgs(); }
    }

    public class PublicIPublicInterface : IPublicInterface {
        private PublicDelegateType _myEvent;
        IPublicInterface IPublicInterface.Hello { get { return this; } set { } }
        void IPublicInterface.Foo(IPublicInterface f) { }
        IPublicInterface IPublicInterface.RetInterface() { return this; }
        event PublicDelegateType IPublicInterface.MyEvent { add { _myEvent += value; } remove { _myEvent -= value; } }
        IPublicInterface IPublicInterface.FireEvent(PublicEventArgs args) { return _myEvent(this, args); }
        PublicEventArgs IPublicInterface.GetEventArgs() { return new PublicEventArgs(); }
    }

    public static class InterfaceOnlyTest {
        public static IPublicInterface PrivateClass { get { return new PrivateClass(); } }
    }

    #endregion

    #region Classes

    public class Klass {
        public int m() { return 1; }
    }

    public abstract class EmptyAbstractClass { }
    public abstract class AbstractClass { public abstract int m(); }

    public class DerivedFromAbstract : AbstractClass {
        public override int m() { return 1; }
    }

    public abstract class AbstractDerived : Klass { }

    public abstract class AbstractWithBody {
        public abstract string Abstract();
        public virtual string Virtual() { return "clr virtual"; }

        [return: MarshalAs(UnmanagedType.U1)]
        public virtual bool Marshalled() { return true; }

        public string CallAbstract() { return Abstract(); }
        public string CallVirtual() { return Virtual(); }
    }

    public class OverloadedConstructorClass {
        public string val;
        public OverloadedConstructorClass() { val = "empty constructor"; }
        public OverloadedConstructorClass(string str) { val = "string constructor"; }
        public OverloadedConstructorClass(string str, int i) { val = "string int constructor"; }
    }

    public class ClassWithOptionalConstructor {
        public int Arg { get; set; }
        public ClassWithOptionalConstructor([Optional]int arg) { Arg = arg; }
    }

    public class ClassWithDefaultConstructor {
        public string Value { get; set; }
        public ClassWithDefaultConstructor(string value = "default") { Value = value; }
    }

    public class GenericClass<T> { public int m() { return 1; } }
    public interface IGenericInterface<T> { }

    #endregion

    namespace RegressionSpecs {
        public class B { }
        public class C : B { }
        public interface I1 { int f(); }
        public interface I2 { int g(); }
    }

    #region Method binding

    public struct CStruct { public int m() { return 1; } }

    public struct StructImplementsIInterface : IInterface { public void m() { } }

    public enum CustomEnum { A, B, C }
    public enum EnumInt : int { A, B, C }

    public delegate int IntIntDelegate(int arg);

    /// <summary>
    /// Each method records what it received in Tracker and returns its own name.
    /// </summary>
    public class ClassWithMethods {
        public ArrayList Tracker { get; } = new ArrayList();

        public string NoArg() { return "NoArg"; }
        public string ObjectArg(object arg) { Tracker.Add(arg); return "ObjectArg"; }
        public string BooleanArg(bool arg) { Tracker.Add(arg); return "BooleanArg"; }
        public string StringArg(string arg) { Tracker.Add(arg); return "StringArg"; }
        public string CharArg(char arg) { Tracker.Add(arg); return "CharArg"; }
        public string Int32Arg(int arg) { Tracker.Add(arg); return "Int32Arg"; }
        public string DoubleArg(double arg) { Tracker.Add(arg); return "DoubleArg"; }
        public string SingleArg(float arg) { Tracker.Add(arg); return "SingleArg"; }
        public string DecimalArg(decimal arg) { Tracker.Add(arg); return "DecimalArg"; }

        public string IInterfaceArg(IInterface arg) { Tracker.Add(arg); return "IInterfaceArg"; }
        public string ImplementsIInterfaceArg(ImplementsIInterface arg) { Tracker.Add(arg); return "ImplementsIInterfaceArg"; }
        public string CStructArg(CStruct arg) { Tracker.Add(arg); return "CStructArg"; }
        public string StructImplementsIInterfaceArg(StructImplementsIInterface arg) { Tracker.Add(arg); return "StructImplementsIInterfaceArg"; }
        public string AbstractClassArg(AbstractClass arg) { Tracker.Add(arg); return "AbstractClassArg"; }
        public string CustomEnumArg(CustomEnum arg) { Tracker.Add(arg); return "CustomEnumArg"; }
        public string EnumIntArg(EnumInt arg) { Tracker.Add(arg); return "EnumIntArg"; }

        public string ObjectArrArg(object[] arg) { Tracker.Add(arg); return "ObjectArrArg"; }
        public string IInterfaceArrArg(IInterface[] arg) { Tracker.Add(arg); return "IInterfaceArrArg"; }
        public string ParamsIInterfaceArrArg(params IInterface[] arg) { Tracker.Add(arg); return "ParamsIInterfaceArrArg"; }
        public string ParamsCStructArrArg(params CStruct[] arg) { Tracker.Add(arg); return "ParamsCStructArrArg"; }
        public string IListOfObjArg(IList<object> arg) { Tracker.Add(arg); return "IListOfObjArg"; }
        public string IListOfCharArg(IList<char> arg) { Tracker.Add(arg.Count); return "IListOfCharArg"; }
        public string IListArg(IList arg) { Tracker.Add(arg.Count); return "IListArg"; }
        public string IEnumerableArg(IEnumerable arg) { Tracker.Add(arg); return "IEnumerableArg"; }
        public string IDictionaryOfObjectObjectArg(IDictionary<object, object> arg) { Tracker.Add(arg); return "IDictionaryOfObjectObjectArg"; }
        public string IDictionaryOfIntStringArg(IDictionary<int, string> arg) { Tracker.Add(arg); return "IDictionaryOfIntStringArg"; }

        public string IEnumerableIteratingArg(IEnumerable arg) {
            foreach (var item in arg) Tracker.Add(item);
            return "IEnumerableIteratingArg";
        }

        public string IEnumerableOfCharIteratingArg(IEnumerable<char> arg) {
            foreach (var item in arg) Tracker.Add(item);
            return "IEnumerableOfCharIteratingArg";
        }

        public string DelegateArg(Delegate arg) { Tracker.Add(((IntIntDelegate)arg)(10)); return "DelegateArg"; }
        public string IntIntDelegateArg(IntIntDelegate arg) { Tracker.Add(arg(10)); return "IntIntDelegateArg"; }
        public string RefBooleanArg(ref bool arg) { Tracker.Add(arg); return "RefBooleanArg"; }
        public string GenericArg<T>(T arg) { Tracker.Add(arg); return String.Format("GenericArg[{0}]", typeof(T)); }
    }

    #endregion
#pragma warning restore 414, 169, 649, 67
}

namespace IronRuby.Tests {
    public partial class Tests {
        private void LoadInteropFixtures() {
            Runtime.LoadAssembly(typeof(IronRubyTests.InteropFixtures.ClassWithFields).Assembly);
        }

        private const string InteropFixturesPrologue = @"
include IronRubyTests::InteropFixtures
def t
  yield
rescue Exception => e
  e.class
end
";

        /// <summary>
        /// Public and protected fields, instance and static, read and written; const and readonly fields have
        /// no setter. Private and internal fields are not visible without PrivateBinding. (fields/access_spec.rb,
        /// method/invocation/internal_spec.rb)
        /// </summary>
        public void ClrInterop_Fields1() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + @"
C = ClassWithFields
o = C.new
p o.field, C.constField, o.readOnlyField, C.staticField, C.staticReadOnlyField
p o.protectedField, C.protectedConstField, o.protectedReadOnlyField, C.protectedStaticField, C.protectedStaticReadOnlyField
o.field = 'bar'
o.send(:field=, 'foo')
C.staticField = 'baz'
o.protectedField = 'qux'
C.protectedStaticField = 'quux'
p o.field, C.staticField, o.protectedField, C.protectedStaticField
p [t { C.constField = 'x' }, t { o.readOnlyField = 'x' }, t { C.staticReadOnlyField = 'x' }]
p [t { C.protectedConstField = 'x' }, t { o.protectedReadOnlyField = 'x' }, t { C.protectedStaticReadOnlyField = 'x' }]
p [t { o.privateField }, t { C.privateConstField }, t { o.internalField }, t { o.internalField = 'x' }]
p t { InternalClassWithMethods }
", @"
'field'
'const'
'readonly'
'static'
'static readonly'
'protected field'
'protected const'
'protected readonly'
'protected static'
'protected static readonly'
'foo'
'baz'
'qux'
'quux'
[NoMethodError, NoMethodError, NoMethodError]
[NoMethodError, NoMethodError, NoMethodError]
[NoMethodError, NoMethodError, NoMethodError, NoMethodError]
NameError
");
        }

        /// <summary>
        /// With PrivateBinding private and internal fields and methods are visible, and writable unless const or
        /// readonly. Registered after ClrInterop_Fields1, which looks the same fields up without PrivateBinding:
        /// the cache of failed CLR member lookups is shared by all runtimes in the process and used to hand that
        /// failure on to this runtime.
        /// </summary>
        [Options(PrivateBinding = true)]
        public void ClrInterop_Fields2() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + @"
C = ClassWithFields
o = C.new
p o.privateField, C.privateConstField, o.privateReadOnlyField, C.privateStaticField, C.privateStaticReadOnlyField, o.internalField
o.privateField = 'bar'
C.privateStaticField = 'baz'
o.internalField = 'qux'
p o.privateField, C.privateStaticField, o.internalField
p [t { C.privateConstField = 'x' }, t { o.privateReadOnlyField = 'x' }, t { C.privateStaticReadOnlyField = 'x' }]
p InternalClassWithMethods.new.foo, InternalClassWithMethods.new.bar
", @"
'private field'
'private const'
'private readonly'
'private static'
'private static readonly'
'internal field'
'bar'
'baz'
'qux'
[NoMethodError, NoMethodError, NoMethodError]
1
2
");
        }

        /// <summary>
        /// A Ruby class implementing .NET interfaces, called from C#: one Ruby method serves every overload of an
        /// interface method, generic interface methods dispatch to it, an unimplemented method is a NoMethodError
        /// on either side, and method_missing receives the CLR name when C# calls. (interface/implementation_spec.rb,
        /// interface/reflection_spec.rb, interface/mapping_spec.rb)
        /// </summary>
        public void ClrInterop_InterfaceImplementation1() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + @"
class RubyDoesStuff
  include IDoStuff
  def stuff_foo(foo) = foo
  def stuff_bar(bar) = bar.to_s
end
class RubyDoesFoo
  include IDoFoo
  def foo(str, i = 1) = i
end
s, f = RubyDoesStuff.new, RubyDoesFoo.new
p ConsumeIDoStuff.ConsumeStuffFoo(s), ConsumeIDoStuff.ConsumeStuffBar(s)
p [ConsumeIDoFoo.ConsumeFoo1(f), ConsumeIDoFoo.ConsumeFoo2(f), ConsumeIDoFoo.ConsumeFoo3(f)]

class RubyDoesNothing
  include IDoFoo
  include IDoStuff
end
n = RubyDoesNothing.new
p n.kind_of?(IDoFoo), n.kind_of?(IDoStuff)
p [t { n.foo(1) }, t { n.stuff_foo(1) }, t { ConsumeIDoStuff.ConsumeStuffFoo(n) }, t { ConsumeIDoFoo.ConsumeFoo2(n) }]

class RubyDoesFooMM
  include IDoFoo
  attr_reader :tracker
  def method_missing(name, *args)
    @tracker = ""#{name}(#{args.map(&:to_s).join(', ')})""
    args.size
  end
end
m = RubyDoesFooMM.new
p m.foo(1), m.tracker
p ConsumeIDoFoo.ConsumeFoo3(m), m.tracker

class RubyHasGenerics
  include IHaveGenerics
  def generics_here(arg) = ""generics here #{arg}""
  def more_generics_here(arg) = ""more generics here #{arg}""
end
g = RubyHasGenerics.new
p g.generics_here('a'), EatIHaveGenerics.test_generics_here(g), EatIHaveGenerics.test_more_generics_here(g)

class RubyImplementsIInterface
  include IInterface
  def m; end
end
p ImplementsIInterface.ancestors.include?(IInterface), RubyImplementsIInterface.ancestors.include?(IInterface)
p IInterface.instance_of?(Module), System::IEquatable.of(Integer).instance_of?(Module)

class HashWithIDictionary < Hash
  include System::Collections::IDictionary
end
p HashWithIDictionary.new.class
", @"
1
'2'
[1, 1, 1]
true
true
[NoMethodError, NoMethodError, NoMethodError, NoMethodError]
1
""foo(1)""
2
""Foo(hello, 1)""
""generics here a""
'generics here test'
'more generics here 1'
true
true
true
true
HashWithIDictionary
");
        }

        /// <summary>
        /// A non-public class seen through its public interface, and a public class implementing the interface
        /// explicitly reached through clr_member: properties, interface arguments and results, and events.
        /// (interface/methods_spec.rb)
        /// </summary>
        public void ClrInterop_InterfaceMethods1() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + @"
explicit = PublicIPublicInterface.new
def explicit.method_missing(name, *args, &blk) = clr_member(IPublicInterface, name).call(*args, &blk)
[InterfaceOnlyTest.PrivateClass, explicit].each do |target|
  p target.class.name
  target.Hello = target
  p target.hello.equal?(target), target.Foo(target), target.RetInterface.equal?(target)
  fired = []
  handler = proc { |sender, args| fired << args.class.name; sender }
  target.MyEvent.add(handler)
  p target.FireEvent(target.GetEventArgs).equal?(target), fired
  target.MyEvent.remove(handler)
end
", @"
""IronRubyTests::InteropFixtures::PrivateClass""
true
nil
true
true
[""IronRubyTests::InteropFixtures::PrivateEventArgs""]
""IronRubyTests::InteropFixtures::PublicIPublicInterface""
true
nil
true
true
[""IronRubyTests::InteropFixtures::PublicEventArgs""]
");
        }

        /// <summary>
        /// Explicit implementations of generic interfaces are reached with clr_member and the constructed
        /// interface; a class's own M hides none of them. (interface/methods_spec.rb)
        /// </summary>
        public void ClrInterop_ExplicitGenericInterfaces1() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + @"
def m(o, i = nil) = t { i ? o.clr_member(i, :M).call : o.M }
o = ClassI3Obj.new
p [m(o), m(o, I3[Object])]
o = ClassI1I2I3Obj.new
p [m(o), m(o, I3[Object]), m(o, I1), m(o, I2)]
o = ClassI3_1[String].new
p [m(o), m(o, I3[String]), m(o, I3[Object])]
o = ClassI3_2[String].new
p [m(o), m(o, I3[String]), m(o, I3[Object])]
o = ClassI3ObjI3Int.new
p [m(o), m(o, I3[Object]), m(o, I3[Integer]), m(o, I3[String])]
o = ClassI1I4.new
p [m(o), m(o, I1), o.clr_member(I4, :M).call(1)]
", @"
['class M', 'I3<object>.M']
['class M', 'I3<object>.M', 'I1.M', 'I2.M']
['class M', 'I3<T>.M', NameError]
[NoMethodError, 'I3<T>.M', NameError]
[NoMethodError, 'I3<object>.M', 'I3<int>.M', NameError]
[NoMethodError, 'I1.M', 'I4.M']
");
        }

        /// <summary>
        /// Abstract CLR classes have no allocator; a Ruby subclass does. super from a Ruby override reaches a virtual
        /// CLR method (one with a marshalled return value too), but an abstract one has no body: it is a NoMethodError,
        /// as is C# calling an abstract method the Ruby class does not define. (class/instantiation/abstract_spec.rb,
        /// class/derivation/attribute_spec.rb)
        /// </summary>
        public void ClrInterop_AbstractClasses1() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + @"
p [t { EmptyAbstractClass.new }, t { AbstractClass.new }, t { AbstractDerived.new }]
class RubyFromAbstract < AbstractClass
  def m = 3
end
class RubyFromDerived < DerivedFromAbstract
  def m = super + 1
end
p DerivedFromAbstract.new.m, RubyFromAbstract.new.m, RubyFromDerived.new.m

class SubAbstract < AbstractWithBody
  def abstract = super
  def virtual = 'ruby ' + super
  def marshalled = !super
end
o = SubAbstract.new
p [t { o.abstract }, t { o.CallAbstract }], o.virtual, o.CallVirtual, o.marshalled

class EmptySubAbstract < AbstractWithBody; end
e = EmptySubAbstract.new
p [t { e.abstract }, t { e.CallAbstract }], e.CallVirtual
begin
  e.CallAbstract
rescue NoMethodError => ex
  puts ex.message
end
", @"
[TypeError, TypeError, TypeError]
1
3
2
[NoMethodError, NoMethodError]
""ruby clr virtual""
'ruby clr virtual'
false
[NoMethodError, NoMethodError]
'clr virtual'
undefined method 'Abstract' for an instance of EmptySubAbstract
");
        }

        /// <summary>
        /// A Ruby subclass inherits the CLR constructors, including an [Optional] or defaulted parameter, and its
        /// initialize can pick an overload with super. Generic definitions cannot be derived from; constructed generics
        /// can. A Ruby class can include interfaces at two levels of a CLR hierarchy (CP#2949). (class/fixtures/classes.rb,
        /// class/derivation/basic_spec.rb, class/derivation/generic_spec.rb, bcl/regressions_spec.rb)
        /// </summary>
        public void ClrInterop_Constructors1() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + @"
class RubyOptional < ClassWithOptionalConstructor; end
p RubyOptional.new.arg, RubyOptional.new(5).arg, ClassWithOptionalConstructor.new.arg
class RubyDefaulted < ClassWithDefaultConstructor; end
p RubyDefaulted.new.value, RubyDefaulted.new('x').value
class RubyOverloaded < OverloadedConstructorClass
  def initialize(*args) = super(*args)
end
p RubyOverloaded.new.val, RubyOverloaded.new('a').val, RubyOverloaded.new('a', 1).val

class KlassUser < Klass
  def foo = m
end
k = KlassUser.new
p k.kind_of?(Klass), k.m, k.foo

p t { Class.new(GenericClass) }
c = Class.new(GenericClass[Integer])
p c.new.kind_of?(GenericClass[Integer]), c.new.m

module IronRubyTests::InteropFixtures::RegressionSpecs
  class D < C
    include I1
  end
  class E < D
    include I2
  end
end
p IronRubyTests::InteropFixtures::RegressionSpecs::E.new.class.ancestors.take(6).map { |a| a.name.split('::').last }
", @"
0
5
0
'default'
'x'
'empty constructor'
'string constructor'
'string int constructor'
true
1
1
TypeError
true
1
[""E"", ""I2"", ""D"", ""I1"", ""C"", ""B""]
");
        }

        // Method binding. ClassWithMethods has one method per parameter type, each returning its own name;
        // RubyClassWithMethods, a Ruby subclass, has to bind the same way. Sampled from method/binding/*_spec.rb,
        // mostly the inputs those specs tagged as failing in 2009.
        private const string BindingPrologue = @"
class RubyClassWithMethods < ClassWithMethods; end
def bind(meth, input)
  results = [ClassWithMethods, RubyClassWithMethods].map do |k|
    o = k.new
    begin
      o.send(meth, input)
      o.tracker.count == 0 ? :none : o.tracker[o.tracker.count - 1]
    rescue TypeError
      :TE
    rescue => e
      e.class.name.to_sym
    end
  end
  results.uniq.size == 1 ? results[0] : results
end
def matrix(methods, inputs)
  inputs.each do |name, input|
    bound = methods.map { |m| [m, bind(m, input)] }.reject { |_, r| r == :TE }
    puts ""#{name}: #{bound.map { |m, r| r.equal?(input) ? m : ""#{m}=#{r.inspect}"" }.join(' ')}""
  end
end
";

        /// <summary>
        /// Interface, class, struct and enum parameters take only instances of their own type (an enum, any enum);
        /// nil fits reference types; an Object parameter takes anything, a Boolean one is truthiness.
        /// </summary>
        public void ClrInterop_BindingClassLike1() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + BindingPrologue + @"
class RubyImplementsIInterface
  include IInterface
  def m; end
end
class RubyFromAbstract < AbstractClass
  def m = 1
end
class RubyFromImplementsIInterface < ImplementsIInterface; end
matrix %w[IInterfaceArg ImplementsIInterfaceArg CStructArg StructImplementsIInterfaceArg AbstractClassArg CustomEnumArg EnumIntArg ObjectArg BooleanArg], {
  'anonymous instance' => Class.new.new,
  'class' => RubyImplementsIInterface,
  'Ruby IInterface' => RubyImplementsIInterface.new,
  'Ruby < ImplementsIInterface' => RubyFromImplementsIInterface.new,
  'DerivedFromImplementsIInterface' => DerivedFromImplementsIInterface.new,
  'Ruby < AbstractClass' => RubyFromAbstract.new,
  'CStruct' => CStruct.new,
  'StructImplementsIInterface' => StructImplementsIInterface.new,
  'EnumInt' => EnumInt.B,
  'nil' => nil,
}
", @"
anonymous instance: ObjectArg BooleanArg=true
class: ObjectArg BooleanArg=true
Ruby IInterface: IInterfaceArg ObjectArg BooleanArg=true
Ruby < ImplementsIInterface: IInterfaceArg ImplementsIInterfaceArg ObjectArg BooleanArg=true
DerivedFromImplementsIInterface: IInterfaceArg ImplementsIInterfaceArg ObjectArg BooleanArg=true
Ruby < AbstractClass: AbstractClassArg ObjectArg BooleanArg=true
CStruct: CStructArg ObjectArg BooleanArg=true
StructImplementsIInterface: IInterfaceArg StructImplementsIInterfaceArg ObjectArg BooleanArg=true
EnumInt: CustomEnumArg=B EnumIntArg ObjectArg BooleanArg=true
nil: IInterfaceArg ImplementsIInterfaceArg AbstractClassArg ObjectArg BooleanArg=false
");
        }

        /// <summary>
        /// Collections: CLR arrays bind to arrays of a base element type (reference types only), IList&lt;object&gt; and
        /// IEnumerable; a Hash to IDictionary&lt;object, object&gt;; a Ruby class including Enumerable to IEnumerable.
        /// An object whose singleton class includes Enumerable does not: its CLR type was fixed when it was allocated.
        /// </summary>
        public void ClrInterop_BindingCollections1() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + BindingPrologue + @"
class RubyImplementsIInterface
  include IInterface
  def m; end
end
class ImplementsEnumerable
  include Enumerable
  def each = [1, 2, 3].each { |i| yield i }
end
monkeypatched = Object.new
class << monkeypatched
  include Enumerable
  def each = [1, 2, 3].each { |i| yield i }
end
SA = System::Array
D = System::Collections::Generic::Dictionary
matrix %w[IEnumerableArg ObjectArrArg IInterfaceArrArg ParamsIInterfaceArrArg ParamsCStructArrArg IListOfObjArg IDictionaryOfObjectObjectArg IDictionaryOfIntStringArg], {
  'Enumerable singleton' => monkeypatched,
  'Enumerable class' => ImplementsEnumerable.new,
  'Hash' => {1 => 2},
  'String' => 'abc',
  'Object[]' => SA[Object].new(2, 7),
  'IInterface[]' => SA[IInterface].new(2, RubyImplementsIInterface.new),
  'empty IInterface[]' => SA[IInterface].new(0),
  'CStruct[]' => SA[CStruct].new(2, CStruct.new),
  'Dictionary[Object, Object]' => D[Object, Object].new,
  'Dictionary[Integer, String]' => D[Integer, System::String].new,
}
p bind('ParamsIInterfaceArrArg', RubyImplementsIInterface.new).class, bind('ParamsCStructArrArg', CStruct.new).length
", @"
Enumerable singleton: 
Enumerable class: IEnumerableArg
Hash: IEnumerableArg IDictionaryOfObjectObjectArg
String: 
Object[]: IEnumerableArg ObjectArrArg IListOfObjArg
IInterface[]: IEnumerableArg ObjectArrArg IInterfaceArrArg ParamsIInterfaceArrArg IListOfObjArg
empty IInterface[]: IEnumerableArg ObjectArrArg IInterfaceArrArg ParamsIInterfaceArrArg IListOfObjArg
CStruct[]: IEnumerableArg ParamsCStructArrArg
Dictionary[Object, Object]: IEnumerableArg IDictionaryOfObjectObjectArg
Dictionary[Integer, String]: IEnumerableArg IDictionaryOfIntStringArg
IronRubyTests::InteropFixtures::IInterface[]
1
");
        }

        /// <summary>
        /// Strings and numbers: Ruby and CLR strings and a Ruby subclass of String bind to String; to_str and
        /// to_int are the implicit conversions to String and Int32; a Float parameter parses a string as Float()
        /// does. System::Char into Double, Single and Decimal is left out until Largo/ironruby#153 settles how it converts.
        /// </summary>
        public void ClrInterop_BindingNumericAndString1() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + BindingPrologue + @"
class MyString < String; end
class ToInt; def to_int = 1; end
class ToIntToI; def to_i = 1; def to_int = 2; end
class ToStr; def to_str = 'to_str'; end
matrix %w[StringArg ObjectArg BooleanArg CharArg Int32Arg DoubleArg DecimalArg], {
  ""''"" => '',
  ""'a'"" => 'a',
  ""'1.5'"" => '1.5',
  ""System::String 'a'"" => System::String.new('a'),
  ""MyString 'a'"" => MyString.new('a'),
  'ToInt' => ToInt.new,
  'ToIntToI' => ToIntToI.new,
  'ToStr' => ToStr.new,
}
matrix %w[StringArg ObjectArg CharArg Int32Arg], 'System::Char' => System::Char.new('a')
", @"
'': StringArg='' ObjectArg='' BooleanArg=true DoubleArg=:ArgumentError
'a': StringArg='a' ObjectArg='a' BooleanArg=true CharArg='a' (Char) DoubleArg=:ArgumentError
'1.5': StringArg='1.5' ObjectArg='1.5' BooleanArg=true CharArg='1' (Char) DoubleArg=1.5
System::String 'a': StringArg ObjectArg BooleanArg=true DoubleArg=:ArgumentError
MyString 'a': StringArg='a' ObjectArg='a' BooleanArg=true CharArg='a' (Char) DoubleArg=:ArgumentError
ToInt: ObjectArg BooleanArg=true Int32Arg=1
ToIntToI: ObjectArg BooleanArg=true Int32Arg=2
ToStr: StringArg='to_str' ObjectArg BooleanArg=true
System::Char: StringArg='a' ObjectArg CharArg
");
        }

        /// <summary>
        /// Delegates, iterated collections, ref parameters and generic inference: a lambda, proc or Method converts
        /// to a concrete delegate type but not to Delegate itself; a lambda passed for a Boolean is truthy like any
        /// other object; a generic parameter is inferred from the argument's CLR type.
        /// </summary>
        public void ClrInterop_BindingMisc1() {
            LoadInteropFixtures();
            TestOutput(InteropFixturesPrologue + BindingPrologue + @"
SA = System::Array
matrix %w[IEnumerableIteratingArg IEnumerableOfCharIteratingArg IListOfCharArg IListArg DelegateArg IntIntDelegateArg BooleanArg RefBooleanArg], {
  'String' => 'abc',
  'System::String' => System::String.new('ab'),
  'String[]' => SA[System::String].new(2, System::String.new('a')),
  'Char[]' => SA[System::Char].new(2, System::Char.new('a')),
  'lambda' => lambda { |a| a + 2 },
  'proc' => proc { |a| a + 2 },
  'Method' => 1.method(:+),
  'IntIntDelegate' => IntIntDelegate.new { |a| a + 1 },
}
o = ClassWithMethods.new
[nil, 1, 'abc', System::String.new('ab'), [1], {}, lambda { }, IntIntDelegate.new { |a| a }, SA[System::String].new(1)].each { |x| puts o.GenericArg(x) }
", @"
String: BooleanArg=true RefBooleanArg=true
System::String: IEnumerableIteratingArg='b' (Char) IEnumerableOfCharIteratingArg='b' (Char) BooleanArg=true RefBooleanArg=true
String[]: IEnumerableIteratingArg='a' IListArg=2 BooleanArg=true RefBooleanArg=true
Char[]: IEnumerableIteratingArg='a' (Char) IEnumerableOfCharIteratingArg='a' (Char) IListOfCharArg=2 IListArg=2 BooleanArg=true RefBooleanArg=true
lambda: IntIntDelegateArg=12 BooleanArg=true RefBooleanArg=true
proc: IntIntDelegateArg=12 BooleanArg=true RefBooleanArg=true
Method: IntIntDelegateArg=11 BooleanArg=true RefBooleanArg=true
IntIntDelegate: DelegateArg=11 IntIntDelegateArg=11 BooleanArg=true RefBooleanArg=true
GenericArg[System.Object]
GenericArg[System.Int32]
GenericArg[IronRuby.Builtins.MutableString]
GenericArg[System.String]
GenericArg[IronRuby.Builtins.RubyArray]
GenericArg[IronRuby.Builtins.Hash]
GenericArg[IronRuby.Builtins.Proc]
GenericArg[IronRubyTests.InteropFixtures.IntIntDelegate]
GenericArg[System.String[]]
");
        }
    }
}

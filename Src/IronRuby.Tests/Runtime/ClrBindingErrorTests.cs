/* ****************************************************************************
 *
 * The errors of a call to a CLR method that cannot bind: arguments no overload converts, and type
 * arguments that break a generic parameter's constraints (#141).
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;

namespace IronRuby.Tests {
    public partial class Tests {
        public static class OverloadFixture {
            public static string Take(System.Text.StringBuilder sb) { return "StringBuilder"; }
            public static string Take(Version v) { return "Version"; }
            public static string Pair(System.Text.StringBuilder a, int b) { return "Pair(StringBuilder, int)"; }
            public static string Pair(int a, Version b) { return "Pair(int, Version)"; }
        }

        /// <summary>
        /// #141: when no overload of a CLR method takes the arguments, the TypeError names what the plausible overloads
        /// would have converted the argument to, not the conversion of whichever overload was tried first. Every Abs
        /// overload fails to convert nil alike, so none is better than another, and the error lists them by the classes
        /// MRI names ("into System::SByte" before). Overloads that fail on different arguments: the ones that got furthest
        /// are the plausible ones. A CLR String parameter wants a String (a library method's wants a method name).
        /// </summary>
        public void ClrInterop_OverloadConversionErrors1() {
            Context.ObjectClass.SetConstant("OF", Context.GetClass(typeof(OverloadFixture)));
            TestOutput(@"
p((System::Math.abs(nil) rescue $!), (System::Math.abs('x') rescue $!), (System::Math.abs(Object.new) rescue $!))
p System::Math.abs(-3), System::Math.abs(-2.5), System::Math.abs(2**70)
p((System::String.is_null_or_empty(Object.new) rescue $!))
o = Object.new; def o.to_str; ''; end
p System::String.is_null_or_empty(o), System::String.is_null_or_empty(:a), System::String.is_null_or_empty(nil)
p((Object.instance_method(1) rescue $!))
p((OF.take('x') rescue $!), (OF.pair(1, 'x') rescue $!))
p OF.take(System::Version.new(1, 0)), OF.pair(1, System::Version.new(1, 0))
", @"
#<TypeError: no implicit conversion of nil into Integer or Float>
#<TypeError: no implicit conversion of String into Integer or Float>
#<TypeError: no implicit conversion of Object into Integer or Float>
3
2.5
1.1805916207174113e+21
#<TypeError: no implicit conversion of Object into String>
true
false
true
#<TypeError: 1 is not a symbol nor a string>
#<TypeError: no implicit conversion of String into System::Text::StringBuilder or System::Version>
#<TypeError: no implicit conversion of String into System::Version>
'Version'
'Pair(int, Version)'
");
        }

        public static class ConstraintFixture {
            public static string MakeNew<T>() where T : new() { return new T().GetType().Name; }
            public static string RefOnly<T>() where T : class { return typeof(T).Name; }
            public static string ValOnly<T>() where T : struct { return typeof(T).Name; }
            public static string Comparable<T>() where T : IComparable { return typeof(T).Name; }
            public static string Related<S, T>() where S : T { return typeof(S).Name; }
        }

        /// <summary>
        /// #141: a type argument that breaks a generic parameter's constraint is an ArgumentError naming the constraint
        /// and the argument as Ruby code wrote it. The CLR's ArgumentException leaked before, naming the CLR type behind a
        /// Ruby class: "GenericArguments[0], 'IronRuby.Builtins.RubyObject', on '...' violates the constraint of type 'T'".
        /// A Ruby class never satisfies new(): its CLR type is constructed with the class, so it has no parameterless
        /// constructor. Generic types (Module#[], a type group's []) check the same way.
        /// </summary>
        public void ClrInterop_GenericConstraints1() {
            Context.ObjectClass.SetConstant("CF", Context.GetClass(typeof(ConstraintFixture)));
            TestOutput(@"
class Foo; end
def t; puts yield; rescue => e; puts ""#{e.class}: #{e.message}""; end
t { CF.method(:make_new).of(Foo).call }
t { CF.method(:make_new).of(System::String).call }
t { CF.method(:make_new).of(System::IO::Stream).call }
t { CF.method(:make_new).of(System::Text::StringBuilder).call }
t { CF.method(:make_new).of(Integer).call }
t { CF.method(:ref_only).of(Integer).call }
t { CF.method(:val_only).of(String).call }
t { CF.method(:comparable).of(Object).call }
t { CF.method(:comparable).of(String).call }
t { CF.method(:related).of(Object, String).call }
t { CF.method(:related).of(String, Object).call }
t { System::Nullable[Foo] }
t { System::Nullable.of(String) }
t { System::Nullable[System::Int32] }
t { System::Collections::Generic::List[Foo] }
", @"
ArgumentError: Foo does not satisfy the new() constraint on T of 'make_new': a Ruby class has no public parameterless CLR constructor
ArgumentError: System::String does not satisfy the new() constraint on T of 'make_new': it has no public parameterless constructor
ArgumentError: System::IO::Stream does not satisfy the new() constraint on T of 'make_new': it is abstract
StringBuilder
Int32
ArgumentError: Integer does not satisfy the class constraint on T of 'ref_only': it is a value type
ArgumentError: String does not satisfy the struct constraint on T of 'val_only': it is not a non-nullable value type
ArgumentError: Object does not satisfy the System::IComparable constraint on T of 'comparable': it is not a System::IComparable
MutableString
ArgumentError: the type arguments do not satisfy the constraints of 'related'
MutableString
ArgumentError: Foo does not satisfy the struct constraint on T of 'Nullable': it is not a non-nullable value type
ArgumentError: String does not satisfy the struct constraint on T of 'Nullable': it is not a non-nullable value type
System::Nullable[Integer]
System::Collections::Generic::List[Object]
");
        }
    }
}

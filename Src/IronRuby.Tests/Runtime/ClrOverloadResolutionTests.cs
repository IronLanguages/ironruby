/* ****************************************************************************
 *
 * How a Ruby call chooses between CLR overloads that fit its arguments
 * equally: C#'s tie-break for generic methods, and what the arguments'
 * conversion protocols can actually do.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;

namespace IronRuby.Tests {
    public partial class Tests {
        // Shaped like Enumerable.Max/Min: selectors returning Int32, Int64 or any TResult.
        public static class TieBreakSelectors {
            public static string Pick<TSource>(IEnumerable<TSource> source, Func<TSource, int> selector) { return "Pick(Func<T, Int32>)"; }
            public static string Pick<TSource>(IEnumerable<TSource> source, Func<TSource, long> selector) { return "Pick(Func<T, Int64>)"; }
            public static string Pick<TSource, TResult>(IEnumerable<TSource> source, Func<TSource, TResult> selector) { return "Pick(Func<T, TResult>)"; }
            public static Func<int, long> Int64Selector() { return x => -x; }

            // Shaped like Enumerable.Sum: no selector returns a type parameter.
            public static string Total<TSource>(IEnumerable<TSource> source, Func<TSource, int> selector) { return "Total(Func<T, Int32>)"; }
            public static string Total<TSource>(IEnumerable<TSource> source, Func<TSource, double> selector) { return "Total(Func<T, Double>)"; }

            // Each more specific in one parameter: neither wins.
            public static string Cross<T>(T a, int b) { return "Cross(T, Int32)"; }
            public static string Cross<T>(int a, T b) { return "Cross(Int32, T)"; }

            // More specific deeper down: an array of Int32 and a list of Int32.
            public static string Items<T>(T[] items, T item) { return "Items(T[], T)"; }
            public static string Items<T>(int[] items, T item) { return "Items(Int32[], T)"; }
            public static string Lists<T, U>(Func<T, IList<U>> f, T arg) { return "Lists(Func<T, IList<U>>)"; }
            public static string Lists<T>(Func<T, IList<int>> f, T arg) { return "Lists(Func<T, IList<Int32>>)"; }

            // A non-generic method wins over a generic one, as before.
            public static string Plain<T>(IEnumerable<T> source, Func<T, int> selector) { return "Plain<T>"; }
            public static string Plain(IEnumerable<int> source, Func<int, int> selector) { return "Plain"; }
        }

        // A type parameter of the declaring type is less specific too.
        public class TieBreakBox<T> {
            public string Put(T item) { return "Put(T)"; }
            public string Put(int item) { return "Put(Int32)"; }
        }

        /// <summary>
        /// Overloads that take the same parameter types once instantiated are told apart as C# does: the one whose declared
        /// parameter types are more specific wins, a type parameter being less specific than any other type.
        /// Max&lt;TSource&gt;(…, Func&lt;TSource, int&gt;) wins over Max&lt;TSource, TResult&gt;(…, Func&lt;TSource, TResult&gt;).
        /// </summary>
        public void ClrOverloadGenericTieBreak1() {
            Context.ObjectClass.SetConstant("S", Context.GetClass(typeof(TieBreakSelectors)));
            Context.ObjectClass.SetConstant("IntBox", Context.GetClass(typeof(TieBreakBox<int>)));
            Context.ObjectClass.SetConstant("StringBox", Context.GetClass(typeof(TieBreakBox<string>)));
            TestOutput(@"
list = System::Collections::Generic::List[Integer].new([3, 1, 4])
puts S.pick(list, System::Func[Integer, Integer].new { -it })
puts S.pick(list, S.int64_selector)
puts S.pick(list, System::Func[Integer, Float].new { -it })
puts S.pick(list) { -it }
puts S.total(list, System::Func[Integer, Integer].new { -it })
puts S.total(list, System::Func[Integer, Float].new { -it })
puts S.items(System::Array[Integer].new([1]), 2)
puts S.lists(System::Func[Integer, System::Collections::Generic::IList[Integer]].new { [it] }, 1)
puts S.plain(list, System::Func[Integer, Integer].new { -it })
puts IntBox.new.put(1)
puts StringBox.new.put('x')
begin
  S.cross(1, 2)
rescue System::Reflection::AmbiguousMatchException
  puts 'ambiguous'
end
", @"
Pick(Func<T, Int32>)
Pick(Func<T, Int64>)
Pick(Func<T, TResult>)
Pick(Func<T, TResult>)
Total(Func<T, Int32>)
Total(Func<T, Double>)
Items(Int32[], T)
Lists(Func<T, IList<Int32>>)
Plain
Put(Int32)
Put(T)
ambiguous
");
        }

        /// <summary>
        /// LINQ's Max and Min take an explicit selector delegate; a block that fits several selector types is still
        /// reported, with the hint to pass the delegate.
        /// </summary>
        public void ClrOverloadGenericTieBreak2() {
            Context.ObjectClass.SetConstant("SystemCoreAssembly", typeof(System.Linq.Enumerable).Assembly.FullName);
            TestOutput(@"
load_assembly SystemCoreAssembly
using System::Linq
list = System::Collections::Generic::List[Integer].new([3, 1, 4])
p list.max(System::Func[Integer, Integer].new { -it })
p list.min(System::Func[Integer, Integer].new { -it })
p list.min(System::Func[Integer, Float].new { it * 1.5 })
p list.max(System::Func[Integer, System::String].new { it.to_s })
p list.sum(System::Func[Integer, Integer].new { -it })
p list.average(System::Func[Integer, Float].new { it * 3.0 })
p list.max { -it }
begin
  list.sum { it * 2 }
rescue System::Reflection::AmbiguousMatchException => e
  puts e.message.end_with?('pass the delegate itself, e.g. System::Func[...].new { ... }')
end
", @"
-1
-4
1.5
'4'
-8
8.0
-1
true
");
        }

        // Generic overloads C# tells apart by how they take the arguments, before it compares their parameter types.
        public static class TieBreakForms {
            // Each parameter gets an argument: wins over one that needs a default value, though that is more specific.
            public static string Defaults<T>(T a, T b) { return "Defaults(T, T)"; }
            public static string Defaults<T>(int a, T b, int c = 0) { return "Defaults(Int32, T, Int32=)"; }

            // The normal form wins over an expanded params array, also one expanded into no items.
            public static string Normal<T>(T a, T b) { return "Normal(T, T)"; }
            public static string Normal<T>(int a, T b, params int[] rest) { return "Normal(Int32, T, params Int32[])"; }

            // Of two expanded ones the one with more declared parameters wins, ...
            public static string Declared<T>(T a, params T[] rest) { return "Declared(T, params T[])"; }
            public static string Declared<T>(T a, T b, params T[] rest) { return "Declared(T, T, params T[])"; }

            // ... or of as many the more specific one, the items compared by their type.
            public static string Expanded<T>(T a, params T[] rest) { return "Expanded(T, params T[])"; }
            public static string Expanded<T>(int a, params T[] rest) { return "Expanded(Int32, params T[])"; }
            public static string Rest<T>(T a, params T[] rest) { return "Rest(T, params T[])"; }
            public static string Rest<T>(T a, params int[] rest) { return "Rest(T, params Int32[])"; }

            // Of two that need default values the more specific one wins if they have as many parameters, else neither.
            public static string SameDefaults<T>(T a, T b, int c = 0) { return "SameDefaults(T, T, Int32=)"; }
            public static string SameDefaults<T>(int a, T b, int c = 0) { return "SameDefaults(Int32, T, Int32=)"; }
            public static string OtherDefaults<T>(T a, T b, int c = 0, int d = 0) { return "OtherDefaults(T, T, Int32=, Int32=)"; }
            public static string OtherDefaults<T>(int a, T b, int c = 0) { return "OtherDefaults(Int32, T, Int32=)"; }

            // ref Int32 is more specific than ref T.
            public static string Ref<T>(T a, ref int b) { b = 1; return "Ref(T, ref Int32)"; }
            public static string Ref<T>(T a, ref T b) { return "Ref(T, ref T)"; }
        }

        /// <summary>
        /// C#'s tie-break rules come in order: the normal form over an expanded params array, of two expanded ones the
        /// one with more declared parameters, all parameters passed over a default value substituted - and only then the
        /// more specific parameter types. Each result is the one the C# compiler picks; "ambiguous" is its error CS0121.
        /// </summary>
        public void ClrOverloadGenericTieBreak3() {
            Context.ObjectClass.SetConstant("F", Context.GetClass(typeof(TieBreakForms)));
            TestOutput(@"
def try = (yield rescue $!.class.name)
puts try { F.defaults(1, 2) }
puts try { F.defaults(1, 2, 3) }
puts try { F.normal(1, 2) }
puts try { F.normal(1, 2, 3) }
puts try { F.declared(1, 2, 3) }
puts try { F.expanded(1, 2) }
puts try { F.expanded(1, 2, 3) }
puts try { F.rest(1, 2) }
puts try { F.rest(1) }
puts try { F.same_defaults(1, 2) }
puts try { F.other_defaults(1, 2) }
puts try { F.ref(1, System::Runtime::CompilerServices::StrongBox[Integer].new(0)) }
", @"
Defaults(T, T)
Defaults(Int32, T, Int32=)
Normal(T, T)
Normal(Int32, T, params Int32[])
Declared(T, T, params T[])
Expanded(Int32, params T[])
Expanded(Int32, params T[])
Rest(T, params Int32[])
System::Reflection::AmbiguousMatchException
SameDefaults(Int32, T, Int32=)
System::Reflection::AmbiguousMatchException
Ref(T, ref Int32)
");
        }

        public class ProtocolOverloads {
            public string Put(string key, string value) { return "Put(String, String)"; }
            public string Put(string key, int value) { return "Put(String, Int32)"; }

            public string Three(string value) { return "Three(String)"; }
            public string Three(int value) { return "Three(Int32)"; }
            public string Three(double value) { return "Three(Double)"; }

            public string Real(int value) { return "Real(Int32)"; }
            public string Real(float value) { return "Real(Single)"; }
            public string Real(double value) { return "Real(Double)"; }

            public string Pair(int a, int b) { return "Pair(Int32, Int32)"; }
            public string Pair(double a, double b) { return "Pair(Double, Double)"; }
        }

        /// <summary>
        /// A CLR method's String and Int32 parameters take any object by Ruby's conversion protocols (to_str, to_int),
        /// which only the call can try. An overload whose conversion the object's class does not implement is no match for
        /// one whose conversion it does; if it implements none, the call fails with MRI's TypeError rather than as an
        /// ambiguity - passed positionally, splatted or by keyword. An object that implements both is an ambiguity still,
        /// and so is a call whose other arguments prefer another overload. A Float parameter counts only a Numeric as
        /// convertible, as MRI does, though IronRuby calls to_f of any object passed to it alone.
        /// </summary>
        public void ClrOverloadProtocolConversions1() {
            Context.ObjectClass.SetConstant("P", Context.GetClass(typeof(ProtocolOverloads)));
            TestOutput(@"
o = P.new
def try = (yield rescue ""#{$!.class}: #{$!.message}"")
class IntLike; def to_int = 1; end
class StrLike; def to_str = 's'; end
class Both; def to_int = 1; def to_str = 's'; end
class Ghost; def method_missing(name, *) = (name == :to_int ? 1 : super); end
class FloatLike; def to_f = 1.0; end

puts try { o.put('k', 'v') }
puts try { o.put('k', 1) }
puts try { o.put('k', nil) }
puts try { o.put('k', Time.now) }
puts try { o.put('k', Object.new) }
puts try { o.put('k', IntLike.new) }
puts try { o.put('k', StrLike.new) }
puts try { o.put('k', Both.new) }
puts try { o.put('k', Ghost.new) }
puts try { o.put(*['k', Time.now]) }
puts try { o.put(*['k', IntLike.new]) }
puts try { o.put(key: 'k', value: Time.now) }
puts try { o.put('k', value: IntLike.new) }
puts try { o.put(**{key: 'k', value: Time.now}) }
puts try { o.three(Object.new) }
puts try { o.three(FloatLike.new) }
puts try { o.real(IntLike.new) }
puts try { o.real(FloatLike.new) }
puts try { o.real(Time.now) }
puts try { o.real(nil) }
puts try { o.real(:sym) }
puts try { o.pair(IntLike.new, 1.5) }
puts try { o.pair(IntLike.new, IntLike.new) }
puts try { System::Math.max(Time.at(100), 1) }
puts try { System::Math.max('3', 1) }
puts try { System::Math.min(FloatLike.new, 1.5) }
", @"
Put(String, String)
Put(String, Int32)
Put(String, String)
TypeError: no implicit conversion of Time into String or Integer
TypeError: no implicit conversion of Object into String or Integer
Put(String, Int32)
Put(String, String)
System::Reflection::AmbiguousMatchException: Found multiple methods for 'put': put(System::String, System::String), put(System::String, Integer)
System::Reflection::AmbiguousMatchException: Found multiple methods for 'put': put(System::String, System::String), put(System::String, Integer)
TypeError: no implicit conversion of Time into String or Integer
Put(String, Int32)
TypeError: no implicit conversion of Time into String or Integer
Put(String, Int32)
TypeError: no implicit conversion of Time into String or Integer
TypeError: no implicit conversion of Object into String, Integer, or Float
TypeError: no implicit conversion of FloatLike into String, Integer, or Float
Real(Int32)
TypeError: no implicit conversion of FloatLike into Integer
TypeError: no implicit conversion of Time into Integer
TypeError: no implicit conversion of nil into Integer
TypeError: no implicit conversion of Symbol into Integer
System::Reflection::AmbiguousMatchException: Found multiple methods for 'pair': pair(Integer, Integer), pair(Float, Float)
Pair(Int32, Int32)
TypeError: no implicit conversion of Time into Integer or Float
TypeError: no implicit conversion of String into Integer or Float
TypeError: no implicit conversion of FloatLike into Integer or Float
");
        }

        /// <summary>
        /// What decides between the overloads is the class of the argument: a call site re-binds when the class gains or
        /// loses a conversion method, and an object with a singleton method of its own is told apart from others of its class.
        /// </summary>
        public void ClrOverloadProtocolConversions2() {
            Context.ObjectClass.SetConstant("P", Context.GetClass(typeof(ProtocolOverloads)));
            TestOutput(@"
O = P.new
def put(value) = (O.put('k', value) rescue ""#{$!.class}: #{$!.message}"")
def put_splat(*args) = (O.put(*args) rescue ""#{$!.class}: #{$!.message}"")
class Foo; end
foo = Foo.new
puts put(foo), put_splat('k', foo)
class Foo; def to_int = 1; end
puts put(foo), put_splat('k', foo)
class Foo; remove_method :to_int; end
puts put(foo), put_splat('k', foo)
module Stringish; def to_str = 's'; end
class Foo; include Stringish; end
puts put(foo), put_splat('k', foo)

a, b = Object.new, Object.new
def b.to_int = 2
puts put(a), put(b), put(a)

sb1, sb2 = System::Text::StringBuilder.new, System::Text::StringBuilder.new
def sb2.to_int = 3
puts put(sb1), put(sb2), put(sb1)
", @"
TypeError: no implicit conversion of Foo into String or Integer
TypeError: no implicit conversion of Foo into String or Integer
Put(String, Int32)
Put(String, Int32)
TypeError: no implicit conversion of Foo into String or Integer
TypeError: no implicit conversion of Foo into String or Integer
Put(String, String)
Put(String, String)
TypeError: no implicit conversion of Object into String or Integer
Put(String, Int32)
TypeError: no implicit conversion of Object into String or Integer
TypeError: no implicit conversion of System::Text::StringBuilder into String or Integer
Put(String, Int32)
TypeError: no implicit conversion of System::Text::StringBuilder into String or Integer
");
        }
    }
}

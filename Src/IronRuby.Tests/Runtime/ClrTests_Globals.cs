using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Scripting.Utils;

namespace InteropTests.Generics1 {
    public class C {
        public virtual int Arity { get { return 0; } }
    }

    public class C<T> {
        public virtual int Arity { get { return 1; } }
    }

    public class C<T, S> {
        public virtual int Arity { get { return 2; } }
    }

    public class D : C {
        public override int Arity { get { return 10; } }
    }

    public class D<T> : C<T> {
        public override int Arity { get { return 11; } }
    }

    public interface J<T> {
        void MethodOnJ();
    }

    public interface I<T> : J<T> {
        void MethodOnI();
    }

    public class E<T> : I<T>, J<T> {
        void I<T>.MethodOnI() { }
        void J<T>.MethodOnJ() { }
    }

    public static class Extensions {
        public static IEnumerable<R> Select<T, R>(this IEnumerable<T> a, Func<T, R> func) {
            foreach (var item in a) {
                yield return func(item);
            }
        }
    }
}

namespace InteropTests.Namespaces2 {
    public class C { }
    namespace N {
        public class D { }
    }
}

namespace IronRubyTests.ExtensionMethods1 {
    using IronRuby.Tests;

    public static class OverloadInheritanceExtensions2 {
        public static string f(this Tests.OverloadInheritance2.C self, int a, int b, int c, int d, int e) { return "e5"; }
    }

    public static class OverloadInheritanceExtensions3 {
        public static string f(this Tests.OverloadInheritance2.A self, int a, int b, int c, int d, int e, int f) { return "e6"; }
        public static string f(this Tests.OverloadInheritance2.F self, int a, int b, int c, int d) { return "e4-dup"; }
    }
}

namespace System.Linq {
    public class Dummy {
    }
}

// top level (no namespace)
public static class OverloadInheritanceExtensions4 {
    public static string f(this IronRuby.Tests.Tests.OverloadInheritance2.C self, int a, int b, int c, int d, int e, int f, int g) { return "e7"; }
}

namespace IronRubyTests.ExtensionMethods2 {
    public class A {
    }

    public class B : A {
    }

    public interface I {
    }

    public class X : I {
    }

    public class Y : X {
    }

    public struct S {
    }

    public static class EMs {
        public static string f1<T>(this List<T> self) where T : I { 
            return "f1"; 
        }

        public static string f2<S,T>(this Dictionary<S, T> self) where S : T {
            return "f2";
        }

        public static string f3<S, T>(this List<S> self, T t) 
            where S : T, I, new() 
        {
            return "f3";
        }

        public static string f4<T>(this T self) where T : struct {
            return "f4";
        }

        public static string f5<T>(this T self) where T : A {
            return "f5";
        }

        public static string f6<T>(this T[] self) where T : A {
            return "f6";
        }

        public static string f6<S, T>(this List<Dictionary<S, List<T>>[]> self) 
            where T : class
            where S : A 
        {
            return "f6";
        }
    }
}

// Extension methods whose Ruby name (describe) is not their CLR name (Describe).
namespace IronRubyTests.ExtensionMethods3 {
    public class Widget {
        public string Describe() { return "Describe()"; }
        public string Describe(int a) { return "Describe(int)"; }
    }

    public class SubWidget : Widget {
    }

    public interface IShape {
    }

    public class Square : IShape {
        public string Area() { return "Area()"; }
    }

    public class Box<T> {
        public string Open() { return "Open()"; }
    }

    public static class EMs {
        // same signature as Widget.Describe() => the instance method is preferred
        public static string Describe(this Widget self) { return "e:Describe()"; }
        public static string Describe(this Widget self, string a) { return "e:Describe(string)"; }

        public static string Area(this IShape self, int scale) { return "e:Area(int)"; }

        public static string Open<T>(this Box<T> self, T item) { return "e:Open(T)"; }
    }

    // `using IronRubyTests::ExtensionMethods3::MoreEMs' (C#'s `using static') brings in these and not EMs'.
    public static class MoreEMs {
        public static string Label(this Widget self) { return "e:Label()"; }
    }
}

// Extension methods named like property accessors (get_Weight, set_Item). They aren't accessors: C# makes no property
// or indexer of them, and doesn't let them change the extended type's own.
namespace IronRubyTests.ExtensionMethods4 {
    // no property or indexer of its own
    public class Crate {
    }

    // a Weight property and an indexer, whose accessors' names the extension methods below take
    public class Scale {
        public int Weight { get { return 1; } set { Last = "Weight=" + value; } }
        public int this[int i] { get { return 10 + i; } set { Last = "[" + i + "]=" + value; } }
        public string Last;
    }

    public interface IWeighed {
    }

    public class Parcel : IWeighed {
    }

    // an instance method named like an accessor that isn't one
    public class Plain {
        public string get_Weight() { return "get_Weight()"; }
    }

    public static class EMs {
        public static string get_Weight(this Crate self) { return "e:get_Weight()"; }
        public static string set_Weight(this Crate self, int value) { return "e:set_Weight(int)"; }
        public static string get_Item(this Crate self, int i) { return "e:get_Item(int)"; }
        public static string set_Item(this Crate self, int i, int value) { return "e:set_Item(int, int)"; }

        // same signatures as Scale's accessors => the accessors are preferred
        public static string get_Weight(this Scale self) { return "e:get_Weight()"; }
        public static string set_Weight(this Scale self, int value) { return "e:set_Weight(int)"; }
        public static string get_Item(this Scale self, int i) { return "e:get_Item(int)"; }
        public static string set_Item(this Scale self, int i, int value) { return "e:set_Item(int, int)"; }
        public static string get_Weight(this Scale self, int scale) { return "e:get_Weight(int)"; }

        public static string get_Weight(this IWeighed self) { return "e:IWeighed.get_Weight()"; }

        public static string get_Heft(this object self) { return "e:get_Heft()"; }
    }
}
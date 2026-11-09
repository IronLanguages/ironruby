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
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;

using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;

using IronRuby.Builtins;

namespace IronRuby.Tests {
    public partial class Tests {
        public void RubyHosting_DelegateConversions() {
            object lambda = Engine.Execute(@"lambda { |a| a + 1 }");
            var result = Engine.Operations.Invoke(lambda, 5);
            Assert((int)result == 6);

            var func = Engine.Operations.ConvertTo<Func<int, int>>(lambda);
            Assert(func(10) == 11);

            object method = Engine.Execute(@"def foo(a,b); a + b; end; method(:foo)");
            var func2 = Engine.Operations.ConvertTo<Func<int, int, int>>(method);
            Assert(func2(1, 2) == 3);

            Engine.Runtime.Globals.SetVariable("F1", typeof(Func<int, int>));
            var func3 = (Func<int, int>)Engine.Execute(@"F1.to_class.new { |a| a + 3 }");
            Assert(func3(1) == 4);

            var func4 = (Func<int, int>)Engine.Execute(@"F1.to_class.new lambda { |a| a + 4 }");
            Assert(func4(1) == 5);

            var func5 = (Func<int, int>)Engine.Execute(@"F1.to_class.new(*[lambda { |a| a + 5 }])");
            Assert(func5(1) == 6);

            AssertExceptionThrown<ArgumentException>(() => Engine.Execute(@"F1.to_class.new(*[])"));
        }

        public void RubyHosting1A() {
            ScriptScope scope = Engine.Runtime.CreateScope();
            scope.SetVariable("x", 1);
            scope.SetVariable("y", 2);

            // Reads x, y from scope via method_missing.
            // The code is instance-eval'd as a proc against the scope so both instance and 
            // singleton method definitions define a singleton method on main object.
            // Method definition on main object bound to a scope copies the method to the scope.
            Engine.Execute("def z; x + y; end", scope);
            Engine.Execute("def self.w(a); a + 1; end", scope);

            int z = scope.GetVariable<Func<int>>("z")();
            Assert(z == 3);

            int w = scope.GetVariable<Func<int, int>>("w")(1);
            Assert(w == 2);
        }

        public void RubyHosting1B() {
            ScriptScope scope = Engine.CreateScope();
            scope.SetVariable("SomeValue", 1);
            scope.SetVariable("other_value", 2);

            // Method names are unmangled for scope lookups.
            // "tmp" is defined in the top-level binding, which is associated with the scope:
            Engine.Execute("tmp = some_value + other_value", scope);
            
            // "tmp" symbol is extracted from scope's top-level binding and passed to the compiler as a compiler option
            // so that the parser treats it as a local variable.
            string tmpDefined = Engine.Execute<MutableString>("defined?(tmp)", scope).ToString();
            Assert(tmpDefined == "local-variable");

            // The code is eval'd against the existing top-level local scope created by the first execution.
            // tmp2 local is looked up dynamically:
            Engine.Execute("tmp2 = 10", scope);

            // result= is turned into a scope variable assignment in method_missing:
            Engine.Execute("self.result = tmp", scope);

            // If "scope" variable is not defined on "self" and in the DLR scope we alias it for "self":
            Engine.Execute("scope.result += tmp2", scope);

            int result = scope.GetVariable<int>("result");
            Assert(result == 13);

            // Ruby local variables are not exposed:
            Assert(scope.ContainsVariable("tmp") == false);
        }

        public void RubyHosting1C() {
            // Main singleton in a scope-unbound code doesn't define method_missing:
            AssertExceptionThrown<MemberAccessException>(
                () => Engine.Execute("class << self; remove_method(:method_missing); end")
            );
            

            // Main singleton in a scope-bound code defines method_missing:
            Engine.Execute("class << self; remove_method(:method_missing); end", Engine.CreateScope());

            var scope = Engine.CreateScope();
            Engine.Execute("self.tmp = 1", scope);
            Assert(scope.ContainsVariable("tmp"));
            
            AssertExceptionThrown<MissingMethodException>(() => Engine.Execute("self.tmp = 1"));


            // method_missing on top-level scope is defined dynamically, not at compile time:
            var compiled = Engine.CreateScriptSourceFromString("some_variable").Compile();
            scope = Engine.CreateScope();

            scope.SetVariable("some_variable", 123);
            Assert(compiled.Execute<int>(scope) == 123);
            
            // NameError: undefined local variable or method 'some_variable'
            AssertExceptionThrown<MemberAccessException>(() => compiled.Execute());

            scope.SetVariable("some_variable", "foo");
            Assert(compiled.Execute<string>(scope) == "foo");

            // we throw correct exceptions:
            scope = Engine.CreateScope();
            scope.SetVariable("bar", 1);
            AssertExceptionThrown<MissingMethodException>(() => Engine.Execute("foo 1,2,3"));
            AssertExceptionThrown<MissingMethodException>(() => Engine.Execute("foo 1,2,3", scope));
            AssertExceptionThrown<ArgumentException>(() => Engine.Execute("bar 1,2,3", scope));
            AssertExceptionThrown<ArgumentException>(() => Engine.Execute("bar *[1,2,3]", scope));
            AssertExceptionThrown<ArgumentException>(() => Engine.Execute("scope *[1,2,3]", scope));
            Assert(Engine.Execute<int>("bar *[]", scope) == 1);
        }

        public void RubyHosting1D() {
            // When executed without a scope top-level methods are defined on Object (as in MRI):
            Engine.Execute("def foo; 1; end");
            Assert(Context.ObjectClass.GetMethod("foo") != null);

            // The method is private and shouldn't be invokable via InvokeMember:
            AssertExceptionThrown<MissingMethodException>(() => Engine.Operations.InvokeMember(new object(), "foo"));

            // When executed against a scope top-level methods are defined on main singleton and also stored in the scope.
            // They are private methods of Object as well, so that instances of other classes can call them, as in MRI
            // (issue 163).
            var scope = Engine.CreateScope();
            Engine.Execute("def bar; 1; end", scope);
            Assert(Context.ObjectClass.GetMethod("bar") != null);
            Assert(Context.ObjectClass.GetMethod("bar").IsPrivate);
            Assert(scope.GetVariable<object>("bar") != null);
            
            // we can invoke the method on a scope:
            Assert((int)Engine.Operations.InvokeMember(scope, "bar") == 1);
            
            // Since the top-level of a scope finds the method in main singleton first,
            // executions against different scopes don’t step on each other:
            var scope1 = Engine.CreateScope();
            var scope2 = Engine.CreateScope();
            Engine.Execute("def foo(a,b); a + b; end", scope1);
            Engine.Execute("def foo(a,b); a - b; end", scope2);
            Assert(Engine.Execute<int>("foo(1,2)", scope1) == 3);
            Assert(Engine.Execute<int>("foo(1,2)", scope2) == -1);

            // Contrary, last one wins when executing w/o scope:
            Engine.Execute("def baz(a,b); a + b; end");
            Engine.Execute("def baz(a,b); a - b; end");
            Assert(Engine.Execute<int>("baz(1,2)") == -1);
        }

        /// <summary>
        /// missing_method on scope forwards to super class if the variable is not defined in scope;
        /// unmangled name is used if available and the mangled is not found in the scope.
        /// </summary>
        /// <summary>
        /// Code run in a host's scope defines a top-level method in main's singleton class (the scope's) and in Object (issue
        /// 163, RubyHosting1D). Top-level `alias', `undef', `public :x'/`private :x' and main.define_method acted on main's
        /// singleton class alone, so an instance of another class did not see what they did (issue 177). They act on Object
        /// as well now, and this prints what CRuby 4 prints for the same file.
        /// </summary>
        public void RubyHosting_TopLevelOps1() {
            var scope = Engine.CreateScope();
            AssertOutput(() => Engine.Execute(@"
def foo = 1
alias bar foo
def baz = 2
undef baz
def pub = 3
public :pub
def priv = 3
public :priv
private :priv
define_method(:dm) { 4 }
def src = 5
define_method(:dm2, method(:src))
define_method(:dm3, Object.instance_method(:src))
class Hosted177
  def calls = [bar, pub, dm, dm2, dm3, respond_to?(:baz, true)]
end
p Hosted177.new.calls
p [bar, pub, priv, dm, dm2, dm3]
p Object.private_method_defined?(:bar), Object.public_method_defined?(:pub), Object.private_method_defined?(:priv)
p [:dm, :dm2, :dm3].map { |m| Object.public_method_defined?(m) }
p 1.pub, 1.dm
begin; undef nope; rescue NameError => e; p e.message; end
begin; alias nope2 nope; rescue NameError => e; p e.message; end
self.instance_eval { alias ie2 foo }
p Object.private_method_defined?(:ie2), singleton_class.method_defined?(:ie2) || singleton_class.private_method_defined?(:ie2)
", scope), @"
[1, 3, 4, 5, 5, false]
[1, 3, 3, 4, 5, 5]
true
true
true
[true, true, true]
3
4
""undefined method 'nope' for class 'Object'""
""undefined method 'nope' for class 'Object'""
false
true
");
            // the host still reads the methods from the scope
            Assert(scope.GetVariable<Func<int>>("foo")() == 1);
            Assert(scope.GetVariable<Func<int>>("pub")() == 3);

            // A singleton method of main (`def self.x') is not Object's: CRuby's `alias'/`undef'/`private' of it raise
            // NameError and its define_method TypeError, and a host's scope has them act on main's singleton class alone,
            // as before.
            AssertOutput(() => Engine.Execute(@"
def self.single = 6
alias single2 single
define_method(:single3, method(:single))
private :single
p single2, single3, Object.method_defined?(:single2), Object.method_defined?(:single3), Object.private_method_defined?(:single)
undef single2
p respond_to?(:single2, true)
", scope), @"
6
6
false
false
false
false
");

            // each scope's top level still finds its own; an instance of another class finds Object's, which the last
            // scope to run made, as in CRuby
            var scope1 = Engine.CreateScope();
            var scope2 = Engine.CreateScope();
            Engine.Execute("def op = 1; alias op2 op; class Calc177; def run = op2; end", scope1);
            Engine.Execute("def op = 2; alias op2 op", scope2);
            Assert(Engine.Execute<int>("op2", scope1) == 1);
            Assert(Engine.Execute<int>("op2", scope2) == 2);
            Assert(Engine.Execute<int>("Calc177.new.run", scope1) == 2);
            Engine.Execute("undef op2", scope2);
            Assert(Engine.Execute<int>("op2", scope1) == 1);
            AssertExceptionThrown<MemberAccessException>(() => Engine.Execute("Calc177.new.run", scope1));
        }

        public void RubyHosting1E() {
            var scope = Engine.CreateScope();
            scope.SetVariable("baz", 1);
            scope.SetVariable("Baz", 2);
            scope.SetVariable("Boo", 3);

            AssertOutput(() =>
                Engine.Execute(@"
class Object
  # Deliberately written without #puts or a block: every conversion the
  # interpreter attempts on the way - #to_ary for the argument list, #to_ary to
  # destructure a block parameter - now reaches method_missing, so a
  # method_missing defined this widely and implemented with either would call
  # itself forever. MRI warns about exactly this when Object#method_missing is
  # redefined.
  def method_missing *args
    $stdout.write(args[0].to_s + ""\n"" + args[1].to_s + ""\n"" + args[2].to_s + ""\n"" + args[3].to_s + ""\n"")
  end
end

bar(baz, Baz(), boo)
", scope), @"
bar
1
2
3
");

        }

        /// <summary>
        /// method_missing on main singleton can be invoked directly.
        /// </summary>
        public void RubyHosting1F() {
            var scope = Engine.CreateScope();
            scope.SetVariable("bar", 1);

            // TODO: this should print the value of :bar
            AssertOutput(() =>
                Engine.Execute(@"
puts method_missing(:bar) rescue p $!
", scope), @"
#<NoMethodError: undefined method 'bar' for main>
");
        }

        /// <summary>
        /// A name the scope does not have goes on to the method_missing above main's, with the name and
        /// the arguments - and a call signature that counts them right, which a define_method body checks.
        /// </summary>
        public void RubyHosting1G() {
            var scope = Engine.CreateScope();
            scope.SetVariable("bar", 1);

            AssertOutput(() =>
                Engine.Execute(@"
class Object
  define_method(:method_missing) { |name, arg| $stdout.write(name.to_s + ' ' + arg.to_s + ""\n"") }
end
begin
  foo 2
  p bar
ensure
  class Object; remove_method :method_missing; end
end
", scope), @"
foo 2
1
");
        }

        public void RubyHosting2() {
            Hashtable hash = new Hashtable();
            hash.Add("foo", "bar");
            
            ScriptScope scope = Engine.CreateScope();
            scope.SetVariable("h", hash);

            AssertOutput(() => 
                Engine.Execute(@"
def h.method_missing name
  get_Item(name.to_clr_string)
end

puts h.foo
", scope), @"
bar
");
        }

        public void RubyHosting3() {
            object value;
            Engine.Execute("C = 1");

            // non-module values are not published:
            Assert(!Runtime.Globals.TryGetVariable("C", out value));

            // built-ins are not published:
            Assert(!Runtime.Globals.TryGetVariable("Object", out value));
            Assert(!Runtime.Globals.TryGetVariable("String", out value));

            // global modules and classes are published:
            Engine.Execute("class D; end");
            Assert(Runtime.Globals.TryGetVariable("D", out value));
            Assert(((RubyClass)value).Name == "D");
            
            // assignment to a constant on Object class also publishes modules and classes:
            Engine.Execute("E = Module.new");
            Assert(Runtime.Globals.TryGetVariable("E", out value));
            Assert(((RubyModule)value).Name == "E");

            bool result = Engine.RequireFile("fcntl");
            Assert(result == true);

            // built-in class:
            Assert(Context.ObjectClass.TryGetConstant(null, "String", out value) 
                && ((RubyModule)value).Restrictions == ModuleRestrictions.Builtin);

            // IronRuby specific built-in class:
            Assert(Context.ObjectClass.TryGetConstant(null, "IronRuby", out value)
                && ((RubyModule)value).Restrictions == ModuleRestrictions.NotPublished);

            // a class from standard library:
            Assert(Context.ObjectClass.TryGetConstant(null, "Fcntl", out value)
                && ((RubyModule)value).Restrictions == (ModuleRestrictions.None | ModuleRestrictions.NoUnderlyingType));

            // standard library classes are also published (whether implemented in C# or not):
            var module = Runtime.Globals.GetVariable("Fcntl");
            Assert(module is RubyModule && ((RubyModule)module).Name == "Fcntl");
        }

        public void RubyHosting4() {
            Runtime.Globals.SetVariable("foo_bar", 1);
            Engine.Execute(@"
IronRuby.globals.x = 2
IronRuby.globals.z = IronRuby.globals.x + FooBar
");
            Assert(Runtime.Globals.GetVariable<int>("z") == 3);

            dynamic scope = Engine.CreateScope();
            Engine.Execute(@"def foo; 1; end", scope);

            RubyMethod method = (RubyMethod)scope.foo;
            Assert(method.Name == "foo");

            object value = scope.foo();
            Assert((int)value == 1);
        }

        public void RubyHosting5() {
            // app-domain creation:
            Assert(Engine.RequireFile("fcntl") == true);
            Assert(Engine.Execute<bool>("Object.constants.include?(:Fcntl)") == true);
        }

        public void RubyHosting_Scopes1() {
            TestOutput(@"
engine = IronRuby.create_engine
scope = engine.create_scope
scope.x = 1
scope.y = 2
p scope.x + scope.y
", @"
3
");
        }

        public void RubyHosting_Scopes2() {
            var s = Engine.CreateScope();
            Context.ObjectClass.SetConstant("S", s);
            s.SetVariable("FooBar", 123);

            TestOutput(@"
p S.get_variable('FooBar')
p S.get_variable('foo_bar')
p S.GetVariable('FooBar')
p S.GetVariable('foo_bar')
", @"
123
123
123
123
");
        }

        public void RubyHosting_Scopes3() {
            // TODO: test other backing storages (not implemented yet):

            var variables = new Dictionary<string, object>();
            variables["x"] = 1;
            variables["y"] = 3;

            var scope = Engine.CreateScope(variables);

            string script = @"
def foo
  x + y
end
";
            Engine.Execute(script, scope);

            Assert(scope.GetVariable<int>("x") == 1);
            Assert(scope.GetVariable<int>("y") == 3);

            var foo = scope.GetVariable<Func<int>>("foo");
            Assert(foo() == 4);
        }

        public void HostingDefaultOptions1() {
            // this reports warnings that the default ErrorSink should ignore:
            Engine.Execute(@"
x = lambda { |i| }
1.times &x

a = 'ba'.gsub /b/, '1'
");

            // errors and fatal errors should trigger an exception:
            AssertExceptionThrown<SyntaxErrorException>(() => Engine.Execute("}"));
        }

        /// <summary>
        /// -E takes "locale" besides the names of real encodings, as MRI's does; "external" and
        /// "filesystem", which Encoding.find knows, are unknown encoding names there.
        /// </summary>
        public void HostingEncodingOption1() {
            foreach (var arg in new[] { "-Eexternal", "-Efilesystem", "-Eutf-8:external", "--external-encoding=filesystem" }) {
                AssertExceptionThrown<Microsoft.Scripting.Hosting.Shell.InvalidOptionException>(() =>
                    new IronRuby.Hosting.RubyOptionsParser().Parse(new[] { arg, "-e", "1" }, new ScriptRuntimeSetup(), Ruby.CreateRubySetup(), PlatformAdaptationLayer.Default));
            }
            new IronRuby.Hosting.RubyOptionsParser().Parse(new[] { "-Elocale:locale", "-e", "1" }, new ScriptRuntimeSetup(), Ruby.CreateRubySetup(), PlatformAdaptationLayer.Default);
        }

        public void Interactive1() {
            ScriptScope scope = Runtime.CreateScope();
            AssertOutput(() => Engine.CreateScriptSourceFromString("", SourceCodeKind.InteractiveCode).Execute(scope), "");
            AssertOutput(() => Engine.CreateScriptSourceFromString("x = 1 + 1", SourceCodeKind.InteractiveCode).Execute(scope), "=> 2");
        }

        public void Interactive2() {
            string s;
            Assert((s = Engine.Operations.Format(new RubyArray(new[] { 1,2,3 }))) == "[1, 2, 3]");
            
            // Format asks for #inspect, which no longer falls back to #to_s: an object that
            // defines only #to_s formats the way MRI shows it, by class and id.
            var obj = Engine.Execute(@"class C; def to_s; 'hello'; end; new; end");
            Assert((s = Engine.Operations.Format(obj)).StartsWith("#<C:0x"));

            obj = Engine.Execute(@"class C; def inspect; 'bye'; end; new; end");
            Assert((s = Engine.Operations.Format(obj)) == "bye");
            
            obj = Engine.Execute(@"class C; def inspect; [7,8,9]; end; new; end");
            Assert((s = Engine.Operations.Format(obj)) == "[7, 8, 9]");

            var scope = Engine.CreateScope();
            scope.SetVariable("ops", Engine.Operations);
            s = Engine.Execute<string>(@"ops.format({1 => 2, 3 => 4})", scope);
            Assert(s == "{1 => 2, 3 => 4}");
        }

        public void Scenario_RubyConsole1() {
            ScriptScope module = Runtime.CreateScope();
            AssertOutput(delegate() {
                Engine.CreateScriptSourceFromString("class C; def foo() puts 'foo'; end end", SourceCodeKind.InteractiveCode).Execute(module);
                Engine.CreateScriptSourceFromString("C.new.foo", SourceCodeKind.InteractiveCode).Execute(module);
            }, @"
=> :foo
foo
=> nil
");
        }
        
        public void CrossRuntime1() {
            AssertOutput(() => {
                CompilerTest(@"
engine = IronRuby.create_engine
puts engine.execute('1+1')
puts engine.execute('Integer')
engine.execute('class Integer; def + other; 123; end; end')
puts 1+1
puts engine.execute('1+1')
");
            }, @"
2
Integer@*
2
123
", OutputFlags.Match);
        }
        
        public void CrossRuntime2() {
            Engine.Execute(@"
C = IronRuby.create_engine.execute <<end
  class C
    def bar
    end
  end
  C
end
");
            // can't define a method on a foreign class:
            AssertExceptionThrown<InvalidOperationException>(() => Engine.Execute("C.send(:define_method, :foo) {}"));

            // can't open a scope of a foreign class:
            AssertExceptionThrown<InvalidOperationException>(() => Engine.Execute("class C; end"));

            // alias operates in the runtime of the class within which scope it is used:
            Engine.Execute("C.send(:alias_method, :foo, :bar); C.new.foo");
        }
        
        public void ObjectOperations1() {
            var cls = Engine.Execute(@"
class C
  def foo *a
    p a
  end
  self
end
");
            var obj = Engine.Operations.CreateInstance(cls) as RubyObject;
            Assert(obj != null && obj.ImmediateClass.Name == "C");

            obj = Engine.Operations.InvokeMember(cls, "new") as RubyObject;
            Assert(obj != null && obj.ImmediateClass.Name == "C");

            var foo = Engine.Operations.GetMember(obj, "foo") as RubyMethod;
            Assert(foo != null && foo.Name == "foo" && foo.Target == obj);

            AssertOutput(() => Engine.Operations.Invoke(foo, 1, 2), "[1, 2]");
            AssertOutput(() => Engine.Operations.InvokeMember(obj, "foo", 1, 2), "[1, 2]");

            var str = Engine.Operations.ConvertTo<string>(MutableString.CreateAscii("foo"));
            Assert(str == "foo");

            str = Engine.Operations.ConvertTo<string>(Engine.Execute<object>("class C; def to_str; 'bar'; end; new; end"));
            Assert(str == "bar");

            var b = Engine.Operations.ConvertTo<byte>(Engine.Execute<object>("class C; def to_int; 123; end; new; end"));
            Assert(b == 123);

            var lambda = Engine.Operations.ConvertTo<Func<int, int>>(Engine.Execute<object>("lambda { |x| x * 2 }"));
            Assert(lambda(10) == 20);

            Assert((int)Engine.CreateOperations().InvokeMember(null, "to_i") == 0);
        }

        public void ObjectOperations2() {
            object cls = Engine.Execute(@"
class C
  def foo *a
    p a
  end
  def self.bar
  end
  undef_method :freeze
  self
end
");
            var names = Engine.Operations.GetMemberNames(cls);
            Assert(!names.Contains("foo"));
            Assert(names.Contains("instance_variables"));
            Assert(names.Contains("bar"));

            object obj = Engine.Operations.CreateInstance(cls);
            names = Engine.Operations.GetMemberNames(obj);
            Assert(names.Contains("foo"));
            Assert(names.Contains("instance_variables"));
            Assert(!names.Contains("freeze"));
            Assert(!names.Contains("bar"));
        }

        public void CustomTypeDescriptor1() {
            object cls = Engine.Execute(@"
class C
  attr_accessor :a
  def b
  end
  def b= x
    @written = true
  end
  def c
    @written
  end
  def d= x
  end
  def e x
  end
  def e= x
  end
  def initialize
    @a = 'Hello world'
    @written = false
  end
  self
end
");
            object obj = Engine.Operations.CreateInstance(cls);
            Assert(obj != null);
            var ictd = Engine.Operations.CreateInstance(cls) as ICustomTypeDescriptor;
            Assert(ictd != null);
            Assert(ictd.GetClassName() == "C");
            var props = ictd.GetProperties();
            Assert(props.Count == 2);
            props[0].SetValue(obj, "abc");
            props[1].SetValue(obj, "abc");
            Assert((string)Engine.Operations.InvokeMember(obj, "a") == "abc");
            Assert((bool)Engine.Operations.InvokeMember(obj, "c"));
        }

        public void CustomTypeDescriptor2() {
            Context.ObjectClass.SetConstant("C", Context.GetClass(typeof(ArrayList)));
            var cls = Engine.Execute(@"
class D < C
  attr_accessor :a
  self
end
");
            var obj = Engine.Operations.CreateInstance(cls);
            Assert(obj != null);
            var ictd = Engine.Operations.CreateInstance(cls) as ICustomTypeDescriptor;
            Assert(ictd != null);
            Assert(ictd.GetClassName() == "D");
            var props = ictd.GetProperties();
            Assert(props.Count == 1);
            props[0].SetValue(obj, "abc");
            Assert(Engine.Operations.InvokeMember(obj, "a").Equals("abc"));
        }
    }
}

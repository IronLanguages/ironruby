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
using System.Diagnostics;
using System.IO;
using System.Dynamic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Hosting.Providers;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime.Calls;

namespace IronRuby.Tests {
    public partial class Tests {

        public void Loader_Assemblies1() {
            string assembly;
            string type;
            string str;
            bool b;
            
            str = "a.rb";
            b = Loader.TryParseAssemblyName(str, out type, out assembly);
            Assert(b == false);

            str = "IronRuby";
            b = Loader.TryParseAssemblyName(str, out type, out assembly);
            Assert(b == false);
            
            str = @"..\foo\bar\a,b.rb";
            b = Loader.TryParseAssemblyName(str, out type, out assembly);
            Assert(b == false);
            
            str = "IronRuby.Runtime.RubyContext, IronRuby, Version=" + IronRuby.CurrentVersion.DisplayVersion + ", Culture=neutral, PublicKeyToken=31bf3856ad364e35";
            b = Loader.TryParseAssemblyName(str, out type, out assembly);
            Assert(b == true &&
                assembly == "IronRuby, Version=" + IronRuby.CurrentVersion.DisplayVersion + ", Culture=neutral, PublicKeyToken=31bf3856ad364e35" &&
                type == "IronRuby.Runtime.RubyContext"
            );

            str = "IronRuby, Version=" + IronRuby.CurrentVersion.DisplayVersion + ", Culture=neutral, PublicKeyToken=31bf3856ad364e35";
            b = Loader.TryParseAssemblyName(str, out type, out assembly);
            Assert(b == true && assembly == str && type == null);

            str = "IronRuby, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
            b = Loader.TryParseAssemblyName(str, out type, out assembly);
            Assert(b == true && assembly == str && type == null);

            str = "IronRuby, Version=" + IronRuby.CurrentVersion.DisplayVersion;
            b = Loader.TryParseAssemblyName(str, out type, out assembly);
            Assert(b == true && assembly == str && type == null);
        }

        public void Require1() {
            if (_driver.PartialTrust) return;

            try {
                string temp = _driver.MakeTempDir();
                Context.Loader.SetLoadPaths(new[] { temp });
                File.WriteAllText(Path.Combine(temp, "a.rb"), @"C = 123");

                AssertOutput(delegate() {
                    CompilerTest(@"
puts(require('a'))
puts C
");
                }, @"
true
123
");

                AssertOutput(delegate() {
                    CompilerTest(@"
puts(require('a.rb'))
puts C
");
                }, @"
false
123
");
            } finally {
                File.Delete("a.rb");
            }
        }

        public void Load1() {
            if (_driver.PartialTrust) return;

            try {
                string temp = _driver.MakeTempDir();
                Context.Loader.SetLoadPaths(new[] { temp });

                File.WriteAllText(Path.Combine(temp, "a.rb"), @"C = 123");
                
                AssertOutput(delegate() {
                    CompilerTest(@"
puts(load('a.rb', true))
puts C rescue puts 'error'
");
                }, @"
true
error
");
            } finally {
                File.Delete("a.rb");
            }
        }

        public void RequireInterop1() {
            if (_driver.PartialTrust || !_driver.RunPython) return;

            try {
                string temp = _driver.MakeTempDir();
                Context.Loader.SetLoadPaths(new[] { temp });

                File.WriteAllText(Path.Combine(temp, "a.py"), @"
print 'Hello from Python'
");
                AssertExceptionThrown<LoadError>(() => CompilerTest(@"require('a')"));
            } finally {
                File.Delete("b.py");
            }
        }

        public void RequireInterop2() {
            if (_driver.PartialTrust || !_driver.RunPython) return;

            try {
                string temp = _driver.MakeTempDir();
                Context.Loader.SetLoadPaths(new[] { temp });

                File.WriteAllText(Path.Combine(temp, "a.py"), @"
def WhoIsThis():
  print 'Python'

Foo = 1

class Bar(object):
  def baz(self):
    print Foo
");

                TestOutput(@"
a = IronRuby.require('a')
scopes = IronRuby.loaded_scripts.collect { |z| z.value }
a.who_is_this
a.foo += 1
a.bar.baz             # a Python class is callable so we get Bar's instance from a.bar
puts scopes[0].Foo
", @"
Python
2
2
");

            } finally {
                File.Delete("b.py");
            }
        }

        public class TestLibraryInitializer1 : LibraryInitializer {
            protected override void LoadModules() {
                Context.ObjectClass.SetConstant("TEST_LIBRARY", "hello from library");
                ExtendClass(typeof(Object), 0, null, ObjectMonkeyPatch, null, null, RubyModule.EmptyArray);  
            }

            private void ObjectMonkeyPatch(RubyModule/*!*/ module) {
                Debug.Assert(module == Context.ObjectClass);

                DefineLibraryMethod(module, "object_monkey", 0x9, new[] {
                    LibraryOverload.Reflect(new Func<object, string>(MonkeyWorker)),
                });
            }

            public static string MonkeyWorker(object obj) {
                return "This is monkey!";
            }
        }

        public void LibraryLoader1() {
            Context.DefineGlobalVariable("lib_name", MutableString.CreateAscii(typeof(TestLibraryInitializer1).AssemblyQualifiedName));

            AssertOutput(delegate() {
                CompilerTest(@"
require($lib_name)
puts TEST_LIBRARY
puts object_monkey
");
            }, @"
hello from library
This is monkey!
");

        }

        public class TestLibraryInitializer2 : LibraryInitializer {
            protected override void LoadModules() {
                DefineGlobalModule("LibModule", typeof(Object), 0, null, LibModuleSingletonMethods, null, RubyModule.EmptyArray);
            }

            private void LibModuleSingletonMethods(RubyModule/*!*/ module) {
                DefineLibraryMethod(module, "bar", (int)RubyMethodAttributes.PublicSingleton, new[] {
                    LibraryOverload.Reflect(new Func<RubyModule, string>(Bar)),
                });
            }

            public static string Bar(RubyModule/*!*/ self) {
                return "bar";
            }
        }

        public void LibraryLoader2() {
            Context.DefineGlobalVariable("lib_name", MutableString.CreateAscii(typeof(TestLibraryInitializer2).AssemblyQualifiedName));

            TestOutput(@"
module LibModule
  def self.foo
    'foo'
  end
end
require($lib_name)
puts LibModule.foo
puts LibModule.bar
", @"
foo
bar
");

        }

        #region DLLs required by path

        // The fixtures are PathLoadedWidgets.dll, with a class PathLoadedFixture.Widget, and DLLs built against it, each with a
        // static class PathLoadedFixture.WidgetExtensions whose extension method Shout(this Widget) returns "shout". require
        // loads a DLL by path with Assembly.LoadFile, which gives it a load context of its own: a reference it makes to
        // another DLL isn't found there by name, so the Loader's AssemblyResolve handler is asked.

        /// <summary>
        /// Two DLLs required by path, the second built against the first. Once extension methods are active, each
        /// assembly's are read as it loads, and reading Shout's parameter needs Widget: the second require raised a
        /// LoadError, as the handler looked for PathLoadedWidgets on $LOAD_PATH only.
        /// </summary>
        public void Require_PathLoadedDependency1() {
            if (_driver.PartialTrust) return;

            string dir = MakeFixtureDir();
            WriteFixtureAssembly(dir, "PathLoadedWidgets", "1.0.0.0", null);
            WriteFixtureAssembly(dir, "PathLoadedExtensions", "1.0.0.0", "1.0.0.0");

            // what using_clr_extensions does: from here on, an assembly's extension methods are read as it loads
            Context.ActivateExtensions("PathLoadedFixture");

            TestOutput(@"
require ""#{$fixtures}/PathLoadedWidgets.dll""
w = PathLoadedFixture::Widget.new
puts((w.shout rescue $!.class))
require ""#{$fixtures}/PathLoadedExtensions.dll""
puts w.shout
", @"
NoMethodError
shout
");
        }

        /// <summary>
        /// A reference that no loaded DLL and nothing on $LOAD_PATH resolves is looked for next to the DLL making it,
        /// as .NET Framework's LoadFrom context did; the DLL found there is loaded as require would load it.
        /// </summary>
        public void Require_PathLoadedDependency2() {
            if (_driver.PartialTrust) return;

            string dir = MakeFixtureDir();
            WriteFixtureAssembly(dir, "PathLoadedWidgets", "1.0.0.0", null);
            WriteFixtureAssembly(dir, "PathLoadedExtensions", "1.0.0.0", "1.0.0.0");
            Context.ActivateExtensions("PathLoadedFixture");

            TestOutput(@"
require ""#{$fixtures}/PathLoadedExtensions.dll""
puts PathLoadedFixture::Widget.new.shout
puts IronRuby.loaded_assemblies.map { |a| a.get_name.name.to_s }.grep(/PathLoaded/).sort
", @"
shout
PathLoadedExtensions
PathLoadedWidgets
");
        }

        /// <summary>
        /// A reference to a DLL that is loaded already is to that one, though there are copies of it on $LOAD_PATH and
        /// next to the DLL making the reference. A copy would be loaded a second time with types of its own, and Shout
        /// would not take the Widgets Ruby made from the first.
        /// </summary>
        public void Require_PathLoadedDependency3() {
            if (_driver.PartialTrust) return;

            string dir = MakeFixtureDir();
            WriteFixtureAssembly(Path.Combine(dir, "a"), "PathLoadedWidgets", "1.0.0.0", null);
            WriteFixtureAssembly(Path.Combine(dir, "b"), "PathLoadedWidgets", "1.0.0.0", null);
            WriteFixtureAssembly(Path.Combine(dir, "b"), "PathLoadedExtensions", "1.0.0.0", "1.0.0.0");
            WriteFixtureAssembly(Path.Combine(dir, "c"), "PathLoadedWidgets", "1.0.0.0", null);
            Context.ActivateExtensions("PathLoadedFixture");

            TestOutput(@"
$LOAD_PATH << ""#{$fixtures}/c""
require ""#{$fixtures}/a/PathLoadedWidgets.dll""
w = PathLoadedFixture::Widget.new
require ""#{$fixtures}/b/PathLoadedExtensions.dll""
puts w.shout
puts System::AppDomain.current_domain.get_assemblies.count { |a|
  a.get_name.name.to_s == 'PathLoadedWidgets' && a.location.to_s.tr('\\', '/').start_with?($fixtures)
}
", @"
shout
1
");
        }

        /// <summary>
        /// A loaded DLL resolves a reference to the version it has or an earlier one, as .NET's binder lets an
        /// application's assemblies do, and not a reference to a later version.
        /// </summary>
        public void Require_PathLoadedDependency4() {
            if (_driver.PartialTrust) return;

            string dir = MakeFixtureDir();
            WriteFixtureAssembly(Path.Combine(dir, "a"), "PathLoadedWidgets", "2.0.0.0", null);
            WriteFixtureAssembly(Path.Combine(dir, "b"), "PathLoadedOlderExtensions", "1.0.0.0", "1.0.0.0");
            WriteFixtureAssembly(Path.Combine(dir, "b"), "PathLoadedNewerExtensions", "1.0.0.0", "3.0.0.0");
            Context.ActivateExtensions("PathLoadedFixture");

            TestOutput(@"
require ""#{$fixtures}/a/PathLoadedWidgets.dll""
require ""#{$fixtures}/b/PathLoadedOlderExtensions.dll""
puts PathLoadedFixture::Widget.new.shout
begin
  require ""#{$fixtures}/b/PathLoadedNewerExtensions.dll""
rescue LoadError
  puts $!.class
end
", @"
shout
LoadError
");
        }

        /// <summary>
        /// $LOAD_PATH is searched as it was, and before the directory of the DLL making the reference.
        /// </summary>
        public void Require_PathLoadedDependency5() {
            if (_driver.PartialTrust) return;

            string dir = MakeFixtureDir();
            WriteFixtureAssembly(Path.Combine(dir, "a"), "PathLoadedWidgets", "1.0.0.0", null);
            WriteFixtureAssembly(Path.Combine(dir, "b"), "PathLoadedWidgets", "1.0.0.0", null);
            WriteFixtureAssembly(Path.Combine(dir, "b"), "PathLoadedExtensions", "1.0.0.0", "1.0.0.0");
            Context.ActivateExtensions("PathLoadedFixture");

            TestOutput(@"
$LOAD_PATH << ""#{$fixtures}/a""
require ""#{$fixtures}/b/PathLoadedExtensions.dll""
puts PathLoadedFixture::Widget.new.shout
puts PathLoadedFixture::Widget.to_clr_type.assembly.location.to_s.tr('\\', '/') == ""#{$fixtures}/a/PathLoadedWidgets.dll""
", @"
shout
true
");
        }

        /// <summary>
        /// AssemblyResolve is the process's event, and of the runtimes in a process the one that hooked it first is
        /// asked first. A reference made by a DLL another runtime loaded is left to that runtime: answered with this
        /// runtime's copy of PathLoadedWidgets, found on its $LOAD_PATH or loaded, it would make Shout take this
        /// runtime's Widgets and not the other's.
        /// </summary>
        public void Require_PathLoadedDependency6() {
            if (_driver.PartialTrust) return;

            string dir = MakeFixtureDir();
            foreach (string subdir in new[] { "a", "b", "c" }) {
                WriteFixtureAssembly(Path.Combine(dir, subdir), "PathLoadedWidgets", "1.0.0.0", null);
            }
            WriteFixtureAssembly(Path.Combine(dir, "b"), "PathLoadedExtensions", "1.0.0.0", "1.0.0.0");
            WriteFixtureAssembly(Path.Combine(dir, "c"), "PathLoadedExtensions", "1.0.0.0", "1.0.0.0");

            Engine.Execute(@"$LOAD_PATH << ""#{$fixtures}/a""");
            Assert(ShoutInAnotherRuntime(Path.Combine(dir, "b")) == "shout");

            Engine.Execute(@"require 'PathLoadedWidgets.dll'");
            Assert(ShoutInAnotherRuntime(Path.Combine(dir, "c")) == "shout");
        }

        private static string/*!*/ ShoutInAnotherRuntime(string/*!*/ dir) {
            var engine = Ruby.CreateEngine();
            try {
                var context = (RubyContext)HostingHelpers.GetLanguageContext(engine);
                context.DefineGlobalVariable("dir", MutableString.Create(dir.Replace('\\', '/'), RubyEncoding.UTF8));
                context.ActivateExtensions("PathLoadedFixture");

                return engine.Execute(@"
require ""#{$dir}/PathLoadedWidgets.dll""
require ""#{$dir}/PathLoadedExtensions.dll""
PathLoadedFixture::Widget.new.shout
").ToString();
            } finally {
                engine.Runtime.Shutdown();
            }
        }

        /// <summary>
        /// A require that fails reading the DLL's extension methods, because a type in their signatures can't be found,
        /// can be retried once it can: the DLL isn't in $LOADED_FEATURES, and the second require reads them. It used to
        /// return true without reading them, as the runtime had seen the assembly already.
        /// </summary>
        public void Require_UnreadableExtensions1() {
            if (_driver.PartialTrust) return;

            string dir = MakeFixtureDir();
            WriteFixtureAssembly(Path.Combine(dir, "a"), "PathLoadedWidgets", "1.0.0.0", null);
            WriteFixtureAssembly(Path.Combine(dir, "b"), "PathLoadedExtensions", "1.0.0.0", "1.0.0.0");
            Context.ActivateExtensions("PathLoadedFixture");

            TestOutput(@"
begin
  require ""#{$fixtures}/b/PathLoadedExtensions.dll""
rescue LoadError
  puts $!.class
end
puts $LOADED_FEATURES.grep(/PathLoadedExtensions/).size
require ""#{$fixtures}/a/PathLoadedWidgets.dll""
puts require(""#{$fixtures}/b/PathLoadedExtensions.dll"")
puts PathLoadedFixture::Widget.new.shout
", @"
LoadError
0
true
shout
");
        }

        /// <summary>
        /// The extension methods of DLLs required before any are asked for are read when they first are. One DLL whose
        /// extension methods can't be read gets a warning and doesn't cost the others theirs. The activation (using_clr_extensions
        /// or `using') used to raise, and the DLLs queued after that one never had theirs read. The next activation of its
        /// namespace tries it again.
        /// </summary>
        public void Require_UnreadableExtensions2() {
            if (_driver.PartialTrust) return;

            string dir = MakeFixtureDir();
            WriteFixtureAssembly(dir, "PathLoadedWidgets", "1.0.0.0", null);
            WriteFixtureAssembly(dir, "PathLoadedNewerExtensions", "1.0.0.0", "3.0.0.0");
            WriteFixtureAssembly(dir, "PathLoadedWhisper", "1.0.0.0", "1.0.0.0", "Whisper");

            TestOutput(@"
require ""#{$fixtures}/PathLoadedWidgets.dll""
require ""#{$fixtures}/PathLoadedNewerExtensions.dll""
require ""#{$fixtures}/PathLoadedWhisper.dll""
puts :required
", @"
required
");

            Context.ActivateExtensions("PathLoadedFixture");
            Assert(Context.RuntimeErrorSink.WarningCount == 1);

            TestOutput(@"
w = PathLoadedFixture::Widget.new
puts w.whisper
puts((w.shout rescue $!.class))
", @"
whisper
NoMethodError
", 0, 1);

            // PathLoadedWidgets 3.0 still can't be found: reported again rather than raised
            Context.ActivateExtensions("PathLoadedFixture");
            Assert(Context.RuntimeErrorSink.WarningCount == 2);
        }

        /// <summary>
        /// The test's temp directory, emptied, as $fixtures. A DLL loaded from it stays loaded until the process
        /// exits, and on Windows can't be deleted until then, so each run starts over rather than cleaning up.
        /// </summary>
        private string/*!*/ MakeFixtureDir() {
            string dir = _driver.MakeTempDir();
            Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Context.DefineGlobalVariable("fixtures", MutableString.Create(dir.Replace('\\', '/'), RubyEncoding.UTF8));
            return dir;
        }

        /// <summary>
        /// Writes <paramref name="name"/>.dll, version <paramref name="version"/>, to <paramref name="dir"/>: with the Widget
        /// class, or, given the version of PathLoadedWidgets to build against, with WidgetExtensions. Its method is Shout, or
        /// <paramref name="method"/> in a class named after it, and returns its name in lower case. It is emitted with
        /// System.Reflection.Metadata because PersistedAssemblyBuilder needs .NET 9, and these tests run on .NET 8 too.
        /// </summary>
        private static void WriteFixtureAssembly(string/*!*/ dir, string/*!*/ name, string/*!*/ version, string widgetsVersion, string method = "Shout") {
            var metadata = new MetadataBuilder();
            var il = new BlobBuilder();
            var bodies = new MethodBodyStreamEncoder(il);

            metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default(GuidHandle), default(GuidHandle));
            var assembly = metadata.AddAssembly(metadata.GetOrAddString(name), new Version(version), default(StringHandle), default(BlobHandle), 0,
                AssemblyHashAlgorithm.None);

            AssemblyName coreLibrary = typeof(object).Assembly.GetName();
            var core = metadata.AddAssemblyReference(metadata.GetOrAddString(coreLibrary.Name), coreLibrary.Version, default(StringHandle),
                metadata.GetOrAddBlob(coreLibrary.GetPublicKeyToken()), 0, default(BlobHandle));
            var objectType = metadata.AddTypeReference(core, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));

            var ctorSignatureBlob = new BlobBuilder();
            new BlobEncoder(ctorSignatureBlob).MethodSignature(isInstanceMethod: true).Parameters(0, r => r.Void(), p => { });
            var ctorSignature = metadata.GetOrAddBlob(ctorSignatureBlob);

            metadata.AddTypeDefinition(0, default(StringHandle), metadata.GetOrAddString("<Module>"), default(EntityHandle),
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

            var code = new InstructionEncoder(new BlobBuilder());
            if (widgetsVersion == null) {
                // public class Widget { }
                code.LoadArgument(0);
                code.Call(metadata.AddMemberReference(objectType, metadata.GetOrAddString(".ctor"), ctorSignature));
                code.OpCode(ILOpCode.Ret);
                var ctor = metadata.AddMethodDefinition(
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.IL, metadata.GetOrAddString(".ctor"), ctorSignature, bodies.AddMethodBody(code),
                    MetadataTokens.ParameterHandle(1));

                metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
                    metadata.GetOrAddString("PathLoadedFixture"), metadata.GetOrAddString("Widget"), objectType,
                    MetadataTokens.FieldDefinitionHandle(1), ctor);
            } else {
                // public static class WidgetExtensions { public static string Shout(this Widget w) => "shout"; }
                var widgets = metadata.AddAssemblyReference(metadata.GetOrAddString("PathLoadedWidgets"), new Version(widgetsVersion),
                    default(StringHandle), default(BlobHandle), 0, default(BlobHandle));
                var widget = metadata.AddTypeReference(widgets, metadata.GetOrAddString("PathLoadedFixture"), metadata.GetOrAddString("Widget"));
                var extensionAttribute = metadata.AddMemberReference(
                    metadata.AddTypeReference(core, metadata.GetOrAddString("System.Runtime.CompilerServices"), metadata.GetOrAddString("ExtensionAttribute")),
                    metadata.GetOrAddString(".ctor"), ctorSignature);
                var noArguments = metadata.GetOrAddBlob(new byte[] { 1, 0, 0, 0 });

                var shoutSignature = new BlobBuilder();
                new BlobEncoder(shoutSignature).MethodSignature().Parameters(1, r => r.Type().String(), p => p.AddParameter().Type().Type(widget, false));
                code.LoadString(metadata.GetOrAddUserString(method.ToLowerInvariant()));
                code.OpCode(ILOpCode.Ret);
                var shout = metadata.AddMethodDefinition(
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                    MethodImplAttributes.IL, metadata.GetOrAddString(method), metadata.GetOrAddBlob(shoutSignature), bodies.AddMethodBody(code),
                    metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString("w"), 1));

                var extensions = metadata.AddTypeDefinition(
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                    metadata.GetOrAddString("PathLoadedFixture"), metadata.GetOrAddString(method == "Shout" ? "WidgetExtensions" : method + "Extensions"), objectType,
                    MetadataTokens.FieldDefinitionHandle(1), shout);

                metadata.AddCustomAttribute(assembly, extensionAttribute, noArguments);
                metadata.AddCustomAttribute(extensions, extensionAttribute, noArguments);
                metadata.AddCustomAttribute(shout, extensionAttribute, noArguments);
            }

            var image = new BlobBuilder();
            new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), il).Serialize(image);
            Directory.CreateDirectory(dir);
            using (var file = File.Create(Path.Combine(dir, name + ".dll"))) {
                image.WriteContentTo(file);
            }
        }

        #endregion
    }
}

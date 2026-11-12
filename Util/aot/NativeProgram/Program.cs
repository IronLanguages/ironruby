// The native executable's entry point hands over to the compiled Ruby program's own Main
// (RubyProgram.Main in the assembly IronRuby.Aot wrote). That assembly is rooted for ILC
// (TrimmerRootAssembly), so its IL - the Ruby - is compiled to native code with IronRuby.
// It is reached by name because it references System.Private.CoreLib directly (it was emitted
// against the runtime, not against reference assemblies), which C# cannot compile against.
using System;
using System.Reflection;

internal static class NativeEntry {
    private static int Main(string[] args) {
        string name = null;
        foreach (var a in typeof(NativeEntry).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()) {
            if (a.Key == "RubyProgram") name = a.Value;
        }
        var program = Type.GetType("RubyProgram, " + name, throwOnError: true);
        return (int)program.GetMethod("Main", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { args });
    }
}

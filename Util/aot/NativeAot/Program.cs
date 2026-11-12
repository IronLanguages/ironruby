// The `ir` console host, as a NativeAOT executable (see ../README.md, part B).
// Same as Src/Console/Program.cs except where NativeAOT needs it to differ; each difference
// says why.
using System;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Hosting;
using IronRuby.Runtime;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;
using Microsoft.Scripting.Hosting.Shell;

internal sealed class Host : RubyConsoleHost {
    private static void OnCancelKey(ConsoleCancelEventArgs ev, RubyContext context, Thread mainThread) {
        if (ev.SpecialKey == ConsoleSpecialKey.ControlC) {
            ev.Cancel = true;
            Action handler = context.InterruptSignalHandler;
            if (handler != null) {
                try {
                    handler();
                } catch (Exception e) {
                    RubyUtils.RaiseAsyncException(mainThread, e);
                }
            }
        }
    }

    // Wall 1: ScriptRuntimeSetup.ReadConfiguration() reads app.config through
    // System.Configuration.ConfigurationManager, which is reflection all the way down and dies
    // at startup under NativeAOT. There is no app.config to read: build the setup directly.
    protected override ScriptRuntimeSetup CreateRuntimeSetup() {
        var setup = new ScriptRuntimeSetup();
        setup.LanguageSetups.Add(CreateLanguageSetup());
        return setup;
    }

    protected override IConsole CreateConsole(ScriptEngine engine, CommandLine commandLine, ConsoleOptions options) {
        IConsole console = base.CreateConsole(engine, commandLine, options);
        Thread mainThread = Thread.CurrentThread;
        RubyContext context = (RubyContext)HostingHelpers.GetLanguageContext(engine);
        context.InterruptSignalHandler = delegate() { RubyUtils.RaiseAsyncException(mainThread, new Interrupt()); };
        ((BasicConsole)console).ConsoleCancelEventHandler = delegate(object sender, ConsoleCancelEventArgs e) {
            OnCancelKey(e, context, mainThread);
        };
        return console;
    }

    protected override ConsoleOptions ParseOptions(string[] args, ScriptRuntimeSetup runtimeSetup, LanguageSetup languageSetup) {
        var rubyOptions = (RubyConsoleOptions)base.ParseOptions(args, runtimeSetup, languageSetup);
        if (rubyOptions == null) {
            return null;
        }
        // Diagnostic: IR_NO_PRELUDE=1 skips the Ruby half of the core library (ruby4.rb), to
        // look at the bare interpreter.
        if (Environment.GetEnvironmentVariable("IR_NO_PRELUDE") == "1" &&
            languageSetup.Options.TryGetValue("RequiredPaths", out object paths) && paths is System.Collections.Generic.List<string> list) {
            list.RemoveAll(p => p == "ruby4.rb" || p == "gem_prelude.rb");
        }
        if (rubyOptions.ChangeDirectory != null) {
            Environment.CurrentDirectory = rubyOptions.ChangeDirectory;
        }
        if (rubyOptions.DisplayVersion && (rubyOptions.Command != null || rubyOptions.FileName != null)) {
            Console.WriteLine(RubyContext.MakeDescriptionString(), Style.Out);
        }
        return rubyOptions;
    }

    protected override void PrintVersion() {
        Console.WriteLine(RubyContext.MakeDescriptionString());
    }

    protected override void ReportInvalidOption(InvalidOptionException e) {
        Console.Error.WriteLine(e.Message);
    }

    [STAThread]
    [RubyStackTraceHidden]
    static int Main(string[] args) {
        // Diagnostic: IR_FIRST_CHANCE=N prints the first N exceptions thrown anywhere (distinct
        // type + message), which is how the walls below were found.
        if (int.TryParse(Environment.GetEnvironmentVariable("IR_FIRST_CHANCE"), out int firstChance)) {
            var seen = new System.Collections.Generic.HashSet<string>();
            AppDomain.CurrentDomain.FirstChanceException += (sender, e) => {
                string key = e.Exception.GetType().FullName + ": " + e.Exception.Message;
                lock (seen) {
                    if (seen.Count < firstChance && seen.Add(key)) {
                        Console.Error.WriteLine("[first chance] " + key);
                        Console.Error.WriteLine(new System.Diagnostics.StackTrace(1, false).ToString());
                    }
                }
            };
        }
        if (Environment.GetEnvironmentVariable("IR_NATIVE_DIAG") == "1") {
            var bi = typeof(System.Numerics.BigInteger);
            Console.WriteLine("BigInteger methods visible to reflection: " + bi.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Length);
            try {
                var e = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Constant(1), bi);
                Console.WriteLine("Convert int->BigInteger: " + e.Method);
                Console.WriteLine("  evaluates to " + System.Linq.Expressions.Expression.Lambda<Func<object>>(System.Linq.Expressions.Expression.Convert(e, typeof(object))).Compile()());
            } catch (Exception ex) {
                Console.WriteLine("Convert int->BigInteger failed: " + ex.Message);
            }
            Console.WriteLine("IsDynamicCodeSupported: " + System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);
        }
        // Prism is the only parser; -X:UsePrism, which used to choose it, is accepted and ignored.
        if (Array.IndexOf(args, "-X:UsePrism") >= 0) {
            args = Array.FindAll(args, a => a != "-X:UsePrism");
        }
        RubyContext.AlternativeParser = IronRuby.Prism.PrismAstBridge.Parse;
        return new Host().Run(args);
    }
}

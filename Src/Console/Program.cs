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

    // NativeAOT: ScriptRuntimeSetup.ReadConfiguration() goes through System.Configuration, which is
    // reflection all the way down and fails at startup there. ir has no app.config to read anyway,
    // so without dynamic code the setup is built directly.
    protected override ScriptRuntimeSetup CreateRuntimeSetup() {
        if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) {
            return base.CreateRuntimeSetup();
        }
        var setup = new ScriptRuntimeSetup();
        setup.LanguageSetups.Add(CreateLanguageSetup());
        return setup;
    }

    protected override IConsole CreateConsole(ScriptEngine engine, CommandLine commandLine, ConsoleOptions options) {
        IConsole console = base.CreateConsole(engine, commandLine, options);

        Thread mainThread = Thread.CurrentThread;
        RubyContext context = (RubyContext)HostingHelpers.GetLanguageContext(engine);
        context.InterruptSignalHandler = delegate() { RubyUtils.RaiseAsyncException(mainThread, new Interrupt(String.Empty)); };
        ((BasicConsole)console).ConsoleCancelEventHandler = delegate(object sender, ConsoleCancelEventArgs e) {
            OnCancelKey(e, context, mainThread); 
        };

        return console;
    }

    protected override ConsoleOptions ParseOptions(string/*!*/[]/*!*/ args, ScriptRuntimeSetup/*!*/ runtimeSetup, LanguageSetup/*!*/ languageSetup) {
        var rubyOptions = (RubyConsoleOptions)base.ParseOptions(args, runtimeSetup, languageSetup);
        if (rubyOptions == null) {
            return null;
        }

        if (rubyOptions.ChangeDirectory != null) {
            Environment.CurrentDirectory = rubyOptions.ChangeDirectory;
        }

        if (rubyOptions.DisplayVersion && (rubyOptions.Command != null || rubyOptions.FileName != null)) {
            Console.WriteLine(RubyContext.MakeDescriptionString());
        }

        return rubyOptions;
    }

    // ruby -v / --version print RUBY_DESCRIPTION
    protected override void PrintVersion() {
        Console.WriteLine(RubyContext.MakeDescriptionString());
    }

    protected override void ReportInvalidOption(InvalidOptionException e) {
        Console.Error.WriteLine(e.Message);
    }

    [STAThread]
    [RubyStackTraceHidden]
    static int Main(string[] args) {
        // Windows gives a process's main thread a 1 MB stack, Linux and macOS 8 MB: the program runs on a thread with
        // as much as a Ruby thread has (RubyUtils.ThreadStackSize, #129). Single-threaded apartment, as Main is, for
        // Windows Forms and WPF.
        if (OperatingSystem.IsWindows()) {
            var run = new ProgramRun { Args = args };
            var thread = new Thread(RunProgram, RubyUtils.ThreadStackSize);
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(run);
            thread.Join();
            return run.Status;
        }
        return RunProgram(args);
    }

    private sealed class ProgramRun {
        public string[] Args;
        public int Status;
    }

    // a method of its own, not a lambda, so that backtraces leave it out as they do Main
    [RubyStackTraceHidden]
    private static void RunProgram(object run) {
        var r = (ProgramRun)run;
        r.Status = RunProgram(r.Args);
    }

    [RubyStackTraceHidden]
    private static int RunProgram(string[] args) {
        // A process the warm-start broker started (Util/warm): see IronRuby.Hosting.WarmStart.
        WarmStart.InitializeFromEnvironment();

        // -X:UsePrism chose prism over the legacy parser. Prism is the only parser now; the switch
        // is still accepted, and ignored, so that scripts and older launchers that pass it work.
        if (Array.IndexOf(args, "-X:UsePrism") >= 0) {
            args = Array.FindAll(args, a => a != "-X:UsePrism");
        }
        RubyContext.AlternativeParser = IronRuby.Prism.PrismAstBridge.Parse;
        int status = new Host().Run(args);
        WarmStart.ReportExit(status);
        return status;
    }
}

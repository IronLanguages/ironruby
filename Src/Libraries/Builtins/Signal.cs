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
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    [RubyModule("Signal")]
    public static class Signal {
        #region Private Instance & Singleton Methods

        [RubyMethod("list", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ List(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            Hash result = new Hash(context);
            result.Add(MutableString.CreateAscii("EXIT"), ScriptingRuntimeHelpers.Int32ToObject(0));
            foreach (var entry in PosixSignals.Numbers) {
                result.Add(MutableString.CreateAscii(entry.Key), ScriptingRuntimeHelpers.Int32ToObject(entry.Value));
            }
            return result;
        }

        [RubyMethod("signame", RubyMethodAttributes.PublicSingleton)]
        public static MutableString Signame(RubyModule/*!*/ self, [DefaultProtocol]int number) {
            string name = PosixSignals.ToName(number);
            return (name != null) ? MutableString.CreateAscii(name) : null;
        }

        /// <summary>
        /// Installs a handler for a signal and returns the one it replaced. The handler cancels the
        /// platform's default disposition, so trapping SIGTERM really does stop SIGTERM from ending
        /// the process.
        /// </summary>
        [RubyMethod("trap", RubyMethodAttributes.PublicSingleton)]
        public static object Trap(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ callStorage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            RubyContext/*!*/ context,
            object self,
            object signalId,
            object command) {

            // MRI converts the signal with #to_str (never #to_int), and does so on every call.
            if (!(signalId is int) && !(signalId is MutableString) && !(signalId is RubySymbol)) {
                var converted = Protocols.TryCastToString(stringCast, signalId);
                if (converted != null) {
                    signalId = converted;
                }
            }

            int number = PosixSignals.ToNumber(context, signalId, true);

            // a non-main Ractor's handler runs in the main Ractor, so a block has to be isolated (CRuby's rb_proc_isolate)
            var handler = command as Proc;
            if (handler != null && RactorIsolation.IsNonMain(context) && !Shareability.IsShareable(context, handler)) {
                RubyRactor.Isolate(handler);
            }

            if (number == SignalExit) {
                // EXIT is not a signal at all - nothing sends it - so it is the runtime's
                // shutdown that has to run it, which is what this hands over.
                object exitCommand = command;
                context.ExitSignalHandler = PosixSignals.IsDefaultOrIgnore(exitCommand)
                    ? null
                    : new Action(() => {
                        var exitSite = callStorage.GetCallSite("call", 1);
                        exitSite.Target(exitSite, exitCommand, ScriptingRuntimeHelpers.Int32ToObject(0));
                    });
            }

            // MRI runs trap handlers on the main thread at an interrupt check, not on whatever
            // thread the signal landed on. PosixSignals parks them; this is where the main thread
            // is named and where the safe point that drains them is hooked up.
            PosixSignals.MainThread = context.MainThread;
            RubyUtils.SafePointHandler = PosixSignals.RunPending;

            // MRI takes a block, a Proc, a Method - anything that answers #call - so dispatch
            // dynamically rather than insisting on a Proc.
            var site = callStorage.GetCallSite("call", 1);
            var mainThread = context.MainThread;

            // MRI's default SIGINT disposition raises Interrupt on the main thread. Leaving it to
            // the platform instead makes `Signal.trap(:INT, :SIG_DFL); Process.kill :INT; sleep`
            // hang, because .NET's own SIGINT handling swallows it.
            Action<int> defaultInvoke = (number == SignalInterrupt && mainThread != null)
                ? new Action<int>(_ => RubyUtils.RaiseAsyncException(mainThread, new Interrupt(String.Empty)))
                : null;

            // No try/catch around the call: PosixSignals runs handlers on the main thread at a
            // safe point, so an exception out of one propagates there by itself, which is where
            // MRI raises it too.
            object previous = PosixSignals.Trap(number, command, signalNumber =>
                site.Target(site, command, ScriptingRuntimeHelpers.Int32ToObject(signalNumber)),
                defaultInvoke
            );

            if (number == SignalInterrupt) {
                // The console's Ctrl-C hook (Console.CancelKeyPress) runs InterruptSignalHandler on
                // .NET's signal thread. Where PosixSignals has a registration of its own for SIGINT,
                // that one already runs the handler, on the main thread: dispatching it from the hook
                // too ran a trap(:INT) block twice, once on the wrong thread, and raised a second
                // Interrupt into a program already unwinding from the first.
                var proc = command as Proc;
                context.InterruptSignalHandler = (proc != null && !PosixSignals.IsRegistered(number))
                    ? new Action(() => proc.Call(null))
                    : null;
            }
            return previous;
        }

        [RubyMethod("trap", RubyMethodAttributes.PublicSingleton)]
        public static object Trap(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ callStorage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            RubyContext/*!*/ context,
            BlockParam block,
            object self,
            object signalId) {

            if (block == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }
            return Trap(callStorage, stringCast, context, self, signalId, block.Proc);
        }

        private const int SignalInterrupt = PosixSignals.SignalInterrupt;
        private const int SignalExit = 0;

        #endregion
    }
}
